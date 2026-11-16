-- Additive report metadata only. All predecessor facts, shapes and grants remain unchanged.
DO $braid_require_report_roles$
DECLARE
    application_role record;
BEGIN
    IF (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('braid_report_reader','braid_deriver')) <> 2 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Revision reports require both dedicated restricted logins.';
    END IF;
    FOR application_role IN SELECT * FROM pg_catalog.pg_roles WHERE rolname IN ('braid_report_reader','braid_deriver') LOOP
        IF NOT application_role.rolcanlogin OR application_role.rolinherit OR application_role.rolsuper
            OR application_role.rolbypassrls OR application_role.rolcreatedb OR application_role.rolcreaterole
            OR application_role.rolreplication
            OR NOT pg_catalog.has_database_privilege(application_role.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(application_role.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR pg_catalog.has_parameter_privilege(application_role.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles other WHERE other.oid <> application_role.oid
                       AND pg_catalog.pg_has_role(application_role.oid,other.oid,'MEMBER'))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%'
                       AND n.nspname NOT LIKE 'pg_toast_temp_%'
                       AND (n.nspowner = application_role.oid OR pg_catalog.has_schema_privilege(application_role.oid,n.oid,'CREATE')))
            OR pg_catalog.has_schema_privilege(application_role.oid,'braid','USAGE')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid = r.relnamespace
                       WHERE n.nspname = 'braid' AND r.relkind IN ('r','p','v','m','f')
                       AND (pg_catalog.has_table_privilege(application_role.oid,r.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR pg_catalog.has_any_column_privilege(application_role.oid,r.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid = f.pronamespace
                       WHERE n.nspname = 'braid' AND pg_catalog.has_function_privilege(application_role.oid,f.oid,'EXECUTE'))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid = r.relnamespace
                       WHERE n.nspname = 'braid' AND CASE WHEN r.relkind = 'S'
                       THEN pg_catalog.has_sequence_privilege(application_role.oid,r.oid,'USAGE,SELECT,UPDATE') ELSE false END)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                       WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='braid'))
                       AND a.grantee IN (0,application_role.oid)) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Revision report logins require explicit CONNECT-only provisioning.';
        END IF;
    END LOOP;
END;
$braid_require_report_roles$;

-- A standalone index preserves the predecessor's exact pg_constraint set.
CREATE UNIQUE INDEX mapping_revisions_derivation_source_key ON braid.mapping_revisions
    (organization_id,project_id,mapping_id,id,native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id);

