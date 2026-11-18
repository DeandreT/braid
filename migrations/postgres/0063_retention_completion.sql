-- Completes organization retention and permanent deletion.
-- * Regression runs record when they become terminal; runs that ended before this
--   migration have no reliable terminal time and stay held.
-- * The lifecycle worker erases regression runs and invocation history whose
--   expire policy covers them. Their client identities are retired so a repeat is
--   refused, never executed again. Audit log events stay.
-- * A conversation an expire policy makes due is unreadable at once, before the
--   sweep reaches it.
-- * Large purge families are removed in bounded batches inside their step, and an
--   administrator can resume a blocked deletion job once its blocker is gone.
CREATE TABLE braid.regression_run_terminals (
    organization_id uuid NOT NULL,
    run_id uuid NOT NULL,
    terminal_at timestamptz NOT NULL CHECK(isfinite(terminal_at)),
    PRIMARY KEY(organization_id,run_id),
    FOREIGN KEY(organization_id,run_id) REFERENCES braid.regression_runs(organization_id,run_id) ON DELETE CASCADE
);
CREATE TABLE braid.retained_history_keys (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    history_kind text COLLATE "C" NOT NULL CHECK(history_kind IN ('regressionRun','invocation')),
    identity text COLLATE "C" NOT NULL CHECK(octet_length(identity) BETWEEN 36 AND 256),
    retired_at timestamptz NOT NULL CHECK(isfinite(retired_at)),
    PRIMARY KEY(organization_id,history_kind,identity)
);
CREATE TABLE braid.history_sweeps (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    sweep_id uuid NOT NULL CHECK(sweep_id<>'00000000-0000-0000-0000-000000000000'),
    family text COLLATE "C" NOT NULL CHECK(family IN ('regressionRun','invocationHistory')),
    policy_version bigint NOT NULL CHECK(policy_version>0),
    instance_id uuid NOT NULL,
    swept_at timestamptz NOT NULL CHECK(isfinite(swept_at)),
    transaction_id xid8 NOT NULL,
    PRIMARY KEY(organization_id,sweep_id)
);
CREATE INDEX history_sweep_transaction ON braid.history_sweeps(organization_id,transaction_id);
CREATE TABLE braid.deletion_job_resumptions (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    actor_id uuid NOT NULL CHECK(actor_id<>'00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    job_id uuid NOT NULL,
    blocker text COLLATE "C" NOT NULL CHECK(blocker IN ('activeRelease','integrity','storage','attemptsExhausted')),
    resumed_state text COLLATE "C" NOT NULL CHECK(resumed_state IN ('inventory','catalogPurging','objectsPending')),
    created_at timestamptz NOT NULL CHECK(isfinite(created_at)),
    PRIMARY KEY(organization_id,actor_id,operation_id),
    FOREIGN KEY(organization_id,job_id) REFERENCES braid.deletion_jobs(organization_id,job_id)
);

-- Append-only records; a terminal time leaves only with its run.
CREATE FUNCTION braid.completion_record_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP='DELETE' AND TG_TABLE_NAME='regression_run_terminals' AND current_user='braid_migrator' THEN
        IF NOT EXISTS(SELECT 1 FROM ONLY braid.regression_runs r WHERE r.organization_id=OLD.organization_id AND r.run_id=OLD.run_id) THEN RETURN OLD; END IF;
    END IF;
    IF current_user<>'braid_migrator' OR TG_OP<>'INSERT' THEN
        RAISE EXCEPTION 'Retention records are append-only.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;

DO $policies$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['regression_run_terminals','retained_history_keys','history_sweeps','deletion_job_resumptions'] LOOP
        EXECUTE format('ALTER TABLE braid.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format($p$CREATE POLICY organization_scope ON braid.%I USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid)$p$,t);
        EXECUTE format('REVOKE ALL ON braid.%I FROM PUBLIC',t);
        EXECUTE format('CREATE TRIGGER completion_record_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.%I FOR EACH ROW EXECUTE FUNCTION braid.completion_record_guard()',t);
        EXECUTE format('CREATE TRIGGER completion_record_no_truncate BEFORE TRUNCATE ON braid.%I FOR EACH STATEMENT EXECUTE FUNCTION braid.completion_record_guard()',t);
    END LOOP;
END;
$policies$;

-- The first move into a terminal state is the run's terminal time.
CREATE FUNCTION braid.regression_run_terminal() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF NEW.state IN ('completed','canceled','exhausted','revoked') AND OLD.state NOT IN ('completed','canceled','exhausted','revoked') THEN
        INSERT INTO braid.regression_run_terminals(organization_id,run_id,terminal_at) VALUES(NEW.organization_id,NEW.run_id,clock_timestamp())
            ON CONFLICT DO NOTHING;
    END IF;
    RETURN NULL;
END;
$body$;
CREATE TRIGGER regression_run_terminal AFTER UPDATE ON braid.regression_runs FOR EACH ROW EXECUTE FUNCTION braid.regression_run_terminal();

-- An erased run or invocation keeps its client identity; repeating it is refused.
CREATE FUNCTION braid.history_key_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF EXISTS(SELECT 1 FROM ONLY braid.retained_history_keys k WHERE k.organization_id=NEW.organization_id
        AND k.history_kind=CASE WHEN TG_TABLE_NAME='regression_runs' THEN 'regressionRun' ELSE 'invocation' END
        AND k.identity=CASE WHEN TG_TABLE_NAME='regression_runs' THEN (to_jsonb(NEW)->>'run_id')
            ELSE braid.invocation_identity(to_jsonb(NEW)) END) THEN
        RAISE EXCEPTION 'The request was erased.' USING ERRCODE='PWD01';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE FUNCTION braid.invocation_identity(r jsonb) RETURNS text
LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT concat_ws(':',r->>'target_id',r->>'provider',r->>'tenant_id',r->>'object_id',r->>'request_id')
$body$;
CREATE TRIGGER history_key_guard BEFORE INSERT ON braid.regression_runs FOR EACH ROW EXECUTE FUNCTION braid.history_key_guard();
CREATE TRIGGER history_key_guard BEFORE INSERT ON braid.machine_invocations FOR EACH ROW EXECUTE FUNCTION braid.history_key_guard();

-- Whether this transaction is a lifecycle worker history sweep for this organization.
CREATE FUNCTION braid.history_sweep_admitted(p_org uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT session_user='braid_lifecycle_worker' AND EXISTS(SELECT 1 FROM ONLY braid.history_sweeps s
        WHERE s.organization_id=p_org AND s.transaction_id=pg_current_xact_id())
$body$;

-- Whether the organization's current conversation policy has already expired this
-- conversation. Reads treat a due conversation as gone before it is erased.
CREATE FUNCTION braid.chat_retention_due(p_org uuid,p_created timestamp with time zone) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT coalesce((SELECT p.mode='expire' AND (p.applies_to_existing OR p_created>=p.effective_at)
            AND p_created+make_interval(secs=>p.duration_seconds)<=statement_timestamp()
        FROM ONLY braid.organization_retention_policies p WHERE p.organization_id=p_org AND p.family='conversation'
        ORDER BY p.version DESC LIMIT 1),false)
$body$;

-- One bounded sweep of regression runs or invocation history in one organization,
-- under its gate, judged by the policy current after the wait. Runs used as
-- approval or qualification evidence stay. Usage, quota and audit log events stay.
CREATE FUNCTION braid.sweep_retention_history(p_instance uuid,p_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE candidate record; p braid.organization_retention_policies%ROWTYPE; at_time timestamptz; n integer:=0; sweep uuid:=gen_random_uuid(); items text[];
    targets uuid[]; providers text[]; tenants uuid[]; callers uuid[]; requests uuid[];
BEGIN
    IF session_user<>'braid_lifecycle_worker' OR current_user<>'braid_migrator' OR p_instance IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Lifecycle worker required.' USING ERRCODE='42501';
    END IF;
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle work requires read committed transactions.' USING ERRCODE='25001';
    END IF;
    SELECT x.organization_id,x.family INTO candidate FROM (
        SELECT DISTINCT ON (o.organization_id,o.family) o.* FROM ONLY braid.organization_retention_policies o
        WHERE o.family IN ('regressionRun','invocationHistory') ORDER BY o.organization_id,o.family,o.version DESC) x
    CROSS JOIN LATERAL (
        (SELECT t.terminal_at+make_interval(secs=>x.duration_seconds) AS due_at
            FROM ONLY braid.regression_run_terminals t JOIN ONLY braid.regression_runs r USING(organization_id,run_id)
            WHERE x.family='regressionRun' AND x.mode='expire' AND t.organization_id=x.organization_id
                AND (x.applies_to_existing OR r.created_at>=x.effective_at)
                AND t.terminal_at+make_interval(secs=>x.duration_seconds)<=clock_timestamp()
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.build_approvals a WHERE a.organization_id=r.organization_id AND a.evidence_id=r.run_id)
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.build_qualifications q WHERE q.organization_id=r.organization_id AND q.evidence_id=r.run_id)
            ORDER BY t.terminal_at,r.run_id LIMIT 1)
        UNION ALL
        (SELECT i.finished_at+make_interval(secs=>x.duration_seconds) AS due_at FROM ONLY braid.machine_invocations i
            WHERE x.family='invocationHistory' AND x.mode='expire' AND i.organization_id=x.organization_id AND i.finished_at IS NOT NULL
                 AND NOT EXISTS(SELECT 1 FROM ONLY braid.host_order_pins h WHERE (h.organization_id,h.target_id,h.provider,h.tenant_id,h.object_id,h.request_id)=(i.organization_id,i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id))
                AND (x.applies_to_existing OR i.created_at>=x.effective_at)
                AND i.finished_at+make_interval(secs=>x.duration_seconds)<=clock_timestamp()
            ORDER BY i.finished_at,i.request_id,i.target_id,i.provider,i.tenant_id,i.object_id LIMIT 1)
    ) eligible
    -- Newer work in a busy low-ID organization cannot displace older due work.
    ORDER BY eligible.due_at,x.organization_id,x.family LIMIT 1;
    IF NOT FOUND THEN RETURN jsonb_build_object('erased','0'); END IF;
    PERFORM braid.lifecycle_gate(candidate.organization_id,true);
    PERFORM set_config('braid.organization_id',candidate.organization_id::text,true);
    SELECT * INTO p FROM ONLY braid.organization_retention_policies WHERE organization_id=candidate.organization_id AND family=candidate.family
        ORDER BY version DESC LIMIT 1;
    IF p.mode<>'expire' THEN RETURN jsonb_build_object('erased','0'); END IF;
    at_time:=clock_timestamp();
    INSERT INTO braid.history_sweeps(organization_id,sweep_id,family,policy_version,instance_id,swept_at,transaction_id)
        VALUES(p.organization_id,sweep,p.family,p.version,p_instance,at_time,pg_current_xact_id());
    IF p.family='regressionRun' THEN
        SELECT array_agg(run_id::text) INTO items FROM (SELECT r.run_id FROM ONLY braid.regression_run_terminals t
                JOIN ONLY braid.regression_runs r USING(organization_id,run_id)
            WHERE t.organization_id=p.organization_id AND (p.applies_to_existing OR r.created_at>=p.effective_at)
                AND t.terminal_at+make_interval(secs=>p.duration_seconds)<=at_time
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.build_approvals a WHERE a.organization_id=r.organization_id AND a.evidence_id=r.run_id)
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.build_qualifications q WHERE q.organization_id=r.organization_id AND q.evidence_id=r.run_id)
            ORDER BY t.terminal_at,r.run_id LIMIT p_limit FOR UPDATE OF r) due;
        INSERT INTO braid.retained_history_keys(organization_id,history_kind,identity,retired_at)
            SELECT p.organization_id,'regressionRun',x,at_time FROM unnest(items) x ON CONFLICT DO NOTHING;
        DELETE FROM braid.regression_runs r WHERE r.organization_id=p.organization_id AND r.run_id::text=ANY(items);
        GET DIAGNOSTICS n=ROW_COUNT;
    ELSE
        SELECT array_agg(target_id),array_agg(provider),array_agg(tenant_id),array_agg(object_id),array_agg(request_id)
            INTO targets,providers,tenants,callers,requests
        FROM (SELECT i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id FROM ONLY braid.machine_invocations i
            WHERE i.organization_id=p.organization_id AND i.finished_at IS NOT NULL
                 AND NOT EXISTS(SELECT 1 FROM ONLY braid.host_order_pins h WHERE (h.organization_id,h.target_id,h.provider,h.tenant_id,h.object_id,h.request_id)=(i.organization_id,i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id)) AND (p.applies_to_existing OR i.created_at>=p.effective_at)
                AND i.finished_at+make_interval(secs=>p.duration_seconds)<=at_time
            ORDER BY i.finished_at,i.request_id,i.target_id,i.provider,i.tenant_id,i.object_id LIMIT p_limit FOR UPDATE OF i) due;
        INSERT INTO braid.retained_history_keys(organization_id,history_kind,identity,retired_at)
            SELECT p.organization_id,'invocation',concat_ws(':',target_id::text,provider,tenant_id::text,object_id::text,request_id::text),at_time
            FROM unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id) ON CONFLICT DO NOTHING;
        -- Match only the selected typed keys. Never serialize response bodies or
        -- operations metadata, even for rows outside this bounded batch.
        DELETE FROM braid.invocation_operations_metadata x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p.organization_id AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocation_results x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p.organization_id AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocation_audit x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p.organization_id AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocations x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p.organization_id AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        GET DIAGNOSTICS n=ROW_COUNT;
    END IF;
    PERFORM braid.audit_log_append('history_sweeps',sweep::text,p.organization_id,NULL,NULL,NULL,'system','administration',
        'retentionApplied',p.organization_id,p.family||'.'||n::text,at_time,false);
    RETURN jsonb_build_object('organizationId',p.organization_id,'family',p.family,'policyVersion',p.version::text,'erased',n::text);
