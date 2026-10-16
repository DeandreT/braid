-- Private durable metadata only. Both logins are provisioned separately with CONNECT only.
-- No authentication registration, provider facts, ticket codec or deletion authority lives here.
DO $weft_require_session_profiles$
DECLARE
    profile_name text;
    r record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user
        OR current_setting('server_encoding') <> 'UTF8'
        OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Server sessions require the accepted migration environment.';
    END IF;
    FOREACH profile_name IN ARRAY ARRAY['weft_session_reader','weft_session_writer']::text[] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname = profile_name;
        IF NOT FOUND OR NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls
            OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
            OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
                WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                    AND (n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE')))
            OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE')
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                WHERE n.nspname='weft' AND pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE'))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND CASE WHEN c.relkind='S' THEN
                    pg_catalog.has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') ELSE false END)
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl d
                CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft'))
                    AND (d.defaclrole=r.oid OR a.grantee IN (0,r.oid)))
            OR EXISTS (SELECT 1 FROM pg_catalog.pg_parameter_acl p
                CROSS JOIN LATERAL pg_catalog.aclexplode(p.paracl) a WHERE a.grantee IN (0,r.oid)) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Server sessions require two explicitly provisioned CONNECT-only profiles.';
        END IF;
    END LOOP;
END;
$weft_require_session_profiles$;

CREATE TABLE weft.server_sessions (
    handle_digest bytea NOT NULL,
    organization_id uuid NOT NULL,
    binding_id uuid NOT NULL,
    binding_version bigint NOT NULL,
    login_epoch uuid NOT NULL,
    issued_at timestamptz NOT NULL,
    idle_expires_at timestamptz NOT NULL,
    absolute_expires_at timestamptz NOT NULL,
    version bigint NOT NULL,
    revoked_at timestamptz,
    policy_version integer NOT NULL,
    codec_version integer NOT NULL,
    protected_ticket bytea NOT NULL,
    CONSTRAINT server_sessions_pkey PRIMARY KEY (handle_digest),
    CONSTRAINT server_sessions_organization_fkey FOREIGN KEY (organization_id)
        REFERENCES weft.organizations(id) ON DELETE RESTRICT ON UPDATE NO ACTION NOT DEFERRABLE,
    CONSTRAINT server_sessions_binding_fkey FOREIGN KEY (binding_id)
        REFERENCES weft.external_identity_bindings(binding_id) ON DELETE RESTRICT ON UPDATE NO ACTION NOT DEFERRABLE,
    CONSTRAINT server_sessions_digest_check CHECK (octet_length(handle_digest) = 32),
    CONSTRAINT server_sessions_ids_check CHECK (
        organization_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND binding_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND login_epoch <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT server_sessions_versions_check CHECK (
        binding_version > 0 AND version > 0 AND policy_version = 1 AND codec_version = 1),
    CONSTRAINT server_sessions_instants_check CHECK (
        isfinite(issued_at) AND issued_at > '0001-01-01 00:00:00+00'::timestamptz
        AND issued_at <= '9999-12-31 23:59:59.999999+00'::timestamptz
        AND isfinite(idle_expires_at) AND idle_expires_at > '0001-01-01 00:00:00+00'::timestamptz
        AND idle_expires_at <= '9999-12-31 23:59:59.999999+00'::timestamptz
        AND isfinite(absolute_expires_at) AND absolute_expires_at > '0001-01-01 00:00:00+00'::timestamptz
        AND absolute_expires_at <= '9999-12-31 23:59:59.999999+00'::timestamptz),
    CONSTRAINT server_sessions_lifetime_check CHECK (
        issued_at < idle_expires_at AND idle_expires_at <= absolute_expires_at
        AND absolute_expires_at - issued_at <= interval '60 minutes'),
    CONSTRAINT server_sessions_revocation_check CHECK (
        revoked_at IS NULL OR (
            isfinite(revoked_at) AND revoked_at > '0001-01-01 00:00:00+00'::timestamptz
            AND revoked_at <= '9999-12-31 23:59:59.999999+00'::timestamptz
            AND revoked_at >= issued_at AND version >= 2)),
    CONSTRAINT server_sessions_ticket_check CHECK (octet_length(protected_ticket) BETWEEN 1 AND 16384)
);

CREATE INDEX server_sessions_organization ON weft.server_sessions (organization_id);
CREATE INDEX server_sessions_binding ON weft.server_sessions (binding_id);

ALTER TABLE weft.server_sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY server_sessions_organization_scope ON weft.server_sessions
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);

CREATE FUNCTION weft.guard_server_sessions_transition() RETURNS trigger
LANGUAGE plpgsql STABLE PARALLEL UNSAFE SECURITY INVOKER
SET search_path = pg_catalog, weft, pg_temp
AS $weft_guard_server_sessions_transition$
DECLARE
    sampled_at timestamptz := statement_timestamp();
    proposed_idle timestamptz;
