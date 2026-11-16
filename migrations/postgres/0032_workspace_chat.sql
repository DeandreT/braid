-- Organization-scoped workspace conversations beside the existing revision-pinned kind. Revision
-- conversations keep their exact columns, pins, rules and stored rows; a workspace conversation has no
-- revision pin and an optional focus that guides context and never grants access. Workspace citations
-- and shared-data accounting are new round-bound ledger events, and every shared-data event writes a
-- no-content audit row that survives conversation erasure.
DO $chat_workspace_owner$
BEGIN
    IF current_user <> 'braid_migrator' OR session_user <> current_user THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Workspace chat requires the separately provisioned owner login.';
    END IF;
END;
$chat_workspace_owner$;

LOCK TABLE braid.chat_conversations,braid.chat_turns,braid.chat_rounds,braid.chat_events,braid.chat_identities IN ACCESS EXCLUSIVE MODE;

ALTER TABLE braid.chat_conversations ALTER COLUMN project_id DROP NOT NULL, ALTER COLUMN mapping_id DROP NOT NULL, ALTER COLUMN revision_id DROP NOT NULL, ALTER COLUMN projection_entry_id DROP NOT NULL, ALTER COLUMN projection_id DROP NOT NULL, ALTER COLUMN projection_key DROP NOT NULL, ALTER COLUMN projection_digest DROP NOT NULL, ALTER COLUMN validation_entry_id DROP NOT NULL, ALTER COLUMN validation_id DROP NOT NULL, ALTER COLUMN validation_key DROP NOT NULL, ALTER COLUMN validation_digest DROP NOT NULL,
    ADD COLUMN conversation_kind integer NOT NULL DEFAULT 1, ADD COLUMN focus_project_id uuid, ADD COLUMN focus_mapping_id uuid,
    ADD COLUMN focus_revision_id uuid;
