-- Workspace chat may save a mapping edit as the next version, or show it read-only. Each such write or
-- proposal is a new round-bound ledger event, and every one writes a no-content audit row that survives
-- conversation erasure: who wrote which version of which mapping from which base, with which profile.
DO $chat_mapping_writes_owner$
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Chat mapping writes require the separately provisioned owner login.';
    END IF;
END;
$chat_mapping_writes_owner$;

LOCK TABLE weft.chat_conversations,weft.chat_turns,weft.chat_rounds,weft.chat_events,weft.chat_identities,weft.chat_payload_audit IN ACCESS EXCLUSIVE MODE;

ALTER TABLE weft.chat_events DROP CONSTRAINT chat_events_kind_check, DROP CONSTRAINT chat_events_check,
    ADD CONSTRAINT chat_events_kind_check CHECK(kind BETWEEN 1 AND 15),
    ADD CONSTRAINT chat_events_check CHECK((kind IN (2,3,4) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NULL AND octet_length(private_text) BETWEEN 1 AND 8192 AND btrim(private_text)<>'')
        OR (kind IN (9,10,11,12,13,14,15) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NOT NULL AND private_text='')
        OR (kind=1 AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND metadata_json IS NULL)
        OR (kind IN (5,6,7,8) AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND (metadata_json IS NULL OR octet_length(metadata_json)<=2048)));

-- One row per mapping write event, with identities and versions only: no edit, change text or conversation text.
CREATE TABLE weft.chat_mapping_write_audit (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, event_id uuid NOT NULL,
    conversation_id uuid NOT NULL, turn_id uuid NOT NULL, round_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    base_revision_id uuid NOT NULL, base_sequence bigint NOT NULL, revision_id uuid, revision_sequence bigint,
    outcome integer NOT NULL, reason integer, operation_count integer NOT NULL, edit_digest text NOT NULL,
    profile_id text NOT NULL, profile_version integer NOT NULL, provider_id text NOT NULL, model_id text NOT NULL,
    metadata_policy_id text NOT NULL, metadata_policy_version integer NOT NULL, recorded_at timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,event_id)
);
ALTER TABLE weft.chat_mapping_write_audit ENABLE ROW LEVEL SECURITY;
CREATE POLICY creator_scope ON weft.chat_mapping_write_audit USING (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid) WITH CHECK (organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid);
REVOKE ALL ON weft.chat_mapping_write_audit FROM PUBLIC;

CREATE FUNCTION weft.guard_chat_mapping_write_audit() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $guard_chat_mapping_write_audit$
BEGIN
    RAISE EXCEPTION 'Chat mapping write audit is append-only.' USING ERRCODE='23514';
END;
$guard_chat_mapping_write_audit$;
CREATE TRIGGER chat_mapping_write_audit_guard BEFORE UPDATE OR DELETE ON weft.chat_mapping_write_audit FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();
CREATE TRIGGER chat_mapping_write_audit_truncate BEFORE TRUNCATE ON weft.chat_mapping_write_audit FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_mapping_write_audit();

-- The audit row is written in the same statement as its event, by the owner, from the admitted event and turn.
CREATE FUNCTION weft.audit_chat_mapping_write() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $audit_chat_mapping_write$
DECLARE j jsonb; t weft.chat_turns%ROWTYPE;
BEGIN
    IF NEW.kind<>15 THEN RETURN NULL; END IF;
    SELECT * INTO t FROM weft.chat_turns x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
        AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Missing chat turn.' USING ERRCODE='23514'; END IF;
    j:=NEW.metadata_json::jsonb;
    INSERT INTO weft.chat_mapping_write_audit(organization_id,creator_id,event_id,conversation_id,turn_id,round_id,project_id,mapping_id,
        base_revision_id,base_sequence,revision_id,revision_sequence,outcome,reason,operation_count,edit_digest,
        profile_id,profile_version,provider_id,model_id,metadata_policy_id,metadata_policy_version,recorded_at)
        VALUES(NEW.organization_id,NEW.creator_id,NEW.event_id,NEW.conversation_id,NEW.turn_id,NEW.round_id,(j->>'project')::uuid,
            (j->>'mapping')::uuid,(j->>'base')::uuid,(j->>'baseSequence')::bigint,(j->>'revision')::uuid,(j->>'sequence')::bigint,
            (j->>'outcome')::integer,(j->>'reason')::integer,(j->>'operations')::integer,j->>'digest',t.profile_id,t.profile_version,
            t.provider_id,t.model_id,t.metadata_policy_id,t.metadata_policy_version,statement_timestamp());
    RETURN NULL;