END;
$body$;

-- A bounded batch of the claimed job's current step, under the same step admission:
-- invocations (with their audit copied first), regression runs and previews (with
-- their request keys tombstoned). The step itself then removes what is left.
CREATE FUNCTION braid.purge_catalog_batch(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid,p_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; at_time timestamptz:=clock_timestamp(); n integer:=0; c integer; items text[];
    targets uuid[]; providers text[]; tenants uuid[]; callers uuid[]; requests uuid[];
BEGIN
    IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION 'Invalid batch.' USING ERRCODE='22023'; END IF;
    j:=braid.lock_deletion_claim(p_org,p_job,p_instance,p_generation,p_token);
    IF j.job_id IS NULL THEN RETURN jsonb_build_object('outcome','claimLost'); END IF;
    IF j.state<>'catalogPurging' OR j.step NOT IN (4,6,7) THEN RETURN jsonb_build_object('outcome','none','removed','0'); END IF;
    IF j.step=4 AND braid.purge_active_releases(p_org,j.project_id,j.mapping_id)>0 THEN RETURN jsonb_build_object('outcome','none','removed','0'); END IF;
    PERFORM braid.record_deletion_step(j,'advanced');
    IF j.step=4 THEN
        SELECT array_agg(target_id),array_agg(provider),array_agg(tenant_id),array_agg(object_id),array_agg(request_id)
            INTO targets,providers,tenants,callers,requests
        FROM (SELECT i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id FROM ONLY braid.machine_invocations i
            WHERE i.organization_id=p_org AND i.project_id=j.project_id AND (j.mapping_id IS NULL OR i.mapping_id=j.mapping_id)
            ORDER BY i.created_at,i.request_id,i.target_id,i.provider,i.tenant_id,i.object_id LIMIT p_limit FOR UPDATE) due;
        INSERT INTO braid.retained_invocation_audit SELECT a.*,j.job_id,i.project_id,i.mapping_id FROM ONLY braid.machine_invocation_audit a
            JOIN ONLY braid.machine_invocations i USING(organization_id,target_id,provider,tenant_id,object_id,request_id)
            JOIN unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
                USING(target_id,provider,tenant_id,object_id,request_id)
            WHERE i.organization_id=p_org;
        GET DIAGNOSTICS c=ROW_COUNT;
        DELETE FROM braid.invocation_operations_metadata x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p_org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocation_results x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p_org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocation_audit x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p_org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        DELETE FROM braid.machine_invocations x USING unnest(targets,providers,tenants,callers,requests) k(target_id,provider,tenant_id,object_id,request_id)
            WHERE x.organization_id=p_org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(k.target_id,k.provider,k.tenant_id,k.object_id,k.request_id);
        GET DIAGNOSTICS n=ROW_COUNT;
        UPDATE braid.deletion_jobs SET retained_audit_count=retained_audit_count+c,version=version+1 WHERE organization_id=p_org AND job_id=p_job;
    ELSIF j.step=6 THEN
        DELETE FROM braid.regression_runs r WHERE r.organization_id=p_org AND (r.organization_id,r.run_id) IN (
            SELECT x.organization_id,x.run_id FROM ONLY braid.regression_runs x WHERE x.organization_id=p_org AND x.project_id=j.project_id
                AND (j.mapping_id IS NULL OR x.mapping_id=j.mapping_id) ORDER BY x.created_at,x.run_id LIMIT p_limit);
        GET DIAGNOSTICS n=ROW_COUNT;
    ELSE
        SELECT array_agg(run_id::text) INTO items FROM (SELECT x.run_id FROM ONLY braid.preview_runs x WHERE x.organization_id=p_org
            AND x.project_id=j.project_id AND (j.mapping_id IS NULL OR x.mapping_id=j.mapping_id) ORDER BY x.created_at,x.run_id LIMIT p_limit) due;
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,submitter_id,request_key,retired_at)
            SELECT organization_id,'preview',run_id,project_id,mapping_id,submitter_id,request_key,at_time FROM ONLY braid.preview_runs
            WHERE organization_id=p_org AND run_id::text=ANY(items) ON CONFLICT DO NOTHING;
        DELETE FROM braid.preview_runs WHERE organization_id=p_org AND run_id::text=ANY(items);
        GET DIAGNOSTICS n=ROW_COUNT;
    END IF;
    RETURN jsonb_build_object('outcome','batched','removed',n::text);
