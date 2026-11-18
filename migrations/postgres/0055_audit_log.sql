-- Metadata-only history survives retirement of its original resource. No payload,
-- digest, private text, arbitrary JSON or credential is copied into this journal.
LOCK TABLE braid.administration_audit,braid.local_release_audit,braid.invocation_grant_audit,
    braid.machine_invocation_audit,braid.api_request_audit,braid.chat_payload_audit,
    braid.build_qualifications,braid.build_approvals,braid.build_runs,braid.build_publications IN SHARE ROW EXCLUSIVE MODE;
CREATE TABLE braid.audit_log_events (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    event_id uuid NOT NULL DEFAULT pg_catalog.gen_random_uuid() CHECK(event_id<>'00000000-0000-0000-0000-000000000000'),
    source text COLLATE "C" NOT NULL CHECK(source ~ '^[a-z_]{1,64}$'),
    source_key text COLLATE "C" NOT NULL CHECK(octet_length(source_key) BETWEEN 1 AND 512),
    occurred_at timestamptz NOT NULL CHECK(isfinite(occurred_at)),
    kind text COLLATE "C" NOT NULL CHECK(kind IN ('administration','release','build','approval','request','payload','grant','invocation')),
    action text COLLATE "C" NOT NULL CHECK(action ~ '^[A-Za-z][A-Za-z0-9.]{0,127}$'),
    actor_kind text COLLATE "C" NOT NULL CHECK(actor_kind IN ('user','machine','system','unknown')),
    actor_id uuid, project_id uuid, mapping_id uuid,
    resource_id uuid NOT NULL CHECK(resource_id<>'00000000-0000-0000-0000-000000000000'),
    outcome text COLLATE "C" NOT NULL CHECK(outcome ~ '^[A-Za-z][A-Za-z0-9.]{0,63}$'),
    historical_observation boolean NOT NULL DEFAULT false,
    PRIMARY KEY(organization_id,event_id), UNIQUE(organization_id,source,source_key),
    CHECK((actor_kind IN ('user','machine'))=(actor_id IS NOT NULL)),
    CHECK(actor_id IS NULL OR actor_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(project_id IS NULL OR project_id<>'00000000-0000-0000-0000-000000000000'),
    CHECK(mapping_id IS NULL OR project_id IS NOT NULL)
);
CREATE INDEX audit_log_time ON braid.audit_log_events(organization_id,occurred_at DESC,event_id DESC);
CREATE INDEX audit_log_project_time ON braid.audit_log_events(organization_id,project_id,occurred_at DESC,event_id DESC);
CREATE INDEX audit_log_actor_time ON braid.audit_log_events(organization_id,actor_id,occurred_at DESC,event_id DESC);
CREATE INDEX audit_log_kind_time ON braid.audit_log_events(organization_id,kind,occurred_at DESC,event_id DESC);
ALTER TABLE braid.audit_log_events ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.audit_log_events
    USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.audit_log_events FROM PUBLIC;

CREATE FUNCTION braid.audit_log_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP<>'INSERT' OR current_user<>'braid_migrator' THEN
        RAISE EXCEPTION 'Audit history is append-only.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER audit_log_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.audit_log_events
    FOR EACH ROW EXECUTE FUNCTION braid.audit_log_guard();
CREATE TRIGGER audit_log_no_truncate BEFORE TRUNCATE ON braid.audit_log_events
    FOR EACH STATEMENT EXECUTE FUNCTION braid.audit_log_guard();

CREATE FUNCTION braid.audit_log_append(p_source text,p_key text,p_org uuid,p_project uuid,p_mapping uuid,
    p_actor uuid,p_actor_kind text,p_kind text,p_action text,p_resource uuid,p_outcome text,p_at timestamp with time zone,p_observation boolean)
RETURNS void LANGUAGE sql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    INSERT INTO braid.audit_log_events(organization_id,source,source_key,occurred_at,kind,action,actor_kind,actor_id,
        project_id,mapping_id,resource_id,outcome,historical_observation)
    VALUES(p_org,p_source,p_key,p_at,p_kind,p_action,p_actor_kind,p_actor,p_project,p_mapping,p_resource,p_outcome,p_observation)
    ON CONFLICT(organization_id,source,source_key) DO NOTHING
$body$;

CREATE FUNCTION braid.audit_log_project(p_source text,p_row anyelement) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE columns text[]; expression text; result jsonb;
BEGIN
    columns:=CASE p_source
        WHEN 'administration_audit' THEN ARRAY['organization_id','sequence','actor_id','target_id','project_id','operation','recorded_at']
        WHEN 'local_release_audit' THEN ARRAY['organization_id','release_id','sequence','kind','rejection','recorded_at']
        WHEN 'invocation_grant_audit' THEN ARRAY['organization_id','grant_id','sequence','actor_id','kind','recorded_at']
        WHEN 'machine_invocation_audit' THEN ARRAY['organization_id','target_id','provider','tenant_id','object_id','request_id','sequence','kind','outcome','recorded_at']
        WHEN 'api_request_audit' THEN ARRAY['organization_id','principal_id','audit_id','operation','resource_names','resource_values','recorded_at']
        WHEN 'chat_payload_audit' THEN ARRAY['organization_id','project_id','creator_id','subject_id','event_id','source_kind','recorded_at']
        WHEN 'build_qualifications' THEN ARRAY['organization_id','project_id','mapping_id','actor_id','qualification_id','created_at']
        WHEN 'build_approvals' THEN ARRAY['organization_id','project_id','mapping_id','actor_id','approval_id','created_at','revoked']
        WHEN 'build_publications' THEN ARRAY['organization_id','build_id','approval_id','created_at','state']
        WHEN 'build_runs' THEN ARRAY['organization_id','project_id','actor_id','build_id','created_at','state','attempt']
        ELSE NULL END;
    IF columns IS NULL THEN RAISE EXCEPTION 'Unknown audit source.' USING ERRCODE='22023'; END IF;
    SELECT string_agg(format('%L,(($1)::braid.%I).%I',name,p_source,name),',' ORDER BY ordinal)
        INTO expression FROM unnest(columns) WITH ORDINALITY AS c(name,ordinal);
    EXECUTE 'SELECT jsonb_build_object('||expression||')' INTO result USING p_row;
    RETURN result;
END;
$body$;

CREATE FUNCTION braid.audit_log_record(p_source text,n jsonb,o jsonb,p_snapshot boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE org uuid:=(n->>'organization_id')::uuid; project uuid; mapping uuid; actor uuid;
    resource uuid; key text; kind text; action text; outcome text; at_time timestamptz; actor_kind text:='user';
BEGIN
    IF p_source='administration_audit' THEN
        project:=(n->>'project_id')::uuid; actor:=(n->>'actor_id')::uuid; resource:=(n->>'target_id')::uuid;
        key:=n->>'sequence'; kind:='administration'; action:=n->>'operation'; outcome:='recorded'; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='local_release_audit' THEN
        SELECT t.project_id,t.mapping_id,r.actor_id INTO project,mapping,actor FROM braid.local_releases r
            JOIN braid.local_release_targets t ON t.organization_id=r.organization_id AND t.target_id=r.target_id
            WHERE r.organization_id=org AND r.release_id=(n->>'release_id')::uuid;
        resource:=(n->>'release_id')::uuid; key:=resource::text||':'||(n->>'sequence'); kind:='release'; action:=n->>'kind';
        outcome:=CASE WHEN n->>'rejection'='none' THEN n->>'kind' ELSE n->>'rejection' END; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='invocation_grant_audit' THEN
        SELECT g.project_id,g.mapping_id INTO project,mapping FROM braid.invocation_grants g
            WHERE g.organization_id=org AND g.grant_id=(n->>'grant_id')::uuid;
        actor:=(n->>'actor_id')::uuid; resource:=(n->>'grant_id')::uuid; key:=resource::text||':'||(n->>'sequence');
        kind:='grant'; action:=n->>'kind'; outcome:=action; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='machine_invocation_audit' THEN
        SELECT i.project_id,i.mapping_id INTO project,mapping FROM braid.machine_invocations i
            WHERE i.organization_id=org AND i.target_id=(n->>'target_id')::uuid AND i.provider=n->>'provider'
                AND i.tenant_id=(n->>'tenant_id')::uuid AND i.object_id=(n->>'object_id')::uuid AND i.request_id=(n->>'request_id')::uuid;
        actor:=(n->>'object_id')::uuid; actor_kind:='machine'; resource:=(n->>'request_id')::uuid;
        key:=(n->>'target_id')||':'||(n->>'tenant_id')||':'||actor::text||':'||resource::text||':'||(n->>'sequence');
        kind:='invocation'; action:=n->>'kind'; outcome:=n->>'outcome'; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='api_request_audit' THEN
        SELECT n->'resource_values'->>(e.ordinality::integer-1) INTO key
            FROM jsonb_array_elements_text(n->'resource_names') WITH ORDINALITY e(name,ordinality) WHERE name='projectId';
        project:=CASE WHEN key ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND key<>'00000000-0000-0000-0000-000000000000' THEN key::uuid END;
        SELECT n->'resource_values'->>(e.ordinality::integer-1) INTO key
            FROM jsonb_array_elements_text(n->'resource_names') WITH ORDINALITY e(name,ordinality) WHERE name='mappingId';
        -- The source permits independent route identifiers and decimal values.
        -- A mapping without a known project stays in the original request audit;
        -- it must not manufacture project authority or prevent the request record.
        mapping:=CASE WHEN project IS NOT NULL AND key ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND key<>'00000000-0000-0000-0000-000000000000' THEN key::uuid END;
        actor:=(n->>'principal_id')::uuid; resource:=(n->>'audit_id')::uuid; key:=resource::text;
        kind:='request'; action:=n->>'operation'; outcome:='admitted'; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='chat_payload_audit' THEN
        project:=(n->>'project_id')::uuid; actor:=(n->>'creator_id')::uuid; resource:=(n->>'subject_id')::uuid;
        key:=actor::text||':'||(n->>'event_id'); kind:='payload'; outcome:='shared';
        action:=CASE n->>'source_kind' WHEN '1' THEN 'previewInput' WHEN '2' THEN 'previewOutput'
            WHEN '3' THEN 'regressionCase' WHEN '4' THEN 'regressionOutput' WHEN '5' THEN 'specification' END; at_time:=(n->>'recorded_at')::timestamptz;
    ELSIF p_source='build_qualifications' THEN
        project:=(n->>'project_id')::uuid; mapping:=(n->>'mapping_id')::uuid; actor:=(n->>'actor_id')::uuid;
        resource:=(n->>'qualification_id')::uuid; key:=resource::text; kind:='build'; action:='qualified'; outcome:='qualified'; at_time:=(n->>'created_at')::timestamptz;
    ELSIF p_source='build_approvals' THEN
        project:=(n->>'project_id')::uuid; mapping:=(n->>'mapping_id')::uuid; resource:=(n->>'approval_id')::uuid;
        IF o IS NULL THEN
            PERFORM braid.audit_log_append(p_source,resource::text||':approved',org,project,mapping,(n->>'actor_id')::uuid,
                'user','approval','approved',resource,'approved',(n->>'created_at')::timestamptz,false);
        END IF;
        IF (n->>'revoked')::boolean AND (o IS NULL OR NOT (o->>'revoked')::boolean) THEN
            actor:=CASE WHEN p_snapshot THEN NULL ELSE NULLIF(current_setting('braid.build_principal_id',true),'')::uuid END;
            PERFORM braid.audit_log_append(p_source,resource::text||':revoked',org,project,mapping,actor,
                CASE WHEN actor IS NULL THEN 'unknown' ELSE 'user' END,'approval','revoked',resource,'revoked',clock_timestamp(),p_snapshot);
        END IF;
        RETURN;
    ELSIF p_source='build_publications' THEN
        resource:=(n->>'build_id')::uuid;
        SELECT a.project_id,a.mapping_id,a.actor_id INTO project,mapping,actor FROM braid.build_approvals a
            WHERE a.organization_id=org AND a.approval_id=(n->>'approval_id')::uuid;
        IF o IS NULL THEN
            PERFORM braid.audit_log_append(p_source,resource::text||':staged',org,project,mapping,actor,
                'user','build','staged',resource,'staged',(n->>'created_at')::timestamptz,false);
        END IF;
        IF (n->>'state')::integer=2 AND (o IS NULL OR (o->>'state')::integer<>2) THEN
            actor:=CASE WHEN p_snapshot THEN NULL ELSE NULLIF(current_setting('braid.build_principal_id',true),'')::uuid END;
            PERFORM braid.audit_log_append(p_source,resource::text||':published',org,project,mapping,actor,
                CASE WHEN actor IS NULL THEN 'unknown' ELSE 'user' END,'build','published',resource,'published',clock_timestamp(),p_snapshot);
        END IF;
        RETURN;
    ELSIF p_source='build_runs' THEN
        project:=(n->>'project_id')::uuid; resource:=(n->>'build_id')::uuid;
        SELECT a.mapping_id INTO mapping FROM braid.build_approvals a WHERE a.organization_id=org AND a.build_id=resource;
        IF o IS NULL THEN
            PERFORM braid.audit_log_append(p_source,resource::text||':created',org,project,mapping,(n->>'actor_id')::uuid,
                'user','build','created',resource,'queued',(n->>'created_at')::timestamptz,false);
        END IF;
        IF (o IS NULL AND (n->>'state'<>'queued' OR (n->>'attempt')::integer>0)) OR
            (o IS NOT NULL AND (o->>'state' IS DISTINCT FROM n->>'state' OR o->>'attempt' IS DISTINCT FROM n->>'attempt')) THEN
            PERFORM braid.audit_log_append(p_source,resource::text||':'||(n->>'attempt')||':'||(n->>'state'),org,project,mapping,NULL,
                'system','build','stateChanged',resource,n->>'state',clock_timestamp(),p_snapshot);
        END IF;
        RETURN;
    ELSE
        RAISE EXCEPTION 'Unknown audit source.' USING ERRCODE='22023';
    END IF;
    PERFORM braid.audit_log_append(p_source,key,org,project,mapping,actor,actor_kind,kind,action,resource,outcome,at_time,false);
END;
$body$;

-- Backfill known event times exactly. Current state without an old event time is
-- explicitly an observation at migration time, not a fabricated historical action.
DO $backfill$
DECLARE s text; r record;
BEGIN
    FOREACH s IN ARRAY ARRAY['administration_audit','local_release_audit','invocation_grant_audit','machine_invocation_audit',
        'api_request_audit','chat_payload_audit','build_qualifications','build_approvals','build_runs','build_publications'] LOOP
        FOR r IN EXECUTE format('SELECT braid.audit_log_project(%L,x) AS value FROM ONLY braid.%I x',s,s) LOOP
            PERFORM braid.audit_log_record(s,r.value,NULL,true);
        END LOOP;
    END LOOP;
END;
$backfill$;

CREATE FUNCTION braid.audit_log_capture() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    PERFORM braid.audit_log_record(TG_TABLE_NAME,braid.audit_log_project(TG_TABLE_NAME,NEW),
        CASE WHEN TG_OP='UPDATE' THEN braid.audit_log_project(TG_TABLE_NAME,OLD) ELSE NULL END,false);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER audit_capture_administration AFTER INSERT ON braid.administration_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_release AFTER INSERT ON braid.local_release_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_grant AFTER INSERT ON braid.invocation_grant_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_invocation AFTER INSERT ON braid.machine_invocation_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_request AFTER INSERT ON braid.api_request_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_payload AFTER INSERT ON braid.chat_payload_audit FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_qualification AFTER INSERT ON braid.build_qualifications FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_approval AFTER INSERT OR UPDATE ON braid.build_approvals FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_build AFTER INSERT OR UPDATE ON braid.build_runs FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();
CREATE TRIGGER audit_capture_publication AFTER INSERT OR UPDATE ON braid.build_publications FOR EACH ROW EXECUTE FUNCTION braid.audit_log_capture();

CREATE FUNCTION braid.read_audit_log(p_org uuid,p_actor uuid,p_project uuid,p_filter_actor uuid,p_kind text,
    p_since timestamp with time zone,p_until timestamp with time zone,p_before_at timestamp with time zone,p_before_id uuid,p_limit integer)
RETURNS jsonb LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE org_admin boolean; result jsonb;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN NULL; END IF;
    IF p_org IS NULL OR p_actor IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR
        p_since IS NULL OR p_until IS NULL OR NOT isfinite(p_since) OR NOT isfinite(p_until) OR p_since>=p_until OR
        ((p_before_at IS NULL)<>(p_before_id IS NULL)) OR p_before_id='00000000-0000-0000-0000-000000000000' OR
        p_kind IS NOT NULL AND p_kind NOT IN ('administration','release','build','approval','request','payload','grant','invocation') THEN
        RAISE EXCEPTION 'Invalid audit query.' USING ERRCODE='22023';
    END IF;
    org_admin:=braid.administration_authorized(p_org,p_actor,NULL);
    IF NOT org_admin AND NOT EXISTS(SELECT 1 FROM braid.project_memberships m WHERE m.organization_id=p_org
        AND (p_project IS NULL OR m.project_id=p_project) AND m.principal_id=p_actor AND braid.administration_authorized(p_org,p_actor,m.project_id)) THEN RETURN NULL; END IF;
    WITH page AS (
        SELECT e.* FROM ONLY braid.audit_log_events e WHERE e.organization_id=p_org
            AND (p_project IS NULL OR e.project_id=p_project) AND (p_filter_actor IS NULL OR e.actor_id=p_filter_actor)
            AND (p_kind IS NULL OR e.kind=p_kind) AND e.occurred_at>=p_since AND e.occurred_at<p_until
            AND (p_before_at IS NULL OR (e.occurred_at,e.event_id)<(p_before_at,p_before_id))
            AND (org_admin OR e.project_id IS NOT NULL AND braid.administration_authorized(p_org,p_actor,e.project_id))
        ORDER BY e.occurred_at DESC,e.event_id DESC LIMIT p_limit+1
    )
    SELECT jsonb_build_object('entries',coalesce((SELECT jsonb_agg(jsonb_build_object(
        'eventId',p.event_id::text,'occurredAtUtc',p.occurred_at,'kind',p.kind,'action',p.action,'actorKind',p.actor_kind,
        'actorId',p.actor_id,'projectId',p.project_id,'mappingId',p.mapping_id,'resourceId',p.resource_id,
        'outcome',p.outcome,'historicalObservation',p.historical_observation) ORDER BY p.occurred_at DESC,p.event_id DESC)
        FROM (SELECT * FROM page ORDER BY occurred_at DESC,event_id DESC LIMIT p_limit) p),'[]'::jsonb),
        'hasMore',(SELECT count(*)>p_limit FROM page),'organizationWide',org_admin) INTO result;
    RETURN result;
END;
$body$;
REVOKE ALL ON FUNCTION braid.audit_log_guard(),braid.audit_log_append(text,text,uuid,uuid,uuid,uuid,text,text,text,uuid,text,timestamptz,boolean),
    braid.audit_log_project(text,anyelement),braid.audit_log_record(text,jsonb,jsonb,boolean),braid.audit_log_capture(),braid.read_audit_log(uuid,uuid,uuid,uuid,text,timestamptz,timestamptz,timestamptz,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_audit_log(uuid,uuid,uuid,uuid,text,timestamptz,timestamptz,timestamptz,uuid,integer) TO braid_administration;
