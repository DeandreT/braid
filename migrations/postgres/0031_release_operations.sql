-- Operations metadata is deliberately separate from the publisher-only private proof reader.
-- Keep the original guard for release rows and target identity. Only the reversible
-- enabled bit changes; the old write_local_target entry point remains one-way.
CREATE FUNCTION braid.guard_operations_target() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_release_writer' OR TG_OP IN ('DELETE','TRUNCATE') THEN
        RAISE EXCEPTION 'Local release mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='local_release_targets' AND TG_OP='UPDATE' THEN
        IF ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.environment_id,NEW.environment_version,NEW.runtime_image,NEW.profile,NEW.definition,NEW.created_by,NEW.created_at)
            IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.environment_id,OLD.environment_version,OLD.runtime_image,OLD.profile,OLD.definition,OLD.created_by,OLD.created_at) THEN
            RAISE EXCEPTION 'Local target identity is immutable.' USING ERRCODE='42501';
        END IF;
        IF NEW.active_release_id IS DISTINCT FROM OLD.active_release_id AND (NOT NEW.enabled OR NOT EXISTS(
            SELECT 1 FROM ONLY braid.local_releases r WHERE r.organization_id=NEW.organization_id AND r.target_id=NEW.target_id
                AND r.release_id=NEW.active_release_id AND r.phase='active' AND r.finished_at=statement_timestamp()
                AND (convert_from(r.intent_body,'UTF8')::jsonb->>'expectedActiveReleaseId')::uuid IS NOT DISTINCT FROM OLD.active_release_id)) THEN
            RAISE EXCEPTION 'Local target activation must be conditional.' USING ERRCODE='42501';
        END IF;
    ELSIF TG_TABLE_NAME='local_release_targets' THEN
        IF NOT NEW.enabled OR NEW.active_release_id IS NOT NULL OR NEW.created_at<>statement_timestamp()
            OR braid.build_authorized(NEW.organization_id,NEW.project_id,NEW.created_by,ARRAY['administrator']) IS NOT TRUE THEN
            RAISE EXCEPTION 'Local target registration refused.' USING ERRCODE='42501';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_operations_target() FROM PUBLIC;
DROP TRIGGER local_target_guard ON braid.local_release_targets;
DROP TRIGGER local_target_no_truncate ON braid.local_release_targets;
CREATE TRIGGER local_target_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.local_release_targets FOR EACH ROW EXECUTE FUNCTION braid.guard_operations_target();
CREATE TRIGGER local_target_no_truncate BEFORE TRUNCATE ON braid.local_release_targets FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_operations_target();
CREATE TABLE braid.release_target_operations (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, operation_id uuid NOT NULL,
    actor_id uuid NOT NULL, action text NOT NULL CHECK(action IN ('activate','rollback','enable','disable')),
    expected_version bigint NOT NULL CHECK(expected_version>0), qualification_id uuid, source_release_id uuid,
    outcome integer NOT NULL CHECK(outcome BETWEEN 0 AND 4),
    recorded_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,operation_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id)
);
CREATE TABLE braid.release_target_events (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, version bigint NOT NULL CHECK(version>0),
    actor_id uuid, enabled boolean NOT NULL, active_release_id uuid,
    origin text NOT NULL CHECK(origin IN ('baseline','registered','activation','disabled','enabled')),
    recorded_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,target_id,version),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id)
);
CREATE TABLE braid.invocation_operations_metadata (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, provider text NOT NULL, tenant_id uuid NOT NULL, object_id uuid NOT NULL, request_id uuid NOT NULL,
    input_bytes integer NOT NULL CHECK(input_bytes>=0), replay_count bigint NOT NULL DEFAULT 0 CHECK(replay_count>=0),
    PRIMARY KEY(organization_id,target_id,provider,tenant_id,object_id,request_id),
    FOREIGN KEY(organization_id,target_id,provider,tenant_id,object_id,request_id)
        REFERENCES braid.machine_invocations(organization_id,target_id,provider,tenant_id,object_id,request_id)
);
ALTER TABLE braid.invocation_operations_metadata ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.invocation_operations_metadata USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.invocation_operations_metadata FROM PUBLIC;

