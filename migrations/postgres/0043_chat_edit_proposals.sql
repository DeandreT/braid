-- #437. 0041 (CSV) and 0042 (XML) are owned by parallel branches; do not fill their slots.
-- Provision weft_chat_approver as an isolated LOGIN NOINHERIT role before this migration.
-- The writer is trusted to admit verified application generation, never client storage envelopes.
DO $owner$
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>current_user THEN
        RAISE EXCEPTION 'Proposal persistence requires its owner login.' USING ERRCODE='42501';
    END IF;
END;
$owner$;
GRANT USAGE ON SCHEMA weft TO weft_chat_approver;
GRANT SELECT(version,checksum) ON weft.schema_migrations TO weft_chat_approver;

-- Exact call identity includes its round, unlike the retired legacy key domain.
ALTER TABLE weft.chat_mapping_write_intents DROP CONSTRAINT chat_mapping_write_intents_call_key;
ALTER TABLE weft.chat_mapping_write_intents ADD CONSTRAINT chat_mapping_write_intents_call_key
    UNIQUE(organization_id,creator_id,conversation_id,turn_id,round_id,call_id);

CREATE TABLE weft.chat_edit_proposals (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, proposal_id uuid NOT NULL,
    request_key uuid NOT NULL, conversation_id uuid NOT NULL, turn_id uuid NOT NULL, round_id uuid NOT NULL,
    call_id text COLLATE "C" NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    base_revision_id uuid NOT NULL, base_sequence bigint NOT NULL, fingerprint text NOT NULL,
    identity_facts text COLLATE "C" NOT NULL, generation_id uuid NOT NULL,
    generation_provenance text COLLATE "C" NOT NULL, recorded_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,proposal_id),
    CONSTRAINT chat_edit_proposals_request_key UNIQUE(organization_id,creator_id,request_key),
    CONSTRAINT chat_edit_proposals_call_key UNIQUE(organization_id,creator_id,conversation_id,turn_id,round_id,call_id)
);
CREATE TABLE weft.chat_edit_content (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, proposal_id uuid NOT NULL,
    conversation_id uuid NOT NULL, encoding bytea NOT NULL, native bytea NOT NULL, journal_proposal text COLLATE "C" NOT NULL,
    PRIMARY KEY(organization_id,creator_id,proposal_id),
    CONSTRAINT chat_edit_content_conversation_fkey FOREIGN KEY(organization_id,creator_id,conversation_id)
        REFERENCES weft.chat_conversations(organization_id,creator_id,conversation_id) ON DELETE CASCADE ON UPDATE RESTRICT
);
CREATE TABLE weft.chat_edit_approvals (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, proposal_id uuid NOT NULL,
    request_key uuid NOT NULL, fingerprint text NOT NULL, approved_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,proposal_id)
);
ALTER TABLE weft.chat_edit_proposals ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.chat_edit_content ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.chat_edit_approvals ENABLE ROW LEVEL SECURITY;
CREATE POLICY creator_scope ON weft.chat_edit_proposals USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
CREATE POLICY creator_scope ON weft.chat_edit_content USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
CREATE POLICY creator_scope ON weft.chat_edit_approvals USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
REVOKE ALL ON weft.chat_edit_proposals,weft.chat_edit_content,weft.chat_edit_approvals FROM PUBLIC;
CREATE TRIGGER chat_edit_proposals_guard BEFORE UPDATE OR DELETE ON weft.chat_edit_proposals FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_edit_proposals_truncate BEFORE TRUNCATE ON weft.chat_edit_proposals FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_edit_approvals_guard BEFORE UPDATE OR DELETE ON weft.chat_edit_approvals FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_edit_approvals_truncate BEFORE TRUNCATE ON weft.chat_edit_approvals FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_edit_content_guard BEFORE UPDATE OR DELETE ON weft.chat_edit_content FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_content();
CREATE TRIGGER chat_edit_content_truncate BEFORE TRUNCATE ON weft.chat_edit_content FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_content();

CREATE FUNCTION weft.chat_edit_create(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid,p_facts text,
    p_generation uuid,p_provenance text,p_encoding bytea,p_native bytea,p_journal text) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_edit_create$