CREATE TABLE braid.revision_derivations (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    cache_entry_id uuid NOT NULL,
    operation_kind text NOT NULL,
    native_object_id uuid NOT NULL,
    native_class text NOT NULL,
    native_digest text NOT NULL,
    native_byte_count bigint NOT NULL,
    logical_entry_document text NOT NULL,
    dependency_manifest_id text NOT NULL,
    backend_id text NOT NULL,
    engine_revision text NOT NULL,
    backend_build text NOT NULL,
    adapter_build text NOT NULL,
    schema_version text NOT NULL,
    algorithm_version text NOT NULL,
    codec_version text NOT NULL,
    policy_schema_version text NOT NULL,
    policy_json text NOT NULL,
    policy_fingerprint text NOT NULL,
    native_byte_limit integer NOT NULL,
    worker_response_byte_limit integer NOT NULL,
    item_limit integer NOT NULL,
    response_item_limit integer NOT NULL,
    json_depth_limit integer NOT NULL,
    detail_depth_limit integer NOT NULL,
    diagnostic_limit integer NOT NULL,
    validation_issue_limit integer NOT NULL,
    text_byte_limit integer NOT NULL,
    header_byte_limit integer NOT NULL,
    stored_object_byte_limit integer NOT NULL,
    response_byte_limit integer NOT NULL,
    key_fingerprint text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    current_generation bigint NOT NULL DEFAULT 0,
    current_attempt_id uuid,
    selected_derivation_id uuid,
    selected_usable boolean NOT NULL DEFAULT true,
    CONSTRAINT revision_derivations_pk PRIMARY KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id),
    CONSTRAINT revision_derivations_identity_key UNIQUE (organization_id,project_id,mapping_id,revision_id,operation_kind,key_fingerprint),
    CONSTRAINT revision_derivations_native_facts_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id)
        REFERENCES braid.mapping_revisions(organization_id,project_id,mapping_id,id,native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT revision_derivations_ids_check CHECK (
        organization_id <> '00000000-0000-0000-0000-000000000000' AND project_id <> '00000000-0000-0000-0000-000000000000'
        AND mapping_id <> '00000000-0000-0000-0000-000000000000' AND revision_id <> '00000000-0000-0000-0000-000000000000'
        AND cache_entry_id <> '00000000-0000-0000-0000-000000000000' AND native_object_id <> '00000000-0000-0000-0000-000000000000'
        AND (current_attempt_id IS NULL OR current_attempt_id <> '00000000-0000-0000-0000-000000000000')
        AND (selected_derivation_id IS NULL OR selected_derivation_id <> '00000000-0000-0000-0000-000000000000')),
    CONSTRAINT revision_derivations_operation_check CHECK (operation_kind IN ('projection','validation')),
    CONSTRAINT revision_derivations_native_check CHECK (native_class = 'native' AND native_digest ~ '^[0-9a-f]{64}$'
        AND native_byte_count BETWEEN 1 AND 4194304 AND logical_entry_document = 'entry.native.json'
        AND dependency_manifest_id = 'braid.dependencies.empty.v1'),
    CONSTRAINT revision_derivations_profile_check CHECK (octet_length(backend_id) BETWEEN 1 AND 128
        AND octet_length(engine_revision) BETWEEN 1 AND 256 AND octet_length(backend_build) BETWEEN 1 AND 2048
        AND octet_length(adapter_build) BETWEEN 1 AND 256),
    CONSTRAINT revision_derivations_report_identity_check CHECK (octet_length(schema_version) BETWEEN 1 AND 128
        AND octet_length(algorithm_version) BETWEEN 1 AND 128 AND octet_length(codec_version) BETWEEN 1 AND 128),
    CONSTRAINT revision_derivations_policy_limits_check CHECK (native_byte_limit BETWEEN 1 AND 4194304
        AND worker_response_byte_limit BETWEEN 1 AND 8388608 AND item_limit BETWEEN 1 AND 8192
        AND response_item_limit BETWEEN 1 AND 32768 AND json_depth_limit BETWEEN 1 AND 64
        AND detail_depth_limit BETWEEN 1 AND 16 AND diagnostic_limit BETWEEN 1 AND 128
        AND validation_issue_limit BETWEEN 1 AND 32768 AND text_byte_limit BETWEEN 1 AND 2048
        AND header_byte_limit BETWEEN 1 AND 16384 AND stored_object_byte_limit BETWEEN 1 AND 16777216
        AND response_byte_limit BETWEEN 1 AND 16777216),
    CONSTRAINT revision_derivations_policy_bytes_check CHECK (policy_schema_version = 'braid.revision-report-policy.v1'
        AND octet_length(policy_json) <= 4096 AND policy_json COLLATE "C" = (
            '{"schemaVersion":"braid.revision-report-policy.v1","nativeByteLimit":' || native_byte_limit::text
            || ',"workerResponseByteLimit":' || worker_response_byte_limit::text || ',"itemLimit":' || item_limit::text
            || ',"responseItemLimit":' || response_item_limit::text || ',"jsonDepthLimit":' || json_depth_limit::text
            || ',"detailDepthLimit":' || detail_depth_limit::text || ',"diagnosticLimit":' || diagnostic_limit::text
            || ',"validationIssueLimit":' || validation_issue_limit::text || ',"textByteLimit":' || text_byte_limit::text
            || ',"headerByteLimit":' || header_byte_limit::text || ',"storedObjectByteLimit":' || stored_object_byte_limit::text
            || ',"responseByteLimit":' || response_byte_limit::text || '}') COLLATE "C"
        AND policy_fingerprint COLLATE "C" = pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(policy_json,'UTF8')),'hex') COLLATE "C"),
    CONSTRAINT revision_derivations_fingerprints_check CHECK (key_fingerprint ~ '^[0-9a-f]{64}$' AND policy_fingerprint ~ '^[0-9a-f]{64}$'),
    CONSTRAINT revision_derivations_pointers_check CHECK (selected_usable AND current_generation >= 0
        AND ((current_generation = 0 AND current_attempt_id IS NULL AND selected_derivation_id IS NULL)
             OR (current_generation > 0 AND current_attempt_id IS NOT NULL)))
);

