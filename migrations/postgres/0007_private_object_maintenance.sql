-- Metadata fences only. No physical object is deleted or adopted by this migration.
DO $braid_require_maintenance_role$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname = 'braid_maintenance';
    IF NOT FOUND OR NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls
        OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles x WHERE x.oid <> r.oid AND pg_catalog.pg_has_role(r.oid,x.oid,'MEMBER'))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
            AND (n.nspowner = r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE')))
        OR pg_catalog.has_schema_privilege(r.oid,'braid','USAGE')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'braid' AND c.relkind IN ('r','p','v','m','f')
            AND (c.relowner = r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid = f.pronamespace
            WHERE n.nspname = 'braid' AND pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE'))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'braid' AND CASE WHEN c.relkind = 'S' THEN pg_catalog.has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') ELSE false END)
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
            WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'braid')) AND a.grantee IN (0,r.oid)) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Maintenance requires an explicitly provisioned CONNECT-only login.';
    END IF;
END;
$braid_require_maintenance_role$;

LOCK TABLE braid.mapping_revisions,braid.revision_ingestions,braid.revision_ingestion_attempts,
    braid.revision_derivations,braid.revision_derivation_attempts IN SHARE ROW EXCLUSIVE MODE;

CREATE TABLE braid.private_object_publications (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    object_class text NOT NULL,
    object_id uuid NOT NULL,
    publication_id uuid NOT NULL,
    root_id uuid NOT NULL,
    temp_id uuid NOT NULL,
    format text NOT NULL,
    owner_kind text NOT NULL,
    actor_principal_id uuid NOT NULL,
    ingestion_operation_id uuid,
    cache_entry_id uuid,
    derivation_id uuid,
    native_object_id uuid,
    candidate_object_id uuid,
    owner_generation bigint NOT NULL,
    owner_instance_id uuid NOT NULL,
    owner_token uuid NOT NULL,
    owner_lease_expires_at timestamp with time zone NOT NULL,
    absence_record_digest text NOT NULL,
    registered_at timestamp with time zone NOT NULL,
    control_namespace_id uuid,
    linux_boot_id uuid,
    mount_namespace_device numeric(20,0),
    mount_namespace_inode numeric(20,0),
    effective_user_id bigint,
    file_inode numeric(20,0),
    file_device_major bigint,
    file_device_minor bigint,
    file_mount_id numeric(20,0),
    directory_inode numeric(20,0),
    directory_device_major bigint,
    directory_device_minor bigint,
    directory_mount_id numeric(20,0),
    bound_record_digest text,
    bound_at timestamp with time zone,
    CONSTRAINT private_object_publications_pk PRIMARY KEY (organization_id,project_id,publication_id),
    CONSTRAINT private_object_publications_natural_key UNIQUE (root_id,organization_id,project_id,mapping_id,revision_id,object_class,object_id,temp_id),
    CONSTRAINT private_object_publications_target_key UNIQUE (organization_id,project_id,mapping_id,revision_id,object_class,object_id,publication_id),
    CONSTRAINT private_object_publications_native_object_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,native_object_id)
        REFERENCES braid.revision_ingestions(organization_id,project_id,future_mapping_id,future_revision_id,native_object_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_publications_ingestion_fk FOREIGN KEY (organization_id,project_id,ingestion_operation_id)
        REFERENCES braid.revision_ingestions(organization_id,project_id,operation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_publications_ingestion_claim_fk FOREIGN KEY (organization_id,project_id,ingestion_operation_id,owner_generation,owner_instance_id,owner_token)
        REFERENCES braid.revision_ingestion_attempts(organization_id,project_id,operation_id,generation,claimant_instance_id,claim_token) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_publications_report_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id,owner_generation,derivation_id)
        REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_publications_candidate_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,candidate_object_id)
        REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,candidate_object_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_publications_ids_check CHECK (organization_id <> '00000000-0000-0000-0000-000000000000' AND project_id <> '00000000-0000-0000-0000-000000000000' AND mapping_id <> '00000000-0000-0000-0000-000000000000' AND revision_id <> '00000000-0000-0000-0000-000000000000' AND object_id <> '00000000-0000-0000-0000-000000000000' AND publication_id <> '00000000-0000-0000-0000-000000000000' AND root_id <> '00000000-0000-0000-0000-000000000000' AND temp_id <> '00000000-0000-0000-0000-000000000000' AND actor_principal_id <> '00000000-0000-0000-0000-000000000000' AND owner_instance_id <> '00000000-0000-0000-0000-000000000000' AND owner_token <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT private_object_publications_origin_check CHECK (((owner_kind = 'native_ingestion' AND object_class = 'native' AND ingestion_operation_id IS NOT NULL AND cache_entry_id IS NULL AND derivation_id IS NULL AND native_object_id = object_id AND native_object_id IS NOT NULL AND candidate_object_id IS NULL) OR (owner_kind = 'report_attempt' AND object_class = 'derived' AND ingestion_operation_id IS NULL AND cache_entry_id IS NOT NULL AND derivation_id IS NOT NULL AND native_object_id IS NULL AND candidate_object_id = object_id AND candidate_object_id IS NOT NULL)) IS TRUE),
    CONSTRAINT private_object_publications_claim_check CHECK (owner_generation > 0 AND (owner_kind <> 'native_ingestion' OR owner_generation <= 8) AND registered_at < owner_lease_expires_at),
    CONSTRAINT private_object_publications_format_check CHECK (format = 'braid.private-publication.linux.v2' AND absence_record_digest ~ '^[0-9a-f]{64}$'),
    CONSTRAINT private_object_publications_binding_check CHECK (num_nonnulls(control_namespace_id,linux_boot_id,mount_namespace_device,mount_namespace_inode,effective_user_id,file_inode,file_device_major,file_device_minor,file_mount_id,directory_inode,directory_device_major,directory_device_minor,directory_mount_id,bound_record_digest,bound_at) IN (0,15) AND (bound_at IS NULL OR (bound_at >= registered_at AND bound_at < owner_lease_expires_at AND control_namespace_id <> '00000000-0000-0000-0000-000000000000' AND linux_boot_id <> '00000000-0000-0000-0000-000000000000' AND bound_record_digest ~ '^[0-9a-f]{64}$' AND file_device_major = directory_device_major AND file_device_minor = directory_device_minor AND file_mount_id = directory_mount_id AND file_inode <> directory_inode))),
    CONSTRAINT private_object_publications_uint64_check CHECK ((mount_namespace_device IS NULL OR mount_namespace_device BETWEEN 0 AND 18446744073709551615) AND (mount_namespace_inode IS NULL OR mount_namespace_inode BETWEEN 1 AND 18446744073709551615) AND (file_inode IS NULL OR file_inode BETWEEN 1 AND 18446744073709551615) AND (file_mount_id IS NULL OR file_mount_id BETWEEN 1 AND 18446744073709551615) AND (directory_inode IS NULL OR directory_inode BETWEEN 1 AND 18446744073709551615) AND (directory_mount_id IS NULL OR directory_mount_id BETWEEN 1 AND 18446744073709551615)),
    CONSTRAINT private_object_publications_uint32_check CHECK ((effective_user_id IS NULL OR effective_user_id BETWEEN 0 AND 4294967295) AND (file_device_major IS NULL OR file_device_major BETWEEN 0 AND 4294967295) AND (file_device_minor IS NULL OR file_device_minor BETWEEN 0 AND 4294967295) AND (directory_device_major IS NULL OR directory_device_major BETWEEN 0 AND 4294967295) AND (directory_device_minor IS NULL OR directory_device_minor BETWEEN 0 AND 4294967295))
);

