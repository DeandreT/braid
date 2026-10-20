-- Source-prepared slot 0011: root must rebase the number against qualified P4 before integration.
-- No login is created here. All three separately provisioned profiles must initially be CONNECT-only.
DO $chat_profiles$
DECLARE profile_name text; r record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user
        OR (SELECT datdba<>(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=current_user) FROM pg_catalog.pg_database WHERE datname=current_database())
        OR current_setting('server_encoding') <> 'UTF8' OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Chat requires the accepted migration environment.';
    END IF;
    FOREACH profile_name IN ARRAY ARRAY['weft_chat_reader','weft_chat_writer','weft_chat_lifecycle']::text[] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=profile_name;
        IF NOT FOUND OR NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls
            OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
            OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE pg_catalog.left(n.nspname,8)<>'pg_temp_'
                AND pg_catalog.left(n.nspname,14)<>'pg_toast_temp_' AND (n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE')))
            OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE pg_catalog.left(n.nspname,3)<>'pg_'
                AND n.nspname NOT IN ('information_schema','public') AND pg_catalog.has_schema_privilege(r.oid,n.oid,'USAGE'))
            OR pg_catalog.has_database_privilege('public',current_database(),'CONNECT,CREATE,TEMPORARY')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE a.grantee=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_database d CROSS JOIN LATERAL pg_catalog.aclexplode(d.datacl) a
                WHERE d.datname=current_database() AND a.grantee=r.oid AND (a.privilege_type<>'CONNECT' OR a.is_grantable OR a.grantor<>d.datdba))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND c.relkind IN ('r','p','v','m','f') AND (c.relowner=r.oid
                    OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND (f.proowner=r.oid OR pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE')))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE pg_catalog.left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND CASE WHEN c.relkind='S' THEN c.relowner=r.oid OR pg_catalog.has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') ELSE false END)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                WHERE d.defaclrole=r.oid OR a.grantee IN (0,r.oid))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_parameter_acl p CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a WHERE a.grantee IN (0,r.oid)) THEN
            RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Chat requires explicitly provisioned CONNECT-only profiles.';
        END IF;
    END LOOP;
END;
$chat_profiles$;