ALTER TABLE braid.chat_conversations ALTER COLUMN conversation_kind DROP DEFAULT,
    ADD CONSTRAINT chat_conversations_kind_check CHECK((conversation_kind=1 AND num_nulls(project_id,mapping_id,revision_id,projection_entry_id,projection_id,projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest)=0
        AND num_nonnulls(focus_project_id,focus_mapping_id,focus_revision_id)=0)
        OR (conversation_kind=2 AND num_nonnulls(project_id,mapping_id,revision_id,projection_entry_id,projection_id,projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest)=0
            AND (focus_project_id IS NULL)=(focus_mapping_id IS NULL) AND (focus_revision_id IS NULL OR focus_mapping_id IS NOT NULL))),
    ADD CONSTRAINT chat_conversations_focus_mapping_fkey FOREIGN KEY(organization_id,focus_project_id,focus_mapping_id)
        REFERENCES braid.mappings(organization_id,project_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    ADD CONSTRAINT chat_conversations_focus_revision_fkey FOREIGN KEY(organization_id,focus_project_id,focus_mapping_id,focus_revision_id)
        REFERENCES braid.mapping_revisions(organization_id,project_id,mapping_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT;
CREATE INDEX chat_conversations_recent ON braid.chat_conversations(organization_id,creator_id,created_at,conversation_id);

ALTER TABLE braid.chat_events DROP CONSTRAINT chat_events_kind_check, DROP CONSTRAINT chat_events_check,
    ADD CONSTRAINT chat_events_kind_check CHECK(kind BETWEEN 1 AND 14),
    ADD CONSTRAINT chat_events_check CHECK((kind IN (2,3,4) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NULL AND octet_length(private_text) BETWEEN 1 AND 8192 AND btrim(private_text)<>'')
        OR (kind IN (9,10,11,12,13,14) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NOT NULL AND private_text='')
        OR (kind=1 AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND metadata_json IS NULL)
        OR (kind IN (5,6,7,8) AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND (metadata_json IS NULL OR octet_length(metadata_json)<=2048)));

-- One row per shared-data event: who shared what kind of payload from which project and run with which
-- profile, and how many bytes. No content, digest of content or conversation text is retained.
CREATE TABLE braid.chat_payload_audit (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, event_id uuid NOT NULL,
    conversation_id uuid NOT NULL, turn_id uuid NOT NULL, round_id uuid NOT NULL, project_id uuid NOT NULL,
    source_kind integer NOT NULL CHECK(source_kind BETWEEN 1 AND 4), subject_id uuid NOT NULL, subject_version bigint,
    byte_count integer NOT NULL CHECK(byte_count BETWEEN 1 AND 1048576),
    profile_id text NOT NULL, profile_version integer NOT NULL, provider_id text NOT NULL, model_id text NOT NULL,
    metadata_policy_id text NOT NULL, metadata_policy_version integer NOT NULL, recorded_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,event_id),
    CONSTRAINT chat_payload_audit_subject_check CHECK((source_kind=3)=(subject_version IS NOT NULL) AND (subject_version IS NULL OR subject_version>0))
);
ALTER TABLE braid.chat_payload_audit ENABLE ROW LEVEL SECURITY;
CREATE POLICY creator_scope ON braid.chat_payload_audit USING (organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid);
REVOKE ALL ON braid.chat_payload_audit FROM PUBLIC;

CREATE FUNCTION braid.guard_chat_payload_audit() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $guard_chat_payload_audit$
BEGIN
    RAISE EXCEPTION 'Chat payload audit is append-only.' USING ERRCODE='23514';
END;
$guard_chat_payload_audit$;
CREATE TRIGGER chat_payload_audit_guard BEFORE UPDATE OR DELETE ON braid.chat_payload_audit FOR EACH ROW EXECUTE FUNCTION braid.guard_chat_payload_audit();
CREATE TRIGGER chat_payload_audit_truncate BEFORE TRUNCATE ON braid.chat_payload_audit FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_chat_payload_audit();

-- The audit row is written in the same statement as its event, by the owner, from the admitted event and turn.
CREATE FUNCTION braid.audit_chat_payload() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $audit_chat_payload$
DECLARE j jsonb; t braid.chat_turns%ROWTYPE;
BEGIN
    IF NEW.kind<>14 THEN RETURN NULL; END IF;
    SELECT * INTO t FROM braid.chat_turns x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
        AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Missing chat turn.' USING ERRCODE='23514'; END IF;
    j:=NEW.metadata_json::jsonb;
    INSERT INTO braid.chat_payload_audit(organization_id,creator_id,event_id,conversation_id,turn_id,round_id,project_id,source_kind,
        subject_id,subject_version,byte_count,profile_id,profile_version,provider_id,model_id,metadata_policy_id,metadata_policy_version,recorded_at)
        VALUES(NEW.organization_id,NEW.creator_id,NEW.event_id,NEW.conversation_id,NEW.turn_id,NEW.round_id,(j->>'project')::uuid,
            (j->>'source')::integer,(j->>'subject')::uuid,(j->>'version')::bigint,(j->>'bytes')::integer,t.profile_id,t.profile_version,
            t.provider_id,t.model_id,t.metadata_policy_id,t.metadata_policy_version,statement_timestamp());
    RETURN NULL;
END;
$audit_chat_payload$;
REVOKE ALL ON FUNCTION braid.guard_chat_payload_audit(),braid.audit_chat_payload() FROM PUBLIC;
CREATE TRIGGER chat_payload_audit AFTER INSERT ON braid.chat_events FOR EACH ROW EXECUTE FUNCTION braid.audit_chat_payload();

CREATE OR REPLACE FUNCTION braid.chat_authorized(org uuid, project uuid, creator uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $chat_authorized$
    SELECT org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND creator=NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid
        AND EXISTS(SELECT 1 FROM ONLY braid.principals p
            JOIN ONLY braid.organization_memberships o ON o.principal_id=p.id AND o.organization_id=org AND o.active
            WHERE p.id=creator AND p.active AND (project IS NULL OR EXISTS(SELECT 1 FROM ONLY braid.project_memberships m
                WHERE m.principal_id=p.id AND m.organization_id=org AND m.project_id=project AND m.active
                AND m.role IN ('viewer','runner','publisher','administrator'))))
$chat_authorized$;

CREATE OR REPLACE FUNCTION braid.guard_chat_conversation() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $guard_chat_conversation$
DECLARE c braid.chat_conversations%ROWTYPE; n integer;
BEGIN
    IF TG_OP='UPDATE' OR TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'Immutable chat context.' USING ERRCODE='23514'; END IF;
    IF TG_OP='DELETE' THEN c:=OLD; ELSE c:=NEW; END IF;
    IF braid.chat_authorized(c.organization_id,c.project_id,c.creator_id) IS NOT TRUE
        AND NOT (TG_OP='DELETE' AND braid.chat_is_lifecycle()) THEN
        RAISE EXCEPTION 'Chat authority unavailable.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='DELETE' THEN
        IF statement_timestamp()<c.retain_until OR EXISTS(SELECT 1 FROM braid.chat_turns t
            WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id AND t.conversation_id=c.conversation_id AND t.phase=1) THEN
            RAISE EXCEPTION 'Chat retention or active turn prevents deletion.' USING ERRCODE='23514';
        END IF;
        IF NOT EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=c.organization_id AND i.creator_id=c.creator_id AND i.identity_kind=1 AND i.identity_id=c.conversation_id AND i.erased_at IS NOT NULL)
            OR EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=c.organization_id AND i.creator_id=c.creator_id AND i.conversation_id=c.conversation_id AND i.erased_at IS NULL) THEN
            RAISE EXCEPTION 'Chat erasure requires retained identity protection.' USING ERRCODE='23514';
        END IF;
        RETURN OLD;
    END IF;
    IF NEW.created_at<>statement_timestamp() OR NEW.retain_until<=statement_timestamp() THEN
        RAISE EXCEPTION 'Invalid chat retention.' USING ERRCODE='23514';
    END IF;
    IF NOT EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.identity_kind=1 AND i.identity_id=NEW.conversation_id AND i.conversation_id=NEW.conversation_id AND i.erased_at IS NULL) THEN
        RAISE EXCEPTION 'Chat identity unavailable.' USING ERRCODE='23514';
    END IF;
    IF NEW.conversation_kind=2 THEN
        -- A focus guides context only; it must be readable by the creator when the conversation is created.
        IF NEW.focus_project_id IS NOT NULL AND braid.chat_authorized(NEW.organization_id,NEW.focus_project_id,NEW.creator_id) IS NOT TRUE THEN
            RAISE EXCEPTION 'Chat focus unavailable.' USING ERRCODE='42501';
        END IF;
        RETURN NEW;
    END IF;
    SELECT count(*) INTO n FROM braid.revision_derivations d JOIN braid.revision_derivation_attempts a
        ON a.organization_id=d.organization_id AND a.project_id=d.project_id AND a.mapping_id=d.mapping_id
        AND a.revision_id=d.revision_id AND a.cache_entry_id=d.cache_entry_id
        WHERE d.organization_id=c.organization_id AND d.project_id=c.project_id AND d.mapping_id=c.mapping_id AND d.revision_id=c.revision_id
        AND a.result_usable AND a.completion_kind='usable' AND (
            (d.operation_kind='projection' AND d.cache_entry_id=c.projection_entry_id AND a.derivation_id=c.projection_id AND d.key_fingerprint=c.projection_key AND a.derived_digest=c.projection_digest)
            OR (d.operation_kind='validation' AND d.cache_entry_id=c.validation_entry_id AND a.derivation_id=c.validation_id AND d.key_fingerprint=c.validation_key AND a.derived_digest=c.validation_digest));
    IF n<>2 THEN RAISE EXCEPTION 'Exact retained chat reports unavailable.' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$guard_chat_conversation$;