END;
$audit_chat_mapping_write$;
REVOKE ALL ON FUNCTION weft.guard_chat_mapping_write_audit(),weft.audit_chat_mapping_write() FROM PUBLIC;
CREATE TRIGGER chat_mapping_write_audit AFTER INSERT ON weft.chat_events FOR EACH ROW EXECUTE FUNCTION weft.audit_chat_mapping_write();

CREATE OR REPLACE FUNCTION weft.chat_metadata_valid(kind integer, value text, expected_round uuid) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
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
    ELSIF kind=15 THEN
        -- A mapping edit a chat tool saved or proposed read-only: identities, versions, the edit's size and
        -- digest and bounded plain-language changes derived from verified projections.
        IF keys IS DISTINCT FROM ARRAY['base','baseSequence','changes','digest','head','headSequence','mapping','operations','organization','outcome','project','reason','revision','sequence','truncated','v']::text[]
            OR jsonb_typeof(j->'outcome') IS DISTINCT FROM 'number' OR j->>'outcome' !~ '^[1-2]$'
            OR jsonb_typeof(j->'operations') IS DISTINCT FROM 'number' OR j->>'operations' !~ '^[1-9][0-9]{0,3}$'
            OR jsonb_typeof(j->'digest') IS DISTINCT FROM 'string' OR j->>'digest' !~ '^[0-9a-f]{64}$'
            OR jsonb_typeof(j->'truncated') IS DISTINCT FROM 'boolean' OR jsonb_typeof(j->'changes') IS DISTINCT FROM 'array'
            OR jsonb_array_length(j->'changes')>8 OR (j->>'operations')::integer>2000 THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['organization','project','mapping','base']::text[] LOOP
            IF jsonb_typeof(j->k) IS DISTINCT FROM 'string' OR (j->>k)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN false; END IF;
        END LOOP;
        FOREACH k IN ARRAY ARRAY['baseSequence','sequence','headSequence']::text[] LOOP
            IF jsonb_typeof(j->k)='number' THEN
                IF j->>k !~ '^[1-9][0-9]{0,18}$' OR (j->>k)::numeric>9223372036854775807 THEN RETURN false; END IF;
            ELSIF k='baseSequence' OR j->k IS DISTINCT FROM 'null'::jsonb THEN RETURN false; END IF;
        END LOOP;
        FOREACH k IN ARRAY ARRAY['revision','head']::text[] LOOP
            IF jsonb_typeof(j->k)='string' THEN
                IF (j->>k)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN false; END IF;
            ELSIF j->k IS DISTINCT FROM 'null'::jsonb THEN RETURN false; END IF;
        END LOOP;
        IF (j->'revision'='null'::jsonb)<>(j->'sequence'='null'::jsonb) OR (j->'head'='null'::jsonb)<>(j->'headSequence'='null'::jsonb) THEN RETURN false; END IF;
        IF (j->>'outcome')::integer=1 THEN
            IF j->'reason'<>'null'::jsonb OR j->'revision'='null'::jsonb OR j->'head'<>'null'::jsonb
                OR (j->>'sequence')::numeric<=(j->>'baseSequence')::numeric THEN RETURN false; END IF;
        ELSIF jsonb_typeof(j->'reason') IS DISTINCT FROM 'number' OR j->>'reason' !~ '^[1-2]$' OR j->'revision'<>'null'::jsonb
            OR ((j->>'reason')::integer=2)<>(j->'head'<>'null'::jsonb) THEN RETURN false;
        END IF;
        FOR c IN 0..jsonb_array_length(j->'changes')-1 LOOP
            usage:=j->'changes'->c;
            IF (SELECT array_agg(x ORDER BY x) FROM jsonb_object_keys(usage) x) IS DISTINCT FROM ARRAY['after','before','field']::text[] THEN RETURN false; END IF;
            FOREACH k IN ARRAY ARRAY['field','before','after']::text[] LOOP
                IF jsonb_typeof(usage->k) IS DISTINCT FROM 'string' OR octet_length(usage->>k) NOT BETWEEN 1 AND 1024 THEN RETURN false; END IF;
            END LOOP;
        END LOOP;
        RETURN true;
    ELSIF kind IN (5,6,7,8,12) THEN
        IF octet_length(value)>2048 OR keys IS DISTINCT FROM ARRAY['failure','finish','usage','v']::text[] THEN RETURN false; END IF;
        FOREACH k IN ARRAY ARRAY['usage','failure']::text[] LOOP
            IF jsonb_typeof(j->k) IS NULL OR jsonb_typeof(j->k) NOT IN ('object','null') THEN RETURN false; END IF;
        END LOOP;
        IF jsonb_typeof(j->'finish') IS NULL OR jsonb_typeof(j->'finish') NOT IN ('number','null') THEN RETURN false; END IF;
        usage:=j->'usage'; failure:=j->'failure';
        IF usage='null'::jsonb AND failure='null'::jsonb THEN RETURN false; END IF;
        IF usage<>'null'::jsonb AND weft.chat_metadata_valid(9,usage::text,expected_round) IS NOT TRUE THEN RETURN false; END IF;
        IF failure<>'null'::jsonb AND weft.chat_metadata_valid(10,failure::text,expected_round) IS NOT TRUE THEN RETURN false; END IF;
        IF usage<>'null'::jsonb AND failure<>'null'::jsonb AND usage->>'round' IS DISTINCT FROM failure->>'round' THEN RETURN false; END IF;
        IF j->'finish'<>'null'::jsonb AND (j->>'finish' !~ '^[1-5]$' OR usage='null'::jsonb OR failure<>'null'::jsonb) THEN RETURN false; END IF;
        RETURN true;
    END IF;
    RETURN false;