DECLARE f jsonb; j jsonb; old weft.chat_edit_proposals%ROWTYPE; c weft.chat_conversations%ROWTYPE; key_bytes bytea;
BEGIN
    IF session_user<>'weft_chat_writer' OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN RETURN 'Refused'; END IF;
    SELECT * INTO c FROM weft.chat_conversations WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation FOR UPDATE;
    IF NOT FOUND OR c.conversation_kind<>2 OR c.retain_until<=clock_timestamp()
        OR weft.chat_authorized(p_org,NULL,p_creator) IS NOT TRUE THEN RETURN 'Refused'; END IF;
    IF p_facts IS NULL OR octet_length(p_facts)>8192 OR p_provenance IS NULL OR octet_length(p_provenance)>8192
        OR p_generation IS NULL OR p_generation='00000000-0000-0000-0000-000000000000'
        OR p_encoding IS NULL OR octet_length(p_encoding) NOT BETWEEN 1 AND 8388608
        OR p_native IS NULL OR octet_length(p_native) NOT BETWEEN 1 AND 4194304
        OR weft.chat_metadata_valid(15,p_journal,(p_facts::jsonb->>'round')::uuid) IS NOT TRUE THEN RETURN 'Refused'; END IF;
    f:=p_facts::jsonb; j:=p_journal::jsonb;
    -- Integrity of the separately retained provenance assertion. Its authenticity is the
    -- verified application admission capability, not this JSON or any digest within it.
    IF p_provenance::jsonb->'Confirmed' IS DISTINCT FROM 'true'::jsonb
        OR p_provenance::jsonb->'NativeDigest'->>'Value' IS DISTINCT FROM f->>'nativeDigest'
        OR p_provenance::jsonb->'Scope'->>'OrganizationId' IS DISTINCT FROM f->>'organization'
        OR p_provenance::jsonb->'Scope'->>'ProjectId' IS DISTINCT FROM f->>'project'
        OR p_provenance::jsonb->'Scope'->>'MappingId' IS DISTINCT FROM f->>'mapping'
        OR p_provenance::jsonb->'Scope'->>'RevisionId' IS DISTINCT FROM f->>'base' THEN RETURN 'Refused'; END IF;
    IF (f->>'canonicalIdentity')::jsonb IS DISTINCT FROM jsonb_build_array(p_org,p_creator,p_conversation,p_turn,(f->>'round')::uuid,f->>'call')
        OR f->>'call' IS NULL OR octet_length(f->>'call') NOT BETWEEN 1 AND 256 THEN RETURN 'Refused'; END IF;
    key_bytes:=sha256(convert_to('weft.chat-edit-proposal.v1'||chr(10)||(f->>'canonicalIdentity'),'UTF8'));
    key_bytes:=set_byte(set_byte(key_bytes,6,(get_byte(key_bytes,6)&15)|128),8,(get_byte(key_bytes,8)&63)|128);
    IF (f->>'proposal')::uuid IS DISTINCT FROM encode(substring(key_bytes from 1 for 16),'hex')::uuid THEN RETURN 'Refused'; END IF;
    key_bytes:=sha256(convert_to('weft.chat-approved-edit.v1'||chr(10)||(f->>'canonicalIdentity'),'UTF8'));
    key_bytes:=set_byte(set_byte(key_bytes,6,(get_byte(key_bytes,6)&15)|128),8,(get_byte(key_bytes,8)&63)|128);
    IF (f->>'key')::uuid IS DISTINCT FROM encode(substring(key_bytes from 1 for 16),'hex')::uuid THEN RETURN 'Refused'; END IF;
    IF (f->>'organization')::uuid IS DISTINCT FROM p_org OR (f->>'creator')::uuid IS DISTINCT FROM p_creator
        OR (f->>'conversation')::uuid IS DISTINCT FROM p_conversation OR (f->>'turn')::uuid IS DISTINCT FROM p_turn
        OR f->>'encodingDigest' IS DISTINCT FROM encode(sha256(p_encoding),'hex')
        OR f->>'nativeDigest' IS DISTINCT FROM encode(sha256(p_native),'hex')
        OR (f->>'nativeBytes')::integer IS DISTINCT FROM octet_length(p_native)
        OR j->>'organization' IS DISTINCT FROM f->>'organization' OR j->>'project' IS DISTINCT FROM f->>'project'
        OR j->>'mapping' IS DISTINCT FROM f->>'mapping' OR j->>'base' IS DISTINCT FROM f->>'base'
        OR j->>'baseSequence' IS DISTINCT FROM f->>'baseSequence' OR j->>'digest' IS DISTINCT FROM f->>'operationsDigest'
        OR j->>'operations' IS DISTINCT FROM f->>'operationCount' OR j->>'outcome'<>'2' OR j->>'reason'<>'1'
        OR weft.chat_authorized(p_org,(f->>'project')::uuid,p_creator) IS NOT TRUE THEN RETURN 'Refused'; END IF;
    SELECT * INTO old FROM weft.chat_edit_proposals WHERE organization_id=p_org AND creator_id=p_creator
        AND conversation_id=p_conversation AND turn_id=p_turn AND round_id=(f->>'round')::uuid AND call_id=f->>'call';
    IF FOUND THEN
        IF old.identity_facts<>p_facts THEN RETURN 'Conflict'; END IF;
        RETURN 'Existing';
    END IF;
    IF NOT EXISTS(SELECT 1 FROM weft.chat_turns t JOIN weft.chat_rounds r USING(organization_id,creator_id,conversation_id,turn_id)
        JOIN weft.chat_events e USING(organization_id,creator_id,conversation_id,turn_id,round_id)
        WHERE t.organization_id=p_org AND t.creator_id=p_creator AND t.conversation_id=p_conversation AND t.turn_id=p_turn
        AND t.phase=1 AND t.deadline>clock_timestamp() AND r.round_id=(f->>'round')::uuid AND r.ordinal=t.round_count AND r.outcome=1
        AND e.kind=3 AND e.private_text::jsonb->>'call'=f->>'call' AND e.private_text::jsonb->>'name'='propose_mapping_edit')
        OR (SELECT count(*) FROM weft.chat_edit_proposals WHERE organization_id=p_org AND creator_id=p_creator AND turn_id=p_turn)>=8
        OR NOT EXISTS(SELECT 1 FROM weft.mapping_revisions r WHERE r.organization_id=p_org AND r.project_id=(f->>'project')::uuid
            AND r.mapping_id=(f->>'mapping')::uuid AND r.id=(f->>'base')::uuid AND r.sequence=(f->>'baseSequence')::bigint)
        THEN RETURN 'Refused'; END IF;
    INSERT INTO weft.chat_edit_proposals VALUES(p_org,p_creator,(f->>'proposal')::uuid,(f->>'key')::uuid,p_conversation,p_turn,
        (f->>'round')::uuid,f->>'call',(f->>'project')::uuid,(f->>'mapping')::uuid,(f->>'base')::uuid,(f->>'baseSequence')::bigint,
        f->>'fingerprint',p_facts,p_generation,p_provenance,clock_timestamp());
    INSERT INTO weft.chat_edit_content VALUES(p_org,p_creator,(f->>'proposal')::uuid,p_conversation,p_encoding,p_native,p_journal);
    RETURN 'Created';
