-- Prepared independently; activate only after the ordered P7 and saved-case migrations qualify.
DO $roles$
DECLARE r record; role_name text;
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>current_user THEN RAISE EXCEPTION 'Run migration owner required.' USING ERRCODE='42501'; END IF;
    FOREACH role_name IN ARRAY ARRAY['weft_run_reader','weft_run_writer'] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=role_name;
        IF NOT FOUND OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
            OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE')) THEN
            RAISE EXCEPTION 'Run roles must be CONNECT-only.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE weft.regression_runs (
    organization_id uuid NOT NULL,
    run_id uuid NOT NULL CHECK(run_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    submitter_id uuid NOT NULL REFERENCES weft.principals(id),
    case_id uuid NOT NULL,
    version bigint NOT NULL,
    creator_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    native_digest text NOT NULL CHECK(native_digest~'^[0-9a-f]{64}$'),
    case_digest text NOT NULL CHECK(case_digest~'^[0-9a-f]{64}$'),
    case_bytes integer NOT NULL CHECK(case_bytes BETWEEN 1 AND 17039684),
    state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','completed','canceled','exhausted','revoked')),
    attempt integer NOT NULL DEFAULT 0 CHECK(attempt BETWEEN 0 AND 3),
    created_at timestamptz NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at)),
    eligible_at timestamptz NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(eligible_at)),
    worker_id uuid,
    claim_token uuid,
    lease_expires timestamptz,
    result_digest text,
    result_bytes integer,
    result_body bytea,
    PRIMARY KEY(organization_id,run_id),
    FOREIGN KEY(organization_id,project_id,mapping_id,case_id,version)
        REFERENCES weft.regression_case_versions(organization_id,project_id,mapping_id,case_id,version),
    CHECK((state='running')=(worker_id IS NOT NULL AND claim_token IS NOT NULL AND lease_expires IS NOT NULL)),
    CHECK(state='running' OR (worker_id IS NULL AND claim_token IS NULL AND lease_expires IS NULL)),
    CHECK(worker_id IS NULL OR worker_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(claim_token IS NULL OR claim_token<>'00000000-0000-0000-0000-000000000000'),
    CHECK(lease_expires IS NULL OR isfinite(lease_expires)),
    CHECK((state='completed')=(result_body IS NOT NULL AND result_digest IS NOT NULL AND result_bytes IS NOT NULL)),
    CHECK(state='completed' OR (result_body IS NULL AND result_digest IS NULL AND result_bytes IS NULL)),
    CHECK(result_body IS NULL OR (result_bytes BETWEEN 1 AND 12582912 AND octet_length(result_body)=result_bytes
        AND result_digest~'^[0-9a-f]{64}$' AND encode(sha256(result_body),'hex')=result_digest))
);
ALTER TABLE weft.regression_runs ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON weft.regression_runs USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.regression_runs FROM PUBLIC;

