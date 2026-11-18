-- Private markers: one rejected source version per route configuration, never the file name or contents.
CREATE TABLE braid.trigger_input_quarantine (
 organization_id uuid NOT NULL, run_id uuid NOT NULL, trigger_id uuid NOT NULL, configuration_version bigint NOT NULL,
 source_identity text NOT NULL CHECK(source_identity~'^src:[0-9a-f]{64}$'), rejection text NOT NULL CHECK(rejection IN ('tooLarge','invalidInput','digestMismatch')),
 item_id uuid NOT NULL DEFAULT gen_random_uuid(), recorded_at timestamptz NOT NULL DEFAULT statement_timestamp(),
 PRIMARY KEY(organization_id,trigger_id,configuration_version,source_identity), FOREIGN KEY(organization_id,run_id) REFERENCES braid.trigger_runs(organization_id,run_id));
ALTER TABLE braid.trigger_input_quarantine ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.trigger_input_quarantine USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.trigger_input_quarantine FROM PUBLIC;
CREATE FUNCTION braid.record_trigger_quarantine_page(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, run uuid, claim uuid, cursor_before text, cursor_after text, complete boolean, discovered integer, changed integer, rejected integer, issuer text, tenant uuid, principal uuid, application uuid, sources text[], keys text[], request_shas text[], bodies bytea[], invalid_sources text[], reasons text[]) RETURNS TABLE(status integer, works uuid[], outcomes integer[])
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE page record;
BEGIN
    IF cardinality(invalid_sources) IS DISTINCT FROM rejected OR cardinality(reasons) IS DISTINCT FROM rejected OR rejected NOT BETWEEN 0 AND 1024 THEN RAISE EXCEPTION 'Invalid rejected page.'; END IF;
    SELECT * INTO STRICT page FROM braid.record_trigger_run_page(org,project,mapping,environment,target_version,target,trigger,run,claim,cursor_before,cursor_after,complete,discovered,changed,rejected,issuer,tenant,principal,application,sources,keys,request_shas,bodies);
    IF page.status=0 AND rejected>0 THEN
        INSERT INTO braid.trigger_input_quarantine(organization_id,run_id,trigger_id,configuration_version,source_identity,rejection)
        SELECT org,run,trigger,r.configuration_version,v.id,v.reason FROM ONLY braid.trigger_runs r CROSS JOIN unnest(invalid_sources,reasons) v(id,reason)
        WHERE r.organization_id=org AND r.run_id=run AND r.trigger_id=trigger ON CONFLICT DO NOTHING;
    END IF;
    RETURN QUERY SELECT page.status,page.works,page.outcomes;
END;
$body$;
REVOKE ALL ON FUNCTION braid.record_trigger_quarantine_page(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,uuid,text,text,boolean,integer,integer,integer,text,uuid,uuid,uuid,text[],text[],text[],bytea[],text[],text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.record_trigger_quarantine_page(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,uuid,text,text,boolean,integer,integer,integer,text,uuid,uuid,uuid,text[],text[],text[],bytea[],text[],text[]) TO braid_trigger_worker;
CREATE OR REPLACE FUNCTION braid.read_trigger_sources(org uuid, target uuid, trigger uuid, issuer text, tenant uuid, principal uuid, sources text[]) RETURNS TABLE(source text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT DISTINCT w.source_identity FROM ONLY braid.trigger_controls c JOIN ONLY braid.trigger_work w ON w.organization_id=c.organization_id
        AND w.target_id=c.target_id AND w.source_binding_id=c.source_binding_id
    WHERE session_user='braid_trigger_worker' AND c.organization_id=org AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND c.trigger_id=trigger AND c.target_id=target AND w.provider=issuer AND w.tenant_id=tenant AND w.object_id=principal
        AND cardinality(sources)<=1024 AND w.source_identity=ANY(sources)
    UNION SELECT q.source_identity FROM ONLY braid.trigger_input_quarantine q JOIN ONLY braid.trigger_controls c ON c.organization_id=q.organization_id AND c.trigger_id=q.trigger_id
    JOIN ONLY braid.trigger_runs r ON r.organization_id=q.organization_id AND r.run_id=q.run_id
    WHERE session_user='braid_trigger_worker' AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid AND c.organization_id=org
    AND c.trigger_id=trigger AND c.target_id=target AND c.current_version=q.configuration_version AND r.provider=issuer AND r.tenant_id=tenant AND r.object_id=principal
    AND cardinality(sources)<=1024 AND q.source_identity=ANY(sources)
$body$;
CREATE OR REPLACE FUNCTION braid.read_trigger_operations(org uuid, project uuid, mapping uuid, target uuid, section text, page_offset integer, page_limit integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE result jsonb;
BEGIN
    IF session_user NOT IN ('braid_release_reader','braid_release_writer') OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR section IS NULL OR section NOT IN ('triggers','runs','deliveries','quarantine') OR page_offset IS NULL OR page_offset NOT BETWEEN 0 AND 1000000
        OR page_limit IS NULL OR page_limit NOT BETWEEN 1 AND 101
        OR braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['publisher','administrator']) IS NOT TRUE
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target AND t.project_id=project AND t.mapping_id=mapping) THEN
        RETURN NULL;
    END IF;
    IF section='quarantine' THEN
        SELECT coalesce(jsonb_agg(x ORDER BY recorded DESC,id),'[]') INTO result FROM (
            SELECT jsonb_build_object('item_id',id,'trigger_id',trig,'configuration_version',v,'reason',reason,'recorded_at',recorded) x,id,recorded FROM (
                SELECT q.item_id id,q.trigger_id trig,q.configuration_version v,q.rejection reason,q.recorded_at recorded FROM ONLY braid.trigger_input_quarantine q JOIN ONLY braid.trigger_runs r USING(organization_id,run_id)
                WHERE q.organization_id=org AND r.target_id=target
                UNION ALL SELECT w.work_id,w.trigger_id,w.configuration_version,w.outcome,w.finished_at FROM ONLY braid.trigger_work w
                WHERE w.organization_id=org AND w.target_id=target AND w.phase='failed') failures
            ORDER BY recorded DESC,id OFFSET page_offset LIMIT page_limit) y;
    ELSIF section='triggers' THEN
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
