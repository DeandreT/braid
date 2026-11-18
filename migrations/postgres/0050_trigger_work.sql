-- Durable trigger configuration, order admission, pinned execution and the delivery outbox.
-- Trigger work shares the machine invocation ledger rows, audit and retained responses; it
-- never sends anything itself and stores connector references, never endpoints or secrets.
DO $roles$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='braid_trigger_worker';
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
        RAISE EXCEPTION 'Independent trigger worker role required.' USING ERRCODE='42501';
    END IF;
END;
$roles$;

-- The current version and control state of one trigger. Retirement is one-way; disable and
-- retire advance the admission epoch, which fences every order admitted under an earlier one.
CREATE TABLE braid.trigger_controls (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    trigger_id uuid NOT NULL CHECK(trigger_id<>'00000000-0000-0000-0000-000000000000'),
    kind text NOT NULL CHECK(kind IN ('serviceBusQueue')),
    source_binding_id uuid NOT NULL CHECK(source_binding_id<>'00000000-0000-0000-0000-000000000000'),
    source_connector_id text NOT NULL CHECK(source_connector_id~'^[a-z0-9][a-z0-9.-]{0,62}$'),
    current_version bigint NOT NULL CHECK(current_version>0), enabled boolean NOT NULL, retired boolean NOT NULL,
    generation bigint NOT NULL CHECK(generation>0), admission_epoch bigint NOT NULL CHECK(admission_epoch>0),
    updated_at timestamp with time zone NOT NULL CHECK(isfinite(updated_at)),
    PRIMARY KEY(organization_id,trigger_id),
    UNIQUE(organization_id,source_binding_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK(NOT(enabled AND retired))
);
CREATE UNIQUE INDEX trigger_controls_source ON braid.trigger_controls(organization_id,source_connector_id) WHERE NOT retired;
CREATE TABLE braid.trigger_configurations (
    organization_id uuid NOT NULL, trigger_id uuid NOT NULL, version bigint NOT NULL CHECK(version>0),
    definition bytea NOT NULL, definition_digest text NOT NULL, actor_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,trigger_id,version),
    FOREIGN KEY(organization_id,trigger_id) REFERENCES braid.trigger_controls(organization_id,trigger_id),
    CHECK(octet_length(definition) BETWEEN 2 AND 65536 AND encode(sha256(definition),'hex')=definition_digest)
);
CREATE TABLE braid.trigger_operations (
    organization_id uuid NOT NULL, operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    target_id uuid NOT NULL, trigger_id uuid NOT NULL, actor_id uuid NOT NULL,
    action text NOT NULL CHECK(action IN ('configure','enable','disable','retire')),
    expected_generation bigint NOT NULL CHECK(expected_generation>=0),
    definition_digest text CHECK(definition_digest~'^[0-9a-f]{64}$'), outcome integer NOT NULL CHECK(outcome BETWEEN 0 AND 4),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,operation_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK((action='configure')=(definition_digest IS NOT NULL))
);

-- One logical order. Its source identity is independent of configuration and run versions,
-- and its request ID, digest and complete release pin are fixed once at admission.
CREATE TABLE braid.trigger_work (
    organization_id uuid NOT NULL, work_id uuid NOT NULL CHECK(work_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL, trigger_id uuid NOT NULL, configuration_version bigint NOT NULL,
    source_binding_id uuid NOT NULL, source_identity text NOT NULL CHECK(char_length(source_identity) BETWEEN 1 AND 128),
    order_key text NOT NULL CHECK(octet_length(order_key)<=256), raw_source_digest text NOT NULL CHECK(raw_source_digest~'^[0-9a-f]{64}$'),
    provider text NOT NULL CHECK(provider IN ('entra')), tenant_id uuid NOT NULL, object_id uuid NOT NULL, application_id uuid NOT NULL,
    request_id uuid NOT NULL CHECK(request_id<>'00000000-0000-0000-0000-000000000000'), request_digest text NOT NULL CHECK(request_digest~'^[0-9a-f]{64}$'),
    release_id uuid NOT NULL, qualification_id uuid NOT NULL, build_id uuid NOT NULL, build_intent_digest text NOT NULL CHECK(build_intent_digest~'^[0-9a-f]{64}$'),
    artifact_digest text NOT NULL CHECK(artifact_digest~'^[0-9a-f]{64}$'), revision_id uuid NOT NULL, runtime_image text NOT NULL CHECK(runtime_image~'^sha256:[0-9a-f]{64}$'),
    publisher_id uuid NOT NULL, target_generation bigint NOT NULL CHECK(target_generation>0), grant_id uuid NOT NULL,
    admission_epoch bigint NOT NULL CHECK(admission_epoch>0),
    phase text NOT NULL CHECK(phase IN ('admitted','completed','failed','fenced','superseded')),
    outcome text CHECK(outcome IN ('completed','refused','invalidInput','invalidParameter','executionFailed','resourceLimit','integrityFailure',
        'unsupported','attemptsExhausted')),
    created_at timestamp with time zone NOT NULL CHECK(isfinite(created_at)), finished_at timestamp with time zone,
    PRIMARY KEY(organization_id,work_id),
    UNIQUE(organization_id,target_id,provider,tenant_id,object_id,source_binding_id,source_identity,order_key),
    UNIQUE(organization_id,target_id,provider,tenant_id,object_id,request_id),
    FOREIGN KEY(organization_id,trigger_id,configuration_version) REFERENCES braid.trigger_configurations(organization_id,trigger_id,version),
    FOREIGN KEY(organization_id,grant_id) REFERENCES braid.invocation_grants(organization_id,grant_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK((phase='admitted')=(finished_at IS NULL)), CHECK((phase IN ('completed','failed'))=(outcome IS NOT NULL)),
    CHECK((phase='completed')=(outcome IS NOT DISTINCT FROM 'completed')),
    CHECK(finished_at IS NULL OR (isfinite(finished_at) AND finished_at>=created_at))
);
CREATE INDEX trigger_work_pending ON braid.trigger_work(organization_id,trigger_id) WHERE phase='admitted';
CREATE TABLE braid.trigger_receipts (
    organization_id uuid NOT NULL, receipt_id uuid NOT NULL, work_id uuid NOT NULL,
    direction text NOT NULL CHECK(direction IN ('input','output')), output_name text NOT NULL CHECK(octet_length(output_name)<=2048),
    media_type text NOT NULL CHECK(octet_length(media_type) BETWEEN 1 AND 256), byte_count integer NOT NULL CHECK(byte_count BETWEEN 0 AND 4194304),
    digest text NOT NULL CHECK(digest~'^[0-9a-f]{64}$'),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,receipt_id),
    UNIQUE(organization_id,work_id,direction,output_name),
    FOREIGN KEY(organization_id,work_id) REFERENCES braid.trigger_work(organization_id,work_id),
    CHECK(direction='output' OR output_name='')
);
-- Exact private bytes, retained independently of the receipt that names them.
CREATE TABLE braid.trigger_payloads (
    organization_id uuid NOT NULL, receipt_id uuid NOT NULL, body bytea NOT NULL CHECK(octet_length(body)<=4194304),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,receipt_id),
    FOREIGN KEY(organization_id,receipt_id) REFERENCES braid.trigger_receipts(organization_id,receipt_id)
);
-- One immutable delivery per output and configured route. The delivery ID is the remote key.
CREATE TABLE braid.trigger_deliveries (
    organization_id uuid NOT NULL, delivery_id uuid NOT NULL, work_id uuid NOT NULL, receipt_id uuid NOT NULL,
    target_id uuid NOT NULL, output_name text NOT NULL, destination_binding_id uuid NOT NULL,
    destination_kind text NOT NULL CHECK(destination_kind IN ('blob','sftp','https','serviceBusTopic','as2')),
    connector_id text NOT NULL CHECK(connector_id~'^[a-z0-9][a-z0-9.-]{0,62}$'), connector_version bigint NOT NULL CHECK(connector_version>0),
    phase text NOT NULL CHECK(phase IN ('pending','sending','accepted','unknown','permanentFailure','exhausted','fenced','superseded','payloadUnavailable')),
    sends integer NOT NULL CHECK(sends BETWEEN 0 AND 32), claim_id uuid, claim_expires_at timestamp with time zone,
    next_at timestamp with time zone NOT NULL CHECK(isfinite(next_at)), admitted_at timestamp with time zone NOT NULL CHECK(isfinite(admitted_at)),
    first_ambiguous_at timestamp with time zone, last_dispatch_at timestamp with time zone, settled_at timestamp with time zone,
    PRIMARY KEY(organization_id,delivery_id),
    UNIQUE(organization_id,work_id,output_name,destination_binding_id,connector_version),
    FOREIGN KEY(organization_id,work_id) REFERENCES braid.trigger_work(organization_id,work_id),
    FOREIGN KEY(organization_id,receipt_id) REFERENCES braid.trigger_receipts(organization_id,receipt_id),
    CHECK((phase='sending')=(claim_id IS NOT NULL) AND (claim_id IS NULL)=(claim_expires_at IS NULL)),
    CHECK((phase IN ('accepted','permanentFailure','exhausted','fenced','superseded','payloadUnavailable'))=(settled_at IS NOT NULL)),
    CHECK((sends=0)=(last_dispatch_at IS NULL)), CHECK(first_ambiguous_at IS NULL OR first_ambiguous_at<=last_dispatch_at)
);
CREATE INDEX trigger_deliveries_due ON braid.trigger_deliveries(organization_id,target_id,destination_binding_id,next_at) WHERE phase IN ('pending','sending');
-- Append-only metadata history for Operations: identifiers and fixed outcomes only.
CREATE TABLE braid.trigger_events (
    organization_id uuid NOT NULL, event_id uuid NOT NULL DEFAULT pg_catalog.gen_random_uuid(), trigger_id uuid NOT NULL, work_id uuid NOT NULL,
    delivery_id uuid, kind text NOT NULL CHECK(kind IN ('admitted','claimed','finished','fenced','superseded','deliveryEnqueued','deliveryClaimed',
        'deliveryObserved','deliveryExpired','deliverySettled','deliveryLate')),
    outcome text CHECK(outcome~'^[a-zA-Z]{1,32}$'), attempt integer CHECK(attempt BETWEEN 1 AND 32),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,event_id),
    FOREIGN KEY(organization_id,work_id) REFERENCES braid.trigger_work(organization_id,work_id),
    FOREIGN KEY(organization_id,delivery_id) REFERENCES braid.trigger_deliveries(organization_id,delivery_id)
);
CREATE INDEX trigger_events_work ON braid.trigger_events(organization_id,work_id,recorded_at);
ALTER TABLE braid.trigger_controls ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_configurations ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_operations ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_work ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_payloads ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_deliveries ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.trigger_events ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.trigger_controls USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_configurations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_operations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_work USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_receipts USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_payloads USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_deliveries USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.trigger_events USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.trigger_controls,braid.trigger_configurations,braid.trigger_operations,braid.trigger_work,braid.trigger_receipts,
    braid.trigger_payloads,braid.trigger_deliveries,braid.trigger_events FROM PUBLIC;