CREATE OR REPLACE FUNCTION braid.chat_metadata_valid(kind integer, value text, expected_round uuid) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $chat_metadata_valid$
DECLARE j jsonb; keys text[]; k text; n numeric; usage jsonb; failure jsonb; c integer; t integer;
BEGIN
    IF kind IS NULL OR value IS NULL OR octet_length(value) NOT BETWEEN 1 AND 8192 THEN RETURN false; END IF;
    j:=value::jsonb;
    -- Missing keys yield SQL NULL; JSON null is a separate JSON type. Never let
    -- either reach casts/comparisons for mandatory fields and fall through IF.
    IF jsonb_typeof(j) IS DISTINCT FROM 'object' OR jsonb_typeof(j->'v') IS DISTINCT FROM 'number'
        OR j->>'v' IS DISTINCT FROM '1' THEN RETURN false; END IF;
    SELECT array_agg(x ORDER BY x) INTO keys FROM jsonb_object_keys(j) x;
    IF kind=9 THEN
        IF keys IS DISTINCT FROM ARRAY['input','known','origin','output','round','total','v']::text[]
            OR jsonb_typeof(j->'round') IS DISTINCT FROM 'string' OR jsonb_typeof(j->'origin') IS DISTINCT FROM 'number'
            OR jsonb_typeof(j->'known') IS DISTINCT FROM 'boolean' THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['input','output','total']::text[] LOOP
            IF jsonb_typeof(j->k) IS NULL OR jsonb_typeof(j->k) NOT IN ('number','null') THEN RETURN false; END IF;
        END LOOP;
        IF j->>'origin' !~ '^[1-3]$' OR (j->>'round')::uuid='00000000-0000-0000-0000-000000000000'::uuid
            OR (expected_round IS NOT NULL AND (j->>'round')::uuid IS DISTINCT FROM expected_round) THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['input','output','total']::text[] LOOP
            IF jsonb_typeof(j->k)='number' THEN
                IF j->>k !~ '^(0|[1-9][0-9]*)$' THEN RETURN false; END IF;
                n:=(j->>k)::numeric; IF n<0 OR n>1000000000000 THEN RETURN false; END IF;
            END IF;
        END LOOP;
        IF (j->>'origin')::integer=3 AND (j->'input'<>'null'::jsonb OR j->'output'<>'null'::jsonb OR j->'total'<>'null'::jsonb) THEN RETURN false; END IF;
        RETURN ((j->>'known')::boolean=(j->'input'<>'null'::jsonb OR j->'output'<>'null'::jsonb OR j->'total'<>'null'::jsonb)) IS TRUE;
    ELSIF kind=10 THEN
        IF keys IS DISTINCT FROM ARRAY['code','dispatch','round','v']::text[]
            OR jsonb_typeof(j->'round') IS DISTINCT FROM 'string' OR jsonb_typeof(j->'code') IS DISTINCT FROM 'number'
            OR jsonb_typeof(j->'dispatch') IS DISTINCT FROM 'number' THEN RETURN false; END IF;
        RETURN (j->>'code' ~ '^[1-9][0-9]*$' AND (j->>'code')::integer BETWEEN 1 AND 17 AND j->>'dispatch' ~ '^[1-2]$'
            AND (j->>'round')::uuid<>'00000000-0000-0000-0000-000000000000'::uuid
            AND (expected_round IS NULL OR (j->>'round')::uuid=expected_round)) IS TRUE;
    ELSIF kind=11 THEN
        IF keys IS DISTINCT FROM ARRAY['attempt','digest','entry','key','kind','mapping','ordinal','organization','project','resource','revision','v']::text[]
            OR jsonb_typeof(j->'kind') IS DISTINCT FROM 'number' THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['organization','project','mapping','revision','entry','attempt','key','digest']::text[] LOOP
            IF jsonb_typeof(j->k) IS DISTINCT FROM 'string' THEN RETURN false; END IF;
        END LOOP;
        IF jsonb_typeof(j->'resource') IS NULL OR jsonb_typeof(j->'resource') NOT IN ('string','null')
            OR jsonb_typeof(j->'ordinal') IS NULL OR jsonb_typeof(j->'ordinal') NOT IN ('number','null') THEN RETURN false; END IF;
        IF j->>'kind' !~ '^[1-9]$' OR j->>'key' !~ '^[0-9a-f]{64}$' OR j->>'digest' !~ '^[0-9a-f]{64}$' THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['organization','project','mapping','revision','entry','attempt']::text[] LOOP
            IF (j->>k)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN false; END IF;
        END LOOP;
        IF (j->>'kind')::integer IN (6,7) THEN
            IF jsonb_typeof(j->'ordinal') IS DISTINCT FROM 'number' THEN RETURN false; END IF;
            RETURN (j->'resource'='null'::jsonb AND j->>'ordinal' ~ '^(0|[1-9][0-9]*)$' AND (j->>'ordinal')::integer BETWEEN 0 AND 32767) IS TRUE;
        ELSIF (j->>'kind')::integer IN (8,9) THEN
            RETURN (j->'resource'='null'::jsonb AND j->'ordinal'='null'::jsonb) IS TRUE;
        END IF;
        IF jsonb_typeof(j->'resource') IS DISTINCT FROM 'string' THEN RETURN false; END IF;
        RETURN (j->'ordinal'='null'::jsonb AND octet_length(j->>'resource') BETWEEN 1 AND 2048) IS TRUE;
    ELSIF kind=13 THEN
        -- A workspace citation: every key is present; each kind fixes which are strings, numbers or null.
        IF keys IS DISTINCT FROM ARRAY['attempt','digest','entry','key','kind','mapping','ordinal','organization','project','resource','revision','section','subject','targetKind','v','version']::text[]
            OR jsonb_typeof(j->'kind') IS DISTINCT FROM 'number' OR j->>'kind' !~ '^[1-9][0-9]?$' THEN RETURN false; END IF;
        c:=(j->>'kind')::integer;
        IF c NOT BETWEEN 1 AND 11 THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['organization','project','mapping','revision','entry','attempt','subject']::text[] LOOP
            IF (CASE k WHEN 'organization' THEN true WHEN 'project' THEN c<>11 WHEN 'mapping' THEN c BETWEEN 2 AND 9
                WHEN 'revision' THEN c IN (3,4,5) WHEN 'subject' THEN c BETWEEN 5 AND 10 ELSE c=4 END) THEN
                IF jsonb_typeof(j->k) IS DISTINCT FROM 'string' OR (j->>k)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN false; END IF;
            ELSIF j->k<>'null'::jsonb THEN RETURN false; END IF;
        END LOOP;
        FOREACH k IN ARRAY ARRAY['key','digest']::text[] LOOP
            IF c=4 THEN
                IF jsonb_typeof(j->k) IS DISTINCT FROM 'string' OR j->>k !~ '^[0-9a-f]{64}$' THEN RETURN false; END IF;
            ELSIF j->k<>'null'::jsonb THEN RETURN false; END IF;
        END LOOP;
        IF c IN (6,10) THEN
            IF jsonb_typeof(j->'version') IS DISTINCT FROM 'number' OR j->>'version' !~ '^[1-9][0-9]{0,18}$' THEN RETURN false; END IF;
            n:=(j->>'version')::numeric; IF n>9223372036854775807 THEN RETURN false; END IF;
        ELSIF j->'version'<>'null'::jsonb THEN RETURN false; END IF;
        IF c=11 THEN
            IF jsonb_typeof(j->'section') IS DISTINCT FROM 'string' OR j->>'section' !~ '^[a-z0-9][a-z0-9-]{0,63}$' THEN RETURN false; END IF;
        ELSIF j->'section'<>'null'::jsonb THEN RETURN false; END IF;
        IF c<>4 THEN
            RETURN (j->'targetKind'='null'::jsonb AND j->'resource'='null'::jsonb AND j->'ordinal'='null'::jsonb) IS TRUE;
        END IF;
        IF jsonb_typeof(j->'targetKind') IS DISTINCT FROM 'number' OR j->>'targetKind' !~ '^[1-9]$'
            OR jsonb_typeof(j->'resource') IS NULL OR jsonb_typeof(j->'resource') NOT IN ('string','null')
            OR jsonb_typeof(j->'ordinal') IS NULL OR jsonb_typeof(j->'ordinal') NOT IN ('number','null') THEN RETURN false; END IF;
        t:=(j->>'targetKind')::integer;
        IF t IN (6,7) THEN
            IF jsonb_typeof(j->'ordinal') IS DISTINCT FROM 'number' THEN RETURN false; END IF;
            RETURN (j->'resource'='null'::jsonb AND j->>'ordinal' ~ '^(0|[1-9][0-9]*)$' AND (j->>'ordinal')::integer BETWEEN 0 AND 32767) IS TRUE;
        ELSIF t IN (8,9) THEN
            RETURN (j->'resource'='null'::jsonb AND j->'ordinal'='null'::jsonb) IS TRUE;
        END IF;
        IF jsonb_typeof(j->'resource') IS DISTINCT FROM 'string' THEN RETURN false; END IF;
        RETURN (j->'ordinal'='null'::jsonb AND octet_length(j->>'resource') BETWEEN 1 AND 2048) IS TRUE;
    ELSIF kind=14 THEN
        -- Shared payload accounting: identities and byte counts only, never content.
        IF keys IS DISTINCT FROM ARRAY['bytes','project','source','subject','v','version']::text[]
            OR jsonb_typeof(j->'project') IS DISTINCT FROM 'string' OR jsonb_typeof(j->'subject') IS DISTINCT FROM 'string'
            OR jsonb_typeof(j->'source') IS DISTINCT FROM 'number' OR j->>'source' !~ '^[1-4]$'
            OR jsonb_typeof(j->'bytes') IS DISTINCT FROM 'number' OR j->>'bytes' !~ '^[1-9][0-9]{0,6}$' THEN RETURN false; END IF;
        IF (j->>'project')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR (j->>'subject')::uuid='00000000-0000-0000-0000-000000000000'::uuid
            OR (j->>'bytes')::integer>1048576 THEN RETURN false; END IF;
        IF (j->>'source')::integer=3 THEN
            IF jsonb_typeof(j->'version') IS DISTINCT FROM 'number' OR j->>'version' !~ '^[1-9][0-9]{0,18}$' THEN RETURN false; END IF;
            n:=(j->>'version')::numeric; RETURN n<=9223372036854775807;
        END IF;
        RETURN j->'version'='null'::jsonb;
    ELSIF kind IN (5,6,7,8,12) THEN
        IF octet_length(value)>2048 OR keys IS DISTINCT FROM ARRAY['failure','finish','usage','v']::text[] THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['usage','failure']::text[] LOOP
            IF jsonb_typeof(j->k) IS NULL OR jsonb_typeof(j->k) NOT IN ('object','null') THEN RETURN false; END IF;
        END LOOP;
        IF jsonb_typeof(j->'finish') IS NULL OR jsonb_typeof(j->'finish') NOT IN ('number','null') THEN RETURN false; END IF;
        usage:=j->'usage'; failure:=j->'failure';
        IF usage='null'::jsonb AND failure='null'::jsonb THEN RETURN false; END IF;
        IF usage<>'null'::jsonb AND braid.chat_metadata_valid(9,usage::text,expected_round) IS NOT TRUE THEN RETURN false; END IF;
        IF failure<>'null'::jsonb AND braid.chat_metadata_valid(10,failure::text,expected_round) IS NOT TRUE THEN RETURN false; END IF;
        IF usage<>'null'::jsonb AND failure<>'null'::jsonb AND usage->>'round' IS DISTINCT FROM failure->>'round' THEN RETURN false; END IF;
        IF j->'finish'<>'null'::jsonb AND (j->>'finish' !~ '^[1-5]$' OR usage='null'::jsonb OR failure<>'null'::jsonb) THEN RETURN false; END IF;
        RETURN true;
    END IF;
    RETURN false;
