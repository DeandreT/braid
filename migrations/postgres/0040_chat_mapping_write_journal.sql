-- A chat save has a durable intent before ingestion is called. Its deterministic call key binds the
-- intent to ingestion's immutable own-receipt. There is deliberately no fallible post-commit journal write.
DO $owner$
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>current_user THEN
        RAISE EXCEPTION 'Chat write journal requires its owner login.' USING ERRCODE='42501';
    END IF;
END;
$owner$;

CREATE TABLE weft.chat_mapping_write_intents (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, request_key uuid NOT NULL,
    conversation_id uuid NOT NULL, turn_id uuid NOT NULL, round_id uuid NOT NULL, call_id text NOT NULL,
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, base_revision_id uuid NOT NULL, base_sequence bigint NOT NULL,
    operation_count integer NOT NULL, edit_digest text NOT NULL, native_digest text NOT NULL, native_byte_count bigint NOT NULL,
    profile_id text NOT NULL, profile_version integer NOT NULL, provider_id text NOT NULL, model_id text NOT NULL,
    metadata_policy_id text NOT NULL, metadata_policy_version integer NOT NULL, recorded_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,request_key),
    CONSTRAINT chat_mapping_write_intents_call_key UNIQUE(organization_id,creator_id,turn_id,call_id)
);
-- Private replay bytes and change summaries have conversation retention, unlike the no-content intent audit.
CREATE TABLE weft.chat_mapping_write_content (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, request_key uuid NOT NULL,
    conversation_id uuid NOT NULL, proposal text COLLATE "C" NOT NULL, native bytea NOT NULL,
    PRIMARY KEY(organization_id,creator_id,request_key),
    CONSTRAINT chat_mapping_write_content_conversation_fkey FOREIGN KEY(organization_id,creator_id,conversation_id)
        REFERENCES weft.chat_conversations(organization_id,creator_id,conversation_id) ON DELETE CASCADE ON UPDATE RESTRICT
);
-- Success evidence is captured atomically with ingestion, independently of request retention or erasure.
-- A request-key retirement tombstone is identity only and is never interpreted as a successful save.
CREATE TABLE weft.chat_mapping_write_commits (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, request_key uuid NOT NULL,
    operation_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL, revision_sequence bigint NOT NULL, committed_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,request_key)
);
ALTER TABLE weft.chat_mapping_write_intents ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.chat_mapping_write_content ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.chat_mapping_write_commits ENABLE ROW LEVEL SECURITY;
CREATE POLICY creator_scope ON weft.chat_mapping_write_intents USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
CREATE POLICY creator_scope ON weft.chat_mapping_write_content USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
CREATE POLICY creator_scope ON weft.chat_mapping_write_commits USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
REVOKE ALL ON weft.chat_mapping_write_intents,weft.chat_mapping_write_content,weft.chat_mapping_write_commits FROM PUBLIC;
CREATE TRIGGER chat_mapping_write_intents_guard BEFORE UPDATE OR DELETE ON weft.chat_mapping_write_intents FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_mapping_write_intents_truncate BEFORE TRUNCATE ON weft.chat_mapping_write_intents FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_mapping_write_commits_guard BEFORE UPDATE OR DELETE ON weft.chat_mapping_write_commits FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_mapping_write_commits_truncate BEFORE TRUNCATE ON weft.chat_mapping_write_commits FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();

