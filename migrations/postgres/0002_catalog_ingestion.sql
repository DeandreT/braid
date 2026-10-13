CREATE TABLE weft.mappings (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    id uuid NOT NULL,
    name text NOT NULL,
    description text NOT NULL,
    backend_id text NOT NULL,
    creator_principal_id uuid NOT NULL,
    owner_principal_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    version bigint NOT NULL DEFAULT 1,
    head_revision_id uuid NOT NULL,
    head_sequence bigint NOT NULL,
    CONSTRAINT mappings_pk PRIMARY KEY (organization_id, project_id, id),
    CONSTRAINT mappings_backend_key UNIQUE (organization_id, project_id, id, backend_id),
    CONSTRAINT mappings_project_fk FOREIGN KEY (organization_id, project_id)
        REFERENCES weft.projects(organization_id, id) ON DELETE RESTRICT,
    CONSTRAINT mappings_creator_fk FOREIGN KEY (organization_id, project_id, creator_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT mappings_owner_fk FOREIGN KEY (organization_id, project_id, owner_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT mappings_ids_check CHECK (id <> '00000000-0000-0000-0000-000000000000' AND head_revision_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT mappings_name_check CHECK (char_length(name) BETWEEN 1 AND 200 AND name = btrim(name) AND char_length(btrim(name)) > 0),
    CONSTRAINT mappings_description_check CHECK (char_length(description) <= 4000),
    CONSTRAINT mappings_backend_check CHECK (octet_length(backend_id) BETWEEN 1 AND 128 AND char_length(btrim(backend_id)) > 0),
    CONSTRAINT mappings_metadata_check CHECK (version > 0 AND head_sequence > 0 AND updated_at >= created_at)
);

CREATE TABLE weft.revision_ingestions (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    actor_principal_id uuid NOT NULL,
    operation_kind text NOT NULL,
    idempotency_key uuid NOT NULL,
    fingerprint_version integer NOT NULL,
    intent_fingerprint text NOT NULL,
    target_mapping_id uuid,
    future_mapping_id uuid NOT NULL,
    future_revision_id uuid NOT NULL,
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
    admission_policy_id text NOT NULL,
    effective_native_byte_limit bigint NOT NULL,
    display_filename text NOT NULL,
    create_name text,
    create_description text,
    head_condition text NOT NULL,
    expected_head_revision_id uuid,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    phase text NOT NULL DEFAULT 'reserved',
    lease_owner_id uuid,
    lease_token uuid,
    lease_generation bigint NOT NULL DEFAULT 0,
    lease_expires_at timestamp with time zone,
    claim_count integer NOT NULL DEFAULT 0,
    verified_stage_generation bigint,
    verified_stage_at timestamp with time zone,
    eligible_attempt_generation bigint,
    eligible_admission text,
    last_error text,
    completed_at timestamp with time zone,
    committed_revision_id uuid,
    CONSTRAINT revision_ingestions_pk PRIMARY KEY (organization_id, project_id, operation_id),
    CONSTRAINT revision_ingestions_project_fk FOREIGN KEY (organization_id, project_id)
        REFERENCES weft.projects(organization_id, id) ON DELETE RESTRICT,
    CONSTRAINT revision_ingestions_actor_fk FOREIGN KEY (organization_id, project_id, actor_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT revision_ingestions_target_fk FOREIGN KEY (organization_id, project_id, target_mapping_id, backend_id)
        REFERENCES weft.mappings(organization_id, project_id, id, backend_id) ON DELETE RESTRICT,
    CONSTRAINT revision_ingestions_revision_key UNIQUE (organization_id, project_id, future_mapping_id, future_revision_id),
    CONSTRAINT revision_ingestions_object_key UNIQUE (organization_id, project_id, future_mapping_id, future_revision_id, native_object_id),
    CONSTRAINT revision_ingestions_native_facts_key UNIQUE (organization_id, project_id, operation_id, future_mapping_id, future_revision_id, native_object_id, native_class, native_digest, native_byte_count, backend_id, logical_entry_document, dependency_manifest_id, actor_principal_id),
    CONSTRAINT revision_ingestions_ids_check CHECK (operation_id <> '00000000-0000-0000-0000-000000000000' AND idempotency_key <> '00000000-0000-0000-0000-000000000000' AND future_mapping_id <> '00000000-0000-0000-0000-000000000000' AND future_revision_id <> '00000000-0000-0000-0000-000000000000' AND native_object_id <> '00000000-0000-0000-0000-000000000000' AND (lease_owner_id IS NULL OR lease_owner_id <> '00000000-0000-0000-0000-000000000000') AND (lease_token IS NULL OR lease_token <> '00000000-0000-0000-0000-000000000000') AND (expected_head_revision_id IS NULL OR expected_head_revision_id <> '00000000-0000-0000-0000-000000000000')),
    CONSTRAINT revision_ingestions_intent_check CHECK (fingerprint_version = 1 AND intent_fingerprint ~ '^[0-9a-f]{64}$' AND operation_kind IN ('create', 'append') AND head_condition IN ('any', 'equal')),
    CONSTRAINT revision_ingestions_kind_check CHECK ((operation_kind = 'create' AND target_mapping_id IS NULL AND create_name IS NOT NULL AND create_description IS NOT NULL AND head_condition = 'any') OR (operation_kind = 'append' AND target_mapping_id = future_mapping_id AND target_mapping_id IS NOT NULL AND create_name IS NULL AND create_description IS NULL)),
    CONSTRAINT revision_ingestions_head_condition_check CHECK ((head_condition = 'any' AND expected_head_revision_id IS NULL) OR (head_condition = 'equal' AND operation_kind = 'append' AND expected_head_revision_id IS NOT NULL)),
    CONSTRAINT revision_ingestions_native_check CHECK (native_class = 'native' AND native_digest ~ '^[0-9a-f]{64}$' AND native_byte_count BETWEEN 1 AND 4194304 AND effective_native_byte_limit BETWEEN native_byte_count AND 4194304 AND logical_entry_document = 'entry.native.json' AND dependency_manifest_id = 'weft.dependencies.empty.v1'),
    CONSTRAINT revision_ingestions_profile_check CHECK (octet_length(backend_id) BETWEEN 1 AND 128 AND char_length(btrim(backend_id)) > 0 AND octet_length(engine_revision) BETWEEN 1 AND 256 AND char_length(btrim(engine_revision)) > 0 AND octet_length(backend_build) BETWEEN 1 AND 2048 AND char_length(btrim(backend_build)) > 0 AND octet_length(adapter_build) BETWEEN 1 AND 256 AND char_length(btrim(adapter_build)) > 0 AND octet_length(admission_policy_id) BETWEEN 1 AND 128 AND char_length(btrim(admission_policy_id)) > 0),
    CONSTRAINT revision_ingestions_filename_check CHECK (octet_length(display_filename) BETWEEN 1 AND 255 AND display_filename NOT IN ('.', '..') AND display_filename !~ '[/\\[:cntrl:]]'),
    CONSTRAINT revision_ingestions_create_metadata_check CHECK ((create_name IS NULL OR (char_length(create_name) BETWEEN 1 AND 200 AND create_name = btrim(create_name) AND char_length(btrim(create_name)) > 0)) AND (create_description IS NULL OR char_length(create_description) <= 4000)),
    CONSTRAINT revision_ingestions_phase_check CHECK (phase IN ('reserved', 'staged', 'ready', 'committed', 'rejected', 'head_conflict', 'attempt_exhausted')),
    CONSTRAINT revision_ingestions_claim_check CHECK (lease_generation BETWEEN 0 AND 8 AND claim_count = lease_generation AND num_nonnulls(lease_owner_id, lease_token, lease_expires_at) IN (0, 3) AND (lease_generation > 0 OR lease_owner_id IS NULL)),
    CONSTRAINT revision_ingestions_stage_check CHECK (num_nonnulls(verified_stage_generation, verified_stage_at) IN (0, 2) AND (verified_stage_generation IS NULL OR verified_stage_generation BETWEEN 1 AND lease_generation) AND (phase NOT IN ('staged', 'ready', 'committed', 'head_conflict') OR verified_stage_generation IS NOT NULL)),
    CONSTRAINT revision_ingestions_eligible_check CHECK (num_nonnulls(eligible_attempt_generation, eligible_admission) IN (0, 2) AND (eligible_attempt_generation IS NULL OR (eligible_attempt_generation BETWEEN 1 AND lease_generation AND eligible_admission IN ('eligible_valid', 'eligible_decoded_invalid'))) AND (phase NOT IN ('ready', 'committed', 'head_conflict') OR eligible_attempt_generation IS NOT NULL)),
    CONSTRAINT revision_ingestions_terminal_check CHECK ((completed_at IS NOT NULL) = (phase IN ('committed', 'rejected', 'head_conflict', 'attempt_exhausted')) AND (committed_revision_id IS NOT NULL) = (phase = 'committed') AND (committed_revision_id IS NULL OR committed_revision_id = future_revision_id) AND (completed_at IS NULL OR lease_owner_id IS NULL)),
    CONSTRAINT revision_ingestions_error_check CHECK (last_error IN ('needs_source', 'storage_integrity', 'storage_io', 'backend_unavailable', 'backend_failure', 'timed_out', 'canceled', 'isolation_failure', 'protocol_failure', 'reserved_profile_unavailable', 'evidence_limit'))
);

CREATE UNIQUE INDEX revision_ingestions_create_key ON weft.revision_ingestions (organization_id, project_id, actor_principal_id, idempotency_key) WHERE operation_kind = 'create';
CREATE UNIQUE INDEX revision_ingestions_append_key ON weft.revision_ingestions (organization_id, project_id, actor_principal_id, target_mapping_id, idempotency_key) WHERE operation_kind = 'append';
CREATE UNIQUE INDEX revision_ingestions_future_mapping_key ON weft.revision_ingestions (organization_id, project_id, future_mapping_id) WHERE operation_kind = 'create';

CREATE TABLE weft.revision_ingestion_attempts (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    generation bigint NOT NULL,
    claimant_instance_id uuid NOT NULL,
    claim_token uuid NOT NULL,
    started_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    verified_stage_at timestamp with time zone,
    finished_at timestamp with time zone,
    completion_kind text,
    backend_status text,
    admission_classification text,
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
    is_valid boolean,
    total_issue_count integer,
    emitted_issue_count integer,
    issues_truncated boolean,
    evidence_version text,
    evidence jsonb,
    reused_attempt_generation bigint,
    reused_admission text,
    last_error text,
    CONSTRAINT revision_ingestion_attempts_pk PRIMARY KEY (organization_id, project_id, operation_id, generation),
    CONSTRAINT revision_ingestion_attempts_ingestion_fk FOREIGN KEY (organization_id, project_id, operation_id)
        REFERENCES weft.revision_ingestions(organization_id, project_id, operation_id) ON DELETE RESTRICT,
    CONSTRAINT revision_ingestion_attempts_claim_key UNIQUE (organization_id, project_id, operation_id, generation, claimant_instance_id, claim_token),
    CONSTRAINT revision_ingestion_attempts_admission_key UNIQUE (organization_id, project_id, operation_id, generation, admission_classification),
    CONSTRAINT revision_ingestion_attempts_reused_fk FOREIGN KEY (organization_id, project_id, operation_id, reused_attempt_generation, reused_admission)
        REFERENCES weft.revision_ingestion_attempts(organization_id, project_id, operation_id, generation, admission_classification) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT revision_ingestion_attempts_claim_check CHECK (generation BETWEEN 1 AND 8 AND claimant_instance_id <> '00000000-0000-0000-0000-000000000000' AND claim_token <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT revision_ingestion_attempts_completion_check CHECK ((finished_at IS NULL) = (completion_kind IS NULL) AND (completion_kind IS NULL) = (admission_classification IS NULL) AND completion_kind IN ('validation', 'operational_failure', 'reused_evidence') AND admission_classification IN ('eligible_valid', 'eligible_decoded_invalid', 'rejected_native_decode', 'rejected_unsupported', 'rejected_resource_limit', 'retryable_failure', 'reused_evidence')),
    CONSTRAINT revision_ingestion_attempts_status_check CHECK (backend_status IN ('succeeded', 'invalid_mapping', 'unsupported_artifact', 'resource_limit', 'protocol_failure', 'backend_unavailable', 'backend_failure', 'timed_out', 'canceled', 'isolation_failure')),
    CONSTRAINT revision_ingestion_attempts_error_check CHECK (last_error IN ('needs_source', 'storage_integrity', 'storage_io', 'backend_unavailable', 'backend_failure', 'timed_out', 'canceled', 'isolation_failure', 'protocol_failure', 'reserved_profile_unavailable', 'evidence_limit')),
    CONSTRAINT revision_ingestion_attempts_provenance_check CHECK (num_nonnulls(provenance_confirmed, provenance_organization_id, provenance_project_id, provenance_mapping_id, provenance_revision_id, provenance_native_digest, provenance_backend_id, provenance_engine_revision, provenance_backend_build, provenance_adapter_build) IN (0, 10) AND (provenance_confirmed IS NULL OR (provenance_organization_id <> '00000000-0000-0000-0000-000000000000' AND provenance_project_id <> '00000000-0000-0000-0000-000000000000' AND provenance_mapping_id <> '00000000-0000-0000-0000-000000000000' AND provenance_revision_id <> '00000000-0000-0000-0000-000000000000' AND provenance_native_digest ~ '^[0-9a-f]{64}$' AND octet_length(provenance_backend_id) BETWEEN 1 AND 128 AND char_length(btrim(provenance_backend_id)) > 0 AND octet_length(provenance_engine_revision) BETWEEN 1 AND 256 AND char_length(btrim(provenance_engine_revision)) > 0 AND octet_length(provenance_backend_build) BETWEEN 1 AND 2048 AND char_length(btrim(provenance_backend_build)) > 0 AND octet_length(provenance_adapter_build) BETWEEN 1 AND 256 AND char_length(btrim(provenance_adapter_build)) > 0))),
    CONSTRAINT revision_ingestion_attempts_counts_check CHECK (total_issue_count BETWEEN 0 AND 32768 AND emitted_issue_count BETWEEN 0 AND 128 AND emitted_issue_count <= total_issue_count),
    CONSTRAINT revision_ingestion_attempts_evidence_size_check CHECK (evidence IS NULL OR octet_length(evidence::text) <= 8388608),
    CONSTRAINT revision_ingestion_attempts_shape_check CHECK ((
        (completion_kind IS NULL AND num_nonnulls(backend_status, provenance_confirmed, is_valid, total_issue_count, emitted_issue_count, issues_truncated, evidence_version, evidence, reused_attempt_generation, reused_admission, last_error) = 0)
        OR (completion_kind = 'validation' AND verified_stage_at IS NOT NULL AND backend_status IS NOT NULL AND provenance_confirmed IS NOT NULL AND total_issue_count IS NOT NULL AND emitted_issue_count IS NOT NULL AND issues_truncated IS NOT NULL AND evidence_version = 'weft.ingestion-validation.v1' AND evidence IS NOT NULL AND jsonb_typeof(evidence) = 'object' AND reused_attempt_generation IS NULL AND reused_admission IS NULL AND last_error IS NULL)
        OR (completion_kind = 'operational_failure' AND num_nonnulls(backend_status, is_valid, total_issue_count, emitted_issue_count, issues_truncated, evidence_version, evidence, reused_attempt_generation, reused_admission) = 0 AND last_error IS NOT NULL AND ((last_error = 'evidence_limit' AND admission_classification = 'rejected_resource_limit') OR (last_error <> 'evidence_limit' AND provenance_confirmed IS NULL AND admission_classification = 'retryable_failure')))
        OR (completion_kind = 'reused_evidence' AND verified_stage_at IS NOT NULL AND num_nonnulls(backend_status, provenance_confirmed, is_valid, total_issue_count, emitted_issue_count, issues_truncated, evidence_version, evidence, last_error) = 0 AND admission_classification = 'reused_evidence' AND reused_attempt_generation BETWEEN 1 AND generation - 1 AND reused_admission IN ('eligible_valid', 'eligible_decoded_invalid'))
    ) IS TRUE),
    CONSTRAINT revision_ingestion_attempts_envelope_check CHECK (evidence IS NULL OR (
        evidence->>'version' = evidence_version
        AND evidence->'scope'->>'organizationId' = provenance_organization_id::text
        AND evidence->'scope'->>'projectId' = provenance_project_id::text
        AND evidence->'scope'->>'mappingId' = provenance_mapping_id::text
        AND evidence->'scope'->>'revisionId' = provenance_revision_id::text
        AND evidence->>'nativeDigest' = provenance_native_digest
        AND evidence->'backendProfile'->>'backendId' = provenance_backend_id
        AND evidence->'backendProfile'->>'engineRevision' = provenance_engine_revision
        AND evidence->'backendProfile'->>'backendBuild' = provenance_backend_build
        AND evidence->'backendProfile'->>'adapterBuild' = provenance_adapter_build
        AND evidence->'confirmed' = to_jsonb(provenance_confirmed)
        AND evidence->>'status' = backend_status
        AND evidence->'isValid' = COALESCE(to_jsonb(is_valid), 'null'::jsonb)
        AND evidence->'totalIssueCount' = to_jsonb(total_issue_count)
        AND evidence->'emittedIssueCount' = to_jsonb(emitted_issue_count)
        AND evidence->'truncated' = to_jsonb(issues_truncated)
        AND CASE WHEN jsonb_typeof(evidence->'diagnostics') = 'array' THEN jsonb_array_length(evidence->'diagnostics') BETWEEN 0 AND 128 AND (is_valid IS NULL OR jsonb_array_length(evidence->'diagnostics') = emitted_issue_count) ELSE false END
    ) IS TRUE)
);

CREATE TABLE weft.mapping_revisions (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    id uuid NOT NULL,
    sequence bigint NOT NULL,
    native_object_id uuid NOT NULL,
    native_class text NOT NULL,
    native_digest text NOT NULL,
    native_byte_count bigint NOT NULL,
    logical_entry_document text NOT NULL,
    dependency_manifest_id text NOT NULL,
    backend_id text NOT NULL,
    creator_principal_id uuid NOT NULL,
    committed_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    ingestion_operation_id uuid NOT NULL,
    initial_attempt_generation bigint NOT NULL,
    initial_admission text NOT NULL,
    CONSTRAINT mapping_revisions_pk PRIMARY KEY (organization_id, project_id, mapping_id, id),
    CONSTRAINT mapping_revisions_sequence_key UNIQUE (organization_id, project_id, mapping_id, sequence),
    CONSTRAINT mapping_revisions_head_key UNIQUE (organization_id, project_id, mapping_id, id, sequence),
    CONSTRAINT mapping_revisions_backend_fk FOREIGN KEY (organization_id, project_id, mapping_id, backend_id)
        REFERENCES weft.mappings(organization_id, project_id, id, backend_id) ON DELETE RESTRICT,
    CONSTRAINT mapping_revisions_creator_fk FOREIGN KEY (organization_id, project_id, creator_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT mapping_revisions_native_facts_fk FOREIGN KEY (organization_id, project_id, ingestion_operation_id, mapping_id, id, native_object_id, native_class, native_digest, native_byte_count, backend_id, logical_entry_document, dependency_manifest_id, creator_principal_id)
        REFERENCES weft.revision_ingestions(organization_id, project_id, operation_id, future_mapping_id, future_revision_id, native_object_id, native_class, native_digest, native_byte_count, backend_id, logical_entry_document, dependency_manifest_id, actor_principal_id) ON DELETE RESTRICT,
    CONSTRAINT mapping_revisions_attempt_fk FOREIGN KEY (organization_id, project_id, ingestion_operation_id, initial_attempt_generation, initial_admission)
        REFERENCES weft.revision_ingestion_attempts(organization_id, project_id, operation_id, generation, admission_classification) ON DELETE RESTRICT,
    CONSTRAINT mapping_revisions_ids_check CHECK (id <> '00000000-0000-0000-0000-000000000000' AND native_object_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT mapping_revisions_sequence_check CHECK (sequence > 0 AND initial_attempt_generation BETWEEN 1 AND 8),
    CONSTRAINT mapping_revisions_native_check CHECK (native_class = 'native' AND native_digest ~ '^[0-9a-f]{64}$' AND native_byte_count BETWEEN 1 AND 4194304 AND logical_entry_document = 'entry.native.json' AND dependency_manifest_id = 'weft.dependencies.empty.v1'),
    CONSTRAINT mapping_revisions_admission_check CHECK (initial_admission IN ('eligible_valid', 'eligible_decoded_invalid'))
);

ALTER TABLE weft.mappings ADD CONSTRAINT mappings_head_fk FOREIGN KEY (organization_id, project_id, id, head_revision_id, head_sequence)
    REFERENCES weft.mapping_revisions(organization_id, project_id, mapping_id, id, sequence) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE weft.revision_ingestions ADD CONSTRAINT revision_ingestions_lease_fk FOREIGN KEY (organization_id, project_id, operation_id, lease_generation, lease_owner_id, lease_token)
    REFERENCES weft.revision_ingestion_attempts(organization_id, project_id, operation_id, generation, claimant_instance_id, claim_token) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE weft.revision_ingestions ADD CONSTRAINT revision_ingestions_eligible_fk FOREIGN KEY (organization_id, project_id, operation_id, eligible_attempt_generation, eligible_admission)
    REFERENCES weft.revision_ingestion_attempts(organization_id, project_id, operation_id, generation, admission_classification) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE weft.revision_ingestions ADD CONSTRAINT revision_ingestions_committed_fk FOREIGN KEY (organization_id, project_id, future_mapping_id, committed_revision_id)
    REFERENCES weft.mapping_revisions(organization_id, project_id, mapping_id, id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE weft.mappings ENABLE ROW LEVEL SECURITY;
CREATE POLICY mappings_organization_scope ON weft.mappings
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
ALTER TABLE weft.revision_ingestions ENABLE ROW LEVEL SECURITY;
CREATE POLICY revision_ingestions_organization_scope ON weft.revision_ingestions
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
ALTER TABLE weft.revision_ingestion_attempts ENABLE ROW LEVEL SECURITY;
CREATE POLICY revision_ingestion_attempts_organization_scope ON weft.revision_ingestion_attempts
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
ALTER TABLE weft.mapping_revisions ENABLE ROW LEVEL SECURITY;
CREATE POLICY mapping_revisions_organization_scope ON weft.mapping_revisions
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);

CREATE FUNCTION weft.guard_mappings_transition() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Mapping deletion is unavailable.' USING ERRCODE = '23514';
    ELSIF TG_OP = 'INSERT' THEN
        IF NEW.head_sequence <> 1 OR NEW.version <> 1 THEN
            RAISE EXCEPTION 'A mapping requires its initial committed head.' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF (to_jsonb(NEW) - ARRAY['head_revision_id','head_sequence','updated_at']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['head_revision_id','head_sequence','updated_at'])
            OR OLD.head_sequence = 9223372036854775807 OR NEW.head_sequence <> OLD.head_sequence + 1
            OR NEW.head_revision_id = OLD.head_revision_id OR NEW.updated_at < OLD.updated_at THEN
            RAISE EXCEPTION 'Mapping identity and monotonic head facts are immutable.' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$guard$;

CREATE FUNCTION weft.guard_revision_ingestions_transition() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Ingestion deletion is unavailable.' USING ERRCODE = '23514';
    ELSIF TG_OP = 'INSERT' THEN
        IF NEW.phase <> 'reserved' OR NEW.lease_generation <> 0 OR NEW.claim_count <> 0
            OR num_nonnulls(NEW.lease_owner_id,NEW.lease_token,NEW.lease_expires_at,NEW.verified_stage_generation,NEW.verified_stage_at,NEW.eligible_attempt_generation,NEW.eligible_admission,NEW.last_error,NEW.completed_at,NEW.committed_revision_id) <> 0 THEN
            RAISE EXCEPTION 'A reservation must start without a claim or publication.' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF (to_jsonb(NEW) - ARRAY['phase','lease_owner_id','lease_token','lease_generation','lease_expires_at','claim_count','verified_stage_generation','verified_stage_at','eligible_attempt_generation','eligible_admission','last_error','completed_at','committed_revision_id']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['phase','lease_owner_id','lease_token','lease_generation','lease_expires_at','claim_count','verified_stage_generation','verified_stage_at','eligible_attempt_generation','eligible_admission','last_error','completed_at','committed_revision_id']) THEN
        RAISE EXCEPTION 'Reserved ingestion intent and profile are immutable.' USING ERRCODE = '23514';
    END IF;
    IF OLD.phase IN ('committed','rejected','head_conflict','attempt_exhausted') AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Terminal ingestion receipts are immutable.' USING ERRCODE = '23514';
    END IF;
    IF NOT (NEW.phase = OLD.phase OR (OLD.phase = 'reserved' AND NEW.phase IN ('staged','rejected','attempt_exhausted')) OR (OLD.phase = 'staged' AND NEW.phase IN ('ready','rejected','attempt_exhausted')) OR (OLD.phase = 'ready' AND NEW.phase IN ('committed','head_conflict','rejected','attempt_exhausted'))) THEN
        RAISE EXCEPTION 'The ingestion phase cannot regress or skip publication.' USING ERRCODE = '23514';
    END IF;
    IF OLD.phase = 'ready' AND NEW.phase = 'rejected' AND NEW.last_error IS DISTINCT FROM 'evidence_limit' THEN
        RAISE EXCEPTION 'Ready evidence can only be rejected by the bounded evidence policy.' USING ERRCODE = '23514';
    END IF;
    IF NEW.lease_generation = OLD.lease_generation THEN
        IF ROW(NEW.lease_owner_id,NEW.lease_token,NEW.lease_expires_at) IS DISTINCT FROM ROW(OLD.lease_owner_id,OLD.lease_token,OLD.lease_expires_at)
            AND num_nonnulls(NEW.lease_owner_id,NEW.lease_token,NEW.lease_expires_at) <> 0 THEN
            RAISE EXCEPTION 'An existing lease cannot be replaced or extended.' USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.lease_generation <> OLD.lease_generation + 1 OR NEW.claim_count <> OLD.claim_count + 1
        OR NEW.lease_owner_id IS NULL OR OLD.completed_at IS NOT NULL
        OR (OLD.lease_expires_at IS NOT NULL AND OLD.lease_expires_at > clock_timestamp()) THEN
        RAISE EXCEPTION 'A claim requires a released or expired predecessor.' USING ERRCODE = '23514';
    END IF;
    IF OLD.eligible_attempt_generation IS NOT NULL AND ROW(NEW.eligible_attempt_generation,NEW.eligible_admission) IS DISTINCT FROM ROW(OLD.eligible_attempt_generation,OLD.eligible_admission) THEN
        RAISE EXCEPTION 'Original eligible evidence cannot be relabeled.' USING ERRCODE = '23514';
    END IF;
    IF OLD.verified_stage_generation IS NOT NULL AND (NEW.verified_stage_generation IS NULL OR NEW.verified_stage_generation < OLD.verified_stage_generation) THEN
        RAISE EXCEPTION 'Verified-stage progress cannot regress.' USING ERRCODE = '23514';
    END IF;
    IF NEW.verified_stage_generation IS DISTINCT FROM OLD.verified_stage_generation AND NEW.verified_stage_generation <> NEW.lease_generation THEN
        RAISE EXCEPTION 'Verified-stage progress belongs to the current claim.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$guard$;

CREATE FUNCTION weft.guard_revision_ingestion_attempts_transition() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
DECLARE
    reserved weft.revision_ingestions%ROWTYPE;
    matched boolean;
    expected_class text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Attempt deletion is unavailable.' USING ERRCODE = '23514';
    ELSIF TG_OP = 'INSERT' THEN
        IF num_nonnulls(NEW.verified_stage_at,NEW.finished_at,NEW.completion_kind,NEW.backend_status,NEW.admission_classification,NEW.provenance_confirmed,NEW.is_valid,NEW.total_issue_count,NEW.emitted_issue_count,NEW.issues_truncated,NEW.evidence_version,NEW.evidence,NEW.reused_attempt_generation,NEW.reused_admission,NEW.last_error) <> 0 THEN
            RAISE EXCEPTION 'An attempt must begin without invented progress or evidence.' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.organization_id,NEW.project_id,NEW.operation_id,NEW.generation,NEW.claimant_instance_id,NEW.claim_token,NEW.started_at) IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.operation_id,OLD.generation,OLD.claimant_instance_id,OLD.claim_token,OLD.started_at) THEN
        RAISE EXCEPTION 'An attempt claim header is immutable.' USING ERRCODE = '23514';
    END IF;
    IF OLD.finished_at IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'Completed attempt evidence is immutable.' USING ERRCODE = '23514';
    END IF;
    IF OLD.verified_stage_at IS NOT NULL AND NEW.verified_stage_at IS DISTINCT FROM OLD.verified_stage_at THEN
        RAISE EXCEPTION 'Attempt stage verification is immutable.' USING ERRCODE = '23514';
    END IF;
    IF NEW.finished_at IS NULL OR OLD.finished_at IS NOT NULL THEN
        RETURN NEW;
    END IF;
    IF NEW.completion_kind = 'validation' THEN
        SELECT * INTO STRICT reserved FROM weft.revision_ingestions
            WHERE organization_id = NEW.organization_id AND project_id = NEW.project_id AND operation_id = NEW.operation_id;
        matched := ROW(NEW.provenance_organization_id,NEW.provenance_project_id,NEW.provenance_mapping_id,NEW.provenance_revision_id,NEW.provenance_native_digest,NEW.provenance_backend_id,NEW.provenance_engine_revision,NEW.provenance_backend_build,NEW.provenance_adapter_build)
            = ROW(reserved.organization_id,reserved.project_id,reserved.future_mapping_id,reserved.future_revision_id,reserved.native_digest,reserved.backend_id,reserved.engine_revision,reserved.backend_build,reserved.adapter_build)
            AND NOT (NEW.evidence->'diagnostics' @> '[{"code":"ferrule.digestMismatch"}]'::jsonb);
        IF matched AND NEW.provenance_confirmed IS TRUE AND NEW.backend_status = 'succeeded' AND NEW.is_valid IS TRUE THEN
            expected_class := 'eligible_valid';
        ELSIF matched AND NEW.provenance_confirmed IS TRUE AND NEW.backend_status = 'invalid_mapping' AND NEW.is_valid IS FALSE THEN
            expected_class := 'eligible_decoded_invalid';
        ELSIF matched AND NEW.provenance_confirmed IS TRUE AND NEW.backend_status = 'invalid_mapping' AND NEW.is_valid IS NULL AND NEW.evidence->'diagnostics' @> '[{"code":"ferrule.nativeDecodeFailed"}]'::jsonb THEN
            expected_class := 'rejected_native_decode';
        ELSIF matched AND NEW.backend_status = 'unsupported_artifact' AND NEW.is_valid IS NULL THEN
            expected_class := 'rejected_unsupported';
        ELSIF matched AND NEW.backend_status = 'resource_limit' AND NEW.is_valid IS NULL THEN
            expected_class := 'rejected_resource_limit';
        ELSE
            expected_class := 'retryable_failure';
        END IF;
    ELSIF NEW.completion_kind = 'operational_failure' THEN
        expected_class := CASE WHEN NEW.last_error = 'evidence_limit' THEN 'rejected_resource_limit' ELSE 'retryable_failure' END;
    ELSE
        expected_class := 'reused_evidence';
    END IF;
    IF NEW.admission_classification IS DISTINCT FROM expected_class THEN
        RAISE EXCEPTION 'Attempt classification must agree with exact observed evidence.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$guard$;

CREATE FUNCTION weft.guard_mapping_revisions_transition() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Committed revision facts are immutable.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$guard$;

CREATE TRIGGER mappings_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.mappings FOR EACH ROW EXECUTE FUNCTION weft.guard_mappings_transition();
CREATE TRIGGER revision_ingestions_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.revision_ingestions FOR EACH ROW EXECUTE FUNCTION weft.guard_revision_ingestions_transition();
CREATE TRIGGER revision_ingestion_attempts_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.revision_ingestion_attempts FOR EACH ROW EXECUTE FUNCTION weft.guard_revision_ingestion_attempts_transition();
CREATE TRIGGER mapping_revisions_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.mapping_revisions FOR EACH ROW EXECUTE FUNCTION weft.guard_mapping_revisions_transition();

REVOKE ALL ON FUNCTION weft.guard_mappings_transition(), weft.guard_revision_ingestions_transition(), weft.guard_revision_ingestion_attempts_transition(), weft.guard_mapping_revisions_transition() FROM PUBLIC, weft_runtime, weft_ingest;
REVOKE ALL ON weft.mappings, weft.revision_ingestions, weft.revision_ingestion_attempts, weft.mapping_revisions FROM PUBLIC, weft_runtime, weft_ingest;
GRANT USAGE ON SCHEMA weft TO weft_ingest;
GRANT SELECT ON weft.organizations, weft.principals, weft.organization_memberships, weft.projects, weft.project_memberships, weft.mappings, weft.revision_ingestions, weft.revision_ingestion_attempts, weft.mapping_revisions TO weft_ingest;
GRANT INSERT ON weft.mappings, weft.revision_ingestions, weft.revision_ingestion_attempts, weft.mapping_revisions TO weft_ingest;
GRANT UPDATE (head_revision_id, head_sequence, updated_at) ON weft.mappings TO weft_ingest;
GRANT UPDATE (phase, lease_owner_id, lease_token, lease_generation, lease_expires_at, claim_count, verified_stage_generation, verified_stage_at, eligible_attempt_generation, eligible_admission, last_error, completed_at, committed_revision_id) ON weft.revision_ingestions TO weft_ingest;
GRANT UPDATE (verified_stage_at, finished_at, completion_kind, backend_status, admission_classification, provenance_confirmed, provenance_organization_id, provenance_project_id, provenance_mapping_id, provenance_revision_id, provenance_native_digest, provenance_backend_id, provenance_engine_revision, provenance_backend_build, provenance_adapter_build, is_valid, total_issue_count, emitted_issue_count, issues_truncated, evidence_version, evidence, reused_attempt_generation, reused_admission, last_error) ON weft.revision_ingestion_attempts TO weft_ingest;
