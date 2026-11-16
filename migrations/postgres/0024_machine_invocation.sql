-- Machine invocation grants and the invocation ledger. They are independent of browser
-- sessions and cloud credentials, and only read the local release target pointer.
DO $roles$
DECLARE n text; r record;
BEGIN
    FOREACH n IN ARRAY ARRAY['braid_invocation_grant_writer','braid_invocation_writer'] LOOP
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
            RAISE EXCEPTION 'Independent machine invocation roles required.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$roles$;

-- One grant binds one machine principal to one registered target. Revocation is one-way.
CREATE TABLE braid.invocation_grants (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    grant_id uuid NOT NULL CHECK(grant_id<>'00000000-0000-0000-0000-000000000000'),
    provider text NOT NULL CHECK(provider IN ('entra')),
    tenant_id uuid NOT NULL CHECK(tenant_id<>'00000000-0000-0000-0000-000000000000'),
    object_id uuid NOT NULL CHECK(object_id<>'00000000-0000-0000-0000-000000000000'),
    granted_by uuid NOT NULL, granted_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(granted_at)),
    revoked_by uuid, revoked_at timestamp with time zone,
    PRIMARY KEY(organization_id,grant_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK((revoked_by IS NULL)=(revoked_at IS NULL)),
    CHECK(revoked_at IS NULL OR (isfinite(revoked_at) AND revoked_at>=granted_at))
);
CREATE UNIQUE INDEX invocation_grants_active ON braid.invocation_grants(organization_id,target_id,provider,tenant_id,object_id) WHERE revoked_at IS NULL;
CREATE TABLE braid.invocation_grant_audit (
    organization_id uuid NOT NULL, grant_id uuid NOT NULL, sequence integer NOT NULL CHECK(sequence IN (1,2)),
    kind text NOT NULL CHECK(kind IN ('granted','revoked')), actor_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,grant_id,sequence),
    FOREIGN KEY(organization_id,grant_id) REFERENCES braid.invocation_grants(organization_id,grant_id),
    CHECK((sequence=1)=(kind='granted'))
);