CREATE TABLE braid.private_object_retirements (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    object_class text NOT NULL,
    object_id uuid NOT NULL,
    retirement_id uuid NOT NULL,
    target_kind text NOT NULL,
    publication_id uuid,
    origin_kind text NOT NULL,
    ingestion_operation_id uuid,
    cache_entry_id uuid,
    derivation_id uuid,
    origin_generation bigint,
    native_object_id uuid,
    candidate_object_id uuid,
    reason text NOT NULL,
    eligibility_origin_at timestamp with time zone NOT NULL,
    retired_at timestamp with time zone NOT NULL,
    policy_id text NOT NULL DEFAULT 'braid.orphan-maintenance.v1',
    current_generation bigint NOT NULL DEFAULT 0,
    current_attempt_id uuid,
    completed_generation bigint,
    completed_attempt_id uuid,
    completed_success boolean NOT NULL DEFAULT true,
    CONSTRAINT private_object_retirements_pk PRIMARY KEY (organization_id,project_id,retirement_id),
    CONSTRAINT private_object_retirements_target_key UNIQUE (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id),
    CONSTRAINT private_object_retirements_publication_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,object_class,object_id,publication_id)
        REFERENCES braid.private_object_publications(organization_id,project_id,mapping_id,revision_id,object_class,object_id,publication_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_retirements_ingestion_fk FOREIGN KEY (organization_id,project_id,ingestion_operation_id)
        REFERENCES braid.revision_ingestions(organization_id,project_id,operation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_retirements_native_object_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,native_object_id)
        REFERENCES braid.revision_ingestions(organization_id,project_id,future_mapping_id,future_revision_id,native_object_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_retirements_report_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,cache_entry_id,origin_generation,derivation_id)
        REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_retirements_candidate_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,candidate_object_id)
        REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,candidate_object_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_retirements_ids_check CHECK (organization_id <> '00000000-0000-0000-0000-000000000000' AND project_id <> '00000000-0000-0000-0000-000000000000' AND mapping_id <> '00000000-0000-0000-0000-000000000000' AND revision_id <> '00000000-0000-0000-0000-000000000000' AND object_id <> '00000000-0000-0000-0000-000000000000' AND retirement_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT private_object_retirements_target_check CHECK (((target_kind = 'final_object' AND publication_id IS NULL) OR (target_kind = 'tracked_temporary' AND publication_id IS NOT NULL)) IS TRUE),
    CONSTRAINT private_object_retirements_origin_check CHECK (((origin_kind = 'native_ingestion' AND object_class = 'native' AND ingestion_operation_id IS NOT NULL AND cache_entry_id IS NULL AND derivation_id IS NULL AND origin_generation IS NULL AND native_object_id IS NOT NULL AND native_object_id = object_id AND candidate_object_id IS NULL) OR (origin_kind = 'report_attempt' AND object_class = 'derived' AND ingestion_operation_id IS NULL AND cache_entry_id IS NOT NULL AND derivation_id IS NOT NULL AND origin_generation > 0 AND native_object_id IS NULL AND candidate_object_id IS NOT NULL AND candidate_object_id = object_id)) IS TRUE),
    CONSTRAINT private_object_retirements_reason_check CHECK (((target_kind = 'tracked_temporary' AND reason = 'abandoned_tracked_temporary') OR (target_kind = 'final_object' AND origin_kind = 'native_ingestion' AND reason = 'terminal_native_stage') OR (target_kind = 'final_object' AND origin_kind = 'report_attempt' AND reason IN ('failed_derived_candidate','expired_derived_candidate'))) IS TRUE),
    CONSTRAINT private_object_retirements_progress_check CHECK (completed_success AND current_generation BETWEEN 0 AND 8 AND ((current_generation = 0 AND current_attempt_id IS NULL AND completed_attempt_id IS NULL AND completed_generation IS NULL) OR (current_generation > 0 AND current_attempt_id IS NOT NULL)) AND num_nonnulls(completed_generation,completed_attempt_id) IN (0,2) AND (completed_generation IS NULL OR completed_generation = current_generation)),
    CONSTRAINT private_object_retirements_policy_check CHECK (policy_id = 'braid.orphan-maintenance.v1' AND eligibility_origin_at + interval '24 hours' <= retired_at)
);

CREATE TABLE braid.private_object_cleanup_attempts (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    object_class text NOT NULL,
    object_id uuid NOT NULL,
    retirement_id uuid NOT NULL,
    generation bigint NOT NULL,
    attempt_id uuid NOT NULL,
    instance_id uuid NOT NULL,
    lease_token uuid NOT NULL,
    started_at timestamp with time zone NOT NULL,
    lease_expires_at timestamp with time zone NOT NULL,
    finished_at timestamp with time zone,
    disposition text,
    success boolean NOT NULL DEFAULT false,
    CONSTRAINT private_object_cleanup_attempts_pk PRIMARY KEY (organization_id,project_id,retirement_id,generation),
    CONSTRAINT private_object_cleanup_attempts_id_key UNIQUE (organization_id,project_id,attempt_id),
    CONSTRAINT private_object_cleanup_attempts_current_key UNIQUE (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,attempt_id),
    CONSTRAINT private_object_cleanup_attempts_success_key UNIQUE (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,attempt_id,success),
    CONSTRAINT private_object_cleanup_attempts_retirement_fk FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id)
        REFERENCES braid.private_object_retirements(organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CONSTRAINT private_object_cleanup_attempts_ids_check CHECK (attempt_id <> '00000000-0000-0000-0000-000000000000' AND instance_id <> '00000000-0000-0000-0000-000000000000' AND lease_token <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT private_object_cleanup_attempts_generation_check CHECK (generation BETWEEN 1 AND 8),
    CONSTRAINT private_object_cleanup_attempts_lease_check CHECK (lease_expires_at = started_at + interval '60 seconds'),
    CONSTRAINT private_object_cleanup_attempts_completion_check CHECK (((finished_at IS NULL AND disposition IS NULL AND NOT success) OR (finished_at >= started_at AND finished_at < lease_expires_at AND disposition IN ('deleted','missing','canceled','deadline','storage_io','unsafe_storage') AND success = (disposition IN ('deleted','missing')))) IS TRUE)
);

ALTER TABLE braid.private_object_retirements ADD CONSTRAINT private_object_retirements_current_fk
    FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,current_generation,current_attempt_id)
    REFERENCES braid.private_object_cleanup_attempts(organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,attempt_id) ON DELETE RESTRICT ON UPDATE RESTRICT;
ALTER TABLE braid.private_object_retirements ADD CONSTRAINT private_object_retirements_completed_fk
    FOREIGN KEY (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,completed_generation,completed_attempt_id,completed_success)
    REFERENCES braid.private_object_cleanup_attempts(organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,attempt_id,success) ON DELETE RESTRICT ON UPDATE RESTRICT;

CREATE UNIQUE INDEX private_object_retirements_final_key ON braid.private_object_retirements
    (organization_id,project_id,mapping_id,revision_id,object_class,object_id) WHERE target_kind = 'final_object';
CREATE UNIQUE INDEX private_object_retirements_temporary_key ON braid.private_object_retirements
    (organization_id,project_id,mapping_id,revision_id,object_class,object_id,publication_id) WHERE target_kind = 'tracked_temporary';
CREATE INDEX private_object_retirements_due_scan ON braid.private_object_retirements
    (organization_id,project_id,retired_at,mapping_id,revision_id,object_class,object_id,publication_id) WHERE completed_attempt_id IS NULL AND current_generation < 8;
CREATE INDEX revision_ingestions_cleanup_scan ON braid.revision_ingestions
    (organization_id,project_id,completed_at,future_mapping_id,future_revision_id,native_object_id) WHERE phase IN ('rejected','head_conflict','attempt_exhausted');
CREATE INDEX revision_derivation_attempts_cleanup_scan ON braid.revision_derivation_attempts
    (organization_id,project_id,(COALESCE(finished_at,lease_expires_at)),mapping_id,revision_id,candidate_object_id) WHERE NOT result_usable;
CREATE INDEX private_object_publications_temporary_scan ON braid.private_object_publications
    (organization_id,project_id,(greatest(registered_at,bound_at,owner_lease_expires_at)),mapping_id,revision_id,object_class,object_id,publication_id);

