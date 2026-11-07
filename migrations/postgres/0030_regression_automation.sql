-- Project automation and a transactional, metadata-only notification outbox.
-- Existing narrow run roles serve the API/dispatcher; no role receives table access.
CREATE TABLE weft.automation_projects (
    organization_id uuid NOT NULL, project_id uuid NOT NULL,
    require_regression boolean NOT NULL DEFAULT false,
    error_percent integer NOT NULL DEFAULT 20 CHECK(error_percent BETWEEN 1 AND 100),
    minimum_requests integer NOT NULL DEFAULT 10 CHECK(minimum_requests BETWEEN 1 AND 10000),
    window_seconds integer NOT NULL DEFAULT 300 CHECK(window_seconds BETWEEN 60 AND 3600),
    PRIMARY KEY(organization_id,project_id),
    FOREIGN KEY(organization_id,project_id) REFERENCES weft.projects(organization_id,id)
);
CREATE TABLE weft.automation_mappings (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, enabled boolean NOT NULL,
    PRIMARY KEY(organization_id,project_id,mapping_id),
    FOREIGN KEY(organization_id,project_id,mapping_id) REFERENCES weft.mappings(organization_id,project_id,id)
);
CREATE TABLE weft.automation_suites (
    organization_id uuid NOT NULL, suite_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, revision_id uuid NOT NULL,
    actor_id uuid NOT NULL REFERENCES weft.principals(id), native_digest text NOT NULL,
    state text NOT NULL CHECK(state IN ('queued','passed','failed','noCases','disabled')),
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,suite_id),
    FOREIGN KEY(organization_id,project_id,mapping_id,revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id)
);
CREATE TABLE weft.automation_evaluations (
    organization_id uuid NOT NULL, suite_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    case_id uuid NOT NULL, version bigint NOT NULL, evaluation_id uuid NOT NULL DEFAULT gen_random_uuid(),
    state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','passed','failed','revoked','exhausted')),
    attempt integer NOT NULL DEFAULT 0 CHECK(attempt BETWEEN 0 AND 3),
    token uuid, lease_expires timestamp with time zone, eligible_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    result_body bytea, result_digest text,
    PRIMARY KEY(organization_id,suite_id,case_id), UNIQUE(evaluation_id),
    FOREIGN KEY(organization_id,suite_id) REFERENCES weft.automation_suites(organization_id,suite_id),
    FOREIGN KEY(organization_id,project_id,mapping_id,case_id,version) REFERENCES weft.regression_case_versions(organization_id,project_id,mapping_id,case_id,version),
    CHECK((state='running')=(token IS NOT NULL AND lease_expires IS NOT NULL)),
    CHECK((state IN ('passed','failed'))=(result_body IS NOT NULL)),
    CHECK((result_body IS NULL)=(result_digest IS NULL)),
    CHECK(result_body IS NULL OR (octet_length(result_body) BETWEEN 1 AND 12582912 AND encode(sha256(result_body),'hex')=result_digest))
);
CREATE TABLE weft.notification_subscriptions (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, principal_id uuid NOT NULL,
    events text[] NOT NULL, in_app boolean NOT NULL, email boolean NOT NULL, webhooks text[] NOT NULL,
    PRIMARY KEY(organization_id,project_id,principal_id),
    FOREIGN KEY(organization_id,project_id,principal_id) REFERENCES weft.project_memberships(organization_id,project_id,principal_id),
    CHECK(cardinality(events)<=9 AND array_position(events,NULL) IS NULL AND events <@ ARRAY['revisionUploaded','regressionFailed','regressionRecovered','buildQualified','buildFailed','releaseActivated','releaseRolledBack','endpointErrorRate','invitation']::text[]),
    CHECK(cardinality(webhooks)<=16 AND array_position(webhooks,NULL) IS NULL)
);
CREATE TABLE weft.notification_events (
    organization_id uuid NOT NULL, event_id uuid NOT NULL DEFAULT gen_random_uuid(), project_id uuid NOT NULL,
    kind text NOT NULL CHECK(kind IN ('revisionUploaded','regressionFailed','regressionRecovered','buildQualified','buildFailed','releaseActivated','releaseRolledBack','endpointErrorRate','invitation')),
    mapping_id uuid, revision_id uuid, resource_id uuid NOT NULL, recipient_id uuid,
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,event_id),
    FOREIGN KEY(organization_id,project_id) REFERENCES weft.projects(organization_id,id)
);
CREATE TABLE weft.notifications (
    organization_id uuid NOT NULL, event_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES weft.principals(id),
    read_at timestamp with time zone,
    PRIMARY KEY(organization_id,event_id,principal_id),
    FOREIGN KEY(organization_id,event_id) REFERENCES weft.notification_events(organization_id,event_id)
);
CREATE TABLE weft.notification_destinations (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, destination text NOT NULL,
    failures integer NOT NULL DEFAULT 0 CHECK(failures BETWEEN 0 AND 10), disabled boolean NOT NULL DEFAULT false,
    PRIMARY KEY(organization_id,project_id,destination)
);
CREATE TABLE weft.notification_deliveries (
    organization_id uuid NOT NULL, delivery_id uuid NOT NULL DEFAULT gen_random_uuid(), event_id uuid NOT NULL,
    project_id uuid NOT NULL, principal_id uuid NOT NULL REFERENCES weft.principals(id), destination text NOT NULL,
    state text NOT NULL DEFAULT 'queued' CHECK(state IN ('queued','running','delivered','failed','disabled','revoked')),
    attempt integer NOT NULL DEFAULT 0 CHECK(attempt BETWEEN 0 AND 5), token uuid, lease_expires timestamp with time zone,
    eligible_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,delivery_id), UNIQUE(organization_id,event_id,principal_id,destination),
    FOREIGN KEY(organization_id,event_id) REFERENCES weft.notification_events(organization_id,event_id),
    CHECK((state='running')=(token IS NOT NULL AND lease_expires IS NOT NULL))
);
CREATE TABLE weft.notification_delivery_attempts (
    organization_id uuid NOT NULL, delivery_id uuid NOT NULL, attempt integer NOT NULL CHECK(attempt BETWEEN 1 AND 5),
    outcome text NOT NULL CHECK(outcome IN ('delivered','unavailable','refused','transportFailure','leaseExpired')),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,delivery_id,attempt),
    FOREIGN KEY(organization_id,delivery_id) REFERENCES weft.notification_deliveries(organization_id,delivery_id)
);
CREATE TABLE weft.notification_endpoint_windows (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, project_id uuid NOT NULL,
    window_start timestamp with time zone NOT NULL, total bigint NOT NULL, errors bigint NOT NULL, crossed boolean NOT NULL,
    CHECK(total>=0 AND errors>=0 AND errors<=total),
    PRIMARY KEY(organization_id,target_id)
);
CREATE TABLE weft.organization_notifications (
    organization_id uuid NOT NULL, event_id uuid NOT NULL DEFAULT gen_random_uuid(),
    principal_id uuid NOT NULL REFERENCES weft.principals(id), invitation_id uuid NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp(), read_at timestamp with time zone,
    PRIMARY KEY(organization_id,event_id), UNIQUE(organization_id,invitation_id,principal_id),
    FOREIGN KEY(organization_id,invitation_id) REFERENCES weft.organization_invitations(organization_id,invitation_id)
);
CREATE INDEX automation_evaluation_ready ON weft.automation_evaluations(eligible_at) WHERE state IN ('queued','running');
CREATE INDEX notification_delivery_ready ON weft.notification_deliveries(eligible_at) WHERE state IN ('queued','running');
CREATE INDEX notifications_recipient ON weft.notifications(organization_id,principal_id,event_id);
CREATE UNIQUE INDEX notification_once_per_resource ON weft.notification_events(organization_id,project_id,kind,mapping_id,resource_id)
    NULLS NOT DISTINCT WHERE kind<>'endpointErrorRate';