EXCEPTION WHEN OTHERS THEN RETURN false;
END;
$chat_metadata_valid$;

CREATE OR REPLACE FUNCTION braid.guard_chat_child() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $guard_chat_child$
DECLARE c braid.chat_conversations%ROWTYPE; t braid.chat_turns%ROWTYPE; r braid.chat_rounds%ROWTYPE; lifecycle_allowed boolean:=false; j jsonb;
BEGIN
    IF TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'Chat truncate refused.' USING ERRCODE='23514'; END IF;
    IF TG_OP='DELETE' THEN
        -- Only the guarded parent cascade may delete child content; no child DELETE is granted.
        IF EXISTS(SELECT 1 FROM braid.chat_conversations x WHERE x.organization_id=OLD.organization_id AND x.creator_id=OLD.creator_id AND x.conversation_id=OLD.conversation_id) THEN
            RAISE EXCEPTION 'Chat child deletion refused.' USING ERRCODE='23514';
        END IF;
        RETURN OLD;
    END IF;
    SELECT * INTO c FROM braid.chat_conversations x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id;
    IF braid.chat_is_lifecycle() THEN
        IF TG_TABLE_NAME='chat_turns' THEN lifecycle_allowed:=TG_OP='UPDATE' AND NEW.phase IN (4,5);
        ELSIF TG_TABLE_NAME='chat_events' THEN lifecycle_allowed:=TG_OP='INSERT' AND NEW.kind IN (7,8); END IF;
    END IF;
    IF c.conversation_id IS NULL OR (braid.chat_authorized(c.organization_id,c.project_id,c.creator_id) IS NOT TRUE AND NOT lifecycle_allowed) THEN
        RAISE EXCEPTION 'Chat authority unavailable.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='chat_turns' THEN
        IF TG_OP='INSERT' THEN
            IF NOT EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=2 AND i.identity_id=NEW.turn_id AND i.erased_at IS NULL)
                OR NOT EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=3 AND i.identity_id=NEW.request_id AND i.erased_at IS NULL) THEN
                RAISE EXCEPTION 'Chat turn/request identity unavailable.' USING ERRCODE='23514';
            END IF;
            IF NEW.created_at<>statement_timestamp() OR NEW.deadline>c.retain_until OR c.retain_until<=statement_timestamp()
                OR NEW.phase<>1 OR NEW.version<>1 OR NEW.event_count<>1 OR NEW.event_bytes<>0 OR NEW.round_count<>0 THEN
                RAISE EXCEPTION 'Invalid chat turn admission.' USING ERRCODE='23514';
            END IF;
        ELSE
            IF (to_jsonb(NEW)-ARRAY['phase','version','event_count','event_bytes','round_count','finished_at'])
                IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['phase','version','event_count','event_bytes','round_count','finished_at'])
                OR OLD.phase<>1 OR OLD.version=9223372036854775807 OR NEW.version<>OLD.version+1
                OR NEW.event_count<OLD.event_count OR NEW.event_count>OLD.event_count+1
                OR NEW.event_bytes<OLD.event_bytes OR NEW.round_count<OLD.round_count OR NEW.round_count>OLD.round_count+1 THEN
                RAISE EXCEPTION 'Invalid chat turn transition.' USING ERRCODE='23514';
            END IF;
            IF NEW.phase<>1 THEN
                IF NEW.finished_at IS DISTINCT FROM statement_timestamp() OR NEW.event_count<>OLD.event_count+1 OR NEW.event_bytes<>OLD.event_bytes OR NEW.round_count<>OLD.round_count
                    OR (NEW.phase=2 AND (statement_timestamp()>=OLD.deadline OR NOT EXISTS(SELECT 1 FROM braid.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=NEW.round_count AND x.outcome=3))) THEN
                    RAISE EXCEPTION 'Invalid chat terminal publication.' USING ERRCODE='23514';
                END IF;
            ELSIF statement_timestamp()>=OLD.deadline OR statement_timestamp()>=c.retain_until OR NEW.event_count>=NEW.max_events THEN
                RAISE EXCEPTION 'Chat turn budget expired.' USING ERRCODE='23514';
            ELSIF NOT (
                (NEW.event_count=OLD.event_count+1 AND NEW.event_bytes>OLD.event_bytes AND NEW.round_count=OLD.round_count)
                OR (NEW.event_count=OLD.event_count AND NEW.event_bytes=OLD.event_bytes AND NEW.round_count IN (OLD.round_count,OLD.round_count+1))) THEN
                RAISE EXCEPTION 'Chat transitions advance one ledger operation.' USING ERRCODE='23514';
            END IF;
        END IF;
        RETURN NEW;
    END IF;
    SELECT * INTO t FROM braid.chat_turns x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Missing chat turn.' USING ERRCODE='23514'; END IF;
    IF TG_TABLE_NAME='chat_rounds' THEN
        IF NEW.result_metadata IS NOT NULL AND (braid.chat_metadata_valid(12,NEW.result_metadata,NEW.round_id) IS NOT TRUE
            OR (NEW.result_metadata::jsonb->'failure'<>'null'::jsonb AND NEW.outcome<>4)) THEN
            RAISE EXCEPTION 'Invalid closed round metadata.' USING ERRCODE='23514';
        END IF;
        IF NEW.result_metadata IS NOT NULL THEN
            j:=NEW.result_metadata::jsonb;
            IF (j->'usage'<>'null'::jsonb AND (j->'usage'->>'origin')::integer NOT IN (2,3))
                OR ((j->'usage'->>'origin')::integer=3 AND (NEW.outcome<>4 OR j->'failure'='null'::jsonb))
                OR (j->'finish'<>'null'::jsonb AND (NEW.outcome=4 OR ((NEW.outcome=2)<>((j->>'finish')::integer=2))))
                OR NOT EXISTS(SELECT 1 FROM braid.chat_events e WHERE e.organization_id=NEW.organization_id AND e.creator_id=NEW.creator_id
                    AND e.conversation_id=NEW.conversation_id AND e.turn_id=NEW.turn_id AND e.sequence=t.event_count AND e.kind=12
                    AND e.round_id=NEW.round_id AND e.dispatch_fence=NEW.fence AND e.metadata_json=NEW.result_metadata) THEN
                RAISE EXCEPTION 'Round accounting publication is inconsistent.' USING ERRCODE='23514';
            END IF;
        END IF;
        IF t.phase<>1 OR statement_timestamp()>=t.deadline OR statement_timestamp()>=c.retain_until THEN
            RAISE EXCEPTION 'Chat round closed.' USING ERRCODE='23514';
        END IF;
        IF TG_OP='INSERT' THEN
            IF NOT EXISTS(SELECT 1 FROM braid.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=4 AND i.identity_id=NEW.round_id AND i.erased_at IS NULL)
                OR NEW.result_metadata IS NOT NULL THEN RAISE EXCEPTION 'Chat round identity unavailable.' USING ERRCODE='23514'; END IF;
            IF NEW.ordinal<>t.round_count OR NEW.outcome<>1 OR NEW.finished_at IS NOT NULL OR NEW.reserved_at<>statement_timestamp()
                OR (NEW.ordinal>1 AND NOT EXISTS(SELECT 1 FROM braid.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=NEW.ordinal-1 AND x.outcome=2 AND x.instance_id=NEW.instance_id)) THEN
                RAISE EXCEPTION 'Chat round cannot be replayed.' USING ERRCODE='23514';
            END IF;
        ELSIF (to_jsonb(NEW)-ARRAY['outcome','finished_at','result_metadata']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['outcome','finished_at','result_metadata'])
            OR OLD.outcome<>1 OR NEW.outcome=1 OR NEW.ordinal<>t.round_count OR NEW.finished_at IS DISTINCT FROM statement_timestamp() THEN
            RAISE EXCEPTION 'Chat round transition refused.' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP<>'INSERT' OR NEW.sequence<>t.event_count THEN RAISE EXCEPTION 'Immutable ordered chat event.' USING ERRCODE='23514'; END IF;
    IF NEW.metadata_json IS NOT NULL AND braid.chat_metadata_valid(NEW.kind,NEW.metadata_json,NEW.round_id) IS NOT TRUE THEN
        RAISE EXCEPTION 'Invalid typed chat metadata.' USING ERRCODE='23514';
    END IF;
    IF NEW.kind IN (5,6,7,8) AND NEW.metadata_json IS NOT NULL THEN
        j:=NEW.metadata_json::jsonb;
        IF NOT EXISTS(SELECT 1 FROM braid.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
            AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=t.round_count
            AND x.round_id=coalesce(j->'usage'->>'round',j->'failure'->>'round')::uuid)
            OR (NEW.kind=5 AND j->'failure'<>'null'::jsonb) THEN
            RAISE EXCEPTION 'Terminal accounting belongs to another round.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.kind IN (5,6,7,8) THEN
        SELECT * INTO r FROM braid.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
            AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=t.round_count;
        IF r.result_metadata IS NOT NULL AND NEW.metadata_json IS DISTINCT FROM r.result_metadata THEN
            RAISE EXCEPTION 'Committed round accounting is immutable.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF (NEW.kind=11 AND c.conversation_kind<>1) OR (NEW.kind IN (13,14) AND c.conversation_kind<>2) THEN
        RAISE EXCEPTION 'Chat event kind does not belong to this conversation.' USING ERRCODE='23514';
    END IF;
    IF NEW.kind=13 THEN
        j:=NEW.metadata_json::jsonb;
        IF (j->>'organization')::uuid IS DISTINCT FROM c.organization_id THEN
            RAISE EXCEPTION 'Citation belongs to another organization.' USING ERRCODE='23514';
        END IF;
        IF (j->>'kind')::integer=4 AND NOT EXISTS(SELECT 1 FROM braid.revision_derivations d JOIN braid.revision_derivation_attempts a
            ON a.organization_id=d.organization_id AND a.project_id=d.project_id AND a.mapping_id=d.mapping_id
            AND a.revision_id=d.revision_id AND a.cache_entry_id=d.cache_entry_id
            WHERE d.organization_id=c.organization_id AND d.project_id=(j->>'project')::uuid AND d.mapping_id=(j->>'mapping')::uuid
            AND d.revision_id=(j->>'revision')::uuid AND d.cache_entry_id=(j->>'entry')::uuid AND a.derivation_id=(j->>'attempt')::uuid
            AND d.key_fingerprint=j->>'key' AND a.derived_digest=j->>'digest' AND a.result_usable AND a.completion_kind='usable'
            AND d.operation_kind=CASE WHEN (j->>'targetKind')::integer IN (7,9) THEN 'validation' ELSE 'projection' END) THEN
            RAISE EXCEPTION 'Citation does not identify an exact retained report.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.kind=14 AND braid.chat_authorized(c.organization_id,(NEW.metadata_json::jsonb->>'project')::uuid,c.creator_id) IS NOT TRUE THEN
        RAISE EXCEPTION 'Shared data requires current project authority.' USING ERRCODE='42501';
    END IF;
    IF NEW.kind=11 THEN
        j:=NEW.metadata_json::jsonb;
        IF ROW((j->>'organization')::uuid,(j->>'project')::uuid,(j->>'mapping')::uuid,(j->>'revision')::uuid)
            IS DISTINCT FROM ROW(c.organization_id,c.project_id,c.mapping_id,c.revision_id)
            OR (CASE WHEN (j->>'kind')::integer IN (7,9) THEN
                ROW((j->>'entry')::uuid,(j->>'attempt')::uuid,j->>'key',j->>'digest') IS DISTINCT FROM ROW(c.validation_entry_id,c.validation_id,c.validation_key,c.validation_digest)
                ELSE ROW((j->>'entry')::uuid,(j->>'attempt')::uuid,j->>'key',j->>'digest') IS DISTINCT FROM ROW(c.projection_entry_id,c.projection_id,c.projection_key,c.projection_digest) END) THEN
            RAISE EXCEPTION 'Citation does not identify the exact retained report.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.kind=1 THEN
        IF NEW.sequence<>1 OR t.phase<>1 OR t.version<>1 THEN RAISE EXCEPTION 'Invalid chat start event.' USING ERRCODE='23514'; END IF;
    ELSIF NEW.kind IN (5,6,7,8) THEN
        IF NEW.kind<>t.phase+3 OR t.phase=1 THEN RAISE EXCEPTION 'Invalid chat terminal event.' USING ERRCODE='23514'; END IF;
    ELSE
        SELECT * INTO r FROM braid.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.round_id=NEW.round_id;
        IF NOT FOUND OR t.phase<>1 OR r.outcome<>1 OR r.ordinal<>t.round_count OR r.fence<>NEW.dispatch_fence
            OR statement_timestamp()>=t.deadline OR statement_timestamp()>=c.retain_until OR NEW.sequence>=t.max_events THEN
            RAISE EXCEPTION 'Chat event fence refused.' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$guard_chat_child$;