CREATE FUNCTION braid.guard_trigger() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR session_user<>(CASE WHEN TG_TABLE_NAME IN ('trigger_controls','trigger_configurations','trigger_operations') THEN 'braid_release_writer' ELSE 'braid_trigger_worker' END) THEN
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
REVOKE ALL ON FUNCTION braid.guard_trigger() FROM PUBLIC;
CREATE TRIGGER trigger_control_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_controls FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_control_no_truncate BEFORE TRUNCATE ON braid.trigger_controls FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_configuration_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_configurations FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_configuration_no_truncate BEFORE TRUNCATE ON braid.trigger_configurations FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_operation_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_operations FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_operation_no_truncate BEFORE TRUNCATE ON braid.trigger_operations FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_work_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_work FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_work_no_truncate BEFORE TRUNCATE ON braid.trigger_work FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_receipt_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_receipts FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_receipt_no_truncate BEFORE TRUNCATE ON braid.trigger_receipts FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_payload_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_payloads FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_payload_no_truncate BEFORE TRUNCATE ON braid.trigger_payloads FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_delivery_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_deliveries FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_delivery_no_truncate BEFORE TRUNCATE ON braid.trigger_deliveries FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_event_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.trigger_events FOR EACH ROW EXECUTE FUNCTION braid.guard_trigger();
CREATE TRIGGER trigger_event_no_truncate BEFORE TRUNCATE ON braid.trigger_events FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_trigger();