DO $tables$
DECLARE n text;
BEGIN
    FOREACH n IN ARRAY ARRAY['automation_projects','automation_mappings','automation_suites','automation_evaluations',
        'notification_subscriptions','notification_events','notifications','notification_destinations','notification_deliveries',
        'notification_delivery_attempts','notification_endpoint_windows','organization_notifications'] LOOP
        EXECUTE format('ALTER TABLE weft.%I ENABLE ROW LEVEL SECURITY',n);
        EXECUTE format('CREATE POLICY organization_scope ON weft.%I USING(organization_id=NULLIF(current_setting(''weft.organization_id'',true),'''')::uuid)',n);
        EXECUTE format('REVOKE ALL ON weft.%I FROM PUBLIC',n);
    END LOOP;
END;
$tables$;

CREATE FUNCTION weft.automation_record_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF current_user<>'weft_migrator' OR TG_OP IN ('DELETE','TRUNCATE') THEN
        RAISE EXCEPTION 'Automation history is retained.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='UPDATE' THEN
        IF TG_TABLE_NAME IN ('notification_events','notification_delivery_attempts') THEN
            RAISE EXCEPTION 'Notification history is immutable.' USING ERRCODE='42501';
        ELSIF TG_TABLE_NAME='automation_suites' THEN
            IF OLD.state<>'queued' OR NEW.state NOT IN ('passed','failed') OR
                (to_jsonb(NEW)-'state') IS DISTINCT FROM (to_jsonb(OLD)-'state') THEN
                RAISE EXCEPTION 'Suite provenance is immutable.' USING ERRCODE='23514';
            END IF;
        ELSIF TG_TABLE_NAME='automation_evaluations' THEN
            IF OLD.state NOT IN ('queued','running') OR NEW.attempt<OLD.attempt OR NEW.attempt>OLD.attempt+1 OR
                ROW(NEW.organization_id,NEW.suite_id,NEW.project_id,NEW.mapping_id,NEW.case_id,NEW.version,NEW.evaluation_id)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.suite_id,OLD.project_id,OLD.mapping_id,OLD.case_id,OLD.version,OLD.evaluation_id) THEN
                RAISE EXCEPTION 'Case evaluation provenance and terminal result are immutable.' USING ERRCODE='23514';
            END IF;
        ELSIF TG_TABLE_NAME='organization_notifications' THEN
            IF ROW(NEW.organization_id,NEW.event_id,NEW.principal_id,NEW.invitation_id,NEW.created_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.event_id,OLD.principal_id,OLD.invitation_id,OLD.created_at)
                OR NEW.read_at IS NULL OR (OLD.read_at IS NOT NULL AND NEW.read_at IS DISTINCT FROM OLD.read_at) THEN
                RAISE EXCEPTION 'Organization notification provenance is immutable.' USING ERRCODE='23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_suite_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.automation_suites FOR EACH ROW EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_suite_no_truncate BEFORE TRUNCATE ON weft.automation_suites FOR EACH STATEMENT EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_evaluation_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.automation_evaluations FOR EACH ROW EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_evaluation_no_truncate BEFORE TRUNCATE ON weft.automation_evaluations FOR EACH STATEMENT EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_event_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.notification_events FOR EACH ROW EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_event_no_truncate BEFORE TRUNCATE ON weft.notification_events FOR EACH STATEMENT EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_delivery_attempt_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.notification_delivery_attempts FOR EACH ROW EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_delivery_attempt_no_truncate BEFORE TRUNCATE ON weft.notification_delivery_attempts FOR EACH STATEMENT EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_organization_notification_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.organization_notifications FOR EACH ROW EXECUTE FUNCTION weft.automation_record_guard();
CREATE TRIGGER automation_organization_notification_no_truncate BEFORE TRUNCATE ON weft.organization_notifications FOR EACH STATEMENT EXECUTE FUNCTION weft.automation_record_guard();

CREATE FUNCTION weft.automation_authorized(org uuid, project uuid, actor uuid, admin boolean) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND EXISTS(
        SELECT 1 FROM ONLY weft.principals p JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id
        JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=o.organization_id
        WHERE p.id=actor AND p.active AND o.organization_id=org AND o.active AND m.project_id=project AND m.active
            AND (NOT admin OR m.role='administrator'))
$body$;

CREATE FUNCTION weft.notification_emit(org uuid, project uuid, event_kind text, mapping uuid, revision uuid, resource uuid, recipient uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE eid uuid; member record; sub weft.notification_subscriptions%ROWTYPE; dest text;
BEGIN
    INSERT INTO weft.notification_events(organization_id,project_id,kind,mapping_id,revision_id,resource_id,recipient_id)
        VALUES(org,project,event_kind,mapping,revision,resource,recipient) ON CONFLICT DO NOTHING RETURNING event_id INTO eid;
    IF eid IS NULL THEN RETURN; END IF;
    FOR member IN SELECT m.principal_id FROM ONLY weft.project_memberships m
        JOIN ONLY weft.organization_memberships o ON o.organization_id=m.organization_id AND o.principal_id=m.principal_id AND o.active
        JOIN ONLY weft.principals p ON p.id=m.principal_id AND p.active
        WHERE m.organization_id=org AND m.project_id=project AND m.active AND (recipient IS NULL OR recipient=m.principal_id)
            AND EXISTS(SELECT 1 FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=org AND s.project_id=project AND s.principal_id=m.principal_id)
        ORDER BY m.principal_id LIMIT 1000 LOOP
        SELECT * INTO sub FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=org AND s.project_id=project AND s.principal_id=member.principal_id;
        IF FOUND AND NOT event_kind=ANY(sub.events) THEN CONTINUE; END IF;
        IF NOT FOUND OR sub.in_app THEN
            INSERT INTO weft.notifications VALUES(org,eid,member.principal_id,NULL) ON CONFLICT DO NOTHING;
        END IF;
        IF sub.email THEN
            INSERT INTO weft.notification_deliveries(organization_id,event_id,project_id,principal_id,destination)
                VALUES(org,eid,project,member.principal_id,'email:'||member.principal_id::text) ON CONFLICT DO NOTHING;
        END IF;
        FOREACH dest IN ARRAY coalesce(sub.webhooks,ARRAY[]::text[]) LOOP
            INSERT INTO weft.notification_deliveries(organization_id,event_id,project_id,principal_id,destination)
                VALUES(org,eid,project,member.principal_id,'webhook:'||dest) ON CONFLICT DO NOTHING;
        END LOOP;
    END LOOP;
    -- A known invited principal may not yet have a project membership. The invitation itself is authority
    -- only for this metadata notification, never for project reads or external delivery.
    IF recipient IS NOT NULL AND EXISTS(SELECT 1 FROM ONLY weft.principals p WHERE p.id=recipient AND p.active) THEN
        INSERT INTO weft.notifications VALUES(org,eid,recipient,NULL) ON CONFLICT DO NOTHING;
    END IF;
END;
$body$;

CREATE FUNCTION weft.automation_organization_authorized(org uuid, actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid AND EXISTS(
        SELECT 1 FROM ONLY weft.principals p JOIN ONLY weft.organization_memberships m ON m.principal_id=p.id
        WHERE p.id=actor AND p.active AND m.organization_id=org AND m.active)
$body$;

CREATE FUNCTION weft.automation_invitation_event() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE recipient uuid; entry jsonb;
BEGIN
    IF TG_OP='INSERT' THEN
        INSERT INTO weft.organization_notifications(organization_id,principal_id,invitation_id)
            VALUES(NEW.organization_id,NEW.invited_by,NEW.invitation_id) ON CONFLICT DO NOTHING;
        -- Project delivery remains explicitly subscribed. Org-only invitations never create external delivery.
        FOR entry IN SELECT value FROM jsonb_array_elements(NEW.project_roles) LOOP
            PERFORM weft.notification_emit(NEW.organization_id,(entry->>'projectId')::uuid,'invitation',NULL,NULL,NEW.invitation_id,NULL);
        END LOOP;
        -- Identity is resolved from the verified binding, never from an email address or caller display metadata.
        IF NEW.expected_object_id IS NOT NULL THEN
            SELECT b.principal_id INTO recipient FROM ONLY weft.external_identity_bindings b
                JOIN ONLY weft.principals p ON p.id=b.principal_id AND p.active
                WHERE b.provider='entra.oid' AND b.authority=NEW.tenant_id::text AND b.subject=NEW.expected_object_id::text AND b.enabled;
        END IF;
    ELSE
        IF NEW.redeemed_principal_id IS NOT DISTINCT FROM OLD.redeemed_principal_id THEN RETURN NEW; END IF;
        recipient:=NEW.redeemed_principal_id;
    END IF;
    IF recipient IS NOT NULL THEN
        INSERT INTO weft.organization_notifications(organization_id,principal_id,invitation_id)
            VALUES(NEW.organization_id,recipient,NEW.invitation_id) ON CONFLICT DO NOTHING;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_invitation_event AFTER INSERT OR UPDATE ON weft.organization_invitations FOR EACH ROW EXECUTE FUNCTION weft.automation_invitation_event();

CREATE FUNCTION weft.automation_inbox(org uuid, after_id uuid, maximum integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid; result jsonb;
BEGIN
    IF session_user NOT IN ('weft_run_reader','weft_run_writer') OR maximum NOT BETWEEN 1 AND 100
        OR weft.automation_organization_authorized(org,actor) IS NOT TRUE THEN RETURN NULL; END IF;
    WITH anchor AS MATERIALIZED (
        -- Retain only the caller's cursor position when access to its project changes.
        -- No hidden event content or foreign recipient's position is returned.
        SELECT e.created_at,e.event_id FROM ONLY weft.notifications n
        JOIN ONLY weft.notification_events e USING(organization_id,event_id)
        WHERE n.organization_id=org AND n.principal_id=actor AND n.event_id=after_id
        UNION ALL
        SELECT n.created_at,n.event_id FROM ONLY weft.organization_notifications n
        WHERE n.organization_id=org AND n.principal_id=actor AND n.event_id=after_id
    ), visible AS MATERIALIZED (
        SELECT e.event_id,e.kind,e.project_id,e.mapping_id,e.revision_id,e.resource_id,e.created_at,n.read_at,
            '/catalog/organizations/'||org::text||'/projects/'||e.project_id::text||CASE WHEN e.mapping_id IS NULL THEN '' ELSE '/mappings/'||e.mapping_id::text||CASE WHEN e.revision_id IS NULL THEN '' ELSE '/revisions/'||e.revision_id::text END END AS link
        FROM ONLY weft.notifications n JOIN ONLY weft.notification_events e USING(organization_id,event_id)
        WHERE n.organization_id=org AND n.principal_id=actor AND weft.automation_authorized(org,e.project_id,actor,false)
            AND NOT (e.kind='invitation' AND EXISTS(SELECT 1 FROM ONLY weft.organization_notifications o WHERE o.organization_id=org AND o.principal_id=actor AND o.invitation_id=e.resource_id))
        UNION ALL
        SELECT n.event_id,'invitation',NULL::uuid,NULL::uuid,NULL::uuid,n.invitation_id,n.created_at,n.read_at,
            CASE WHEN i.invited_by=actor THEN '/administration' ELSE '/catalog' END
        FROM ONLY weft.organization_notifications n JOIN ONLY weft.organization_invitations i USING(organization_id,invitation_id)
        WHERE n.organization_id=org AND n.principal_id=actor
    ), page AS (
        SELECT * FROM visible e WHERE after_id IS NULL OR (e.created_at,e.event_id)<(SELECT a.created_at,a.event_id FROM anchor a LIMIT 1)
        ORDER BY e.created_at DESC,e.event_id DESC LIMIT maximum
    ) SELECT jsonb_build_object('notifications',coalesce(jsonb_agg(jsonb_build_object('id',event_id,'event',kind,'projectId',project_id,'mappingId',mapping_id,
        'revisionId',revision_id,'resourceId',resource_id,'link',link,'createdAtUtc',created_at,'read',read_at IS NOT NULL) ORDER BY created_at DESC,event_id DESC),'[]'::jsonb),
        'nextId',CASE WHEN count(*)=maximum THEN (array_agg(event_id ORDER BY created_at DESC,event_id DESC))[maximum] ELSE NULL END) INTO result FROM page;
    RETURN result;
END;
$body$;

CREATE FUNCTION weft.automation_read_notification(org uuid, event uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid;
BEGIN
    IF session_user<>'weft_run_writer' OR weft.automation_organization_authorized(org,actor) IS NOT TRUE THEN RETURN false; END IF;
    UPDATE weft.organization_notifications n SET read_at=coalesce(n.read_at,statement_timestamp())
        WHERE n.organization_id=org AND n.event_id=event AND n.principal_id=actor;
    IF FOUND THEN RETURN true; END IF;
    UPDATE weft.notifications n SET read_at=coalesce(n.read_at,statement_timestamp()) FROM ONLY weft.notification_events e
        WHERE n.organization_id=org AND n.event_id=event AND n.principal_id=actor AND e.organization_id=org AND e.event_id=n.event_id
            AND weft.automation_authorized(org,e.project_id,actor,false);
    RETURN FOUND;
END;
$body$;

CREATE FUNCTION weft.automation_revision_inserted() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE enabled boolean; count_cases integer; suite_key uuid:=gen_random_uuid();
BEGIN
    PERFORM set_config('weft.organization_id',NEW.organization_id::text,true);
    -- Serialize snapshot admission with project policy changes and build gating.
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.organization_id::text||NEW.project_id::text,336));
    SELECT a.enabled INTO enabled FROM ONLY weft.automation_mappings a WHERE a.organization_id=NEW.organization_id AND a.project_id=NEW.project_id AND a.mapping_id=NEW.mapping_id;
    enabled:=coalesce(enabled,true);
    SELECT count(*) INTO count_cases FROM ONLY weft.regression_cases c WHERE c.organization_id=NEW.organization_id AND c.project_id=NEW.project_id AND c.mapping_id=NEW.mapping_id;
    INSERT INTO weft.automation_suites(organization_id,suite_id,project_id,mapping_id,revision_id,actor_id,native_digest,state)
        VALUES(NEW.organization_id,suite_key,NEW.project_id,NEW.mapping_id,NEW.id,NEW.creator_principal_id,NEW.native_digest,
            CASE WHEN NOT enabled THEN 'disabled' WHEN count_cases=0 THEN 'noCases' ELSE 'queued' END);
    IF enabled THEN
        INSERT INTO weft.automation_evaluations(organization_id,suite_id,project_id,mapping_id,case_id,version)
            SELECT NEW.organization_id,suite_key,NEW.project_id,NEW.mapping_id,c.case_id,c.head_version
            FROM ONLY weft.regression_cases c WHERE c.organization_id=NEW.organization_id AND c.project_id=NEW.project_id AND c.mapping_id=NEW.mapping_id;
    END IF;
    PERFORM weft.notification_emit(NEW.organization_id,NEW.project_id,'revisionUploaded',NEW.mapping_id,NEW.id,NEW.id,NULL);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_revision_inserted AFTER INSERT ON weft.mapping_revisions FOR EACH ROW EXECUTE FUNCTION weft.automation_revision_inserted();

CREATE FUNCTION weft.automation_case_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.organization_id::text||NEW.project_id::text,336));
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_case_guard BEFORE INSERT OR UPDATE ON weft.regression_cases FOR EACH ROW EXECUTE FUNCTION weft.automation_case_guard();

CREATE FUNCTION weft.automation_gate(org uuid, project uuid, mapping uuid, revision uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(org::text||project::text,336));
    IF NOT coalesce((SELECT a.require_regression FROM ONLY weft.automation_projects a WHERE a.organization_id=org AND a.project_id=project),false) THEN RETURN true; END IF;
    -- A pass must cover every current case at its current immutable version, on this exact revision.
    RETURN EXISTS(SELECT 1 FROM ONLY weft.automation_suites s WHERE s.organization_id=org AND s.project_id=project
        AND s.mapping_id=mapping AND s.revision_id=revision AND s.state='passed'
        AND NOT EXISTS(SELECT 1 FROM ONLY weft.automation_suites newer WHERE newer.organization_id=org AND newer.project_id=project
            AND newer.mapping_id=mapping AND newer.revision_id=revision AND (newer.created_at,newer.suite_id)>(s.created_at,s.suite_id))
        AND NOT EXISTS(SELECT 1 FROM ONLY weft.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=mapping
            AND NOT EXISTS(SELECT 1 FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id
                AND e.case_id=c.case_id AND e.version=c.head_version AND e.state='passed')))
        AND EXISTS(SELECT 1 FROM ONLY weft.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=mapping)
        ;
END;
$body$;

CREATE FUNCTION weft.automation_build_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE mapping uuid; revision uuid;
BEGIN
    IF TG_TABLE_NAME='build_approvals' THEN mapping:=NEW.mapping_id; revision:=NEW.revision_id;
    ELSE
        SELECT a.mapping_id,a.revision_id INTO mapping,revision FROM ONLY weft.build_approvals a WHERE a.organization_id=NEW.organization_id AND a.build_id=NEW.build_id;
    END IF;
    IF weft.automation_gate(NEW.organization_id,NEW.project_id,mapping,revision) IS NOT TRUE THEN
        RAISE EXCEPTION 'Exact-revision regression is required.' USING ERRCODE='23514',CONSTRAINT='automation_regression_gate';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_build_guard BEFORE INSERT ON weft.build_approvals FOR EACH ROW EXECUTE FUNCTION weft.automation_build_guard();
CREATE TRIGGER automation_qualification_guard BEFORE INSERT ON weft.build_qualifications FOR EACH ROW EXECUTE FUNCTION weft.automation_build_guard();

CREATE FUNCTION weft.automation_build_event() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE mapping uuid; revision uuid;
BEGIN
    IF TG_TABLE_NAME='build_runs' THEN
        IF NEW.state NOT IN ('failed','exhausted') OR OLD.state=NEW.state THEN RETURN NEW; END IF;
    END IF;
    SELECT a.mapping_id,a.revision_id INTO mapping,revision FROM ONLY weft.build_approvals a WHERE a.organization_id=NEW.organization_id AND a.build_id=NEW.build_id;
    PERFORM weft.notification_emit(NEW.organization_id,NEW.project_id,CASE WHEN TG_TABLE_NAME='build_runs' THEN 'buildFailed' ELSE 'buildQualified' END,
        mapping,revision,NEW.build_id,NULL);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_build_event AFTER UPDATE ON weft.build_runs FOR EACH ROW EXECUTE FUNCTION weft.automation_build_event();
CREATE TRIGGER automation_qualification_event AFTER INSERT ON weft.build_qualifications FOR EACH ROW EXECUTE FUNCTION weft.automation_build_event();

CREATE FUNCTION weft.automation_release_event() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r weft.local_releases%ROWTYPE; j jsonb;
BEGIN
    IF NEW.kind<>'activated' THEN RETURN NEW; END IF;
    SELECT * INTO r FROM ONLY weft.local_releases x WHERE x.organization_id=NEW.organization_id AND x.release_id=NEW.release_id;
    j:=convert_from(r.intent_body,'UTF8')::jsonb;
    PERFORM weft.notification_emit(r.organization_id,r.project_id,CASE WHEN j->>'action'='rollback' THEN 'releaseRolledBack' ELSE 'releaseActivated' END,
        r.mapping_id,NULL,r.release_id,NULL);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_release_event AFTER INSERT ON weft.local_release_audit FOR EACH ROW EXECUTE FUNCTION weft.automation_release_event();

CREATE FUNCTION weft.automation_endpoint_event() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE target weft.local_release_targets%ROWTYPE; policy weft.automation_projects%ROWTYPE; w weft.notification_endpoint_windows%ROWTYPE;
    failed boolean;
BEGIN
    IF NEW.kind NOT IN ('completed','failed','released') THEN RETURN NEW; END IF;
    SELECT * INTO target FROM ONLY weft.local_release_targets t WHERE t.organization_id=NEW.organization_id AND t.target_id=NEW.target_id LIMIT 1;
    IF NOT FOUND THEN RETURN NEW; END IF;
    SELECT * INTO policy FROM ONLY weft.automation_projects p WHERE p.organization_id=NEW.organization_id AND p.project_id=target.project_id;
    failed:=NEW.kind<>'completed';
    INSERT INTO weft.notification_endpoint_windows VALUES(NEW.organization_id,NEW.target_id,target.project_id,statement_timestamp(),0,0,false) ON CONFLICT DO NOTHING;
    SELECT * INTO w FROM ONLY weft.notification_endpoint_windows x WHERE x.organization_id=NEW.organization_id AND x.target_id=NEW.target_id FOR UPDATE;
    IF w.window_start + make_interval(secs=>coalesce(policy.window_seconds,300)) <= statement_timestamp() THEN
        w.window_start:=statement_timestamp(); w.total:=0; w.errors:=0; w.crossed:=false;
    END IF;
    w.total:=w.total+1; w.errors:=w.errors+failed::integer;
    IF NOT w.crossed AND w.total>=coalesce(policy.minimum_requests,10) AND w.errors::numeric*100>=w.total::numeric*coalesce(policy.error_percent,20) THEN
        w.crossed:=true;
        PERFORM weft.notification_emit(NEW.organization_id,target.project_id,'endpointErrorRate',target.mapping_id,NULL,NEW.target_id,NULL);
    END IF;
    UPDATE weft.notification_endpoint_windows x SET window_start=w.window_start,total=w.total,errors=w.errors,crossed=w.crossed
        WHERE x.organization_id=w.organization_id AND x.target_id=w.target_id;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER automation_endpoint_event AFTER INSERT ON weft.machine_invocation_audit FOR EACH ROW EXECUTE FUNCTION weft.automation_endpoint_event();

-- JSON below is metadata only, closed by the HTTP and CLI contracts. No content, diagnostics or destination secrets.
CREATE FUNCTION weft.automation_read(org uuid, project uuid, operation text, resource uuid, after_id uuid, maximum integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid; result jsonb;
BEGIN
    IF session_user NOT IN ('weft_run_reader','weft_run_writer') OR maximum NOT BETWEEN 1 AND 100 OR
        weft.automation_authorized(org,project,actor,false) IS NOT TRUE THEN RETURN NULL; END IF;
    IF operation='policy' THEN
        SELECT jsonb_build_object('requirePassingRegression',a.require_regression,'errorRatePercent',a.error_percent,'minimumRequests',a.minimum_requests,'windowSeconds',a.window_seconds)
            INTO result FROM ONLY weft.automation_projects a WHERE a.organization_id=org AND a.project_id=project;
        RETURN coalesce(result,'{"requirePassingRegression":false,"errorRatePercent":20,"minimumRequests":10,"windowSeconds":300}'::jsonb);
    ELSIF operation='mapping' THEN
        IF NOT EXISTS(SELECT 1 FROM ONLY weft.mappings m WHERE m.organization_id=org AND m.project_id=project AND m.id=resource) THEN RETURN NULL; END IF;
        RETURN jsonb_build_object('enabled',coalesce((SELECT a.enabled FROM ONLY weft.automation_mappings a WHERE a.organization_id=org AND a.project_id=project AND a.mapping_id=resource),true));
    ELSIF operation='subscription' THEN
        SELECT jsonb_build_object('events',s.events,'inApp',s.in_app,'email',s.email,'webhooks',s.webhooks) INTO result
            FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=org AND s.project_id=project AND s.principal_id=actor;
        RETURN coalesce(result,'{"events":[],"inApp":false,"email":false,"webhooks":[]}'::jsonb);
    ELSIF operation IN ('suites','suite') THEN
        SELECT jsonb_build_object('suites',coalesce(jsonb_agg(row_data ORDER BY created_at DESC,suite_id DESC),'[]'::jsonb),'nextSuiteId',CASE WHEN count(*)=maximum THEN (array_agg(suite_id ORDER BY created_at DESC,suite_id DESC))[maximum] ELSE NULL END) INTO result FROM (
            SELECT s.suite_id,s.created_at,jsonb_build_object('suiteId',s.suite_id,'revisionId',s.revision_id,'state',s.state,'createdAtUtc',s.created_at,
                'totalCases',(SELECT count(*) FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id),
                'passedCases',(SELECT count(*) FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id AND e.state='passed'),
                'failedCases',(SELECT count(*) FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id AND e.state IN ('failed','revoked','exhausted')),
                'pendingCases',(SELECT count(*) FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id AND e.state IN ('queued','running'))) row_data
            FROM ONLY weft.automation_suites s WHERE s.organization_id=org AND s.project_id=project
                AND (operation='suite' AND s.revision_id=resource OR operation='suites' AND s.mapping_id=resource)
                AND (after_id IS NULL OR (s.created_at,s.suite_id)<(SELECT a.created_at,a.suite_id FROM ONLY weft.automation_suites a WHERE a.organization_id=org AND a.project_id=project AND a.mapping_id=resource AND a.suite_id=after_id))
            ORDER BY s.created_at DESC,s.suite_id DESC LIMIT CASE WHEN operation='suite' THEN 1 ELSE maximum END) page;
        IF operation='suite' THEN RETURN result->'suites'->0; END IF;
        RETURN result;
    ELSIF operation='evaluations' THEN
        SELECT jsonb_build_object('cases',coalesce(jsonb_agg(row_data ORDER BY case_id),'[]'::jsonb),'nextCaseId',CASE WHEN count(*)=maximum THEN max(case_id::text) ELSE NULL END) INTO result FROM (
            SELECT e.case_id,jsonb_build_object('caseId',e.case_id,'version',e.version::text,'originalRevisionId',c.revision_id,'state',e.state,'attempts',e.attempt) row_data
            FROM ONLY weft.automation_evaluations e JOIN ONLY weft.regression_case_versions c ON c.organization_id=e.organization_id AND c.project_id=e.project_id AND c.mapping_id=e.mapping_id AND c.case_id=e.case_id AND c.version=e.version
            WHERE e.organization_id=org AND e.project_id=project AND e.suite_id=resource AND (after_id IS NULL OR e.case_id>after_id)
            ORDER BY e.case_id LIMIT maximum) page;
        RETURN result;
    ELSIF operation='notifications' THEN
        SELECT jsonb_build_object('notifications',coalesce(jsonb_agg(row_data ORDER BY created_at DESC,event_id DESC),'[]'::jsonb),'nextId',CASE WHEN count(*)=maximum THEN (array_agg(event_id ORDER BY created_at DESC,event_id DESC))[maximum] ELSE NULL END) INTO result FROM (
            SELECT e.event_id,e.created_at,jsonb_build_object('id',e.event_id,'event',e.kind,'projectId',e.project_id,'mappingId',e.mapping_id,'revisionId',e.revision_id,
                'resourceId',e.resource_id,'link','/catalog/organizations/'||org::text||'/projects/'||project::text||CASE WHEN e.mapping_id IS NULL THEN '' ELSE '/mappings/'||e.mapping_id::text||CASE WHEN e.revision_id IS NULL THEN '' ELSE '/revisions/'||e.revision_id::text END END,
                'createdAtUtc',e.created_at,'read',n.read_at IS NOT NULL) row_data
            FROM ONLY weft.notifications n JOIN ONLY weft.notification_events e USING(organization_id,event_id)
            WHERE n.organization_id=org AND n.principal_id=actor AND e.project_id=project AND (after_id IS NULL OR (e.created_at,e.event_id)<(SELECT a.created_at,a.event_id FROM ONLY weft.notification_events a JOIN ONLY weft.notifications an USING(organization_id,event_id) WHERE a.organization_id=org AND a.project_id=project AND a.event_id=after_id AND an.principal_id=actor))
            ORDER BY e.created_at DESC,e.event_id DESC LIMIT maximum) page;
        RETURN result;
    ELSIF operation IN ('deliveries','destinations') THEN
        IF weft.automation_authorized(org,project,actor,true) IS NOT TRUE THEN RETURN NULL; END IF;
        IF operation='destinations' THEN
            RETURN jsonb_build_object('destinations',(SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.destination,'channel',split_part(d.destination,':',1),'enabled',NOT d.disabled) ORDER BY d.destination),'[]'::jsonb)
                FROM ONLY weft.notification_destinations d WHERE d.organization_id=org AND d.project_id=project));
        END IF;
        SELECT jsonb_build_object('deliveries',coalesce(jsonb_agg(row_data ORDER BY created_at DESC,delivery_id DESC),'[]'::jsonb),'nextId',CASE WHEN count(*)=maximum THEN (array_agg(delivery_id ORDER BY created_at DESC,delivery_id DESC))[maximum] ELSE NULL END) INTO result FROM (
            SELECT d.delivery_id,e.created_at,jsonb_build_object('id',d.delivery_id,'event',e.kind,'resourceId',e.resource_id,'destinationId',d.destination,'state',d.state,'attempts',d.attempt,
                'nextAttemptAtUtc',CASE WHEN d.state='queued' THEN d.eligible_at ELSE NULL END,'history',(SELECT coalesce(jsonb_agg(jsonb_build_object('attempt',a.attempt,'outcome',a.outcome,'recordedAtUtc',a.recorded_at) ORDER BY a.attempt),'[]'::jsonb)
                    FROM ONLY weft.notification_delivery_attempts a WHERE a.organization_id=org AND a.delivery_id=d.delivery_id)) row_data
            FROM ONLY weft.notification_deliveries d JOIN ONLY weft.notification_events e USING(organization_id,event_id)
            WHERE d.organization_id=org AND d.project_id=project AND (after_id IS NULL OR (e.created_at,d.delivery_id)<(SELECT ae.created_at,a.delivery_id FROM ONLY weft.notification_deliveries a JOIN ONLY weft.notification_events ae USING(organization_id,event_id) WHERE a.organization_id=org AND a.project_id=project AND a.delivery_id=after_id)) ORDER BY e.created_at DESC,d.delivery_id DESC LIMIT maximum) page;
        RETURN result;
    END IF;
    RETURN NULL;
END;
$body$;

CREATE FUNCTION weft.automation_write(org uuid, project uuid, operation text, resource uuid, body jsonb) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('weft.run_principal_id',true),'')::uuid; events text[]; hooks text[]; revision weft.mapping_revisions%ROWTYPE; existing weft.automation_suites%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR weft.automation_authorized(org,project,actor,false) IS NOT TRUE OR octet_length(body::text)>8192 THEN RETURN false; END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended(org::text||project::text,336));
    IF operation='recheck' THEN
        IF weft.regression_run_authorized(org,project,actor) IS NOT TRUE OR resource IS NULL OR resource='00000000-0000-0000-0000-000000000000' THEN RETURN false; END IF;
        -- The pending quota spans projects, so all recheck admission for this actor shares a lock.
        PERFORM pg_advisory_xact_lock(hashtextextended(org::text||actor::text,337));
        -- Waiting for either admission lock may outlive membership or role authority.
        IF weft.regression_run_authorized(org,project,actor) IS NOT TRUE THEN RETURN false; END IF;
        SELECT * INTO revision FROM ONLY weft.mapping_revisions r WHERE r.organization_id=org AND r.project_id=project AND r.mapping_id=(body->>'mappingId')::uuid AND r.id=(body->>'revisionId')::uuid;
        IF NOT FOUND THEN RETURN false; END IF;
        SELECT * INTO existing FROM ONLY weft.automation_suites s WHERE s.organization_id=org AND s.suite_id=resource;
        IF FOUND THEN RETURN existing.project_id=project AND existing.mapping_id=revision.mapping_id AND existing.revision_id=revision.id AND existing.actor_id=actor; END IF;
        IF (SELECT count(*) FROM ONLY weft.automation_suites s WHERE s.organization_id=org AND s.actor_id=actor AND s.state='queued')>=32 THEN RETURN false; END IF;
        INSERT INTO weft.automation_suites(organization_id,suite_id,project_id,mapping_id,revision_id,actor_id,native_digest,state)
            VALUES(org,resource,project,revision.mapping_id,revision.id,actor,revision.native_digest,
                CASE WHEN EXISTS(SELECT 1 FROM ONLY weft.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=revision.mapping_id) THEN 'queued' ELSE 'noCases' END);
        INSERT INTO weft.automation_evaluations(organization_id,suite_id,project_id,mapping_id,case_id,version)
            SELECT org,resource,project,revision.mapping_id,c.case_id,c.head_version FROM ONLY weft.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=revision.mapping_id;
        RETURN true;
    ELSIF operation='subscription' THEN
        IF NOT EXISTS(SELECT 1 FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=org AND s.project_id=project AND s.principal_id=actor)
            AND (SELECT count(*) FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=org AND s.project_id=project)>=1000 THEN RETURN false; END IF;
        SELECT array_agg(value) INTO events FROM jsonb_array_elements_text(body->'events');
        SELECT array_agg(value) INTO hooks FROM jsonb_array_elements_text(body->'webhooks');
        INSERT INTO weft.notification_subscriptions VALUES(org,project,actor,coalesce(events,ARRAY[]::text[]),(body->>'inApp')::boolean,(body->>'email')::boolean,coalesce(hooks,ARRAY[]::text[]))
            ON CONFLICT(organization_id,project_id,principal_id) DO UPDATE SET events=EXCLUDED.events,in_app=EXCLUDED.in_app,email=EXCLUDED.email,webhooks=EXCLUDED.webhooks;
        RETURN true;
    ELSIF operation='read' THEN
        UPDATE weft.notifications n SET read_at=coalesce(n.read_at,statement_timestamp()) FROM ONLY weft.notification_events e
            WHERE n.organization_id=org AND n.principal_id=actor AND n.event_id=resource AND e.organization_id=org AND e.event_id=n.event_id AND e.project_id=project;
        RETURN FOUND;
    END IF;
    IF weft.automation_authorized(org,project,actor,true) IS NOT TRUE THEN RETURN false; END IF;
    IF operation='policy' THEN
        INSERT INTO weft.automation_projects VALUES(org,project,(body->>'requirePassingRegression')::boolean,(body->>'errorRatePercent')::integer,(body->>'minimumRequests')::integer,(body->>'windowSeconds')::integer)
            ON CONFLICT(organization_id,project_id) DO UPDATE SET require_regression=EXCLUDED.require_regression,error_percent=EXCLUDED.error_percent,minimum_requests=EXCLUDED.minimum_requests,window_seconds=EXCLUDED.window_seconds;
        RETURN true;
    ELSIF operation='mapping' THEN
        IF NOT EXISTS(SELECT 1 FROM ONLY weft.mappings m WHERE m.organization_id=org AND m.project_id=project AND m.id=resource) THEN RETURN false; END IF;
        INSERT INTO weft.automation_mappings VALUES(org,project,resource,(body->>'enabled')::boolean)
            ON CONFLICT(organization_id,project_id,mapping_id) DO UPDATE SET enabled=EXCLUDED.enabled;
        RETURN true;
    ELSIF operation='enableDestination' THEN
        UPDATE weft.notification_destinations d SET disabled=false,failures=0 WHERE d.organization_id=org AND d.project_id=project AND d.destination=body->>'destinationId';
        RETURN FOUND;
    END IF;
    RETURN false;
END;
$body$;

CREATE FUNCTION weft.automation_claim_evaluation() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE e weft.automation_evaluations%ROWTYPE; s weft.automation_suites%ROWTYPE; c weft.regression_case_versions%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' THEN RAISE EXCEPTION 'Automation worker required.' USING ERRCODE='42501'; END IF;
    SELECT * INTO e FROM ONLY weft.automation_evaluations x WHERE x.state IN ('queued','running') AND x.eligible_at<=statement_timestamp()
        AND (x.token IS NULL OR x.lease_expires<=statement_timestamp()) ORDER BY x.eligible_at,x.evaluation_id FOR UPDATE SKIP LOCKED LIMIT 1;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO s FROM ONLY weft.automation_suites x WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id;
    PERFORM set_config('weft.organization_id',e.organization_id::text,true);
    IF e.attempt>=3 OR weft.regression_run_authorized(e.organization_id,e.project_id,s.actor_id) IS NOT TRUE THEN
        UPDATE weft.automation_evaluations x SET state=CASE WHEN e.attempt>=3 THEN 'exhausted' ELSE 'revoked' END,token=NULL,lease_expires=NULL
            WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id AND x.case_id=e.case_id;
        PERFORM weft.automation_settle_suite(e.organization_id,e.suite_id); RETURN NULL;
    END IF;
    SELECT * INTO c FROM ONLY weft.regression_case_versions x WHERE x.organization_id=e.organization_id AND x.project_id=e.project_id AND x.mapping_id=e.mapping_id AND x.case_id=e.case_id AND x.version=e.version;
    e.token:=gen_random_uuid(); e.attempt:=e.attempt+1;
    UPDATE weft.automation_evaluations x SET state='running',attempt=e.attempt,token=e.token,lease_expires=statement_timestamp()+interval '120 seconds'
        WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id AND x.case_id=e.case_id;
    RETURN jsonb_build_object('organizationId',e.organization_id,'projectId',e.project_id,'mappingId',e.mapping_id,'suiteId',e.suite_id,'caseId',e.case_id,'version',e.version,
        'actorId',s.actor_id,'originalRevisionId',c.revision_id,'creatorId',c.creator_id,'nativeDigest',c.native_digest,'caseDigest',c.content_digest,'caseBytes',c.byte_count,
        'targetDigest',s.native_digest,'targetRevisionId',s.revision_id,'token',e.token,'attempt',e.attempt,'evaluationId',e.evaluation_id);
END;
$body$;

CREATE FUNCTION weft.automation_settle_suite(org uuid, suite uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE s weft.automation_suites%ROWTYPE; previous text; result text;
BEGIN
    SELECT * INTO s FROM ONLY weft.automation_suites x WHERE x.organization_id=org AND x.suite_id=suite FOR UPDATE;
    IF s.state<>'queued' OR EXISTS(SELECT 1 FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=suite AND e.state IN ('queued','running')) THEN RETURN; END IF;
    result:=CASE WHEN EXISTS(SELECT 1 FROM ONLY weft.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=suite AND e.state<>'passed') THEN 'failed' ELSE 'passed' END;
    UPDATE weft.automation_suites x SET state=result WHERE x.organization_id=org AND x.suite_id=suite;
    SELECT x.state INTO previous FROM ONLY weft.automation_suites x JOIN ONLY weft.mapping_revisions r ON r.organization_id=x.organization_id AND r.id=x.revision_id AND r.project_id=x.project_id AND r.mapping_id=x.mapping_id
        WHERE x.organization_id=org AND x.project_id=s.project_id AND x.mapping_id=s.mapping_id AND x.suite_id<>suite AND x.state IN ('failed','passed')
        AND (r.sequence < (SELECT z.sequence FROM ONLY weft.mapping_revisions z WHERE z.organization_id=org AND z.project_id=s.project_id AND z.mapping_id=s.mapping_id AND z.id=s.revision_id)
            OR x.revision_id=s.revision_id AND x.created_at<s.created_at)
        ORDER BY r.sequence DESC,x.created_at DESC LIMIT 1;
    IF result='failed' OR (result='passed' AND previous='failed') THEN
        PERFORM weft.notification_emit(org,s.project_id,CASE WHEN result='failed' THEN 'regressionFailed' ELSE 'regressionRecovered' END,s.mapping_id,s.revision_id,suite,NULL);
    END IF;
END;
$body$;

CREATE FUNCTION weft.automation_result_passes(org uuid, suite uuid, case_key uuid, result bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE c weft.regression_case_versions%ROWTYPE; header jsonb; observed jsonb; n integer; item jsonb; baseline jsonb; content bytea;
BEGIN
    SELECT v.* INTO c FROM ONLY weft.automation_evaluations e JOIN ONLY weft.regression_case_versions v ON v.organization_id=e.organization_id
        AND v.project_id=e.project_id AND v.mapping_id=e.mapping_id AND v.case_id=e.case_id AND v.version=e.version
        WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key;
    IF NOT FOUND OR result IS NULL OR octet_length(c.envelope)<4 THEN RETURN false; END IF;
    n:=get_byte(c.envelope,0)*16777216+get_byte(c.envelope,1)*65536+get_byte(c.envelope,2)*256+get_byte(c.envelope,3);
    IF n NOT BETWEEN 1 AND 262144 THEN RETURN false; END IF;
    header:=convert_from(substring(c.envelope FROM 5 FOR n),'UTF8')::jsonb;
    observed:=convert_from(result,'UTF8')::jsonb;
    IF observed->>'schema' IS DISTINCT FROM 'weft.regression-run-result.v1' OR observed->>'runId' IS DISTINCT FROM suite::text
        OR observed->>'caseDigest' IS DISTINCT FROM c.content_digest OR observed->>'outcome' IS DISTINCT FROM '0'
        OR observed->>'previewStatus' IS DISTINCT FROM '0' OR jsonb_typeof(observed->'outputs') IS DISTINCT FROM 'array'
        OR jsonb_array_length(header->'expected')=0 OR jsonb_array_length(header->'expected')<>jsonb_array_length(observed->'outputs') THEN RETURN false; END IF;
    FOR item IN SELECT value FROM jsonb_array_elements(observed->'outputs') LOOP
        SELECT value INTO baseline FROM jsonb_array_elements(header->'expected') WHERE value->'name'=item->'name';
        IF NOT FOUND OR (item->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE THEN RETURN false; END IF;
        content:=decode(translate(item->>'content','-_','+/')||repeat('=',(4-length(item->>'content')%4)%4),'base64');
        IF octet_length(content)<>(baseline->>'byteCount')::integer OR encode(sha256(content),'hex') IS DISTINCT FROM baseline->>'sha256' THEN RETURN false; END IF;
    END LOOP;
    RETURN NOT EXISTS(SELECT 1 FROM jsonb_array_elements(header->'expected') x WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(observed->'outputs') y WHERE x->'name'=y->'name'));
EXCEPTION WHEN OTHERS THEN RETURN false;
END;
$body$;

CREATE FUNCTION weft.automation_finish_evaluation(org uuid, suite uuid, case_key uuid, claim uuid, generation integer, result bytea) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE pending weft.automation_evaluations%ROWTYPE; actor uuid;
BEGIN
    IF session_user<>'weft_run_writer' OR (result IS NOT NULL AND octet_length(result) NOT BETWEEN 1 AND 12582912) THEN RETURN false; END IF;
    SELECT * INTO pending FROM ONLY weft.automation_evaluations e
        WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key AND e.token=claim
            AND e.attempt=generation AND e.state='running' AND e.lease_expires>statement_timestamp() FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT s.actor_id INTO actor FROM ONLY weft.automation_suites s WHERE s.organization_id=org AND s.suite_id=suite;
    IF weft.regression_run_authorized(org,pending.project_id,actor) IS NOT TRUE THEN
        UPDATE weft.automation_evaluations e SET state='revoked',token=NULL,lease_expires=NULL
            WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key;
        PERFORM weft.automation_settle_suite(org,suite);
        RETURN false;
    END IF;
    UPDATE weft.automation_evaluations e SET state=CASE WHEN result IS NULL THEN 'queued' WHEN weft.automation_result_passes(org,suite,case_key,result) THEN 'passed' ELSE 'failed' END,
        token=NULL,lease_expires=NULL,result_body=result,result_digest=CASE WHEN result IS NULL THEN NULL ELSE encode(sha256(result),'hex') END,
        eligible_at=statement_timestamp()+CASE WHEN result IS NULL THEN make_interval(secs=>least(30*generation,90)) ELSE interval '0' END
        WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key AND e.token=claim AND e.attempt=generation AND e.state='running' AND e.lease_expires>statement_timestamp();
    IF NOT FOUND THEN RETURN false; END IF;
    PERFORM weft.automation_settle_suite(org,suite); RETURN true;
END;
$body$;

CREATE FUNCTION weft.automation_claim_delivery() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.notification_deliveries%ROWTYPE; e weft.notification_events%ROWTYPE; disabled boolean;
BEGIN
    IF session_user<>'weft_run_writer' THEN RAISE EXCEPTION 'Notification worker required.' USING ERRCODE='42501'; END IF;
    SELECT * INTO d FROM ONLY weft.notification_deliveries x WHERE x.state IN ('queued','running') AND x.eligible_at<=statement_timestamp()
        AND (x.token IS NULL OR x.lease_expires<=statement_timestamp()) ORDER BY x.eligible_at,x.delivery_id FOR UPDATE SKIP LOCKED LIMIT 1;
    IF NOT FOUND THEN RETURN NULL; END IF;
    PERFORM set_config('weft.organization_id',d.organization_id::text,true);
    IF d.state='running' THEN
        INSERT INTO weft.notification_delivery_attempts VALUES(d.organization_id,d.delivery_id,d.attempt,'leaseExpired',statement_timestamp()) ON CONFLICT DO NOTHING;
    END IF;
    SELECT * INTO e FROM ONLY weft.notification_events x WHERE x.organization_id=d.organization_id AND x.event_id=d.event_id;
    INSERT INTO weft.notification_destinations VALUES(d.organization_id,d.project_id,d.destination,0,false) ON CONFLICT DO NOTHING;
    SELECT x.disabled INTO disabled FROM ONLY weft.notification_destinations x WHERE x.organization_id=d.organization_id AND x.project_id=d.project_id AND x.destination=d.destination;
    IF d.attempt>=5 OR disabled OR weft.automation_authorized(d.organization_id,d.project_id,d.principal_id,false) IS NOT TRUE
        OR NOT EXISTS(SELECT 1 FROM ONLY weft.notification_subscriptions s WHERE s.organization_id=d.organization_id AND s.project_id=d.project_id AND s.principal_id=d.principal_id
            AND e.kind=ANY(s.events) AND (d.destination='email:'||s.principal_id::text AND s.email OR substring(d.destination FROM 9)=ANY(s.webhooks))) THEN
        UPDATE weft.notification_deliveries x SET state=CASE WHEN disabled THEN 'disabled' WHEN d.attempt>=5 THEN 'failed' ELSE 'revoked' END,token=NULL,lease_expires=NULL
            WHERE x.organization_id=d.organization_id AND x.delivery_id=d.delivery_id;
        RETURN NULL;
    END IF;
    d.token:=gen_random_uuid(); d.attempt:=d.attempt+1;
    UPDATE weft.notification_deliveries x SET state='running',attempt=d.attempt,token=d.token,lease_expires=statement_timestamp()+interval '60 seconds'
        WHERE x.organization_id=d.organization_id AND x.delivery_id=d.delivery_id;
    RETURN jsonb_build_object('organizationId',d.organization_id,'projectId',d.project_id,'id',d.delivery_id,'recipientId',d.principal_id,'destination',d.destination,'token',d.token,'attempt',d.attempt,
        'message',jsonb_build_object('id',e.event_id,'organizationId',e.organization_id,'projectId',e.project_id,'event',e.kind,'mappingId',e.mapping_id,'revisionId',e.revision_id,'resourceId',e.resource_id,
            'link','/catalog/organizations/'||e.organization_id::text||'/projects/'||e.project_id::text||CASE WHEN e.mapping_id IS NULL THEN '' ELSE '/mappings/'||e.mapping_id::text||CASE WHEN e.revision_id IS NULL THEN '' ELSE '/revisions/'||e.revision_id::text END END,'createdAt',e.created_at));
END;
$body$;

CREATE FUNCTION weft.automation_finish_delivery(org uuid, delivery uuid, claim uuid, generation integer, outcome text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.notification_deliveries%ROWTYPE;
BEGIN
    IF session_user<>'weft_run_writer' OR outcome NOT IN ('delivered','unavailable','refused','transportFailure') THEN RETURN false; END IF;
    SELECT * INTO d FROM ONLY weft.notification_deliveries x WHERE x.organization_id=org AND x.delivery_id=delivery AND x.token=claim AND x.attempt=generation
        AND x.state='running' AND x.lease_expires>statement_timestamp() FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    INSERT INTO weft.notification_delivery_attempts VALUES(org,delivery,generation,outcome,statement_timestamp());
    UPDATE weft.notification_destinations x SET failures=CASE WHEN outcome='delivered' THEN 0 ELSE least(x.failures+1,10) END,
        disabled=x.disabled OR (outcome<>'delivered' AND x.failures>=9) WHERE x.organization_id=org AND x.project_id=d.project_id AND x.destination=d.destination;
    UPDATE weft.notification_deliveries x SET state=CASE WHEN outcome='delivered' THEN 'delivered' WHEN outcome='refused' OR generation>=5 THEN 'failed' ELSE 'queued' END,
        token=NULL,lease_expires=NULL,eligible_at=statement_timestamp()+make_interval(secs=>least(30*(1<<(generation-1)),900))
        WHERE x.organization_id=org AND x.delivery_id=delivery;
    RETURN true;
END;
$body$;

DO $grants$
DECLARE f record;
BEGIN
    FOR f IN SELECT p.oid::regprocedure identity FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='weft' AND (p.proname LIKE 'automation_%' OR p.proname='notification_emit') LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.identity);
    END LOOP;
END;
$grants$;
GRANT EXECUTE ON FUNCTION weft.automation_read(uuid,uuid,text,uuid,uuid,integer),weft.automation_inbox(uuid,uuid,integer) TO weft_run_reader,weft_run_writer;
GRANT EXECUTE ON FUNCTION weft.automation_write(uuid,uuid,text,uuid,jsonb),weft.automation_claim_evaluation(),
    weft.automation_finish_evaluation(uuid,uuid,uuid,uuid,integer,bytea),weft.automation_claim_delivery(),
    weft.automation_finish_delivery(uuid,uuid,uuid,integer,text),weft.automation_read_notification(uuid,uuid) TO weft_run_writer;