-- One idempotent request per caller and request ID. Attempts are fenced by a claim ID and lease.
CREATE TABLE braid.machine_invocations (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    provider text NOT NULL CHECK(provider IN ('entra')), tenant_id uuid NOT NULL, object_id uuid NOT NULL,
    request_id uuid NOT NULL CHECK(request_id<>'00000000-0000-0000-0000-000000000000'),
    request_digest text NOT NULL CHECK(request_digest~'^[0-9a-f]{64}$'),
    application_id uuid NOT NULL CHECK(application_id<>'00000000-0000-0000-0000-000000000000'),
    state text NOT NULL DEFAULT 'running' CHECK(state IN ('running','retryable','completed','failed')),
    attempt integer NOT NULL DEFAULT 1 CHECK(attempt BETWEEN 1 AND 8),
    claim_id uuid NOT NULL, lease_until timestamp with time zone NOT NULL CHECK(isfinite(lease_until)),
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at)),
    finished_at timestamp with time zone,
    PRIMARY KEY(organization_id,target_id,provider,tenant_id,object_id,request_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    CHECK((state IN ('completed','failed'))=(finished_at IS NOT NULL)),
    CHECK(finished_at IS NULL OR (isfinite(finished_at) AND finished_at>=created_at))
);
-- Append-only history: claimed, then completed, failed or released for each attempt. Digests only.
CREATE TABLE braid.machine_invocation_audit (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, provider text NOT NULL, tenant_id uuid NOT NULL, object_id uuid NOT NULL,
    request_id uuid NOT NULL, sequence integer NOT NULL CHECK(sequence BETWEEN 1 AND 16), attempt integer NOT NULL CHECK(attempt BETWEEN 1 AND 8),
    kind text NOT NULL CHECK(kind IN ('claimed','completed','failed','released')),
    outcome text NOT NULL CHECK(outcome IN ('none','completed','refused','invalidInput','invalidParameter','executionFailed','resourceLimit',
        'integrityFailure','unsupported','unavailable','busy','releaseChanged','interrupted','attemptsExhausted')),
    grant_id uuid, application_id uuid NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    release_id uuid, qualification_id uuid, build_id uuid, build_intent_digest text, artifact_digest text, revision_id uuid,
    runtime_image text, clock text, output_digest text, output_bytes integer,
    PRIMARY KEY(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence),
    FOREIGN KEY(organization_id,target_id,provider,tenant_id,object_id,request_id)
        REFERENCES braid.machine_invocations(organization_id,target_id,provider,tenant_id,object_id,request_id),
    CHECK(sequence IN (2*attempt-1,2*attempt)), CHECK((sequence=2*attempt-1)=(kind='claimed')),
    CHECK((kind='claimed')=(outcome='none')), CHECK((kind='completed')=(outcome='completed')),
    CHECK(grant_id IS NOT NULL OR (kind='released' AND outcome='refused')),
    CHECK((release_id IS NULL)=(qualification_id IS NULL) AND (release_id IS NULL)=(build_id IS NULL)
        AND (release_id IS NULL)=(build_intent_digest IS NULL) AND (release_id IS NULL)=(artifact_digest IS NULL)
        AND (release_id IS NULL)=(revision_id IS NULL) AND (release_id IS NULL)=(runtime_image IS NULL)),
    CHECK(build_intent_digest IS NULL OR build_intent_digest~'^[0-9a-f]{64}$'),
    CHECK(artifact_digest IS NULL OR artifact_digest~'^[0-9a-f]{64}$'),
    CHECK(runtime_image IS NULL OR runtime_image~'^sha256:[0-9a-f]{64}$'),
    CHECK(clock IS NULL OR octet_length(clock) BETWEEN 20 AND 35),
    CHECK((kind='completed')=(release_id IS NOT NULL AND clock IS NOT NULL AND output_digest IS NOT NULL)),
    CHECK((output_digest IS NULL)=(output_bytes IS NULL)),
    CHECK(output_digest IS NULL OR (output_digest~'^[0-9a-f]{64}$' AND output_bytes BETWEEN 1 AND 4194304))
);
-- The original successful response, retained privately for replay. It cannot be read after expiry.
CREATE TABLE braid.machine_invocation_results (
    organization_id uuid NOT NULL, target_id uuid NOT NULL, provider text NOT NULL, tenant_id uuid NOT NULL, object_id uuid NOT NULL,
    request_id uuid NOT NULL, body bytea NOT NULL, body_digest text NOT NULL,
    retained_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(retained_at)),
    expires_at timestamp with time zone NOT NULL,
    PRIMARY KEY(organization_id,target_id,provider,tenant_id,object_id,request_id),
    FOREIGN KEY(organization_id,target_id,provider,tenant_id,object_id,request_id)
        REFERENCES braid.machine_invocations(organization_id,target_id,provider,tenant_id,object_id,request_id),
    CHECK(octet_length(body) BETWEEN 1 AND 4194304 AND encode(sha256(body),'hex')=body_digest),
    CHECK(expires_at=retained_at+interval '24 hours')
);
CREATE INDEX machine_invocation_results_expiry ON braid.machine_invocation_results(organization_id,target_id,expires_at);
ALTER TABLE braid.invocation_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.invocation_grant_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.machine_invocations ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.machine_invocation_audit ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.machine_invocation_results ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.invocation_grants USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.invocation_grant_audit USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.machine_invocations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.machine_invocation_audit USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.machine_invocation_results USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.invocation_grants,braid.invocation_grant_audit,braid.machine_invocations,braid.machine_invocation_audit,braid.machine_invocation_results FROM PUBLIC;

CREATE FUNCTION braid.guard_machine_invocation() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP='TRUNCATE'
        OR session_user<>(CASE WHEN TG_TABLE_NAME LIKE 'invocation_grant%' THEN 'braid_invocation_grant_writer' ELSE 'braid_invocation_writer' END) THEN
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
REVOKE ALL ON FUNCTION braid.guard_machine_invocation() FROM PUBLIC;
CREATE TRIGGER invocation_grant_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.invocation_grants FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER invocation_grant_no_truncate BEFORE TRUNCATE ON braid.invocation_grants FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER invocation_grant_audit_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.invocation_grant_audit FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER invocation_grant_audit_no_truncate BEFORE TRUNCATE ON braid.invocation_grant_audit FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.machine_invocations FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_no_truncate BEFORE TRUNCATE ON braid.machine_invocations FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_audit_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.machine_invocation_audit FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_audit_no_truncate BEFORE TRUNCATE ON braid.machine_invocation_audit FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_result_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.machine_invocation_results FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation();
CREATE TRIGGER machine_invocation_result_no_truncate BEFORE TRUNCATE ON braid.machine_invocation_results FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_machine_invocation();