ALTER TABLE braid.private_object_publications ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.private_object_retirements ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.private_object_cleanup_attempts ENABLE ROW LEVEL SECURITY;
CREATE POLICY private_object_publications_organization_scope ON braid.private_object_publications
    USING (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND (current_user <> 'braid_maintenance' OR project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid))
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND (current_user <> 'braid_maintenance' OR project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid));
CREATE POLICY private_object_retirements_organization_scope ON braid.private_object_retirements
    USING (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND (current_user <> 'braid_maintenance' OR project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid))
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND (current_user <> 'braid_maintenance' OR project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid));
CREATE POLICY private_object_cleanup_attempts_organization_scope ON braid.private_object_cleanup_attempts
    USING (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id',true),'')::uuid AND project_id = NULLIF(current_setting('braid.project_id',true),'')::uuid);

CREATE FUNCTION braid.assert_private_object_context_v1(p_organization_id uuid, p_project_id uuid, p_entry boolean) RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_assert_private_object_context_v1$
BEGIN
    IF p_organization_id IS NULL OR p_project_id IS NULL
        OR p_organization_id = '00000000-0000-0000-0000-000000000000' OR p_project_id = '00000000-0000-0000-0000-000000000000'
        OR pg_catalog.current_setting('transaction_isolation') <> 'read committed'
        OR pg_catalog.current_setting('session_replication_role') <> 'origin'
        OR pg_catalog.current_setting('server_encoding') <> 'UTF8'
        OR pg_catalog.current_setting('client_encoding') <> 'UTF8'
        OR pg_catalog.current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Private object metadata requires the trusted transaction profile.';
    END IF;
    IF p_entry AND (session_user <> 'braid_maintenance'
        OR NULLIF(pg_catalog.current_setting('braid.organization_id',true),'')::uuid IS DISTINCT FROM p_organization_id
        OR NULLIF(pg_catalog.current_setting('braid.project_id',true),'')::uuid IS DISTINCT FROM p_project_id) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Maintenance requires its exact bound project.';
    END IF;
    IF session_user IN ('braid_ingest','braid_deriver')
        AND NULLIF(pg_catalog.current_setting('braid.organization_id',true),'')::uuid IS DISTINCT FROM p_organization_id THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Publication requires its exact bound organization.';
    END IF;
END;
$braid_assert_private_object_context_v1$;

CREATE FUNCTION braid.lock_private_object_origin_v1(p_organization_id uuid, p_project_id uuid, p_mapping_id uuid, p_revision_id uuid, p_object_class text, p_object_id uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_lock_private_object_origin_v1$
DECLARE discovered braid.revision_derivation_attempts%ROWTYPE;
BEGIN
    PERFORM braid.assert_private_object_context_v1(p_organization_id,p_project_id,false);
    IF p_object_class = 'native' THEN
        PERFORM 1 FROM braid.revision_ingestions i WHERE i.organization_id = p_organization_id AND i.project_id = p_project_id
            AND i.future_mapping_id = p_mapping_id AND i.future_revision_id = p_revision_id AND i.native_object_id = p_object_id
            AND i.native_class = 'native' FOR UPDATE;
        RETURN FOUND;
    ELSIF p_object_class = 'derived' THEN
        SELECT * INTO discovered FROM braid.revision_derivation_attempts a
            WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id AND a.mapping_id = p_mapping_id
                AND a.revision_id = p_revision_id AND a.candidate_object_id = p_object_id AND a.derived_class = 'derived';
        IF NOT FOUND THEN RETURN false; END IF;
        PERFORM 1 FROM braid.revision_derivations k WHERE k.organization_id = p_organization_id AND k.project_id = p_project_id
            AND k.mapping_id = p_mapping_id AND k.revision_id = p_revision_id AND k.cache_entry_id = discovered.cache_entry_id FOR UPDATE;
        IF NOT FOUND THEN RETURN false; END IF;
        -- A separate post-wait statement; discovery is not an authorization snapshot.
        PERFORM 1 FROM braid.revision_derivation_attempts a WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id
            AND a.mapping_id = p_mapping_id AND a.revision_id = p_revision_id AND a.cache_entry_id = discovered.cache_entry_id
            AND a.generation = discovered.generation AND a.derivation_id = discovered.derivation_id AND a.candidate_object_id = p_object_id;
        RETURN FOUND;
    END IF;
    RETURN false;
END;
$braid_lock_private_object_origin_v1$;

CREATE FUNCTION braid.private_publication_owner_live_v1(p_publication braid.private_object_publications) RETURNS boolean
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_private_publication_owner_live_v1$
DECLARE matched boolean;
BEGIN
    IF p_publication.owner_kind = 'native_ingestion' THEN
        SELECT EXISTS (SELECT 1 FROM braid.revision_ingestions i
            JOIN braid.revision_ingestion_attempts a ON a.organization_id = i.organization_id AND a.project_id = i.project_id
                AND a.operation_id = i.operation_id AND a.generation = i.lease_generation
            JOIN braid.organizations o ON o.id = i.organization_id
            JOIN braid.projects p ON p.organization_id = i.organization_id AND p.id = i.project_id
            JOIN braid.principals u ON u.id = i.actor_principal_id AND u.active
            JOIN braid.organization_memberships om ON om.organization_id = i.organization_id AND om.principal_id = u.id AND om.active
            JOIN braid.project_memberships pm ON pm.organization_id = i.organization_id AND pm.project_id = i.project_id AND pm.principal_id = u.id
                AND pm.active AND pm.role IN ('publisher','administrator')
            WHERE i.organization_id = p_publication.organization_id AND i.project_id = p_publication.project_id
                AND i.operation_id = p_publication.ingestion_operation_id AND i.future_mapping_id = p_publication.mapping_id
                AND i.future_revision_id = p_publication.revision_id AND i.native_object_id = p_publication.object_id
                AND i.actor_principal_id = p_publication.actor_principal_id AND i.completed_at IS NULL
                AND i.lease_generation = p_publication.owner_generation AND i.lease_owner_id = p_publication.owner_instance_id
                AND i.lease_token = p_publication.owner_token AND i.lease_expires_at = p_publication.owner_lease_expires_at
                AND a.claimant_instance_id = p_publication.owner_instance_id AND a.claim_token = p_publication.owner_token
                AND i.lease_expires_at > pg_catalog.clock_timestamp()) INTO matched;
    ELSE
        SELECT EXISTS (SELECT 1 FROM braid.revision_derivation_attempts a
            JOIN braid.revision_derivations k ON k.organization_id = a.organization_id AND k.project_id = a.project_id
                AND k.mapping_id = a.mapping_id AND k.revision_id = a.revision_id AND k.cache_entry_id = a.cache_entry_id
            JOIN braid.organizations o ON o.id = a.organization_id
            JOIN braid.projects p ON p.organization_id = a.organization_id AND p.id = a.project_id
            JOIN braid.principals u ON u.id = a.actor_principal_id AND u.active
            JOIN braid.organization_memberships om ON om.organization_id = a.organization_id AND om.principal_id = u.id AND om.active
            JOIN braid.project_memberships pm ON pm.organization_id = a.organization_id AND pm.project_id = a.project_id AND pm.principal_id = u.id
                AND pm.active AND pm.role IN ('viewer','runner','publisher','administrator')
            JOIN braid.mapping_revisions r ON r.organization_id = k.organization_id AND r.project_id = k.project_id
                AND r.mapping_id = k.mapping_id AND r.id = k.revision_id AND r.native_object_id = k.native_object_id
                AND r.native_class = k.native_class AND r.native_digest = k.native_digest AND r.native_byte_count = k.native_byte_count
                AND r.backend_id = k.backend_id AND r.logical_entry_document = k.logical_entry_document AND r.dependency_manifest_id = k.dependency_manifest_id
            WHERE a.organization_id = p_publication.organization_id AND a.project_id = p_publication.project_id
                AND a.mapping_id = p_publication.mapping_id AND a.revision_id = p_publication.revision_id
                AND a.cache_entry_id = p_publication.cache_entry_id AND a.derivation_id = p_publication.derivation_id
                AND a.generation = p_publication.owner_generation AND a.candidate_object_id = p_publication.object_id
                AND a.actor_principal_id = p_publication.actor_principal_id AND a.claimant_instance_id = p_publication.owner_instance_id
                AND a.claim_token = p_publication.owner_token AND a.lease_expires_at = p_publication.owner_lease_expires_at
                AND a.finished_at IS NULL AND k.current_generation = a.generation AND k.current_attempt_id = a.derivation_id
                AND k.selected_derivation_id IS NULL AND a.lease_expires_at > pg_catalog.clock_timestamp()) INTO matched;
    END IF;
    RETURN matched IS TRUE;
END;
$braid_private_publication_owner_live_v1$;

CREATE FUNCTION braid.private_object_cleanup_safety_v1(p_organization_id uuid, p_project_id uuid, p_mapping_id uuid, p_revision_id uuid, p_object_class text, p_object_id uuid, p_publication_id uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_private_object_cleanup_safety_v1$
DECLARE i braid.revision_ingestions%ROWTYPE;
    a braid.revision_derivation_attempts%ROWTYPE;
    p braid.private_object_publications%ROWTYPE;
    at_time timestamp with time zone;
    origin_at timestamp with time zone;
    reason_value text;
    protected_value boolean := false;
    live_owner boolean;
BEGIN
    IF NOT braid.lock_private_object_origin_v1(p_organization_id,p_project_id,p_mapping_id,p_revision_id,p_object_class,p_object_id) THEN
        RETURN pg_catalog.jsonb_build_object('eligible',false,'protected',true);
    END IF;
    -- All protection reads below are fresh statements after the shared origin lock.
    IF p_object_class = 'native' THEN
        SELECT * INTO STRICT i FROM braid.revision_ingestions x WHERE x.organization_id = p_organization_id AND x.project_id = p_project_id
            AND x.future_mapping_id = p_mapping_id AND x.future_revision_id = p_revision_id AND x.native_object_id = p_object_id;
    ELSE
        SELECT * INTO STRICT a FROM braid.revision_derivation_attempts x WHERE x.organization_id = p_organization_id AND x.project_id = p_project_id
            AND x.mapping_id = p_mapping_id AND x.revision_id = p_revision_id AND x.candidate_object_id = p_object_id;
    END IF;
    IF p_publication_id IS NOT NULL THEN
        SELECT * INTO p FROM braid.private_object_publications x WHERE x.organization_id = p_organization_id AND x.project_id = p_project_id
            AND x.mapping_id = p_mapping_id AND x.revision_id = p_revision_id AND x.object_class = p_object_class AND x.object_id = p_object_id
            AND x.publication_id = p_publication_id FOR UPDATE;
        IF NOT FOUND THEN RETURN pg_catalog.jsonb_build_object('eligible',false,'protected',true); END IF;
        -- Only this captured generation owns this temp; a successor does not inherit it.
        IF p_object_class = 'native' THEN
            live_owner := i.lease_generation = p.owner_generation AND i.lease_owner_id = p.owner_instance_id
                AND i.lease_token = p.owner_token AND i.lease_expires_at = p.owner_lease_expires_at AND i.completed_at IS NULL
                AND i.lease_expires_at > pg_catalog.clock_timestamp();
        ELSE
            SELECT EXISTS (SELECT 1 FROM braid.revision_derivations k WHERE k.organization_id = p_organization_id AND k.project_id = p_project_id
                AND k.mapping_id = p_mapping_id AND k.revision_id = p_revision_id AND k.cache_entry_id = p.cache_entry_id
                AND k.current_generation = p.owner_generation AND k.current_attempt_id = p.derivation_id
                AND a.derivation_id = p.derivation_id AND a.generation = p.owner_generation AND a.claimant_instance_id = p.owner_instance_id
                AND a.claim_token = p.owner_token AND a.lease_expires_at = p.owner_lease_expires_at
                AND a.finished_at IS NULL AND a.lease_expires_at > pg_catalog.clock_timestamp()) INTO live_owner;
        END IF;
        protected_value := COALESCE(live_owner,false);
        origin_at := greatest(p.registered_at,p.bound_at,p.owner_lease_expires_at);
        reason_value := 'abandoned_tracked_temporary';
    ELSIF p_object_class = 'native' THEN
        protected_value := i.phase NOT IN ('rejected','head_conflict','attempt_exhausted') OR i.completed_at IS NULL
            OR EXISTS (SELECT 1 FROM braid.mapping_revisions r WHERE r.organization_id = p_organization_id AND r.project_id = p_project_id
                AND r.mapping_id = p_mapping_id AND r.id = p_revision_id AND r.native_class = 'native' AND r.native_object_id = p_object_id);
        origin_at := i.completed_at;
        reason_value := 'terminal_native_stage';
    ELSE
        protected_value := a.result_usable OR (a.completion_kind IS NOT NULL AND a.completion_kind <> 'failed')
            OR EXISTS (SELECT 1 FROM braid.revision_derivation_attempts x WHERE x.organization_id = p_organization_id AND x.project_id = p_project_id
                AND x.mapping_id = p_mapping_id AND x.revision_id = p_revision_id AND x.candidate_object_id = p_object_id AND x.result_usable)
            OR EXISTS (SELECT 1 FROM braid.revision_derivations k JOIN braid.revision_derivation_attempts x
                ON x.organization_id = k.organization_id AND x.project_id = k.project_id AND x.mapping_id = k.mapping_id
                AND x.revision_id = k.revision_id AND x.cache_entry_id = k.cache_entry_id AND x.derivation_id = k.selected_derivation_id
                WHERE x.organization_id = p_organization_id AND x.project_id = p_project_id AND x.mapping_id = p_mapping_id
                    AND x.revision_id = p_revision_id AND x.candidate_object_id = p_object_id);
        origin_at := CASE WHEN a.finished_at IS NULL THEN a.lease_expires_at ELSE a.finished_at END;
        reason_value := CASE WHEN a.finished_at IS NULL THEN 'expired_derived_candidate' ELSE 'failed_derived_candidate' END;
    END IF;
    at_time := pg_catalog.clock_timestamp();
    RETURN pg_catalog.jsonb_build_object('eligible',NOT protected_value AND origin_at IS NOT NULL AND origin_at + interval '24 hours' <= at_time,
        'protected',protected_value,'observed_at',at_time,'eligibility_origin_at',origin_at,'reason',reason_value,
        'origin_kind',CASE WHEN p_object_class = 'native' THEN 'native_ingestion' ELSE 'report_attempt' END,
        'ingestion_operation_id',i.operation_id,'cache_entry_id',a.cache_entry_id,'derivation_id',a.derivation_id,'origin_generation',a.generation);
END;
$braid_private_object_cleanup_safety_v1$;

CREATE FUNCTION braid.guard_private_object_publications_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_private_object_publications_v1$
DECLARE observed_at timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication registrations are retained.'; END IF;
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    IF (session_user <> 'braid_ingest' OR NEW.owner_kind <> 'native_ingestion')
        AND (session_user <> 'braid_deriver' OR NEW.owner_kind <> 'report_attempt') THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Publication requires its dedicated original-owner login.';
    END IF;
    IF NOT braid.lock_private_object_origin_v1(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The original publication owner is unavailable.';
    END IF;
    IF NOT braid.private_publication_owner_live_v1(NEW) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The original publication owner is unavailable.';
    END IF;
    IF TG_OP = 'UPDATE' AND (pg_catalog.to_jsonb(NEW) - ARRAY['control_namespace_id','linux_boot_id','mount_namespace_device','mount_namespace_inode','effective_user_id','file_inode','file_device_major','file_device_minor','file_mount_id','directory_inode','directory_device_major','directory_device_minor','directory_mount_id','bound_record_digest','bound_at'])
        IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['control_namespace_id','linux_boot_id','mount_namespace_device','mount_namespace_inode','effective_user_id','file_inode','file_device_major','file_device_minor','file_mount_id','directory_inode','directory_device_major','directory_device_minor','directory_mount_id','bound_record_digest','bound_at']) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication ownership and absence facts are immutable.';
    END IF;
    IF EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = NEW.organization_id AND r.project_id = NEW.project_id
        AND r.mapping_id = NEW.mapping_id AND r.revision_id = NEW.revision_id AND r.object_class = NEW.object_class AND r.object_id = NEW.object_id
        AND (r.target_kind = 'final_object' OR (r.target_kind = 'tracked_temporary' AND r.publication_id = NEW.publication_id))) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The exact publication target is retired.';
    END IF;
    observed_at := pg_catalog.clock_timestamp();
    IF TG_OP = 'INSERT' THEN
        IF num_nonnulls(NEW.publication_id,NEW.format,NEW.registered_at,NEW.control_namespace_id,NEW.linux_boot_id,NEW.mount_namespace_device,NEW.mount_namespace_inode,NEW.effective_user_id,NEW.file_inode,NEW.file_device_major,NEW.file_device_minor,NEW.file_mount_id,NEW.directory_inode,NEW.directory_device_major,NEW.directory_device_minor,NEW.directory_mount_id,NEW.bound_record_digest,NEW.bound_at,NEW.native_object_id,NEW.candidate_object_id) <> 0 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication begins with caller intent only.';
        END IF;
        NEW.publication_id := pg_catalog.gen_random_uuid(); NEW.registered_at := observed_at;
        NEW.format := 'braid.private-publication.linux.v2';
        NEW.native_object_id := CASE WHEN NEW.object_class = 'native' THEN NEW.object_id END;
        NEW.candidate_object_id := CASE WHEN NEW.object_class = 'derived' THEN NEW.object_id END;
    ELSE
        IF OLD.bound_at IS NOT NULL THEN
            IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication binding is immutable.'; END IF;
            RETURN OLD;
        END IF;
        IF NEW.bound_at IS NOT NULL OR num_nonnulls(NEW.control_namespace_id,NEW.linux_boot_id,NEW.mount_namespace_device,NEW.mount_namespace_inode,NEW.effective_user_id,NEW.file_inode,NEW.file_device_major,NEW.file_device_minor,NEW.file_mount_id,NEW.directory_inode,NEW.directory_device_major,NEW.directory_device_minor,NEW.directory_mount_id,NEW.bound_record_digest) <> 14 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication binding requires one complete actual identity.';
        END IF;
        NEW.bound_at := observed_at;
    END IF;
    RETURN NEW;
END;
$braid_guard_private_object_publications_v1$;

CREATE FUNCTION braid.guard_native_retirement_attachment_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_native_retirement_attachment_v1$
DECLARE origin_row braid.revision_ingestions%ROWTYPE;
BEGIN
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    IF TG_TABLE_NAME = 'revision_ingestions' AND TG_OP = 'INSERT' THEN RETURN NEW; END IF;
    IF TG_TABLE_NAME = 'revision_ingestions' AND TG_OP = 'UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
    IF TG_TABLE_NAME = 'mapping_revisions' THEN
        SELECT * INTO origin_row FROM braid.revision_ingestions i WHERE i.organization_id = NEW.organization_id
            AND i.project_id = NEW.project_id AND i.operation_id = NEW.ingestion_operation_id FOR UPDATE;
    ELSE
        SELECT * INTO origin_row FROM braid.revision_ingestions i WHERE i.organization_id = NEW.organization_id
            AND i.project_id = NEW.project_id AND i.operation_id = NEW.operation_id FOR UPDATE;
    END IF;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '23503', MESSAGE = 'Native attachment requires its existing scoped origin.';
    END IF;
    -- A successful attachment must satisfy the existing full native-facts or ingestion FK
    -- against this locked immutable parent. Preserve those FK/CHECK error classifications.
    IF EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = origin_row.organization_id
        AND r.project_id = origin_row.project_id AND r.mapping_id = origin_row.future_mapping_id AND r.revision_id = origin_row.future_revision_id
        AND r.object_class = 'native' AND r.object_id = origin_row.native_object_id AND r.target_kind = 'final_object') THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retired native objects cannot acquire references or resume.';
    END IF;
    RETURN NEW;
END;
$braid_guard_native_retirement_attachment_v1$;

CREATE FUNCTION braid.guard_derived_retirement_attachment_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_derived_retirement_attachment_v1$
DECLARE candidate uuid;
BEGIN
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    IF TG_TABLE_NAME = 'revision_derivations' THEN
        IF NEW.selected_derivation_id IS NULL OR NEW.selected_derivation_id IS NOT DISTINCT FROM OLD.selected_derivation_id THEN RETURN NEW; END IF;
        -- UPDATE already holds the exact key lock; reload the selected target separately.
        SELECT a.candidate_object_id INTO STRICT candidate FROM braid.revision_derivation_attempts a
            WHERE a.organization_id = NEW.organization_id AND a.project_id = NEW.project_id AND a.mapping_id = NEW.mapping_id
                AND a.revision_id = NEW.revision_id AND a.cache_entry_id = NEW.cache_entry_id
                AND a.derivation_id = NEW.selected_derivation_id AND a.result_usable;
    ELSE
        -- INSERT uses NEW's already-generated candidate. It cannot query its not-yet-inserted attempt row.
        PERFORM 1 FROM braid.revision_derivations k WHERE k.organization_id = NEW.organization_id AND k.project_id = NEW.project_id
            AND k.mapping_id = NEW.mapping_id AND k.revision_id = NEW.revision_id AND k.cache_entry_id = NEW.cache_entry_id FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Report attachment requires its exact key.'; END IF;
        IF TG_OP = 'UPDATE' AND NOT NEW.result_usable THEN RETURN NEW; END IF;
        candidate := NEW.candidate_object_id;
    END IF;
    IF EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = NEW.organization_id AND r.project_id = NEW.project_id
        AND r.mapping_id = NEW.mapping_id AND r.revision_id = NEW.revision_id AND r.object_class = 'derived'
        AND r.object_id = candidate AND r.target_kind = 'final_object') THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retired derived objects cannot acquire usable references.';
    END IF;
    RETURN NEW;
