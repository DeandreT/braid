-- Local release state is independent of cloud deployment credentials and effects.
DO $roles$
DECLARE n text; r record;
BEGIN
    FOREACH n IN ARRAY ARRAY['braid_release_reader','braid_release_writer'] LOOP
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
            RAISE EXCEPTION 'Independent local release roles required.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE braid.local_release_targets (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    target_id uuid NOT NULL CHECK(target_id<>'00000000-0000-0000-0000-000000000000'),
    environment_id uuid NOT NULL, environment_version bigint NOT NULL,
    runtime_image text NOT NULL CHECK(runtime_image~'^sha256:[0-9a-f]{64}$'), profile jsonb NOT NULL,
    definition bytea NOT NULL CHECK(octet_length(definition) BETWEEN 1 AND 16384),
    enabled boolean NOT NULL DEFAULT true, active_release_id uuid,
    created_by uuid NOT NULL, created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at)),
    PRIMARY KEY(organization_id,target_id),
    FOREIGN KEY(organization_id,project_id,environment_id,environment_version) REFERENCES braid.build_environments(organization_id,project_id,environment_id,version),
    FOREIGN KEY(organization_id,project_id,mapping_id) REFERENCES braid.mappings(organization_id,project_id,id)
);
CREATE TABLE braid.local_releases (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    target_id uuid NOT NULL, release_id uuid NOT NULL CHECK(release_id<>'00000000-0000-0000-0000-000000000000'),
    actor_id uuid NOT NULL, qualification_id uuid NOT NULL, rollback_of_id uuid,
    intent_body bytea NOT NULL CHECK(octet_length(intent_body) BETWEEN 1 AND 16384), intent_digest text NOT NULL,
    phase text NOT NULL DEFAULT 'requested' CHECK(phase IN ('requested','active','canceled','failed')),
    rejection text NOT NULL DEFAULT 'none' CHECK(rejection IN ('none','staleActiveRelease','targetUnavailable','approvalRevoked')),
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at)),
    finished_at timestamp with time zone,
    PRIMARY KEY(organization_id,release_id), UNIQUE(organization_id,target_id,release_id),
    FOREIGN KEY(organization_id,target_id) REFERENCES braid.local_release_targets(organization_id,target_id),
    FOREIGN KEY(organization_id,qualification_id) REFERENCES braid.build_qualifications(organization_id,qualification_id),
    FOREIGN KEY(organization_id,target_id,rollback_of_id) REFERENCES braid.local_releases(organization_id,target_id,release_id),
    CHECK(encode(sha256(intent_body),'hex')=intent_digest),
    CHECK((phase='requested')=(finished_at IS NULL)),
    CHECK(finished_at IS NULL OR (isfinite(finished_at) AND finished_at>=created_at)),
    CHECK((phase='failed')=(rejection<>'none'))
);
ALTER TABLE braid.local_release_targets ADD CONSTRAINT local_target_active_release FOREIGN KEY(organization_id,target_id,active_release_id)
    REFERENCES braid.local_releases(organization_id,target_id,release_id) DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE braid.local_release_audit (
    organization_id uuid NOT NULL, release_id uuid NOT NULL, sequence integer NOT NULL CHECK(sequence IN (1,2)),
    kind text NOT NULL CHECK(kind IN ('requested','activated','canceled','rejected')),
    rejection text NOT NULL CHECK(rejection IN ('none','staleActiveRelease','targetUnavailable','approvalRevoked')),
    recorded_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,release_id,sequence),
    FOREIGN KEY(organization_id,release_id) REFERENCES braid.local_releases(organization_id,release_id),
    CHECK((sequence=1)=(kind='requested')), CHECK((kind='rejected')=(rejection<>'none'))
);
ALTER TABLE braid.local_release_targets ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.local_releases ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.local_release_audit ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.local_release_targets USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.local_releases USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.local_release_audit USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.local_release_targets,braid.local_releases,braid.local_release_audit FROM PUBLIC;