END;
$chat_edit_create$;

CREATE FUNCTION weft.chat_edit_read(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid,p_proposal uuid) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_edit_read$
BEGIN
    IF session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_approver')
        OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN RETURN NULL; END IF;
    RETURN (SELECT jsonb_build_object('facts',p.identity_facts,'encoding',encode(b.encoding,'base64'),
        'status',CASE WHEN a.request_key IS NOT NULL THEN 'Saved' WHEN x.phase='head_conflict' THEN 'OutOfDate'
            WHEN x.phase IN ('rejected','attempt_exhausted','committed') THEN 'NotSaved'
            WHEN x.operation_id IS NOT NULL THEN 'Pending' WHEN v.proposal_id IS NOT NULL THEN 'Approved' ELSE 'AwaitingApproval' END,
        'operation',coalesce(a.operation_id,x.operation_id),'revision',a.revision_id,'sequence',a.revision_sequence)::text
        FROM weft.chat_edit_proposals p JOIN weft.chat_edit_content b USING(organization_id,creator_id,proposal_id,conversation_id)
        JOIN weft.chat_conversations c USING(organization_id,creator_id,conversation_id)
        LEFT JOIN weft.chat_edit_approvals v USING(organization_id,creator_id,proposal_id,request_key,fingerprint)
        LEFT JOIN weft.chat_mapping_write_commits a ON a.organization_id=p.organization_id AND a.creator_id=p.creator_id AND a.request_key=p.request_key
        LEFT JOIN weft.revision_ingestions x ON x.organization_id=p.organization_id AND x.actor_principal_id=p.creator_id
            AND x.idempotency_key=p.request_key AND v.proposal_id IS NOT NULL AND x.project_id=p.project_id
            AND x.target_mapping_id=p.mapping_id AND x.operation_kind='append' AND x.head_condition='equal'
            AND x.expected_head_revision_id=p.base_revision_id AND x.backend_id='ferrule' AND x.display_filename='entry.native.json'
            AND x.native_digest=p.identity_facts::jsonb->>'nativeDigest' AND x.native_byte_count=(p.identity_facts::jsonb->>'nativeBytes')::bigint
        WHERE p.organization_id=p_org AND p.creator_id=p_creator AND p.conversation_id=p_conversation AND p.turn_id=p_turn
            AND p.proposal_id=p_proposal AND c.retain_until>clock_timestamp() AND weft.chat_authorized(p_org,p.project_id,p_creator));