CREATE FUNCTION braid.guard_machine_invocation_history() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE g braid.invocation_grants%ROWTYPE; i braid.machine_invocations%ROWTYPE; n integer; expected integer;
BEGIN
    IF TG_TABLE_NAME LIKE 'invocation_grant%' THEN
        SELECT * INTO g FROM ONLY braid.invocation_grants WHERE organization_id=NEW.organization_id AND grant_id=NEW.grant_id;
        SELECT count(*) INTO n FROM ONLY braid.invocation_grant_audit WHERE organization_id=g.organization_id AND grant_id=g.grant_id;
        IF n<>(CASE WHEN g.revoked_at IS NULL THEN 1 ELSE 2 END) OR NOT EXISTS(SELECT 1 FROM ONLY braid.invocation_grant_audit a
            WHERE a.organization_id=g.organization_id AND a.grant_id=g.grant_id AND a.sequence=1 AND a.actor_id=g.granted_by AND a.recorded_at=g.granted_at)
            OR (g.revoked_at IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.invocation_grant_audit a
                WHERE a.organization_id=g.organization_id AND a.grant_id=g.grant_id AND a.sequence=2 AND a.kind='revoked'
                    AND a.actor_id=g.revoked_by AND a.recorded_at=g.revoked_at)) THEN
            RAISE EXCEPTION 'Invocation grant audit is incomplete.' USING ERRCODE='23514';
        END IF;
        RETURN NULL;
    END IF;
    SELECT * INTO i FROM ONLY braid.machine_invocations WHERE organization_id=NEW.organization_id AND target_id=NEW.target_id AND provider=NEW.provider
        AND tenant_id=NEW.tenant_id AND object_id=NEW.object_id AND request_id=NEW.request_id;
    expected:=2*i.attempt-(CASE WHEN i.state='running' THEN 1 ELSE 0 END);
    SELECT count(*) INTO n FROM ONLY braid.machine_invocation_audit a WHERE a.organization_id=i.organization_id AND a.target_id=i.target_id
        AND a.provider=i.provider AND a.tenant_id=i.tenant_id AND a.object_id=i.object_id AND a.request_id=i.request_id;
    IF n<>expected OR NOT EXISTS(SELECT 1 FROM ONLY braid.machine_invocation_audit a WHERE a.organization_id=i.organization_id AND a.target_id=i.target_id
            AND a.provider=i.provider AND a.tenant_id=i.tenant_id AND a.object_id=i.object_id AND a.request_id=i.request_id
            AND a.sequence=expected AND a.attempt=i.attempt
            AND a.kind=(CASE i.state WHEN 'running' THEN 'claimed' WHEN 'retryable' THEN 'released' WHEN 'completed' THEN 'completed' ELSE 'failed' END)
            AND (i.state<>'running' OR a.recorded_at<=i.lease_until) AND (i.finished_at IS NULL OR a.recorded_at=i.finished_at))
        OR (i.state='completed' AND NOT EXISTS(SELECT 1 FROM ONLY braid.machine_invocation_results x WHERE x.organization_id=i.organization_id
            AND x.target_id=i.target_id AND x.provider=i.provider AND x.tenant_id=i.tenant_id AND x.object_id=i.object_id AND x.request_id=i.request_id
            AND x.retained_at=i.finished_at AND x.body_digest=(SELECT a.output_digest FROM ONLY braid.machine_invocation_audit a WHERE a.organization_id=i.organization_id
                AND a.target_id=i.target_id AND a.provider=i.provider AND a.tenant_id=i.tenant_id AND a.object_id=i.object_id AND a.request_id=i.request_id AND a.sequence=expected))) THEN
        RAISE EXCEPTION 'Machine invocation audit is incomplete.' USING ERRCODE='23514';
    END IF;
    RETURN NULL;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_machine_invocation_history() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER invocation_grant_history AFTER INSERT OR UPDATE ON braid.invocation_grants DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation_history();