EXCEPTION WHEN OTHERS THEN RETURN false;
END;
$chat_metadata_valid$;

CREATE OR REPLACE FUNCTION weft.guard_chat_child() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $guard_chat_child$
DECLARE c weft.chat_conversations%ROWTYPE; t weft.chat_turns%ROWTYPE; r weft.chat_rounds%ROWTYPE; lifecycle_allowed boolean:=false; j jsonb;
BEGIN
    IF TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'Chat truncate refused.' USING ERRCODE='23514'; END IF;
    IF TG_OP='DELETE' THEN
        -- Only the guarded parent cascade may delete child content; no child DELETE is granted.
        IF EXISTS(SELECT 1 FROM weft.chat_conversations x WHERE x.organization_id=OLD.organization_id AND x.creator_id=OLD.creator_id AND x.conversation_id=OLD.conversation_id) THEN
            RAISE EXCEPTION 'Chat child deletion refused.' USING ERRCODE='23514';
        END IF;
        RETURN OLD;
    END IF;
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id;
    IF weft.chat_is_lifecycle() THEN
        IF TG_TABLE_NAME='chat_turns' THEN lifecycle_allowed:=TG_OP='UPDATE' AND NEW.phase IN (4,5);
        ELSIF TG_TABLE_NAME='chat_events' THEN lifecycle_allowed:=TG_OP='INSERT' AND NEW.kind IN (7,8); END IF;
    END IF;
    IF c.conversation_id IS NULL OR (weft.chat_authorized(c.organization_id,c.project_id,c.creator_id) IS NOT TRUE AND NOT lifecycle_allowed) THEN
        RAISE EXCEPTION 'Chat authority unavailable.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='chat_turns' THEN
        IF TG_OP='INSERT' THEN
            IF NOT EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=2 AND i.identity_id=NEW.turn_id AND i.erased_at IS NULL)
                OR NOT EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=3 AND i.identity_id=NEW.request_id AND i.erased_at IS NULL) THEN
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
                    OR (NEW.phase=2 AND (statement_timestamp()>=OLD.deadline OR NOT EXISTS(SELECT 1 FROM weft.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=NEW.round_count AND x.outcome=3))) THEN
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
    SELECT * INTO t FROM weft.chat_turns x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Missing chat turn.' USING ERRCODE='23514'; END IF;
    IF TG_TABLE_NAME='chat_rounds' THEN
        IF NEW.result_metadata IS NOT NULL AND (weft.chat_metadata_valid(12,NEW.result_metadata,NEW.round_id) IS NOT TRUE
            OR (NEW.result_metadata::jsonb->'failure'<>'null'::jsonb AND NEW.outcome<>4)) THEN
            RAISE EXCEPTION 'Invalid closed round metadata.' USING ERRCODE='23514';
        END IF;
        IF NEW.result_metadata IS NOT NULL THEN
            j:=NEW.result_metadata::jsonb;
            IF (j->'usage'<>'null'::jsonb AND (j->'usage'->>'origin')::integer NOT IN (2,3))
                OR ((j->'usage'->>'origin')::integer=3 AND (NEW.outcome<>4 OR j->'failure'='null'::jsonb))
                OR (j->'finish'<>'null'::jsonb AND (NEW.outcome=4 OR ((NEW.outcome=2)<>((j->>'finish')::integer=2))))
                OR NOT EXISTS(SELECT 1 FROM weft.chat_events e WHERE e.organization_id=NEW.organization_id AND e.creator_id=NEW.creator_id
                    AND e.conversation_id=NEW.conversation_id AND e.turn_id=NEW.turn_id AND e.sequence=t.event_count AND e.kind=12
                    AND e.round_id=NEW.round_id AND e.dispatch_fence=NEW.fence AND e.metadata_json=NEW.result_metadata) THEN
                RAISE EXCEPTION 'Round accounting publication is inconsistent.' USING ERRCODE='23514';
            END IF;
        END IF;
        IF t.phase<>1 OR statement_timestamp()>=t.deadline OR statement_timestamp()>=c.retain_until THEN
            RAISE EXCEPTION 'Chat round closed.' USING ERRCODE='23514';
        END IF;
        IF TG_OP='INSERT' THEN
            IF NOT EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.conversation_id=NEW.conversation_id AND i.turn_id=NEW.turn_id AND i.identity_kind=4 AND i.identity_id=NEW.round_id AND i.erased_at IS NULL)
                OR NEW.result_metadata IS NOT NULL THEN RAISE EXCEPTION 'Chat round identity unavailable.' USING ERRCODE='23514'; END IF;
            IF NEW.ordinal<>t.round_count OR NEW.outcome<>1 OR NEW.finished_at IS NOT NULL OR NEW.reserved_at<>statement_timestamp()
                OR (NEW.ordinal>1 AND NOT EXISTS(SELECT 1 FROM weft.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=NEW.ordinal-1 AND x.outcome=2 AND x.instance_id=NEW.instance_id)) THEN
                RAISE EXCEPTION 'Chat round cannot be replayed.' USING ERRCODE='23514';
            END IF;
        ELSIF (to_jsonb(NEW)-ARRAY['outcome','finished_at','result_metadata']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['outcome','finished_at','result_metadata'])
            OR OLD.outcome<>1 OR NEW.outcome=1 OR NEW.ordinal<>t.round_count OR NEW.finished_at IS DISTINCT FROM statement_timestamp() THEN
            RAISE EXCEPTION 'Chat round transition refused.' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END IF;
    IF TG_OP<>'INSERT' OR NEW.sequence<>t.event_count THEN RAISE EXCEPTION 'Immutable ordered chat event.' USING ERRCODE='23514'; END IF;
    IF NEW.metadata_json IS NOT NULL AND weft.chat_metadata_valid(NEW.kind,NEW.metadata_json,NEW.round_id) IS NOT TRUE THEN
        RAISE EXCEPTION 'Invalid typed chat metadata.' USING ERRCODE='23514';
    END IF;
    IF NEW.kind IN (5,6,7,8) AND NEW.metadata_json IS NOT NULL THEN
        j:=NEW.metadata_json::jsonb;
        IF NOT EXISTS(SELECT 1 FROM weft.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
            AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=t.round_count
            AND x.round_id=coalesce(j->'usage'->>'round',j->'failure'->>'round')::uuid)
            OR (NEW.kind=5 AND j->'failure'<>'null'::jsonb) THEN
            RAISE EXCEPTION 'Terminal accounting belongs to another round.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.kind IN (5,6,7,8) THEN
        SELECT * INTO r FROM weft.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id
            AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.ordinal=t.round_count;
        IF r.result_metadata IS NOT NULL AND NEW.metadata_json IS DISTINCT FROM r.result_metadata THEN
            RAISE EXCEPTION 'Committed round accounting is immutable.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF (NEW.kind=11 AND c.conversation_kind<>1) OR (NEW.kind IN (13,14,15) AND c.conversation_kind<>2) THEN
        RAISE EXCEPTION 'Chat event kind does not belong to this conversation.' USING ERRCODE='23514';
    END IF;
    IF NEW.kind=13 THEN
        j:=NEW.metadata_json::jsonb;
        IF (j->>'organization')::uuid IS DISTINCT FROM c.organization_id THEN
            RAISE EXCEPTION 'Citation belongs to another organization.' USING ERRCODE='23514';
        END IF;
        IF (j->>'kind')::integer=4 AND NOT EXISTS(SELECT 1 FROM weft.revision_derivations d JOIN weft.revision_derivation_attempts a
            ON a.organization_id=d.organization_id AND a.project_id=d.project_id AND a.mapping_id=d.mapping_id
            AND a.revision_id=d.revision_id AND a.cache_entry_id=d.cache_entry_id
            WHERE d.organization_id=c.organization_id AND d.project_id=(j->>'project')::uuid AND d.mapping_id=(j->>'mapping')::uuid
            AND d.revision_id=(j->>'revision')::uuid AND d.cache_entry_id=(j->>'entry')::uuid AND a.derivation_id=(j->>'attempt')::uuid
            AND d.key_fingerprint=j->>'key' AND a.derived_digest=j->>'digest' AND a.result_usable AND a.completion_kind='usable'
            AND d.operation_kind=CASE WHEN (j->>'targetKind')::integer IN (7,9) THEN 'validation' ELSE 'projection' END) THEN
            RAISE EXCEPTION 'Citation does not identify an exact retained report.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.kind=14 AND weft.chat_authorized(c.organization_id,(NEW.metadata_json::jsonb->>'project')::uuid,c.creator_id) IS NOT TRUE THEN
        RAISE EXCEPTION 'Shared data requires current project authority.' USING ERRCODE='42501';
    END IF;
    IF NEW.kind=15 THEN
        j:=NEW.metadata_json::jsonb;
        IF (j->>'organization')::uuid IS DISTINCT FROM c.organization_id
            OR weft.chat_authorized(c.organization_id,(j->>'project')::uuid,c.creator_id) IS NOT TRUE THEN
            RAISE EXCEPTION 'A mapping write record requires current project authority.' USING ERRCODE='42501';
        END IF;
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
        SELECT * INTO r FROM weft.chat_rounds x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id AND x.round_id=NEW.round_id;
        IF NOT FOUND OR t.phase<>1 OR r.outcome<>1 OR r.ordinal<>t.round_count OR r.fence<>NEW.dispatch_fence
            OR statement_timestamp()>=t.deadline OR statement_timestamp()>=c.retain_until OR NEW.sequence>=t.max_events THEN
            RAISE EXCEPTION 'Chat event fence refused.' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$guard_chat_child$;

CREATE OR REPLACE FUNCTION weft.chat_schema_lock() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_schema_lock$
BEGIN
    IF current_user<>'weft_migrator' OR session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle') THEN
        RAISE EXCEPTION 'Restricted chat profile required.' USING ERRCODE='42501';
    END IF;
    LOCK TABLE ONLY weft.schema_migrations,ONLY weft.principals,ONLY weft.organization_memberships,ONLY weft.project_memberships,
        ONLY weft.chat_conversations,ONLY weft.chat_turns,ONLY weft.chat_rounds,ONLY weft.chat_events,ONLY weft.chat_identities,ONLY weft.chat_payload_audit,ONLY weft.chat_mapping_write_audit,
        ONLY weft.revision_derivations,ONLY weft.revision_derivation_attempts IN ACCESS SHARE MODE;
END;
$chat_schema_lock$;
