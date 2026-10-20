-- Prepared schema, not part of the active ordered migration manifest.
-- Integrate after the qualified preview persistence/job migrations 0012 and 0013.
-- Both case roles must be separately provisioned CONNECT-only logins before migration.
DO $case_roles$
DECLARE role_name text; r record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user OR current_setting('server_encoding') <> 'UTF8' THEN
        RAISE EXCEPTION 'Regression migration requires its explicit owner.' USING ERRCODE='42501';
    END IF;
    FOREACH role_name IN ARRAY ARRAY['weft_case_reader','weft_case_writer'] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=role_name;
        IF NOT FOUND OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole
            OR r.rolreplication OR r.rolbypassrls OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL
            OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
            OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND (f.proowner=r.oid OR pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE'))) THEN
            RAISE EXCEPTION 'Regression roles must be explicit CONNECT-only logins.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$case_roles$;

CREATE TABLE weft.regression_cases (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    case_id uuid NOT NULL CHECK(case_id<>'00000000-0000-0000-0000-000000000000'),
    creator_id uuid NOT NULL REFERENCES weft.principals(id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    head_version bigint NOT NULL CHECK(head_version>0),
    PRIMARY KEY(organization_id,project_id,mapping_id,case_id),
    UNIQUE(organization_id,project_id,mapping_id,case_id,creator_id),
    FOREIGN KEY(organization_id,project_id,mapping_id) REFERENCES weft.mappings(organization_id,project_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT
);

CREATE TABLE weft.regression_case_versions (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    case_id uuid NOT NULL,
    version bigint NOT NULL CHECK(version>0),
    creator_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    native_digest text NOT NULL CHECK(native_digest~'^[0-9a-f]{64}$'),
    name text COLLATE "C" NOT NULL CHECK(octet_length(name) BETWEEN 1 AND 256 AND btrim(name)<>'' AND name!~'[[:cntrl:]]'),
    content_digest text NOT NULL CHECK(content_digest~'^[0-9a-f]{64}$'),
    byte_count integer NOT NULL CHECK(byte_count BETWEEN 1 AND 17039684),
    envelope bytea NOT NULL,
    saved_by uuid NOT NULL REFERENCES weft.principals(id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at) AND created_at>='0001-01-01+00'::timestamptz AND created_at<'10000-01-01+00'::timestamptz),
    PRIMARY KEY(organization_id,project_id,mapping_id,case_id,version),
    FOREIGN KEY(organization_id,project_id,mapping_id,case_id,creator_id) REFERENCES weft.regression_cases(organization_id,project_id,mapping_id,case_id,creator_id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    FOREIGN KEY(organization_id,project_id,mapping_id,revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id) ON DELETE RESTRICT ON UPDATE RESTRICT,
    CHECK(octet_length(envelope)=byte_count AND pg_catalog.encode(pg_catalog.sha256(envelope),'hex')=content_digest)
);
ALTER TABLE weft.regression_cases ADD CONSTRAINT regression_case_head FOREIGN KEY(organization_id,project_id,mapping_id,case_id,head_version)
    REFERENCES weft.regression_case_versions(organization_id,project_id,mapping_id,case_id,version) DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE weft.regression_cases ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.regression_case_versions ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON weft.regression_cases USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.regression_case_versions USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.regression_cases,weft.regression_case_versions FROM PUBLIC;

CREATE FUNCTION weft.case_authorized(org uuid, project uuid, actor uuid, allowed text[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid
        AND actor=NULLIF(current_setting('weft.case_principal_id',true),'')::uuid
        AND EXISTS(SELECT 1 FROM ONLY weft.principals p
            JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=org AND o.active
            JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=org AND m.project_id=project AND m.active
            JOIN ONLY weft.projects pr ON pr.organization_id=org AND pr.id=project
            WHERE p.id=actor AND p.active AND m.role=ANY(allowed))
$body$;
REVOKE ALL ON FUNCTION weft.case_authorized(uuid,uuid,uuid,text[]) FROM PUBLIC;

CREATE FUNCTION weft.guard_regression_case() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF TG_OP IN ('DELETE','TRUNCATE') OR current_user<>'weft_migrator' OR session_user<>'weft_case_writer' THEN
        RAISE EXCEPTION 'Regression case mutation is not admitted.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='UPDATE' AND (ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.case_id,NEW.creator_id)
        IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.case_id,OLD.creator_id)
        OR OLD.head_version=9223372036854775807 OR NEW.head_version<>OLD.head_version+1) THEN
        RAISE EXCEPTION 'Regression case identity is immutable.' USING ERRCODE='23514';
    END IF;
    IF TG_OP='INSERT' AND NEW.head_version<>1 THEN RAISE EXCEPTION 'Regression case starts at version one.' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION weft.guard_regression_case() FROM PUBLIC;
CREATE TRIGGER regression_case_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.regression_cases FOR EACH ROW EXECUTE FUNCTION weft.guard_regression_case();
CREATE TRIGGER regression_case_truncate_guard BEFORE TRUNCATE ON weft.regression_cases FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_regression_case();

CREATE FUNCTION weft.guard_regression_version() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF TG_OP<>'INSERT' OR current_user<>'weft_migrator' OR session_user<>'weft_case_writer' THEN
        RAISE EXCEPTION 'Regression case versions are immutable.' USING ERRCODE='42501';
    END IF;
    IF NEW.created_at<>statement_timestamp() OR NEW.saved_by<>NULLIF(current_setting('weft.case_principal_id',true),'')::uuid THEN
        RAISE EXCEPTION 'Regression version audit is invalid.' USING ERRCODE='23514';
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.mapping_revisions r WHERE r.organization_id=NEW.organization_id AND r.project_id=NEW.project_id
        AND r.mapping_id=NEW.mapping_id AND r.id=NEW.revision_id AND r.native_digest=NEW.native_digest) THEN
        RAISE EXCEPTION 'Regression native revision does not match.' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION weft.guard_regression_version() FROM PUBLIC;
CREATE TRIGGER regression_version_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.regression_case_versions FOR EACH ROW EXECUTE FUNCTION weft.guard_regression_version();
CREATE TRIGGER regression_version_truncate_guard BEFORE TRUNCATE ON weft.regression_case_versions FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_regression_version();

CREATE FUNCTION weft.save_regression_case(org uuid, project uuid, mapping uuid, id uuid, requested_version bigint, creator uuid,
    revision uuid, native_sha text, case_name text, content_sha text, content_bytes integer, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; c weft.regression_cases%ROWTYPE; existing weft.regression_case_versions%ROWTYPE; inserted boolean;
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>'weft_case_writer' THEN RAISE EXCEPTION 'Regression writer required.' USING ERRCODE='42501'; END IF;
    actor:=NULLIF(current_setting('weft.case_principal_id',true),'')::uuid;
    IF org IS NULL OR project IS NULL OR mapping IS NULL OR id IS NULL OR requested_version IS NULL OR requested_version<1 OR actor IS NULL
        OR creator IS NULL OR revision IS NULL OR native_sha IS NULL OR case_name IS NULL OR content_sha IS NULL OR content_bytes IS NULL OR body IS NULL THEN
        RAISE EXCEPTION 'Regression save input is incomplete.' USING ERRCODE='23514';
    END IF;
    IF content_bytes NOT BETWEEN 1 AND 17039684 OR octet_length(body)<>content_bytes OR octet_length(case_name) NOT BETWEEN 1 AND 256
        OR octet_length(native_sha)<>64 OR octet_length(content_sha)<>64 THEN
        RAISE EXCEPTION 'Regression save input exceeds its bounds.' USING ERRCODE='23514';
    END IF;
    -- Keep fresh authority stable across any later case-row contention. Parent lock order matches ownership writes.
    PERFORM 1 FROM ONLY weft.principals p WHERE p.id=actor FOR SHARE;
    PERFORM 1 FROM ONLY weft.organizations o WHERE o.id=org FOR SHARE;
    PERFORM 1 FROM ONLY weft.organization_memberships o WHERE o.organization_id=org AND o.principal_id=actor FOR SHARE;
    PERFORM 1 FROM ONLY weft.projects p WHERE p.organization_id=org AND p.id=project FOR SHARE;
    PERFORM 1 FROM ONLY weft.project_memberships m WHERE m.organization_id=org AND m.project_id=project AND m.principal_id=actor FOR SHARE;
    IF weft.case_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 0; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.mapping_revisions r WHERE r.organization_id=org AND r.project_id=project
        AND r.mapping_id=mapping AND r.id=revision AND r.native_digest=native_sha) THEN RETURN 0; END IF;
    inserted:=false;
    IF requested_version=1 AND creator=actor THEN
        INSERT INTO weft.regression_cases(organization_id,project_id,mapping_id,case_id,creator_id,head_version)
            VALUES(org,project,mapping,id,creator,1) ON CONFLICT DO NOTHING;
        inserted:=FOUND;
    END IF;
    SELECT * INTO c FROM ONLY weft.regression_cases r WHERE r.organization_id=org AND r.project_id=project AND r.mapping_id=mapping AND r.case_id=id FOR UPDATE;
    IF NOT FOUND OR c.creator_id<>creator THEN RETURN 4; END IF;
    SELECT * INTO existing FROM ONLY weft.regression_case_versions v WHERE v.organization_id=org AND v.project_id=project
        AND v.mapping_id=mapping AND v.case_id=id AND v.version=requested_version;
    IF FOUND THEN
        IF ROW(existing.creator_id,existing.revision_id,existing.native_digest,existing.name,existing.content_digest,existing.byte_count,existing.envelope)
            IS NOT DISTINCT FROM ROW(creator,revision,native_sha,case_name,content_sha,content_bytes,body) THEN RETURN 3; END IF;
        RETURN 4;
    END IF;
    IF NOT inserted AND (c.head_version=9223372036854775807 OR requested_version<>c.head_version+1) THEN RETURN 4; END IF;
    INSERT INTO weft.regression_case_versions(organization_id,project_id,mapping_id,case_id,version,creator_id,revision_id,native_digest,name,content_digest,byte_count,envelope,saved_by)
        VALUES(org,project,mapping,id,requested_version,creator,revision,native_sha,case_name,content_sha,content_bytes,body,actor);
    IF inserted THEN RETURN 1; END IF;
    UPDATE weft.regression_cases r SET head_version=requested_version WHERE r.organization_id=org AND r.project_id=project AND r.mapping_id=mapping AND r.case_id=id;
    RETURN 2;
END;
$body$;
REVOKE ALL ON FUNCTION weft.save_regression_case(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,text,text,integer,bytea) FROM PUBLIC;

CREATE FUNCTION weft.read_regression_case(org uuid, project uuid, mapping uuid, id uuid, requested_version bigint)
RETURNS TABLE(creator_id uuid,revision_id uuid,native_digest text,content_digest text,byte_count integer,envelope bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT v.creator_id,v.revision_id,v.native_digest,v.content_digest,v.byte_count,v.envelope
    FROM ONLY weft.regression_case_versions v
    WHERE current_user='weft_migrator' AND session_user IN ('weft_case_reader','weft_case_writer')
        AND weft.case_authorized(org,project,NULLIF(current_setting('weft.case_principal_id',true),'')::uuid,ARRAY['runner','publisher','administrator'])
        AND v.organization_id=org AND v.project_id=project AND v.mapping_id=mapping AND v.case_id=id AND v.version=requested_version
$body$;
REVOKE ALL ON FUNCTION weft.read_regression_case(uuid,uuid,uuid,uuid,bigint) FROM PUBLIC;

CREATE FUNCTION weft.list_regression_cases(org uuid, project uuid, mapping uuid, after_id uuid, page_size integer)
RETURNS TABLE(case_id uuid,version bigint,creator_id uuid,revision_id uuid,name text,saved_by uuid,created_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT c.case_id,v.version,c.creator_id,v.revision_id,v.name,v.saved_by,v.created_at
    FROM ONLY weft.regression_cases c JOIN ONLY weft.regression_case_versions v
        ON (v.organization_id,v.project_id,v.mapping_id,v.case_id,v.version)=(c.organization_id,c.project_id,c.mapping_id,c.case_id,c.head_version)
    WHERE current_user='weft_migrator' AND session_user IN ('weft_case_reader','weft_case_writer')
        AND page_size BETWEEN 1 AND 101
        AND weft.case_authorized(org,project,NULLIF(current_setting('weft.case_principal_id',true),'')::uuid,ARRAY['viewer','runner','publisher','administrator'])
        AND c.organization_id=org AND c.project_id=project AND c.mapping_id=mapping AND (after_id IS NULL OR c.case_id>after_id)
    ORDER BY c.case_id LIMIT greatest(0,least(page_size,101))
$body$;
REVOKE ALL ON FUNCTION weft.list_regression_cases(uuid,uuid,uuid,uuid,integer) FROM PUBLIC;

GRANT USAGE ON SCHEMA weft TO weft_case_reader,weft_case_writer;
GRANT EXECUTE ON FUNCTION weft.read_regression_case(uuid,uuid,uuid,uuid,bigint),weft.list_regression_cases(uuid,uuid,uuid,uuid,integer) TO weft_case_reader,weft_case_writer;
GRANT EXECUTE ON FUNCTION weft.save_regression_case(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,text,text,integer,bytea) TO weft_case_writer;