CREATE CONSTRAINT TRIGGER invocation_grant_event_history AFTER INSERT ON braid.invocation_grant_audit DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation_history();
CREATE CONSTRAINT TRIGGER machine_invocation_history AFTER INSERT OR UPDATE ON braid.machine_invocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation_history();
CREATE CONSTRAINT TRIGGER machine_invocation_event_history AFTER INSERT ON braid.machine_invocation_audit DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_machine_invocation_history();

-- The active grant for one principal on an enabled target, or NULL.
CREATE FUNCTION braid.machine_invocation_grant(org uuid, target uuid, issuer text, tenant uuid, principal uuid) RETURNS uuid
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT g.grant_id FROM ONLY braid.invocation_grants g JOIN ONLY braid.local_release_targets t ON t.organization_id=g.organization_id AND t.target_id=g.target_id
    WHERE g.organization_id=org AND g.target_id=target AND g.provider=issuer AND g.tenant_id=tenant AND g.object_id=principal AND g.revoked_at IS NULL AND t.enabled
$body$;
REVOKE ALL ON FUNCTION braid.machine_invocation_grant(uuid,uuid,text,uuid,uuid) FROM PUBLIC;

-- Provenance must name the target's activated release and its exact qualified build.
CREATE FUNCTION braid.machine_invocation_provenance(org uuid, target uuid, release uuid, qualification uuid, build uuid, intent_sha text, artifact_sha text, revision uuid, image text) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY braid.local_releases r
        JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id AND q.actor_id=r.actor_id
        JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id AND b.actor_id=q.actor_id
        WHERE r.organization_id=org AND r.target_id=target AND r.release_id=release AND r.phase='active' AND r.qualification_id=qualification
            AND q.build_id=build AND b.intent_digest=intent_sha
            AND convert_from(r.intent_body,'UTF8')::jsonb->>'buildId'=build::text
            AND convert_from(r.intent_body,'UTF8')::jsonb->>'buildIntentDigest'=intent_sha
            AND convert_from(r.intent_body,'UTF8')::jsonb->>'artifactDigest'=artifact_sha
            AND convert_from(r.intent_body,'UTF8')::jsonb->>'runtimeImage'=image
            AND convert_from(b.intent_body,'UTF8')::jsonb->>'revisionId'=revision::text)
$body$;
REVOKE ALL ON FUNCTION braid.machine_invocation_provenance(uuid,uuid,uuid,uuid,uuid,text,text,uuid,text) FROM PUBLIC;