-- The ledger guard is unchanged for its existing logins. The trigger worker may write a ledger
-- row only for an existing admitted order with exactly that key; it has no other ledger path.
CREATE OR REPLACE FUNCTION braid.guard_machine_invocation() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP='TRUNCATE' THEN
        RAISE EXCEPTION 'Machine invocation mutation refused.' USING ERRCODE='42501';
    END IF;
    IF session_user='braid_trigger_worker' THEN
        IF TG_OP='DELETE' OR TG_TABLE_NAME NOT IN ('machine_invocations','machine_invocation_audit','machine_invocation_results') THEN
            RAISE EXCEPTION 'Machine invocation mutation refused.' USING ERRCODE='42501';
        END IF;
        IF NOT EXISTS(SELECT 1 FROM ONLY braid.trigger_work w WHERE w.organization_id=NEW.organization_id AND w.target_id=NEW.target_id
            AND w.provider=NEW.provider AND w.tenant_id=NEW.tenant_id AND w.object_id=NEW.object_id AND w.request_id=NEW.request_id) THEN
            RAISE EXCEPTION 'Machine invocation mutation refused.' USING ERRCODE='42501';
        END IF;
    ELSIF session_user<>(CASE WHEN TG_TABLE_NAME LIKE 'invocation_grant%' THEN 'braid_invocation_grant_writer' ELSE 'braid_invocation_writer' END)
        AND NOT(session_user='braid_administration' AND TG_TABLE_NAME IN ('invocation_grants','invocation_grant_audit')) THEN
        RAISE EXCEPTION 'Machine invocation mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME IN ('invocation_grant_audit','machine_invocation_audit') THEN
        IF TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN RAISE EXCEPTION 'Machine invocation audit is immutable.' USING ERRCODE='42501'; END IF;
    ELSIF TG_TABLE_NAME='invocation_grants' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.revoked_by IS NOT NULL OR NEW.revoked_at IS NOT NULL OR NEW.granted_at<>statement_timestamp()
                OR braid.build_authorized(NEW.organization_id,NEW.project_id,NEW.granted_by,ARRAY['administrator']) IS NOT TRUE THEN
                RAISE EXCEPTION 'Invocation grant refused.' USING ERRCODE='42501';
            END IF;
        ELSIF TG_OP<>'UPDATE' OR ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.grant_id,NEW.provider,NEW.tenant_id,NEW.object_id,NEW.granted_by,NEW.granted_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.grant_id,OLD.provider,OLD.tenant_id,OLD.object_id,OLD.granted_by,OLD.granted_at)
            OR OLD.revoked_at IS NOT NULL OR NEW.revoked_at IS DISTINCT FROM statement_timestamp()
            OR braid.build_authorized(NEW.organization_id,NEW.project_id,NEW.revoked_by,ARRAY['administrator']) IS NOT TRUE THEN
            RAISE EXCEPTION 'Invocation grant history is immutable.' USING ERRCODE='42501';
        END IF;
    ELSIF TG_TABLE_NAME='machine_invocation_results' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.retained_at<>statement_timestamp() OR NOT EXISTS(SELECT 1 FROM ONLY braid.machine_invocations i
                WHERE i.organization_id=NEW.organization_id AND i.target_id=NEW.target_id AND i.provider=NEW.provider AND i.tenant_id=NEW.tenant_id
                    AND i.object_id=NEW.object_id AND i.request_id=NEW.request_id AND i.state='completed' AND i.finished_at=statement_timestamp()) THEN
                RAISE EXCEPTION 'Invocation result retention refused.' USING ERRCODE='42501';
            END IF;
        ELSIF TG_OP<>'DELETE' OR OLD.expires_at>clock_timestamp() THEN
            RAISE EXCEPTION 'Retained invocation results are immutable until they expire.' USING ERRCODE='42501';
        END IF;
        RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
    ELSE
        IF TG_OP='INSERT' THEN
            IF NEW.state<>'running' OR NEW.attempt<>1 OR NEW.finished_at IS NOT NULL OR NEW.created_at<>statement_timestamp() THEN
                RAISE EXCEPTION 'Machine invocation claim refused.' USING ERRCODE='42501';
            END IF;
        ELSIF TG_OP<>'UPDATE' OR ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.provider,NEW.tenant_id,NEW.object_id,NEW.request_id,NEW.request_digest,NEW.application_id,NEW.created_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.provider,OLD.tenant_id,OLD.object_id,OLD.request_id,OLD.request_digest,OLD.application_id,OLD.created_at)
            OR OLD.state IN ('completed','failed')
            OR (NEW.state='running' AND (NEW.attempt<>OLD.attempt+1 OR NEW.claim_id=OLD.claim_id OR (OLD.state='running' AND OLD.lease_until>clock_timestamp())))
            OR (NEW.state<>'running' AND (OLD.state<>'running' OR NEW.attempt<>OLD.attempt OR NEW.claim_id<>OLD.claim_id))
            OR (NEW.state IN ('completed','failed'))<>(NEW.finished_at IS NOT DISTINCT FROM statement_timestamp()) THEN
            RAISE EXCEPTION 'Machine invocation history is immutable.' USING ERRCODE='42501';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;

-- Closed configuration shape. The service also requires the exact canonical bytes on read.
CREATE FUNCTION braid.trigger_definition_valid(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, body bytea) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE d jsonb; p jsonb; r jsonb; n integer;
BEGIN
    IF body IS NULL OR octet_length(body) NOT BETWEEN 2 AND 65536 THEN RETURN false; END IF;
    d:=convert_from(body,'UTF8')::jsonb;
    IF jsonb_typeof(d)<>'object' OR (SELECT count(*) FROM jsonb_object_keys(d))<>21 OR NOT d?&ARRAY['schema','triggerId','organizationId','projectId',
            'mappingId','environmentId','environmentVersion','targetId','kind','sourceBindingId','sourceConnectorId','sourceConnectorVersion','callerProvider',
            'callerTenantId','callerObjectId','applicationId','envelope','inputMediaType','parameters','routes','policies']
        OR d->>'schema' IS DISTINCT FROM 'braid.trigger-configuration.v1' OR (d->>'triggerId')::uuid IS DISTINCT FROM trigger
        OR (d->>'organizationId')::uuid IS DISTINCT FROM org OR (d->>'projectId')::uuid IS DISTINCT FROM project
        OR (d->>'mappingId')::uuid IS DISTINCT FROM mapping OR (d->>'environmentId')::uuid IS DISTINCT FROM environment
        OR (d->>'environmentVersion')::bigint IS DISTINCT FROM target_version OR (d->>'targetId')::uuid IS DISTINCT FROM target
        OR d->>'kind' IS DISTINCT FROM 'serviceBusQueue' OR d->>'envelope' IS DISTINCT FROM 'singleOrder' OR d->>'callerProvider' IS DISTINCT FROM 'entra'
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
    RETURN true;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR character_not_in_repertoire OR untranslatable_character
    OR invalid_parameter_value OR datatype_mismatch THEN RETURN false;
END;
$body$;
REVOKE ALL ON FUNCTION braid.trigger_definition_valid(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bytea) FROM PUBLIC;

-- The next send time after a known non-acceptance or a create-only repeat, or NULL when the
-- explicit attempt count or elapsed lifetime from the original admission is exhausted.
CREATE FUNCTION braid.trigger_retry_at(policies jsonb, sends integer, admitted timestamp with time zone, observed timestamp with time zone) RETURNS timestamp with time zone
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE retry timestamp with time zone;
BEGIN
    IF sends<1 OR sends>=(policies->>'totalSends')::integer THEN RETURN NULL; END IF;
    retry:=observed+((policies->'retryDelaysMs'->>(sends-1))::bigint*interval '1 millisecond');
    IF retry+((policies->>'attemptWindowMs')::bigint*interval '1 millisecond')>admitted+((policies->>'maximumElapsedMs')::bigint*interval '1 millisecond') THEN
        RETURN NULL;
    END IF;
    RETURN retry;
END;
$body$;
REVOKE ALL ON FUNCTION braid.trigger_retry_at(jsonb,integer,timestamp with time zone,timestamp with time zone) FROM PUBLIC;

-- Result: 0 applied, 1 unchanged, 2 stale generation, 3 incomplete (policy or grant missing), 4 refused,
-- 5 the operation ID was used with a different body (nothing recorded).
CREATE FUNCTION braid.operate_trigger(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, operation uuid, action text, expected bigint, body bytea) RETURNS integer
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
        IF registered AND ((d->>'sourceBindingId')::uuid<>c.source_binding_id OR d->>'sourceConnectorId'<>c.source_connector_id OR d->>'kind'<>c.kind) THEN result:=4;
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
REVOKE ALL ON FUNCTION braid.operate_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.operate_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,bytea) TO braid_release_writer;