CREATE OR REPLACE FUNCTION braid.chat_reserve_identity(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_kind integer,p_identity uuid,p_turn uuid)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $chat_reserve_identity$
DECLARE c braid.chat_conversations%ROWTYPE; i braid.chat_identities%ROWTYPE; inserted integer;
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_chat_writer' THEN
        RAISE EXCEPTION 'Chat writer identity routine required.' USING ERRCODE='42501';
    END IF;
    IF braid.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    SELECT * INTO c FROM braid.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation;
    IF (c.conversation_id IS NULL AND p_kind<>1) OR (c.conversation_id IS NOT NULL AND c.project_id IS DISTINCT FROM p_project) THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    -- The subtransaction undoes a candidate if current authority disappears during
    -- unique arbitration, without acknowledging an orphan reservation.
    BEGIN
        INSERT INTO braid.chat_identities(organization_id,creator_id,identity_kind,identity_id,conversation_id,turn_id)
            VALUES(p_org,p_creator,p_kind,p_identity,p_conversation,p_turn)
            ON CONFLICT(organization_id,creator_id,identity_kind,identity_id) DO NOTHING;
        GET DIAGNOSTICS inserted=ROW_COUNT;
        IF braid.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE THEN
            RAISE EXCEPTION 'Chat authority changed during identity arbitration.' USING ERRCODE='PCA01';
        END IF;
        IF inserted=1 THEN RETURN 'Applied'; END IF;
        SELECT * INTO i FROM braid.chat_identities x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.identity_kind=p_kind AND x.identity_id=p_identity;
        IF NOT FOUND THEN RAISE EXCEPTION 'Chat identity facts unavailable.' USING ERRCODE='23514'; END IF;
        IF i.erased_at IS NOT NULL THEN RETURN 'IdentityRetired'; END IF;
        IF i.conversation_id<>p_conversation OR i.turn_id IS DISTINCT FROM p_turn THEN RETURN 'Conflict'; END IF;
        RETURN 'Replayed';
    EXCEPTION WHEN SQLSTATE 'PCA01' THEN RETURN 'NotAuthorizedOrNotFound';
    END;