CREATE FUNCTION braid.local_release_canonical(j jsonb, target_definition boolean) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE k text; keys text[]; result text:='{'; separator text:=''; p text:='{'; ps text:='';
BEGIN
    keys:=CASE WHEN target_definition THEN ARRAY['schema','organizationId','projectId','mappingId','environmentId','environmentVersion','targetId','runtimeImage']
        ELSE ARRAY['schema','releaseId','actorId','organizationId','projectId','mappingId','targetId','environmentId','environmentVersion','qualificationId','qualificationDigest','buildId','buildIntentDigest','artifactDigest','runtimeImage','action','expectedActiveReleaseId','rollbackOfReleaseId'] END;
    FOREACH k IN ARRAY keys LOOP
        result:=result||separator||'"'||k||'":'; separator:=',';
        result:=result||CASE WHEN k='environmentVersion' THEN ((j->>k)::bigint)::text ELSE braid.build_json_text(j->>k,false) END;
    END LOOP;
    IF target_definition THEN
        FOREACH k IN ARRAY ARRAY['id','version','runtime','runtimeVersion','artifactFormat','maximumArtifactBytes'] LOOP
            p:=p||ps||'"'||k||'":'; ps:=',';
            p:=p||CASE WHEN k IN ('version','maximumArtifactBytes') THEN ((j->'profile'->>k)::bigint)::text ELSE braid.build_json_text(j->'profile'->>k,false) END;
        END LOOP;
        result:=result||',"profile":'||p||'}';
    END IF;
    RETURN result||'}';
END;
$body$;
REVOKE ALL ON FUNCTION braid.local_release_canonical(jsonb,boolean) FROM PUBLIC;