CREATE FUNCTION weft.regression_run_authorized(org uuid, project uuid, actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND actor IS NOT NULL
        AND EXISTS(SELECT 1 FROM ONLY weft.principals p
            JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=org AND o.active
            JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=org AND m.project_id=project AND m.active
            JOIN ONLY weft.projects pr ON pr.organization_id=org AND pr.id=project
            WHERE p.id=actor AND p.active AND m.role IN ('runner','publisher','administrator'))
$body$;

CREATE FUNCTION weft.guard_regression_run() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF TG_OP IN ('DELETE','TRUNCATE') OR current_user<>'weft_migrator' OR session_user<>'weft_run_writer' THEN
        RAISE EXCEPTION 'Run mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='INSERT' THEN
        IF NEW.state<>'queued' OR NEW.attempt<>0 OR NEW.created_at<>statement_timestamp() OR NEW.eligible_at<>statement_timestamp() THEN
            RAISE EXCEPTION 'Run initial state invalid.' USING ERRCODE='23514';
        END IF;
    ELSE
        IF OLD.state NOT IN ('queued','running') OR ROW(NEW.organization_id,NEW.run_id,NEW.project_id,NEW.mapping_id,NEW.submitter_id,
            NEW.case_id,NEW.version,NEW.creator_id,NEW.revision_id,NEW.native_digest,NEW.case_digest,NEW.case_bytes,NEW.created_at)
            IS DISTINCT FROM ROW(OLD.organization_id,OLD.run_id,OLD.project_id,OLD.mapping_id,OLD.submitter_id,
            OLD.case_id,OLD.version,OLD.creator_id,OLD.revision_id,OLD.native_digest,OLD.case_digest,OLD.case_bytes,OLD.created_at)
            OR NEW.attempt<OLD.attempt OR NEW.attempt>OLD.attempt+1
            OR (NEW.attempt<>OLD.attempt AND NEW.state<>'running') THEN
            RAISE EXCEPTION 'Run identity or terminal result is immutable.' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.regression_case_versions c WHERE c.organization_id=NEW.organization_id
        AND c.project_id=NEW.project_id AND c.mapping_id=NEW.mapping_id AND c.case_id=NEW.case_id AND c.version=NEW.version
        AND c.creator_id=NEW.creator_id AND c.revision_id=NEW.revision_id AND c.native_digest=NEW.native_digest
        AND c.content_digest=NEW.case_digest AND c.byte_count=NEW.case_bytes) THEN
        RAISE EXCEPTION 'Run case identity mismatch.' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER regression_run_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.regression_runs FOR EACH ROW EXECUTE FUNCTION weft.guard_regression_run();
CREATE TRIGGER regression_run_truncate_guard BEFORE TRUNCATE ON weft.regression_runs FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_regression_run();

CREATE FUNCTION weft.submit_regression_run(org uuid, project uuid, mapping uuid, id uuid, case_key uuid, case_version bigint,
    creator uuid, revision uuid, native_sha text, content_sha text, content_bytes integer) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; r weft.regression_runs%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR current_user<>'weft_migrator' THEN RAISE EXCEPTION 'Run writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN 3; END IF;
    actor:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid;
    -- Serialize a submitter's admission and preserve the established authority lock order.
    PERFORM 1 FROM ONLY weft.principals p WHERE p.id=actor FOR UPDATE;
    PERFORM 1 FROM ONLY weft.organizations o WHERE o.id=org FOR SHARE;
    PERFORM 1 FROM ONLY weft.organization_memberships o WHERE o.organization_id=org AND o.principal_id=actor FOR SHARE;
    PERFORM 1 FROM ONLY weft.projects p WHERE p.organization_id=org AND p.id=project FOR SHARE;
    PERFORM 1 FROM ONLY weft.project_memberships m WHERE m.organization_id=org AND m.project_id=project AND m.principal_id=actor FOR SHARE;
    IF weft.regression_run_authorized(org,project,actor) IS NOT TRUE THEN RETURN 3; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.regression_case_versions c WHERE c.organization_id=org AND c.project_id=project
        AND c.mapping_id=mapping AND c.case_id=case_key AND c.version=case_version AND c.creator_id=creator
        AND c.revision_id=revision AND c.native_digest=native_sha AND c.content_digest=content_sha AND c.byte_count=content_bytes) THEN RETURN 3; END IF;
    SELECT * INTO r FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.run_id=id FOR UPDATE;
    IF FOUND THEN
        IF ROW(r.project_id,r.mapping_id,r.submitter_id,r.case_id,r.version,r.case_digest,r.case_bytes)
            =ROW(project,mapping,actor,case_key,case_version,content_sha,content_bytes) THEN RETURN 1; END IF;
        RETURN 2;
    END IF;
    IF (SELECT count(*) FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.submitter_id=actor AND x.state IN ('queued','running'))>=32 THEN RETURN 4; END IF;
    INSERT INTO weft.regression_runs(organization_id,run_id,project_id,mapping_id,submitter_id,case_id,version,creator_id,revision_id,native_digest,case_digest,case_bytes)
        VALUES(org,id,project,mapping,actor,case_key,case_version,creator,revision,native_sha,content_sha,content_bytes)
        ON CONFLICT(organization_id,run_id) DO NOTHING;
    IF FOUND THEN RETURN 0; END IF;
    SELECT * INTO r FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.run_id=id FOR UPDATE;
    IF ROW(r.project_id,r.mapping_id,r.submitter_id,r.case_id,r.version,r.case_digest,r.case_bytes)
        =ROW(project,mapping,actor,case_key,case_version,content_sha,content_bytes) THEN RETURN 1; END IF;
    RETURN 2;
