-- Owned interactive preview runs of exact revisions. Predecessor facts, shapes and grants are unchanged.
-- Both runtime profiles are provisioned separately and must initially be CONNECT-only.
DO $weft_require_preview_roles$
DECLARE
    application_role record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user
        OR current_setting('server_encoding') <> 'UTF8'
        OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Preview runs require the accepted migration environment.';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('weft_preview_reader','weft_preview_writer')) <> 2 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Preview runs require both dedicated restricted logins.';
    END IF;
    FOR application_role IN SELECT * FROM pg_catalog.pg_roles WHERE rolname IN ('weft_preview_reader','weft_preview_writer') LOOP
        IF NOT application_role.rolcanlogin OR application_role.rolinherit OR application_role.rolsuper
            OR application_role.rolbypassrls OR application_role.rolcreatedb OR application_role.rolcreaterole
            OR application_role.rolreplication
            OR NOT pg_catalog.has_database_privilege(application_role.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(application_role.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR pg_catalog.has_parameter_privilege(application_role.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = application_role.oid OR m.roleid = application_role.oid)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%'
                       AND n.nspname NOT LIKE 'pg_toast_temp_%'
                       AND (n.nspowner = application_role.oid OR pg_catalog.has_schema_privilege(application_role.oid,n.oid,'CREATE')))
            OR pg_catalog.has_schema_privilege(application_role.oid,'weft','USAGE')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid = r.relnamespace
                       WHERE n.nspname = 'weft' AND r.relkind IN ('r','p','v','m','f')
                       AND (pg_catalog.has_table_privilege(application_role.oid,r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR pg_catalog.has_any_column_privilege(application_role.oid,r.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid = f.pronamespace
                       WHERE n.nspname = 'weft' AND pg_catalog.has_function_privilege(application_role.oid,f.oid,'EXECUTE'))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                       WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft'))
                       AND a.grantee IN (0,application_role.oid)) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Preview logins require explicit CONNECT-only provisioning.';
        END IF;
    END LOOP;
END;
$weft_require_preview_roles$;

-- States: 1 queued, 2 running, 3 completed, 4 canceled. A completed run always has an
-- outcome; only a succeeded outcome has output documents.
CREATE TABLE weft.preview_runs (
    organization_id uuid NOT NULL,
    run_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    submitter_id uuid NOT NULL,
    request_key uuid NOT NULL,
    request_document text COLLATE "C" NOT NULL,
    request_fingerprint text COLLATE "C" NOT NULL,
    current_date_time text COLLATE "C" NOT NULL,
    native_object_id uuid NOT NULL,
    native_digest text COLLATE "C" NOT NULL,
    native_byte_count bigint NOT NULL,
    input_count integer NOT NULL,
    input_bytes bigint NOT NULL,
    max_attempts integer NOT NULL,
    state integer NOT NULL DEFAULT 1,
    attempt integer NOT NULL DEFAULT 0,
    cancel_requested boolean NOT NULL DEFAULT false,
    lease_token uuid,
    lease_owner uuid,
    lease_expires_at timestamptz,
    not_before timestamptz NOT NULL DEFAULT statement_timestamp(),
    outcome text COLLATE "C",
    failure_code text COLLATE "C",
    failure_message text COLLATE "C",
    failure_message_truncated boolean,
    failure_subject text COLLATE "C",
    failure_subject_truncated boolean,
    provenance_confirmed boolean,
    engine_revision text COLLATE "C",
    backend_build text COLLATE "C",
    adapter_build text COLLATE "C",
    profile text COLLATE "C",
    records_written bigint,
    output_count integer,
    output_bytes bigint,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    started_at timestamptz,
    finished_at timestamptz,
    expires_at timestamptz NOT NULL,
    CONSTRAINT preview_runs_pkey PRIMARY KEY (organization_id,run_id),
    CONSTRAINT preview_runs_request_key UNIQUE (organization_id,submitter_id,request_key),
    CONSTRAINT preview_runs_revision_fkey FOREIGN KEY (organization_id,project_id,mapping_id,revision_id)
        REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT preview_runs_submitter_fkey FOREIGN KEY (organization_id,project_id,submitter_id)
        REFERENCES weft.project_memberships(organization_id,project_id,principal_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT preview_runs_ids_check CHECK (run_id <> '00000000-0000-0000-0000-000000000000'
        AND request_key <> '00000000-0000-0000-0000-000000000000' AND native_object_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT preview_runs_request_check CHECK (octet_length(request_document) BETWEEN 2 AND 65536
        AND request_fingerprint = encode(sha256(convert_to(request_document,'UTF8')),'hex')),
    CONSTRAINT preview_runs_clock_check CHECK (current_date_time ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,9})?(Z|[+-][0-9]{2}:[0-9]{2})$'),
    CONSTRAINT preview_runs_native_check CHECK (native_digest ~ '^[0-9a-f]{64}$' AND native_byte_count BETWEEN 1 AND 4194304),
    CONSTRAINT preview_runs_inputs_check CHECK (input_count BETWEEN 1 AND 16 AND input_bytes BETWEEN input_count AND 8388608),
    CONSTRAINT preview_runs_attempt_check CHECK (max_attempts BETWEEN 1 AND 8 AND attempt BETWEEN 0 AND max_attempts),
    CONSTRAINT preview_runs_state_check CHECK (state BETWEEN 1 AND 4),
    CONSTRAINT preview_runs_lease_check CHECK ((state = 2) = (lease_token IS NOT NULL)
        AND (lease_token IS NULL) = (lease_owner IS NULL) AND (lease_token IS NULL) = (lease_expires_at IS NULL)
        AND (state <> 2 OR attempt > 0)),
    CONSTRAINT preview_runs_terminal_check CHECK ((state IN (3,4)) = (outcome IS NOT NULL)
        AND (state IN (3,4)) = (finished_at IS NOT NULL) AND (state = 4) = (outcome IS NOT DISTINCT FROM 'canceled')),
    CONSTRAINT preview_runs_outcome_check CHECK (outcome IS NULL OR outcome IN ('succeeded','invalidRequest','invalidMapping',
        'unsupportedArtifact','unsupportedCapability','missingInput','invalidInput','invalidParameter','executionFailed',
        'resourceLimit','protocolFailure','backendUnavailable','backendFailure','timedOut','canceled','isolationFailure',
        'authorityRevoked','attemptsExhausted')),
    CONSTRAINT preview_runs_failure_check CHECK ((failure_code IS NULL) = (failure_message IS NULL)
        AND (failure_code IS NULL) = (failure_message_truncated IS NULL)
        AND (failure_subject IS NULL OR failure_code IS NOT NULL) AND (failure_subject IS NULL) = (failure_subject_truncated IS NULL)
        AND (failure_code IS NULL OR (octet_length(failure_code) BETWEEN 1 AND 128 AND octet_length(failure_message) <= 2048
            AND (failure_subject IS NULL OR octet_length(failure_subject) <= 2048)))
        AND (outcome IS DISTINCT FROM 'succeeded' OR failure_code IS NULL)),
    CONSTRAINT preview_runs_provenance_check CHECK ((provenance_confirmed IS NULL) = (engine_revision IS NULL)
        AND (provenance_confirmed IS NULL) = (backend_build IS NULL) AND (provenance_confirmed IS NULL) = (adapter_build IS NULL)
        AND (provenance_confirmed IS NULL) = (profile IS NULL) AND (provenance_confirmed IS NULL OR state = 3)
        AND (provenance_confirmed IS NOT NULL OR outcome IS DISTINCT FROM 'succeeded')
        AND (provenance_confirmed IS NULL OR (octet_length(engine_revision) BETWEEN 1 AND 256
            AND octet_length(backend_build) BETWEEN 1 AND 2048 AND octet_length(adapter_build) BETWEEN 1 AND 256
            AND octet_length(profile) BETWEEN 1 AND 256))),
    CONSTRAINT preview_runs_result_check CHECK ((outcome IS NOT DISTINCT FROM 'succeeded') = (output_count IS NOT NULL)
        AND (output_count IS NULL) = (output_bytes IS NULL) AND (output_count IS NULL) = (records_written IS NULL)
        AND (output_count IS NULL OR (output_count BETWEEN 1 AND 64 AND output_bytes BETWEEN 0 AND 8388608 AND records_written >= 0))),
    CONSTRAINT preview_runs_time_check CHECK (isfinite(created_at) AND isfinite(expires_at) AND expires_at > created_at
        AND isfinite(not_before) AND (started_at IS NULL OR (isfinite(started_at) AND started_at >= created_at))
        AND (finished_at IS NULL OR (isfinite(finished_at) AND finished_at >= created_at))
        AND (lease_expires_at IS NULL OR isfinite(lease_expires_at)))
);
CREATE INDEX preview_runs_revision ON weft.preview_runs(organization_id,project_id,mapping_id,revision_id);
CREATE INDEX preview_runs_expiry ON weft.preview_runs(expires_at);

-- Direction 1 holds submitted inputs (ordinal 0 primary, then named sources in request order);
-- direction 2 holds published outputs in primary-then-named target order.
CREATE TABLE weft.preview_documents (
    organization_id uuid NOT NULL,
    run_id uuid NOT NULL,
    direction integer NOT NULL,
    ordinal integer NOT NULL,
    name text COLLATE "C",
    target_name text COLLATE "C",
    target_name_truncated boolean,
    media_type text COLLATE "C",
    records_written bigint,
    digest text COLLATE "C" NOT NULL,
    byte_count integer NOT NULL,
    content bytea NOT NULL,
    CONSTRAINT preview_documents_pkey PRIMARY KEY (organization_id,run_id,direction,ordinal),
    CONSTRAINT preview_documents_run_fkey FOREIGN KEY (organization_id,run_id)
        REFERENCES weft.preview_runs(organization_id,run_id) ON DELETE CASCADE ON UPDATE RESTRICT,
    CONSTRAINT preview_documents_position_check CHECK (direction IN (1,2) AND ordinal BETWEEN 0 AND 63),
    CONSTRAINT preview_documents_content_check CHECK (byte_count = octet_length(content)
        AND digest = encode(sha256(content),'hex') AND byte_count BETWEEN 0 AND 8388608),
    CONSTRAINT preview_documents_kind_check CHECK (
        (direction = 1 AND byte_count > 0 AND target_name IS NULL AND target_name_truncated IS NULL AND media_type IS NULL
            AND records_written IS NULL AND (ordinal = 0) = (name IS NULL)
            AND (name IS NULL OR octet_length(name) BETWEEN 1 AND 256))
        OR (direction = 2 AND name ~ '^(primary-output|named-output:(0|[1-9][0-9]{0,19}))$' AND target_name IS NOT NULL
            AND octet_length(target_name) <= 2048 AND target_name_truncated IS NOT NULL
            AND media_type = 'application/json' AND records_written >= 0))
);

ALTER TABLE weft.preview_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.preview_documents ENABLE ROW LEVEL SECURITY;

-- Fresh authority for every transition: an active principal with active organization and
-- project memberships whose project role may submit previews.
CREATE FUNCTION weft.preview_authorized(p_org uuid, p_project uuid, p_principal uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_authorized$
    SELECT EXISTS (SELECT 1 FROM ONLY weft.principals p
        JOIN ONLY weft.organization_memberships o ON o.principal_id = p.id AND o.organization_id = p_org AND o.active
        JOIN ONLY weft.project_memberships m ON m.principal_id = p.id AND m.organization_id = p_org
            AND m.project_id = p_project AND m.active
        WHERE p.id = p_principal AND p.active AND m.role IN ('runner','publisher','administrator'))
$weft_preview_authorized$;

-- Returns outcome 'created', 'replayed', 'conflict' or 'notAuthorizedOrNotFound' and the run identity.
CREATE FUNCTION weft.preview_submit(p_org uuid, p_project uuid, p_mapping uuid, p_revision uuid, p_actor uuid,
    p_request_key uuid, p_request_document text, p_clock text, p_max_attempts integer, p_retention_seconds integer,
    p_names text[], p_contents bytea[])
RETURNS TABLE(outcome text, run_id uuid)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_submit$
DECLARE
    revision_row weft.mapping_revisions%ROWTYPE;
    existing weft.preview_runs%ROWTYPE;
    created uuid := pg_catalog.gen_random_uuid();
    fingerprint text;
    total bigint := 0;
    item integer;
BEGIN
    IF p_org IS NULL OR p_project IS NULL OR p_mapping IS NULL OR p_revision IS NULL OR p_actor IS NULL
        OR p_request_key IS NULL OR p_request_document IS NULL OR p_clock IS NULL OR p_names IS NULL OR p_contents IS NULL
        OR pg_catalog.cardinality(p_names) <> pg_catalog.cardinality(p_contents)
        OR pg_catalog.cardinality(p_contents) NOT BETWEEN 1 AND 16 OR p_names[1] IS NOT NULL
        OR p_max_attempts NOT BETWEEN 1 AND 8 OR p_retention_seconds NOT BETWEEN 60 AND 604800 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview submission arguments are invalid.';
    END IF;
    IF NOT weft.preview_authorized(p_org,p_project,p_actor) THEN
        RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO revision_row FROM ONLY weft.mapping_revisions r
        WHERE r.organization_id = p_org AND r.project_id = p_project AND r.mapping_id = p_mapping AND r.id = p_revision;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text, NULL::uuid; RETURN;
    END IF;
    fingerprint := encode(sha256(convert_to(p_request_document,'UTF8')),'hex');
    FOR item IN 1 .. pg_catalog.cardinality(p_contents) LOOP
        IF p_contents[item] IS NULL OR (item > 1 AND p_names[item] IS NULL) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview submission arguments are invalid.';
        END IF;
        total := total + octet_length(p_contents[item]);
    END LOOP;

    INSERT INTO weft.preview_runs(organization_id,run_id,project_id,mapping_id,revision_id,submitter_id,request_key,
        request_document,request_fingerprint,current_date_time,native_object_id,native_digest,native_byte_count,
        input_count,input_bytes,max_attempts,expires_at)
    VALUES (p_org,created,p_project,p_mapping,p_revision,p_actor,p_request_key,p_request_document,fingerprint,p_clock,
        revision_row.native_object_id,revision_row.native_digest,revision_row.native_byte_count,
        pg_catalog.cardinality(p_contents),total,p_max_attempts,
        statement_timestamp() + pg_catalog.make_interval(secs => p_retention_seconds))
    ON CONFLICT ON CONSTRAINT preview_runs_request_key DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO existing FROM ONLY weft.preview_runs r
            WHERE r.organization_id = p_org AND r.submitter_id = p_actor AND r.request_key = p_request_key;
        IF existing.project_id = p_project AND existing.mapping_id = p_mapping AND existing.revision_id = p_revision
            AND existing.request_fingerprint = fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text, existing.run_id; RETURN;
        END IF;
        RETURN QUERY SELECT 'conflict'::text, NULL::uuid; RETURN;
    END IF;
    FOR item IN 1 .. pg_catalog.cardinality(p_contents) LOOP
        INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,name,digest,byte_count,content)
        VALUES (p_org,created,1,item - 1,p_names[item],encode(sha256(p_contents[item]),'hex'),
            octet_length(p_contents[item]),p_contents[item]);
    END LOOP;
    RETURN QUERY SELECT 'created'::text, created;
END;
$weft_preview_submit$;

-- Claims a queued run, or a running run whose lease expired. A canceled request, revoked
-- authority or exhausted attempts finish the run here instead of returning a claim.
CREATE FUNCTION weft.preview_claim(p_org uuid, p_run uuid, p_owner uuid, p_lease_seconds integer)
RETURNS TABLE(outcome text, attempt integer, lease_token uuid, lease_expires_at timestamptz, project_id uuid, mapping_id uuid,
    revision_id uuid, submitter_id uuid, request_document text, current_date_time text, native_object_id uuid,
    native_digest text, native_byte_count bigint)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_claim$
DECLARE
    run weft.preview_runs%ROWTYPE;
    token uuid := pg_catalog.gen_random_uuid();
    observed_at timestamptz := statement_timestamp();
BEGIN
    IF p_org IS NULL OR p_run IS NULL OR p_owner IS NULL OR p_lease_seconds NOT BETWEEN 5 AND 600 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview claim arguments are invalid.';
    END IF;
    SELECT * INTO run FROM ONLY weft.preview_runs r WHERE r.organization_id = p_org AND r.run_id = p_run FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'missing'::text, NULL::integer, NULL::uuid, NULL::timestamptz, NULL::uuid, NULL::uuid, NULL::uuid,
            NULL::uuid, NULL::text, NULL::text, NULL::uuid, NULL::text, NULL::bigint;
        RETURN;
    END IF;
    IF run.state IN (3,4) THEN
        RETURN QUERY SELECT 'finished'::text, run.attempt, NULL::uuid, NULL::timestamptz, run.project_id, run.mapping_id,
            run.revision_id, run.submitter_id, NULL::text, NULL::text, NULL::uuid, NULL::text, NULL::bigint;
        RETURN;
    END IF;
    IF (run.state = 2 AND run.lease_expires_at > observed_at) OR (run.state = 1 AND run.not_before > observed_at) THEN
        RETURN QUERY SELECT 'busy'::text, run.attempt, NULL::uuid, run.lease_expires_at, run.project_id, run.mapping_id,
            run.revision_id, run.submitter_id, NULL::text, NULL::text, NULL::uuid, NULL::text, NULL::bigint;
        RETURN;
    END IF;
    IF run.cancel_requested OR NOT weft.preview_authorized(p_org,run.project_id,run.submitter_id)
        OR run.attempt >= run.max_attempts THEN
        UPDATE weft.preview_runs r SET state = CASE WHEN run.cancel_requested THEN 4 ELSE 3 END,
            outcome = CASE WHEN run.cancel_requested THEN 'canceled'
                WHEN run.attempt >= run.max_attempts THEN 'attemptsExhausted' ELSE 'authorityRevoked' END,
            lease_token = NULL, lease_owner = NULL, lease_expires_at = NULL, finished_at = observed_at
        WHERE r.organization_id = p_org AND r.run_id = p_run;
        RETURN QUERY SELECT 'finished'::text, run.attempt, NULL::uuid, NULL::timestamptz, run.project_id, run.mapping_id,
            run.revision_id, run.submitter_id, NULL::text, NULL::text, NULL::uuid, NULL::text, NULL::bigint;
        RETURN;
    END IF;
    UPDATE weft.preview_runs r SET state = 2, attempt = run.attempt + 1, lease_token = token, lease_owner = p_owner,
        lease_expires_at = observed_at + pg_catalog.make_interval(secs => p_lease_seconds), started_at = coalesce(run.started_at, observed_at)
    WHERE r.organization_id = p_org AND r.run_id = p_run;
    RETURN QUERY SELECT 'claimed'::text, run.attempt + 1, token, observed_at + pg_catalog.make_interval(secs => p_lease_seconds),
        run.project_id, run.mapping_id, run.revision_id, run.submitter_id, run.request_document, run.current_date_time,
        run.native_object_id, run.native_digest, run.native_byte_count;
END;
$weft_preview_claim$;

-- Inputs of a currently claimed attempt, in their submitted order.
CREATE FUNCTION weft.preview_inputs(p_org uuid, p_run uuid, p_token uuid)
RETURNS TABLE(ordinal integer, name text, digest text, content bytea)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_inputs$
    SELECT d.ordinal, d.name, d.digest, d.content FROM ONLY weft.preview_documents d
    JOIN ONLY weft.preview_runs r ON r.organization_id = d.organization_id AND r.run_id = d.run_id
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.state = 2 AND r.lease_token = p_token AND d.direction = 1
    ORDER BY d.ordinal
$weft_preview_inputs$;

-- Reports whether a claim is still current and whether cancellation was requested.
CREATE FUNCTION weft.preview_observe(p_org uuid, p_run uuid, p_token uuid)
RETURNS TABLE(claim_current boolean, cancel_requested boolean)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_observe$
    SELECT coalesce(bool_or(r.state = 2 AND r.lease_token = p_token), false), coalesce(bool_or(r.cancel_requested), false)
    FROM ONLY weft.preview_runs r WHERE r.organization_id = p_org AND r.run_id = p_run
$weft_preview_observe$;

-- Atomically records the terminal outcome and, for success, every output document. A stale or
-- superseded claim returns 'claimLost' and publishes nothing.
CREATE FUNCTION weft.preview_complete(p_org uuid, p_run uuid, p_token uuid, p_outcome text, p_failure_code text,
    p_failure_message text, p_failure_message_truncated boolean, p_failure_subject text, p_failure_subject_truncated boolean,
    p_confirmed boolean, p_engine_revision text, p_backend_build text, p_adapter_build text, p_profile text,
    p_records_written bigint, p_boundaries text[], p_target_names text[], p_target_names_truncated boolean[],
    p_media_types text[], p_records bigint[], p_contents bytea[])
RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_complete$
DECLARE
    run weft.preview_runs%ROWTYPE;
    items integer := coalesce(pg_catalog.cardinality(p_contents), 0);
    total bigint := 0;
    item integer;
BEGIN
    IF p_org IS NULL OR p_run IS NULL OR p_token IS NULL OR p_outcome IS NULL
        OR (p_outcome = 'succeeded') <> (items > 0)
        OR items <> coalesce(pg_catalog.cardinality(p_boundaries), 0)
        OR items <> coalesce(pg_catalog.cardinality(p_target_names), 0)
        OR items <> coalesce(pg_catalog.cardinality(p_target_names_truncated), 0)
        OR items <> coalesce(pg_catalog.cardinality(p_media_types), 0)
        OR items <> coalesce(pg_catalog.cardinality(p_records), 0) THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview completion arguments are invalid.';
    END IF;
    SELECT * INTO run FROM ONLY weft.preview_runs r WHERE r.organization_id = p_org AND r.run_id = p_run FOR UPDATE;
    IF NOT FOUND OR run.state <> 2 OR run.lease_token IS DISTINCT FROM p_token THEN
        RETURN 'claimLost';
    END IF;
    FOR item IN 1 .. items LOOP
        total := total + octet_length(p_contents[item]);
        INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,name,target_name,target_name_truncated,
            media_type,records_written,digest,byte_count,content)
        VALUES (p_org,p_run,2,item - 1,p_boundaries[item],p_target_names[item],p_target_names_truncated[item],
            p_media_types[item],p_records[item],encode(sha256(p_contents[item]),'hex'),
            octet_length(p_contents[item]),p_contents[item]);
    END LOOP;
    UPDATE weft.preview_runs r SET state = CASE WHEN p_outcome = 'canceled' THEN 4 ELSE 3 END, outcome = p_outcome,
        failure_code = p_failure_code, failure_message = p_failure_message, failure_message_truncated = p_failure_message_truncated,
        failure_subject = p_failure_subject, failure_subject_truncated = p_failure_subject_truncated,
        provenance_confirmed = CASE WHEN p_outcome = 'canceled' THEN NULL ELSE p_confirmed END,
        engine_revision = CASE WHEN p_outcome = 'canceled' THEN NULL ELSE p_engine_revision END,
        backend_build = CASE WHEN p_outcome = 'canceled' THEN NULL ELSE p_backend_build END,
        adapter_build = CASE WHEN p_outcome = 'canceled' THEN NULL ELSE p_adapter_build END,
        profile = CASE WHEN p_outcome = 'canceled' THEN NULL ELSE p_profile END,
        records_written = CASE WHEN items > 0 THEN p_records_written END,
        output_count = CASE WHEN items > 0 THEN items END, output_bytes = CASE WHEN items > 0 THEN total END,
        lease_token = NULL, lease_owner = NULL, lease_expires_at = NULL, finished_at = statement_timestamp()
    WHERE r.organization_id = p_org AND r.run_id = p_run;
    RETURN 'completed';
END;
$weft_preview_complete$;

-- Returns a claimed run to the queue after an infrastructure failure, with a retry delay.
CREATE FUNCTION weft.preview_release(p_org uuid, p_run uuid, p_token uuid, p_retry_seconds integer)
RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_release$
BEGIN
    IF p_org IS NULL OR p_run IS NULL OR p_token IS NULL OR p_retry_seconds NOT BETWEEN 0 AND 3600 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview release arguments are invalid.';
    END IF;
    UPDATE weft.preview_runs r SET state = 1, lease_token = NULL, lease_owner = NULL, lease_expires_at = NULL,
        not_before = statement_timestamp() + pg_catalog.make_interval(secs => p_retry_seconds)
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.state = 2 AND r.lease_token = p_token;
    RETURN CASE WHEN FOUND THEN 'released' ELSE 'claimLost' END;
END;
$weft_preview_release$;

-- The submitter can cancel a queued run immediately or request cancellation of a running one.
CREATE FUNCTION weft.preview_cancel(p_org uuid, p_run uuid, p_actor uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_cancel$
DECLARE
    run weft.preview_runs%ROWTYPE;
BEGIN
    IF p_org IS NULL OR p_run IS NULL OR p_actor IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview cancellation arguments are invalid.';
    END IF;
    SELECT * INTO run FROM ONLY weft.preview_runs r WHERE r.organization_id = p_org AND r.run_id = p_run FOR UPDATE;
    IF NOT FOUND OR run.submitter_id <> p_actor OR NOT weft.preview_authorized(p_org,run.project_id,p_actor) THEN
        RETURN 'notAuthorizedOrNotFound';
    END IF;
    IF run.state IN (3,4) THEN
        RETURN 'finished';
    END IF;
    IF run.state = 1 THEN
        UPDATE weft.preview_runs r SET state = 4, outcome = 'canceled', cancel_requested = true, finished_at = statement_timestamp()
        WHERE r.organization_id = p_org AND r.run_id = p_run;
        RETURN 'canceled';
    END IF;
    UPDATE weft.preview_runs r SET cancel_requested = true WHERE r.organization_id = p_org AND r.run_id = p_run;
    RETURN 'requested';
END;
$weft_preview_cancel$;

-- Submitter-only reads with fresh authority. Unauthorized and missing runs are indistinguishable.
CREATE FUNCTION weft.preview_read(p_org uuid, p_run uuid, p_actor uuid)
RETURNS TABLE(project_id uuid, mapping_id uuid, revision_id uuid, request_key uuid, request_fingerprint text,
    current_date_time text, state integer, attempt integer, max_attempts integer, cancel_requested boolean, outcome text,
    failure_code text, failure_message text, failure_message_truncated boolean, failure_subject text,
    failure_subject_truncated boolean, provenance_confirmed boolean, engine_revision text, backend_build text,
    adapter_build text, profile text, records_written bigint, created_at timestamptz, started_at timestamptz,
    finished_at timestamptz, expires_at timestamptz)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_read$
    SELECT r.project_id, r.mapping_id, r.revision_id, r.request_key, r.request_fingerprint, r.current_date_time, r.state,
        r.attempt, r.max_attempts, r.cancel_requested, r.outcome, r.failure_code, r.failure_message,
        r.failure_message_truncated, r.failure_subject, r.failure_subject_truncated, r.provenance_confirmed,
        r.engine_revision, r.backend_build, r.adapter_build, r.profile, r.records_written, r.created_at, r.started_at,
        r.finished_at, r.expires_at
    FROM ONLY weft.preview_runs r
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.submitter_id = p_actor
        AND weft.preview_authorized(p_org,r.project_id,p_actor)
$weft_preview_read$;

CREATE FUNCTION weft.preview_outputs(p_org uuid, p_run uuid, p_actor uuid)
RETURNS TABLE(ordinal integer, boundary_id text, target_name text, target_name_truncated boolean, media_type text,
    records_written bigint, digest text, byte_count integer)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_outputs$
    SELECT d.ordinal, d.name, d.target_name, d.target_name_truncated, d.media_type, d.records_written, d.digest, d.byte_count
    FROM ONLY weft.preview_documents d
    JOIN ONLY weft.preview_runs r ON r.organization_id = d.organization_id AND r.run_id = d.run_id
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.submitter_id = p_actor AND d.direction = 2
        AND weft.preview_authorized(p_org,r.project_id,p_actor)
    ORDER BY d.ordinal
$weft_preview_outputs$;

CREATE FUNCTION weft.preview_output(p_org uuid, p_run uuid, p_actor uuid, p_ordinal integer)
RETURNS TABLE(digest text, content bytea)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_output$
    SELECT d.digest, d.content FROM ONLY weft.preview_documents d
    JOIN ONLY weft.preview_runs r ON r.organization_id = d.organization_id AND r.run_id = d.run_id
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.submitter_id = p_actor AND d.direction = 2
        AND d.ordinal = p_ordinal AND weft.preview_authorized(p_org,r.project_id,p_actor)
$weft_preview_output$;

REVOKE ALL ON weft.preview_runs,weft.preview_documents FROM PUBLIC;
REVOKE ALL ON FUNCTION weft.preview_authorized(uuid,uuid,uuid),
    weft.preview_submit(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    weft.preview_claim(uuid,uuid,uuid,integer), weft.preview_inputs(uuid,uuid,uuid), weft.preview_observe(uuid,uuid,uuid),
    weft.preview_complete(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[]),
    weft.preview_release(uuid,uuid,uuid,integer), weft.preview_cancel(uuid,uuid,uuid), weft.preview_read(uuid,uuid,uuid),
    weft.preview_outputs(uuid,uuid,uuid), weft.preview_output(uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_preview_reader,weft_preview_writer;
GRANT EXECUTE ON FUNCTION weft.preview_submit(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    weft.preview_claim(uuid,uuid,uuid,integer), weft.preview_inputs(uuid,uuid,uuid), weft.preview_observe(uuid,uuid,uuid),
    weft.preview_complete(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[]),
    weft.preview_release(uuid,uuid,uuid,integer), weft.preview_cancel(uuid,uuid,uuid) TO weft_preview_writer;
GRANT EXECUTE ON FUNCTION weft.preview_read(uuid,uuid,uuid), weft.preview_outputs(uuid,uuid,uuid),
    weft.preview_output(uuid,uuid,uuid,integer) TO weft_preview_reader;