END;
$chat_edit_read$;

CREATE FUNCTION weft.chat_edit_list(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_edit_list$
BEGIN
    IF session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_approver')
        OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN RETURN '[]'; END IF;
    RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',q.proposal_id,'state',q.state) ORDER BY q.recorded_at,q.proposal_id),'[]'::jsonb)::text
        FROM (SELECT p.proposal_id,p.recorded_at,weft.chat_edit_read(p_org,p_creator,p_conversation,p_turn,p.proposal_id) state
            FROM weft.chat_edit_proposals p WHERE p.organization_id=p_org AND p.creator_id=p_creator
                AND p.conversation_id=p_conversation AND p.turn_id=p_turn ORDER BY p.recorded_at,p.proposal_id LIMIT 8) q WHERE q.state IS NOT NULL);
END;
$chat_edit_list$;
REVOKE ALL ON FUNCTION weft.chat_edit_list(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.chat_edit_list(uuid,uuid,uuid,uuid) TO weft_chat_reader,weft_chat_writer,weft_chat_approver;

CREATE FUNCTION weft.chat_edit_approve(p_org uuid,p_creator uuid,p_conversation uuid,p_turn uuid,p_proposal uuid,p_fingerprint text) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_edit_approve$
DECLARE p weft.chat_edit_proposals%ROWTYPE; b weft.chat_edit_content%ROWTYPE; t weft.chat_turns%ROWTYPE; observed text;
BEGIN
    IF session_user<>'weft_chat_approver' OR p_org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR p_creator IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN RETURN NULL; END IF;
    -- Serialize erasure and all proposal approvals before fresh authority and clock reads.
    PERFORM 1 FROM weft.chat_conversations WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation FOR UPDATE;
    SELECT * INTO p FROM weft.chat_edit_proposals WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation
        AND turn_id=p_turn AND proposal_id=p_proposal FOR UPDATE;
    IF NOT FOUND OR p.fingerprint IS DISTINCT FROM p_fingerprint THEN RETURN NULL; END IF;
    observed:=weft.chat_edit_read(p_org,p_creator,p_conversation,p_turn,p_proposal);
    IF observed IS NULL THEN RETURN NULL; END IF;
    -- A committed own receipt wins over a later head or revoked publish (read authority still required).
    IF observed::jsonb->>'status' IN ('Saved','NotSaved','OutOfDate') THEN RETURN observed; END IF;
    IF NOT EXISTS(SELECT 1 FROM weft.principals a JOIN weft.organization_memberships o ON o.principal_id=a.id AND o.organization_id=p_org AND o.active
        JOIN weft.project_memberships m ON m.principal_id=a.id AND m.organization_id=p_org AND m.project_id=p.project_id AND m.active
        WHERE a.id=p_creator AND a.active AND m.role IN ('publisher','administrator')) THEN
        -- Finalization does not take our conversation/proposal locks. Its receipt may
        -- have committed since the first read. Refresh through the authorized reader:
        -- withdrawn read authority or expired retention must return no private state.
        observed:=weft.chat_edit_read(p_org,p_creator,p_conversation,p_turn,p_proposal);
        IF observed IS NULL THEN RETURN NULL; END IF;
        IF observed::jsonb->>'status'='Saved' THEN RETURN observed; END IF;
        RETURN jsonb_set(observed::jsonb,'{status}','"NotPermitted"')::text;
    END IF;
    IF NOT EXISTS(SELECT 1 FROM weft.mappings m JOIN weft.mapping_revisions r ON r.organization_id=m.organization_id AND r.project_id=m.project_id
        AND r.mapping_id=m.id AND r.id=m.head_revision_id WHERE m.organization_id=p_org AND m.project_id=p.project_id AND m.id=p.mapping_id
        AND m.backend_id='ferrule' AND r.id=p.base_revision_id AND r.sequence=p.base_sequence) THEN
        -- Use a new READ COMMITTED statement snapshot after observing the newer head.
        -- Do not acquire ingestion/mapping locks in the reverse finalization order.
        observed:=weft.chat_edit_read(p_org,p_creator,p_conversation,p_turn,p_proposal);
        IF observed IS NULL THEN RETURN NULL; END IF;
        IF observed::jsonb->>'status'='Saved' THEN RETURN observed; END IF;
        RETURN jsonb_set(observed::jsonb,'{status}','"OutOfDate"')::text;
    END IF;
    IF observed::jsonb->>'status' IN ('Approved','Pending') THEN RETURN observed; END IF;
    SELECT * INTO STRICT b FROM weft.chat_edit_content WHERE organization_id=p_org AND creator_id=p_creator AND proposal_id=p_proposal;
    SELECT * INTO STRICT t FROM weft.chat_turns WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation AND turn_id=p_turn;
    INSERT INTO weft.chat_edit_approvals VALUES(p_org,p_creator,p_proposal,p.request_key,p.fingerprint,clock_timestamp());
    INSERT INTO weft.chat_mapping_write_intents VALUES(p_org,p_creator,p.request_key,p_conversation,p_turn,p.round_id,p.call_id,
        p.project_id,p.mapping_id,p.base_revision_id,p.base_sequence,(p.identity_facts::jsonb->>'operationCount')::integer,
        p.identity_facts::jsonb->>'operationsDigest',p.identity_facts::jsonb->>'nativeDigest',octet_length(b.native),
        t.profile_id,t.profile_version,t.provider_id,t.model_id,t.metadata_policy_id,t.metadata_policy_version,clock_timestamp());
    INSERT INTO weft.chat_mapping_write_content VALUES(p_org,p_creator,p.request_key,p_conversation,b.journal_proposal,b.native);
    RETURN weft.chat_edit_read(p_org,p_creator,p_conversation,p_turn,p_proposal);
END;
$chat_edit_approve$;
REVOKE ALL ON FUNCTION weft.chat_edit_create(uuid,uuid,uuid,uuid,text,uuid,text,bytea,bytea,text),weft.chat_edit_read(uuid,uuid,uuid,uuid,uuid),weft.chat_edit_approve(uuid,uuid,uuid,uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.chat_edit_create(uuid,uuid,uuid,uuid,text,uuid,text,bytea,bytea,text) TO weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_edit_read(uuid,uuid,uuid,uuid,uuid) TO weft_chat_reader,weft_chat_writer,weft_chat_approver;
GRANT EXECUTE ON FUNCTION weft.chat_edit_approve(uuid,uuid,uuid,uuid,uuid,text) TO weft_chat_approver;

-- Fail closed at the legacy entrance. Existing receipts remain readable; no retrospective approval.
REVOKE ALL ON FUNCTION weft.chat_mapping_write_prepare(uuid,uuid,uuid,uuid,uuid,text,uuid,text,bytea) FROM weft_chat_writer;
CREATE OR REPLACE FUNCTION weft.audit_chat_mapping_write_commit() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $audit_chat_mapping_write_commit$
DECLARE intent weft.chat_mapping_write_intents%ROWTYPE; p weft.chat_edit_proposals%ROWTYPE; saved_sequence bigint;
BEGIN
    IF NEW.phase<>'committed' OR OLD.phase='committed' THEN RETURN NULL; END IF;
    -- Identify actor/key FIRST: a mismatched binding must never fall through as ordinary ingestion.
    SELECT * INTO intent FROM weft.chat_mapping_write_intents WHERE organization_id=NEW.organization_id
        AND creator_id=NEW.actor_principal_id AND request_key=NEW.idempotency_key;
    SELECT * INTO p FROM weft.chat_edit_proposals WHERE organization_id=NEW.organization_id
        AND creator_id=NEW.actor_principal_id AND request_key=NEW.idempotency_key;
    IF intent.request_key IS NULL AND p.request_key IS NULL THEN RETURN NULL; END IF;
    IF intent.request_key IS NULL OR p.request_key IS NULL OR NOT EXISTS(SELECT 1 FROM weft.chat_edit_approvals a
        WHERE a.organization_id=p.organization_id AND a.creator_id=p.creator_id AND a.proposal_id=p.proposal_id
            AND a.request_key=p.request_key AND a.fingerprint=p.fingerprint)
        OR NEW.operation_kind IS DISTINCT FROM 'append' OR intent.project_id IS DISTINCT FROM NEW.project_id
        OR intent.mapping_id IS DISTINCT FROM NEW.target_mapping_id OR intent.base_revision_id IS DISTINCT FROM NEW.expected_head_revision_id
        OR NEW.head_condition IS DISTINCT FROM 'equal' OR intent.native_digest IS DISTINCT FROM NEW.native_digest
        OR intent.native_byte_count IS DISTINCT FROM NEW.native_byte_count OR NEW.backend_id IS DISTINCT FROM 'ferrule'
        OR NEW.display_filename IS DISTINCT FROM 'entry.native.json' THEN
        RAISE EXCEPTION 'Exact approved chat save required.' USING ERRCODE='23514';
    END IF;
    SELECT r.sequence INTO STRICT saved_sequence FROM weft.mapping_revisions r WHERE r.organization_id=NEW.organization_id
        AND r.project_id=NEW.project_id AND r.mapping_id=NEW.target_mapping_id AND r.id=NEW.committed_revision_id;
    IF saved_sequence<=intent.base_sequence THEN RAISE EXCEPTION 'Invalid chat save commit.' USING ERRCODE='23514'; END IF;
    INSERT INTO weft.chat_mapping_write_commits VALUES(NEW.organization_id,NEW.actor_principal_id,NEW.idempotency_key,
        NEW.operation_id,NEW.project_id,NEW.target_mapping_id,NEW.committed_revision_id,saved_sequence,NEW.completed_at);
    RETURN NULL;
END;
$audit_chat_mapping_write_commit$;

CREATE OR REPLACE FUNCTION weft.chat_schema_lock() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_schema_lock$
BEGIN
    IF current_user<>'weft_migrator' OR session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle','weft_chat_approver') THEN
        RAISE EXCEPTION 'Restricted chat profile required.' USING ERRCODE='42501';
    END IF;
    LOCK TABLE ONLY weft.schema_migrations,ONLY weft.principals,ONLY weft.organization_memberships,ONLY weft.project_memberships,
        ONLY weft.chat_conversations,ONLY weft.chat_turns,ONLY weft.chat_rounds,ONLY weft.chat_events,ONLY weft.chat_identities,ONLY weft.chat_payload_audit,ONLY weft.chat_mapping_write_audit,
        ONLY weft.chat_mapping_write_intents,ONLY weft.chat_mapping_write_content,ONLY weft.chat_mapping_write_commits,ONLY weft.mappings,ONLY weft.mapping_revisions,ONLY weft.revision_ingestions,
        ONLY weft.revision_derivations,ONLY weft.revision_derivation_attempts,ONLY weft.chat_edit_proposals,ONLY weft.chat_edit_content,ONLY weft.chat_edit_approvals IN ACCESS SHARE MODE;
END;
$chat_schema_lock$;
GRANT EXECUTE ON FUNCTION weft.chat_schema_lock() TO weft_chat_approver;