CREATE FUNCTION braid.read_trigger_operation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, operation uuid, action text, expected bigint, digest text) RETURNS integer
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; old braid.trigger_operations%ROWTYPE;
BEGIN
    IF session_user<>'braid_release_writer' OR braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN RETURN 4; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version AND t.target_id=target) THEN RETURN 4; END IF;
    SELECT * INTO old FROM ONLY braid.trigger_operations WHERE organization_id=org AND operation_id=operation;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF ROW(old.target_id,old.trigger_id,old.actor_id,old.action,old.expected_generation,old.definition_digest)
        IS DISTINCT FROM ROW(target,trigger,actor,action,expected,digest) THEN RETURN 5; END IF;
    RETURN old.outcome;
END;
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_operation(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_operation(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,text) TO braid_release_writer;

CREATE FUNCTION braid.read_trigger(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid) RETURNS TABLE(version bigint, definition bytea, enabled boolean, retired boolean, generation bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT c.current_version,x.definition,c.enabled,c.retired,c.generation
    FROM ONLY braid.trigger_controls c JOIN ONLY braid.trigger_configurations x ON x.organization_id=c.organization_id AND x.trigger_id=c.trigger_id AND x.version=c.current_version
    JOIN ONLY braid.local_release_targets t ON t.organization_id=c.organization_id AND t.target_id=c.target_id
    WHERE session_user='braid_release_writer' AND c.organization_id=org AND c.trigger_id=trigger AND c.target_id=target AND t.project_id=project
        AND t.mapping_id=mapping AND t.environment_id=environment AND t.environment_version=target_version
        AND braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['publisher','administrator'])
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_release_writer;

-- The worker's view: the current non-secret definition of a trigger that is not retired.
CREATE FUNCTION braid.read_trigger_definition(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid) RETURNS TABLE(version bigint, definition bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT c.current_version,x.definition
    FROM ONLY braid.trigger_controls c JOIN ONLY braid.trigger_configurations x ON x.organization_id=c.organization_id AND x.trigger_id=c.trigger_id AND x.version=c.current_version
    JOIN ONLY braid.local_release_targets t ON t.organization_id=c.organization_id AND t.target_id=c.target_id
    WHERE session_user='braid_trigger_worker' AND c.organization_id=org AND org=NULLIF(current_setting('braid.organization_id',true),'')::uuid
        AND c.trigger_id=trigger AND c.target_id=target AND NOT c.retired AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version
$body$;
REVOKE ALL ON FUNCTION braid.read_trigger_definition(uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_trigger_definition(uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_trigger_worker;

-- Status: 0 admitted, 1 existing work for identical bytes and application, 2 idempotency conflict,
-- 3 refused (disabled, retired, stale configuration, caller or grant), 4 no current release,
-- 5 capacity or message size. A source identity is looked up before any current configuration.
CREATE FUNCTION braid.admit_trigger_work(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, trigger uuid, configuration bigint, issuer text, tenant uuid, principal uuid, application uuid, source text, order_key text, request_sha text, body bytea) RETURNS TABLE(status integer, work uuid)
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
    IF NOT FOUND OR c.target_id<>target OR c.project_id<>project OR c.mapping_id<>mapping THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
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
        OR (d->>'applicationId')::uuid<>application OR jsonb_typeof(d->'policies')<>'object' THEN RETURN QUERY SELECT 3,NULL::uuid; RETURN; END IF;
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
REVOKE ALL ON FUNCTION braid.admit_trigger_work(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,text,text,text,bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.admit_trigger_work(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bigint,text,uuid,uuid,uuid,text,text,text,bytea) TO braid_trigger_worker;

-- Ends admitted work that its pin no longer authorizes: 'superseded' when the pointer moved,
-- otherwise 'fenced' (disable, retirement, revoked pinned grant or any target generation change).
CREATE FUNCTION braid.trigger_work_fence(w braid.trigger_work, t braid.local_release_targets, grant_revoked boolean, c braid.trigger_controls) RETURNS text
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT CASE WHEN t.active_release_id IS DISTINCT FROM w.release_id THEN 'superseded'
        WHEN NOT t.enabled OR grant_revoked OR NOT c.enabled OR c.retired OR c.admission_epoch<>w.admission_epoch
            OR (SELECT max(e.version) FROM ONLY braid.release_target_events e WHERE e.organization_id=w.organization_id AND e.target_id=w.target_id)
                IS DISTINCT FROM w.target_generation THEN 'fenced' END
$body$;
REVOKE ALL ON FUNCTION braid.trigger_work_fence(braid.trigger_work,braid.local_release_targets,boolean,braid.trigger_controls) FROM PUBLIC;

-- Status: 0 claimed, 1 recorded final outcome, 2 attempt in progress, 3 refused, 4 fenced, 5 release superseded.
CREATE FUNCTION braid.claim_trigger_work(org uuid, work uuid, issuer text, tenant uuid, principal uuid, application uuid) RETURNS TABLE(status integer, attempt_number integer, claim_key uuid, request uuid, request_sha text, configuration bytea, input bytea, release uuid, qualification uuid, build uuid, intent_sha text, artifact_sha text, revision uuid, image text, publisher uuid, target_generation bigint, grant_key uuid, final_outcome text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE w braid.trigger_work%ROWTYPE; t braid.local_release_targets%ROWTYPE; g braid.invocation_grants%ROWTYPE; c braid.trigger_controls%ROWTYPE;
    i braid.machine_invocations%ROWTYPE; existing boolean; fence text; fresh uuid:=pg_catalog.gen_random_uuid(); next_attempt integer; seat integer;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR work IS NULL OR issuer IS NULL OR tenant IS NULL OR principal IS NULL OR application IS NULL THEN
        RETURN QUERY SELECT 3,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    SELECT * INTO w FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.work_id=work;
    IF NOT FOUND OR ROW(w.provider,w.tenant_id,w.object_id,w.application_id) IS DISTINCT FROM ROW(issuer,tenant,principal,application) THEN
        RETURN QUERY SELECT 3,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    -- Lock order: target, grant, ledger, trigger control, work.
    SELECT * INTO t FROM ONLY braid.local_release_targets x WHERE x.organization_id=org AND x.target_id=w.target_id FOR SHARE;
    SELECT * INTO g FROM ONLY braid.invocation_grants x WHERE x.organization_id=org AND x.grant_id=w.grant_id FOR SHARE;
    SELECT * INTO i FROM ONLY braid.machine_invocations x WHERE x.organization_id=org AND x.target_id=w.target_id AND x.provider=w.provider
        AND x.tenant_id=w.tenant_id AND x.object_id=w.object_id AND x.request_id=w.request_id FOR UPDATE;
    existing:=FOUND;
    SELECT * INTO c FROM ONLY braid.trigger_controls x WHERE x.organization_id=org AND x.trigger_id=w.trigger_id FOR SHARE;
    SELECT * INTO w FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.work_id=work FOR UPDATE;
    IF w.phase IN ('completed','failed') THEN
        RETURN QUERY SELECT 1,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,w.outcome; RETURN;
    END IF;
    IF w.phase<>'admitted' THEN
        RETURN QUERY SELECT CASE WHEN w.phase='superseded' THEN 5 ELSE 4 END,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    fence:=braid.trigger_work_fence(w,t,g.revoked_at IS NOT NULL OR g.object_id<>w.object_id,c);
    IF fence IS NOT NULL AND NOT (existing AND i.state='running' AND i.lease_until>clock_timestamp()) THEN
        -- A live attempt elsewhere settles the work itself when it finishes.
        UPDATE ONLY braid.trigger_work SET phase=fence,finished_at=statement_timestamp() WHERE organization_id=org AND work_id=work;
        INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind) VALUES(org,w.trigger_id,work,fence);
        RETURN QUERY SELECT CASE WHEN fence='superseded' THEN 5 ELSE 4 END,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    IF existing AND i.state IN ('completed','failed') THEN RAISE EXCEPTION 'Trigger work and ledger disagree.' USING ERRCODE='23514'; END IF;
    IF existing AND i.state='running' AND i.lease_until>clock_timestamp() THEN
        RETURN QUERY SELECT 2,i.attempt,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    -- Share the same bounded seats and advisory key as HTTP Host invocations. No ledger
    -- attempt or pin is consumed on contention. Expired leases free their seat.
    IF pg_try_advisory_xact_lock(hashtextextended('braid.host.target:'||org::text||':'||w.target_id::text,0)) THEN
        SELECT n INTO seat FROM generate_series(1,2) n WHERE NOT EXISTS (
            SELECT 1 FROM ONLY braid.host_target_slots x JOIN ONLY braid.machine_invocations live
                USING (organization_id,target_id,provider,tenant_id,object_id,request_id,claim_id)
            WHERE x.organization_id=org AND x.target_id=w.target_id AND x.slot=n AND live.state='running' AND live.lease_until>clock_timestamp())
            ORDER BY n LIMIT 1;
    END IF;
    IF seat IS NULL THEN
        RETURN QUERY SELECT 2,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,NULL::text; RETURN;
    END IF;
    IF NOT existing THEN
        INSERT INTO braid.machine_invocations(organization_id,project_id,mapping_id,target_id,provider,tenant_id,object_id,request_id,request_digest,application_id,claim_id,lease_until)
            VALUES(org,w.project_id,w.mapping_id,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,w.request_digest,w.application_id,fresh,clock_timestamp()+interval '120 seconds');
        next_attempt:=1;
    ELSE
        IF i.request_digest<>w.request_digest OR i.application_id<>w.application_id THEN RAISE EXCEPTION 'Trigger work and ledger disagree.' USING ERRCODE='23514'; END IF;
        IF i.state='running' THEN
            -- The previous attempt ended without an outcome: its lease expired.
            IF i.attempt=8 THEN
                UPDATE ONLY braid.machine_invocations SET state='failed',finished_at=statement_timestamp() WHERE organization_id=org AND target_id=w.target_id
                    AND provider=w.provider AND tenant_id=w.tenant_id AND object_id=w.object_id AND request_id=w.request_id;
                INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
                    VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,16,8,'failed','attemptsExhausted',w.grant_id,w.application_id);
                UPDATE ONLY braid.trigger_work SET phase='failed',outcome='attemptsExhausted',finished_at=statement_timestamp() WHERE organization_id=org AND work_id=work;
                INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind,outcome,attempt) VALUES(org,w.trigger_id,work,'finished','attemptsExhausted',8);
                RETURN QUERY SELECT 1,8,NULL::uuid,NULL::uuid,NULL::text,NULL::bytea,NULL::bytea,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::text,NULL::uuid,NULL::bigint,NULL::uuid,'attemptsExhausted'::text; RETURN;
            END IF;
            INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
                VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,2*i.attempt,i.attempt,'released','interrupted',w.grant_id,w.application_id);
            UPDATE ONLY braid.machine_invocations SET state='retryable' WHERE organization_id=org AND target_id=w.target_id
                AND provider=w.provider AND tenant_id=w.tenant_id AND object_id=w.object_id AND request_id=w.request_id;
        END IF;
        next_attempt:=i.attempt+1;
        UPDATE ONLY braid.machine_invocations SET state='running',attempt=next_attempt,claim_id=fresh,lease_until=clock_timestamp()+interval '120 seconds'
            WHERE organization_id=org AND target_id=w.target_id AND provider=w.provider AND tenant_id=w.tenant_id AND object_id=w.object_id AND request_id=w.request_id;
    END IF;
    IF NOT existing THEN
        INSERT INTO braid.host_order_pins VALUES(org,w.target_id,issuer,tenant,principal,w.request_id,w.release_id,w.publisher_id);
    ELSIF NOT EXISTS (SELECT 1 FROM ONLY braid.host_order_pins h WHERE h.organization_id=org AND h.target_id=w.target_id AND h.provider=issuer
        AND h.tenant_id=tenant AND h.object_id=principal AND h.request_id=w.request_id AND h.release_id=w.release_id AND h.publisher_id=w.publisher_id) THEN
        RAISE EXCEPTION 'Pinned Host order unavailable.' USING ERRCODE='23514';
    END IF;
    INSERT INTO braid.host_target_slots VALUES(org,w.target_id,seat,issuer,tenant,principal,w.request_id,fresh)
        ON CONFLICT ON CONSTRAINT host_target_slots_pkey DO UPDATE SET provider=excluded.provider,tenant_id=excluded.tenant_id,
        object_id=excluded.object_id,request_id=excluded.request_id,claim_id=excluded.claim_id;
    INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
        VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,2*next_attempt-1,next_attempt,'claimed','none',w.grant_id,w.application_id);
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind,attempt) VALUES(org,w.trigger_id,work,'claimed',next_attempt);
    RETURN QUERY SELECT 0,next_attempt,fresh,w.request_id,w.request_digest,
        (SELECT x.definition FROM ONLY braid.trigger_configurations x WHERE x.organization_id=org AND x.trigger_id=w.trigger_id AND x.version=w.configuration_version),
        (SELECT p.body FROM ONLY braid.trigger_receipts r JOIN ONLY braid.trigger_payloads p ON p.organization_id=r.organization_id AND p.receipt_id=r.receipt_id
            WHERE r.organization_id=org AND r.work_id=work AND r.direction='input'),
        w.release_id,w.qualification_id,w.build_id,w.build_intent_digest,w.artifact_digest,w.revision_id,w.runtime_image,w.publisher_id,w.target_generation,w.grant_id,NULL::text;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_trigger_work(uuid,uuid,text,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.claim_trigger_work(uuid,uuid,text,uuid,uuid,uuid) TO braid_trigger_worker;

-- Result: 0 recorded, 1 fenced (nothing retained or enqueued), 2 superseded or expired claim
-- (nothing written), 3 invalid finish, provenance or output set, 4 release superseded.
CREATE FUNCTION braid.finish_trigger_work(org uuid, work uuid, claim uuid, application uuid, finding text, release uuid, qualification uuid, build uuid, intent_sha text, artifact_sha text, revision uuid, image text, clock text, response bytea, output_names text[], output_media text[], output_bodies bytea[]) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE w braid.trigger_work%ROWTYPE; t braid.local_release_targets%ROWTYPE; g braid.invocation_grants%ROWTYPE; c braid.trigger_controls%ROWTYPE;
    i braid.machine_invocations%ROWTYPE; d jsonb; v jsonb; fence text; event text; next_state text; recorded text:=finding; receipt uuid; k integer; route jsonb;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR work IS NULL OR claim IS NULL OR application IS NULL OR finding IS NULL
        OR finding NOT IN ('completed','refused','invalidInput','invalidParameter','executionFailed','resourceLimit','integrityFailure','unsupported',
            'unavailable','busy','releaseChanged')
        OR (finding='completed')<>(response IS NOT NULL) OR (finding='completed' AND (release IS NULL OR clock IS NULL))
        OR (finding='completed')<>(output_names IS NOT NULL) OR (output_names IS NULL)<>(output_media IS NULL) OR (output_names IS NULL)<>(output_bodies IS NULL)
        OR coalesce(cardinality(output_names),1)<1 OR cardinality(output_names)<>cardinality(output_media) OR cardinality(output_names)<>cardinality(output_bodies)
        OR array_position(output_names,NULL) IS NOT NULL OR array_position(output_media,NULL) IS NOT NULL OR array_position(output_bodies,NULL) IS NOT NULL
        OR octet_length(response) NOT BETWEEN 1 AND 4194304
        OR (release IS NULL)<>(qualification IS NULL) OR (release IS NULL)<>(build IS NULL) OR (release IS NULL)<>(intent_sha IS NULL)
        OR (release IS NULL)<>(artifact_sha IS NULL) OR (release IS NULL)<>(revision IS NULL) OR (release IS NULL)<>(image IS NULL) THEN RETURN 3; END IF;
    SELECT * INTO w FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.work_id=work;
    IF NOT FOUND OR w.application_id<>application THEN RETURN 3; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets x WHERE x.organization_id=org AND x.target_id=w.target_id FOR SHARE;
    SELECT * INTO g FROM ONLY braid.invocation_grants x WHERE x.organization_id=org AND x.grant_id=w.grant_id FOR SHARE;
    SELECT * INTO i FROM ONLY braid.machine_invocations x WHERE x.organization_id=org AND x.target_id=w.target_id AND x.provider=w.provider
        AND x.tenant_id=w.tenant_id AND x.object_id=w.object_id AND x.request_id=w.request_id FOR UPDATE;
    -- The lease is the attempt's authority: an expired attempt cannot record or retain an outcome.
    IF NOT FOUND OR i.state<>'running' OR i.claim_id<>claim OR i.lease_until<=clock_timestamp() THEN RETURN 2; END IF;
    SELECT * INTO c FROM ONLY braid.trigger_controls x WHERE x.organization_id=org AND x.trigger_id=w.trigger_id FOR SHARE;
    SELECT * INTO w FROM ONLY braid.trigger_work x WHERE x.organization_id=org AND x.work_id=work FOR UPDATE;
    IF release IS NOT NULL AND (ROW(release,qualification,build,intent_sha,artifact_sha,revision,image)
            IS DISTINCT FROM ROW(w.release_id,w.qualification_id,w.build_id,w.build_intent_digest,w.artifact_digest,w.revision_id,w.runtime_image)
        OR NOT braid.machine_invocation_provenance(org,w.target_id,release,qualification,build,intent_sha,artifact_sha,revision,image)) THEN RETURN 3; END IF;
    fence:=CASE WHEN w.phase IN ('fenced','superseded') THEN w.phase WHEN w.phase<>'admitted' THEN 'invalid'
        ELSE braid.trigger_work_fence(w,t,g.revoked_at IS NOT NULL OR g.object_id<>w.object_id,c) END;
    IF fence='invalid' THEN RETURN 3; END IF;
    IF i.lease_until<=clock_timestamp() THEN RETURN 2; END IF;
    IF fence IS NOT NULL THEN
        -- Nothing is retained or enqueued; the ledger attempt is released and the work ends for good.
        event:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'released' END;
        recorded:=CASE WHEN i.attempt=8 THEN 'attemptsExhausted' WHEN fence='superseded' THEN 'releaseChanged' ELSE 'refused' END;
        UPDATE ONLY braid.machine_invocations SET state=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'retryable' END,
            finished_at=CASE WHEN i.attempt=8 THEN statement_timestamp() END
            WHERE organization_id=org AND target_id=w.target_id AND provider=w.provider AND tenant_id=w.tenant_id AND object_id=w.object_id AND request_id=w.request_id;
        INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
            VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,2*i.attempt,i.attempt,event,recorded,w.grant_id,w.application_id);
        IF w.phase='admitted' THEN
            UPDATE ONLY braid.trigger_work SET phase=fence,finished_at=statement_timestamp() WHERE organization_id=org AND work_id=work;
            INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind,attempt) VALUES(org,w.trigger_id,work,fence,i.attempt);
        END IF;
        RETURN CASE WHEN fence='superseded' THEN 4 ELSE 1 END;
    END IF;
    IF finding='completed' THEN
        -- The retained response, the output bytes and the configured routes must describe exactly the same outputs.
        v:=convert_from(response,'UTF8')::jsonb;
        SELECT convert_from(x.definition,'UTF8')::jsonb INTO d FROM ONLY braid.trigger_configurations x
            WHERE x.organization_id=org AND x.trigger_id=w.trigger_id AND x.version=w.configuration_version;
        IF jsonb_typeof(v)<>'object' OR v->>'requestId' IS DISTINCT FROM w.request_id::text OR v->>'releaseId' IS DISTINCT FROM w.release_id::text
            OR jsonb_typeof(v->'outputs')<>'array' OR jsonb_array_length(v->'outputs')<>cardinality(output_names)
            OR (SELECT count(DISTINCT n) FROM unnest(output_names) n)<>cardinality(output_names)
            OR EXISTS(SELECT 1 FROM unnest(output_names,output_media,output_bodies) o(n,m,b) WHERE octet_length(b)>4194304 OR octet_length(m) NOT BETWEEN 1 AND 256
                OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v->'outputs') e WHERE coalesce(e->>'name','')=o.n AND e->>'sha256'=encode(sha256(o.b),'hex')
                    AND coalesce(e->>'mediaType',m)=m))
            OR EXISTS(SELECT 1 FROM jsonb_array_elements(d->'routes') r WHERE NOT coalesce(r->>'outputName','')=ANY(output_names))
            OR EXISTS(SELECT 1 FROM unnest(output_names) n WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(d->'routes') r WHERE coalesce(r->>'outputName','')=n)) THEN
            RETURN 3;
        END IF;
        event:='completed'; next_state:='completed';
    ELSIF finding IN ('unavailable','busy','releaseChanged') THEN
        event:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'released' END;
        next_state:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'retryable' END;
        IF i.attempt=8 THEN recorded:='attemptsExhausted'; END IF;
    ELSE event:='failed'; next_state:='failed';
    END IF;
    UPDATE ONLY braid.machine_invocations SET state=next_state,finished_at=CASE WHEN next_state IN ('completed','failed') THEN statement_timestamp() END
        WHERE organization_id=org AND target_id=w.target_id AND provider=w.provider AND tenant_id=w.tenant_id AND object_id=w.object_id AND request_id=w.request_id;
    INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id,
        release_id,qualification_id,build_id,build_intent_digest,artifact_digest,revision_id,runtime_image,clock,output_digest,output_bytes)
        VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,2*i.attempt,i.attempt,event,recorded,w.grant_id,w.application_id,
            release,qualification,build,intent_sha,artifact_sha,revision,image,clock,
            CASE WHEN response IS NOT NULL THEN encode(sha256(response),'hex') END,octet_length(response));
    IF next_state='completed' THEN
        INSERT INTO braid.machine_invocation_results(organization_id,target_id,provider,tenant_id,object_id,request_id,body,body_digest,expires_at)
            VALUES(org,w.target_id,w.provider,w.tenant_id,w.object_id,w.request_id,response,encode(sha256(response),'hex'),statement_timestamp()+interval '24 hours');
        FOR k IN 1..cardinality(output_names) LOOP
            receipt:=pg_catalog.gen_random_uuid();
            INSERT INTO braid.trigger_receipts(organization_id,receipt_id,work_id,direction,output_name,media_type,byte_count,digest)
                VALUES(org,receipt,work,'output',output_names[k],output_media[k],octet_length(output_bodies[k]),encode(sha256(output_bodies[k]),'hex'));
            INSERT INTO braid.trigger_payloads(organization_id,receipt_id,body) VALUES(org,receipt,output_bodies[k]);
            FOR route IN SELECT value FROM jsonb_array_elements(d->'routes') WHERE coalesce(value->>'outputName','')=output_names[k] LOOP
                WITH created AS (
                    INSERT INTO braid.trigger_deliveries(organization_id,delivery_id,work_id,receipt_id,target_id,output_name,destination_binding_id,destination_kind,
                        connector_id,connector_version,phase,sends,next_at,admitted_at)
                    VALUES(org,pg_catalog.gen_random_uuid(),work,receipt,w.target_id,output_names[k],(route->>'destinationBindingId')::uuid,route->>'kind',
                        route->>'connectorId',(route->>'connectorVersion')::bigint,'pending',0,statement_timestamp(),statement_timestamp())
                    RETURNING delivery_id)
                INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind) SELECT org,w.trigger_id,work,delivery_id,'deliveryEnqueued' FROM created;
            END LOOP;
        END LOOP;
        UPDATE ONLY braid.trigger_work SET phase='completed',outcome='completed',finished_at=statement_timestamp() WHERE organization_id=org AND work_id=work;
    ELSIF next_state='failed' THEN
        UPDATE ONLY braid.trigger_work SET phase='failed',outcome=recorded,finished_at=statement_timestamp() WHERE organization_id=org AND work_id=work;
    END IF;
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,kind,outcome,attempt) VALUES(org,w.trigger_id,work,'finished',recorded,i.attempt);
    RETURN 0;