END;
$braid_guard_derived_retirement_attachment_v1$;

CREATE FUNCTION braid.guard_private_object_retirements_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_private_object_retirements_v1$
DECLARE safety jsonb;
    next_attempt braid.private_object_cleanup_attempts%ROWTYPE;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retirement fences are permanent.'; END IF;
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    IF NOT braid.lock_private_object_origin_v1(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retirement requires an exact retained origin.';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF num_nonnulls(NEW.retirement_id,NEW.origin_kind,NEW.ingestion_operation_id,NEW.cache_entry_id,NEW.derivation_id,NEW.origin_generation,NEW.native_object_id,NEW.candidate_object_id,NEW.reason,NEW.eligibility_origin_at,NEW.retired_at,NEW.current_attempt_id,NEW.completed_generation,NEW.completed_attempt_id) <> 0
            OR NEW.current_generation <> 0 OR NOT NEW.completed_success OR NEW.policy_id <> 'braid.orphan-maintenance.v1'
            OR (NEW.target_kind = 'final_object' AND NEW.publication_id IS NOT NULL)
            OR (NEW.target_kind = 'tracked_temporary' AND NEW.publication_id IS NULL) THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retirement begins with an exact target only.';
        END IF;
        safety := braid.private_object_cleanup_safety_v1(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id,NEW.publication_id);
        IF (safety->>'eligible')::boolean IS DISTINCT FROM true THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The exact object remains protected or is not due.';
        END IF;
        NEW.retirement_id := pg_catalog.gen_random_uuid(); NEW.retired_at := (safety->>'observed_at')::timestamp with time zone;
        NEW.origin_kind := safety->>'origin_kind'; NEW.ingestion_operation_id := (safety->>'ingestion_operation_id')::uuid;
        NEW.cache_entry_id := (safety->>'cache_entry_id')::uuid; NEW.derivation_id := (safety->>'derivation_id')::uuid;
        NEW.origin_generation := (safety->>'origin_generation')::bigint; NEW.reason := safety->>'reason';
        NEW.eligibility_origin_at := (safety->>'eligibility_origin_at')::timestamp with time zone;
        NEW.native_object_id := CASE WHEN NEW.object_class = 'native' THEN NEW.object_id END;
        NEW.candidate_object_id := CASE WHEN NEW.object_class = 'derived' THEN NEW.object_id END;
        RETURN NEW;
    END IF;
    IF (pg_catalog.to_jsonb(NEW) - ARRAY['current_generation','current_attempt_id','completed_generation','completed_attempt_id'])
        IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['current_generation','current_attempt_id','completed_generation','completed_attempt_id'])
        OR OLD.completed_attempt_id IS NOT NULL THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Retirement identity and acknowledged cleanup are immutable.';
    END IF;
    IF NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
    SELECT * INTO next_attempt FROM braid.private_object_cleanup_attempts a WHERE a.organization_id = NEW.organization_id
        AND a.project_id = NEW.project_id AND a.retirement_id = NEW.retirement_id AND a.generation = NEW.current_generation
        AND a.attempt_id = NEW.current_attempt_id;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup progress requires its exact retained attempt.'; END IF;
    IF NEW.current_generation = OLD.current_generation THEN
        IF NEW.current_attempt_id IS DISTINCT FROM OLD.current_attempt_id OR NOT next_attempt.success
            OR NEW.completed_attempt_id IS DISTINCT FROM next_attempt.attempt_id OR NEW.completed_generation IS DISTINCT FROM next_attempt.generation THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Only an acknowledged exact success can complete cleanup.';
        END IF;
    ELSIF OLD.current_generation >= 8 OR NEW.current_generation <> OLD.current_generation + 1
        OR NEW.completed_generation IS NOT NULL OR NEW.completed_attempt_id IS NOT NULL OR next_attempt.finished_at IS NOT NULL THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup generations advance exactly once without rewriting history.';
    END IF;
    RETURN NEW;