CREATE FUNCTION weft.audit_chat_mapping_write_commit() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $audit_chat_mapping_write_commit$
DECLARE intent weft.chat_mapping_write_intents%ROWTYPE; saved_sequence bigint;
BEGIN
    IF NEW.phase<>'committed' OR OLD.phase='committed' OR NEW.operation_kind<>'append' THEN RETURN NULL; END IF;
    SELECT * INTO intent FROM weft.chat_mapping_write_intents i WHERE i.organization_id=NEW.organization_id
        AND i.creator_id=NEW.actor_principal_id AND i.project_id=NEW.project_id AND i.mapping_id=NEW.target_mapping_id
        AND i.request_key=NEW.idempotency_key AND i.base_revision_id=NEW.expected_head_revision_id AND NEW.head_condition='equal'
        AND i.native_digest=NEW.native_digest AND i.native_byte_count=NEW.native_byte_count AND NEW.backend_id='ferrule'
        AND NEW.display_filename='entry.native.json';
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT r.sequence INTO STRICT saved_sequence FROM weft.mapping_revisions r WHERE r.organization_id=NEW.organization_id
        AND r.project_id=NEW.project_id AND r.mapping_id=NEW.target_mapping_id AND r.id=NEW.committed_revision_id;
    IF saved_sequence<=intent.base_sequence THEN RAISE EXCEPTION 'Invalid chat save commit.' USING ERRCODE='23514'; END IF;
    INSERT INTO weft.chat_mapping_write_commits VALUES(NEW.organization_id,NEW.actor_principal_id,NEW.idempotency_key,
        NEW.operation_id,NEW.project_id,NEW.target_mapping_id,NEW.committed_revision_id,saved_sequence,NEW.completed_at);
    RETURN NULL;
END;
$audit_chat_mapping_write_commit$;
REVOKE ALL ON FUNCTION weft.audit_chat_mapping_write_commit() FROM PUBLIC;
CREATE TRIGGER chat_mapping_write_commit AFTER UPDATE ON weft.revision_ingestions FOR EACH ROW EXECUTE FUNCTION weft.audit_chat_mapping_write_commit();

CREATE FUNCTION weft.guard_chat_mapping_write_content() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $guard_chat_mapping_write_content$
BEGIN
    IF TG_OP='DELETE' AND NOT EXISTS(SELECT 1 FROM weft.chat_conversations c WHERE c.organization_id=OLD.organization_id
        AND c.creator_id=OLD.creator_id AND c.conversation_id=OLD.conversation_id) THEN RETURN OLD; END IF;
    RAISE EXCEPTION 'Chat write content is immutable.' USING ERRCODE='23514';
END;
$guard_chat_mapping_write_content$;
CREATE TRIGGER chat_mapping_write_content_guard BEFORE UPDATE OR DELETE ON weft.chat_mapping_write_content FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_content();
CREATE TRIGGER chat_mapping_write_content_truncate BEFORE TRUNCATE ON weft.chat_mapping_write_content FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_content();

CREATE FUNCTION weft.chat_mapping_write_prepare(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid,p_round uuid,p_call text,p_key uuid,p_proposal text,p_native bytea) RETURNS boolean
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_mapping_write_prepare$
DECLARE c weft.chat_conversations%ROWTYPE; t weft.chat_turns%ROWTYPE; old weft.chat_mapping_write_intents%ROWTYPE;
    j jsonb; native_hash text; key_bytes bytea; computed_key uuid;