CREATE FUNCTION braid.write_invocation_grant(operation text, org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, key uuid, issuer text, tenant uuid, principal uuid) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; t braid.local_release_targets%ROWTYPE; g braid.invocation_grants%ROWTYPE;
BEGIN
    IF session_user<>'braid_invocation_grant_writer' OR actor IS NULL OR org IS NULL OR project IS NULL OR mapping IS NULL
        OR environment IS NULL OR target_version IS NULL OR target_version<1 OR target IS NULL
        OR key IS NULL OR key='00000000-0000-0000-0000-000000000000' OR operation IS NULL OR operation NOT IN ('grant','revoke')
        OR (operation='grant')<>(issuer IS NOT NULL AND tenant IS NOT NULL AND principal IS NOT NULL)
        OR (operation='revoke' AND (issuer IS NOT NULL OR tenant IS NOT NULL OR principal IS NOT NULL)) THEN RETURN 3; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN RETURN 3; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR UPDATE;
    IF NOT FOUND OR t.project_id IS DISTINCT FROM project OR t.mapping_id IS DISTINCT FROM mapping OR t.environment_id IS DISTINCT FROM environment
        OR t.environment_version IS DISTINCT FROM target_version THEN RETURN 3; END IF;
    SELECT * INTO g FROM ONLY braid.invocation_grants WHERE organization_id=org AND grant_id=key FOR UPDATE;
    IF FOUND THEN
        IF g.target_id<>target THEN RETURN 2; END IF;
        IF operation='grant' THEN
            RETURN CASE WHEN g.provider=issuer AND g.tenant_id=tenant AND g.object_id=principal THEN 1 ELSE 2 END;
        END IF;
        IF g.revoked_at IS NOT NULL THEN RETURN 1; END IF;
        UPDATE braid.invocation_grants SET revoked_by=actor,revoked_at=statement_timestamp() WHERE organization_id=org AND grant_id=key;
        INSERT INTO braid.invocation_grant_audit(organization_id,grant_id,sequence,kind,actor_id) VALUES(org,key,2,'revoked',actor);
        RETURN 0;
    END IF;
    IF operation<>'grant' OR issuer<>'entra' OR tenant='00000000-0000-0000-0000-000000000000' OR principal='00000000-0000-0000-0000-000000000000'
        OR NOT t.enabled THEN RETURN 3; END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.invocation_grants a WHERE a.organization_id=org AND a.target_id=target AND a.provider=issuer
        AND a.tenant_id=tenant AND a.object_id=principal AND a.revoked_at IS NULL) THEN RETURN 2; END IF;
    INSERT INTO braid.invocation_grants(organization_id,project_id,mapping_id,target_id,grant_id,provider,tenant_id,object_id,granted_by)
        VALUES(org,project,mapping,target,key,issuer,tenant,principal,actor);
    INSERT INTO braid.invocation_grant_audit(organization_id,grant_id,sequence,kind,actor_id) VALUES(org,key,1,'granted',actor);
    RETURN 0;
END;
$body$;
REVOKE ALL ON FUNCTION braid.write_invocation_grant(text,uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.read_invocation_grants(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid) RETURNS TABLE(grant_id uuid, provider text, tenant_id uuid, object_id uuid, granted_by uuid, granted_at timestamp with time zone, revoked_by uuid, revoked_at timestamp with time zone)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT g.grant_id,g.provider,g.tenant_id,g.object_id,g.granted_by,g.granted_at,g.revoked_by,g.revoked_at
    FROM ONLY braid.invocation_grants g JOIN ONLY braid.local_release_targets t ON t.organization_id=g.organization_id AND t.target_id=g.target_id
    WHERE session_user='braid_invocation_grant_writer' AND g.organization_id=org AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version AND g.target_id=target
        AND braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['administrator'])
    ORDER BY g.granted_at,g.grant_id
$body$;
REVOKE ALL ON FUNCTION braid.read_invocation_grants(uuid,uuid,uuid,uuid,bigint,uuid) FROM PUBLIC;

-- Status: 0 claimed, 1 recorded outcome, 2 different request under this ID, 3 attempt in progress,
-- 4 refused (no active grant, disabled or other target), 5 no current release. Only 0 and 1 write.
CREATE FUNCTION braid.claim_machine_invocation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, issuer text, tenant uuid, principal uuid, application uuid, request uuid, digest text) RETURNS TABLE(status integer, attempt_number integer, claim_key uuid, publisher uuid, final_state text, final_outcome text, final_release uuid, retained bytea)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE t braid.local_release_targets%ROWTYPE; g uuid; i braid.machine_invocations%ROWTYPE; existing boolean:=false; owner uuid;
    fresh uuid:=pg_catalog.gen_random_uuid(); ends timestamp with time zone;