END;
$body$;

-- An exact-project administrator resumes a blocked job once its blocker is gone.
-- It continues where it stopped; a still-active release keeps it blocked.
CREATE FUNCTION braid.resume_deletion_job(p_org uuid,p_actor uuid,p_operation uuid,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; r braid.deletion_job_resumptions%ROWTYPE; target text; old_blocker text; at_time timestamptz;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    IF p_actor IS NULL OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_job IS NULL THEN
        RETURN jsonb_build_object('outcome','invalidRequest');
    END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND job_id=p_job FOR UPDATE;
    IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    -- Project membership disappears with the catalog. Only a current organization
    -- administrator may resume cleanup then; the requester's readable receipt is
    -- not mutation authority. A surviving project still requires its own admin.
    IF NOT braid.administration_authorized(p_org,p_actor,CASE WHEN EXISTS(
        SELECT 1 FROM ONLY braid.projects WHERE organization_id=p_org AND id=j.project_id)
        THEN j.project_id ELSE NULL END) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT * INTO r FROM ONLY braid.deletion_job_resumptions WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF FOUND THEN
        IF r.job_id<>p_job THEN RETURN jsonb_build_object('outcome','conflict'); END IF;
        RETURN jsonb_build_object('outcome','replayed','job',braid.deletion_job_projection(j));
    END IF;
    IF j.state<>'blocked' THEN RETURN jsonb_build_object('outcome','conflict'); END IF;
    IF j.blocker='activeRelease' AND braid.purge_active_releases(p_org,j.project_id,j.mapping_id)>0 THEN
        RETURN jsonb_build_object('outcome','stillBlocked');
    END IF;
    target:=CASE WHEN j.catalog_removed_at IS NOT NULL THEN 'objectsPending' WHEN j.step>=1 THEN 'catalogPurging' ELSE 'inventory' END;
    at_time:=clock_timestamp(); old_blocker:=j.blocker;
    INSERT INTO braid.deletion_job_resumptions(organization_id,actor_id,operation_id,job_id,blocker,resumed_state,created_at)
        VALUES(p_org,p_actor,p_operation,p_job,j.blocker,target,at_time);
    UPDATE braid.deletion_objects SET disposition='pending',observation=NULL,attempts=0,next_attempt_at=at_time,
        claim_instance=NULL,claim_token=NULL,claim_expires_at=NULL
        WHERE organization_id=p_org AND job_id=p_job AND disposition='blocked';
    UPDATE braid.deletion_jobs SET state=target,blocker=NULL,next_attempt_at=at_time,changed_at=at_time,claim_instance=NULL,claim_token=NULL,
        claim_expires_at=NULL,version=version+1 WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
    PERFORM braid.purge_audit(j,'DeleteResumed',old_blocker,p_actor,at_time);
    RETURN jsonb_build_object('outcome','resumed','job',braid.deletion_job_projection(j));
END;
$body$;

REVOKE ALL ON FUNCTION braid.completion_record_guard(),braid.regression_run_terminal(),braid.history_key_guard(),braid.invocation_identity(jsonb),
    braid.history_sweep_admitted(uuid),braid.chat_retention_due(uuid,timestamptz),braid.sweep_retention_history(uuid,integer),
    braid.purge_catalog_batch(uuid,uuid,uuid,bigint,uuid,integer),braid.resume_deletion_job(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.chat_retention_due(uuid,timestamptz) TO braid_chat_reader,braid_chat_writer;
GRANT EXECUTE ON FUNCTION braid.sweep_retention_history(uuid,integer),braid.purge_catalog_batch(uuid,uuid,uuid,bigint,uuid,integer) TO braid_lifecycle_worker;
GRANT EXECUTE ON FUNCTION braid.resume_deletion_job(uuid,uuid,uuid,uuid) TO braid_administration;

-- Exact preludes, nested so restricted sessions never call the owner-only check.
DO $guards$
DECLARE entry record; source text; definition text; offset_at integer;
    deletion text:=$p$    IF TG_OP='DELETE' AND current_user='braid_migrator' THEN
        IF braid.history_sweep_admitted(OLD.organization_id) THEN RETURN OLD; END IF;
    END IF;
$p$;
BEGIN
    FOR entry IN SELECT * FROM (VALUES ('guard_machine_invocation',deletion),('guard_regression_run',deletion)) inventory(name,prelude) LOOP
        SELECT p.prosrc,pg_get_functiondef(p.oid) INTO STRICT source,definition
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=entry.name;
        offset_at:=strpos(source,E'\nBEGIN\n');
        IF offset_at=0 THEN RAISE EXCEPTION 'Retention guard has no canonical block: %',entry.name; END IF;
        EXECUTE replace(definition,source,overlay(source PLACING E'\nBEGIN\n'||entry.prelude FROM offset_at FOR 7));
    END LOOP;
END;
$guards$;