BEGIN
    IF session_user<>'weft_chat_writer' OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN RETURN false; END IF;
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation FOR UPDATE;
    IF NOT FOUND OR c.conversation_kind<>2 OR c.retain_until<=statement_timestamp()
        OR weft.chat_authorized(p_org,NULL,p_creator) IS NOT TRUE THEN RETURN false; END IF;
    IF weft.chat_metadata_valid(15,p_proposal,p_round) IS NOT TRUE OR p_native IS NULL OR octet_length(p_native) NOT BETWEEN 1 AND 4194304
        OR p_call IS NULL OR octet_length(p_call) NOT BETWEEN 1 AND 256 THEN RETURN false; END IF;
    j:=p_proposal::jsonb;
    IF j->>'outcome'<>'2' OR j->>'reason'<>'1' OR (j->>'organization')::uuid IS DISTINCT FROM p_org THEN RETURN false; END IF;
    key_bytes:=sha256(convert_to('weft.chat-mapping-write.v1'||chr(10)||p_turn::text||chr(10)||p_call,'UTF8'));
    key_bytes:=set_byte(set_byte(key_bytes,6,(get_byte(key_bytes,6)&15)|128),8,(get_byte(key_bytes,8)&63)|128);
    computed_key:=encode(substring(key_bytes from 1 for 16),'hex')::uuid;
    IF p_key IS DISTINCT FROM computed_key THEN RETURN false; END IF;
    native_hash:=encode(sha256(p_native),'hex');
    SELECT * INTO old FROM weft.chat_mapping_write_intents i WHERE i.organization_id=p_org AND i.creator_id=p_creator AND i.request_key=p_key;
    IF FOUND THEN
        RETURN old.conversation_id=p_conversation AND old.turn_id=p_turn AND old.round_id=p_round AND old.call_id=p_call
            AND old.project_id=(j->>'project')::uuid AND old.mapping_id=(j->>'mapping')::uuid AND old.base_revision_id=(j->>'base')::uuid
            AND old.edit_digest=j->>'digest' AND old.operation_count=(j->>'operations')::integer AND old.native_digest=native_hash
            AND old.native_byte_count=octet_length(p_native)
            AND weft.chat_authorized(p_org,old.project_id,p_creator);
    END IF;
    SELECT * INTO t FROM weft.chat_turns x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation AND x.turn_id=p_turn;
    IF NOT FOUND OR t.phase<>1 OR t.deadline<=statement_timestamp()
        OR NOT EXISTS(SELECT 1 FROM weft.chat_rounds r WHERE r.organization_id=p_org AND r.creator_id=p_creator AND r.conversation_id=p_conversation
            AND r.turn_id=p_turn AND r.round_id=p_round AND r.ordinal=t.round_count AND r.outcome=1)
        OR NOT EXISTS(SELECT 1 FROM weft.chat_events e WHERE e.organization_id=p_org AND e.creator_id=p_creator AND e.conversation_id=p_conversation
            AND e.turn_id=p_turn AND e.round_id=p_round AND e.kind=3 AND e.private_text::jsonb->>'call'=p_call
            AND e.private_text::jsonb->>'name'='save_mapping_edit')
        OR (SELECT count(*) FROM weft.chat_mapping_write_intents i WHERE i.organization_id=p_org AND i.creator_id=p_creator AND i.turn_id=p_turn)>=8 THEN RETURN false; END IF;
    IF NOT EXISTS(SELECT 1 FROM weft.principals p JOIN weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=p_org AND o.active
        JOIN weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=p_org AND m.project_id=(j->>'project')::uuid AND m.active
        JOIN weft.mappings mp ON mp.organization_id=m.organization_id AND mp.project_id=m.project_id AND mp.id=(j->>'mapping')::uuid
        JOIN weft.mapping_revisions r ON r.organization_id=mp.organization_id AND r.project_id=mp.project_id AND r.mapping_id=mp.id AND r.id=(j->>'base')::uuid
        WHERE p.id=p_creator AND p.active AND m.role IN ('publisher','administrator') AND mp.backend_id='ferrule'
            AND mp.head_revision_id=r.id AND r.sequence=(j->>'baseSequence')::bigint) THEN RETURN false; END IF;
    INSERT INTO weft.chat_mapping_write_intents VALUES(p_org,p_creator,p_key,p_conversation,p_turn,p_round,p_call,
        (j->>'project')::uuid,(j->>'mapping')::uuid,(j->>'base')::uuid,(j->>'baseSequence')::bigint,
        (j->>'operations')::integer,j->>'digest',native_hash,octet_length(p_native),t.profile_id,t.profile_version,t.provider_id,t.model_id,
        t.metadata_policy_id,t.metadata_policy_version,statement_timestamp());
    INSERT INTO weft.chat_mapping_write_content VALUES(p_org,p_creator,p_key,p_conversation,p_proposal,p_native);
    RETURN true;
END;
$chat_mapping_write_prepare$;

