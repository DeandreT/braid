-- Schedule triggers with deterministic runs per scheduled instant, explicit batch envelopes and
-- their durable parents. A run records discovery only; every admitted order keeps its own
-- ledger boundary, pin and outbox from 0043. Nothing here sends or executes anything.
ALTER TABLE braid.trigger_controls DROP CONSTRAINT trigger_controls_kind_check;
ALTER TABLE braid.trigger_controls ADD CONSTRAINT trigger_controls_kind_check CHECK(kind IN ('serviceBusQueue','schedule'));

-- One scheduled occurrence (unique per schedule epoch and instant) or one queue batch parent
-- (unique per source identity, retaining its complete child manifest digest). At most one run of
-- a trigger scans at a time; a later occurrence is recorded as skipped, never run alongside it.
CREATE TABLE braid.trigger_runs (
    organization_id uuid NOT NULL, run_id uuid NOT NULL CHECK(run_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL, trigger_id uuid NOT NULL, configuration_version bigint NOT NULL,
    kind text NOT NULL CHECK(kind IN ('schedule','queueBatch')),
    schedule_epoch text CHECK(schedule_epoch~'^[0-9a-f]{64}$'), scheduled_at timestamp with time zone CHECK(isfinite(scheduled_at)),
    utc_offset_seconds integer CHECK(utc_offset_seconds BETWEEN -86400 AND 86400),
    source_binding_id uuid NOT NULL, provider text NOT NULL CHECK(provider IN ('entra')), tenant_id uuid NOT NULL, object_id uuid NOT NULL, application_id uuid NOT NULL,
    source_identity text CHECK(char_length(source_identity) BETWEEN 1 AND 128), manifest_digest text CHECK(manifest_digest~'^[0-9a-f]{64}$'),
    state text NOT NULL CHECK(state IN ('missed','skipped','scanning','scanned','fenced','admitted')),
    claim_id uuid, claim_expires_at timestamp with time zone, scan_cursor text CHECK(octet_length(scan_cursor) BETWEEN 1 AND 4096),
    pages integer NOT NULL CHECK(pages>=0), discovered integer NOT NULL CHECK(discovered>=0), admitted integer NOT NULL CHECK(admitted>=0),
    changed integer NOT NULL CHECK(changed>=0), rejected integer NOT NULL CHECK(rejected>=0),
    created_at timestamp with time zone NOT NULL CHECK(isfinite(created_at)), updated_at timestamp with time zone NOT NULL CHECK(isfinite(updated_at)),
    finished_at timestamp with time zone,
    PRIMARY KEY(organization_id,run_id),
    UNIQUE(organization_id,trigger_id,schedule_epoch,scheduled_at),
    UNIQUE(organization_id,target_id,provider,tenant_id,object_id,source_binding_id,source_identity),
    FOREIGN KEY(organization_id,trigger_id,configuration_version) REFERENCES braid.trigger_configurations(organization_id,trigger_id,version),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK((kind='schedule')=(schedule_epoch IS NOT NULL AND scheduled_at IS NOT NULL AND utc_offset_seconds IS NOT NULL)),
    CHECK((kind='queueBatch')=(source_identity IS NOT NULL AND manifest_digest IS NOT NULL)),
    CHECK((kind='queueBatch')=(state='admitted')),
    CHECK((state='scanning')=(claim_id IS NOT NULL) AND (claim_id IS NULL)=(claim_expires_at IS NULL)),
    CHECK((state='scanning')=(finished_at IS NULL)), CHECK(finished_at IS NULL OR finished_at>=created_at)
);
CREATE UNIQUE INDEX trigger_runs_scanning ON braid.trigger_runs(organization_id,trigger_id) WHERE state='scanning';
-- Which run discovered or admitted each order; an order belongs to at most one run.
CREATE TABLE braid.trigger_run_work (
    organization_id uuid NOT NULL, run_id uuid NOT NULL, work_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,work_id),
    FOREIGN KEY(organization_id,run_id) REFERENCES braid.trigger_runs(organization_id,run_id),
    FOREIGN KEY(organization_id,work_id) REFERENCES braid.trigger_work(organization_id,work_id)
);
CREATE INDEX trigger_run_work_run ON braid.trigger_run_work(organization_id,run_id);
ALTER TABLE braid.trigger_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_run_work ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.trigger_runs USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_run_work USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.trigger_runs,braid.trigger_run_work FROM PUBLIC;

CREATE FUNCTION braid.guard_trigger_run() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE') OR session_user<>'braid_trigger_worker' THEN
        RAISE EXCEPTION 'Trigger run mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='trigger_run_work' THEN
        IF TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN RAISE EXCEPTION 'Trigger run history is immutable.' USING ERRCODE='42501'; END IF;
    ELSIF TG_OP='INSERT' THEN
        IF NEW.created_at<>statement_timestamp() OR NEW.updated_at<>NEW.created_at OR NEW.scan_cursor IS NOT NULL
            OR (NEW.kind='schedule' AND (NEW.pages<>0 OR NEW.discovered<>0 OR NEW.admitted<>0 OR NEW.changed<>0 OR NEW.rejected<>0
                OR NEW.state NOT IN ('missed','skipped','scanning')))
            OR (NEW.state<>'scanning' AND NEW.finished_at IS DISTINCT FROM statement_timestamp()) THEN
            RAISE EXCEPTION 'Trigger run refused.' USING ERRCODE='42501';
        END IF;
    ELSIF (to_jsonb(NEW)-'state'-'claim_id'-'claim_expires_at'-'scan_cursor'-'pages'-'discovered'-'admitted'-'changed'-'rejected'-'updated_at'-'finished_at')
            IS DISTINCT FROM (to_jsonb(OLD)-'state'-'claim_id'-'claim_expires_at'-'scan_cursor'-'pages'-'discovered'-'admitted'-'changed'-'rejected'-'updated_at'-'finished_at')
        OR OLD.state<>'scanning' OR NEW.state NOT IN ('scanning','scanned','fenced') OR NEW.updated_at<>statement_timestamp()
        OR NEW.pages<OLD.pages OR NEW.discovered<OLD.discovered OR NEW.admitted<OLD.admitted OR NEW.changed<OLD.changed OR NEW.rejected<OLD.rejected
        OR (NEW.state<>'scanning' AND NEW.finished_at IS DISTINCT FROM statement_timestamp()) THEN
        RAISE EXCEPTION 'Trigger run history is immutable.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_trigger_run() FROM PUBLIC;
CREATE TRIGGER trigger_run_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_runs FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger_run();
CREATE TRIGGER trigger_run_no_truncate BEFORE TRUNCATE ON braid.trigger_runs FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger_run();
CREATE TRIGGER trigger_run_work_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_run_work FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger_run();
CREATE TRIGGER trigger_run_work_no_truncate BEFORE TRUNCATE ON braid.trigger_run_work FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger_run();