END;
$chat_reserve_identity$;

CREATE OR REPLACE FUNCTION braid.chat_schema_lock() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $chat_schema_lock$
BEGIN
    IF current_user<>'braid_migrator' OR session_user NOT IN ('braid_chat_reader','braid_chat_writer','braid_chat_lifecycle') THEN
        RAISE EXCEPTION 'Restricted chat profile required.' USING ERRCODE='42501';
    END IF;
    LOCK TABLE ONLY braid.schema_migrations,ONLY braid.principals,ONLY braid.organization_memberships,ONLY braid.project_memberships,
        ONLY braid.chat_conversations,ONLY braid.chat_turns,ONLY braid.chat_rounds,ONLY braid.chat_events,ONLY braid.chat_identities,ONLY braid.chat_payload_audit,
        ONLY braid.revision_derivations,ONLY braid.revision_derivation_attempts IN ACCESS SHARE MODE;
END;
$chat_schema_lock$;

CREATE OR REPLACE FUNCTION braid.chat_lifecycle_observe(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_turn uuid)
RETURNS TABLE(phase integer,version bigint)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $chat_lifecycle_observe$
BEGIN
    IF NOT braid.chat_is_lifecycle() THEN RAISE EXCEPTION 'Trusted chat lifecycle profile required.' USING ERRCODE='42501'; END IF;
    RETURN QUERY SELECT t.phase,t.version FROM braid.chat_turns t JOIN braid.chat_conversations c USING(organization_id,creator_id,conversation_id)
        WHERE c.organization_id=p_org AND c.creator_id=p_creator AND c.conversation_id=p_conversation
        AND c.project_id IS NOT DISTINCT FROM p_project AND c.mapping_id IS NOT DISTINCT FROM p_mapping AND c.revision_id IS NOT DISTINCT FROM p_revision AND t.turn_id=p_turn;