BEGIN
    IF session_user<>'braid_invocation_writer' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target_version<1 OR target IS NULL
        OR issuer IS DISTINCT FROM 'entra' OR tenant IS NULL OR tenant='00000000-0000-0000-0000-000000000000'
        OR principal IS NULL OR principal='00000000-0000-0000-0000-000000000000' OR application IS NULL OR application='00000000-0000-0000-0000-000000000000'
        OR request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR digest IS NULL OR digest!~'^[0-9a-f]{64}$' THEN
        RETURN QUERY SELECT 4,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR SHARE;
    IF NOT FOUND OR t.project_id<>project OR t.mapping_id<>mapping OR t.environment_id<>environment OR t.environment_version<>target_version OR NOT t.enabled THEN
        RETURN QUERY SELECT 4,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    SELECT x.grant_id INTO g FROM ONLY braid.invocation_grants x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
        AND x.tenant_id=tenant AND x.object_id=principal AND x.revoked_at IS NULL FOR SHARE;
    IF g IS NULL THEN
        RETURN QUERY SELECT 4,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    SELECT r.actor_id INTO owner FROM ONLY braid.local_releases r WHERE r.organization_id=org AND r.target_id=target
        AND r.release_id=t.active_release_id AND r.phase='active';
    -- The first claim inserts atomically; a concurrent first claim that lost the insert evaluates the winner's row.
    FOR pass IN 1..2 LOOP
        SELECT * INTO i FROM ONLY braid.machine_invocations x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
            AND x.tenant_id=tenant AND x.object_id=principal AND x.request_id=request FOR UPDATE;
        existing:=FOUND;
        EXIT WHEN existing;
        IF owner IS NULL THEN
            RETURN QUERY SELECT 5,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
        END IF;
        ends:=clock_timestamp()+interval '120 seconds';
        INSERT INTO braid.machine_invocations(organization_id,project_id,mapping_id,target_id,provider,tenant_id,object_id,request_id,request_digest,application_id,claim_id,lease_until)
            VALUES(org,project,mapping,target,issuer,tenant,principal,request,digest,application,fresh,ends) ON CONFLICT DO NOTHING;
        IF FOUND THEN
            INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
                VALUES(org,target,issuer,tenant,principal,request,1,1,'claimed','none',g,application);
            RETURN QUERY SELECT 0,1,fresh,owner,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
        END IF;
    END LOOP;
    IF NOT existing THEN
        RETURN QUERY SELECT 3,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    IF i.request_digest<>digest OR i.application_id<>application OR i.project_id<>project OR i.mapping_id<>mapping THEN
        RETURN QUERY SELECT 2,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    IF i.state IN ('completed','failed') THEN
        RETURN QUERY SELECT 1,i.attempt,NULL::uuid,NULL::uuid,i.state,a.outcome,a.release_id,
            (SELECT x.body FROM ONLY braid.machine_invocation_results x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
                AND x.tenant_id=tenant AND x.object_id=principal AND x.request_id=request AND x.expires_at>clock_timestamp())
            FROM ONLY braid.machine_invocation_audit a WHERE a.organization_id=org AND a.target_id=target AND a.provider=issuer
                AND a.tenant_id=tenant AND a.object_id=principal AND a.request_id=request AND a.sequence=2*i.attempt;
        RETURN;
    END IF;
    IF i.state='running' AND i.lease_until>clock_timestamp() THEN
        RETURN QUERY SELECT 3,i.attempt,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    IF owner IS NULL THEN
        RETURN QUERY SELECT 5,NULL::integer,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::uuid,NULL::bytea; RETURN;
    END IF;
    IF i.state='running' THEN
        -- The previous attempt ended without an outcome: its lease expired.
        IF i.attempt=8 THEN
            UPDATE braid.machine_invocations SET state='failed',finished_at=statement_timestamp() WHERE organization_id=org AND target_id=target
                AND provider=issuer AND tenant_id=tenant AND object_id=principal AND request_id=request;
            INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
                VALUES(org,target,issuer,tenant,principal,request,16,8,'failed','attemptsExhausted',g,application);
            RETURN QUERY SELECT 1,8,NULL::uuid,NULL::uuid,'failed'::text,'attemptsExhausted'::text,NULL::uuid,NULL::bytea; RETURN;
        END IF;
        INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
            VALUES(org,target,issuer,tenant,principal,request,2*i.attempt,i.attempt,'released','interrupted',g,application);
        UPDATE braid.machine_invocations SET state='retryable' WHERE organization_id=org AND target_id=target
            AND provider=issuer AND tenant_id=tenant AND object_id=principal AND request_id=request;
    END IF;
    ends:=clock_timestamp()+interval '120 seconds';
    UPDATE braid.machine_invocations SET state='running',attempt=i.attempt+1,claim_id=fresh,lease_until=ends WHERE organization_id=org AND target_id=target
        AND provider=issuer AND tenant_id=tenant AND object_id=principal AND request_id=request;
    INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
        VALUES(org,target,issuer,tenant,principal,request,2*i.attempt+1,i.attempt+1,'claimed','none',g,application);
    RETURN QUERY SELECT 0,i.attempt+1,fresh,owner,NULL::text,NULL::text,NULL::uuid,NULL::bytea;
