-- Operations reads of triggers, runs and deliveries, and explicit administrator resolution of
-- deliveries whose remote outcome is unknown. Metadata only: no payload bytes, digests, source
-- identities, endpoints or credentials are returned or journaled.
CREATE TABLE braid.trigger_delivery_resolutions (
    organization_id uuid NOT NULL, operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    target_id uuid NOT NULL, delivery_id uuid NOT NULL, actor_id uuid NOT NULL,
    resolution text NOT NULL CHECK(resolution IN ('accepted','abandoned','resend')),
    expected_sends integer NOT NULL CHECK(expected_sends BETWEEN 0 AND 32), outcome integer NOT NULL CHECK(outcome IN (0,2,4)),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,operation_id),
    FOREIGN KEY(organization_id,delivery_id) REFERENCES braid.trigger_deliveries(organization_id,delivery_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id)
);
CREATE INDEX trigger_delivery_resolutions_delivery ON braid.trigger_delivery_resolutions(organization_id,delivery_id,recorded_at);
ALTER TABLE braid.trigger_delivery_resolutions ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.trigger_delivery_resolutions USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.trigger_delivery_resolutions FROM PUBLIC;

CREATE FUNCTION braid.guard_trigger_resolution() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_release_writer' OR TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN
        RAISE EXCEPTION 'Trigger resolution history is immutable.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_trigger_resolution() FROM PUBLIC;
CREATE TRIGGER trigger_resolution_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_delivery_resolutions FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger_resolution();
CREATE TRIGGER trigger_resolution_no_truncate BEFORE TRUNCATE ON braid.trigger_delivery_resolutions FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger_resolution();

-- Unchanged from 0043 except that the release writer may move an unknown delivery to the
-- resolution recorded for it in the same statement, and nothing else.
CREATE OR REPLACE FUNCTION braid.guard_trigger() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE resolved boolean:=false;
BEGIN
    IF TG_TABLE_NAME='trigger_deliveries' AND TG_OP='UPDATE' AND session_user='braid_release_writer' THEN
        resolved:=OLD.phase='unknown' AND EXISTS(SELECT 1 FROM ONLY braid.trigger_delivery_resolutions x WHERE x.organization_id=NEW.organization_id
            AND x.delivery_id=NEW.delivery_id AND x.outcome=0 AND x.recorded_at=statement_timestamp() AND x.expected_sends=OLD.sends
            AND NEW.phase=CASE x.resolution WHEN 'accepted' THEN 'accepted' WHEN 'abandoned' THEN 'permanentFailure' ELSE 'pending' END)
            AND NEW.sends=OLD.sends AND NEW.claim_id IS NULL;
    END IF;
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR NOT resolved AND session_user<>(CASE WHEN TG_TABLE_NAME IN ('trigger_controls','trigger_configurations','trigger_operations') THEN 'braid_release_writer' ELSE 'braid_trigger_worker' END) THEN
        RAISE EXCEPTION 'Trigger mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME IN ('trigger_configurations','trigger_operations','trigger_receipts','trigger_payloads','trigger_events') THEN
        IF TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN RAISE EXCEPTION 'Trigger history is immutable.' USING ERRCODE='42501'; END IF;
        IF TG_TABLE_NAME='trigger_payloads' THEN
            IF NOT EXISTS(SELECT 1 FROM ONLY braid.trigger_receipts r WHERE r.organization_id=NEW.organization_id
                AND r.receipt_id=NEW.receipt_id AND r.byte_count=octet_length(NEW.body) AND r.digest=encode(sha256(NEW.body),'hex')) THEN
                RAISE EXCEPTION 'Trigger payload does not match its receipt.' USING ERRCODE='42501';
            END IF;
        END IF;
    ELSIF TG_TABLE_NAME='trigger_controls' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.current_version<>1 OR NEW.generation<>1 OR NEW.admission_epoch<>1 OR NEW.enabled OR NEW.retired OR NEW.updated_at<>statement_timestamp() THEN
                RAISE EXCEPTION 'Trigger registration refused.' USING ERRCODE='42501';
            END IF;
        ELSIF ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.trigger_id,NEW.kind,NEW.source_binding_id,NEW.source_connector_id)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.trigger_id,OLD.kind,OLD.source_binding_id,OLD.source_connector_id)
            OR OLD.retired OR NEW.generation<>OLD.generation+1 OR NEW.admission_epoch NOT IN (OLD.admission_epoch,OLD.admission_epoch+1)
            OR NEW.current_version NOT IN (OLD.current_version,OLD.current_version+1) OR NEW.updated_at<>statement_timestamp() THEN
            RAISE EXCEPTION 'Trigger control history is immutable.' USING ERRCODE='42501';
        END IF;
    ELSIF TG_TABLE_NAME='trigger_work' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.phase<>'admitted' OR NEW.created_at<>statement_timestamp() THEN RAISE EXCEPTION 'Trigger admission refused.' USING ERRCODE='42501'; END IF;
        ELSIF (to_jsonb(NEW)-'phase'-'outcome'-'finished_at') IS DISTINCT FROM (to_jsonb(OLD)-'phase'-'outcome'-'finished_at')
            OR OLD.phase<>'admitted' OR NEW.finished_at IS DISTINCT FROM statement_timestamp() THEN
            RAISE EXCEPTION 'Trigger work history is immutable.' USING ERRCODE='42501';
        END IF;
    ELSE
        IF TG_OP='INSERT' THEN
            IF NEW.phase<>'pending' OR NEW.sends<>0 OR NEW.admitted_at<>statement_timestamp() THEN RAISE EXCEPTION 'Trigger delivery refused.' USING ERRCODE='42501'; END IF;
        ELSIF ROW(NEW.organization_id,NEW.delivery_id,NEW.work_id,NEW.receipt_id,NEW.target_id,NEW.output_name,NEW.destination_binding_id,NEW.destination_kind,
                NEW.connector_id,NEW.connector_version,NEW.admitted_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.delivery_id,OLD.work_id,OLD.receipt_id,OLD.target_id,OLD.output_name,OLD.destination_binding_id,OLD.destination_kind,
                OLD.connector_id,OLD.connector_version,OLD.admitted_at)
            OR OLD.phase IN ('accepted','permanentFailure','exhausted','fenced','superseded','payloadUnavailable')
            OR NEW.sends NOT IN (OLD.sends,OLD.sends+1) OR (OLD.first_ambiguous_at IS NOT NULL AND NEW.first_ambiguous_at IS DISTINCT FROM OLD.first_ambiguous_at) THEN
            RAISE EXCEPTION 'Trigger delivery history is immutable.' USING ERRCODE='42501';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;