CREATE FUNCTION weft.chat_mapping_write_read(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_mapping_write_read$
BEGIN
    IF session_user NOT IN ('weft_chat_reader','weft_chat_writer') OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid
        OR weft.chat_authorized(p_org,NULL,p_creator) IS NOT TRUE THEN RETURN '[]'; END IF;
    RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('key',i.request_key,'round',i.round_id,'proposal',b.proposal,
        'status',CASE WHEN a.request_key IS NOT NULL THEN 'saved' WHEN x.phase IN ('committed','head_conflict','rejected','attempt_exhausted') THEN 'notSaved'
            WHEN x.operation_id IS NOT NULL OR (t.phase=1 AND t.deadline>statement_timestamp()) THEN 'pending' ELSE 'notSaved' END,
        'operation',coalesce(a.operation_id,x.operation_id),'revision',a.revision_id,'sequence',a.revision_sequence,
        'native',CASE WHEN x.operation_id IS NOT NULL AND x.phase NOT IN ('committed','head_conflict','rejected','attempt_exhausted') THEN encode(b.native,'base64') ELSE NULL END)
        ORDER BY i.recorded_at,i.request_key),'[]'::jsonb)::text
        FROM weft.chat_mapping_write_intents i JOIN weft.chat_mapping_write_content b USING(organization_id,creator_id,request_key,conversation_id)
        JOIN weft.chat_conversations c USING(organization_id,creator_id,conversation_id)
        JOIN weft.chat_turns t USING(organization_id,creator_id,conversation_id,turn_id)
        LEFT JOIN weft.chat_mapping_write_commits a ON a.organization_id=i.organization_id AND a.creator_id=i.creator_id
            AND a.request_key=i.request_key AND a.project_id=i.project_id AND a.mapping_id=i.mapping_id
        LEFT JOIN weft.revision_ingestions x ON x.organization_id=i.organization_id AND x.project_id=i.project_id AND x.actor_principal_id=i.creator_id
            AND x.operation_kind='append' AND x.target_mapping_id=i.mapping_id AND x.idempotency_key=i.request_key
            AND x.backend_id='ferrule' AND x.display_filename='entry.native.json'
            AND x.head_condition='equal' AND x.expected_head_revision_id=i.base_revision_id AND x.native_digest=i.native_digest AND x.native_byte_count=i.native_byte_count
        WHERE i.organization_id=p_org AND i.creator_id=p_creator AND i.conversation_id=p_conversation AND i.turn_id=p_turn
            AND c.retain_until>statement_timestamp() AND weft.chat_authorized(p_org,i.project_id,p_creator));
END;
$chat_mapping_write_read$;
REVOKE ALL ON FUNCTION weft.guard_chat_mapping_write_content(),weft.chat_mapping_write_prepare(uuid,uuid,uuid,uuid,uuid,text,uuid,text,bytea),weft.chat_mapping_write_read(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.chat_mapping_write_prepare(uuid,uuid,uuid,uuid,uuid,text,uuid,text,bytea) TO weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_mapping_write_read(uuid,uuid,uuid,uuid) TO weft_chat_reader,weft_chat_writer;

CREATE OR REPLACE FUNCTION weft.chat_schema_lock() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_schema_lock$
BEGIN
    IF current_user<>'weft_migrator' OR session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle') THEN
        RAISE EXCEPTION 'Restricted chat profile required.' USING ERRCODE='42501';
    END IF;
    LOCK TABLE ONLY weft.schema_migrations,ONLY weft.principals,ONLY weft.organization_memberships,ONLY weft.project_memberships,
        ONLY weft.chat_conversations,ONLY weft.chat_turns,ONLY weft.chat_rounds,ONLY weft.chat_events,ONLY weft.chat_identities,ONLY weft.chat_payload_audit,ONLY weft.chat_mapping_write_audit,
        ONLY weft.chat_mapping_write_intents,ONLY weft.chat_mapping_write_content,ONLY weft.chat_mapping_write_commits,ONLY weft.mappings,ONLY weft.mapping_revisions,ONLY weft.revision_ingestions,
        ONLY weft.revision_derivations,ONLY weft.revision_derivation_attempts IN ACCESS SHARE MODE;
END;
$chat_schema_lock$;