BEGIN
    IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION 'Server session tombstones cannot be removed.' USING ERRCODE = '23514';
    END IF;
    IF NOT isfinite(sampled_at)
        OR sampled_at <= '0001-01-01 00:00:00+00'::timestamptz
        OR sampled_at > '9999-12-31 23:59:59.999999+00'::timestamptz THEN
        RAISE EXCEPTION 'Server session statement time is unsupported.' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        -- Historical, already-expired coherent metadata is permitted; future issuance is not.
        -- The normal store supplies exactly this statement sample for issuance.
        IF NEW.version <> 1 OR NEW.revoked_at IS NOT NULL OR NEW.issued_at > sampled_at
            OR NEW.issued_at > '9999-12-31 22:59:59.999999+00'::timestamptz
            OR NEW.idle_expires_at IS DISTINCT FROM least(NEW.issued_at + interval '20 minutes', NEW.absolute_expires_at)
            OR NEW.absolute_expires_at > NEW.issued_at + interval '60 minutes' THEN
            RAISE EXCEPTION 'Server session creation facts are inconsistent.' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM weft.external_identity_bindings b
            JOIN weft.principals p ON p.id = b.principal_id
            WHERE b.binding_id = NEW.binding_id AND b.enabled AND b.version = NEW.binding_version AND p.active
        ) THEN
            RAISE EXCEPTION 'Server session creation requires current identity facts.' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.handle_digest, NEW.organization_id, NEW.binding_id, NEW.binding_version,
            NEW.login_epoch, NEW.issued_at, NEW.absolute_expires_at, NEW.policy_version,
            NEW.codec_version, NEW.protected_ticket)
        IS DISTINCT FROM ROW(OLD.handle_digest, OLD.organization_id, OLD.binding_id, OLD.binding_version,
            OLD.login_epoch, OLD.issued_at, OLD.absolute_expires_at, OLD.policy_version,
            OLD.codec_version, OLD.protected_ticket) THEN
        RAISE EXCEPTION 'Server session issued facts are immutable.' USING ERRCODE = '23514';
    END IF;
    IF NEW IS NOT DISTINCT FROM OLD THEN
        RETURN NEW;
    END IF;
    IF OLD.revoked_at IS NOT NULL OR OLD.version = 9223372036854775807 THEN
        RAISE EXCEPTION 'Server session transition cannot advance.' USING ERRCODE = '23514';
    END IF;
    IF NEW.version IS DISTINCT FROM OLD.version + 1 THEN
        RAISE EXCEPTION 'Server session changes advance exactly once.' USING ERRCODE = '23514';
    END IF;
    IF NEW.revoked_at IS NOT NULL THEN
        -- Revocation deliberately does not read or require active identity/organization parents.
        IF NEW.revoked_at IS DISTINCT FROM sampled_at OR sampled_at < OLD.issued_at
            OR NEW.idle_expires_at IS DISTINCT FROM OLD.idle_expires_at THEN
            RAISE EXCEPTION 'Server session revocation facts are inconsistent.' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF sampled_at < OLD.issued_at OR sampled_at >= OLD.idle_expires_at
        OR sampled_at >= OLD.absolute_expires_at
        OR sampled_at > '9999-12-31 23:39:59.999999+00'::timestamptz THEN
        RAISE EXCEPTION 'Server session renewal requires a live supported statement time.' USING ERRCODE = '23514';
    END IF;
    proposed_idle := greatest(OLD.idle_expires_at,
        least(sampled_at + interval '20 minutes', OLD.absolute_expires_at));
    IF proposed_idle <= OLD.idle_expires_at OR NEW.idle_expires_at IS DISTINCT FROM proposed_idle THEN
        RAISE EXCEPTION 'Server session renewal exceeds its fixed bounds.' USING ERRCODE = '23514';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM weft.external_identity_bindings b
        JOIN weft.principals p ON p.id = b.principal_id
        WHERE b.binding_id = OLD.binding_id AND b.enabled AND b.version = OLD.binding_version AND p.active
    ) THEN
        RAISE EXCEPTION 'Server session renewal requires current identity facts.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$weft_guard_server_sessions_transition$;

REVOKE ALL ON FUNCTION weft.guard_server_sessions_transition() FROM PUBLIC;
CREATE TRIGGER server_sessions_transition_guard
    BEFORE INSERT OR UPDATE OR DELETE ON weft.server_sessions
    FOR EACH ROW EXECUTE FUNCTION weft.guard_server_sessions_transition();
CREATE TRIGGER server_sessions_truncate_guard
    BEFORE TRUNCATE ON weft.server_sessions
    FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_server_sessions_transition();

GRANT USAGE ON SCHEMA weft TO weft_session_reader, weft_session_writer;
GRANT SELECT (handle_digest, organization_id, binding_id, binding_version, login_epoch, issued_at,
    idle_expires_at, absolute_expires_at, version, revoked_at, policy_version, codec_version, protected_ticket)
    ON weft.server_sessions TO weft_session_reader, weft_session_writer;
GRANT INSERT (handle_digest, organization_id, binding_id, binding_version, login_epoch, issued_at,
    idle_expires_at, absolute_expires_at, version, revoked_at, policy_version, codec_version, protected_ticket)
    ON weft.server_sessions TO weft_session_writer;
GRANT UPDATE (idle_expires_at, version, revoked_at) ON weft.server_sessions TO weft_session_writer;
GRANT SELECT (binding_id, principal_id, enabled, version) ON weft.external_identity_bindings
    TO weft_session_reader, weft_session_writer;
GRANT SELECT (id, active) ON weft.principals TO weft_session_reader, weft_session_writer;
GRANT SELECT (id) ON weft.organizations TO weft_session_writer;