END;
$body$;
REVOKE ALL ON FUNCTION braid.claim_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text) FROM PUBLIC;

-- Result: 0 recorded, 1 refused at delivery (grant revoked or target disabled; the attempt is
-- released), 2 superseded or expired claim (nothing written), 3 invalid finish or provenance.
CREATE FUNCTION braid.finish_machine_invocation(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, issuer text, tenant uuid, principal uuid, request uuid, claim uuid, application uuid, finding text, release uuid, qualification uuid, build uuid, intent_sha text, artifact_sha text, revision uuid, image text, clock text, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE t braid.local_release_targets%ROWTYPE; g uuid; i braid.machine_invocations%ROWTYPE; event text; next_state text; recorded text:=finding;
BEGIN
    IF session_user<>'braid_invocation_writer' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR project IS NULL OR mapping IS NULL OR environment IS NULL OR target_version IS NULL OR target IS NULL OR issuer IS NULL OR tenant IS NULL
        OR principal IS NULL OR request IS NULL OR claim IS NULL OR application IS NULL OR finding IS NULL
        OR finding NOT IN ('completed','refused','invalidInput','invalidParameter','executionFailed','resourceLimit','integrityFailure','unsupported',
            'unavailable','busy','releaseChanged')
        OR (finding='completed')<>(body IS NOT NULL) OR (finding='completed' AND (release IS NULL OR clock IS NULL))
        OR (release IS NULL)<>(qualification IS NULL) OR (release IS NULL)<>(build IS NULL) OR (release IS NULL)<>(intent_sha IS NULL)
        OR (release IS NULL)<>(artifact_sha IS NULL) OR (release IS NULL)<>(revision IS NULL) OR (release IS NULL)<>(image IS NULL) THEN RETURN 3; END IF;
    SELECT * INTO i FROM ONLY braid.machine_invocations x WHERE x.organization_id=org AND x.target_id=target AND x.provider=issuer
        AND x.tenant_id=tenant AND x.object_id=principal AND x.request_id=request FOR UPDATE;
    -- The lease is the attempt's authority: an expired attempt cannot record or retain an outcome.
    IF NOT FOUND OR i.state<>'running' OR i.claim_id<>claim OR i.lease_until<=clock_timestamp() THEN RETURN 2; END IF;
    IF i.project_id<>project OR i.mapping_id<>mapping OR i.application_id<>application THEN RETURN 3; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR SHARE;
    IF t.environment_id<>environment OR t.environment_version<>target_version THEN RETURN 3; END IF;
    IF release IS NOT NULL AND NOT braid.machine_invocation_provenance(org,target,release,qualification,build,intent_sha,artifact_sha,revision,image) THEN RETURN 3; END IF;
    g:=braid.machine_invocation_grant(org,target,issuer,tenant,principal);
    -- Target/provenance reads may also wait; authority must still be live immediately before a write.
    IF i.lease_until<=clock_timestamp() THEN RETURN 2; END IF;
    IF g IS NULL THEN
        -- Revocation or disablement during the attempt: no outcome is delivered or retained.
        event:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'released' END;
        recorded:=CASE WHEN i.attempt=8 THEN 'attemptsExhausted' ELSE 'refused' END;
        UPDATE braid.machine_invocations SET state=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'retryable' END,
            finished_at=CASE WHEN i.attempt=8 THEN statement_timestamp() END
            WHERE organization_id=org AND target_id=target AND provider=issuer AND tenant_id=tenant AND object_id=principal AND request_id=request;
        INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id)
            VALUES(org,target,issuer,tenant,principal,request,2*i.attempt,i.attempt,event,recorded,
                CASE WHEN i.attempt=8 THEN (SELECT a.grant_id FROM ONLY braid.machine_invocation_audit a WHERE a.organization_id=org AND a.target_id=target
                    AND a.provider=issuer AND a.tenant_id=tenant AND a.object_id=principal AND a.request_id=request AND a.sequence=15) END,application);
        RETURN 1;
    END IF;
    IF finding='completed' THEN event:='completed'; next_state:='completed';
    ELSIF finding IN ('unavailable','busy','releaseChanged') THEN
        event:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'released' END;
        next_state:=CASE WHEN i.attempt=8 THEN 'failed' ELSE 'retryable' END;
        IF i.attempt=8 THEN recorded:='attemptsExhausted'; END IF;
    ELSE event:='failed'; next_state:='failed';
    END IF;
    UPDATE braid.machine_invocations SET state=next_state,finished_at=CASE WHEN next_state IN ('completed','failed') THEN statement_timestamp() END
        WHERE organization_id=org AND target_id=target AND provider=issuer AND tenant_id=tenant AND object_id=principal AND request_id=request;
    INSERT INTO braid.machine_invocation_audit(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence,attempt,kind,outcome,grant_id,application_id,
        release_id,qualification_id,build_id,build_intent_digest,artifact_digest,revision_id,runtime_image,clock,output_digest,output_bytes)
        VALUES(org,target,issuer,tenant,principal,request,2*i.attempt,i.attempt,event,recorded,g,application,
            release,qualification,build,intent_sha,artifact_sha,revision,image,clock,
            CASE WHEN body IS NOT NULL THEN encode(sha256(body),'hex') END,octet_length(body));
    IF finding='completed' THEN
        INSERT INTO braid.machine_invocation_results(organization_id,target_id,provider,tenant_id,object_id,request_id,body,body_digest,expires_at)
            VALUES(org,target,issuer,tenant,principal,request,body,encode(sha256(body),'hex'),statement_timestamp()+interval '24 hours');
    END IF;
    RETURN 0;