END;
$body$;
REVOKE ALL ON FUNCTION braid.finish_trigger_work(uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea,text[],text[],bytea[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.finish_trigger_work(uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea,text[],text[],bytea[]) TO braid_trigger_worker;

-- Status: 0 claimed, 1 nothing due, 2 one due delivery settled without a send (fenced, superseded,
-- exhausted or payload unavailable), 3 refused. Expired sends without an observation never become
-- a fresh send permit unless the destination's create-only key makes a repeat safe.
CREATE FUNCTION braid.claim_trigger_delivery(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, destination uuid) RETURNS TABLE(status integer, delivery uuid, claim_key uuid, send_number integer, deadline timestamp with time zone, receipt uuid, admitted timestamp with time zone, media text, content_sha text, content bytea, route_kind text, route_connector text, route_version bigint, route_output text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE t braid.local_release_targets%ROWTYPE; x braid.trigger_deliveries%ROWTYPE; w braid.trigger_work%ROWTYPE; g braid.invocation_grants%ROWTYPE;
    c braid.trigger_controls%ROWTYPE; p jsonb; body bytea; settle text; retry timestamp with time zone; fresh uuid:=pg_catalog.gen_random_uuid();
    ends timestamp with time zone;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR destination IS NULL THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid,NULL::integer,NULL::timestamptz,NULL::uuid,NULL::timestamptz,NULL::text,NULL::text,NULL::bytea,NULL::text,NULL::text,NULL::bigint,NULL::text; RETURN;
    END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets y WHERE y.organization_id=org AND y.target_id=target FOR SHARE;
    IF NOT FOUND OR ROW(t.project_id,t.mapping_id,t.environment_id,t.environment_version) IS DISTINCT FROM ROW(project,mapping,environment,target_version) THEN
        RETURN QUERY SELECT 3,NULL::uuid,NULL::uuid,NULL::integer,NULL::timestamptz,NULL::uuid,NULL::timestamptz,NULL::text,NULL::text,NULL::bytea,NULL::text,NULL::text,NULL::bigint,NULL::text; RETURN;
    END IF;
    FOR x IN SELECT * FROM ONLY braid.trigger_deliveries y WHERE y.organization_id=org AND y.target_id=target AND y.destination_binding_id=destination
        AND y.phase='sending' AND y.claim_expires_at<=clock_timestamp() ORDER BY y.delivery_id FOR UPDATE LOOP
        SELECT convert_from(f.definition,'UTF8')::jsonb->'policies' INTO p FROM ONLY braid.trigger_work y
            JOIN ONLY braid.trigger_configurations f ON f.organization_id=y.organization_id AND f.trigger_id=y.trigger_id AND f.version=y.configuration_version
            WHERE y.organization_id=org AND y.work_id=x.work_id;
        retry:=CASE WHEN x.destination_kind IN ('blob','sftp') THEN braid.trigger_retry_at(p,x.sends,x.admitted_at,x.claim_expires_at) END;
        UPDATE ONLY braid.trigger_deliveries SET phase=CASE WHEN x.destination_kind NOT IN ('blob','sftp') THEN 'unknown' WHEN retry IS NULL THEN 'exhausted' ELSE 'pending' END,
            claim_id=NULL,claim_expires_at=NULL,next_at=coalesce(retry,x.next_at),first_ambiguous_at=coalesce(x.first_ambiguous_at,x.last_dispatch_at),
            settled_at=CASE WHEN x.destination_kind IN ('blob','sftp') AND retry IS NULL THEN statement_timestamp() END
            WHERE organization_id=org AND delivery_id=x.delivery_id;
        INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind,outcome,attempt)
            SELECT org,y.trigger_id,x.work_id,x.delivery_id,'deliveryExpired','unknown',x.sends FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.work_id=x.work_id;
    END LOOP;
    SELECT * INTO x FROM ONLY braid.trigger_deliveries y WHERE y.organization_id=org AND y.target_id=target AND y.destination_binding_id=destination
        AND y.phase='pending' AND y.next_at<=clock_timestamp() ORDER BY y.next_at,y.delivery_id LIMIT 1;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 1,NULL::uuid,NULL::uuid,NULL::integer,NULL::timestamptz,NULL::uuid,NULL::timestamptz,NULL::text,NULL::text,NULL::bytea,NULL::text,NULL::text,NULL::bigint,NULL::text; RETURN;
    END IF;
    -- Lock order: target, grant, work and control, then the delivery.
    SELECT * INTO w FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.work_id=x.work_id;
    SELECT * INTO g FROM ONLY braid.invocation_grants y WHERE y.organization_id=org AND y.grant_id=w.grant_id FOR SHARE;
    SELECT * INTO c FROM ONLY braid.trigger_controls y WHERE y.organization_id=org AND y.trigger_id=w.trigger_id FOR SHARE;
    SELECT * INTO w FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.work_id=x.work_id FOR SHARE;
    SELECT * INTO x FROM ONLY braid.trigger_deliveries y WHERE y.organization_id=org AND y.delivery_id=x.delivery_id FOR UPDATE;
    SELECT convert_from(f.definition,'UTF8')::jsonb->'policies' INTO p FROM ONLY braid.trigger_configurations f
        WHERE f.organization_id=org AND f.trigger_id=w.trigger_id AND f.version=w.configuration_version;
    SELECT y.body INTO body FROM ONLY braid.trigger_payloads y WHERE y.organization_id=org AND y.receipt_id=x.receipt_id;
    settle:=braid.trigger_work_fence(w,t,g.revoked_at IS NOT NULL,c);
    -- The stored publisher's current project role; the unbound worker session cannot use build_authorized.
    IF settle IS NULL AND (NOT EXISTS(SELECT 1 FROM ONLY braid.principals pa
            JOIN ONLY braid.organization_memberships o ON o.principal_id=pa.id AND o.organization_id=org AND o.active
            JOIN ONLY braid.project_memberships m ON m.principal_id=pa.id AND m.organization_id=org AND m.project_id=w.project_id AND m.active
            JOIN ONLY braid.projects pr ON pr.organization_id=org AND pr.id=w.project_id
            WHERE pa.id=w.publisher_id AND pa.active AND m.role IN ('publisher','administrator'))
        OR EXISTS(SELECT 1 FROM ONLY braid.build_approvals a WHERE a.organization_id=org AND a.build_id=w.build_id AND a.revoked)) THEN settle:='fenced'; END IF;
    IF settle IS NULL AND body IS NULL THEN settle:='payloadUnavailable'; END IF;
    ends:=clock_timestamp()+((p->>'attemptWindowMs')::bigint*interval '1 millisecond');
    IF settle IS NULL AND (x.sends>=(p->>'totalSends')::integer OR ends>x.admitted_at+((p->>'maximumElapsedMs')::bigint*interval '1 millisecond')) THEN settle:='exhausted'; END IF;
    IF settle IS NOT NULL THEN
        UPDATE ONLY braid.trigger_deliveries SET phase=settle,settled_at=statement_timestamp() WHERE organization_id=org AND delivery_id=x.delivery_id;
        INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind,outcome) VALUES(org,w.trigger_id,w.work_id,x.delivery_id,'deliverySettled',settle);
        RETURN QUERY SELECT 2,x.delivery_id,NULL::uuid,NULL::integer,NULL::timestamptz,NULL::uuid,NULL::timestamptz,NULL::text,NULL::text,NULL::bytea,NULL::text,NULL::text,NULL::bigint,NULL::text; RETURN;
    END IF;
    UPDATE ONLY braid.trigger_deliveries SET phase='sending',sends=sends+1,claim_id=fresh,claim_expires_at=ends,last_dispatch_at=clock_timestamp()
        WHERE organization_id=org AND delivery_id=x.delivery_id;
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind,attempt) VALUES(org,w.trigger_id,w.work_id,x.delivery_id,'deliveryClaimed',x.sends+1);
    RETURN QUERY SELECT 0,x.delivery_id,fresh,x.sends+1,ends,x.receipt_id,x.admitted_at,o.media_type,o.digest,body,x.destination_kind,x.connector_id,x.connector_version,
        x.output_name FROM ONLY braid.trigger_receipts o WHERE o.organization_id=org AND o.receipt_id=x.receipt_id;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_trigger_delivery(uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.claim_trigger_delivery(uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_trigger_worker;

-- Result: 0 recorded against the live claim, 1 late (retained as history only), 3 invalid.
-- An ambiguous HTTPS or topic outcome is held for manual resolution and never retried here.
CREATE FUNCTION braid.observe_trigger_delivery(org uuid, delivery uuid, claim uuid, observation text) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE x braid.trigger_deliveries%ROWTYPE; w braid.trigger_work%ROWTYPE; p jsonb; retry timestamp with time zone; phase_after text;
BEGIN
    PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||org::text));
    IF session_user<>'braid_trigger_worker' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR delivery IS NULL OR claim IS NULL OR observation IS NULL OR observation NOT IN ('accepted','knownNotAccepted','unknown','permanentFailure') THEN RETURN 3; END IF;
    SELECT * INTO x FROM ONLY braid.trigger_deliveries y WHERE y.organization_id=org AND y.delivery_id=delivery FOR UPDATE;
    IF NOT FOUND THEN RETURN 3; END IF;
    SELECT * INTO w FROM ONLY braid.trigger_work y WHERE y.organization_id=org AND y.work_id=x.work_id;
    IF x.phase<>'sending' OR x.claim_id IS DISTINCT FROM claim OR x.claim_expires_at<=clock_timestamp() THEN
        INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind,outcome) VALUES(org,w.trigger_id,w.work_id,delivery,'deliveryLate',observation);
        RETURN 1;
    END IF;
    SELECT convert_from(f.definition,'UTF8')::jsonb->'policies' INTO p FROM ONLY braid.trigger_configurations f
        WHERE f.organization_id=org AND f.trigger_id=w.trigger_id AND f.version=w.configuration_version;
    IF observation='knownNotAccepted' OR (observation='unknown' AND x.destination_kind IN ('blob','sftp')) THEN
        retry:=braid.trigger_retry_at(p,x.sends,x.admitted_at,clock_timestamp());
        phase_after:=CASE WHEN retry IS NULL THEN 'exhausted' ELSE 'pending' END;
    ELSE
        phase_after:=observation;
    END IF;
    UPDATE ONLY braid.trigger_deliveries SET phase=phase_after,claim_id=NULL,claim_expires_at=NULL,next_at=coalesce(retry,x.next_at),
        first_ambiguous_at=CASE WHEN observation='unknown' THEN coalesce(x.first_ambiguous_at,x.last_dispatch_at) ELSE x.first_ambiguous_at END,
        settled_at=CASE WHEN phase_after IN ('accepted','permanentFailure','exhausted') THEN statement_timestamp() END
        WHERE organization_id=org AND delivery_id=delivery;
    INSERT INTO braid.trigger_events(organization_id,trigger_id,work_id,delivery_id,kind,outcome,attempt) VALUES(org,w.trigger_id,w.work_id,delivery,'deliveryObserved',observation,x.sends);
    RETURN 0;
END;
$body$;
REVOKE ALL ON FUNCTION braid.observe_trigger_delivery(uuid,uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.observe_trigger_delivery(uuid,uuid,uuid,text) TO braid_trigger_worker;
GRANT USAGE ON SCHEMA braid TO braid_trigger_worker;

CREATE OR REPLACE FUNCTION braid.guard_host_order() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user NOT IN ('braid_invocation_writer','braid_trigger_worker') OR current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR (TG_TABLE_NAME='host_order_pins' AND TG_OP<>'INSERT') THEN
        RAISE EXCEPTION 'Host order mutation refused.' USING ERRCODE='42501';
    END IF;
    IF session_user='braid_trigger_worker' AND NOT EXISTS (SELECT 1 FROM ONLY braid.trigger_work w
        WHERE w.organization_id=NEW.organization_id AND w.target_id=NEW.target_id AND w.provider=NEW.provider
            AND w.tenant_id=NEW.tenant_id AND w.object_id=NEW.object_id AND w.request_id=NEW.request_id AND w.phase='admitted') THEN
        RAISE EXCEPTION 'Trigger order missing.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