CREATE TABLE braid.revision_derivation_attempts (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    cache_entry_id uuid NOT NULL,
    generation bigint NOT NULL,
    derivation_id uuid NOT NULL,
    actor_principal_id uuid NOT NULL,
    claimant_instance_id uuid NOT NULL,
    claim_token uuid NOT NULL,
    repair_target_derivation_id uuid,
    started_at timestamp with time zone NOT NULL,
    lease_expires_at timestamp with time zone NOT NULL,
    candidate_object_id uuid NOT NULL,
    derived_class text NOT NULL DEFAULT 'derived',
    finished_at timestamp with time zone,
    completion_kind text,
    result_usable boolean NOT NULL DEFAULT false,
    failure_code text,
    observation_present boolean NOT NULL DEFAULT false,
    backend_status text,
    provenance_confirmed boolean,
    provenance_organization_id uuid,
    provenance_project_id uuid,
    provenance_mapping_id uuid,
    provenance_revision_id uuid,
    provenance_native_digest text,
    provenance_backend_id text,
    provenance_engine_revision text,
    provenance_backend_build text,
    provenance_adapter_build text,
    projection_coverage text,
    document_item_count integer,
    document_diagnostic_count integer,
    coverage_gap_count integer,
    operation_diagnostic_count integer,
    is_valid boolean,
    total_issue_count integer,
    emitted_issue_count integer,
    issues_truncated boolean,
    validation_diagnostic_count integer,
    derived_digest text,
    derived_byte_count bigint,
    CONSTRAINT revision_derivation_attempts_pk PRIMARY KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation),
    CONSTRAINT revision_derivation_attempts_id_key UNIQUE (organization_id,project_id,mapping_id,revision_id,derivation_id),
    CONSTRAINT revision_derivation_attempts_candidate_key UNIQUE (organization_id,project_id,mapping_id,revision_id,candidate_object_id),
    CONSTRAINT revision_derivation_attempts_current_key UNIQUE (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,derivation_id),
    CONSTRAINT revision_derivation_attempts_selected_key UNIQUE (organization_id,project_id,mapping_id,revision_id,cache_entry_id,derivation_id,result_usable),
    CONSTRAINT revision_derivation_attempts_entry_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id)
        REFERENCES braid.revision_derivations(organization_id,project_id,mapping_id,revision_id,cache_entry_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT revision_derivation_attempts_actor_fk FOREIGN KEY (actor_principal_id)
        REFERENCES braid.principals(id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT revision_derivation_attempts_repair_target_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,repair_target_derivation_id)
        REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT revision_derivation_attempts_ids_check CHECK (
        organization_id <> '00000000-0000-0000-0000-000000000000' AND project_id <> '00000000-0000-0000-0000-000000000000'
        AND mapping_id <> '00000000-0000-0000-0000-000000000000' AND revision_id <> '00000000-0000-0000-0000-000000000000'
        AND cache_entry_id <> '00000000-0000-0000-0000-000000000000' AND derivation_id <> '00000000-0000-0000-0000-000000000000'
        AND actor_principal_id <> '00000000-0000-0000-0000-000000000000' AND claimant_instance_id <> '00000000-0000-0000-0000-000000000000'
        AND claim_token <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT revision_derivation_attempts_generation_check CHECK (generation > 0),
    CONSTRAINT revision_derivation_attempts_lease_check CHECK (lease_expires_at = started_at + interval '60 seconds'),
    CONSTRAINT revision_derivation_attempts_candidate_check CHECK (derived_class = 'derived' AND candidate_object_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT revision_derivation_attempts_completion_check CHECK (((
        completion_kind IS NULL AND finished_at IS NULL AND NOT result_usable AND failure_code IS NULL
        AND NOT observation_present AND derived_digest IS NULL AND derived_byte_count IS NULL)
        OR (completion_kind = 'usable' AND finished_at >= started_at AND finished_at < lease_expires_at
            AND result_usable AND failure_code IS NULL AND observation_present AND derived_digest IS NOT NULL AND derived_byte_count IS NOT NULL)
        OR (completion_kind = 'failed' AND finished_at >= started_at AND finished_at < lease_expires_at
            AND NOT result_usable AND failure_code IN ('object_unavailable','storage_integrity','storage_io','backend_unavailable','backend_failure','timed_out','canceled','isolation_failure','protocol_failure','profile_unavailable','resource_limit','unsupported_artifact')
            AND derived_digest IS NULL AND derived_byte_count IS NULL)) IS TRUE),
    CONSTRAINT revision_derivation_attempts_observation_check CHECK (
        (backend_status IS NULL OR backend_status IN ('succeeded','invalid_mapping','unsupported_artifact','resource_limit','protocol_failure','backend_unavailable','backend_failure','timed_out','canceled','isolation_failure'))
        AND (observation_present OR num_nonnulls(backend_status,provenance_confirmed,provenance_organization_id,provenance_project_id,provenance_mapping_id,provenance_revision_id,provenance_native_digest,provenance_backend_id,provenance_engine_revision,provenance_backend_build,provenance_adapter_build,projection_coverage,document_item_count,document_diagnostic_count,coverage_gap_count,operation_diagnostic_count,is_valid,total_issue_count,emitted_issue_count,issues_truncated,validation_diagnostic_count) = 0)),
    CONSTRAINT revision_derivation_attempts_provenance_check CHECK ((
        num_nonnulls(provenance_confirmed,provenance_organization_id,provenance_project_id,provenance_mapping_id,provenance_revision_id,provenance_native_digest,provenance_backend_id,provenance_engine_revision,provenance_backend_build,provenance_adapter_build) = 0
        OR (num_nonnulls(provenance_confirmed,provenance_organization_id,provenance_project_id,provenance_mapping_id,provenance_revision_id,provenance_native_digest,provenance_backend_id,provenance_engine_revision,provenance_backend_build,provenance_adapter_build) = 10
            AND provenance_organization_id <> '00000000-0000-0000-0000-000000000000' AND provenance_project_id <> '00000000-0000-0000-0000-000000000000'
            AND provenance_mapping_id <> '00000000-0000-0000-0000-000000000000' AND provenance_revision_id <> '00000000-0000-0000-0000-000000000000'
            AND provenance_native_digest ~ '^[0-9a-f]{64}$' AND octet_length(provenance_backend_id) BETWEEN 1 AND 128
            AND octet_length(provenance_engine_revision) BETWEEN 1 AND 256 AND octet_length(provenance_backend_build) BETWEEN 1 AND 2048
            AND octet_length(provenance_adapter_build) BETWEEN 1 AND 256)) IS TRUE),
    CONSTRAINT revision_derivation_attempts_projection_check CHECK ((
        num_nonnulls(projection_coverage,document_item_count,document_diagnostic_count,coverage_gap_count,operation_diagnostic_count) = 0
        OR (num_nonnulls(document_item_count,document_diagnostic_count,coverage_gap_count,operation_diagnostic_count) = 4
            AND document_item_count BETWEEN 0 AND 8192 AND document_diagnostic_count BETWEEN 0 AND 128
            AND coverage_gap_count BETWEEN 0 AND 128 AND operation_diagnostic_count BETWEEN 0 AND 128
            AND document_diagnostic_count + coverage_gap_count <= document_item_count
            AND ((projection_coverage IS NULL AND document_item_count = 0 AND document_diagnostic_count = 0 AND coverage_gap_count = 0)
                OR (projection_coverage = 'complete' AND coverage_gap_count = 0)
                OR (projection_coverage IN ('partial','unavailable') AND coverage_gap_count > 0)))) IS TRUE),
    CONSTRAINT revision_derivation_attempts_validation_check CHECK ((
        num_nonnulls(is_valid,total_issue_count,emitted_issue_count,issues_truncated,validation_diagnostic_count) = 0
        OR (num_nonnulls(total_issue_count,emitted_issue_count,issues_truncated,validation_diagnostic_count) = 4
            AND total_issue_count BETWEEN 0 AND 32768 AND emitted_issue_count BETWEEN 0 AND least(total_issue_count,128)
            AND validation_diagnostic_count BETWEEN 0 AND 128 AND (is_valid IS NULL OR validation_diagnostic_count = emitted_issue_count))) IS TRUE),
    CONSTRAINT revision_derivation_attempts_result_check CHECK ((derived_digest IS NULL AND derived_byte_count IS NULL)
        OR (derived_digest ~ '^[0-9a-f]{64}$' AND derived_byte_count BETWEEN 1 AND 16777216) IS TRUE),
    CONSTRAINT revision_derivation_attempts_repair_check CHECK (repair_target_derivation_id IS NULL
        OR (repair_target_derivation_id <> '00000000-0000-0000-0000-000000000000' AND repair_target_derivation_id <> derivation_id))
);

ALTER TABLE braid.revision_derivations ADD CONSTRAINT revision_derivations_current_fk
    FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id,current_generation,current_attempt_id)
    REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT;
ALTER TABLE braid.revision_derivations ADD CONSTRAINT revision_derivations_selected_fk
    FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id,selected_derivation_id,selected_usable)
    REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,derivation_id,result_usable) ON DELETE RESTRICT ON UPDATE RESTRICT;