EXCEPTION WHEN check_violation OR data_exception THEN RETURN 3;
END;
$body$;
REVOKE ALL ON FUNCTION braid.finish_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea) FROM PUBLIC;

-- Removes expired retained results for one target, oldest first. Audit and digests remain.
CREATE FUNCTION braid.purge_machine_invocation_results(org uuid, target uuid, maximum integer) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE n integer;
BEGIN
    IF session_user<>'braid_invocation_writer' OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR target IS NULL OR maximum IS NULL OR maximum NOT BETWEEN 1 AND 1000 THEN RETURN -1; END IF;
    DELETE FROM ONLY braid.machine_invocation_results x USING (SELECT y.provider,y.tenant_id,y.object_id,y.request_id FROM ONLY braid.machine_invocation_results y
        WHERE y.organization_id=org AND y.target_id=target AND y.expires_at<=clock_timestamp() ORDER BY y.expires_at LIMIT maximum FOR UPDATE SKIP LOCKED) e
        WHERE x.organization_id=org AND x.target_id=target AND x.provider=e.provider AND x.tenant_id=e.tenant_id AND x.object_id=e.object_id AND x.request_id=e.request_id;
    GET DIAGNOSTICS n=ROW_COUNT;
    RETURN n;
END;
$body$;
REVOKE ALL ON FUNCTION braid.purge_machine_invocation_results(uuid,uuid,integer) FROM PUBLIC;
GRANT USAGE ON SCHEMA braid TO braid_invocation_grant_writer,braid_invocation_writer;
GRANT EXECUTE ON FUNCTION braid.write_invocation_grant(text,uuid,uuid,uuid,uuid,bigint,uuid,uuid,text,uuid,uuid),
    braid.read_invocation_grants(uuid,uuid,uuid,uuid,bigint,uuid) TO braid_invocation_grant_writer;
GRANT EXECUTE ON FUNCTION braid.claim_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,text),
    braid.finish_machine_invocation(uuid,uuid,uuid,uuid,bigint,uuid,text,uuid,uuid,uuid,uuid,uuid,text,uuid,uuid,uuid,text,text,uuid,text,text,bytea),
    braid.purge_machine_invocation_results(uuid,uuid,integer) TO braid_invocation_writer;
