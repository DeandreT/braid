-- UNALLOCATED source preparation: root assigns its numbered name after qualified P4a.
-- Explicit run.payload.read metadata only. No payload bytes, identity or parent grant is created.
-- Reader bootstrap is separate; no role or parent authority is created by this migration.
DO $braid_require_payload_permission_reader$
DECLARE r record;
BEGIN
    IF current_user <> 'braid_migrator' OR session_user <> current_user
        OR current_setting('server_encoding') <> 'UTF8'
        OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Payload permission requires its accepted migration environment.';
    END IF;
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname = 'braid_payload_permission_reader';
    IF NOT FOUND OR NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls
        OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
            WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
              AND (n.nspowner = r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE')))
        OR pg_catalog.has_schema_privilege(r.oid,'braid','USAGE')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='braid' AND c.relkind IN ('r','p','v','m','f')
              AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                   OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
            WHERE n.nspname='braid' AND pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE'))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='braid' AND CASE WHEN c.relkind='S' THEN
                pg_catalog.has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') ELSE false END)
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d
            CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
            WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='braid'))
              AND (d.defaclrole=r.oid OR a.grantee IN (0,r.oid)))
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_parameter_acl p
            CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a WHERE a.grantee IN (0,r.oid)) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Payload permission requires an explicitly provisioned CONNECT-only reader.';
    END IF;
END;
$braid_require_payload_permission_reader$;

CREATE TABLE braid.project_payload_read_grants (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    active boolean NOT NULL DEFAULT true,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT project_payload_read_grants_pkey PRIMARY KEY (organization_id,project_id,principal_id),
    CONSTRAINT project_payload_read_grants_project_fkey FOREIGN KEY (organization_id,project_id)
        REFERENCES braid.projects(organization_id,id) ON DELETE RESTRICT,
    CONSTRAINT project_payload_read_grants_membership_fkey FOREIGN KEY (organization_id,project_id,principal_id)
        REFERENCES braid.project_memberships(organization_id,project_id,principal_id) ON DELETE RESTRICT,
    CONSTRAINT project_payload_read_grants_ids_check CHECK (
        organization_id <> '00000000-0000-0000-0000-000000000000'
        AND project_id <> '00000000-0000-0000-0000-000000000000'
        AND principal_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT project_payload_read_grants_version_check CHECK (version > 0)
);

ALTER TABLE braid.project_payload_read_grants ENABLE ROW LEVEL SECURITY;
CREATE POLICY project_payload_read_grants_organization_scope ON braid.project_payload_read_grants
    USING (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('braid.organization_id', true), '')::uuid);

CREATE FUNCTION braid.guard_project_payload_read_grants_transition() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_project_payload_read_grants_transition$
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Payload grant rows cannot be deleted.';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NOT NEW.active OR NEW.version <> 1 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Payload grants must start active at version one.';
        END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.organization_id,NEW.project_id,NEW.principal_id)
        IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.principal_id) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Payload grant ownership is immutable.';
    END IF;
    IF NEW.active = OLD.active THEN
        IF NEW.version <> OLD.version THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'An unchanged payload grant preserves its version.';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.version = 9223372036854775807 THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Payload grant version is exhausted.';
    END IF;
    IF NEW.version <> OLD.version + 1 THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'A payload grant state change advances exactly one version.';
    END IF;
    RETURN NEW;
END;
$braid_guard_project_payload_read_grants_transition$;

CREATE TRIGGER project_payload_read_grants_transition_guard
    BEFORE INSERT OR UPDATE OR DELETE ON braid.project_payload_read_grants
    FOR EACH ROW EXECUTE FUNCTION braid.guard_project_payload_read_grants_transition();
CREATE TRIGGER project_payload_read_grants_truncate_guard
    BEFORE TRUNCATE ON braid.project_payload_read_grants
    FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_project_payload_read_grants_transition();

REVOKE ALL ON braid.project_payload_read_grants FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,
    braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver,braid_maintenance,braid_identity_reader,
    braid_payload_permission_reader;
REVOKE ALL ON FUNCTION braid.guard_project_payload_read_grants_transition()
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,
         braid_report_reader,braid_deriver,braid_maintenance,braid_identity_reader,braid_payload_permission_reader;
GRANT USAGE ON SCHEMA braid TO braid_payload_permission_reader;
GRANT SELECT (id,active) ON braid.principals TO braid_payload_permission_reader;
GRANT SELECT (organization_id,principal_id,active) ON braid.organization_memberships TO braid_payload_permission_reader;
GRANT SELECT (organization_id,id) ON braid.projects TO braid_payload_permission_reader;
GRANT SELECT (organization_id,project_id,principal_id,role,active)
    ON braid.project_memberships TO braid_payload_permission_reader;
GRANT SELECT (organization_id,project_id,principal_id,active,version)
    ON braid.project_payload_read_grants TO braid_payload_permission_reader;