CREATE FUNCTION braid.record_invocation_metadata(org uuid, target uuid, issuer text, tenant uuid, principal uuid, request uuid,
    application uuid, digest text, bytes integer, replayed boolean) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_invocation_writer' OR bytes IS NULL OR bytes<0 OR replayed IS NULL OR NOT EXISTS(
        SELECT 1 FROM ONLY braid.machine_invocations i WHERE i.organization_id=org AND i.target_id=target AND i.provider=issuer
            AND i.tenant_id=tenant AND i.object_id=principal AND i.request_id=request AND i.application_id=application AND i.request_digest=digest
            AND (NOT replayed OR i.state IN ('completed','failed'))) THEN RETURN false; END IF;
    INSERT INTO braid.invocation_operations_metadata(organization_id,target_id,provider,tenant_id,object_id,request_id,input_bytes,replay_count)
    VALUES(org,target,issuer,tenant,principal,request,bytes,CASE WHEN replayed THEN 1 ELSE 0 END)
    ON CONFLICT(organization_id,target_id,provider,tenant_id,object_id,request_id) DO UPDATE
        SET replay_count=braid.invocation_operations_metadata.replay_count+CASE WHEN replayed THEN 1 ELSE 0 END;
    RETURN true;
END;
$body$;
REVOKE ALL ON FUNCTION braid.record_invocation_metadata(uuid,uuid,text,uuid,uuid,uuid,uuid,text,integer,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.record_invocation_metadata(uuid,uuid,text,uuid,uuid,uuid,uuid,text,integer,boolean) TO braid_invocation_writer;
-- The initial snapshot preserves existing pointers without inventing historical actors or times.
INSERT INTO braid.release_target_events
SELECT organization_id,target_id,1,NULL,enabled,active_release_id,'baseline',statement_timestamp() FROM ONLY braid.local_release_targets;
ALTER TABLE braid.release_target_operations ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.release_target_events ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.release_target_operations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.release_target_events USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.release_target_operations,braid.release_target_events FROM PUBLIC;

CREATE FUNCTION braid.capture_release_target() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP='INSERT' OR ROW(NEW.enabled,NEW.active_release_id) IS DISTINCT FROM ROW(OLD.enabled,OLD.active_release_id) THEN
        INSERT INTO braid.release_target_events(organization_id,target_id,version,actor_id,enabled,active_release_id,origin)
        SELECT NEW.organization_id,NEW.target_id,coalesce(max(version),0)+1,
            NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,NEW.enabled,NEW.active_release_id,
            CASE WHEN TG_OP='INSERT' THEN 'registered' WHEN NEW.active_release_id IS DISTINCT FROM OLD.active_release_id THEN 'activation'
                WHEN NEW.enabled THEN 'enabled' ELSE 'disabled' END
        FROM ONLY braid.release_target_events WHERE organization_id=NEW.organization_id AND target_id=NEW.target_id;
    END IF;
    RETURN NULL;
END;
$body$;
REVOKE ALL ON FUNCTION braid.capture_release_target() FROM PUBLIC;
CREATE TRIGGER release_target_capture AFTER INSERT OR UPDATE ON braid.local_release_targets FOR EACH ROW EXECUTE FUNCTION braid.capture_release_target();

CREATE FUNCTION braid.guard_release_operations() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_release_writer' OR TG_OP<>'INSERT'
        OR NEW.recorded_at<>statement_timestamp() THEN
        RAISE EXCEPTION 'Release operations history is immutable.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_release_operations() FROM PUBLIC;
CREATE TRIGGER release_operations_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.release_target_operations FOR EACH ROW EXECUTE FUNCTION braid.guard_release_operations();
CREATE TRIGGER release_events_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.release_target_events FOR EACH ROW EXECUTE FUNCTION braid.guard_release_operations();
CREATE TRIGGER release_operations_no_truncate BEFORE TRUNCATE ON braid.release_target_operations FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_release_operations();
CREATE TRIGGER release_events_no_truncate BEFORE TRUNCATE ON braid.release_target_events FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_release_operations();

CREATE FUNCTION braid.operate_release_target(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint,
    target uuid, operation uuid, action text, expected bigint, qualification uuid, source_release uuid, intent bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid;
    t braid.local_release_targets%ROWTYPE; old braid.release_target_operations%ROWTYPE; result integer; current_version bigint;
BEGIN
    IF session_user<>'braid_release_writer' OR actor IS NULL OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'
        OR expected IS NULL OR expected<1 OR action IS NULL OR action NOT IN ('activate','rollback','enable','disable') THEN RETURN 4; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 4; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR UPDATE;
    IF NOT FOUND OR ROW(t.project_id,t.mapping_id,t.environment_id,t.environment_version) IS DISTINCT FROM ROW(project,mapping,environment,target_version) THEN RETURN 4; END IF;
    -- Operation IDs are organization-wide, including simultaneous requests in different projects.
    -- Fence that identity before any effect; a uniqueness error after a pointer change is not a receipt.
    PERFORM pg_advisory_xact_lock(hashtextextended('braid.release.operation:'||org::text||':'||operation::text,0));
    SELECT * INTO old FROM ONLY braid.release_target_operations WHERE organization_id=org AND operation_id=operation;
    IF FOUND THEN
        IF ROW(old.target_id,old.actor_id,old.action,old.expected_version,old.qualification_id,old.source_release_id)
            IS DISTINCT FROM ROW(target,actor,action,expected,qualification,source_release) THEN RETURN 5; END IF;
        RETURN old.outcome;
    END IF;
    SELECT max(version) INTO current_version FROM ONLY braid.release_target_events WHERE organization_id=org AND target_id=target;
    IF current_version IS DISTINCT FROM expected THEN result:=3;
    ELSIF action IN ('enable','disable') THEN
        IF qualification IS NOT NULL OR source_release IS NOT NULL OR intent IS NOT NULL THEN RETURN 4; END IF;
        IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN result:=4;
        ELSIF action='disable' THEN
            result:=braid.write_local_target('disable',org,project,mapping,environment,target_version,target,NULL);
            result:=CASE WHEN result IN (0,1) THEN 0 ELSE 4 END;
        ELSE
            UPDATE ONLY braid.local_release_targets SET enabled=true WHERE organization_id=org AND target_id=target;
            result:=0;
        END IF;
    ELSE
        IF intent IS NULL THEN result:=4;
        ELSE
        IF (convert_from(intent,'UTF8')::jsonb->>'releaseId') IS DISTINCT FROM operation::text
            OR (convert_from(intent,'UTF8')::jsonb->>'action') IS DISTINCT FROM action
            OR (convert_from(intent,'UTF8')::jsonb->>'qualificationId') IS DISTINCT FROM qualification::text
            OR (convert_from(intent,'UTF8')::jsonb->>'rollbackOfReleaseId') IS DISTINCT FROM source_release::text THEN RETURN 4; END IF;
        result:=braid.write_local_release(org,project,mapping,environment,target_version,target,operation,intent);
        IF result IN (0,1) THEN result:=braid.transition_local_release('apply',org,project,mapping,environment,target_version,target,operation);
        ELSIF result=2 THEN result:=3; ELSE result:=4; END IF;
        END IF;
    END IF;
    INSERT INTO braid.release_target_operations(organization_id,target_id,operation_id,actor_id,action,expected_version,qualification_id,source_release_id,outcome)
    VALUES(org,target,operation,actor,action,expected,qualification,source_release,result);
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.operate_release_target(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,bigint,uuid,uuid,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.operate_release_target(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,bigint,uuid,uuid,bytea) TO braid_release_writer;

CREATE FUNCTION braid.read_release_operation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint,
    target uuid, operation uuid, action text, expected bigint, qualification uuid, source_release uuid) RETURNS integer
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; old braid.release_target_operations%ROWTYPE;
BEGIN
    IF session_user<>'braid_release_writer' OR braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 4; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version AND t.target_id=target) THEN RETURN 4; END IF;
    SELECT * INTO old FROM ONLY braid.release_target_operations WHERE organization_id=org AND operation_id=operation;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF ROW(old.target_id,old.actor_id,old.action,old.expected_version,old.qualification_id,old.source_release_id)
        IS DISTINCT FROM ROW(target,actor,action,expected,qualification,source_release) THEN RETURN 5; END IF;
    RETURN old.outcome;
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_release_operation(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,bigint,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_release_operation(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,bigint,uuid,uuid) TO braid_release_writer;

CREATE FUNCTION braid.read_release_operations(org uuid, project uuid, mapping uuid, target uuid, section text,
    page_offset integer, page_limit integer, since timestamp with time zone, until_time timestamp with time zone, caller uuid, outcome_filter text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE result jsonb;
BEGIN
    IF session_user NOT IN ('braid_release_reader','braid_release_writer') OR page_offset IS NULL OR page_offset<0 OR page_offset>1000000
        OR page_limit IS NULL OR page_limit NOT BETWEEN 1 AND 100 OR since IS NULL OR until_time IS NULL
        OR NOT isfinite(since) OR NOT isfinite(until_time) OR since>=until_time
        OR braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN NULL; END IF;
    IF section='targets' THEN
        SELECT coalesce(jsonb_agg(row),'[]') INTO result FROM (
            SELECT t.target_id,t.environment_id,t.environment_version::text,t.enabled,t.active_release_id,t.runtime_image,t.profile,
                (SELECT max(version)::text FROM ONLY braid.release_target_events e WHERE e.organization_id=org AND e.target_id=t.target_id) AS version,
                (SELECT jsonb_build_object('releaseId',r.release_id,'actorId',r.actor_id,'qualificationId',r.qualification_id,
                    'buildId',q.build_id,'artifactDigest',convert_from(q.proof_body,'UTF8')::jsonb->>'artifactDigest',
                    'approvalId',convert_from(b.intent_body,'UTF8')::jsonb->>'approvalId',
                    'approval',jsonb_build_object('approvalId',a.approval_id,'revoked',a.revoked,'createdAt',a.created_at))
                 FROM ONLY braid.local_releases r JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id
                 JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id
                 JOIN ONLY braid.build_approvals a ON a.organization_id=q.organization_id AND a.build_id=q.build_id AND a.actor_id=q.actor_id
                 WHERE r.organization_id=org AND r.release_id=t.active_release_id) AS active
            FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping
                AND (target IS NULL OR t.target_id=target) ORDER BY t.target_id LIMIT page_limit+1 OFFSET page_offset
        ) row;
        RETURN result;
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping AND t.target_id=target) THEN RETURN NULL; END IF;
    IF section='releases' THEN
        SELECT coalesce(jsonb_agg(row),'[]') INTO result FROM (
            SELECT r.release_id,r.actor_id,r.qualification_id,r.rollback_of_id,r.phase,r.rejection,r.created_at,r.finished_at,q.build_id,
                convert_from(q.proof_body,'UTF8')::jsonb->>'artifactDigest' AS artifact_digest,
                convert_from(q.proof_body,'UTF8')::jsonb->>'runtimeImage' AS runtime_image,
                convert_from(b.intent_body,'UTF8')::jsonb->>'approvalId' AS approval_id,
                a.revoked AS approval_revoked,a.created_at AS approval_created_at
            FROM ONLY braid.local_releases r JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id
            JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id
            JOIN ONLY braid.build_approvals a ON a.organization_id=q.organization_id AND a.build_id=q.build_id AND a.actor_id=q.actor_id
            WHERE r.organization_id=org AND r.target_id=target ORDER BY r.created_at DESC,r.release_id LIMIT page_limit+1 OFFSET page_offset
        ) row;
    ELSIF section='history' THEN
        SELECT coalesce(jsonb_agg(row),'[]') INTO result FROM (
            SELECT e.version::text,e.actor_id,e.enabled,e.active_release_id,e.origin,
                CASE WHEN e.origin='activation' AND r.rollback_of_id IS NOT NULL THEN 'rollback' ELSE e.origin END AS action,e.recorded_at
            FROM ONLY braid.release_target_events e LEFT JOIN ONLY braid.local_releases r ON r.organization_id=e.organization_id AND r.release_id=e.active_release_id
            WHERE e.organization_id=org AND e.target_id=target ORDER BY e.version DESC LIMIT page_limit+1 OFFSET page_offset
        ) row;
    ELSIF section='actions' THEN
        SELECT coalesce(jsonb_agg(row),'[]') INTO result FROM (
            SELECT o.operation_id,o.actor_id,o.action,o.expected_version::text,o.qualification_id,o.source_release_id,o.outcome,o.recorded_at
            FROM ONLY braid.release_target_operations o WHERE o.organization_id=org AND o.target_id=target
            ORDER BY o.recorded_at DESC,o.operation_id LIMIT page_limit+1 OFFSET page_offset
        ) row;
    ELSIF section='callers' THEN
        SELECT coalesce(jsonb_agg(row),'[]') INTO result FROM (
            SELECT g.grant_id,g.provider,g.tenant_id,g.object_id,g.granted_by,g.granted_at,g.revoked_by,g.revoked_at
            FROM ONLY braid.invocation_grants g WHERE g.organization_id=org AND g.target_id=target ORDER BY g.granted_at DESC,g.grant_id LIMIT page_limit+1 OFFSET page_offset
        ) row;
    ELSIF section IN ('invocations','summary') THEN
        WITH attempts AS (
            SELECT i.request_id,i.application_id AS caller_client_id,i.provider,i.tenant_id,i.object_id,a.release_id,i.created_at AS started_at,a.recorded_at AS finished_at,
                i.state,a.outcome,i.attempt,a.output_bytes,m.input_bytes,m.replay_count,m.replay_count>0 AS replayed,
                extract(epoch FROM (a.recorded_at-i.created_at))*1000 AS latency_ms
            FROM ONLY braid.machine_invocations i LEFT JOIN ONLY braid.machine_invocation_audit a
                ON a.organization_id=i.organization_id AND a.target_id=i.target_id AND a.provider=i.provider AND a.tenant_id=i.tenant_id
                AND a.object_id=i.object_id AND a.request_id=i.request_id AND a.sequence=i.attempt*2
            LEFT JOIN ONLY braid.invocation_operations_metadata m ON m.organization_id=i.organization_id AND m.target_id=i.target_id
                AND m.provider=i.provider AND m.tenant_id=i.tenant_id AND m.object_id=i.object_id AND m.request_id=i.request_id
            WHERE i.organization_id=org AND i.target_id=target AND i.created_at>=since AND i.created_at<until_time
                AND (caller IS NULL OR i.application_id=caller) AND (outcome_filter IS NULL OR a.outcome=outcome_filter)
        ) SELECT CASE WHEN section='summary' THEN
            jsonb_build_object('count',count(*),'failed',count(*) FILTER(WHERE state='failed'),
                'running',count(*) FILTER(WHERE state='running'),'retryable',count(*) FILTER(WHERE state='retryable'),
                'failuresByCategory',(SELECT coalesce(jsonb_object_agg(category,total),'{}') FROM
                    (SELECT outcome AS category,count(*) AS total FROM attempts WHERE outcome IS NOT NULL AND outcome<>'completed' GROUP BY outcome) categories),
                'meanLatencyMs',avg(latency_ms),'p95LatencyMs',percentile_cont(0.95) WITHIN GROUP(ORDER BY latency_ms))
            ELSE (SELECT coalesce(jsonb_agg(row),'[]') FROM (SELECT * FROM attempts ORDER BY started_at DESC,request_id,provider,tenant_id,object_id LIMIT page_limit+1 OFFSET page_offset) row)
            END INTO result FROM attempts;
    ELSE RETURN NULL;
    END IF;
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_release_operations(uuid,uuid,uuid,uuid,text,integer,integer,timestamptz,timestamptz,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_release_operations(uuid,uuid,uuid,uuid,text,integer,integer,timestamptz,timestamptz,uuid,text) TO braid_release_reader,braid_release_writer;