END;
$chat_lifecycle_observe$;

CREATE OR REPLACE FUNCTION braid.chat_lifecycle_close(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,
    p_turn uuid,p_expected bigint,p_phase integer,p_event uuid,p_metadata text) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
SET braid.organization_id='' SET braid.chat_principal_id=''
AS $chat_lifecycle_close$
DECLARE c braid.chat_conversations%ROWTYPE; t braid.chat_turns%ROWTYPE; metadata text; last_round uuid;
BEGIN
    IF NOT braid.chat_is_lifecycle() THEN RAISE EXCEPTION 'Trusted chat lifecycle profile required.' USING ERRCODE='42501'; END IF;
    PERFORM pg_catalog.set_config('braid.organization_id',p_org::text,true),pg_catalog.set_config('braid.chat_principal_id',p_creator::text,true);
    SET CONSTRAINTS braid.chat_counters DEFERRED;
    IF p_phase NOT IN (4,5) OR p_expected<1 OR p_event IS NULL OR p_event='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'Invalid chat lifecycle command.' USING ERRCODE='23514';
    END IF;
    SELECT * INTO c FROM braid.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation
        AND x.project_id IS NOT DISTINCT FROM p_project AND x.mapping_id IS NOT DISTINCT FROM p_mapping AND x.revision_id IS NOT DISTINCT FROM p_revision FOR UPDATE;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    SELECT * INTO t FROM braid.chat_turns x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation AND x.turn_id=p_turn;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF t.version<>p_expected THEN RETURN 'Conflict'; END IF;
    IF t.phase<>1 THEN RETURN 'InvalidState'; END IF;
    IF t.version=9223372036854775807 OR t.event_count>=t.max_events THEN RETURN 'LimitReached'; END IF;
    SELECT r.result_metadata,r.round_id INTO metadata,last_round FROM braid.chat_rounds r WHERE r.organization_id=p_org AND r.creator_id=p_creator
        AND r.conversation_id=p_conversation AND r.turn_id=p_turn ORDER BY ordinal DESC LIMIT 1;
    IF p_metadata IS NOT NULL THEN
        IF last_round IS NULL OR octet_length(p_metadata)>2048 OR braid.chat_metadata_valid(p_phase+3,p_metadata,last_round) IS NOT TRUE THEN
            RAISE EXCEPTION 'Invalid lifecycle accounting metadata.' USING ERRCODE='23514';
        END IF;
        IF metadata IS NOT NULL AND p_metadata<>metadata THEN RETURN 'Conflict'; END IF;
        metadata:=p_metadata;
    END IF;
    UPDATE braid.chat_turns SET version=version+1,phase=p_phase,finished_at=statement_timestamp(),event_count=event_count+1
        WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation AND turn_id=p_turn AND version=p_expected AND phase=1;
    IF NOT FOUND THEN RETURN 'Conflict'; END IF;
    INSERT INTO braid.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,metadata_json)
        VALUES(p_org,p_creator,p_conversation,p_turn,t.event_count+1,p_event,p_phase+3,'',metadata);
    -- Drain only chat checks while still inside the trusted routine. The lifecycle
    -- login gains no content SELECT and the check itself remains SECURITY INVOKER.
    SET CONSTRAINTS braid.chat_counters IMMEDIATE;
    SET CONSTRAINTS braid.chat_counters DEFERRED;
    RETURN 'Applied';