END;
$braid_guard_private_object_retirements_v1$;

CREATE FUNCTION braid.guard_private_object_cleanup_attempts_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_private_object_cleanup_attempts_v1$
DECLARE header braid.private_object_retirements%ROWTYPE;
    previous braid.private_object_cleanup_attempts%ROWTYPE;
    observed_at timestamp with time zone;
    safety jsonb;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup attempts are retained.'; END IF;
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    safety := braid.private_object_cleanup_safety_v1(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id,
        (SELECT r.publication_id FROM braid.private_object_retirements r WHERE r.organization_id = NEW.organization_id AND r.project_id = NEW.project_id AND r.retirement_id = NEW.retirement_id));
    IF (safety->>'eligible')::boolean IS DISTINCT FROM true THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup requires the exact unprotected retired target.';
    END IF;
    SELECT * INTO STRICT header FROM braid.private_object_retirements r WHERE r.organization_id = NEW.organization_id
        AND r.project_id = NEW.project_id AND r.retirement_id = NEW.retirement_id FOR UPDATE;
    observed_at := pg_catalog.clock_timestamp();
    IF ROW(NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id) IS DISTINCT FROM ROW(header.mapping_id,header.revision_id,header.object_class,header.object_id)
        OR header.completed_attempt_id IS NOT NULL THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup attempt target is unavailable.';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF header.current_generation >= 8 OR NEW.generation <> header.current_generation + 1
            OR num_nonnulls(NEW.attempt_id,NEW.lease_token,NEW.started_at,NEW.lease_expires_at,NEW.finished_at,NEW.disposition) <> 0 OR NEW.success THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A new cleanup claim requires the next uninvented header.';
        END IF;
        IF header.current_generation > 0 THEN
            SELECT * INTO STRICT previous FROM braid.private_object_cleanup_attempts a WHERE a.organization_id = header.organization_id
                AND a.project_id = header.project_id AND a.retirement_id = header.retirement_id AND a.generation = header.current_generation;
            IF previous.success OR COALESCE(previous.finished_at,previous.lease_expires_at) + interval '30 seconds' > observed_at THEN
                RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The predecessor cleanup claim is not retryable yet.';
            END IF;
        END IF;
        NEW.attempt_id := pg_catalog.gen_random_uuid(); NEW.lease_token := pg_catalog.gen_random_uuid();
        NEW.started_at := observed_at; NEW.lease_expires_at := observed_at + interval '60 seconds';
        RETURN NEW;
    END IF;
    IF (pg_catalog.to_jsonb(NEW) - ARRAY['finished_at','disposition','success']) IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['finished_at','disposition','success'])
        OR OLD.finished_at IS NOT NULL OR NEW.finished_at IS NOT NULL OR NEW.success IS DISTINCT FROM OLD.success
        OR NEW.disposition IS NULL OR header.current_generation <> OLD.generation OR header.current_attempt_id IS DISTINCT FROM OLD.attempt_id
        OR OLD.lease_expires_at <= observed_at THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Cleanup completion requires its exact live immutable claim.';
    END IF;
    NEW.finished_at := observed_at; NEW.success := NEW.disposition IN ('deleted','missing');
    RETURN NEW;
END;
$braid_guard_private_object_cleanup_attempts_v1$;

CREATE FUNCTION braid.private_publication_record_v1(p_publication braid.private_object_publications) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_private_publication_record_v1$
DECLARE ancillary jsonb;
BEGIN
    IF p_publication.owner_kind = 'native_ingestion' THEN
        SELECT pg_catalog.jsonb_build_object('ingestion',pg_catalog.jsonb_build_object(
            'organization_id',i.organization_id,'project_id',i.project_id,'operation_id',i.operation_id,
            'future_mapping_id',i.future_mapping_id,'future_revision_id',i.future_revision_id,'native_object_id',i.native_object_id,
            'native_class',i.native_class,'actor_principal_id',i.actor_principal_id),
            'owner_ingestion_attempt',pg_catalog.jsonb_build_object('organization_id',a.organization_id,'project_id',a.project_id,
                'operation_id',a.operation_id,'generation',a.generation,'claimant_instance_id',a.claimant_instance_id,'claim_token',a.claim_token))
            INTO STRICT ancillary FROM braid.revision_ingestions i JOIN braid.revision_ingestion_attempts a
                ON a.organization_id = i.organization_id AND a.project_id = i.project_id AND a.operation_id = i.operation_id
            WHERE i.organization_id = p_publication.organization_id AND i.project_id = p_publication.project_id
                AND i.operation_id = p_publication.ingestion_operation_id AND a.generation = p_publication.owner_generation;
    ELSE
        SELECT pg_catalog.jsonb_build_object('report_entry',pg_catalog.to_jsonb(k),
            'owner_report_attempt',pg_catalog.jsonb_build_object('organization_id',a.organization_id,'project_id',a.project_id,
                'mapping_id',a.mapping_id,'revision_id',a.revision_id,'cache_entry_id',a.cache_entry_id,'generation',a.generation,
                'derivation_id',a.derivation_id,'actor_principal_id',a.actor_principal_id,'claimant_instance_id',a.claimant_instance_id,
                'claim_token',a.claim_token,'started_at',a.started_at,'lease_expires_at',a.lease_expires_at,
                'candidate_object_id',a.candidate_object_id,'derived_class',a.derived_class,'repair_target_derivation_id',a.repair_target_derivation_id))
            INTO STRICT ancillary FROM braid.revision_derivations k JOIN braid.revision_derivation_attempts a
                ON a.organization_id = k.organization_id AND a.project_id = k.project_id AND a.mapping_id = k.mapping_id
                AND a.revision_id = k.revision_id AND a.cache_entry_id = k.cache_entry_id
            WHERE a.organization_id = p_publication.organization_id AND a.project_id = p_publication.project_id
                AND a.mapping_id = p_publication.mapping_id AND a.revision_id = p_publication.revision_id
                AND a.cache_entry_id = p_publication.cache_entry_id AND a.derivation_id = p_publication.derivation_id AND a.generation = p_publication.owner_generation;
    END IF;
    RETURN pg_catalog.to_jsonb(p_publication) || ancillary;
