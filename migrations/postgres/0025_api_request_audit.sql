-- An append-only audit of mutating API requests that a signed-in user made with a bearer token: the
-- principal, its external identity binding and client application, the operation and the request's
-- resource identifiers. It never records a token, claim, request body or response.
DO $roles$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='weft_api_audit_writer';
    IF current_user<>'weft_migrator' OR session_user<>current_user OR NOT FOUND OR NOT r.rolcanlogin
        OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
        OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace s WHERE s.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,s.oid,'CREATE'))
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace s ON s.oid=c.relnamespace
            WHERE s.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
            AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
        OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE') THEN
        RAISE EXCEPTION 'Independent API audit role required.' USING ERRCODE='42501';
    END IF;
END;
$roles$;

-- One row per admitted mutating bearer request, written before the request runs. Resource names are
-- route parameter names; their values are canonical UUIDs or positive decimal integers, nothing else.
CREATE TABLE weft.api_request_audit (
    organization_id uuid NOT NULL REFERENCES weft.organizations(id),
    audit_id uuid NOT NULL CHECK(audit_id<>'00000000-0000-0000-0000-000000000000'),
    principal_id uuid NOT NULL REFERENCES weft.principals(id),
    binding_id uuid NOT NULL REFERENCES weft.external_identity_bindings(binding_id),
    client_application_id uuid NOT NULL CHECK(client_application_id<>'00000000-0000-0000-0000-000000000000'),
    operation text NOT NULL CHECK(operation~'^[A-Z][A-Za-z0-9]{0,127}$'),
    method text NOT NULL CHECK(method IN ('POST','PUT','PATCH','DELETE')),
    resource_names text[] NOT NULL, resource_values text[] NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,audit_id),
    CHECK(pg_catalog.array_ndims(resource_names) IS NULL OR pg_catalog.array_ndims(resource_names)=1),
    CHECK(pg_catalog.array_ndims(resource_values) IS NULL OR pg_catalog.array_ndims(resource_values)=1),
    CHECK(pg_catalog.cardinality(resource_names)=pg_catalog.cardinality(resource_values) AND pg_catalog.cardinality(resource_names)<=8),
    CHECK(pg_catalog.array_position(resource_names,NULL) IS NULL AND pg_catalog.array_position(resource_values,NULL) IS NULL),
    CHECK(pg_catalog.array_to_string(resource_names,',')~'^([a-z][A-Za-z0-9]{0,63}(,[a-z][A-Za-z0-9]{0,63})*)?$'),
    CHECK(pg_catalog.array_to_string(resource_values,',')
        ~'^(([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[1-9][0-9]{0,18})(,([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[1-9][0-9]{0,18}))*)?$')
);
CREATE INDEX api_request_audit_principal ON weft.api_request_audit(organization_id,principal_id,recorded_at);
ALTER TABLE weft.api_request_audit ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON weft.api_request_audit USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.api_request_audit FROM PUBLIC;

CREATE FUNCTION weft.guard_api_request_audit() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>'weft_api_audit_writer' OR TG_OP<>'INSERT'
        OR NEW.recorded_at<>statement_timestamp() THEN
        RAISE EXCEPTION 'The API request audit is append-only.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION weft.guard_api_request_audit() FROM PUBLIC;
CREATE TRIGGER api_request_audit_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.api_request_audit FOR EACH ROW EXECUTE FUNCTION weft.guard_api_request_audit();
CREATE TRIGGER api_request_audit_no_truncate BEFORE TRUNCATE ON weft.api_request_audit FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_api_request_audit();

-- Status: 0 recorded; 1 the binding is no longer enabled, owned by the principal or the principal is
-- inactive, so the request must not run; 2 refused input. Only 0 writes.
CREATE FUNCTION weft.record_api_request(org uuid, audit uuid, principal uuid, binding uuid, client uuid, operation_name text, http_method text, names text[], resource_ids text[]) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF session_user<>'weft_api_audit_writer' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR audit IS NULL OR audit='00000000-0000-0000-0000-000000000000' OR principal IS NULL OR binding IS NULL
        OR client IS NULL OR client='00000000-0000-0000-0000-000000000000' OR operation_name IS NULL OR http_method IS NULL
        OR names IS NULL OR resource_ids IS NULL THEN
        RETURN 2;
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.external_identity_bindings b JOIN ONLY weft.principals p ON p.id=b.principal_id
        WHERE b.binding_id=binding AND b.principal_id=principal AND b.enabled AND p.active) THEN
        RETURN 1;
    END IF;
    INSERT INTO weft.api_request_audit(organization_id,audit_id,principal_id,binding_id,client_application_id,operation,method,resource_names,resource_values)
    VALUES(org,audit,principal,binding,client,operation_name,http_method,names,resource_ids);
    RETURN 0;
END;
$body$;
REVOKE ALL ON FUNCTION weft.record_api_request(uuid,uuid,uuid,uuid,uuid,text,text,text[],text[]) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_api_audit_writer;
GRANT EXECUTE ON FUNCTION weft.record_api_request(uuid,uuid,uuid,uuid,uuid,text,text,text[],text[]) TO weft_api_audit_writer;