END;
$chat_lifecycle_close$;

CREATE OR REPLACE FUNCTION braid.chat_erase_expired(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,
    p_policy text,p_policy_version integer,p_retirement_not_before timestamptz) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $chat_erase_expired$
DECLARE c braid.chat_conversations%ROWTYPE;
BEGIN
    SELECT * INTO c FROM braid.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation
        AND x.project_id IS NOT DISTINCT FROM p_project AND x.mapping_id IS NOT DISTINCT FROM p_mapping AND x.revision_id IS NOT DISTINCT FROM p_revision FOR UPDATE;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF braid.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE AND NOT braid.chat_is_lifecycle() THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF c.retain_until>statement_timestamp() OR EXISTS(SELECT 1 FROM braid.chat_turns t WHERE t.organization_id=p_org AND t.creator_id=p_creator AND t.conversation_id=p_conversation AND t.phase=1) THEN RETURN 'InvalidState'; END IF;
    IF p_policy IS NULL OR p_policy !~ '^[a-z0-9][a-z0-9._-]{0,63}$' OR p_policy_version IS NULL OR p_policy_version<1
        OR p_retirement_not_before IS NULL OR NOT isfinite(p_retirement_not_before) OR p_retirement_not_before<=statement_timestamp()
        OR p_retirement_not_before>'9999-12-31 23:59:59.999999+00'::timestamptz THEN
        RAISE EXCEPTION 'Explicit chat erasure policy required.' USING ERRCODE='23514';
    END IF;
    UPDATE braid.chat_identities SET erased_at=statement_timestamp(),retirement_policy_id=p_policy,
        retirement_policy_version=p_policy_version,retirement_not_before=p_retirement_not_before
        WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation AND erased_at IS NULL;
    DELETE FROM braid.chat_conversations WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation;
    RETURN 'Applied';
END;
$chat_erase_expired$;

GRANT SELECT(conversation_kind,focus_project_id,focus_mapping_id,focus_revision_id) ON braid.chat_conversations TO braid_chat_reader,braid_chat_writer;
GRANT INSERT(conversation_kind,focus_project_id,focus_mapping_id,focus_revision_id) ON braid.chat_conversations TO braid_chat_writer;