CREATE FUNCTION braid.lock_local_release_scope(org uuid, project uuid, actor uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    PERFORM 1 FROM ONLY braid.principals WHERE id=actor FOR UPDATE;
    PERFORM 1 FROM ONLY braid.organizations WHERE id=org FOR SHARE;
    PERFORM 1 FROM ONLY braid.organization_memberships WHERE organization_id=org AND principal_id=actor FOR SHARE;
    PERFORM 1 FROM ONLY braid.projects WHERE organization_id=org AND id=project FOR UPDATE;
    PERFORM 1 FROM ONLY braid.project_memberships WHERE organization_id=org AND project_id=project AND principal_id=actor FOR SHARE;
END;
$body$;
REVOKE ALL ON FUNCTION braid.lock_local_release_scope(uuid,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.local_release_admitted(org uuid, target_key uuid, qkey uuid, actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY braid.local_release_targets t
        JOIN ONLY braid.build_qualifications q ON q.organization_id=t.organization_id AND q.project_id=t.project_id AND q.mapping_id=t.mapping_id
        JOIN ONLY braid.build_approvals a ON a.organization_id=q.organization_id AND a.build_id=q.build_id AND a.actor_id=q.actor_id
        WHERE t.organization_id=org AND t.target_id=target_key AND t.enabled AND q.qualification_id=qkey AND q.actor_id=actor
            AND a.environment_id=t.environment_id AND a.environment_version=t.environment_version
            AND convert_from(q.proof_body,'UTF8')::jsonb->>'runtimeImage'=t.runtime_image
            AND t.profile=jsonb_build_object('id',convert_from(a.intent_body,'UTF8')::jsonb->'profileId',
                'version',convert_from(a.intent_body,'UTF8')::jsonb->'profileVersion','runtime',convert_from(a.intent_body,'UTF8')::jsonb->'runtime',
                'runtimeVersion',convert_from(a.intent_body,'UTF8')::jsonb->'runtimeVersion','artifactFormat',convert_from(a.intent_body,'UTF8')::jsonb->'artifactFormat',
                'maximumArtifactBytes',convert_from(a.intent_body,'UTF8')::jsonb->'maximumArtifactBytes')
            AND braid.build_approval_active(org,a.approval_id,actor)
            AND braid.qualification_valid(org,t.project_id,t.mapping_id,q.qualification_id,actor,q.proof_body,q.result_body))
$body$;
REVOKE ALL ON FUNCTION braid.local_release_admitted(uuid,uuid,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.guard_local_release() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_release_writer' OR TG_OP IN ('DELETE','TRUNCATE') THEN
        RAISE EXCEPTION 'Local release mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME='local_release_audit' THEN
        IF TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN RAISE EXCEPTION 'Release audit is immutable.' USING ERRCODE='42501'; END IF;
    ELSIF TG_TABLE_NAME='local_release_targets' THEN
        IF TG_OP='INSERT' THEN
            IF NOT NEW.enabled OR NEW.active_release_id IS NOT NULL OR NEW.created_at<>statement_timestamp()
                OR braid.build_authorized(NEW.organization_id,NEW.project_id,NEW.created_by,ARRAY['administrator']) IS NOT TRUE THEN
                RAISE EXCEPTION 'Local target registration refused.' USING ERRCODE='42501';
            END IF;
        ELSE
            IF ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.environment_id,NEW.environment_version,NEW.runtime_image,NEW.profile,NEW.definition,NEW.created_by,NEW.created_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.environment_id,OLD.environment_version,OLD.runtime_image,OLD.profile,OLD.definition,OLD.created_by,OLD.created_at)
                OR (NOT OLD.enabled AND NEW.enabled) THEN RAISE EXCEPTION 'Local target identity is immutable.' USING ERRCODE='42501'; END IF;
            IF NEW.active_release_id IS DISTINCT FROM OLD.active_release_id AND (NOT NEW.enabled OR NOT EXISTS(
                SELECT 1 FROM ONLY braid.local_releases r WHERE r.organization_id=NEW.organization_id AND r.target_id=NEW.target_id
                    AND r.release_id=NEW.active_release_id AND r.phase='active' AND r.finished_at=statement_timestamp()
                    AND (convert_from(r.intent_body,'UTF8')::jsonb->>'expectedActiveReleaseId')::uuid IS NOT DISTINCT FROM OLD.active_release_id)) THEN
                RAISE EXCEPTION 'Local target activation must be conditional.' USING ERRCODE='42501';
            END IF;
        END IF;
    ELSE
        IF TG_OP='INSERT' THEN
            IF NEW.phase<>'requested' OR NEW.finished_at IS NOT NULL OR NEW.created_at<>statement_timestamp()
                OR NOT braid.local_release_admitted(NEW.organization_id,NEW.target_id,NEW.qualification_id,NEW.actor_id) THEN
                RAISE EXCEPTION 'Local release request refused.' USING ERRCODE='42501';
            END IF;
        ELSE
            IF ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.release_id,NEW.actor_id,NEW.qualification_id,NEW.rollback_of_id,NEW.intent_body,NEW.intent_digest,NEW.created_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.release_id,OLD.actor_id,OLD.qualification_id,OLD.rollback_of_id,OLD.intent_body,OLD.intent_digest,OLD.created_at)
                OR OLD.phase<>'requested' OR NEW.phase='requested' OR NEW.finished_at<>statement_timestamp() THEN
                RAISE EXCEPTION 'Local release history is immutable.' USING ERRCODE='42501';
            END IF;
            IF NEW.phase='active' AND NOT braid.local_release_admitted(NEW.organization_id,NEW.target_id,NEW.qualification_id,NEW.actor_id) THEN
                RAISE EXCEPTION 'Local release activation refused.' USING ERRCODE='42501';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_local_release() FROM PUBLIC;
CREATE TRIGGER local_target_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.local_release_targets FOR EACH ROW EXECUTE FUNCTION braid.guard_local_release();
CREATE TRIGGER local_target_no_truncate BEFORE TRUNCATE ON braid.local_release_targets FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_local_release();
CREATE TRIGGER local_release_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.local_releases FOR EACH ROW EXECUTE FUNCTION braid.guard_local_release();
CREATE TRIGGER local_release_no_truncate BEFORE TRUNCATE ON braid.local_releases FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_local_release();
CREATE TRIGGER local_audit_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.local_release_audit FOR EACH ROW EXECUTE FUNCTION braid.guard_local_release();
CREATE TRIGGER local_audit_no_truncate BEFORE TRUNCATE ON braid.local_release_audit FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_local_release();

CREATE FUNCTION braid.guard_local_release_history() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.local_releases%ROWTYPE; n integer;
BEGIN
    SELECT * INTO r FROM ONLY braid.local_releases WHERE organization_id=NEW.organization_id AND release_id=NEW.release_id;
    SELECT count(*) INTO n FROM ONLY braid.local_release_audit WHERE organization_id=r.organization_id AND release_id=r.release_id;
    IF n<>(CASE WHEN r.phase='requested' THEN 1 ELSE 2 END) OR NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_audit a
        WHERE a.organization_id=r.organization_id AND a.release_id=r.release_id AND a.sequence=1 AND a.kind='requested' AND a.rejection='none' AND a.recorded_at=r.created_at)
        OR (r.phase<>'requested' AND NOT EXISTS(SELECT 1 FROM ONLY braid.local_release_audit a
            WHERE a.organization_id=r.organization_id AND a.release_id=r.release_id AND a.sequence=2 AND a.recorded_at=r.finished_at AND a.rejection=r.rejection
                AND a.kind=CASE r.phase WHEN 'active' THEN 'activated' WHEN 'canceled' THEN 'canceled' ELSE 'rejected' END)) THEN
        RAISE EXCEPTION 'Local release audit is incomplete.' USING ERRCODE='23514';
    END IF;
    RETURN NULL;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_local_release_history() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER local_release_history AFTER INSERT OR UPDATE ON braid.local_releases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_local_release_history();