END;
$body$;

CREATE FUNCTION weft.claim_regression_run(org uuid, id uuid, worker uuid)
RETURNS TABLE(outcome integer, attempt_number integer, lease_token uuid, expires_at timestamptz, project uuid, mapping uuid,
    case_key uuid, case_version bigint, creator uuid, revision uuid, native_sha text, content_sha text, content_bytes integer, actor uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r weft.regression_runs%ROWTYPE; c weft.regression_case_versions%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR current_user<>'weft_migrator' OR worker IS NULL OR worker='00000000-0000-0000-0000-000000000000' THEN
        RAISE EXCEPTION 'Run worker required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN outcome:=3; RETURN NEXT; RETURN; END IF;
    SELECT * INTO r FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.run_id=id FOR UPDATE;
    IF NOT FOUND THEN outcome:=3; RETURN NEXT; RETURN; END IF;
    IF r.state NOT IN ('queued','running') THEN outcome:=2; RETURN NEXT; RETURN; END IF;
    IF r.state='running' AND r.lease_expires>statement_timestamp() OR r.eligible_at>statement_timestamp() THEN outcome:=1; RETURN NEXT; RETURN; END IF;
    IF weft.regression_run_authorized(org,r.project_id,r.submitter_id) IS NOT TRUE THEN
        UPDATE weft.regression_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL WHERE organization_id=org AND run_id=id;
        outcome:=2; RETURN NEXT; RETURN;
    END IF;
    IF r.attempt>=3 THEN
        UPDATE weft.regression_runs SET state='exhausted',worker_id=NULL,claim_token=NULL,lease_expires=NULL WHERE organization_id=org AND run_id=id;
        outcome:=2; RETURN NEXT; RETURN;
    END IF;
    SELECT * INTO c FROM ONLY weft.regression_case_versions x WHERE x.organization_id=org AND x.project_id=r.project_id AND x.mapping_id=r.mapping_id AND x.case_id=r.case_id AND x.version=r.version;
    outcome:=0; attempt_number:=r.attempt+1; lease_token:=gen_random_uuid(); expires_at:=statement_timestamp()+interval '90 seconds';
    project:=r.project_id; mapping:=r.mapping_id; case_key:=r.case_id; case_version:=r.version; creator:=c.creator_id;
    revision:=c.revision_id; native_sha:=c.native_digest; content_sha:=r.case_digest; content_bytes:=r.case_bytes; actor:=r.submitter_id;
    UPDATE weft.regression_runs SET state='running',attempt=attempt_number,worker_id=worker,claim_token=lease_token,lease_expires=expires_at
        WHERE organization_id=org AND run_id=id;
    RETURN NEXT;
END;
$body$;

CREATE FUNCTION weft.observe_regression_run(org uuid, id uuid, worker uuid, token uuid, generation integer) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT session_user='weft_run_writer' AND org=NULLIF(current_setting('weft.organization_id',true),'')::uuid
        AND EXISTS(SELECT 1 FROM ONLY weft.regression_runs r WHERE r.organization_id=org
        AND r.run_id=id AND r.state='running' AND r.worker_id=worker AND r.claim_token=token AND r.attempt=generation
        AND r.lease_expires>statement_timestamp() AND weft.regression_run_authorized(org,r.project_id,r.submitter_id))
$body$;

CREATE FUNCTION weft.finish_regression_run(org uuid, id uuid, worker uuid, token uuid, generation integer, body bytea, sha text, length integer) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r weft.regression_runs%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR current_user<>'weft_migrator' THEN RAISE EXCEPTION 'Run writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN false; END IF;
    SELECT * INTO r FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.run_id=id FOR UPDATE;
    IF NOT FOUND OR r.state<>'running' OR r.worker_id IS DISTINCT FROM worker OR r.claim_token IS DISTINCT FROM token
        OR r.attempt IS DISTINCT FROM generation OR r.lease_expires<=statement_timestamp() THEN RETURN false; END IF;
    IF weft.regression_run_authorized(org,r.project_id,r.submitter_id) IS NOT TRUE THEN
        UPDATE weft.regression_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL WHERE organization_id=org AND run_id=id;
        RETURN false;
    END IF;
    IF body IS NULL OR sha IS NULL OR length IS NULL OR length NOT BETWEEN 1 AND 12582912 OR octet_length(body)<>length OR encode(sha256(body),'hex')<>sha THEN
        RAISE EXCEPTION 'Run completion invalid.' USING ERRCODE='23514'; END IF;
    UPDATE weft.regression_runs SET state='completed',worker_id=NULL,claim_token=NULL,lease_expires=NULL,result_digest=sha,result_bytes=length,result_body=body
        WHERE organization_id=org AND run_id=id;
    RETURN true;
END;
$body$;

CREATE FUNCTION weft.release_regression_run(org uuid, id uuid, worker uuid, token uuid, generation integer) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF session_user<>'weft_run_writer' OR current_user<>'weft_migrator' THEN RAISE EXCEPTION 'Run writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN false; END IF;
    UPDATE weft.regression_runs SET state='queued',worker_id=NULL,claim_token=NULL,lease_expires=NULL,eligible_at=statement_timestamp()+interval '1 second'
        WHERE organization_id=org AND run_id=id AND state='running' AND worker_id=worker AND claim_token=token AND attempt=generation AND lease_expires>statement_timestamp();
    RETURN FOUND;
END;
$body$;

CREATE FUNCTION weft.cancel_regression_run(org uuid, id uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; r weft.regression_runs%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR current_user<>'weft_migrator' THEN RAISE EXCEPTION 'Run writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN false; END IF;
    actor:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid;
    SELECT * INTO r FROM ONLY weft.regression_runs x WHERE x.organization_id=org AND x.run_id=id AND x.submitter_id=actor FOR UPDATE;
    IF NOT FOUND OR weft.regression_run_authorized(org,r.project_id,actor) IS NOT TRUE THEN RETURN false; END IF;
    IF r.state IN ('queued','running') THEN
        UPDATE weft.regression_runs SET state='canceled',worker_id=NULL,claim_token=NULL,lease_expires=NULL WHERE organization_id=org AND run_id=id;
    END IF;
    RETURN true;
END;
$body$;

CREATE FUNCTION weft.read_regression_run(org uuid, id uuid) RETURNS SETOF weft.regression_runs
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT r.* FROM ONLY weft.regression_runs r WHERE session_user IN ('weft_run_reader','weft_run_writer') AND r.organization_id=org AND r.run_id=id
        AND r.submitter_id=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid AND weft.regression_run_authorized(org,r.project_id,r.submitter_id)
$body$;

GRANT USAGE ON SCHEMA weft TO weft_run_reader,weft_run_writer;
REVOKE ALL ON FUNCTION weft.regression_run_authorized(uuid,uuid,uuid),weft.guard_regression_run(),
    weft.submit_regression_run(uuid,uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,text,integer),weft.claim_regression_run(uuid,uuid,uuid),
    weft.observe_regression_run(uuid,uuid,uuid,uuid,integer),weft.finish_regression_run(uuid,uuid,uuid,uuid,integer,bytea,text,integer),
    weft.release_regression_run(uuid,uuid,uuid,uuid,integer),weft.cancel_regression_run(uuid,uuid),weft.read_regression_run(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.read_regression_run(uuid,uuid) TO weft_run_reader,weft_run_writer;
GRANT EXECUTE ON FUNCTION weft.submit_regression_run(uuid,uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,text,integer),weft.claim_regression_run(uuid,uuid,uuid),
    weft.observe_regression_run(uuid,uuid,uuid,uuid,integer),weft.finish_regression_run(uuid,uuid,uuid,uuid,integer,bytea,text,integer),
    weft.release_regression_run(uuid,uuid,uuid,uuid,integer),weft.cancel_regression_run(uuid,uuid) TO weft_run_writer;