ALTER TABLE braid.revision_derivations ENABLE ROW LEVEL SECURITY;
CREATE POLICY revision_derivations_organization_scope ON braid.revision_derivations
    USING (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid);
ALTER TABLE braid.revision_derivation_attempts ENABLE ROW LEVEL SECURITY;
CREATE POLICY revision_derivation_attempts_organization_scope ON braid.revision_derivation_attempts
    USING (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid);

CREATE FUNCTION braid.guard_revision_derivations_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog,braid,pg_temp
AS $braid_guard_revision_derivations_transition$
DECLARE
    proposed braid.revision_derivation_attempts%ROWTYPE;
    previous braid.revision_derivation_attempts%ROWTYPE;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Revision derivation deletion is unavailable.';
    ELSIF TG_OP = 'INSERT' THEN
        IF NEW.current_generation <> 0 OR NEW.current_attempt_id IS NOT NULL
            OR NEW.selected_derivation_id IS NOT NULL OR NEW.selected_usable IS DISTINCT FROM true THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A derivation key must begin without an attempt or result.';
        END IF;
        NEW.cache_entry_id := pg_catalog.gen_random_uuid();
        NEW.created_at := pg_catalog.clock_timestamp();
        RETURN NEW;
    END IF;
    IF (pg_catalog.to_jsonb(NEW) - ARRAY['current_generation','current_attempt_id','selected_derivation_id'])
        IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['current_generation','current_attempt_id','selected_derivation_id']) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Revision report source, key and policy facts are immutable.';
    END IF;
    IF NEW.current_generation = OLD.current_generation THEN
        IF NEW.current_attempt_id IS DISTINCT FROM OLD.current_attempt_id OR OLD.selected_derivation_id IS NOT NULL
            OR NEW.selected_derivation_id IS NULL OR NEW.selected_derivation_id IS DISTINCT FROM OLD.current_attempt_id THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Selection requires the exact current usable result.';
        END IF;
        SELECT * INTO proposed FROM braid.revision_derivation_attempts
        WHERE organization_id = OLD.organization_id AND project_id = OLD.project_id AND mapping_id = OLD.mapping_id
          AND revision_id = OLD.revision_id AND cache_entry_id = OLD.cache_entry_id
          AND generation = OLD.current_generation AND derivation_id = NEW.selected_derivation_id;
        IF NOT FOUND OR proposed.completion_kind IS DISTINCT FROM 'usable' OR proposed.result_usable IS DISTINCT FROM true THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Selection requires completed matching usable metadata.';
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM braid.projects project
            JOIN braid.principals principal ON principal.id = proposed.actor_principal_id AND principal.active
            JOIN braid.organization_memberships admission ON admission.organization_id = project.organization_id
                AND admission.principal_id = principal.id AND admission.active
            JOIN braid.project_memberships membership ON membership.organization_id = project.organization_id
                AND membership.project_id = project.id AND membership.principal_id = principal.id AND membership.active
                AND membership.role COLLATE "C" IN ('viewer','runner','publisher','administrator')
            WHERE project.organization_id = OLD.organization_id AND project.id = OLD.project_id
        ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Selecting a report requires fresh original-actor access.';
        END IF;
        RETURN NEW;
    END IF;
    -- Keep overflow outside arithmetic: it is a refusal, never a wrapped generation.
    IF OLD.current_generation = 9223372036854775807 THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Revision report generation is exhausted.';
    END IF;
    IF NEW.current_generation <> OLD.current_generation + 1 OR NEW.current_attempt_id IS NULL
        OR NEW.current_attempt_id IS NOT DISTINCT FROM OLD.current_attempt_id OR NEW.selected_derivation_id IS NOT NULL THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A new claim must advance exactly one generation and clear only its repair selection.';
    END IF;
    SELECT * INTO proposed FROM braid.revision_derivation_attempts
    WHERE organization_id = OLD.organization_id AND project_id = OLD.project_id AND mapping_id = OLD.mapping_id
      AND revision_id = OLD.revision_id AND cache_entry_id = OLD.cache_entry_id
      AND generation = NEW.current_generation AND derivation_id = NEW.current_attempt_id;
    IF NOT FOUND OR proposed.completion_kind IS NOT NULL OR proposed.finished_at IS NOT NULL
        OR proposed.repair_target_derivation_id IS DISTINCT FROM OLD.selected_derivation_id THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A new current claim requires its matching pending intent and repair lineage.';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM braid.projects project
        JOIN braid.principals principal ON principal.id = proposed.actor_principal_id AND principal.active
        JOIN braid.organization_memberships admission ON admission.organization_id = project.organization_id
            AND admission.principal_id = principal.id AND admission.active
        JOIN braid.project_memberships membership ON membership.organization_id = project.organization_id
            AND membership.project_id = project.id AND membership.principal_id = principal.id AND membership.active
            AND membership.role COLLATE "C" IN ('viewer','runner','publisher','administrator')
        WHERE project.organization_id = OLD.organization_id AND project.id = OLD.project_id
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Advancing a report claim requires fresh original-actor access.';
    END IF;
    IF OLD.current_generation > 0 THEN
        SELECT * INTO previous FROM braid.revision_derivation_attempts
        WHERE organization_id = OLD.organization_id AND project_id = OLD.project_id AND mapping_id = OLD.mapping_id
          AND revision_id = OLD.revision_id AND cache_entry_id = OLD.cache_entry_id
          AND generation = OLD.current_generation AND derivation_id = OLD.current_attempt_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The predecessor attempt must remain present.';
        END IF;
        IF OLD.selected_derivation_id IS NOT NULL THEN
            IF OLD.selected_derivation_id IS DISTINCT FROM previous.derivation_id OR previous.result_usable IS DISTINCT FROM true THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Repair must replace exactly the selected usable predecessor.';
            END IF;
        ELSIF previous.completion_kind IS NULL THEN
            IF previous.lease_expires_at > pg_catalog.clock_timestamp() THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A live report lease cannot be replaced.';
            END IF;
        ELSIF previous.completion_kind = 'failed' THEN
            IF previous.finished_at + interval '30 seconds' > pg_catalog.clock_timestamp() THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Failed report retry is not due.';
            END IF;
        ELSE
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A usable predecessor can only be superseded by exact repair.';
        END IF;
    END IF;
    RETURN NEW;