END;
$braid_private_publication_record_v1$;

CREATE FUNCTION braid.private_object_cleanup_claim_json_v1(p_retirement braid.private_object_retirements, p_attempt braid.private_object_cleanup_attempts) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_private_object_cleanup_claim_json_v1$
DECLARE publication_value jsonb;
BEGIN
    IF p_retirement.publication_id IS NOT NULL THEN
        SELECT braid.private_publication_record_v1(p) INTO STRICT publication_value FROM braid.private_object_publications p
            WHERE p.organization_id = p_retirement.organization_id AND p.project_id = p_retirement.project_id AND p.publication_id = p_retirement.publication_id;
    END IF;
    RETURN pg_catalog.jsonb_build_object('retirement',
        (pg_catalog.to_jsonb(p_retirement) - ARRAY['current_generation','current_attempt_id','completed_generation','completed_attempt_id','completed_success'])
            || pg_catalog.jsonb_build_object('publication',publication_value),
        'attempt_id',p_attempt.attempt_id,'generation',p_attempt.generation,'instance_id',p_attempt.instance_id,
        'lease_token',p_attempt.lease_token,'started_at',p_attempt.started_at,'lease_expires_at',p_attempt.lease_expires_at);
END;
$braid_private_object_cleanup_claim_json_v1$;