CREATE FUNCTION weft.chat_authorized(org uuid, project uuid, creator uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $chat_authorized$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid
        AND creator=NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid
        AND EXISTS(SELECT 1 FROM ONLY weft.principals p
            JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=org AND o.active
            JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=org AND m.project_id=project AND m.active
            WHERE p.id=creator AND p.active AND m.role IN ('viewer','runner','publisher','administrator'))
$chat_authorized$;
REVOKE ALL ON FUNCTION weft.chat_authorized(uuid,uuid,uuid) FROM PUBLIC;

-- This is a semantic capability check, not role/grant admission. Only the separately
-- credentialed lifecycle login entering an owner-defined routine can satisfy it.
CREATE FUNCTION weft.chat_is_lifecycle() RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $chat_is_lifecycle$ SELECT current_user='weft_migrator' AND session_user='weft_chat_lifecycle' $chat_is_lifecycle$;
REVOKE ALL ON FUNCTION weft.chat_is_lifecycle() FROM PUBLIC;

-- Permanent non-content identity reservations become tombstones on erasure. No content,
-- prompt hash, response hash, profile or report identity is retained here.
CREATE TABLE weft.chat_identities (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, identity_kind integer NOT NULL CHECK(identity_kind BETWEEN 1 AND 4),
    identity_id uuid NOT NULL CHECK(identity_id<>'00000000-0000-0000-0000-000000000000'),
    conversation_id uuid NOT NULL CHECK(conversation_id<>'00000000-0000-0000-0000-000000000000'), turn_id uuid,
    erased_at timestamptz, retirement_policy_id text, retirement_policy_version integer, retirement_not_before timestamptz,
    PRIMARY KEY(organization_id,creator_id,identity_kind,identity_id),
    CONSTRAINT chat_identities_scope_check CHECK(organization_id<>'00000000-0000-0000-0000-000000000000' AND creator_id<>'00000000-0000-0000-0000-000000000000'),
    CONSTRAINT chat_identities_association_check CHECK((identity_kind=1 AND turn_id IS NULL AND identity_id=conversation_id)
        OR (identity_kind IN (2,3,4) AND turn_id IS NOT NULL AND turn_id<>'00000000-0000-0000-0000-000000000000' AND (identity_kind<>2 OR identity_id=turn_id))),
    CONSTRAINT chat_identities_erasure_check CHECK((erased_at IS NULL AND retirement_policy_id IS NULL AND retirement_policy_version IS NULL AND retirement_not_before IS NULL)
        OR (erased_at IS NOT NULL AND isfinite(erased_at) AND erased_at>'0001-01-01+00'::timestamptz
            AND retirement_policy_id IS NOT NULL AND retirement_policy_id~'^[a-z0-9][a-z0-9._-]{0,63}$'
            AND retirement_policy_version IS NOT NULL AND retirement_policy_version>0
            AND retirement_not_before IS NOT NULL AND isfinite(retirement_not_before) AND retirement_not_before>erased_at
            AND retirement_not_before<='9999-12-31 23:59:59.999999+00'::timestamptz))
);
CREATE INDEX chat_identities_conversation ON weft.chat_identities(organization_id,creator_id,conversation_id);

CREATE TABLE weft.chat_conversations (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, conversation_id uuid NOT NULL CHECK(conversation_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, revision_id uuid NOT NULL,
    projection_entry_id uuid NOT NULL, projection_id uuid NOT NULL, projection_key text NOT NULL CHECK(projection_key~'^[0-9a-f]{64}$'),
    projection_digest text NOT NULL CHECK(projection_digest~'^[0-9a-f]{64}$'),
    validation_entry_id uuid NOT NULL, validation_id uuid NOT NULL, validation_key text NOT NULL CHECK(validation_key~'^[0-9a-f]{64}$'),
    validation_digest text NOT NULL CHECK(validation_digest~'^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(), retain_until timestamptz NOT NULL,
    PRIMARY KEY(organization_id,creator_id,conversation_id),
    CONSTRAINT chat_conversations_creator_fkey FOREIGN KEY(creator_id) REFERENCES weft.principals(id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT chat_conversations_revision_fkey FOREIGN KEY(organization_id,project_id,mapping_id,revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT chat_conversations_projection_fkey FOREIGN KEY(organization_id,project_id,mapping_id,revision_id,projection_id) REFERENCES weft.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT chat_conversations_validation_fkey FOREIGN KEY(organization_id,project_id,mapping_id,revision_id,validation_id) REFERENCES weft.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT chat_conversations_check CHECK(projection_id<>validation_id),
    CONSTRAINT chat_conversations_check1 CHECK(isfinite(created_at) AND isfinite(retain_until) AND created_at>'0001-01-01+00'::timestamptz
        AND retain_until>created_at AND retain_until<='9999-12-31 23:59:59.999999+00'::timestamptz)
);

CREATE TABLE weft.chat_turns (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, conversation_id uuid NOT NULL, turn_id uuid NOT NULL CHECK(turn_id<>'00000000-0000-0000-0000-000000000000'),
    original_request text COLLATE "C" NOT NULL CHECK(octet_length(original_request) BETWEEN 1 AND 65536 AND btrim(original_request)<>''),
    request_id uuid NOT NULL CHECK(request_id<>'00000000-0000-0000-0000-000000000000'),
    original_envelope text COLLATE "C" NOT NULL CHECK(octet_length(original_envelope) BETWEEN 1 AND 524288),
    canonical_envelope text COLLATE "C" NOT NULL CHECK(octet_length(canonical_envelope) BETWEEN 1 AND 1048576),
    envelope_fingerprint text NOT NULL CONSTRAINT chat_turns_envelope_fingerprint_check CHECK(envelope_fingerprint=encode(sha256(convert_to(canonical_envelope,'UTF8')),'hex')),
    profile_id text NOT NULL CHECK(octet_length(profile_id) BETWEEN 1 AND 64 AND profile_id~'^[a-z0-9][a-z0-9._-]*$'), profile_version integer NOT NULL CHECK(profile_version>0),
    provider_id text NOT NULL CHECK(octet_length(provider_id) BETWEEN 1 AND 64 AND provider_id~'^[a-z0-9][a-z0-9._-]*$'),
    model_id text NOT NULL CHECK(octet_length(model_id) BETWEEN 1 AND 256 AND btrim(model_id)<>''),
    metadata_policy_id text NOT NULL CHECK(octet_length(metadata_policy_id) BETWEEN 1 AND 64 AND metadata_policy_id~'^[a-z0-9][a-z0-9._-]*$'),
    metadata_policy_version integer NOT NULL CHECK(metadata_policy_version>0), profile_fingerprint text NOT NULL CHECK(profile_fingerprint~'^[0-9a-f]{64}$'),
    max_events integer NOT NULL CHECK(max_events BETWEEN 2 AND 4096), max_event_bytes integer NOT NULL CHECK(max_event_bytes BETWEEN 1 AND 4194304),
    max_rounds integer NOT NULL CHECK(max_rounds BETWEEN 1 AND 4), deadline timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(), phase integer NOT NULL DEFAULT 1 CHECK(phase BETWEEN 1 AND 5),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0), event_count integer NOT NULL DEFAULT 1, event_bytes integer NOT NULL DEFAULT 0,
    round_count integer NOT NULL DEFAULT 0, finished_at timestamptz,
    PRIMARY KEY(organization_id,creator_id,conversation_id,turn_id),
    CONSTRAINT chat_turns_identity_key UNIQUE(organization_id,creator_id,turn_id), CONSTRAINT chat_turns_request_key UNIQUE(organization_id,creator_id,request_id),
    CONSTRAINT chat_turns_conversation_fkey FOREIGN KEY(organization_id,creator_id,conversation_id) REFERENCES weft.chat_conversations ON DELETE CASCADE ON UPDATE RESTRICT,
    CONSTRAINT chat_turns_check CHECK(event_count BETWEEN 1 AND max_events AND event_bytes BETWEEN 0 AND max_event_bytes AND round_count BETWEEN 0 AND max_rounds),
    CONSTRAINT chat_turns_check1 CHECK(isfinite(created_at) AND isfinite(deadline) AND created_at>'0001-01-01+00'::timestamptz
        AND created_at<deadline AND deadline<='9999-12-31 23:59:59.999999+00'::timestamptz),
    CONSTRAINT chat_turns_check2 CHECK((phase=1 AND finished_at IS NULL) OR (phase<>1 AND finished_at IS NOT NULL AND isfinite(finished_at)
        AND finished_at>=created_at AND finished_at<='9999-12-31 23:59:59.999999+00'::timestamptz))
);
-- One active turn per conversation, including an uncertain abandoned dispatch.
CREATE UNIQUE INDEX chat_one_active_turn ON weft.chat_turns(organization_id,creator_id,conversation_id) WHERE phase=1;

CREATE TABLE weft.chat_rounds (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, conversation_id uuid NOT NULL, turn_id uuid NOT NULL,
    round_id uuid NOT NULL CHECK(round_id<>'00000000-0000-0000-0000-000000000000'), ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 4),
    fence uuid NOT NULL CHECK(fence<>'00000000-0000-0000-0000-000000000000'), instance_id uuid NOT NULL CHECK(instance_id<>'00000000-0000-0000-0000-000000000000'),
    exact_request text COLLATE "C" NOT NULL CHECK(octet_length(exact_request) BETWEEN 1 AND 2097152 AND btrim(exact_request)<>''),
    result_metadata text COLLATE "C" CHECK(result_metadata IS NULL OR octet_length(result_metadata) BETWEEN 1 AND 2048),
    outcome integer NOT NULL DEFAULT 1 CHECK(outcome BETWEEN 1 AND 4), reserved_at timestamptz NOT NULL DEFAULT statement_timestamp(), finished_at timestamptz,
    PRIMARY KEY(organization_id,creator_id,conversation_id,turn_id,round_id),
    CONSTRAINT chat_rounds_identity_key UNIQUE(organization_id,creator_id,round_id),
    CONSTRAINT chat_rounds_ordinal_key UNIQUE(organization_id,creator_id,conversation_id,turn_id,ordinal),
    CONSTRAINT chat_rounds_turn_fkey FOREIGN KEY(organization_id,creator_id,conversation_id,turn_id) REFERENCES weft.chat_turns ON DELETE CASCADE ON UPDATE RESTRICT,
    CONSTRAINT chat_rounds_reserved_at_check CHECK(isfinite(reserved_at) AND reserved_at>'0001-01-01+00'::timestamptz AND reserved_at<='9999-12-31 23:59:59.999999+00'::timestamptz),
    CONSTRAINT chat_rounds_check CHECK((outcome=1 AND finished_at IS NULL) OR (outcome<>1 AND finished_at IS NOT NULL AND isfinite(finished_at)
        AND finished_at>=reserved_at AND finished_at<='9999-12-31 23:59:59.999999+00'::timestamptz))
);

CREATE TABLE weft.chat_events (
    organization_id uuid NOT NULL, creator_id uuid NOT NULL, conversation_id uuid NOT NULL, turn_id uuid NOT NULL,
    sequence integer NOT NULL CHECK(sequence BETWEEN 1 AND 4096), event_id uuid NOT NULL CHECK(event_id<>'00000000-0000-0000-0000-000000000000'),
    kind integer NOT NULL CHECK(kind BETWEEN 1 AND 12), private_text text COLLATE "C" NOT NULL, round_id uuid, dispatch_fence uuid,
    metadata_json text COLLATE "C" CHECK(metadata_json IS NULL OR octet_length(metadata_json) BETWEEN 1 AND 8192),
    PRIMARY KEY(organization_id,creator_id,conversation_id,turn_id,sequence),
    CONSTRAINT chat_events_event_id_key UNIQUE(organization_id,creator_id,conversation_id,turn_id,event_id),
    CONSTRAINT chat_events_turn_fkey FOREIGN KEY(organization_id,creator_id,conversation_id,turn_id) REFERENCES weft.chat_turns ON DELETE CASCADE ON UPDATE RESTRICT,
    CONSTRAINT chat_events_round_fkey FOREIGN KEY(organization_id,creator_id,conversation_id,turn_id,round_id) REFERENCES weft.chat_rounds ON DELETE CASCADE ON UPDATE RESTRICT,
    CONSTRAINT chat_events_check CHECK((kind IN (2,3,4) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NULL AND octet_length(private_text) BETWEEN 1 AND 8192 AND btrim(private_text)<>'')
        OR (kind IN (9,10,11,12) AND round_id IS NOT NULL AND dispatch_fence IS NOT NULL AND metadata_json IS NOT NULL AND private_text='')
        OR (kind=1 AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND metadata_json IS NULL)
        OR (kind IN (5,6,7,8) AND round_id IS NULL AND dispatch_fence IS NULL AND private_text='' AND (metadata_json IS NULL OR octet_length(metadata_json)<=2048)))
);

DO $chat_rls$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['chat_conversations','chat_turns','chat_rounds','chat_events','chat_identities']::text[] LOOP
        EXECUTE format('ALTER TABLE weft.%I ENABLE ROW LEVEL SECURITY',table_name);
        EXECUTE format('CREATE POLICY creator_scope ON weft.%I USING (organization_id=NULLIF(current_setting(''weft.organization_id'',true),'''')::uuid AND creator_id=NULLIF(current_setting(''weft.chat_principal_id'',true),'''')::uuid) WITH CHECK (organization_id=NULLIF(current_setting(''weft.organization_id'',true),'''')::uuid AND creator_id=NULLIF(current_setting(''weft.chat_principal_id'',true),'''')::uuid)',table_name);
        EXECUTE format('REVOKE ALL ON weft.%I FROM PUBLIC',table_name);
    END LOOP;
END;
$chat_rls$;

CREATE FUNCTION weft.guard_chat_conversation() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $guard_chat_conversation$
DECLARE c weft.chat_conversations%ROWTYPE; n integer;
BEGIN
    IF TG_OP='UPDATE' OR TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'Immutable chat context.' USING ERRCODE='23514'; END IF;
    IF TG_OP='DELETE' THEN c:=OLD; ELSE c:=NEW; END IF;
    IF weft.chat_authorized(c.organization_id,c.project_id,c.creator_id) IS NOT TRUE
        AND NOT (TG_OP='DELETE' AND weft.chat_is_lifecycle()) THEN
        RAISE EXCEPTION 'Chat authority unavailable.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='DELETE' THEN
        IF statement_timestamp()<c.retain_until OR EXISTS(SELECT 1 FROM weft.chat_turns t
            WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id AND t.conversation_id=c.conversation_id AND t.phase=1) THEN
            RAISE EXCEPTION 'Chat retention or active turn prevents deletion.' USING ERRCODE='23514';
        END IF;
        IF NOT EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=c.organization_id AND i.creator_id=c.creator_id AND i.identity_kind=1 AND i.identity_id=c.conversation_id AND i.erased_at IS NOT NULL)
            OR EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=c.organization_id AND i.creator_id=c.creator_id AND i.conversation_id=c.conversation_id AND i.erased_at IS NULL) THEN
            RAISE EXCEPTION 'Chat erasure requires retained identity protection.' USING ERRCODE='23514';
        END IF;
        RETURN OLD;
    END IF;
    IF NEW.created_at<>statement_timestamp() OR NEW.retain_until<=statement_timestamp() THEN
        RAISE EXCEPTION 'Invalid chat retention.' USING ERRCODE='23514';
    END IF;
    IF NOT EXISTS(SELECT 1 FROM weft.chat_identities i WHERE i.organization_id=NEW.organization_id AND i.creator_id=NEW.creator_id AND i.identity_kind=1 AND i.identity_id=NEW.conversation_id AND i.conversation_id=NEW.conversation_id AND i.erased_at IS NULL) THEN
        RAISE EXCEPTION 'Chat identity unavailable.' USING ERRCODE='23514';
    END IF;
    SELECT count(*) INTO n FROM weft.revision_derivations d JOIN weft.revision_derivation_attempts a
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

CREATE FUNCTION weft.chat_metadata_valid(kind integer, value text, expected_round uuid) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $chat_metadata_valid$
DECLARE j jsonb; keys text[]; k text; n numeric; usage jsonb; failure jsonb;
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
REVOKE ALL ON FUNCTION weft.chat_metadata_valid(integer,text,uuid) FROM PUBLIC;

CREATE FUNCTION weft.guard_chat_identity() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $guard_chat_identity$
DECLARE c weft.chat_conversations%ROWTYPE;
BEGIN
    IF TG_OP IN ('DELETE','TRUNCATE') THEN RAISE EXCEPTION 'Chat replay protection cannot be removed.' USING ERRCODE='23514'; END IF;
    IF TG_OP='INSERT' THEN
        IF NEW.erased_at IS NOT NULL OR NEW.organization_id IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
            OR NEW.creator_id IS DISTINCT FROM NULLIF(current_setting('weft.chat_principal_id',true),'')::uuid THEN
            RAISE EXCEPTION 'Chat identity reservation scope refused.' USING ERRCODE='23514';
        END IF;
        RETURN NEW;
    END IF;
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=OLD.organization_id AND x.creator_id=OLD.creator_id AND x.conversation_id=OLD.conversation_id;
    IF c.conversation_id IS NULL OR (weft.chat_authorized(c.organization_id,c.project_id,c.creator_id) IS NOT TRUE AND NOT weft.chat_is_lifecycle())
        OR c.retain_until>statement_timestamp() OR OLD.erased_at IS NOT NULL OR NEW.erased_at IS DISTINCT FROM statement_timestamp()
        OR EXISTS(SELECT 1 FROM weft.chat_turns t WHERE t.organization_id=OLD.organization_id AND t.creator_id=OLD.creator_id AND t.conversation_id=OLD.conversation_id AND t.phase=1)
        OR (to_jsonb(NEW)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before']) IS DISTINCT FROM
            (to_jsonb(OLD)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before']) THEN
        RAISE EXCEPTION 'Chat erasure identity transition refused.' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$guard_chat_identity$;
REVOKE ALL ON FUNCTION weft.guard_chat_identity() FROM PUBLIC;
CREATE TRIGGER chat_identity_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.chat_identities FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_identity();
CREATE TRIGGER chat_identity_truncate BEFORE TRUNCATE ON weft.chat_identities FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_identity();
CREATE FUNCTION weft.check_chat_identity_erasure() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
SET weft.organization_id='' SET weft.chat_principal_id=''
AS $check_chat_identity_erasure$
BEGIN
    PERFORM pg_catalog.set_config('weft.organization_id',NEW.organization_id::text,true),
        pg_catalog.set_config('weft.chat_principal_id',NEW.creator_id::text,true);
    IF NEW.erased_at IS NOT NULL AND EXISTS(SELECT 1 FROM weft.chat_conversations c
        WHERE c.organization_id=NEW.organization_id AND c.creator_id=NEW.creator_id AND c.conversation_id=NEW.conversation_id) THEN
        RAISE EXCEPTION 'Chat erasure did not remove content.' USING ERRCODE='23514';
    END IF;
    RETURN NULL;
END;
$check_chat_identity_erasure$;
REVOKE ALL ON FUNCTION weft.check_chat_identity_erasure() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER chat_identity_erasure AFTER UPDATE ON weft.chat_identities DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION weft.check_chat_identity_erasure();

CREATE FUNCTION weft.guard_chat_child() RETURNS trigger
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

-- Multi-row transitions must commit exactly the counters, ordered immutable events and round facts.
CREATE FUNCTION weft.check_chat_counters() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
SET weft.organization_id='' SET weft.chat_principal_id=''
AS $check_chat_counters$
DECLARE t weft.chat_turns%ROWTYPE; n bigint; bytes bigint; max_seq integer; rounds bigint; completed_rounds bigint; accounted_rounds bigint;
BEGIN
    -- Bind every read to the actual queued tuple, independent of final caller scope.
    -- Function-local frames restore changed/empty/malformed incoming GUCs on all exits.
    PERFORM pg_catalog.set_config('weft.organization_id',NEW.organization_id::text,true),
        pg_catalog.set_config('weft.chat_principal_id',NEW.creator_id::text,true);
    SELECT * INTO t FROM weft.chat_turns x WHERE x.organization_id=NEW.organization_id AND x.creator_id=NEW.creator_id AND x.conversation_id=NEW.conversation_id AND x.turn_id=NEW.turn_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT count(*),coalesce(sum(CASE WHEN kind IN (5,6,7,8) THEN 0 ELSE octet_length(private_text)+coalesce(octet_length(metadata_json),0) END),0),max(sequence) INTO n,bytes,max_seq FROM weft.chat_events e
        WHERE e.organization_id=t.organization_id AND e.creator_id=t.creator_id AND e.conversation_id=t.conversation_id AND e.turn_id=t.turn_id;
    SELECT count(*),count(*) FILTER(WHERE outcome<>1),count(*) FILTER(WHERE result_metadata IS NOT NULL)
        INTO rounds,completed_rounds,accounted_rounds FROM weft.chat_rounds r
        WHERE r.organization_id=t.organization_id AND r.creator_id=t.creator_id AND r.conversation_id=t.conversation_id AND r.turn_id=t.turn_id;
    IF n<>t.event_count OR max_seq<>t.event_count OR bytes<>t.event_bytes OR rounds<>t.round_count
        -- A typed completion publishes one accounting event in the SAME version step.
        OR t.version<>n+rounds+completed_rounds-accounted_rounds
        OR accounted_rounds<>(SELECT count(*) FROM weft.chat_events e WHERE e.organization_id=t.organization_id AND e.creator_id=t.creator_id
            AND e.conversation_id=t.conversation_id AND e.turn_id=t.turn_id AND e.kind=12)
        OR EXISTS(SELECT 1 FROM weft.chat_events e WHERE e.organization_id=t.organization_id AND e.creator_id=t.creator_id
            AND e.conversation_id=t.conversation_id AND e.turn_id=t.turn_id AND e.kind=12 AND NOT EXISTS(
                SELECT 1 FROM weft.chat_rounds r WHERE r.organization_id=t.organization_id AND r.creator_id=t.creator_id
                AND r.conversation_id=t.conversation_id AND r.turn_id=t.turn_id AND r.round_id=e.round_id AND r.fence=e.dispatch_fence
                AND r.outcome<>1 AND r.result_metadata=e.metadata_json))
        OR NOT EXISTS(SELECT 1 FROM weft.chat_events e WHERE e.organization_id=t.organization_id AND e.creator_id=t.creator_id AND e.conversation_id=t.conversation_id AND e.turn_id=t.turn_id AND e.sequence=1 AND e.kind=1)
        OR (t.phase<>1 AND NOT EXISTS(SELECT 1 FROM weft.chat_events e WHERE e.organization_id=t.organization_id AND e.creator_id=t.creator_id AND e.conversation_id=t.conversation_id AND e.turn_id=t.turn_id AND e.sequence=t.event_count AND e.kind=t.phase+3)) THEN
        RAISE EXCEPTION 'Inconsistent chat ledger.' USING ERRCODE='23514';
    END IF;
    RETURN NULL;
END;
$check_chat_counters$;

REVOKE ALL ON FUNCTION weft.guard_chat_conversation(),weft.guard_chat_child(),weft.check_chat_counters() FROM PUBLIC;
CREATE TRIGGER chat_conversation_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.chat_conversations FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_conversation();
CREATE TRIGGER chat_conversation_truncate BEFORE TRUNCATE ON weft.chat_conversations FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_conversation();
DO $chat_triggers$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['chat_turns','chat_rounds','chat_events']::text[] LOOP
        EXECUTE format('CREATE TRIGGER chat_child_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.%I FOR EACH ROW EXECUTE FUNCTION weft.guard_chat_child()',table_name);
        EXECUTE format('CREATE TRIGGER chat_child_truncate BEFORE TRUNCATE ON weft.%I FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_chat_child()',table_name);
        EXECUTE format('CREATE CONSTRAINT TRIGGER chat_counters AFTER INSERT OR UPDATE ON weft.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION weft.check_chat_counters()',table_name);
    END LOOP;
END;
$chat_triggers$;

-- Reserve only non-content IDs. INSERT is encapsulated so a writer cannot allocate
-- identities without exact current project authority or spoof a lifecycle bypass.
CREATE FUNCTION weft.chat_reserve_identity(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_kind integer,p_identity uuid,p_turn uuid)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_reserve_identity$
DECLARE c weft.chat_conversations%ROWTYPE; i weft.chat_identities%ROWTYPE; inserted integer;
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>'weft_chat_writer' THEN
        RAISE EXCEPTION 'Chat writer identity routine required.' USING ERRCODE='42501';
    END IF;
    IF weft.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation;
    IF (c.conversation_id IS NULL AND p_kind<>1) OR (c.conversation_id IS NOT NULL AND c.project_id<>p_project) THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    -- The subtransaction undoes a candidate if current authority disappears during
    -- unique arbitration, without acknowledging an orphan reservation.
    BEGIN
        INSERT INTO weft.chat_identities(organization_id,creator_id,identity_kind,identity_id,conversation_id,turn_id)
            VALUES(p_org,p_creator,p_kind,p_identity,p_conversation,p_turn)
            ON CONFLICT(organization_id,creator_id,identity_kind,identity_id) DO NOTHING;
        GET DIAGNOSTICS inserted=ROW_COUNT;
        IF weft.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE THEN
            RAISE EXCEPTION 'Chat authority changed during identity arbitration.' USING ERRCODE='PCA01';
        END IF;
        IF inserted=1 THEN RETURN 'Applied'; END IF;
        SELECT * INTO i FROM weft.chat_identities x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.identity_kind=p_kind AND x.identity_id=p_identity;
        IF NOT FOUND THEN RAISE EXCEPTION 'Chat identity facts unavailable.' USING ERRCODE='23514'; END IF;
        IF i.erased_at IS NOT NULL THEN RETURN 'IdentityRetired'; END IF;
        IF i.conversation_id<>p_conversation OR i.turn_id IS DISTINCT FROM p_turn THEN RETURN 'Conflict'; END IF;
        RETURN 'Replayed';
    EXCEPTION WHEN SQLSTATE 'PCA01' THEN RETURN 'NotAuthorizedOrNotFound';
    END;
END;
$chat_reserve_identity$;
REVOKE ALL ON FUNCTION weft.chat_reserve_identity(uuid,uuid,uuid,uuid,integer,uuid,uuid) FROM PUBLIC;

-- Exact-target trusted lifecycle operations expose only closed categorical outcomes.
-- The lifecycle login receives no table SELECT, owner role membership or admin content port.
-- This non-content capability holds the same fixed relation locks as owner ports,
-- without giving lifecycle a listing/read capability on those relations.
CREATE FUNCTION weft.chat_schema_lock() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_schema_lock$
BEGIN
    IF current_user<>'weft_migrator' OR session_user NOT IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle') THEN
        RAISE EXCEPTION 'Restricted chat profile required.' USING ERRCODE='42501';
    END IF;
    LOCK TABLE ONLY weft.schema_migrations,ONLY weft.principals,ONLY weft.organization_memberships,ONLY weft.project_memberships,
        ONLY weft.chat_conversations,ONLY weft.chat_turns,ONLY weft.chat_rounds,ONLY weft.chat_events,ONLY weft.chat_identities,
        ONLY weft.revision_derivations,ONLY weft.revision_derivation_attempts IN ACCESS SHARE MODE;
END;
$chat_schema_lock$;
REVOKE ALL ON FUNCTION weft.chat_schema_lock() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.chat_schema_lock() TO weft_chat_reader,weft_chat_writer,weft_chat_lifecycle;
GRANT SELECT(version,checksum) ON weft.schema_migrations TO weft_chat_lifecycle;

CREATE FUNCTION weft.chat_lifecycle_observe(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_turn uuid)
RETURNS TABLE(phase integer,version bigint)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $chat_lifecycle_observe$
BEGIN
    IF NOT weft.chat_is_lifecycle() THEN RAISE EXCEPTION 'Trusted chat lifecycle profile required.' USING ERRCODE='42501'; END IF;
    RETURN QUERY SELECT t.phase,t.version FROM weft.chat_turns t JOIN weft.chat_conversations c USING(organization_id,creator_id,conversation_id)
        WHERE c.organization_id=p_org AND c.creator_id=p_creator AND c.conversation_id=p_conversation
        AND c.project_id=p_project AND c.mapping_id=p_mapping AND c.revision_id=p_revision AND t.turn_id=p_turn;
END;
$chat_lifecycle_observe$;
CREATE FUNCTION weft.chat_lifecycle_close(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,
    p_turn uuid,p_expected bigint,p_phase integer,p_event uuid,p_metadata text) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
SET weft.organization_id='' SET weft.chat_principal_id=''
AS $chat_lifecycle_close$
DECLARE c weft.chat_conversations%ROWTYPE; t weft.chat_turns%ROWTYPE; metadata text; last_round uuid;
BEGIN
    IF NOT weft.chat_is_lifecycle() THEN RAISE EXCEPTION 'Trusted chat lifecycle profile required.' USING ERRCODE='42501'; END IF;
    PERFORM pg_catalog.set_config('weft.organization_id',p_org::text,true),pg_catalog.set_config('weft.chat_principal_id',p_creator::text,true);
    SET CONSTRAINTS weft.chat_counters DEFERRED;
    IF p_phase NOT IN (4,5) OR p_expected<1 OR p_event IS NULL OR p_event='00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'Invalid chat lifecycle command.' USING ERRCODE='23514';
    END IF;
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation
        AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.revision_id=p_revision FOR UPDATE;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    SELECT * INTO t FROM weft.chat_turns x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation AND x.turn_id=p_turn;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF t.version<>p_expected THEN RETURN 'Conflict'; END IF;
    IF t.phase<>1 THEN RETURN 'InvalidState'; END IF;
    IF t.version=9223372036854775807 OR t.event_count>=t.max_events THEN RETURN 'LimitReached'; END IF;
    SELECT r.result_metadata,r.round_id INTO metadata,last_round FROM weft.chat_rounds r WHERE r.organization_id=p_org AND r.creator_id=p_creator
        AND r.conversation_id=p_conversation AND r.turn_id=p_turn ORDER BY ordinal DESC LIMIT 1;
    IF p_metadata IS NOT NULL THEN
        IF last_round IS NULL OR octet_length(p_metadata)>2048 OR weft.chat_metadata_valid(p_phase+3,p_metadata,last_round) IS NOT TRUE THEN
            RAISE EXCEPTION 'Invalid lifecycle accounting metadata.' USING ERRCODE='23514';
        END IF;
        IF metadata IS NOT NULL AND p_metadata<>metadata THEN RETURN 'Conflict'; END IF;
        metadata:=p_metadata;
    END IF;
    UPDATE weft.chat_turns SET version=version+1,phase=p_phase,finished_at=statement_timestamp(),event_count=event_count+1
        WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation AND turn_id=p_turn AND version=p_expected AND phase=1;
    IF NOT FOUND THEN RETURN 'Conflict'; END IF;
    INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,metadata_json)
        VALUES(p_org,p_creator,p_conversation,p_turn,t.event_count+1,p_event,p_phase+3,'',metadata);
    -- Drain only chat checks while still inside the trusted routine. The lifecycle
    -- login gains no content SELECT and the check itself remains SECURITY INVOKER.
    SET CONSTRAINTS weft.chat_counters IMMEDIATE;
    SET CONSTRAINTS weft.chat_counters DEFERRED;
    RETURN 'Applied';
END;
$chat_lifecycle_close$;

CREATE FUNCTION weft.chat_erase_expired(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,
    p_policy text,p_policy_version integer,p_retirement_not_before timestamptz) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $chat_erase_expired$
DECLARE c weft.chat_conversations%ROWTYPE;
BEGIN
    SELECT * INTO c FROM weft.chat_conversations x WHERE x.organization_id=p_org AND x.creator_id=p_creator AND x.conversation_id=p_conversation
        AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.revision_id=p_revision FOR UPDATE;
    IF NOT FOUND THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF weft.chat_authorized(p_org,p_project,p_creator) IS NOT TRUE AND NOT weft.chat_is_lifecycle() THEN RETURN 'NotAuthorizedOrNotFound'; END IF;
    IF c.retain_until>statement_timestamp() OR EXISTS(SELECT 1 FROM weft.chat_turns t WHERE t.organization_id=p_org AND t.creator_id=p_creator AND t.conversation_id=p_conversation AND t.phase=1) THEN RETURN 'InvalidState'; END IF;
    IF p_policy IS NULL OR p_policy !~ '^[a-z0-9][a-z0-9._-]{0,63}$' OR p_policy_version IS NULL OR p_policy_version<1
        OR p_retirement_not_before IS NULL OR NOT isfinite(p_retirement_not_before) OR p_retirement_not_before<=statement_timestamp()
        OR p_retirement_not_before>'9999-12-31 23:59:59.999999+00'::timestamptz THEN
        RAISE EXCEPTION 'Explicit chat erasure policy required.' USING ERRCODE='23514';
    END IF;
    UPDATE weft.chat_identities SET erased_at=statement_timestamp(),retirement_policy_id=p_policy,
        retirement_policy_version=p_policy_version,retirement_not_before=p_retirement_not_before
        WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation AND erased_at IS NULL;
    DELETE FROM weft.chat_conversations WHERE organization_id=p_org AND creator_id=p_creator AND conversation_id=p_conversation;
    RETURN 'Applied';
END;
$chat_erase_expired$;

CREATE FUNCTION weft.chat_lifecycle_erase(p_org uuid,p_creator uuid,p_conversation uuid,p_project uuid,p_mapping uuid,p_revision uuid,
    p_policy text,p_policy_version integer,p_retirement_not_before timestamptz) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
SET weft.organization_id='' SET weft.chat_principal_id=''
AS $chat_lifecycle_erase$
DECLARE result text;
BEGIN
    IF NOT weft.chat_is_lifecycle() THEN RAISE EXCEPTION 'Trusted chat lifecycle profile required.' USING ERRCODE='42501'; END IF;
    PERFORM pg_catalog.set_config('weft.organization_id',p_org::text,true),pg_catalog.set_config('weft.chat_principal_id',p_creator::text,true);
    SET CONSTRAINTS weft.chat_counters,weft.chat_identity_erasure DEFERRED;
    result:=weft.chat_erase_expired(p_org,p_creator,p_conversation,p_project,p_mapping,p_revision,p_policy,p_policy_version,p_retirement_not_before);
    SET CONSTRAINTS weft.chat_counters,weft.chat_identity_erasure IMMEDIATE;
    SET CONSTRAINTS weft.chat_counters,weft.chat_identity_erasure DEFERRED;
    RETURN result;
END;
$chat_lifecycle_erase$;
REVOKE ALL ON FUNCTION weft.chat_lifecycle_observe(uuid,uuid,uuid,uuid,uuid,uuid,uuid),weft.chat_lifecycle_close(uuid,uuid,uuid,uuid,uuid,uuid,uuid,bigint,integer,uuid,text),
    weft.chat_erase_expired(uuid,uuid,uuid,uuid,uuid,uuid,text,integer,timestamptz),
    weft.chat_lifecycle_erase(uuid,uuid,uuid,uuid,uuid,uuid,text,integer,timestamptz) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_chat_lifecycle;
GRANT EXECUTE ON FUNCTION weft.chat_lifecycle_observe(uuid,uuid,uuid,uuid,uuid,uuid,uuid),weft.chat_lifecycle_close(uuid,uuid,uuid,uuid,uuid,uuid,uuid,bigint,integer,uuid,text),
    weft.chat_lifecycle_erase(uuid,uuid,uuid,uuid,uuid,uuid,text,integer,timestamptz) TO weft_chat_lifecycle;

GRANT USAGE ON SCHEMA weft TO weft_chat_reader,weft_chat_writer;
-- History is non-content metadata; no journal mutation or table-wide SELECT is granted.
GRANT SELECT(version,checksum) ON weft.schema_migrations TO weft_chat_reader,weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_authorized(uuid,uuid,uuid) TO weft_chat_reader,weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_is_lifecycle(),weft.chat_metadata_valid(integer,text,uuid) TO weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_erase_expired(uuid,uuid,uuid,uuid,uuid,uuid,text,integer,timestamptz) TO weft_chat_writer;
GRANT EXECUTE ON FUNCTION weft.chat_reserve_identity(uuid,uuid,uuid,uuid,integer,uuid,uuid) TO weft_chat_writer;
GRANT SELECT(organization_id,creator_id,identity_kind,identity_id,conversation_id,turn_id,erased_at,retirement_policy_id,retirement_policy_version,retirement_not_before)
    ON weft.chat_identities TO weft_chat_writer;
GRANT UPDATE(erased_at,retirement_policy_id,retirement_policy_version,retirement_not_before) ON weft.chat_identities TO weft_chat_writer;
GRANT SELECT(id,active) ON weft.principals TO weft_chat_reader,weft_chat_writer;
GRANT SELECT(organization_id,principal_id,active) ON weft.organization_memberships TO weft_chat_reader,weft_chat_writer;
GRANT SELECT(organization_id,project_id,principal_id,active,role) ON weft.project_memberships TO weft_chat_reader,weft_chat_writer;
GRANT SELECT(organization_id,creator_id,conversation_id,project_id,mapping_id,revision_id,projection_entry_id,projection_id,
    projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest,created_at,retain_until)
    ON weft.chat_conversations TO weft_chat_reader,weft_chat_writer;
GRANT INSERT(organization_id,creator_id,conversation_id,project_id,mapping_id,revision_id,projection_entry_id,projection_id,
    projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest,retain_until)
    ON weft.chat_conversations TO weft_chat_writer;
GRANT SELECT(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,profile_id,profile_version,provider_id,model_id,
    metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline,created_at,
    phase,version,event_count,event_bytes,round_count,finished_at) ON weft.chat_turns TO weft_chat_reader,weft_chat_writer;
GRANT INSERT(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,profile_id,profile_version,provider_id,model_id,
    metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline)
    ON weft.chat_turns TO weft_chat_writer;
GRANT SELECT(organization_id,creator_id,conversation_id,turn_id,round_id,ordinal,fence,instance_id,exact_request,outcome,reserved_at,finished_at,result_metadata)
    ON weft.chat_rounds TO weft_chat_reader,weft_chat_writer;
GRANT INSERT(organization_id,creator_id,conversation_id,turn_id,round_id,ordinal,fence,instance_id,exact_request)
    ON weft.chat_rounds TO weft_chat_writer;
GRANT SELECT(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
    ON weft.chat_events TO weft_chat_reader,weft_chat_writer;
GRANT INSERT(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
    ON weft.chat_events TO weft_chat_writer;
-- PostgreSQL row locks require one UPDATE column. Its immutable trigger rejects actual edits.
GRANT UPDATE(retain_until) ON weft.chat_conversations TO weft_chat_writer;
GRANT DELETE ON weft.chat_conversations TO weft_chat_writer;
GRANT UPDATE(phase,version,event_count,event_bytes,round_count,finished_at) ON weft.chat_turns TO weft_chat_writer;
GRANT UPDATE(outcome,finished_at,result_metadata) ON weft.chat_rounds TO weft_chat_writer;
GRANT SELECT(organization_id,project_id,mapping_id,revision_id,cache_entry_id,operation_kind,key_fingerprint)
    ON weft.revision_derivations TO weft_chat_writer;
GRANT SELECT(organization_id,project_id,mapping_id,revision_id,cache_entry_id,derivation_id,result_usable,completion_kind,derived_digest)
    ON weft.revision_derivation_attempts TO weft_chat_writer;