END;
$braid_guard_revision_derivations_transition$;

CREATE FUNCTION braid.guard_revision_derivation_attempts_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog,braid,pg_temp
AS $braid_guard_revision_derivation_attempts_transition$
DECLARE
    reserved braid.revision_derivations%ROWTYPE;
    previous braid.revision_derivation_attempts%ROWTYPE;
    observed_at timestamp with time zone;
    matched boolean;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Report attempt deletion is unavailable.';
    END IF;
    SELECT * INTO reserved FROM braid.revision_derivations
    WHERE organization_id = NEW.organization_id AND project_id = NEW.project_id AND mapping_id = NEW.mapping_id
      AND revision_id = NEW.revision_id AND cache_entry_id = NEW.cache_entry_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A report attempt requires its exact immutable key.';
    END IF;
    -- This separate command runs after the key-row lock, including direct trusted-role SQL.
    IF NOT EXISTS (
        SELECT 1 FROM braid.organizations organization
        JOIN braid.projects project ON project.organization_id = organization.id AND project.id = NEW.project_id
        JOIN braid.principals principal ON principal.id = NEW.actor_principal_id AND principal.active
        JOIN braid.organization_memberships admission ON admission.organization_id = organization.id
            AND admission.principal_id = principal.id AND admission.active
        JOIN braid.project_memberships membership ON membership.organization_id = organization.id
            AND membership.project_id = project.id AND membership.principal_id = principal.id AND membership.active
            AND membership.role COLLATE "C" IN ('viewer','runner','publisher','administrator')
        JOIN braid.mappings mapping ON mapping.organization_id = project.organization_id AND mapping.project_id = project.id
            AND mapping.id = NEW.mapping_id
        JOIN braid.mapping_revisions revision ON revision.organization_id = mapping.organization_id
            AND revision.project_id = mapping.project_id AND revision.mapping_id = mapping.id AND revision.id = NEW.revision_id
        WHERE organization.id = NEW.organization_id
          AND pg_catalog.jsonb_build_array(revision.native_object_id,revision.native_class,revision.native_digest,revision.native_byte_count,revision.logical_entry_document,revision.dependency_manifest_id,revision.backend_id)
            = pg_catalog.jsonb_build_array(reserved.native_object_id,reserved.native_class,reserved.native_digest,reserved.native_byte_count,reserved.logical_entry_document,reserved.dependency_manifest_id,reserved.backend_id)
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Report publication requires fresh original-actor source access.';
    END IF;
    observed_at := pg_catalog.clock_timestamp();
    IF TG_OP = 'INSERT' THEN
        IF reserved.current_generation = 9223372036854775807 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Revision report generation is exhausted.';
        END IF;
        IF NEW.generation <> reserved.current_generation + 1 OR NEW.completion_kind IS NOT NULL
            OR NEW.finished_at IS NOT NULL OR NEW.result_usable IS DISTINCT FROM false OR NEW.observation_present IS DISTINCT FROM false
            OR num_nonnulls(NEW.failure_code,NEW.backend_status,NEW.provenance_confirmed,NEW.provenance_organization_id,NEW.provenance_project_id,NEW.provenance_mapping_id,NEW.provenance_revision_id,NEW.provenance_native_digest,NEW.provenance_backend_id,NEW.provenance_engine_revision,NEW.provenance_backend_build,NEW.provenance_adapter_build,NEW.projection_coverage,NEW.document_item_count,NEW.document_diagnostic_count,NEW.coverage_gap_count,NEW.operation_diagnostic_count,NEW.is_valid,NEW.total_issue_count,NEW.emitted_issue_count,NEW.issues_truncated,NEW.validation_diagnostic_count,NEW.derived_digest,NEW.derived_byte_count) <> 0
            OR NEW.derived_class IS DISTINCT FROM 'derived'
            OR NEW.repair_target_derivation_id IS DISTINCT FROM reserved.selected_derivation_id THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A report attempt must start as a fresh matching pending claim.';
        END IF;
        IF reserved.current_generation > 0 THEN
            SELECT * INTO previous FROM braid.revision_derivation_attempts
            WHERE organization_id = reserved.organization_id AND project_id = reserved.project_id AND mapping_id = reserved.mapping_id
              AND revision_id = reserved.revision_id AND cache_entry_id = reserved.cache_entry_id
              AND generation = reserved.current_generation AND derivation_id = reserved.current_attempt_id;
            IF NOT FOUND THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The prior report intent must remain present.';
            END IF;
            IF reserved.selected_derivation_id IS NOT NULL THEN
                IF previous.derivation_id IS DISTINCT FROM reserved.selected_derivation_id OR previous.result_usable IS DISTINCT FROM true THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Repair requires the exact selected usable intent.';
                END IF;
            ELSIF previous.completion_kind IS NULL THEN
                IF previous.lease_expires_at > observed_at THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A live report claim cannot be replaced.';
                END IF;
            ELSIF previous.completion_kind = 'failed' THEN
                IF previous.finished_at + interval '30 seconds' > observed_at THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Failed report retry is not due.';
                END IF;
            ELSE
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Usable report history can only be superseded by repair.';
            END IF;
        END IF;
        NEW.derivation_id := pg_catalog.gen_random_uuid();
        NEW.claim_token := pg_catalog.gen_random_uuid();
        NEW.candidate_object_id := pg_catalog.gen_random_uuid();
        NEW.started_at := observed_at;
        NEW.lease_expires_at := observed_at + interval '60 seconds';
        RETURN NEW;
    END IF;
    IF (pg_catalog.to_jsonb(NEW) - ARRAY['finished_at','completion_kind','result_usable','failure_code','observation_present','backend_status','provenance_confirmed','provenance_organization_id','provenance_project_id','provenance_mapping_id','provenance_revision_id','provenance_native_digest','provenance_backend_id','provenance_engine_revision','provenance_backend_build','provenance_adapter_build','projection_coverage','document_item_count','document_diagnostic_count','coverage_gap_count','operation_diagnostic_count','is_valid','total_issue_count','emitted_issue_count','issues_truncated','validation_diagnostic_count','derived_digest','derived_byte_count'])
        IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['finished_at','completion_kind','result_usable','failure_code','observation_present','backend_status','provenance_confirmed','provenance_organization_id','provenance_project_id','provenance_mapping_id','provenance_revision_id','provenance_native_digest','provenance_backend_id','provenance_engine_revision','provenance_backend_build','provenance_adapter_build','projection_coverage','document_item_count','document_diagnostic_count','coverage_gap_count','operation_diagnostic_count','is_valid','total_issue_count','emitted_issue_count','issues_truncated','validation_diagnostic_count','derived_digest','derived_byte_count']) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Report actor, claim, object and lease headers are immutable.';
    END IF;
    IF OLD.finished_at IS NOT NULL OR OLD.completion_kind IS NOT NULL OR NEW.completion_kind IS NULL
        OR reserved.current_generation <> OLD.generation OR reserved.current_attempt_id IS DISTINCT FROM OLD.derivation_id
        OR reserved.selected_derivation_id IS NOT NULL OR OLD.lease_expires_at <= observed_at THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Report completion requires the live current unfinished claim.';
    END IF;
    IF (reserved.operation_kind = 'projection' AND num_nonnulls(NEW.is_valid,NEW.total_issue_count,NEW.emitted_issue_count,NEW.issues_truncated,NEW.validation_diagnostic_count) <> 0)
        OR (reserved.operation_kind = 'validation' AND num_nonnulls(NEW.projection_coverage,NEW.document_item_count,NEW.document_diagnostic_count,NEW.coverage_gap_count,NEW.operation_diagnostic_count) <> 0) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Report observations must match their immutable operation.';
    END IF;
    NEW.finished_at := observed_at;
    NEW.result_usable := NEW.completion_kind = 'usable';
    IF NEW.result_usable THEN
        matched := NEW.provenance_confirmed IS TRUE
            AND pg_catalog.jsonb_build_array(NEW.provenance_organization_id,NEW.provenance_project_id,NEW.provenance_mapping_id,NEW.provenance_revision_id,NEW.provenance_native_digest,NEW.provenance_backend_id,NEW.provenance_engine_revision,NEW.provenance_backend_build,NEW.provenance_adapter_build)
              = pg_catalog.jsonb_build_array(reserved.organization_id,reserved.project_id,reserved.mapping_id,reserved.revision_id,reserved.native_digest,reserved.backend_id,reserved.engine_revision,reserved.backend_build,reserved.adapter_build);
        IF matched IS DISTINCT FROM true OR NEW.observation_present IS DISTINCT FROM true
            OR reserved.native_byte_count > reserved.native_byte_limit OR NEW.derived_digest IS NULL
            OR NEW.derived_byte_count IS NULL OR NEW.derived_byte_count > reserved.stored_object_byte_limit THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Usable report publication requires matching observed provenance and bounded object facts.';
        END IF;
        IF reserved.operation_kind = 'projection' THEN
            IF NEW.backend_status IS DISTINCT FROM 'succeeded' OR NEW.projection_coverage IS NULL
                OR NEW.document_item_count IS NULL OR NEW.document_item_count > reserved.item_limit
                OR NEW.document_diagnostic_count > reserved.diagnostic_limit OR NEW.coverage_gap_count > reserved.diagnostic_limit
                OR NEW.operation_diagnostic_count > reserved.diagnostic_limit THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Usable projection requires an actual successful bounded document.';
            END IF;
        ELSE
            IF ((NEW.backend_status = 'succeeded' AND NEW.is_valid IS TRUE)
                 OR (NEW.backend_status = 'invalid_mapping' AND NEW.is_valid IS FALSE)) IS DISTINCT FROM true
                OR NEW.total_issue_count IS NULL OR NEW.total_issue_count > reserved.validation_issue_limit
                OR NEW.validation_diagnostic_count > reserved.diagnostic_limit THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Usable validation requires an actual eligible bounded validity result.';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$braid_guard_revision_derivation_attempts_transition$;