CREATE FUNCTION braid.canonical_private_object_cleanup_claim_v1(p_claim jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_canonical_private_object_cleanup_claim_v1$
DECLARE path_value text[];
    field_name text;
    value jsonb := p_claim;
BEGIN
    IF pg_catalog.jsonb_typeof(value) <> 'object' OR pg_catalog.octet_length(value::text) > 131072 THEN RETURN NULL; END IF;
    IF pg_catalog.jsonb_typeof(value #> '{retirement,publication}') = 'object' THEN
        value := pg_catalog.jsonb_set(value,'{retirement,publication}',
            (value #> '{retirement,publication}') - ARRAY['report_entry','owner_report_attempt','ingestion','owner_ingestion_attempt']);
    END IF;
    FOREACH field_name IN ARRAY ARRAY['started_at','lease_expires_at'] LOOP
        value := pg_catalog.jsonb_set(value,ARRAY[field_name],pg_catalog.to_jsonb((value->>field_name)::timestamp with time zone));
    END LOOP;
    FOREACH field_name IN ARRAY ARRAY['eligibility_origin_at','retired_at'] LOOP
        path_value := ARRAY['retirement',field_name];
        value := pg_catalog.jsonb_set(value,path_value,pg_catalog.to_jsonb((value #>> path_value)::timestamp with time zone));
    END LOOP;
    IF pg_catalog.jsonb_typeof(value #> '{retirement,publication}') = 'object' THEN
        FOREACH field_name IN ARRAY ARRAY['registered_at','bound_at','owner_lease_expires_at'] LOOP
            path_value := ARRAY['retirement','publication',field_name];
            IF value #>> path_value IS NOT NULL THEN
                value := pg_catalog.jsonb_set(value,path_value,pg_catalog.to_jsonb((value #>> path_value)::timestamp with time zone));
            END IF;
        END LOOP;
    END IF;
    RETURN value;
EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow THEN RETURN NULL;
END;
$braid_canonical_private_object_cleanup_claim_v1$;

CREATE FUNCTION braid.claim_private_object_cleanup_v1(p_organization_id uuid, p_project_id uuid, p_instance_id uuid, p_after jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_claim_private_object_cleanup_v1$
DECLARE candidates jsonb;
    candidate jsonb;
    safety jsonb;
    header braid.private_object_retirements%ROWTYPE;
    previous braid.private_object_cleanup_attempts%ROWTYPE;
    claimed braid.private_object_cleanup_attempts%ROWTYPE;
    claims jsonb := '[]'::jsonb;
    examined integer := 0;
    protected_count integer := 0;
    not_due integer := 0;
    next_cursor jsonb;
    scan_started timestamp with time zone;
    observed_at timestamp with time zone;
    after_stream integer := 0;
    after_time timestamp with time zone;
    zero_id uuid := '00000000-0000-0000-0000-000000000000';
BEGIN
    PERFORM braid.assert_private_object_context_v1(p_organization_id,p_project_id,true);
    IF p_instance_id IS NULL OR p_instance_id = zero_id THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Maintenance requires a nonzero instance.';
    END IF;
    IF p_after IS NOT NULL THEN
        IF (pg_catalog.jsonb_typeof(p_after) <> 'object' OR pg_catalog.octet_length(p_after::text) > 2048
            OR (SELECT count(*) FROM pg_catalog.jsonb_object_keys(p_after)) <> 10
            OR NOT (p_after ?& ARRAY['organization_id','project_id','stream','eligible_at','mapping_id','revision_id','object_class','object_id','target_kind','publication_id'])
            OR (p_after->>'organization_id')::uuid IS DISTINCT FROM p_organization_id OR (p_after->>'project_id')::uuid IS DISTINCT FROM p_project_id
            OR (p_after->>'stream')::integer NOT BETWEEN 1 AND 4 OR p_after->>'eligible_at' IS NULL
            OR (p_after->>'mapping_id')::uuid IS NULL OR (p_after->>'mapping_id')::uuid = zero_id
            OR (p_after->>'revision_id')::uuid IS NULL OR (p_after->>'revision_id')::uuid = zero_id
            OR (p_after->>'object_id')::uuid IS NULL OR (p_after->>'object_id')::uuid = zero_id
            OR p_after->>'object_class' NOT IN ('native','derived') OR p_after->>'target_kind' NOT IN ('final_object','tracked_temporary')
            OR (p_after->>'stream' = '2' AND (p_after->>'object_class' <> 'native' OR p_after->>'target_kind' <> 'final_object'))
            OR (p_after->>'stream' = '3' AND (p_after->>'object_class' <> 'derived' OR p_after->>'target_kind' <> 'final_object'))
            OR (p_after->>'stream' = '4' AND p_after->>'target_kind' <> 'tracked_temporary')
            OR (p_after->>'publication_id' IS NOT NULL AND (p_after->>'publication_id')::uuid = zero_id)
            OR ((p_after->>'target_kind' = 'final_object') IS DISTINCT FROM (p_after->>'publication_id' IS NULL))) IS DISTINCT FROM false THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'The cleanup cursor is invalid for this project.';
        END IF;
        after_stream := (p_after->>'stream')::integer; after_time := (p_after->>'eligible_at')::timestamp with time zone;
        IF NOT pg_catalog.isfinite(after_time) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'The cleanup cursor timestamp must be finite.';
        END IF;
    END IF;
    scan_started := pg_catalog.clock_timestamp();
    -- The result contains at most100 rows. These optimistic candidates grant no cleanup authority.
    WITH possible AS (
        SELECT 1 AS stream,r.retired_at AS eligible_at,r.mapping_id,r.revision_id,r.object_class,r.object_id,r.target_kind,r.publication_id,
            r.retirement_id,r.ingestion_operation_id,r.cache_entry_id
            FROM braid.private_object_retirements r WHERE r.organization_id = p_organization_id AND r.project_id = p_project_id
                AND r.completed_attempt_id IS NULL AND r.current_generation < 8
        UNION ALL
        SELECT 2,i.completed_at + interval '24 hours',i.future_mapping_id,i.future_revision_id,'native',i.native_object_id,'final_object',NULL::uuid,
            NULL::uuid,i.operation_id,NULL::uuid
            FROM braid.revision_ingestions i WHERE i.organization_id = p_organization_id AND i.project_id = p_project_id
                AND i.phase IN ('rejected','head_conflict','attempt_exhausted') AND i.completed_at <= scan_started - interval '24 hours'
                AND NOT EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = i.organization_id AND r.project_id = i.project_id
                    AND r.mapping_id = i.future_mapping_id AND r.revision_id = i.future_revision_id AND r.object_class = 'native'
                    AND r.object_id = i.native_object_id AND r.target_kind = 'final_object')
        UNION ALL
        SELECT 3,COALESCE(a.finished_at,a.lease_expires_at) + interval '24 hours',a.mapping_id,a.revision_id,'derived',a.candidate_object_id,'final_object',NULL::uuid,
            NULL::uuid,NULL::uuid,a.cache_entry_id
            FROM braid.revision_derivation_attempts a WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id
                AND NOT a.result_usable AND (a.completion_kind IS NULL OR a.completion_kind = 'failed')
                AND COALESCE(a.finished_at,a.lease_expires_at) <= scan_started - interval '24 hours'
                AND NOT EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = a.organization_id AND r.project_id = a.project_id
                    AND r.mapping_id = a.mapping_id AND r.revision_id = a.revision_id AND r.object_class = 'derived'
                    AND r.object_id = a.candidate_object_id AND r.target_kind = 'final_object')
        UNION ALL
        SELECT 4,greatest(p.registered_at,p.bound_at,p.owner_lease_expires_at) + interval '24 hours',p.mapping_id,p.revision_id,p.object_class,p.object_id,'tracked_temporary',p.publication_id,
            NULL::uuid,p.ingestion_operation_id,p.cache_entry_id
            FROM braid.private_object_publications p WHERE p.organization_id = p_organization_id AND p.project_id = p_project_id
                AND greatest(p.registered_at,p.bound_at,p.owner_lease_expires_at) <= scan_started - interval '24 hours'
                AND NOT EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = p.organization_id AND r.project_id = p.project_id
                    AND r.publication_id = p.publication_id AND r.target_kind = 'tracked_temporary')
    ), bounded AS (
        SELECT * FROM possible c WHERE p_after IS NULL OR
            ROW(c.stream,c.eligible_at,c.mapping_id,c.revision_id,c.object_class COLLATE "C",c.object_id,c.target_kind COLLATE "C",COALESCE(c.publication_id,zero_id)) >
            ROW(after_stream,after_time,(p_after->>'mapping_id')::uuid,(p_after->>'revision_id')::uuid,(p_after->>'object_class') COLLATE "C",(p_after->>'object_id')::uuid,(p_after->>'target_kind') COLLATE "C",COALESCE((p_after->>'publication_id')::uuid,zero_id))
        ORDER BY c.stream,c.eligible_at,c.mapping_id,c.revision_id,c.object_class COLLATE "C",c.object_id,c.target_kind COLLATE "C",COALESCE(c.publication_id,zero_id) LIMIT 100
    ) SELECT COALESCE(pg_catalog.jsonb_agg(pg_catalog.to_jsonb(b) ORDER BY b.stream,b.eligible_at,b.mapping_id,b.revision_id,b.object_class COLLATE "C",b.object_id,b.target_kind COLLATE "C",COALESCE(b.publication_id,zero_id)),'[]'::jsonb) INTO candidates FROM bounded b;
    -- Lock all discovered origins in a deterministic immutable-PK order before making any retirement decision.
    FOR candidate IN SELECT value FROM pg_catalog.jsonb_array_elements(candidates)
        ORDER BY value->>'object_class',CASE WHEN value->>'object_class' = 'native' THEN value->>'ingestion_operation_id' END,
            value->>'mapping_id',value->>'revision_id',value->>'cache_entry_id' LOOP
        PERFORM braid.lock_private_object_origin_v1(p_organization_id,p_project_id,(candidate->>'mapping_id')::uuid,
            (candidate->>'revision_id')::uuid,candidate->>'object_class',(candidate->>'object_id')::uuid);
    END LOOP;
    FOR candidate IN SELECT value FROM pg_catalog.jsonb_array_elements(candidates) LOOP
        EXIT WHEN pg_catalog.jsonb_array_length(claims) >= 16 OR pg_catalog.clock_timestamp() >= scan_started + interval '30 seconds';
        examined := examined + 1;
        next_cursor := pg_catalog.jsonb_build_object('organization_id',p_organization_id,'project_id',p_project_id,'stream',candidate->'stream',
            'eligible_at',candidate->'eligible_at','mapping_id',candidate->'mapping_id','revision_id',candidate->'revision_id',
            'object_class',candidate->'object_class','object_id',candidate->'object_id','target_kind',candidate->'target_kind','publication_id',candidate->'publication_id');
        safety := braid.private_object_cleanup_safety_v1(p_organization_id,p_project_id,(candidate->>'mapping_id')::uuid,
            (candidate->>'revision_id')::uuid,candidate->>'object_class',(candidate->>'object_id')::uuid,(candidate->>'publication_id')::uuid);
        IF (safety->>'eligible')::boolean IS DISTINCT FROM true THEN
            IF (safety->>'protected')::boolean IS TRUE THEN protected_count := protected_count + 1; ELSE not_due := not_due + 1; END IF;
            CONTINUE;
        END IF;
        SELECT * INTO header FROM braid.private_object_retirements r WHERE r.organization_id = p_organization_id AND r.project_id = p_project_id
            AND r.mapping_id = (candidate->>'mapping_id')::uuid AND r.revision_id = (candidate->>'revision_id')::uuid
            AND r.object_class = candidate->>'object_class' AND r.object_id = (candidate->>'object_id')::uuid AND r.target_kind = candidate->>'target_kind'
            AND r.publication_id IS NOT DISTINCT FROM (candidate->>'publication_id')::uuid FOR UPDATE;
        IF NOT FOUND THEN
            INSERT INTO braid.private_object_retirements (organization_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,publication_id)
                VALUES (p_organization_id,p_project_id,(candidate->>'mapping_id')::uuid,(candidate->>'revision_id')::uuid,candidate->>'object_class',
                    (candidate->>'object_id')::uuid,candidate->>'target_kind',(candidate->>'publication_id')::uuid) RETURNING * INTO header;
        END IF;
        observed_at := pg_catalog.clock_timestamp();
        IF header.completed_attempt_id IS NOT NULL OR header.current_generation >= 8 THEN protected_count := protected_count + 1; CONTINUE; END IF;
        IF header.current_generation > 0 THEN
            SELECT * INTO STRICT previous FROM braid.private_object_cleanup_attempts a WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id
                AND a.retirement_id = header.retirement_id AND a.generation = header.current_generation;
            IF previous.success OR COALESCE(previous.finished_at,previous.lease_expires_at) + interval '30 seconds' > observed_at THEN not_due := not_due + 1; CONTINUE; END IF;
        END IF;
        INSERT INTO braid.private_object_cleanup_attempts (organization_id,project_id,mapping_id,revision_id,object_class,object_id,retirement_id,generation,instance_id)
            VALUES (header.organization_id,header.project_id,header.mapping_id,header.revision_id,header.object_class,header.object_id,header.retirement_id,header.current_generation + 1,p_instance_id)
            RETURNING * INTO claimed;
        UPDATE braid.private_object_retirements SET current_generation = claimed.generation,current_attempt_id = claimed.attempt_id
            WHERE organization_id = p_organization_id AND project_id = p_project_id AND retirement_id = header.retirement_id RETURNING * INTO header;
        claims := claims || pg_catalog.jsonb_build_array(braid.private_object_cleanup_claim_json_v1(header,claimed));
    END LOOP;
    IF examined = pg_catalog.jsonb_array_length(candidates) AND examined < 100 THEN next_cursor := NULL; END IF;
    RETURN pg_catalog.jsonb_build_object('claims',claims,'examined',examined,'protected',protected_count,'not_due',not_due,'next_cursor',next_cursor,'observed_at',pg_catalog.clock_timestamp());
END;
$braid_claim_private_object_cleanup_v1$;

CREATE FUNCTION braid.finish_private_object_cleanup_v1(p_organization_id uuid, p_project_id uuid, p_claim jsonb, p_disposition text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_finish_private_object_cleanup_v1$
DECLARE header braid.private_object_retirements%ROWTYPE;
    attempt braid.private_object_cleanup_attempts%ROWTYPE;
    canonical jsonb;
    expected jsonb;
    safety jsonb;
BEGIN
    PERFORM braid.assert_private_object_context_v1(p_organization_id,p_project_id,true);
    IF p_disposition IS NULL OR p_disposition NOT IN ('deleted','missing','canceled','deadline','storage_io','unsafe_storage') THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'The cleanup disposition is invalid.';
    END IF;
    canonical := braid.canonical_private_object_cleanup_claim_v1(p_claim);
    IF canonical IS NULL OR (canonical #>> '{retirement,organization_id}')::uuid IS DISTINCT FROM p_organization_id
        OR (canonical #>> '{retirement,project_id}')::uuid IS DISTINCT FROM p_project_id THEN
        RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL);
    END IF;
    IF NOT braid.lock_private_object_origin_v1(p_organization_id,p_project_id,(canonical #>> '{retirement,mapping_id}')::uuid,
        (canonical #>> '{retirement,revision_id}')::uuid,canonical #>> '{retirement,object_class}',(canonical #>> '{retirement,object_id}')::uuid) THEN
        RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL);
    END IF;
    safety := braid.private_object_cleanup_safety_v1(p_organization_id,p_project_id,(canonical #>> '{retirement,mapping_id}')::uuid,
        (canonical #>> '{retirement,revision_id}')::uuid,canonical #>> '{retirement,object_class}',(canonical #>> '{retirement,object_id}')::uuid,(canonical #>> '{retirement,publication_id}')::uuid);
    SELECT * INTO header FROM braid.private_object_retirements r WHERE r.organization_id = p_organization_id AND r.project_id = p_project_id
        AND r.retirement_id = (canonical #>> '{retirement,retirement_id}')::uuid FOR UPDATE;
    IF NOT FOUND THEN RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL); END IF;
    SELECT * INTO attempt FROM braid.private_object_cleanup_attempts a WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id
        AND a.retirement_id = header.retirement_id AND a.generation = (canonical->>'generation')::bigint FOR UPDATE;
    IF NOT FOUND THEN RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL); END IF;
    expected := braid.canonical_private_object_cleanup_claim_v1(braid.private_object_cleanup_claim_json_v1(header,attempt));
    IF canonical IS DISTINCT FROM expected THEN RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL); END IF;
    IF (safety->>'eligible')::boolean IS DISTINCT FROM true THEN
        RETURN pg_catalog.jsonb_build_object('outcome','safety_refusal','attempt',NULL);
    END IF;
    IF attempt.finished_at IS NOT NULL THEN
        IF attempt.disposition = p_disposition THEN
            RETURN pg_catalog.jsonb_build_object('outcome','already_applied','attempt',braid.private_object_cleanup_claim_json_v1(header,attempt)
                || pg_catalog.jsonb_build_object('finished_at',attempt.finished_at,'disposition',attempt.disposition));
        END IF;
        RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL);
    END IF;
    IF header.current_generation <> attempt.generation OR header.current_attempt_id IS DISTINCT FROM attempt.attempt_id
        OR header.completed_attempt_id IS NOT NULL OR attempt.lease_expires_at <= pg_catalog.clock_timestamp() THEN
        RETURN pg_catalog.jsonb_build_object('outcome','claim_lost','attempt',NULL);
    END IF;
    UPDATE braid.private_object_cleanup_attempts SET disposition = p_disposition WHERE organization_id = p_organization_id
        AND project_id = p_project_id AND retirement_id = header.retirement_id AND generation = attempt.generation RETURNING * INTO attempt;
    IF attempt.success THEN
        UPDATE braid.private_object_retirements SET completed_generation = attempt.generation,completed_attempt_id = attempt.attempt_id
            WHERE organization_id = p_organization_id AND project_id = p_project_id AND retirement_id = header.retirement_id RETURNING * INTO header;
    END IF;
    RETURN pg_catalog.jsonb_build_object('outcome','applied','attempt',braid.private_object_cleanup_claim_json_v1(header,attempt)
        || pg_catalog.jsonb_build_object('finished_at',attempt.finished_at,'disposition',attempt.disposition));
END;
$braid_finish_private_object_cleanup_v1$;

CREATE FUNCTION braid.get_private_object_cleanup_attempt_v1(p_organization_id uuid, p_project_id uuid, p_retirement_id uuid, p_generation bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_get_private_object_cleanup_attempt_v1$
DECLARE header braid.private_object_retirements%ROWTYPE;
    attempt braid.private_object_cleanup_attempts%ROWTYPE;
BEGIN
    PERFORM braid.assert_private_object_context_v1(p_organization_id,p_project_id,true);
    IF p_retirement_id IS NULL OR p_retirement_id = '00000000-0000-0000-0000-000000000000' OR p_generation IS NULL OR p_generation NOT BETWEEN 1 AND 8 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'The retained cleanup identity is invalid.';
    END IF;
    SELECT * INTO header FROM braid.private_object_retirements r WHERE r.organization_id = p_organization_id AND r.project_id = p_project_id AND r.retirement_id = p_retirement_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO attempt FROM braid.private_object_cleanup_attempts a WHERE a.organization_id = p_organization_id AND a.project_id = p_project_id
        AND a.retirement_id = p_retirement_id AND a.generation = p_generation;
    IF NOT FOUND THEN RETURN NULL; END IF;
    RETURN braid.private_object_cleanup_claim_json_v1(header,attempt) || pg_catalog.jsonb_build_object('finished_at',attempt.finished_at,'disposition',attempt.disposition);
END;
$braid_get_private_object_cleanup_attempt_v1$;

CREATE TRIGGER private_object_publications_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.private_object_publications
    FOR EACH ROW EXECUTE FUNCTION braid.guard_private_object_publications_v1();
CREATE TRIGGER private_object_retirements_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.private_object_retirements
    FOR EACH ROW EXECUTE FUNCTION braid.guard_private_object_retirements_v1();
CREATE TRIGGER private_object_cleanup_attempts_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.private_object_cleanup_attempts
    FOR EACH ROW EXECUTE FUNCTION braid.guard_private_object_cleanup_attempts_v1();
CREATE TRIGGER revision_ingestions_z_retirement_guard BEFORE INSERT OR UPDATE ON braid.revision_ingestions
    FOR EACH ROW EXECUTE FUNCTION braid.guard_native_retirement_attachment_v1();
CREATE TRIGGER revision_ingestion_attempts_z_retirement_guard BEFORE INSERT OR UPDATE ON braid.revision_ingestion_attempts
    FOR EACH ROW EXECUTE FUNCTION braid.guard_native_retirement_attachment_v1();
CREATE TRIGGER mapping_revisions_z_retirement_guard BEFORE INSERT ON braid.mapping_revisions
    FOR EACH ROW EXECUTE FUNCTION braid.guard_native_retirement_attachment_v1();
CREATE TRIGGER revision_derivation_attempts_z_retirement_guard BEFORE INSERT OR UPDATE ON braid.revision_derivation_attempts
    FOR EACH ROW EXECUTE FUNCTION braid.guard_derived_retirement_attachment_v1();
CREATE TRIGGER revision_derivations_z_retirement_guard BEFORE UPDATE ON braid.revision_derivations
    FOR EACH ROW EXECUTE FUNCTION braid.guard_derived_retirement_attachment_v1();

REVOKE ALL ON FUNCTION
    braid.assert_private_object_context_v1(uuid,uuid,boolean),
    braid.lock_private_object_origin_v1(uuid,uuid,uuid,uuid,text,uuid),
    braid.private_publication_owner_live_v1(braid.private_object_publications),
    braid.private_object_cleanup_safety_v1(uuid,uuid,uuid,uuid,text,uuid,uuid),
    braid.guard_private_object_publications_v1(),braid.guard_native_retirement_attachment_v1(),braid.guard_derived_retirement_attachment_v1(),
    braid.guard_private_object_retirements_v1(),braid.guard_private_object_cleanup_attempts_v1(),
    braid.private_publication_record_v1(braid.private_object_publications),
    braid.private_object_cleanup_claim_json_v1(braid.private_object_retirements,braid.private_object_cleanup_attempts),
    braid.canonical_private_object_cleanup_claim_v1(jsonb),
    braid.claim_private_object_cleanup_v1(uuid,uuid,uuid,jsonb),
    braid.finish_private_object_cleanup_v1(uuid,uuid,jsonb,text),
    braid.get_private_object_cleanup_attempt_v1(uuid,uuid,uuid,bigint)
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver,braid_maintenance;
REVOKE ALL ON braid.private_object_publications,braid.private_object_retirements,braid.private_object_cleanup_attempts
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver,braid_maintenance;
GRANT USAGE ON SCHEMA braid TO braid_maintenance;
GRANT SELECT ON braid.private_object_publications,braid.private_object_retirements,braid.private_object_cleanup_attempts TO braid_maintenance;
GRANT EXECUTE ON FUNCTION braid.claim_private_object_cleanup_v1(uuid,uuid,uuid,jsonb),braid.finish_private_object_cleanup_v1(uuid,uuid,jsonb,text),
    braid.get_private_object_cleanup_attempt_v1(uuid,uuid,uuid,bigint) TO braid_maintenance;
GRANT SELECT (organization_id,project_id,mapping_id,revision_id,object_class,object_id,publication_id,root_id,temp_id,format,owner_kind,actor_principal_id,ingestion_operation_id,cache_entry_id,derivation_id,native_object_id,candidate_object_id,owner_generation,owner_instance_id,owner_token,owner_lease_expires_at,absence_record_digest,registered_at,control_namespace_id,linux_boot_id,mount_namespace_device,mount_namespace_inode,effective_user_id,file_inode,file_device_major,file_device_minor,file_mount_id,directory_inode,directory_device_major,directory_device_minor,directory_mount_id,bound_record_digest,bound_at)
    ON braid.private_object_publications TO braid_ingest,braid_deriver;
GRANT SELECT (organization_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,publication_id)
    ON braid.private_object_retirements TO braid_ingest,braid_deriver;
GRANT INSERT (organization_id,project_id,mapping_id,revision_id,object_class,object_id,root_id,temp_id,owner_kind,actor_principal_id,ingestion_operation_id,cache_entry_id,derivation_id,owner_generation,owner_instance_id,owner_token,owner_lease_expires_at,absence_record_digest)
    ON braid.private_object_publications TO braid_ingest,braid_deriver;
GRANT UPDATE (control_namespace_id,linux_boot_id,mount_namespace_device,mount_namespace_inode,effective_user_id,file_inode,file_device_major,file_device_minor,file_mount_id,directory_inode,directory_device_major,directory_device_minor,directory_mount_id,bound_record_digest)
    ON braid.private_object_publications TO braid_ingest,braid_deriver;