-- One bounded Operations page for a target: triggers, runs or deliveries, newest first.
CREATE FUNCTION braid.read_trigger_operations(org uuid, project uuid, mapping uuid, target uuid, section text, page_offset integer, page_limit integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE result jsonb;
BEGIN
    IF session_user NOT IN ('braid_release_reader','braid_release_writer') OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR section IS NULL OR section NOT IN ('triggers','runs','deliveries') OR page_offset IS NULL OR page_offset NOT BETWEEN 0 AND 1000000
        OR page_limit IS NULL OR page_limit NOT BETWEEN 1 AND 101
        OR braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['publisher','administrator']) IS NOT TRUE
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target AND t.project_id=project AND t.mapping_id=mapping) THEN
        RETURN NULL;
    END IF;
    IF section='triggers' THEN
        SELECT coalesce(jsonb_agg(x ORDER BY x->>'updated_at' DESC,x->>'trigger_id'),'[]') INTO result FROM (
            SELECT jsonb_build_object('trigger_id',c.trigger_id,'trigger_kind',c.kind,'configuration_version',c.current_version,'enabled',c.enabled,
                'retired',c.retired,'generation',c.generation::text,'updated_at',c.updated_at,
                'pending_work',(SELECT count(*) FROM ONLY braid.trigger_work w WHERE w.organization_id=org AND w.trigger_id=c.trigger_id AND w.phase='admitted'),
                'unknown_deliveries',(SELECT count(*) FROM ONLY braid.trigger_deliveries d JOIN ONLY braid.trigger_work w ON w.organization_id=d.organization_id
                    AND w.work_id=d.work_id WHERE d.organization_id=org AND w.trigger_id=c.trigger_id AND d.phase='unknown')) x
            FROM ONLY braid.trigger_controls c WHERE c.organization_id=org AND c.target_id=target
            ORDER BY c.updated_at DESC,c.trigger_id OFFSET page_offset LIMIT page_limit) y;
    ELSIF section='runs' THEN
        SELECT coalesce(jsonb_agg(x ORDER BY x->>'created_at' DESC,x->>'run_id'),'[]') INTO result FROM (
            SELECT jsonb_build_object('run_id',r.run_id,'trigger_id',r.trigger_id,'run_kind',r.kind,'scheduled_at',r.scheduled_at,'state',r.state,
                'pages',r.pages,'discovered',r.discovered,'admitted',r.admitted,'changed',r.changed,'rejected',r.rejected,
                'created_at',r.created_at,'finished_at',r.finished_at) x
            FROM ONLY braid.trigger_runs r WHERE r.organization_id=org AND r.target_id=target
            ORDER BY r.created_at DESC,r.run_id OFFSET page_offset LIMIT page_limit) y;
    ELSE
        SELECT coalesce(jsonb_agg(x ORDER BY x->>'admitted_at' DESC,x->>'delivery_id'),'[]') INTO result FROM (
            SELECT jsonb_build_object('delivery_id',d.delivery_id,'work_id',d.work_id,'trigger_id',w.trigger_id,'destination_kind',d.destination_kind,
                'connector_id',d.connector_id,'connector_version',d.connector_version,'phase',d.phase,'sends',d.sends,'version',d.sends::text,
                'next_at',d.next_at,'admitted_at',d.admitted_at,'first_ambiguous_at',d.first_ambiguous_at,'last_dispatch_at',d.last_dispatch_at,
                'settled_at',d.settled_at,'resolution',(SELECT x.resolution FROM ONLY braid.trigger_delivery_resolutions x WHERE x.organization_id=org
                    AND x.delivery_id=d.delivery_id AND x.outcome=0 ORDER BY x.recorded_at DESC LIMIT 1)) x
            FROM ONLY braid.trigger_deliveries d JOIN ONLY braid.trigger_work w ON w.organization_id=d.organization_id AND w.work_id=d.work_id
            WHERE d.organization_id=org AND d.target_id=target
            ORDER BY d.admitted_at DESC,d.delivery_id OFFSET page_offset LIMIT page_limit) y;
    END IF;
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_operations(uuid,uuid,uuid,uuid,text,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_operations(uuid,uuid,uuid,uuid,text,integer,integer) TO braid_release_reader,braid_release_writer;

-- Administrator resolution of one delivery whose remote outcome is unknown: accepted (receipt
-- confirmed out of band), abandoned (no further send), or resend (the same delivery ID and bytes,
-- with explicit acknowledgement of duplicate risk). Version-conditional on the send count and
-- idempotent by operation ID. Result: 0 applied, 2 stale, 4 refused, 5 the operation ID was used
-- with a different body (nothing recorded). The outcome is journaled in the audit log.
CREATE FUNCTION braid.resolve_trigger_delivery(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, delivery uuid, operation uuid, expected integer, resolution text) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; old braid.trigger_delivery_resolutions%ROWTYPE;
    x braid.trigger_deliveries%ROWTYPE; p jsonb; result integer;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_release_writer' OR actor IS NULL OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL OR delivery IS NULL
        OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR expected IS NULL OR expected NOT BETWEEN 0 AND 32
        OR resolution IS NULL OR resolution NOT IN ('accepted','abandoned','resend') THEN RETURN 4; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target AND t.project_id=project
            AND t.mapping_id=mapping AND t.environment_id=environment AND t.environment_version=target_version) THEN RETURN 4; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('braid.trigger.resolution:'||org::text||':'||operation::text,0));
    SELECT * INTO old FROM ONLY braid.trigger_delivery_resolutions y WHERE y.organization_id=org AND y.operation_id=operation;
    IF FOUND THEN
        IF ROW(old.target_id,old.delivery_id,old.actor_id,old.resolution,old.expected_sends) IS DISTINCT FROM ROW(target,delivery,actor,resolution,expected) THEN RETURN 5; END IF;
        RETURN old.outcome;
    END IF;
    SELECT * INTO x FROM ONLY braid.trigger_deliveries y WHERE y.organization_id=org AND y.delivery_id=delivery AND y.target_id=target FOR UPDATE;
    IF NOT FOUND THEN RETURN 4; END IF;
    SELECT convert_from(f.definition,'UTF8')::jsonb->'policies' INTO p FROM ONLY braid.trigger_work w
        JOIN ONLY braid.trigger_configurations f ON f.organization_id=w.organization_id AND f.trigger_id=w.trigger_id AND f.version=w.configuration_version
        WHERE w.organization_id=org AND w.work_id=x.work_id;
    result:=CASE WHEN x.phase<>'unknown' OR x.sends<>expected THEN 2
        WHEN resolution='resend' AND x.sends>=(p->>'totalSends')::integer THEN 4 ELSE 0 END;
    INSERT INTO braid.trigger_delivery_resolutions(organization_id,operation_id,target_id,delivery_id,actor_id,resolution,expected_sends,outcome)
        VALUES(org,operation,target,delivery,actor,resolution,expected,result);
    IF result=0 THEN
        UPDATE ONLY braid.trigger_deliveries SET phase=CASE resolution WHEN 'accepted' THEN 'accepted' WHEN 'abandoned' THEN 'permanentFailure' ELSE 'pending' END,
            next_at=CASE WHEN resolution='resend' THEN clock_timestamp() ELSE next_at END,
            settled_at=CASE WHEN resolution='resend' THEN NULL ELSE statement_timestamp() END
            WHERE organization_id=org AND delivery_id=delivery;
    END IF;
    PERFORM braid.audit_log_append('trigger_delivery_resolutions',operation::text,org,project,mapping,actor,'user','release',
        'trigger.delivery.'||resolution,delivery,CASE result WHEN 0 THEN 'applied' WHEN 2 THEN 'stale' ELSE 'refused' END,statement_timestamp(),false);
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.resolve_trigger_delivery(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,integer,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.resolve_trigger_delivery(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,integer,text) TO braid_release_writer;