CREATE TRIGGER revision_derivations_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.revision_derivations
    FOR EACH ROW EXECUTE FUNCTION braid.guard_revision_derivations_transition();
CREATE TRIGGER revision_derivation_attempts_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.revision_derivation_attempts
    FOR EACH ROW EXECUTE FUNCTION braid.guard_revision_derivation_attempts_transition();

REVOKE ALL ON FUNCTION braid.guard_revision_derivations_transition(),braid.guard_revision_derivation_attempts_transition()
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver;
REVOKE ALL ON braid.revision_derivations,braid.revision_derivation_attempts
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver;
GRANT USAGE ON SCHEMA braid TO braid_report_reader,braid_deriver;
GRANT SELECT ON braid.organizations,braid.principals,braid.organization_memberships,braid.projects,braid.project_memberships,braid.mappings,braid.mapping_revisions,braid.revision_derivations,braid.revision_derivation_attempts TO braid_report_reader,braid_deriver;
GRANT INSERT (organization_id,project_id,mapping_id,revision_id,operation_kind,native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,policy_schema_version,policy_json,policy_fingerprint,native_byte_limit,worker_response_byte_limit,item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
    ON braid.revision_derivations TO braid_deriver;
GRANT INSERT (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,claimant_instance_id,repair_target_derivation_id)
    ON braid.revision_derivation_attempts TO braid_deriver;
GRANT UPDATE (current_generation,current_attempt_id,selected_derivation_id) ON braid.revision_derivations TO braid_deriver;
GRANT UPDATE (completion_kind,failure_code,observation_present,backend_status,provenance_confirmed,provenance_organization_id,provenance_project_id,provenance_mapping_id,provenance_revision_id,provenance_native_digest,provenance_backend_id,provenance_engine_revision,provenance_backend_build,provenance_adapter_build,projection_coverage,document_item_count,document_diagnostic_count,coverage_gap_count,operation_diagnostic_count,is_valid,total_issue_count,emitted_issue_count,issues_truncated,validation_diagnostic_count,derived_digest,derived_byte_count)
    ON braid.revision_derivation_attempts TO braid_deriver;
