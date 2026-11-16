-- Provider-neutral identity facts only. No organization, project or payload grant is inferred.
DO $braid_require_identity_reader$
DECLARE r record;
BEGIN
    IF current_user <> 'braid_migrator' OR session_user <> current_user
        OR current_setting('server_encoding') <> 'UTF8'
        OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'External identity requires its accepted migration environment.';
    END IF;
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname = 'braid_identity_reader';
    IF NOT FOUND OR NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls
        OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles x WHERE x.oid <> r.oid AND pg_catalog.pg_has_role(r.oid,x.oid,'MEMBER'))
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
              AND a.grantee IN (0,r.oid)) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'External identity requires an explicitly provisioned CONNECT-only reader.';
    END IF;
END;
$braid_require_identity_reader$;

-- UTF8 PostgreSQL text excludes NUL and invalid scalar encodings. Explicit Cc
-- code points below cover the remaining .NET char.IsControl set independently
-- of database locale. Spaces, case, format characters and composition survive.
CREATE FUNCTION braid.external_identity_key_valid_v1(p_provider text,p_authority text,p_subject text) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE STRICT SECURITY INVOKER
SET search_path = pg_catalog, braid, pg_temp AS $braid_external_identity_key_valid_v1$
DECLARE
    v_controls text := U&'\0001\0002\0003\0004\0005\0006\0007\0008\0009\000A\000B\000C\000D\000E\000F\0010\0011\0012\0013\0014\0015\0016\0017\0018\0019\001A\001B\001C\001D\001E\001F\007F\0080\0081\0082\0083\0084\0085\0086\0087\0088\0089\008A\008B\008C\008D\008E\008F\0090\0091\0092\0093\0094\0095\0096\0097\0098\0099\009A\009B\009C\009D\009E\009F';
BEGIN
    RETURN pg_catalog.octet_length(p_provider) BETWEEN 1 AND 64
        AND p_provider COLLATE "C" ~ '^[a-z][a-z0-9._-]{0,63}$'
        AND p_provider COLLATE "C" !~ '[^a-z0-9._-]'
        AND pg_catalog.octet_length(p_authority) BETWEEN 1 AND 1024
        AND pg_catalog.octet_length(p_subject) BETWEEN 1 AND 512
        AND pg_catalog.translate(p_authority,v_controls,'') COLLATE "C" = p_authority COLLATE "C"
        AND pg_catalog.translate(p_subject,v_controls,'') COLLATE "C" = p_subject COLLATE "C";
END;
$braid_external_identity_key_valid_v1$;

CREATE FUNCTION braid.external_identity_binding_state_valid_v1(
    p_enabled boolean,p_version bigint,p_created_at timestamp with time zone,p_disabled_at timestamp with time zone
) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog, braid, pg_temp AS $braid_external_identity_binding_state_valid_v1$
BEGIN
    IF p_enabled IS NULL OR p_version IS NULL OR p_created_at IS NULL THEN
        RETURN false;
    END IF;
    RETURN p_version > 0 AND pg_catalog.isfinite(p_created_at)
        AND ((p_enabled AND p_disabled_at IS NULL)
             OR (NOT p_enabled AND p_version >= 2 AND p_disabled_at IS NOT NULL
                 AND pg_catalog.isfinite(p_disabled_at) AND p_disabled_at >= p_created_at));
END;
$braid_external_identity_binding_state_valid_v1$;

CREATE TABLE braid.external_identity_bindings (
    binding_id uuid NOT NULL,
    provider text COLLATE "C" NOT NULL,
    authority text COLLATE "C" NOT NULL,
    subject text COLLATE "C" NOT NULL,
    principal_id uuid NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    version bigint NOT NULL DEFAULT 1,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    disabled_at timestamp with time zone,
    CONSTRAINT external_identity_bindings_pkey PRIMARY KEY (binding_id),
    CONSTRAINT external_identity_bindings_key_key UNIQUE (provider,authority,subject),
    CONSTRAINT external_identity_bindings_principal_fkey FOREIGN KEY (principal_id)
        REFERENCES braid.principals(id) ON DELETE RESTRICT,
    CONSTRAINT external_identity_bindings_id_check CHECK (binding_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT external_identity_bindings_key_check CHECK (braid.external_identity_key_valid_v1(provider,authority,subject)),
    CONSTRAINT external_identity_bindings_state_check CHECK (braid.external_identity_binding_state_valid_v1(enabled,version,created_at,disabled_at))
);

CREATE FUNCTION braid.guard_external_identity_bindings_transition() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_external_identity_bindings_transition$
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'External identity bindings cannot be deleted.';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NOT NEW.enabled OR NEW.version <> 1 OR NEW.disabled_at IS NOT NULL THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'External identity bindings must start enabled at version one.';
        END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.binding_id,NEW.provider,NEW.authority,NEW.subject,NEW.principal_id,NEW.created_at)
        IS DISTINCT FROM ROW(OLD.binding_id,OLD.provider,OLD.authority,OLD.subject,OLD.principal_id,OLD.created_at)
        OR NOT OLD.enabled OR NEW.enabled OR NEW.disabled_at IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'External identity ownership is immutable and deactivation is one-way.';
    END IF;
    IF OLD.version = 9223372036854775807 THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'External identity binding version is exhausted.';
    END IF;
    IF NEW.version <> OLD.version + 1 OR NOT pg_catalog.isfinite(NEW.disabled_at) OR NEW.disabled_at < OLD.created_at THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'External identity deactivation facts are inconsistent.';
    END IF;
    RETURN NEW;
END;
$braid_guard_external_identity_bindings_transition$;

CREATE TRIGGER external_identity_bindings_transition_guard
    BEFORE INSERT OR UPDATE OR DELETE ON braid.external_identity_bindings
    FOR EACH ROW EXECUTE FUNCTION braid.guard_external_identity_bindings_transition();
CREATE TRIGGER external_identity_bindings_truncate_guard
    BEFORE TRUNCATE ON braid.external_identity_bindings
    FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_external_identity_bindings_transition();

REVOKE ALL ON braid.external_identity_bindings FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,
    braid_catalog_editor,braid_project_creator,braid_report_reader,braid_deriver,braid_maintenance,braid_identity_reader;
REVOKE ALL ON FUNCTION braid.external_identity_key_valid_v1(text,text,text),
    braid.external_identity_binding_state_valid_v1(boolean,bigint,timestamp with time zone,timestamp with time zone),
    braid.guard_external_identity_bindings_transition()
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator,
         braid_report_reader,braid_deriver,braid_maintenance,braid_identity_reader;
GRANT USAGE ON SCHEMA braid TO braid_identity_reader;
GRANT SELECT (binding_id,provider,authority,subject,principal_id,enabled,version,created_at,disabled_at)
    ON braid.external_identity_bindings TO braid_identity_reader;
GRANT SELECT (id,active) ON braid.principals TO braid_identity_reader;