-- The closed configuration shape now admits schedule triggers (with their explicit schedule) and
-- the explicit batch envelope. Queue configurations keep exactly their 0043 form.
CREATE OR REPLACE FUNCTION braid.trigger_definition_valid(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, body bytea) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE d jsonb; p jsonb; r jsonb; s jsonb; n integer; keys integer;
BEGIN
    IF body IS NULL OR octet_length(body) NOT BETWEEN 2 AND 65536 THEN RETURN false; END IF;
    d:=convert_from(body,'UTF8')::jsonb;
    -- A schedule configuration carries one more member: its schedule.
    keys:=21+(d->>'kind' IS NOT DISTINCT FROM 'schedule')::integer;
    IF jsonb_typeof(d)<>'object' OR coalesce(d->>'kind','') NOT IN ('serviceBusQueue','schedule')
        OR (SELECT count(*) FROM jsonb_object_keys(d))<>keys
        OR NOT d?&ARRAY['schema','triggerId','organizationId','projectId',
            'mappingId','environmentId','environmentVersion','targetId','kind','sourceBindingId','sourceConnectorId','sourceConnectorVersion','callerProvider',
            'callerTenantId','callerObjectId','applicationId','envelope','inputMediaType','parameters','routes','policies']
        OR (d->>'kind'='schedule' AND NOT d?'schedule')
        OR d->>'schema' IS DISTINCT FROM 'braid.trigger-configuration.v1' OR (d->>'triggerId')::uuid IS DISTINCT FROM trigger
        OR (d->>'organizationId')::uuid IS DISTINCT FROM org OR (d->>'projectId')::uuid IS DISTINCT FROM project
        OR (d->>'mappingId')::uuid IS DISTINCT FROM mapping OR (d->>'environmentId')::uuid IS DISTINCT FROM environment
        OR (d->>'environmentVersion')::bigint IS DISTINCT FROM target_version OR (d->>'targetId')::uuid IS DISTINCT FROM target
        OR coalesce(d->>'envelope','') NOT IN ('singleOrder','batch') OR d->>'callerProvider' IS DISTINCT FROM 'entra'
        OR (d->>'sourceBindingId')::uuid IS NULL OR (d->>'sourceBindingId')::uuid='00000000-0000-0000-0000-000000000000'
        OR (d->>'callerTenantId')::uuid IS NULL OR (d->>'callerObjectId')::uuid IS NULL
        OR (d->>'applicationId')::uuid IS NULL OR (d->>'applicationId')::uuid='00000000-0000-0000-0000-000000000000'
        OR coalesce(d->>'sourceConnectorId','')!~'^[a-z0-9][a-z0-9.-]{0,62}$' OR (d->>'sourceConnectorVersion')::bigint<1
        OR jsonb_typeof(d->'inputMediaType')<>'string' OR octet_length(d->>'inputMediaType') NOT BETWEEN 1 AND 256
        OR jsonb_typeof(d->'parameters')<>'array' OR jsonb_array_length(d->'parameters')>64
        OR jsonb_typeof(d->'routes')<>'array' OR jsonb_array_length(d->'routes') NOT BETWEEN 1 AND 64
        OR jsonb_typeof(d->'policies') NOT IN ('null','object') THEN RETURN false; END IF;
    FOR r IN SELECT value FROM jsonb_array_elements(d->'parameters') LOOP
        IF jsonb_typeof(r)<>'object' OR (SELECT count(*) FROM jsonb_object_keys(r))<>3 OR NOT r?&ARRAY['name','kind','value']
            OR jsonb_typeof(r->'name')<>'string' OR jsonb_typeof(r->'value')<>'string'
            OR r->>'kind' NOT IN ('string','integer','float','boolean') THEN RETURN false; END IF;
    END LOOP;
    FOR r IN SELECT value FROM jsonb_array_elements(d->'routes') LOOP
        IF jsonb_typeof(r)<>'object' OR (SELECT count(*) FROM jsonb_object_keys(r))<>5
            OR NOT r?&ARRAY['outputName','destinationBindingId','kind','connectorId','connectorVersion']
            OR jsonb_typeof(r->'outputName') NOT IN ('null','string') OR r->>'outputName'=''
            OR r->>'kind' NOT IN ('blob','sftp','https','serviceBusTopic','as2') OR coalesce(r->>'connectorId','')!~'^[a-z0-9][a-z0-9.-]{0,62}$'
            OR (r->>'connectorVersion')::bigint<1 OR (r->>'destinationBindingId')::uuid IS NULL
            OR (r->>'destinationBindingId')::uuid='00000000-0000-0000-0000-000000000000' THEN RETURN false; END IF;
    END LOOP;
    SELECT count(*) INTO n FROM (SELECT DISTINCT value->'outputName',value->'destinationBindingId' FROM jsonb_array_elements(d->'routes')) x;
    IF n<>jsonb_array_length(d->'routes') THEN RETURN false; END IF;
    p:=d->'policies';
    IF jsonb_typeof(p)='object' AND ((SELECT count(*) FROM jsonb_object_keys(p))<>9 OR NOT p?&ARRAY['totalSends','retryDelaysMs','attemptWindowMs',
            'maximumElapsedMs','lockRenewalMs','maximumMessageBytes','payloadRetentionSeconds','maximumPendingWork','maximumPendingBytes']
        OR (p->>'totalSends')::integer NOT BETWEEN 1 AND 32 OR jsonb_typeof(p->'retryDelaysMs')<>'array'
        OR jsonb_array_length(p->'retryDelaysMs')<>(p->>'totalSends')::integer-1
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(p->'retryDelaysMs') x WHERE jsonb_typeof(x)<>'number' OR (x#>>'{}')::bigint<1)
        OR (p->>'attemptWindowMs')::bigint<1 OR (p->>'maximumElapsedMs')::bigint<(p->>'attemptWindowMs')::bigint
        OR (p->>'lockRenewalMs')::bigint NOT BETWEEN 1 AND 90000 OR (p->>'maximumMessageBytes')::bigint NOT BETWEEN 1 AND 4194304
        OR (p->>'payloadRetentionSeconds')::bigint*1000<(p->>'maximumElapsedMs')::bigint
        OR (p->>'maximumPendingWork')::bigint NOT BETWEEN 1 AND 1000000
        OR (p->>'maximumPendingBytes')::bigint<(p->>'maximumMessageBytes')::bigint) THEN RETURN false; END IF;
    IF d->>'kind'='schedule' THEN
        s:=d->'schedule';
        IF jsonb_typeof(s)<>'object' OR (SELECT count(*) FROM jsonb_object_keys(s))<>11 OR NOT s?&ARRAY['grammar','cron','timeZone','timeZoneDataVersion',
                'effectiveFrom','gap','fold','overlap','catchUp','lookbackSeconds','maximumCatchUp']
            OR s->>'grammar' IS DISTINCT FROM 'braid.cron5.v1' OR jsonb_typeof(s->'cron')<>'string' OR s->>'cron'!~'^[0-9*,/-]+( [0-9*,/-]+){4}$'
            OR octet_length(s->>'cron')>256 OR coalesce(s->>'timeZone','')!~'^(UTC|[A-Z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){1,2})$'
            OR coalesce(s->>'timeZoneDataVersion','')!~'^[0-9]{4}[a-z]$'
            OR coalesce(s->>'effectiveFrom','')!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$' OR NOT isfinite((s->>'effectiveFrom')::timestamptz)
            OR s->>'gap' IS DISTINCT FROM 'skip' OR coalesce(s->>'fold','') NOT IN ('first','second','both') OR s->>'overlap' IS DISTINCT FROM 'skipIfRunning'
            OR coalesce(s->>'catchUp','') NOT IN ('none','bounded') OR jsonb_typeof(s->'lookbackSeconds')<>'number' OR (s->>'lookbackSeconds')::bigint NOT BETWEEN 1 AND 604800
            OR (s->>'catchUp'='none' AND jsonb_typeof(s->'maximumCatchUp')<>'null')
            OR (s->>'catchUp'='bounded' AND (jsonb_typeof(s->'maximumCatchUp')<>'number' OR (s->>'maximumCatchUp')::integer NOT BETWEEN 1 AND 64)) THEN
            RETURN false;
        END IF;
    END IF;
    RETURN true;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR character_not_in_repertoire OR untranslatable_character
    OR invalid_parameter_value OR datatype_mismatch OR invalid_datetime_format OR datetime_field_overflow THEN RETURN false;
END;
$body$;

-- The schedule identity: a schedule revision (including a reviewed time-zone data change) starts a new epoch.
CREATE FUNCTION braid.trigger_schedule_epoch(d jsonb) RETURNS text
LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT encode(sha256(convert_to((d->'schedule')::text,'UTF8')),'hex')
$body$;
REVOKE ALL ON FUNCTION braid.trigger_schedule_epoch(jsonb) FROM PUBLIC;

-- Unchanged from 0043 except that a configuration edit may not change the envelope of a source binding.
CREATE OR REPLACE FUNCTION braid.operate_trigger(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, operation uuid, action text, expected bigint, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; t braid.local_release_targets%ROWTYPE;
    c braid.trigger_controls%ROWTYPE; old braid.trigger_operations%ROWTYPE; d jsonb; digest text; result integer; registered boolean;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_release_writer' OR actor IS NULL OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL
        OR trigger IS NULL OR trigger='00000000-0000-0000-0000-000000000000' OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'
        OR expected IS NULL OR expected<0 OR action IS NULL OR action NOT IN ('configure','enable','disable','retire')
        OR (action='configure')<>(body IS NOT NULL) OR (action<>'configure' AND expected=0) THEN RETURN 4; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN RETURN 4; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR SHARE;
    IF NOT FOUND OR ROW(t.project_id,t.mapping_id,t.environment_id,t.environment_version) IS DISTINCT FROM ROW(project,mapping,environment,target_version) THEN RETURN 4; END IF;
    -- Operation IDs are organization-wide; fence the identity before any effect.
    PERFORM pg_advisory_xact_lock(hashtextextended('braid.trigger.operation:'||org::text||':'||operation::text,0));
    digest:=CASE WHEN body IS NULL THEN NULL ELSE encode(sha256(body),'hex') END;
    SELECT * INTO old FROM ONLY braid.trigger_operations WHERE organization_id=org AND operation_id=operation;
    IF FOUND THEN
        IF ROW(old.target_id,old.trigger_id,old.actor_id,old.action,old.expected_generation,old.definition_digest)
            IS DISTINCT FROM ROW(target,trigger,actor,action,expected,digest) THEN RETURN 5; END IF;
        RETURN old.outcome;
    END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls WHERE organization_id=org AND trigger_id=trigger FOR UPDATE;
    registered:=FOUND;
    IF registered AND c.target_id<>target THEN RETURN 4; END IF;
    IF (NOT registered AND expected<>0) OR (registered AND c.generation<>expected) THEN result:=2;
    ELSIF registered AND c.retired THEN result:=4;
    ELSIF action='configure' THEN
        IF NOT braid.trigger_definition_valid(org,project,mapping,environment,target_version,target,trigger,body) THEN RETURN 4; END IF;
        d:=convert_from(body,'UTF8')::jsonb;
        IF registered AND ((d->>'sourceBindingId')::uuid<>c.source_binding_id OR d->>'sourceConnectorId'<>c.source_connector_id OR d->>'kind'<>c.kind
            OR d->>'envelope' IS DISTINCT FROM (SELECT convert_from(x.definition,'UTF8')::jsonb->>'envelope' FROM ONLY braid.trigger_configurations x
                WHERE x.organization_id=org AND x.trigger_id=trigger AND x.version=c.current_version)) THEN result:=4;
        ELSIF registered AND c.enabled AND jsonb_typeof(d->'policies')<>'object' THEN result:=3;
        ELSIF NOT registered AND EXISTS(SELECT 1 FROM ONLY braid.trigger_controls x WHERE x.organization_id=org
            AND (x.source_binding_id=(d->>'sourceBindingId')::uuid OR (x.source_connector_id=d->>'sourceConnectorId' AND NOT x.retired))) THEN result:=4;
        ELSIF registered THEN
            INSERT INTO braid.trigger_configurations(organization_id,trigger_id,version,definition,definition_digest,actor_id)
                VALUES(org,trigger,c.current_version+1,body,digest,actor);
            UPDATE ONLY braid.trigger_controls SET current_version=current_version+1,generation=generation+1,updated_at=statement_timestamp()
                WHERE organization_id=org AND trigger_id=trigger;
            result:=0;
        ELSE
            INSERT INTO braid.trigger_controls(organization_id,project_id,mapping_id,target_id,trigger_id,kind,source_binding_id,source_connector_id,
                current_version,enabled,retired,generation,admission_epoch,updated_at)
                VALUES(org,project,mapping,target,trigger,d->>'kind',(d->>'sourceBindingId')::uuid,d->>'sourceConnectorId',1,false,false,1,1,statement_timestamp());
            INSERT INTO braid.trigger_configurations(organization_id,trigger_id,version,definition,definition_digest,actor_id) VALUES(org,trigger,1,body,digest,actor);
            result:=0;
        END IF;
    ELSIF NOT registered THEN result:=4;
    ELSIF action='enable' THEN
        SELECT convert_from(x.definition,'UTF8')::jsonb INTO d FROM ONLY braid.trigger_configurations x
            WHERE x.organization_id=org AND x.trigger_id=trigger AND x.version=c.current_version;
        IF c.enabled THEN result:=1;
        -- Enabling never creates a grant or fills a missing operating choice.
        ELSIF jsonb_typeof(d->'policies')<>'object' OR NOT t.enabled
            OR braid.machine_invocation_grant(org,target,d->>'callerProvider',(d->>'callerTenantId')::uuid,(d->>'callerObjectId')::uuid) IS NULL THEN result:=3;
        ELSE
            UPDATE ONLY braid.trigger_controls SET enabled=true,generation=generation+1,updated_at=statement_timestamp() WHERE organization_id=org AND trigger_id=trigger;
            result:=0;
        END IF;
    ELSIF action='disable' THEN
        IF NOT c.enabled THEN result:=1;
        ELSE
            UPDATE ONLY braid.trigger_controls SET enabled=false,generation=generation+1,admission_epoch=admission_epoch+1,updated_at=statement_timestamp()
                WHERE organization_id=org AND trigger_id=trigger;
            result:=0;
        END IF;
    ELSE
        UPDATE ONLY braid.trigger_controls SET enabled=false,retired=true,generation=generation+1,admission_epoch=admission_epoch+1,updated_at=statement_timestamp()
            WHERE organization_id=org AND trigger_id=trigger;
        result:=0;
    END IF;
    INSERT INTO braid.trigger_operations(organization_id,operation_id,target_id,trigger_id,actor_id,action,expected_generation,definition_digest,outcome)
        VALUES(org,operation,target,trigger,actor,action,expected,digest,result);
    RETURN result;
END;
$body$;

-- Unchanged from 0043 except that it admits only single-order queue messages; schedules and
-- batches use their own run-scoped admission below.
CREATE OR REPLACE FUNCTION braid.admit_trigger_work(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, configuration bigint, issuer text, tenant uuid, principal uuid, application uuid, source text, order_key text, request_sha text, body bytea) RETURNS TABLE(status integer, work uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE t braid.local_release_targets%ROWTYPE; c braid.trigger_controls%ROWTYPE; w braid.trigger_work%ROWTYPE; g uuid; d jsonb; raw text;
    r record; generation bigint; pending bigint; pending_bytes bigint; fresh uuid:=pg_catalog.gen_random_uuid(); receipt uuid:=pg_catalog.gen_random_uuid();
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL OR trigger IS NULL
        OR configuration IS NULL OR issuer IS DISTINCT FROM 'entra' OR tenant IS NULL OR principal IS NULL
        OR application IS NULL OR application='00000000-0000-0000-0000-000000000000'
        OR source IS NULL OR char_length(source) NOT BETWEEN 1 AND 128 OR order_key IS NULL OR octet_length(order_key)>256
        OR request_sha IS NULL OR request_sha!~'^[0-9a-f]{64}$' OR body IS NULL OR octet_length(body)>4194304 THEN
        RETURN QUERY SELECT 3,NULL::uuid; RETURN;
    END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls x WHERE x.organization_id=org AND x.trigger_id=trigger;
    IF NOT FOUND OR c.target_id<>target OR c.project_id<>project OR c.mapping_id<>mapping OR c.kind<>'serviceBusQueue' THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
    raw:=encode(sha256(body),'hex');
    -- A redelivery reuses the original work, its stored inputs, routes and pin, whatever the current state.
    SELECT * INTO w FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer AND x.tenant_id=tenant
        AND x.object_id=principal AND x.source_binding_id=c.source_binding_id AND x.source_identity=source AND x.order_key=admit_trigger_work.order_key;
    IF FOUND THEN
        RETURN QUERY SELECT CASE WHEN w.raw_source_digest=raw AND w.application_id=application THEN 1 ELSE 2 END,
            CASE WHEN w.raw_source_digest=raw AND w.application_id=application THEN w.work_id END;
        RETURN;
    END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets x WHERE x.organization_id=org AND x.target_id=target FOR SHARE;
    IF NOT FOUND OR ROW(t.project_id,t.mapping_id,t.environment_id,t.environment_version) IS DISTINCT FROM ROW(project,mapping,environment,target_version)
        OR NOT t.enabled THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
    SELECT x.grant_id INTO g FROM ONLY braid.invocation_grants x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
        AND x.tenant_id=tenant AND x.object_id=principal AND x.revoked_at IS NULL FOR SHARE;
    IF g IS NULL THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
    -- Serializes admissions of one trigger so its explicit backlog caps hold across replicas.
    SELECT * INTO c FROM ONLY braid.trigger_controls x WHERE x.organization_id=org AND x.trigger_id=trigger FOR UPDATE;
    IF NOT c.enabled OR c.retired OR c.current_version<>configuration THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
    SELECT convert_from(x.definition,'UTF8')::jsonb INTO d FROM ONLY braid.trigger_configurations x
        WHERE x.organization_id=org AND x.trigger_id=trigger AND x.version=configuration;
    IF d->>'callerProvider'<>issuer OR (d->>'callerTenantId')::uuid<>tenant OR (d->>'callerObjectId')::uuid<>principal
        OR (d->>'applicationId')::uuid<>application OR jsonb_typeof(d->'policies')<>'object'
        OR d->>'envelope'<>'singleOrder' OR order_key<>'' THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
    IF octet_length(body)>(d->'policies'->>'maximumMessageBytes')::bigint THEN RETURN QUERY SELECT 5,NULL::uuid; RETURN; END IF;
    SELECT count(*),coalesce(sum(i.byte_count),0) INTO pending,pending_bytes FROM ONLY braid.trigger_work x
        JOIN ONLY braid.trigger_receipts i ON i.organization_id=x.organization_id AND i.work_id=x.work_id AND i.direction='input'
        WHERE x.organization_id=org AND x.trigger_id=trigger AND x.phase='admitted';
    pending_bytes:=pending_bytes+coalesce((SELECT sum(o.byte_count) FROM ONLY braid.trigger_deliveries y
        JOIN ONLY braid.trigger_work x ON x.organization_id=y.organization_id AND x.work_id=y.work_id
        JOIN ONLY braid.trigger_receipts o ON o.organization_id=y.organization_id AND o.receipt_id=y.receipt_id
        WHERE y.organization_id=org AND x.trigger_id=trigger AND y.phase IN ('pending','sending','unknown')),0);
    IF pending>=(d->'policies'->>'maximumPendingWork')::bigint OR pending_bytes+octet_length(body)>(d->'policies'->>'maximumPendingBytes')::bigint THEN
        RETURN QUERY SELECT 5,NULL::uuid; RETURN;
    END IF;
    SELECT rl.release_id,rl.qualification_id,rl.actor_id,q.build_id,b.intent_digest,convert_from(rl.intent_body,'UTF8')::jsonb->>'artifactDigest' AS artifact,
        (convert_from(b.intent_body,'UTF8')::jsonb->>'revisionId')::uuid AS revision,convert_from(rl.intent_body,'UTF8')::jsonb->>'runtimeImage' AS image INTO r
        FROM ONLY braid.local_releases rl JOIN ONLY braid.build_qualifications q ON q.organization_id=rl.organization_id AND q.qualification_id=rl.qualification_id AND q.actor_id=rl.actor_id
        JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id AND b.actor_id=q.actor_id
        WHERE rl.organization_id=org AND rl.target_id=target AND rl.release_id=t.active_release_id AND rl.phase='active';
    IF NOT FOUND THEN RETURN QUERY SELECT 4,NULL::uuid; RETURN; END IF;
    SELECT max(e.version) INTO generation FROM ONLY braid.release_target_events e WHERE e.organization_id=org AND e.target_id=target;
    INSERT INTO braid.trigger_work(organization_id,work_id,project_id,mapping_id,target_id,trigger_id,configuration_version,source_binding_id,source_identity,
        order_key,raw_source_digest,provider,tenant_id,object_id,application_id,request_id,request_digest,release_id,qualification_id,build_id,
        build_intent_digest,artifact_digest,revision_id,runtime_image,publisher_id,target_generation,grant_id,admission_epoch,phase,created_at)
        VALUES(org,fresh,project,mapping,target,trigger,configuration,c.source_binding_id,source,admit_trigger_work.order_key,raw,issuer,tenant,principal,
            application,pg_catalog.gen_random_uuid(),request_sha,r.release_id,r.qualification_id,r.build_id,r.intent_digest,r.artifact,r.revision,r.image,
            r.actor_id,generation,g,c.admission_epoch,'admitted',statement_timestamp());
    INSERT INTO braid.trigger_receipts(organization_id,receipt_id,work_id,direction,output_name,media_type,byte_count,digest)
        VALUES(org,receipt,fresh,'input','',d->>'inputMediaType',octet_length(body),raw);
    INSERT INTO braid.trigger_payloads(organization_id,receipt_id,body) VALUES(org,receipt,body);
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind) VALUES(org,trigger,fresh,'admitted');
    RETURN QUERY SELECT 0,fresh;
END;
$body$;

-- Shared run and batch admission authority in the common lock order (target, grant, control),
-- after the caller holds the organization lifecycle gate. Status 0 returns a work template with
-- the complete immutable pin; 3 refused (disabled, retired, stale configuration, caller or grant);
-- 4 no current release.
CREATE FUNCTION braid.trigger_admission(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, configuration bigint, issuer text, tenant uuid, principal uuid, application uuid) RETURNS TABLE(status integer, template braid.trigger_work, definition jsonb)
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE t braid.local_release_targets%ROWTYPE; c braid.trigger_controls%ROWTYPE; w braid.trigger_work%ROWTYPE; g uuid; d jsonb; r record;
BEGIN
    SELECT * INTO t FROM ONLY braid.local_release_targets x WHERE x.organization_id=org AND x.target_id=target FOR SHARE;
    IF NOT FOUND OR ROW(t.project_id,t.mapping_id,t.environment_id,t.environment_version) IS DISTINCT FROM ROW(project,mapping,environment,target_version)
        OR NOT t.enabled THEN RETURN QUERY SELECT 3,NULL::braid.trigger_work,NULL::jsonb; RETURN; END IF;
    SELECT x.grant_id INTO g FROM ONLY braid.invocation_grants x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
        AND x.tenant_id=tenant AND x.object_id=principal AND x.revoked_at IS NULL FOR SHARE;
    IF g IS NULL THEN RETURN QUERY SELECT 3,NULL::braid.trigger_work,NULL::jsonb; RETURN; END IF;
    -- Serializes admissions of one trigger so its explicit backlog caps hold across replicas.
    SELECT * INTO c FROM ONLY braid.trigger_controls x WHERE x.organization_id=org AND x.trigger_id=trigger FOR UPDATE;
    IF NOT FOUND OR c.target_id<>target OR NOT c.enabled OR c.retired OR c.current_version<>configuration THEN
        RETURN QUERY SELECT 3,NULL::braid.trigger_work,NULL::jsonb; RETURN;
    END IF;
    SELECT convert_from(x.definition,'UTF8')::jsonb INTO d FROM ONLY braid.trigger_configurations x
        WHERE x.organization_id=org AND x.trigger_id=trigger AND x.version=configuration;
    IF d->>'callerProvider'<>issuer OR (d->>'callerTenantId')::uuid<>tenant OR (d->>'callerObjectId')::uuid<>principal
        OR (d->>'applicationId')::uuid<>application OR jsonb_typeof(d->'policies')<>'object' THEN
        RETURN QUERY SELECT 3,NULL::braid.trigger_work,NULL::jsonb; RETURN;
    END IF;
    SELECT rl.release_id,rl.qualification_id,rl.actor_id,q.build_id,b.intent_digest,convert_from(rl.intent_body,'UTF8')::jsonb->>'artifactDigest' AS artifact,
        (convert_from(b.intent_body,'UTF8')::jsonb->>'revisionId')::uuid AS revision,convert_from(rl.intent_body,'UTF8')::jsonb->>'runtimeImage' AS image INTO r
        FROM ONLY braid.local_releases rl JOIN ONLY braid.build_qualifications q ON q.organization_id=rl.organization_id AND q.qualification_id=rl.qualification_id AND q.actor_id=rl.actor_id
        JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id AND b.actor_id=q.actor_id
        WHERE rl.organization_id=org AND rl.target_id=target AND rl.release_id=t.active_release_id AND rl.phase='active';
    IF NOT FOUND THEN RETURN QUERY SELECT 4,NULL::braid.trigger_work,NULL::jsonb; RETURN; END IF;
    w.organization_id:=org; w.project_id:=project; w.mapping_id:=mapping; w.target_id:=target; w.trigger_id:=trigger; w.configuration_version:=configuration;
    w.source_binding_id:=c.source_binding_id; w.provider:=issuer; w.tenant_id:=tenant; w.object_id:=principal; w.application_id:=application;
    w.release_id:=r.release_id; w.qualification_id:=r.qualification_id; w.build_id:=r.build_id; w.build_intent_digest:=r.intent_digest;
    w.artifact_digest:=r.artifact; w.revision_id:=r.revision; w.runtime_image:=r.image; w.publisher_id:=r.actor_id; w.grant_id:=g;
    w.target_generation:=(SELECT max(e.version) FROM ONLY braid.release_target_events e WHERE e.organization_id=org AND e.target_id=target);
    w.admission_epoch:=c.admission_epoch; w.phase:='admitted'; w.created_at:=statement_timestamp();
    RETURN QUERY SELECT 0,w,d;
END;
$body$;
REVOKE ALL ON FUNCTION braid.trigger_admission(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid) FROM PUBLIC;

-- Admitted orders not yet finished plus output bytes awaiting or uncertain of delivery.
CREATE FUNCTION braid.trigger_backlog(org uuid, trigger uuid) RETURNS TABLE(pending bigint, pending_bytes bigint)
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT (SELECT count(*) FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.trigger_id=trigger AND x.phase='admitted'),
        coalesce((SELECT sum(i.byte_count) FROM ONLY braid.trigger_work x
            JOIN ONLY braid.trigger_receipts i ON i.organization_id=x.organization_id AND i.work_id=x.work_id AND i.direction='input'
            WHERE x.organization_id=org AND x.trigger_id=trigger AND x.phase='admitted'),0)
        +coalesce((SELECT sum(o.byte_count) FROM ONLY braid.trigger_deliveries y
            JOIN ONLY braid.trigger_work x ON x.organization_id=y.organization_id AND x.work_id=y.work_id
            JOIN ONLY braid.trigger_receipts o ON o.organization_id=y.organization_id AND o.receipt_id=y.receipt_id
            WHERE y.organization_id=org AND x.trigger_id=trigger AND y.phase IN ('pending','sending','unknown')),0)
$body$;
REVOKE ALL ON FUNCTION braid.trigger_backlog(uuid,uuid) FROM PUBLIC;

-- One admitted order with its exact input receipt and bytes, linked to the run that found it.
CREATE FUNCTION braid.trigger_insert_work(w braid.trigger_work, media text, body bytea, run uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE receipt uuid:=pg_catalog.gen_random_uuid();
BEGIN
    INSERT INTO braid.trigger_work SELECT (w).*;
    INSERT INTO braid.trigger_receipts(organization_id,receipt_id,work_id,direction,output_name,media_type,byte_count,digest)
        VALUES(w.organization_id,receipt,w.work_id,'input','',media,octet_length(body),w.raw_source_digest);
    INSERT INTO braid.trigger_payloads(organization_id,receipt_id,body) VALUES(w.organization_id,receipt,body);
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind) VALUES(w.organization_id,w.trigger_id,w.work_id,'admitted');
    INSERT INTO braid.trigger_run_work(organization_id,run_id,work_id) VALUES(w.organization_id,run,w.work_id);
END;
$body$;
REVOKE ALL ON FUNCTION braid.trigger_insert_work(braid.trigger_work,text,bytea,uuid) FROM PUBLIC;

-- The schedule view of the worker: the current version, its schedule epoch and the latest recorded occurrence of that epoch.
CREATE FUNCTION braid.read_trigger_schedule(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid) RETURNS TABLE(version bigint, epoch text, latest timestamp with time zone)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT c.current_version,e.epoch,(SELECT max(r.scheduled_at) FROM ONLY braid.trigger_runs r
        WHERE r.organization_id=c.organization_id AND r.trigger_id=c.trigger_id AND r.schedule_epoch=e.epoch)
    FROM ONLY braid.trigger_controls c JOIN ONLY braid.trigger_configurations x ON x.organization_id=c.organization_id AND x.trigger_id=c.trigger_id AND x.version=c.current_version
    JOIN ONLY braid.local_release_targets t ON t.organization_id=c.organization_id AND t.target_id=c.target_id
    CROSS JOIN LATERAL (SELECT braid.trigger_schedule_epoch(convert_from(x.definition,'UTF8')::jsonb) AS epoch) e
    WHERE session_user='braid_trigger_worker' AND c.organization_id=org AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND c.trigger_id=trigger AND c.target_id=target AND c.kind='schedule' AND NOT c.retired AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_schedule(uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_schedule(uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_trigger_worker;

-- Records missed occurrences and claims one due occurrence. Status: 0 a new run is claimed for
-- run_at; 1 recorded only (run_at absent, already finished or skipped behind an unfinished run);
-- 2 run_at is still scanning elsewhere; 3 refused. The same occurrence is unique across replicas.
CREATE FUNCTION braid.record_trigger_occurrences(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, configuration bigint, issuer text, tenant uuid, principal uuid, application uuid, missed timestamp with time zone[], missed_offsets integer[], run_at timestamp with time zone, run_offset integer) RETURNS TABLE(status integer, run uuid, claim uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE c braid.trigger_controls%ROWTYPE; d jsonb; epoch text; x braid.trigger_runs%ROWTYPE; k integer;
    fresh uuid:=pg_catalog.gen_random_uuid(); key uuid:=pg_catalog.gen_random_uuid();
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL OR trigger IS NULL
        OR configuration IS NULL OR issuer IS DISTINCT FROM 'entra' OR tenant IS NULL OR principal IS NULL OR application IS NULL
        OR missed IS NULL OR missed_offsets IS NULL OR cardinality(missed)<>cardinality(missed_offsets) OR cardinality(missed)>1024
        OR array_position(missed,NULL) IS NOT NULL OR array_position(missed_offsets,NULL) IS NOT NULL
        OR EXISTS(SELECT 1 FROM unnest(missed_offsets) o WHERE o NOT BETWEEN -86400 AND 86400)
        OR (run_at IS NULL)<>(run_offset IS NULL) OR run_offset NOT BETWEEN -86400 AND 86400 OR NOT isfinite(run_at)
        OR EXISTS(SELECT 1 FROM unnest(missed) m WHERE NOT isfinite(m) OR m>=run_at) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid; RETURN;
    END IF;
    -- Serializes occurrence recording of one trigger.
    SELECT * INTO c FROM ONLY braid.trigger_controls y WHERE y.organization_id=org AND y.trigger_id=trigger FOR UPDATE;
    IF NOT FOUND OR c.target_id<>target OR c.project_id<>project OR c.mapping_id<>mapping OR c.kind<>'schedule' OR NOT c.enabled OR c.retired
        OR c.current_version<>configuration OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target
            AND t.project_id=project AND t.mapping_id=mapping AND t.environment_id=environment AND t.environment_version=target_version) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid; RETURN;
    END IF;
    SELECT convert_from(y.definition,'UTF8')::jsonb INTO d FROM ONLY braid.trigger_configurations y
        WHERE y.organization_id=org AND y.trigger_id=trigger AND y.version=configuration;
    IF d->>'callerProvider'<>issuer OR (d->>'callerTenantId')::uuid<>tenant OR (d->>'callerObjectId')::uuid<>principal
        OR (d->>'applicationId')::uuid<>application OR jsonb_typeof(d->'policies')<>'object' THEN RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid; RETURN; END IF;
    epoch:=braid.trigger_schedule_epoch(d);
    FOR k IN 1..cardinality(missed) LOOP
        INSERT INTO braid.trigger_runs(organization_id,run_id,project_id,mapping_id,target_id,trigger_id,configuration_version,kind,schedule_epoch,scheduled_at,
            utc_offset_seconds,source_binding_id,provider,tenant_id,object_id,application_id,state,pages,discovered,admitted,changed,rejected,created_at,updated_at,finished_at)
            VALUES(org,pg_catalog.gen_random_uuid(),project,mapping,target,trigger,configuration,'schedule',epoch,missed[k],missed_offsets[k],c.source_binding_id,
                issuer,tenant,principal,application,'missed',0,0,0,0,0,statement_timestamp(),statement_timestamp(),statement_timestamp())
            ON CONFLICT DO NOTHING;
    END LOOP;
    IF run_at IS NULL THEN RETURN QUERY SELECT 1,NULL::uuid,NULL::uuid; RETURN; END IF;
    SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.trigger_id=trigger AND y.schedule_epoch=epoch AND y.scheduled_at=run_at;
    IF FOUND THEN RETURN QUERY SELECT CASE WHEN x.state='scanning' THEN 2 ELSE 1 END,x.run_id,NULL::uuid; RETURN; END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.trigger_id=trigger AND y.state='scanning') THEN
        INSERT INTO braid.trigger_runs(organization_id,run_id,project_id,mapping_id,target_id,trigger_id,configuration_version,kind,schedule_epoch,scheduled_at,
            utc_offset_seconds,source_binding_id,provider,tenant_id,object_id,application_id,state,pages,discovered,admitted,changed,rejected,created_at,updated_at,finished_at)
            VALUES(org,fresh,project,mapping,target,trigger,configuration,'schedule',epoch,run_at,run_offset,c.source_binding_id,
                issuer,tenant,principal,application,'skipped',0,0,0,0,0,statement_timestamp(),statement_timestamp(),statement_timestamp());
        RETURN QUERY SELECT 1,fresh,NULL::uuid; RETURN;
    END IF;
    INSERT INTO braid.trigger_runs(organization_id,run_id,project_id,mapping_id,target_id,trigger_id,configuration_version,kind,schedule_epoch,scheduled_at,
        utc_offset_seconds,source_binding_id,provider,tenant_id,object_id,application_id,state,claim_id,claim_expires_at,pages,discovered,admitted,changed,rejected,
        created_at,updated_at)
        VALUES(org,fresh,project,mapping,target,trigger,configuration,'schedule',epoch,run_at,run_offset,c.source_binding_id,
            issuer,tenant,principal,application,'scanning',key,clock_timestamp()+interval '120 seconds',0,0,0,0,0,statement_timestamp(),statement_timestamp());
    RETURN QUERY SELECT 0,fresh,key;
END;
$body$;
REVOKE ALL ON FUNCTION braid.record_trigger_occurrences(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,timestamp with time zone[],integer[],timestamp with time zone,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.record_trigger_occurrences(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,timestamp with time zone[],integer[],timestamp with time zone,integer) TO braid_trigger_worker;

-- Recovers the unfinished run of a trigger: lease expiry permits recovery of the same run, never
-- another overlapping one. Status: 0 reclaimed (with its persisted cursor), 1 none, 2 still
-- leased elsewhere, 3 refused, 4 fenced because its configuration or schedule was revised.
CREATE FUNCTION braid.claim_trigger_run(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, issuer text, tenant uuid, principal uuid, application uuid) RETURNS TABLE(status integer, run uuid, claim uuid, run_cursor text, scheduled timestamp with time zone, configuration bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE c braid.trigger_controls%ROWTYPE; x braid.trigger_runs%ROWTYPE; key uuid:=pg_catalog.gen_random_uuid();
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR target IS NULL OR trigger IS NULL OR issuer IS NULL OR tenant IS NULL OR principal IS NULL OR application IS NULL
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=target
            AND t.project_id=project AND t.mapping_id=mapping AND t.environment_id=environment AND t.environment_version=target_version) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid,NULL::text,NULL::timestamptz,NULL::bigint; RETURN;
    END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls y WHERE y.organization_id=org AND y.trigger_id=trigger FOR UPDATE;
    IF NOT FOUND OR c.target_id<>target OR c.kind<>'schedule' OR NOT c.enabled OR c.retired THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid,NULL::text,NULL::timestamptz,NULL::bigint; RETURN;
    END IF;
    SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.trigger_id=trigger AND y.state='scanning' FOR UPDATE;
    IF NOT FOUND THEN RETURN QUERY SELECT 1,NULL::uuid,NULL::uuid,NULL::text,NULL::timestamptz,NULL::bigint; RETURN; END IF;
    IF x.configuration_version<>c.current_version THEN
        UPDATE ONLY braid.trigger_runs SET state='fenced',claim_id=NULL,claim_expires_at=NULL,updated_at=statement_timestamp(),finished_at=statement_timestamp()
            WHERE organization_id=org AND run_id=x.run_id;
        RETURN QUERY SELECT 4,x.run_id,NULL::uuid,NULL::text,x.scheduled_at,x.configuration_version; RETURN;
    END IF;
    IF x.claim_expires_at>clock_timestamp() THEN RETURN QUERY SELECT 2,x.run_id,NULL::uuid,NULL::text,x.scheduled_at,x.configuration_version; RETURN; END IF;
    IF ROW(x.provider,x.tenant_id,x.object_id,x.application_id) IS DISTINCT FROM ROW(issuer,tenant,principal,application) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid,NULL::text,NULL::timestamptz,NULL::bigint; RETURN;
    END IF;
    UPDATE ONLY braid.trigger_runs SET claim_id=key,claim_expires_at=clock_timestamp()+interval '120 seconds',updated_at=statement_timestamp()
        WHERE organization_id=org AND run_id=x.run_id;
    RETURN QUERY SELECT 0,x.run_id,key,x.scan_cursor,x.scheduled_at,x.configuration_version;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_trigger_run(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.claim_trigger_run(uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,uuid,uuid,uuid) TO braid_trigger_worker;

-- The oldest admitted orders of a trigger that have no final outcome yet, so an order admitted
-- before a crash or left for retry is executed again under its original pin, never rediscovered.
CREATE FUNCTION braid.read_trigger_pending_work(org uuid, target uuid, trigger uuid, issuer text, tenant uuid, principal uuid, maximum integer) RETURNS TABLE(work uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT w.work_id FROM ONLY braid.trigger_work w
    WHERE session_user='braid_trigger_worker' AND w.organization_id=org AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND w.target_id=target AND w.trigger_id=trigger AND w.provider=issuer AND w.tenant_id=tenant AND w.object_id=principal AND w.phase='admitted'
    ORDER BY w.created_at,w.work_id LIMIT least(greatest(maximum,0),1024)
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_pending_work(uuid,uuid,uuid,text,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_pending_work(uuid,uuid,uuid,text,uuid,uuid,integer) TO braid_trigger_worker;

-- Which of these source identities this trigger has already admitted, so a repeated pass does not reread them.
CREATE FUNCTION braid.read_trigger_sources(org uuid, target uuid, trigger uuid, issuer text, tenant uuid, principal uuid, sources text[]) RETURNS TABLE(source text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT DISTINCT w.source_identity FROM ONLY braid.trigger_controls c JOIN ONLY braid.trigger_work w ON w.organization_id=c.organization_id
        AND w.target_id=c.target_id AND w.source_binding_id=c.source_binding_id
    WHERE session_user='braid_trigger_worker' AND c.organization_id=org AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND c.trigger_id=trigger AND c.target_id=target AND w.provider=issuer AND w.tenant_id=tenant AND w.object_id=principal
        AND cardinality(sources)<=1024 AND w.source_identity=ANY(sources)
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_sources(uuid,uuid,uuid,text,uuid,uuid,text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_sources(uuid,uuid,uuid,text,uuid,uuid,text[]) TO braid_trigger_worker;

-- Persists discovered orders and the next cursor in one transaction under the live claim of the run.
-- A null cursor_after on an incomplete run keeps the cursor (one source page may be admitted in
-- several bounded parts); the pass completes only with complete=true. Status: 0 recorded (per item 0 admitted, 1 existing identical order, 2 identity conflict);
-- 1 the claim or cursor is stale (nothing written); 3 refused; 4 no current release;
-- 5 an order or the backlog exceeds its explicit cap (nothing written); 6 the run was fenced.
CREATE FUNCTION braid.record_trigger_run_page(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, run uuid, claim uuid, cursor_before text, cursor_after text, complete boolean, discovered integer, changed integer, rejected integer, issuer text, tenant uuid, principal uuid, application uuid, sources text[], keys text[], request_shas text[], bodies bytea[]) RETURNS TABLE(status integer, works uuid[], outcomes integer[])
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE x braid.trigger_runs%ROWTYPE; c braid.trigger_controls%ROWTYPE; a record; w braid.trigger_work%ROWTYPE; e braid.trigger_work%ROWTYPE; b record;
    k integer; n integer; fresh_count bigint:=0; fresh_bytes bigint:=0; admitted_count integer:=0; result_works uuid[]:='{}'; result_outcomes integer[]:='{}';
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    n:=coalesce(cardinality(sources),0);
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR run IS NULL OR claim IS NULL OR complete IS NULL OR discovered IS NULL OR changed IS NULL OR rejected IS NULL
        OR discovered<0 OR changed<0 OR rejected<0 OR (complete AND cursor_after IS NOT NULL) OR octet_length(cursor_after) NOT BETWEEN 1 AND 4096
        OR issuer IS NULL OR tenant IS NULL OR principal IS NULL OR application IS NULL
        OR sources IS NULL OR keys IS NULL OR request_shas IS NULL OR bodies IS NULL OR n>1024
        OR cardinality(keys)<>n OR cardinality(request_shas)<>n OR cardinality(bodies)<>n
        OR array_position(sources,NULL) IS NOT NULL OR array_position(keys,NULL) IS NOT NULL OR array_position(request_shas,NULL) IS NOT NULL
        OR array_position(bodies,NULL) IS NOT NULL
        OR EXISTS(SELECT 1 FROM unnest(sources,keys,request_shas,bodies) i(s,o,r,y) WHERE char_length(i.s) NOT BETWEEN 1 AND 128 OR octet_length(i.o)>256
            OR i.r!~'^[0-9a-f]{64}$' OR octet_length(i.y)>4194304) THEN
        RETURN QUERY SELECT 3,NULL::uuid[],NULL::integer[]; RETURN;
    END IF;
    SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.run_id=run;
    IF NOT FOUND OR x.kind<>'schedule' OR x.trigger_id<>trigger OR x.target_id<>target
        OR ROW(x.provider,x.tenant_id,x.object_id,x.application_id) IS DISTINCT FROM ROW(issuer,tenant,principal,application) THEN
        RETURN QUERY SELECT 3,NULL::uuid[],NULL::integer[]; RETURN;
    END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls y WHERE y.organization_id=org AND y.trigger_id=trigger;
    IF c.current_version<>x.configuration_version THEN
        SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.run_id=run FOR UPDATE;
        IF x.state<>'scanning' THEN RETURN QUERY SELECT 1,NULL::uuid[],NULL::integer[]; RETURN; END IF;
        UPDATE ONLY braid.trigger_runs SET state='fenced',claim_id=NULL,claim_expires_at=NULL,updated_at=statement_timestamp(),finished_at=statement_timestamp()
            WHERE organization_id=org AND run_id=run;
        RETURN QUERY SELECT 6,NULL::uuid[],NULL::integer[]; RETURN;
    END IF;
    SELECT * INTO a FROM braid.trigger_admission(org,project,mapping,environment,target_version,target,trigger,x.configuration_version,issuer,tenant,principal,application);
    IF a.status<>0 THEN RETURN QUERY SELECT a.status,NULL::uuid[],NULL::integer[]; RETURN; END IF;
    IF a.definition->>'kind'<>'schedule' THEN RETURN QUERY SELECT 3,NULL::uuid[],NULL::integer[]; RETURN; END IF;
    -- The claim is the authority of the page: a lost, stale or expired claim, or another cursor, writes nothing.
    SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.run_id=run FOR UPDATE;
    IF x.state<>'scanning' OR x.claim_id<>claim OR x.claim_expires_at<=clock_timestamp() OR x.scan_cursor IS DISTINCT FROM cursor_before THEN
        RETURN QUERY SELECT 1,NULL::uuid[],NULL::integer[]; RETURN;
    END IF;
    IF a.definition->>'envelope'='singleOrder' AND EXISTS(SELECT 1 FROM unnest(keys) o WHERE o<>'') THEN RETURN QUERY SELECT 3,NULL::uuid[],NULL::integer[]; RETURN; END IF;
    IF a.definition->>'envelope'='batch' AND EXISTS(SELECT 1 FROM unnest(keys) o WHERE o='') THEN RETURN QUERY SELECT 3,NULL::uuid[],NULL::integer[]; RETURN; END IF;
    FOR k IN 1..n LOOP
        IF NOT EXISTS(SELECT 1 FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.target_id=target AND y.provider=issuer AND y.tenant_id=tenant
            AND y.object_id=principal AND y.source_binding_id=x.source_binding_id AND y.source_identity=sources[k] AND y.order_key=keys[k]) THEN
            fresh_count:=fresh_count+1; fresh_bytes:=fresh_bytes+octet_length(bodies[k]);
            IF octet_length(bodies[k])>(a.definition->'policies'->>'maximumMessageBytes')::bigint THEN RETURN QUERY SELECT 5,NULL::uuid[],NULL::integer[]; RETURN; END IF;
        END IF;
    END LOOP;
    SELECT * INTO b FROM braid.trigger_backlog(org,trigger);
    IF fresh_count>0 AND (b.pending+fresh_count>(a.definition->'policies'->>'maximumPendingWork')::bigint
        OR b.pending_bytes+fresh_bytes>(a.definition->'policies'->>'maximumPendingBytes')::bigint) THEN
        RETURN QUERY SELECT 5,NULL::uuid[],NULL::integer[]; RETURN;
    END IF;
    FOR k IN 1..n LOOP
        SELECT * INTO e FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.target_id=target AND y.provider=issuer AND y.tenant_id=tenant
            AND y.object_id=principal AND y.source_binding_id=x.source_binding_id AND y.source_identity=sources[k] AND y.order_key=keys[k];
        IF FOUND THEN
            result_outcomes:=result_outcomes||CASE WHEN e.raw_source_digest=encode(sha256(bodies[k]),'hex') AND e.application_id=application THEN 1 ELSE 2 END;
            result_works:=result_works||CASE WHEN e.raw_source_digest=encode(sha256(bodies[k]),'hex') AND e.application_id=application THEN e.work_id END;
        ELSE
            w:=a.template; w.work_id:=pg_catalog.gen_random_uuid(); w.request_id:=pg_catalog.gen_random_uuid(); w.source_identity:=sources[k];
            w.order_key:=keys[k]; w.raw_source_digest:=encode(sha256(bodies[k]),'hex'); w.request_digest:=request_shas[k];
            PERFORM braid.trigger_insert_work(w,a.definition->>'inputMediaType',bodies[k],run);
            result_outcomes:=result_outcomes||0; result_works:=result_works||w.work_id; admitted_count:=admitted_count+1;
        END IF;
    END LOOP;
    UPDATE ONLY braid.trigger_runs SET scan_cursor=coalesce(cursor_after,x.scan_cursor),
        pages=x.pages+CASE WHEN complete OR cursor_after IS NOT NULL THEN 1 ELSE 0 END,
        discovered=x.discovered+record_trigger_run_page.discovered,admitted=x.admitted+admitted_count,changed=x.changed+record_trigger_run_page.changed,
        rejected=x.rejected+record_trigger_run_page.rejected,state=CASE WHEN complete THEN 'scanned' ELSE 'scanning' END,
        claim_id=CASE WHEN complete THEN NULL ELSE claim END,claim_expires_at=CASE WHEN complete THEN NULL ELSE clock_timestamp()+interval '120 seconds' END,
        updated_at=statement_timestamp(),finished_at=CASE WHEN complete THEN statement_timestamp() END
        WHERE organization_id=org AND run_id=run;
    RETURN QUERY SELECT 0,result_works,result_outcomes;
END;
$body$;
REVOKE ALL ON FUNCTION braid.record_trigger_run_page(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,uuid,text,text,boolean,integer,integer,integer,text,uuid,uuid,uuid,text[],text[],text[],bytea[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.record_trigger_run_page(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,uuid,text,text,boolean,integer,integer,integer,text,uuid,uuid,uuid,text[],text[],text[],bytea[]) TO braid_trigger_worker;

-- Admits one explicit batch envelope: its parent with the complete child-manifest digest and
-- every keyed order, each with its own ledger boundary, in one transaction. Status: 0 admitted,
-- 1 existing parent for the identical manifest and application (its orders in key order),
-- 2 idempotency conflict, 3 refused, 4 no current release, 5 an order or the backlog exceeds its cap.
CREATE FUNCTION braid.admit_trigger_batch(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, configuration bigint, issuer text, tenant uuid, principal uuid, application uuid, source text, manifest_sha text, keys text[], request_shas text[], bodies bytea[]) RETURNS TABLE(status integer, run uuid, works uuid[])
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE c braid.trigger_controls%ROWTYPE; x braid.trigger_runs%ROWTYPE; a record; w braid.trigger_work%ROWTYPE; b record; k integer; n integer;
    fresh uuid:=pg_catalog.gen_random_uuid(); result_works uuid[]:='{}';
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    n:=coalesce(cardinality(keys),0);
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL OR trigger IS NULL
        OR configuration IS NULL OR issuer IS DISTINCT FROM 'entra' OR tenant IS NULL OR principal IS NULL
        OR application IS NULL OR application='00000000-0000-0000-0000-000000000000'
        OR source IS NULL OR char_length(source) NOT BETWEEN 1 AND 128 OR manifest_sha IS NULL OR manifest_sha!~'^[0-9a-f]{64}$'
        OR n NOT BETWEEN 1 AND 256 OR request_shas IS NULL OR bodies IS NULL OR cardinality(request_shas)<>n OR cardinality(bodies)<>n
        OR array_position(keys,NULL) IS NOT NULL OR array_position(request_shas,NULL) IS NOT NULL OR array_position(bodies,NULL) IS NOT NULL
        OR (SELECT count(DISTINCT o) FROM unnest(keys) o)<>n
        OR EXISTS(SELECT 1 FROM unnest(keys,request_shas,bodies) i(o,r,y) WHERE octet_length(i.o) NOT BETWEEN 1 AND 256 OR i.r!~'^[0-9a-f]{64}$'
            OR octet_length(i.y)>4194304) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid[]; RETURN;
    END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls y WHERE y.organization_id=org AND y.trigger_id=trigger;
    IF NOT FOUND OR c.target_id<>target OR c.project_id<>project OR c.mapping_id<>mapping OR c.kind<>'serviceBusQueue' THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid[]; RETURN;
    END IF;
    -- A redelivery recovers the original parent and its orders, whatever the current state.
    SELECT * INTO x FROM ONLY braid.trigger_runs y WHERE y.organization_id=org AND y.target_id=target AND y.provider=issuer AND y.tenant_id=tenant
        AND y.object_id=principal AND y.source_binding_id=c.source_binding_id AND y.source_identity=source;
    IF FOUND THEN
        IF x.manifest_digest<>manifest_sha OR x.application_id<>application THEN RETURN QUERY SELECT 2,NULL::uuid,NULL::uuid[]; RETURN; END IF;
        RETURN QUERY SELECT 1,x.run_id,ARRAY(SELECT y.work_id FROM unnest(keys) WITH ORDINALITY o(key,i) JOIN ONLY braid.trigger_work y
            ON y.organization_id=org AND y.target_id=target AND y.provider=issuer AND y.tenant_id=tenant AND y.object_id=principal
            AND y.source_binding_id=c.source_binding_id AND y.source_identity=source AND y.order_key=o.key ORDER BY o.i);
        RETURN;
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.target_id=target AND y.provider=issuer AND y.tenant_id=tenant
        AND y.object_id=principal AND y.source_binding_id=c.source_binding_id AND y.source_identity=source) THEN
        RETURN QUERY SELECT 2,NULL::uuid,NULL::uuid[]; RETURN;
    END IF;
    SELECT * INTO a FROM braid.trigger_admission(org,project,mapping,environment,target_version,target,trigger,configuration,issuer,tenant,principal,application);
    IF a.status<>0 THEN RETURN QUERY SELECT a.status,NULL::uuid,NULL::uuid[]; RETURN; END IF;
    IF a.definition->>'envelope'<>'batch' THEN RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid[]; RETURN; END IF;
    SELECT * INTO b FROM braid.trigger_backlog(org,trigger);
    IF EXISTS(SELECT 1 FROM unnest(bodies) y WHERE octet_length(y)>(a.definition->'policies'->>'maximumMessageBytes')::bigint)
        OR b.pending+n>(a.definition->'policies'->>'maximumPendingWork')::bigint
        OR b.pending_bytes+(SELECT sum(octet_length(y)) FROM unnest(bodies) y)>(a.definition->'policies'->>'maximumPendingBytes')::bigint THEN
        RETURN QUERY SELECT 5,NULL::uuid,NULL::uuid[]; RETURN;
    END IF;
    INSERT INTO braid.trigger_runs(organization_id,run_id,project_id,mapping_id,target_id,trigger_id,configuration_version,kind,source_binding_id,provider,
        tenant_id,object_id,application_id,source_identity,manifest_digest,state,pages,discovered,admitted,changed,rejected,created_at,updated_at,finished_at)
        VALUES(org,fresh,project,mapping,target,trigger,configuration,'queueBatch',c.source_binding_id,issuer,tenant,principal,application,source,manifest_sha,
            'admitted',1,n,n,0,0,statement_timestamp(),statement_timestamp(),statement_timestamp());
    FOR k IN 1..n LOOP
        w:=a.template; w.work_id:=pg_catalog.gen_random_uuid(); w.request_id:=pg_catalog.gen_random_uuid(); w.source_identity:=source;
        w.order_key:=keys[k]; w.raw_source_digest:=encode(sha256(bodies[k]),'hex'); w.request_digest:=request_shas[k];
        PERFORM braid.trigger_insert_work(w,a.definition->>'inputMediaType',bodies[k],fresh);
        result_works:=result_works||w.work_id;
    END LOOP;
    RETURN QUERY SELECT 0,fresh,result_works;
END;
$body$;
REVOKE ALL ON FUNCTION braid.admit_trigger_batch(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,text,text,text[],text[],bytea[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.admit_trigger_batch(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,text,text,text[],text[],bytea[]) TO braid_trigger_worker;
