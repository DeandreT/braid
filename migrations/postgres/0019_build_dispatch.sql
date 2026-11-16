-- Actor admission and machine dispatch use independent, CONNECT-only credentials.
DO $roles$
DECLARE n text; r record;
BEGIN
    FOREACH n IN ARRAY ARRAY['braid_build_run_reader','braid_build_run_writer','braid_build_dispatcher'] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=n;
        IF current_user<>'braid_migrator' OR session_user<>current_user OR NOT FOUND OR NOT r.rolcanlogin
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
            OR pg_catalog.has_schema_privilege(r.oid,'braid','USAGE') THEN
            RAISE EXCEPTION 'Independent build run roles required.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE braid.build_runs (
    organization_id uuid NOT NULL, build_id uuid NOT NULL,
    project_id uuid NOT NULL, approval_id uuid NOT NULL, actor_id uuid NOT NULL,
    intent_digest text NOT NULL CHECK(intent_digest~'^[0-9a-f]{64}$'), intent_body bytea NOT NULL,
    state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','succeeded','failed','canceled','exhausted','revoked','uncertain')),
    attempt integer NOT NULL DEFAULT 0 CHECK(attempt BETWEEN 0 AND 3),
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp() CHECK(isfinite(created_at)),
    eligible_at timestamp with time zone NOT NULL DEFAULT clock_timestamp() CHECK(isfinite(eligible_at)),
    worker_id uuid, claim_token uuid, lease_expires timestamp with time zone, execution_deadline timestamp with time zone,
    outcome integer, artifact_digest text, artifact_bytes integer,
    PRIMARY KEY(organization_id,build_id),
    FOREIGN KEY(organization_id,build_id) REFERENCES braid.build_approvals(organization_id,build_id),
    FOREIGN KEY(organization_id,approval_id) REFERENCES braid.build_approvals(organization_id,approval_id),
    CHECK(build_id<>'00000000-0000-0000-0000-000000000000' AND actor_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(state NOT IN ('running','succeeded','failed') OR attempt>0),
    CHECK(state NOT IN ('exhausted','uncertain') OR attempt=3),
    CHECK(octet_length(intent_body) BETWEEN 1 AND 1048576 AND encode(sha256(intent_body),'hex')=intent_digest),
    CHECK((state='running')=(worker_id IS NOT NULL AND claim_token IS NOT NULL AND lease_expires IS NOT NULL AND execution_deadline IS NOT NULL)),
    CHECK(state='running' OR (worker_id IS NULL AND claim_token IS NULL AND lease_expires IS NULL AND execution_deadline IS NULL)),
    CHECK(worker_id IS NULL OR worker_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(claim_token IS NULL OR claim_token<>'00000000-0000-0000-0000-000000000000'),
    CHECK(lease_expires IS NULL OR (isfinite(lease_expires) AND isfinite(execution_deadline) AND lease_expires<=execution_deadline)),
    CHECK((state='succeeded' AND outcome IS NOT NULL AND outcome IN (0,1)) OR (state='failed' AND outcome IS NOT NULL AND outcome IN (2,3,4,5)) OR
        (state NOT IN ('succeeded','failed') AND outcome IS NULL)),
    CHECK((state IN ('succeeded','uncertain') AND artifact_digest IS NOT NULL AND artifact_bytes IS NOT NULL AND artifact_digest~'^[0-9a-f]{64}$' AND artifact_bytes BETWEEN 1 AND 33554432)
        OR (state NOT IN ('succeeded','uncertain') AND artifact_digest IS NULL AND artifact_bytes IS NULL))
);
CREATE INDEX build_run_ready ON braid.build_runs(eligible_at,created_at,organization_id,build_id) WHERE state='queued';
CREATE INDEX build_run_expired ON braid.build_runs(lease_expires,execution_deadline,created_at) WHERE state='running';
CREATE INDEX build_run_actor_admission ON braid.build_runs(organization_id,actor_id) WHERE state IN ('queued','running');
ALTER TABLE braid.build_runs ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.build_runs USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.build_runs FROM PUBLIC;

-- Match schema18's lock order. The actor is loaded from the immutable approval, never supplied by a dispatcher.
-- All publication and run mutation paths hold the same principal/project locks before publication/run locks.
CREATE FUNCTION braid.lock_build_run_authority(org uuid, id uuid) RETURNS braid.build_approvals
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE;
BEGIN
    SELECT * INTO a FROM ONLY braid.build_approvals WHERE organization_id=org AND build_id=id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF session_user='braid_build_dispatcher' THEN
        PERFORM set_config('braid.build_principal_id',a.actor_id::text,true);
    ELSIF a.actor_id IS DISTINCT FROM NULLIF(current_setting('braid.build_principal_id',true),'')::uuid THEN RETURN NULL;
    END IF;
    PERFORM 1 FROM ONLY braid.principals p WHERE p.id=a.actor_id FOR UPDATE;
    PERFORM 1 FROM ONLY braid.organizations o WHERE o.id=org FOR SHARE;
    PERFORM 1 FROM ONLY braid.organization_memberships WHERE organization_id=org AND principal_id=a.actor_id FOR SHARE;
    PERFORM 1 FROM ONLY braid.projects p WHERE p.organization_id=org AND p.id=a.project_id FOR UPDATE;
    PERFORM 1 FROM ONLY braid.project_memberships WHERE organization_id=org AND project_id=a.project_id AND principal_id=a.actor_id FOR SHARE;
    SELECT * INTO a FROM ONLY braid.build_approvals WHERE organization_id=org AND build_id=id FOR SHARE;
    PERFORM 1 FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id FOR UPDATE;
    PERFORM 1 FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id FOR UPDATE;
    RETURN a;
END;
$body$;
REVOKE ALL ON FUNCTION braid.lock_build_run_authority(uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.guard_build_run() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP IN ('DELETE','TRUNCATE') OR current_user<>'braid_migrator'
        OR session_user NOT IN ('braid_build_run_writer','braid_build_dispatcher') THEN
        RAISE EXCEPTION 'Build runs are retained.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='INSERT' THEN
        IF NEW.state<>'queued' OR NEW.attempt<>0 OR NOT EXISTS(SELECT 1 FROM ONLY braid.build_approvals a
            WHERE a.organization_id=NEW.organization_id AND a.build_id=NEW.build_id AND a.approval_id=NEW.approval_id
                AND a.project_id=NEW.project_id AND a.actor_id=NEW.actor_id AND a.intent_body=NEW.intent_body AND a.intent_digest=NEW.intent_digest) THEN
            RAISE EXCEPTION 'Build run identity refused.' USING ERRCODE='42501';
        END IF;
    ELSE
        IF ROW(NEW.organization_id,NEW.build_id,NEW.project_id,NEW.approval_id,NEW.actor_id,NEW.intent_digest,NEW.intent_body,NEW.created_at)
            IS DISTINCT FROM ROW(OLD.organization_id,OLD.build_id,OLD.project_id,OLD.approval_id,OLD.actor_id,OLD.intent_digest,OLD.intent_body,OLD.created_at)
            OR OLD.state NOT IN ('queued','running') THEN RAISE EXCEPTION 'Build run is immutable.' USING ERRCODE='42501'; END IF;
        IF NEW.state<>'running' AND NEW.attempt<>OLD.attempt THEN
            RAISE EXCEPTION 'Build attempt is immutable outside claiming.' USING ERRCODE='42501';
        END IF;
        IF NEW.state='running' AND NOT ((NEW.attempt=OLD.attempt+1 AND OLD.attempt<3 AND
                (OLD.state='queued' OR OLD.lease_expires<=clock_timestamp() OR OLD.execution_deadline<=clock_timestamp())) OR
            (OLD.state='running' AND NEW.attempt=OLD.attempt AND NEW.worker_id=OLD.worker_id AND NEW.claim_token=OLD.claim_token
                AND NEW.execution_deadline=OLD.execution_deadline AND NEW.lease_expires>=OLD.lease_expires)) THEN
            RAISE EXCEPTION 'Build claim transition refused.' USING ERRCODE='42501';
        END IF;
    END IF;
    IF NEW.state IN ('succeeded','uncertain') AND NOT EXISTS(SELECT 1 FROM ONLY braid.build_publications p
        WHERE p.organization_id=NEW.organization_id AND p.build_id=NEW.build_id AND p.approval_id=NEW.approval_id
            AND p.state=2 AND p.artifact_digest=NEW.artifact_digest AND octet_length(p.artifact_body)=NEW.artifact_bytes
            AND encode(sha256(p.artifact_body),'hex')=NEW.artifact_digest) THEN
        RAISE EXCEPTION 'Committed artifact required.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_build_run() FROM PUBLIC;
CREATE TRIGGER build_run_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.build_runs FOR EACH ROW EXECUTE FUNCTION braid.guard_build_run();
CREATE TRIGGER build_run_truncate_guard BEFORE TRUNCATE ON braid.build_runs FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_build_run();

-- Atomic cancellation/lease fencing covers staging, object publication and final publication.
-- A legacy standalone build without a queued run keeps schema18's behavior.
CREATE FUNCTION braid.guard_build_run_publication() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.build_runs%ROWTYPE; now_at timestamp with time zone;
BEGIN
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=NEW.organization_id AND build_id=NEW.build_id FOR UPDATE;
    IF NOT FOUND THEN RETURN NEW; END IF;
    now_at:=clock_timestamp();
    IF r.state<>'running' OR r.lease_expires<=now_at OR r.execution_deadline<=now_at
        OR r.actor_id IS DISTINCT FROM NULLIF(current_setting('braid.build_principal_id',true),'')::uuid
        OR r.worker_id IS DISTINCT FROM NULLIF(current_setting('braid.build_worker_id',true),'')::uuid
        OR r.claim_token IS DISTINCT FROM NULLIF(current_setting('braid.build_claim_token',true),'')::uuid
        OR r.intent_digest IS DISTINCT FROM NULLIF(current_setting('braid.build_claim_intent',true),'')
        OR r.attempt IS DISTINCT FROM NULLIF(current_setting('braid.build_claim_attempt',true),'')::integer THEN
        RAISE EXCEPTION 'Current build claim required.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_build_run_publication() FROM PUBLIC;
CREATE TRIGGER build_run_publication_fence BEFORE INSERT OR UPDATE ON braid.build_publications FOR EACH ROW EXECUTE FUNCTION braid.guard_build_run_publication();

CREATE FUNCTION braid.read_build_run(org uuid, id uuid)
RETURNS TABLE(actor uuid, intent bytea, state text, attempt integer, created timestamp with time zone, outcome integer, artifact_digest text, artifact_bytes integer)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE bound_actor uuid;
BEGIN
    IF session_user NOT IN ('braid_build_run_reader','braid_build_run_writer') OR current_user<>'braid_migrator'
        OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN; END IF;
    bound_actor:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid;
    RETURN QUERY SELECT r.actor_id,r.intent_body,r.state,r.attempt,r.created_at,r.outcome,r.artifact_digest,r.artifact_bytes
        FROM ONLY braid.build_runs r WHERE r.organization_id=org AND r.build_id=id AND r.actor_id=bound_actor
            AND braid.build_authorized(org,r.project_id,bound_actor,ARRAY['publisher','administrator']);
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_build_run(uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.write_build_run(operation text, org uuid, id uuid, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE; r braid.build_runs%ROWTYPE; bound_actor uuid;
BEGIN
    IF session_user<>'braid_build_run_writer' OR current_user<>'braid_migrator' THEN RAISE EXCEPTION 'Build run writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN 3; END IF;
    bound_actor:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid;
    a:=braid.lock_build_run_authority(org,id);
    IF a.actor_id IS NULL OR a.actor_id IS DISTINCT FROM bound_actor OR
        braid.build_authorized(org,a.project_id,bound_actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 3; END IF;
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id;
    IF operation='submit' THEN
        IF body IS NULL OR body IS DISTINCT FROM a.intent_body THEN RETURN 2; END IF;
        IF FOUND THEN RETURN 1; END IF;
        IF braid.build_approval_active(org,a.approval_id,bound_actor) IS NOT TRUE THEN RETURN 3; END IF;
        IF (SELECT count(*) FROM ONLY braid.build_runs x JOIN ONLY braid.build_approvals b ON b.organization_id=x.organization_id AND b.build_id=x.build_id
            JOIN ONLY braid.build_environments e ON e.organization_id=b.organization_id AND e.project_id=b.project_id AND e.environment_id=b.environment_id AND e.version=b.environment_version
            WHERE x.organization_id=org AND x.actor_id=bound_actor AND x.state IN ('queued','running') AND NOT b.revoked AND e.active)>=32 THEN RETURN 4; END IF;
        INSERT INTO braid.build_runs(organization_id,build_id,project_id,approval_id,actor_id,intent_digest,intent_body)
            VALUES(org,id,a.project_id,a.approval_id,bound_actor,a.intent_digest,a.intent_body);
        RETURN 0;
    ELSIF operation='cancel' THEN
        IF body IS NOT NULL THEN RETURN 3; END IF;
        IF NOT FOUND THEN RETURN 3; END IF;
        IF r.state IN ('queued','running') THEN UPDATE braid.build_runs SET state='canceled',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL
            WHERE organization_id=org AND build_id=id; END IF;
        RETURN 0;
    ELSE RAISE EXCEPTION 'Unknown build run operation.' USING ERRCODE='22023'; END IF;
END;
$body$;
REVOKE ALL ON FUNCTION braid.write_build_run(text,uuid,uuid,bytea) FROM PUBLIC;

CREATE FUNCTION braid.claim_build_run(org uuid, id uuid, worker uuid)
RETURNS TABLE(actor uuid, intent bytea, claim uuid, attempt integer, lease timestamp with time zone)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE; r braid.build_runs%ROWTYPE; p braid.build_publications%ROWTYPE; now_at timestamp with time zone;
BEGIN
    IF session_user<>'braid_build_dispatcher' OR current_user<>'braid_migrator' OR worker IS NULL OR worker='00000000-0000-0000-0000-000000000000'
        OR coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')<>'' THEN RAISE EXCEPTION 'Unbound build dispatcher required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN; END IF;
    a:=braid.lock_build_run_authority(org,id);
    IF a.actor_id IS NULL THEN RETURN; END IF;
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id;
    IF NOT FOUND OR r.state NOT IN ('queued','running') THEN RETURN; END IF;
    now_at:=clock_timestamp();
    IF (r.state='queued' AND r.eligible_at>now_at) OR (r.state='running' AND r.lease_expires>now_at AND r.execution_deadline>now_at) THEN RETURN; END IF;
    IF braid.build_approval_active(org,a.approval_id,a.actor_id) IS NOT TRUE THEN
        UPDATE braid.build_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN;
    END IF;
    IF r.attempt>=3 THEN
        SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
        UPDATE braid.build_runs SET state=CASE WHEN FOUND THEN 'uncertain' ELSE 'exhausted' END,
            artifact_digest=CASE WHEN FOUND THEN p.artifact_digest ELSE NULL END,artifact_bytes=CASE WHEN FOUND THEN octet_length(p.artifact_body) ELSE NULL END,
            worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN;
    END IF;
    UPDATE braid.build_runs SET state='running',attempt=r.attempt+1,worker_id=worker,claim_token=gen_random_uuid(),
        lease_expires=now_at+interval '90 seconds',execution_deadline=now_at+interval '6 minutes' WHERE organization_id=org AND build_id=id;
    RETURN QUERY SELECT x.actor_id,x.intent_body,x.claim_token,x.attempt,x.lease_expires FROM ONLY braid.build_runs x WHERE x.organization_id=org AND x.build_id=id;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_build_run(uuid,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.transition_build_run(operation text, org uuid, id uuid, owner uuid, intent_sha text, worker uuid, claim uuid, generation integer, result integer, digest text, bytes integer) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE; r braid.build_runs%ROWTYPE; p braid.build_publications%ROWTYPE; now_at timestamp with time zone;
BEGIN
    IF session_user<>'braid_build_dispatcher' OR current_user<>'braid_migrator'
        OR coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')<>'' THEN RAISE EXCEPTION 'Unbound build dispatcher required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN false; END IF;
    a:=braid.lock_build_run_authority(org,id);
    IF a.actor_id IS NULL OR a.actor_id IS DISTINCT FROM owner OR a.intent_digest IS DISTINCT FROM intent_sha THEN RETURN false; END IF;
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id;
    now_at:=clock_timestamp();
    IF NOT FOUND OR r.state<>'running' OR r.worker_id IS DISTINCT FROM worker OR r.claim_token IS DISTINCT FROM claim
        OR r.attempt IS DISTINCT FROM generation OR r.lease_expires<=now_at OR r.execution_deadline<=now_at THEN RETURN false; END IF;
    IF braid.build_approval_active(org,a.approval_id,a.actor_id) IS NOT TRUE THEN
        UPDATE braid.build_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN false;
    END IF;
    IF operation='renew' THEN
        UPDATE braid.build_runs SET lease_expires=greatest(r.lease_expires,least(now_at+interval '90 seconds',r.execution_deadline)) WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSIF operation='release' THEN
        SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
        UPDATE braid.build_runs SET state=CASE WHEN r.attempt<3 THEN 'queued' WHEN FOUND THEN 'uncertain' ELSE 'exhausted' END,
            artifact_digest=CASE WHEN r.attempt=3 AND FOUND THEN p.artifact_digest ELSE NULL END,
            artifact_bytes=CASE WHEN r.attempt=3 AND FOUND THEN octet_length(p.artifact_body) ELSE NULL END,
            eligible_at=now_at+interval '2 seconds',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSIF operation='finish' THEN
        IF result IS NULL OR result NOT BETWEEN 0 AND 5 THEN RETURN false; END IF;
        IF result IN (0,1) THEN
            SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
            IF NOT FOUND OR p.approval_id<>a.approval_id OR p.artifact_digest IS DISTINCT FROM digest
                OR octet_length(p.artifact_body) IS DISTINCT FROM bytes OR encode(sha256(p.artifact_body),'hex') IS DISTINCT FROM digest THEN RETURN false; END IF;
        ELSIF digest IS NOT NULL OR bytes IS NOT NULL THEN RETURN false; END IF;
        UPDATE braid.build_runs SET state=CASE WHEN result IN (0,1) THEN 'succeeded' ELSE 'failed' END,outcome=result,
            artifact_digest=digest,artifact_bytes=bytes,worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSE RAISE EXCEPTION 'Unknown build transition.' USING ERRCODE='22023'; END IF;
END;
$body$;
REVOKE ALL ON FUNCTION braid.transition_build_run(text,uuid,uuid,uuid,text,uuid,uuid,integer,integer,text,integer) FROM PUBLIC;

CREATE FUNCTION braid.discover_build_runs(maximum integer) RETURNS TABLE(organization_id uuid, build_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_build_dispatcher' OR current_user<>'braid_migrator'
        OR coalesce(nullif(current_setting('braid.organization_id',true),''),'')<>''
        OR coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')<>'' THEN RAISE EXCEPTION 'Unbound build discovery required.' USING ERRCODE='42501'; END IF;
    IF maximum IS NULL OR maximum NOT BETWEEN 1 AND 32 THEN RAISE EXCEPTION 'Build discovery bound required.' USING ERRCODE='22023'; END IF;
    RETURN QUERY SELECT r.organization_id,r.build_id FROM ONLY braid.build_runs r
        WHERE (r.state='queued' AND r.eligible_at<=clock_timestamp()) OR (r.state='running' AND (r.lease_expires<=clock_timestamp() OR r.execution_deadline<=clock_timestamp()))
        ORDER BY r.created_at,r.organization_id,r.build_id LIMIT maximum;
END;
$body$;
REVOKE ALL ON FUNCTION braid.discover_build_runs(integer) FROM PUBLIC;

GRANT USAGE ON SCHEMA braid TO braid_build_run_reader,braid_build_run_writer,braid_build_dispatcher;
GRANT EXECUTE ON FUNCTION braid.read_build_run(uuid,uuid) TO braid_build_run_reader,braid_build_run_writer;
GRANT EXECUTE ON FUNCTION braid.write_build_run(text,uuid,uuid,bytea) TO braid_build_run_writer;
GRANT EXECUTE ON FUNCTION braid.discover_build_runs(integer),braid.claim_build_run(uuid,uuid,uuid),
    braid.transition_build_run(text,uuid,uuid,uuid,text,uuid,uuid,integer,integer,text,integer) TO braid_build_dispatcher;