CREATE CONSTRAINT TRIGGER local_release_event_history AFTER INSERT ON braid.local_release_audit DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION braid.guard_local_release_history();

CREATE FUNCTION braid.write_local_target(operation text, org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; old braid.local_release_targets%ROWTYPE; j jsonb;
BEGIN
    IF session_user<>'braid_release_writer' OR actor IS NULL OR org IS NULL OR project IS NULL OR mapping IS NULL
        OR environment IS NULL OR target_version IS NULL OR target_version<1 OR target IS NULL
        OR target='00000000-0000-0000-0000-000000000000' OR operation IS NULL OR operation NOT IN ('register','disable') THEN RETURN 3; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE THEN RETURN 3; END IF;
    SELECT * INTO old FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR UPDATE;
    IF FOUND THEN
        IF old.project_id IS DISTINCT FROM project OR old.mapping_id IS DISTINCT FROM mapping OR old.environment_id IS DISTINCT FROM environment OR old.environment_version IS DISTINCT FROM target_version THEN RETURN 3; END IF;
        IF operation='register' THEN RETURN CASE WHEN old.definition=body THEN 1 ELSE 2 END; END IF;
        IF operation<>'disable' THEN RETURN 3; END IF;
        IF NOT old.enabled THEN RETURN 1; END IF;
        UPDATE braid.local_release_targets SET enabled=false WHERE organization_id=org AND target_id=target;
        RETURN 0;
    END IF;
    IF operation<>'register' OR body IS NULL OR octet_length(body) NOT BETWEEN 1 AND 16384 THEN RETURN 3; END IF;
    j:=convert_from(body,'UTF8')::jsonb;
    IF convert_to(braid.local_release_canonical(j,true),'UTF8') IS DISTINCT FROM body OR j->>'schema' IS DISTINCT FROM 'braid.local-release-target.v1'
        OR j->>'organizationId' IS DISTINCT FROM org::text OR j->>'projectId' IS DISTINCT FROM project::text OR j->>'mappingId' IS DISTINCT FROM mapping::text
        OR j->>'environmentId' IS DISTINCT FROM environment::text OR (j->>'environmentVersion')::bigint IS DISTINCT FROM target_version OR j->>'targetId' IS DISTINCT FROM target::text
        OR (j->>'runtimeImage'~'^sha256:[0-9a-f]{64}$') IS NOT TRUE OR NOT EXISTS(SELECT 1 FROM ONLY braid.build_environments e
            WHERE e.organization_id=org AND e.project_id=project AND e.environment_id=environment AND e.version=target_version AND e.active AND e.profile=j->'profile') THEN RETURN 3; END IF;
    INSERT INTO braid.local_release_targets(organization_id,project_id,mapping_id,target_id,environment_id,environment_version,runtime_image,profile,definition,created_by)
        VALUES(org,project,mapping,target,environment,target_version,j->>'runtimeImage',j->'profile',body,actor);
    RETURN 0;
EXCEPTION WHEN data_exception THEN RETURN 3;
END;
$body$;
REVOKE ALL ON FUNCTION braid.write_local_target(text,uuid,uuid,uuid,uuid,bigint,uuid,bytea) FROM PUBLIC;

CREATE FUNCTION braid.write_local_release(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, key uuid, body bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; j jsonb; q braid.build_qualifications%ROWTYPE;
    old braid.local_releases%ROWTYPE; t braid.local_release_targets%ROWTYPE; previous braid.local_releases%ROWTYPE; build jsonb;
BEGIN
    IF session_user<>'braid_release_writer' OR actor IS NULL OR org IS NULL OR project IS NULL OR mapping IS NULL
        OR environment IS NULL OR target_version IS NULL OR target_version<1 OR target IS NULL
        OR key IS NULL OR key='00000000-0000-0000-0000-000000000000' OR body IS NULL OR octet_length(body) NOT BETWEEN 1 AND 16384 THEN RETURN 3; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 3; END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR UPDATE;
    IF NOT FOUND OR t.project_id IS DISTINCT FROM project OR t.mapping_id IS DISTINCT FROM mapping OR t.environment_id IS DISTINCT FROM environment OR t.environment_version IS DISTINCT FROM target_version THEN RETURN 3; END IF;
    SELECT * INTO old FROM ONLY braid.local_releases WHERE organization_id=org AND release_id=key;
    IF FOUND THEN
        IF old.actor_id IS DISTINCT FROM actor OR old.target_id IS DISTINCT FROM target THEN RETURN 3; END IF;
        RETURN CASE WHEN old.intent_body=body THEN 1 ELSE 2 END;
    END IF;
    j:=convert_from(body,'UTF8')::jsonb;
    IF convert_to(braid.local_release_canonical(j,false),'UTF8') IS DISTINCT FROM body OR j->>'schema' IS DISTINCT FROM 'braid.release-intent.v1'
        OR j->>'releaseId' IS DISTINCT FROM key::text OR j->>'actorId' IS DISTINCT FROM actor::text OR j->>'targetId' IS DISTINCT FROM target::text
        OR j->>'organizationId' IS DISTINCT FROM org::text OR j->>'projectId' IS DISTINCT FROM project::text OR j->>'mappingId' IS DISTINCT FROM mapping::text
        OR j->>'environmentId' IS DISTINCT FROM environment::text OR (j->>'environmentVersion')::bigint IS DISTINCT FROM target_version
        OR j->>'expectedActiveReleaseId' IS DISTINCT FROM ((j->>'expectedActiveReleaseId')::uuid)::text
        OR j->>'rollbackOfReleaseId' IS DISTINCT FROM ((j->>'rollbackOfReleaseId')::uuid)::text
        OR j->>'action' IS NULL OR j->>'action' NOT IN ('activate','rollback') OR (j->>'expectedActiveReleaseId')::uuid=key OR (j->>'rollbackOfReleaseId')::uuid=key THEN RETURN 3; END IF;
    SELECT * INTO q FROM ONLY braid.build_qualifications WHERE organization_id=org AND qualification_id=(j->>'qualificationId')::uuid AND actor_id=actor;
    IF NOT FOUND OR j->>'qualificationId' IS DISTINCT FROM q.qualification_id::text OR q.project_id<>project OR q.mapping_id<>mapping OR j->>'qualificationDigest' IS DISTINCT FROM q.proof_digest
        OR j->>'buildId' IS DISTINCT FROM q.build_id::text OR j->>'artifactDigest' IS DISTINCT FROM convert_from(q.proof_body,'UTF8')::jsonb->>'artifactDigest'
        OR j->>'buildIntentDigest' IS DISTINCT FROM convert_from(q.proof_body,'UTF8')::jsonb->>'intentDigest'
        OR j->>'runtimeImage' IS DISTINCT FROM t.runtime_image THEN RETURN 3; END IF;
    PERFORM braid.lock_build_run_authority(org,q.build_id);
    IF NOT braid.local_release_admitted(org,target,q.qualification_id,actor) THEN RETURN 3; END IF;
    IF (j->>'expectedActiveReleaseId')::uuid IS DISTINCT FROM t.active_release_id THEN RETURN 2; END IF;
    IF j->>'action'='activate' THEN
        IF j->>'rollbackOfReleaseId' IS NOT NULL THEN RETURN 3; END IF;
    ELSE
        SELECT * INTO previous FROM ONLY braid.local_releases WHERE organization_id=org AND target_id=target AND release_id=(j->>'rollbackOfReleaseId')::uuid;
        IF NOT FOUND OR previous.actor_id<>actor OR previous.phase<>'active' OR previous.qualification_id<>q.qualification_id
            OR previous.release_id=t.active_release_id OR t.active_release_id IS NULL THEN RETURN 3; END IF;
    END IF;
    INSERT INTO braid.local_releases(organization_id,project_id,mapping_id,target_id,release_id,actor_id,qualification_id,rollback_of_id,intent_body,intent_digest)
        VALUES(org,project,mapping,target,key,actor,q.qualification_id,(j->>'rollbackOfReleaseId')::uuid,body,encode(sha256(body),'hex'));
    INSERT INTO braid.local_release_audit(organization_id,release_id,sequence,kind,rejection) VALUES(org,key,1,'requested','none');
    RETURN 0;
EXCEPTION WHEN data_exception THEN RETURN 3;
END;
$body$;
REVOKE ALL ON FUNCTION braid.write_local_release(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bytea) FROM PUBLIC;

CREATE FUNCTION braid.transition_local_release(operation text, org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, key uuid) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; r braid.local_releases%ROWTYPE; t braid.local_release_targets%ROWTYPE;
    q braid.build_qualifications%ROWTYPE; reason text:='none'; state text:='active'; event text:='activated'; outcome integer:=0;
BEGIN
    IF session_user<>'braid_release_writer' OR actor IS NULL OR org IS NULL OR project IS NULL OR mapping IS NULL
        OR environment IS NULL OR target_version IS NULL OR target_version<1 OR target IS NULL OR key IS NULL
        OR operation IS NULL OR operation NOT IN ('apply','cancel') THEN RETURN 4; END IF;
    PERFORM braid.lock_local_release_scope(org,project,actor);
    IF braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 4; END IF;
    SELECT * INTO r FROM ONLY braid.local_releases WHERE organization_id=org AND release_id=key;
    IF NOT FOUND OR r.actor_id IS DISTINCT FROM actor OR r.target_id IS DISTINCT FROM target OR r.project_id IS DISTINCT FROM project OR r.mapping_id IS DISTINCT FROM mapping THEN RETURN 4; END IF;
    SELECT * INTO q FROM ONLY braid.build_qualifications WHERE organization_id=org AND qualification_id=r.qualification_id AND actor_id=actor;
    IF NOT FOUND THEN RETURN 4; END IF;
    PERFORM braid.lock_build_run_authority(org,q.build_id);
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=org AND target_id=target FOR UPDATE;
    IF NOT FOUND OR t.environment_id IS DISTINCT FROM environment OR t.environment_version IS DISTINCT FROM target_version THEN RETURN 4; END IF;
    SELECT * INTO r FROM ONLY braid.local_releases WHERE organization_id=org AND release_id=key FOR UPDATE;
    IF r.phase='active' THEN RETURN CASE WHEN operation='apply' THEN 1 ELSE 3 END; END IF;
    IF r.phase='canceled' THEN RETURN 2; END IF;
    IF r.phase='failed' THEN RETURN CASE WHEN r.rejection='staleActiveRelease' THEN 3 ELSE 4 END; END IF;
    IF operation='cancel' THEN state:='canceled'; event:='canceled'; outcome:=2;
    ELSIF NOT t.enabled THEN reason:='targetUnavailable';
    ELSIF NOT braid.local_release_admitted(org,target,r.qualification_id,actor) THEN reason:='approvalRevoked';
    ELSIF (convert_from(r.intent_body,'UTF8')::jsonb->>'expectedActiveReleaseId')::uuid IS DISTINCT FROM t.active_release_id THEN reason:='staleActiveRelease';
    END IF;
    IF reason<>'none' THEN state:='failed'; event:='rejected'; outcome:=CASE WHEN reason='staleActiveRelease' THEN 3 ELSE 4 END; END IF;
    UPDATE braid.local_releases SET phase=state,rejection=reason,finished_at=statement_timestamp() WHERE organization_id=org AND release_id=key;
    INSERT INTO braid.local_release_audit(organization_id,release_id,sequence,kind,rejection) VALUES(org,key,2,event,reason);
    IF state='active' THEN UPDATE braid.local_release_targets SET active_release_id=key WHERE organization_id=org AND target_id=target; END IF;
    RETURN outcome;
END;
$body$;
REVOKE ALL ON FUNCTION braid.transition_local_release(text,uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION braid.read_local_target(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid) RETURNS TABLE(definition bytea, enabled boolean, active_release_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT t.definition,t.enabled,t.active_release_id FROM ONLY braid.local_release_targets t
    WHERE session_user IN ('braid_release_reader','braid_release_writer') AND t.organization_id=org AND t.project_id=project AND t.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version AND t.target_id=target
        AND braid.build_authorized(org,project,NULLIF(current_setting('braid.build_principal_id',true),'')::uuid,ARRAY['publisher','administrator'])
$body$;
REVOKE ALL ON FUNCTION braid.read_local_target(uuid,uuid,uuid,uuid,bigint,uuid) FROM PUBLIC;

CREATE FUNCTION braid.read_local_release(org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, key uuid) RETURNS TABLE(intent bytea, phase text, audit jsonb, proof bytea, completion bytea, build_intent bytea, artifact_digest text, artifact_bytes integer, build_attempt integer, build_created timestamp with time zone, build_outcome integer, case_owner uuid, case_bytes integer, envelope bytea, evidence_attempt integer, evidence_created timestamp with time zone, evidence bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT r.intent_body,r.phase,(SELECT jsonb_agg(jsonb_build_object('sequence',a.sequence,'kind',a.kind,'recordedAt',a.recorded_at,'rejection',a.rejection) ORDER BY a.sequence)
        FROM ONLY braid.local_release_audit a WHERE a.organization_id=r.organization_id AND a.release_id=r.release_id),
        q.proof_body,q.result_body,b.intent_body,b.artifact_digest,b.artifact_bytes,b.attempt,b.created_at,b.outcome,
        c.creator_id,c.byte_count,c.envelope,rr.attempt,rr.created_at,rr.result_body
    FROM ONLY braid.local_releases r JOIN ONLY braid.local_release_targets t ON t.organization_id=r.organization_id AND t.target_id=r.target_id
    JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id AND q.actor_id=r.actor_id
    JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id AND b.actor_id=q.actor_id AND b.state='succeeded'
    JOIN ONLY braid.regression_runs rr ON rr.organization_id=q.organization_id AND rr.run_id=q.evidence_id AND rr.submitter_id=q.actor_id AND rr.state='completed'
    JOIN ONLY braid.regression_case_versions c ON c.organization_id=rr.organization_id AND c.project_id=rr.project_id AND c.mapping_id=rr.mapping_id
        AND c.case_id=rr.case_id AND c.version=rr.version AND c.content_digest=rr.case_digest
    WHERE session_user IN ('braid_release_reader','braid_release_writer') AND r.organization_id=org AND r.project_id=project AND r.mapping_id=mapping
        AND t.environment_id=environment AND t.environment_version=target_version AND r.target_id=target AND r.release_id=key
        AND r.actor_id=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid
        AND braid.build_authorized(org,project,r.actor_id,ARRAY['publisher','administrator'])
$body$;
REVOKE ALL ON FUNCTION braid.read_local_release(uuid,uuid,uuid,uuid,bigint,uuid,uuid) FROM PUBLIC;
GRANT USAGE ON SCHEMA braid TO braid_release_reader,braid_release_writer;
GRANT EXECUTE ON FUNCTION braid.read_local_target(uuid,uuid,uuid,uuid,bigint,uuid),braid.read_local_release(uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_release_reader,braid_release_writer;
GRANT EXECUTE ON FUNCTION braid.write_local_target(text,uuid,uuid,uuid,uuid,bigint,uuid,bytea),braid.write_local_release(uuid,uuid,uuid,uuid,bigint,uuid,uuid,bytea),braid.transition_local_release(text,uuid,uuid,uuid,uuid,bigint,uuid,uuid) TO braid_release_writer;
