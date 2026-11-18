-- Permanent deletion of an archived project or mapping. A confirmed request
-- commits an irreversible job under the organization gate; a dedicated worker
-- waits for admitted work to settle, inventories private objects, copies the
-- retained evidence and removes the catalog closure in ordered short
-- transactions, then erases private objects through exact physical fences.
-- Audit, usage, quota, lifecycle identities, tombstones and retained evidence
-- are never purged. Guards admit DELETE only inside a worker transaction that
-- recorded a job step, so no session setting or direct statement can purge.
DO $roles$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='braid_lifecycle_worker';
    IF current_user<>'braid_migrator' OR session_user<>current_user OR NOT FOUND
        OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole
        OR r.rolreplication OR r.rolbypassrls OR r.rolconfig IS NOT NULL
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY') THEN
        RAISE EXCEPTION 'Independent lifecycle worker login required.' USING ERRCODE='42501';
    END IF;
END;
$roles$;

CREATE TABLE braid.deletion_jobs (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    job_id uuid NOT NULL CHECK(job_id<>'00000000-0000-0000-0000-000000000000'),
    resource_kind text COLLATE "C" NOT NULL CHECK(resource_kind IN ('project','mapping')),
    project_id uuid NOT NULL CHECK(project_id<>'00000000-0000-0000-0000-000000000000'),
    mapping_id uuid CHECK(mapping_id<>'00000000-0000-0000-0000-000000000000'),
    requested_by uuid NOT NULL CHECK(requested_by<>'00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    confirmation_id uuid NOT NULL CHECK(confirmation_id<>'00000000-0000-0000-0000-000000000000'),
    plan_version integer NOT NULL CHECK(plan_version=1),
    expected_project_version bigint NOT NULL CHECK(expected_project_version>0),
    expected_mapping_version bigint CHECK(expected_mapping_version>0),
    state text COLLATE "C" NOT NULL CHECK(state IN ('quiescing','inventory','catalogPurging','objectsPending','completed','blocked')),
    blocker text COLLATE "C" CHECK(blocker IN ('activeRelease','integrity','storage','attemptsExhausted')),
    step integer NOT NULL DEFAULT 0 CHECK(step BETWEEN 0 AND 12),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0),
    object_count integer NOT NULL DEFAULT 0 CHECK(object_count>=0),
    retained_artifact_count integer NOT NULL DEFAULT 0 CHECK(retained_artifact_count>=0),
    retained_audit_count integer NOT NULL DEFAULT 0 CHECK(retained_audit_count>=0),
    requested_at timestamptz NOT NULL CHECK(isfinite(requested_at)),
    changed_at timestamptz NOT NULL CHECK(isfinite(changed_at)),
    catalog_removed_at timestamptz CHECK(isfinite(catalog_removed_at)),
    completed_at timestamptz CHECK(isfinite(completed_at)),
    next_attempt_at timestamptz NOT NULL CHECK(isfinite(next_attempt_at)),
    claim_generation bigint NOT NULL DEFAULT 0 CHECK(claim_generation>=0),
    claim_instance uuid, claim_token uuid, claim_expires_at timestamptz,
    PRIMARY KEY(organization_id,job_id),
    UNIQUE(organization_id,requested_by,operation_id),
    UNIQUE(organization_id,confirmation_id),
    CHECK((resource_kind='project')=(mapping_id IS NULL) AND (mapping_id IS NULL)=(expected_mapping_version IS NULL)),
    CHECK((state='blocked')=(blocker IS NOT NULL)),
    CHECK((state='completed')=(completed_at IS NOT NULL) AND (completed_at IS NULL OR catalog_removed_at IS NOT NULL)),
    CHECK((claim_instance IS NULL)=(claim_token IS NULL) AND (claim_token IS NULL)=(claim_expires_at IS NULL))
);
-- One irreversible job per lifecycle identity; a deleted identity is never reused.
CREATE UNIQUE INDEX deletion_job_identity ON braid.deletion_jobs(organization_id,project_id,mapping_id) NULLS NOT DISTINCT;
CREATE INDEX deletion_job_due ON braid.deletion_jobs(next_attempt_at,job_id) WHERE state IN ('quiescing','inventory','catalogPurging','objectsPending');
-- One row per worker transaction; a row in the current transaction is what admits guarded deletes.
CREATE TABLE braid.deletion_job_attempts (
    organization_id uuid NOT NULL, job_id uuid NOT NULL,
    generation bigint NOT NULL CHECK(generation>0),
    sequence integer NOT NULL CHECK(sequence>0),
    instance_id uuid NOT NULL CHECK(instance_id<>'00000000-0000-0000-0000-000000000000'),
    state text COLLATE "C" NOT NULL, step integer NOT NULL,
    outcome text COLLATE "C" NOT NULL CHECK(outcome IN ('claimed','advanced','waiting','blocked','completed','released')),
    transaction_id xid8 NOT NULL,
    recorded_at timestamptz NOT NULL CHECK(isfinite(recorded_at)),
    PRIMARY KEY(organization_id,job_id,generation,sequence),
    FOREIGN KEY(organization_id,job_id) REFERENCES braid.deletion_jobs(organization_id,job_id)
);
CREATE INDEX deletion_attempt_transaction ON braid.deletion_job_attempts(organization_id,transaction_id);
-- Exact private object targets frozen from immutable catalog metadata before removal.
CREATE TABLE braid.deletion_objects (
    organization_id uuid NOT NULL, job_id uuid NOT NULL,
    object_target_id uuid NOT NULL CHECK(object_target_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, revision_id uuid NOT NULL,
    object_class text COLLATE "C" NOT NULL CHECK(object_class IN ('native','derived')),
    object_id uuid NOT NULL CHECK(object_id<>'00000000-0000-0000-0000-000000000000'),
    target_kind text COLLATE "C" NOT NULL CHECK(target_kind IN ('final_object','tracked_temporary')),
    publication_id uuid,
    publication jsonb,
    disposition text COLLATE "C" NOT NULL DEFAULT 'pending' CHECK(disposition IN ('pending','deleted','missing','blocked')),
    observation text COLLATE "C" CHECK(observation IN ('deleted','missing','busy','unsupported','unsafeStorage','io')),
    attempts integer NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 8),
    next_attempt_at timestamptz NOT NULL CHECK(isfinite(next_attempt_at)),
    claim_generation bigint NOT NULL DEFAULT 0 CHECK(claim_generation>=0),
    claim_instance uuid, claim_token uuid, claim_expires_at timestamptz,
    finished_at timestamptz,
    PRIMARY KEY(organization_id,job_id,object_target_id),
    UNIQUE NULLS NOT DISTINCT(organization_id,job_id,object_class,project_id,mapping_id,revision_id,object_id,target_kind,publication_id),
    FOREIGN KEY(organization_id,job_id) REFERENCES braid.deletion_jobs(organization_id,job_id),
    CHECK((target_kind='tracked_temporary')=(publication_id IS NOT NULL) AND (publication_id IS NULL)=(publication IS NULL)),
    CHECK((disposition IN ('deleted','missing'))=(finished_at IS NOT NULL)),
    CHECK((claim_instance IS NULL)=(claim_token IS NULL) AND (claim_token IS NULL)=(claim_expires_at IS NULL))
);
CREATE INDEX deletion_object_due ON braid.deletion_objects(organization_id,job_id,next_attempt_at,object_target_id) WHERE disposition='pending';
CREATE TABLE braid.deletion_object_attempts (
    organization_id uuid NOT NULL, job_id uuid NOT NULL, object_target_id uuid NOT NULL,
    generation bigint NOT NULL CHECK(generation>0),
    instance_id uuid NOT NULL, lease_token uuid NOT NULL,
    started_at timestamptz NOT NULL, lease_expires_at timestamptz NOT NULL,
    finished_at timestamptz,
    observation text COLLATE "C" CHECK(observation IN ('deleted','missing','busy','unsupported','unsafeStorage','io')),
    PRIMARY KEY(organization_id,job_id,object_target_id,generation),
    FOREIGN KEY(organization_id,job_id,object_target_id) REFERENCES braid.deletion_objects(organization_id,job_id,object_target_id),
    CHECK((finished_at IS NULL)=(observation IS NULL))
);

-- Retained evidence, detached from live catalog rows: organization FK only.
CREATE TABLE braid.retained_build_artifacts (
    LIKE braid.build_publications INCLUDING CONSTRAINTS,
    job_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL, revision_id uuid NOT NULL,
    actor_id uuid NOT NULL, environment_id uuid NOT NULL, environment_version bigint NOT NULL,
    -- No organization policy covers artifacts: the copy is held, never expired.
    retention_unconfigured boolean NOT NULL DEFAULT true CHECK(retention_unconfigured),
    retain_until timestamptz CHECK(retain_until IS NULL),
    retained_at timestamptz NOT NULL CHECK(isfinite(retained_at)),
    PRIMARY KEY(organization_id,build_id),
    FOREIGN KEY(organization_id) REFERENCES braid.organizations(id)
);
CREATE TABLE braid.retained_release_audit (LIKE braid.local_release_audit INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    PRIMARY KEY(organization_id,release_id,sequence), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));
CREATE TABLE braid.retained_grant_audit (LIKE braid.invocation_grant_audit INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    PRIMARY KEY(organization_id,grant_id,sequence), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));
CREATE TABLE braid.retained_invocation_audit (LIKE braid.machine_invocation_audit INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    PRIMARY KEY(organization_id,target_id,provider,tenant_id,object_id,request_id,sequence), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));
CREATE TABLE braid.retained_object_publications (LIKE braid.private_object_publications INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    PRIMARY KEY(organization_id,project_id,publication_id), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));
CREATE TABLE braid.retained_object_retirements (LIKE braid.private_object_retirements INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    PRIMARY KEY(organization_id,project_id,retirement_id), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));
CREATE TABLE braid.retained_object_cleanup_attempts (LIKE braid.private_object_cleanup_attempts INCLUDING CONSTRAINTS, job_id uuid NOT NULL,
    PRIMARY KEY(organization_id,project_id,retirement_id,generation), FOREIGN KEY(organization_id) REFERENCES braid.organizations(id));

-- Jobs and object targets advance only through the owner functions below; every
-- other record is insert-only. Nothing here is ever deleted.
CREATE FUNCTION braid.purge_record_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR (TG_OP='UPDATE' AND TG_TABLE_NAME NOT IN ('deletion_jobs','deletion_objects','deletion_object_attempts')) THEN
        RAISE EXCEPTION 'Deletion records are owner-maintained.' USING ERRCODE='42501';
    END IF;
    -- Each table's fields resolve only inside its own block.
    IF TG_OP='UPDATE' THEN
        IF TG_TABLE_NAME='deletion_jobs' THEN
            IF ROW(NEW.organization_id,NEW.job_id,NEW.resource_kind,NEW.project_id,NEW.mapping_id,NEW.requested_by,NEW.operation_id,
                    NEW.confirmation_id,NEW.plan_version,NEW.expected_project_version,NEW.expected_mapping_version,NEW.requested_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.job_id,OLD.resource_kind,OLD.project_id,OLD.mapping_id,OLD.requested_by,OLD.operation_id,
                    OLD.confirmation_id,OLD.plan_version,OLD.expected_project_version,OLD.expected_mapping_version,OLD.requested_at)
                OR OLD.state='completed' OR NEW.version<>OLD.version+1 OR NEW.claim_generation<OLD.claim_generation
                OR (OLD.catalog_removed_at IS NOT NULL AND NEW.catalog_removed_at IS DISTINCT FROM OLD.catalog_removed_at) THEN
                RAISE EXCEPTION 'Deletion jobs keep their intent and never reopen.' USING ERRCODE='23514';
            END IF;
        ELSIF TG_TABLE_NAME='deletion_objects' THEN
            IF (to_jsonb(NEW)-ARRAY['disposition','observation','attempts','next_attempt_at','claim_generation','claim_instance','claim_token','claim_expires_at','finished_at'])
                IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['disposition','observation','attempts','next_attempt_at','claim_generation','claim_instance','claim_token','claim_expires_at','finished_at'])
                OR OLD.disposition IN ('deleted','missing') OR NEW.claim_generation<OLD.claim_generation THEN
                RAISE EXCEPTION 'Deletion targets keep their identity and never reopen.' USING ERRCODE='23514';
            END IF;
        ELSE
            IF (to_jsonb(NEW)-ARRAY['finished_at','observation']) IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['finished_at','observation'])
                OR OLD.finished_at IS NOT NULL OR NEW.finished_at IS NULL THEN
                RAISE EXCEPTION 'Deletion attempts finish once.' USING ERRCODE='23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;

DO $policies$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['deletion_jobs','deletion_job_attempts','deletion_objects','deletion_object_attempts','retained_build_artifacts',
        'retained_release_audit','retained_grant_audit','retained_invocation_audit','retained_object_publications','retained_object_retirements',
        'retained_object_cleanup_attempts'] LOOP
        EXECUTE format('ALTER TABLE braid.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format($p$CREATE POLICY organization_scope ON braid.%I USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid)$p$,t);
        EXECUTE format('REVOKE ALL ON braid.%I FROM PUBLIC',t);
        EXECUTE format('CREATE TRIGGER purge_record_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.%I FOR EACH ROW EXECUTE FUNCTION braid.purge_record_guard()',t);
        EXECUTE format('CREATE TRIGGER purge_record_no_truncate BEFORE TRUNCATE ON braid.%I FOR EACH STATEMENT EXECUTE FUNCTION braid.purge_record_guard()',t);
    END LOOP;
END;
$policies$;

-- Whether this transaction is a lifecycle worker step for this organization. A
-- step row can only be written by advance_deletion_job, whose deletes are static
-- and scoped to its job, so no caller-set value can admit a DELETE.
CREATE FUNCTION braid.catalog_purge_admitted(p_org uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT session_user='braid_lifecycle_worker' AND EXISTS(SELECT 1 FROM ONLY braid.deletion_job_attempts a
        WHERE a.organization_id=p_org AND a.transaction_id=pg_current_xact_id() AND a.outcome='advanced')
$body$;

-- A deleting or deleted identity never changes state again and is never reused:
-- restore cannot reopen it, and a mapping in a deleting project cannot appear.
-- A pending mapping deletion also holds its project's lifecycle steady.
CREATE FUNCTION braid.purge_identity_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF EXISTS(SELECT 1 FROM ONLY braid.deletion_jobs j WHERE j.organization_id=NEW.organization_id AND j.project_id=NEW.project_id
        AND (j.mapping_id IS NULL OR (NEW.resource_kind='mapping' AND j.mapping_id=NEW.resource_id)
            OR (TG_OP='UPDATE' AND NEW.resource_kind='project' AND j.state<>'completed'))) THEN
        RAISE EXCEPTION 'The project or mapping is being deleted.' USING ERRCODE='PWD02';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER purge_identity_guard BEFORE INSERT OR UPDATE ON braid.resource_lifecycles FOR EACH ROW EXECUTE FUNCTION braid.purge_identity_guard();

-- Targets whose current active pointer resolves anywhere into the scope, through
-- the target, release, qualification or build approval. Disabled targets count.
CREATE FUNCTION braid.purge_active_releases(p_org uuid,p_project uuid,p_mapping uuid) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT count(DISTINCT t.target_id) FROM ONLY braid.local_release_targets t
    LEFT JOIN ONLY braid.local_releases r ON r.organization_id=t.organization_id AND r.release_id=t.active_release_id
    LEFT JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id
    LEFT JOIN ONLY braid.build_approvals a ON a.organization_id=q.organization_id AND a.build_id=q.build_id
    WHERE t.organization_id=p_org AND (t.active_release_id IS NOT NULL
        OR EXISTS(SELECT 1 FROM ONLY braid.host_order_pins h WHERE h.organization_id=t.organization_id AND h.target_id=t.target_id)
        OR EXISTS(SELECT 1 FROM ONLY braid.trigger_controls c WHERE c.organization_id=t.organization_id AND c.target_id=t.target_id)
        OR EXISTS(SELECT 1 FROM ONLY braid.release_replica_rosters rr WHERE rr.organization_id=t.organization_id AND rr.target_id=t.target_id)) AND (
        (t.project_id=p_project AND (p_mapping IS NULL OR t.mapping_id=p_mapping))
        OR (r.project_id=p_project AND (p_mapping IS NULL OR r.mapping_id=p_mapping))
        OR (q.project_id=p_project AND (p_mapping IS NULL OR q.mapping_id=p_mapping))
        OR (a.project_id=p_project AND (p_mapping IS NULL OR a.mapping_id=p_mapping)))
$body$;

-- Admitted work that still holds a live lease or an open turn inside the scope.
-- Queued or expired work is fenced by archive and removed with the closure.
CREATE FUNCTION braid.purge_live_work(p_org uuid,p_project uuid,p_mapping uuid) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT (SELECT count(*) FROM ONLY braid.preview_runs WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND state=2 AND lease_expires_at>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.regression_runs WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND state='running' AND lease_expires>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.build_runs r JOIN ONLY braid.build_approvals a ON a.organization_id=r.organization_id AND a.approval_id=r.approval_id
            WHERE r.organization_id=p_org AND a.project_id=p_project AND (p_mapping IS NULL OR a.mapping_id=p_mapping)
            AND r.state='running' AND r.lease_expires>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.automation_evaluations WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND state='running' AND lease_expires>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.revision_ingestions WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR future_mapping_id=p_mapping)
            AND completed_at IS NULL AND lease_expires_at>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.revision_derivation_attempts WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND finished_at IS NULL AND lease_expires_at>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.private_object_cleanup_attempts WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND finished_at IS NULL AND lease_expires_at>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.machine_invocations WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)
            AND finished_at IS NULL AND lease_until>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.notification_deliveries d JOIN ONLY braid.notification_events e ON e.organization_id=d.organization_id AND e.event_id=d.event_id
            WHERE d.organization_id=p_org AND e.project_id=p_project AND (p_mapping IS NULL OR e.mapping_id=p_mapping)
            AND d.token IS NOT NULL AND d.lease_expires>clock_timestamp())
        + (SELECT count(*) FROM ONLY braid.chat_turns t JOIN ONLY braid.chat_conversations c USING(organization_id,creator_id,conversation_id)
            WHERE t.organization_id=p_org AND t.phase=1 AND t.deadline>clock_timestamp()
            AND ((c.project_id=p_project AND (p_mapping IS NULL OR c.mapping_id=p_mapping))
                OR (c.focus_project_id=p_project AND (p_mapping IS NULL OR c.focus_mapping_id=p_mapping))))
$body$;

-- Status visible to the deleting administrator, an organization administrator
-- after the project is gone, or the original requester: categories and counts only.
CREATE FUNCTION braid.deletion_job_projection(j braid.deletion_jobs) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT jsonb_build_object('jobId',j.job_id,'resourceKind',j.resource_kind,'projectId',j.project_id,'mappingId',j.mapping_id,
        'state',j.state,'blocker',j.blocker,'catalogRemoved',j.catalog_removed_at IS NOT NULL,
        'objectCount',j.object_count::text,
        'objectsPending',(SELECT count(*) FROM ONLY braid.deletion_objects o WHERE o.organization_id=j.organization_id AND o.job_id=j.job_id AND o.disposition='pending')::text,
        'objectsBlocked',(SELECT count(*) FROM ONLY braid.deletion_objects o WHERE o.organization_id=j.organization_id AND o.job_id=j.job_id AND o.disposition='blocked')::text,
        'retainedArtifacts',j.retained_artifact_count::text,'retainedAuditRecords',j.retained_audit_count::text,
        'requestedAtUtc',j.requested_at,'changedAtUtc',j.changed_at,'catalogRemovedAtUtc',j.catalog_removed_at,'completedAtUtc',j.completed_at)
$body$;

CREATE FUNCTION braid.deletion_job_readable(j braid.deletion_jobs,p_actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT CASE WHEN EXISTS(SELECT 1 FROM ONLY braid.projects p WHERE p.organization_id=j.organization_id AND p.id=j.project_id)
            THEN braid.administration_authorized(j.organization_id,p_actor,j.project_id)
            ELSE braid.administration_authorized(j.organization_id,p_actor,NULL) END
        OR (j.requested_by=p_actor AND braid.resource_deletion_authorized(j.organization_id,p_actor,NULL,NULL))
$body$;

CREATE FUNCTION braid.purge_audit(j braid.deletion_jobs,p_event text,p_outcome text,p_actor uuid,p_at timestamp with time zone) RETURNS void
LANGUAGE sql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT braid.audit_log_append('deletion_jobs',j.job_id::text||':'||p_event||':'||j.claim_generation::text,j.organization_id,j.project_id,j.mapping_id,p_actor,
        CASE WHEN p_actor IS NULL THEN 'system' ELSE 'user' END,'administration',j.resource_kind||p_event,
        coalesce(j.mapping_id,j.project_id),p_outcome,p_at,false)
$body$;

-- Read-only impact for the exact-project administrator. It persists nothing;
-- the confirmation the server builds from it is bound to these versions.
CREATE FUNCTION braid.preview_resource_purge(p_org uuid,p_actor uuid,p_project uuid,p_mapping uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE p braid.resource_lifecycles%ROWTYPE; m braid.resource_lifecycles%ROWTYPE; j braid.deletion_jobs%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS NULL OR p_project IS NULL OR NOT braid.administration_authorized(p_org,p_actor,p_project) THEN RETURN NULL; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.projects WHERE organization_id=p_org AND id=p_project)
        OR (p_mapping IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND id=p_mapping)) THEN RETURN NULL; END IF;
    SELECT * INTO p FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='project' AND resource_id=p_project;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF p_mapping IS NOT NULL THEN
        SELECT * INTO m FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='mapping' AND resource_id=p_mapping AND project_id=p_project;
        IF NOT FOUND THEN RETURN NULL; END IF;
    END IF;
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND project_id=p_project AND (mapping_id IS NULL OR mapping_id=p_mapping)
        ORDER BY mapping_id NULLS FIRST LIMIT 1;
    RETURN jsonb_build_object('resourceKind',CASE WHEN p_mapping IS NULL THEN 'project' ELSE 'mapping' END,'projectId',p_project,'mappingId',p_mapping,
        'effectiveState',CASE WHEN j.job_id IS NOT NULL THEN 'deleting' WHEN p.state='archived' OR m.state='archived' THEN 'archived' ELSE 'active' END,
        'projectVersion',p.version::text,'mappingVersion',m.version::text,'planVersion','1',
        'deletion',CASE WHEN j.job_id IS NULL THEN NULL ELSE braid.deletion_job_projection(j) END,
        'activeReleases',braid.purge_active_releases(p_org,p_project,p_mapping)::text,
        'pendingMappingDeletions',(SELECT count(*) FROM ONLY braid.deletion_jobs x WHERE x.organization_id=p_org AND x.project_id=p_project
            AND x.mapping_id IS NOT NULL AND p_mapping IS NULL AND x.state<>'completed')::text,
        'impact',jsonb_build_object(
            'mappings',(SELECT count(*) FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR id=p_mapping))::text,
            'revisions',(SELECT count(*) FROM ONLY braid.mapping_revisions WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'conversations',(SELECT count(*) FROM ONLY braid.chat_conversations WHERE organization_id=p_org
                AND ((project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)) OR (focus_project_id=p_project AND (p_mapping IS NULL OR focus_mapping_id=p_mapping))))::text,
            'previews',(SELECT count(*) FROM ONLY braid.preview_runs WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'regressionCases',(SELECT count(*) FROM ONLY braid.regression_cases WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'regressionRuns',(SELECT count(*) FROM ONLY braid.regression_runs WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'builds',(SELECT count(*) FROM ONLY braid.build_approvals WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'releaseTargets',(SELECT count(*) FROM ONLY braid.local_release_targets WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))::text,
            'retainedArtifacts',(SELECT count(*) FROM ONLY braid.build_publications b JOIN ONLY braid.build_approvals a ON a.organization_id=b.organization_id AND a.approval_id=b.approval_id
                WHERE b.organization_id=p_org AND a.project_id=p_project AND (p_mapping IS NULL OR a.mapping_id=p_mapping))::text));
END;
$body$;

-- One short transaction: exclusive gate, fresh authority after the wait, exact
-- receipt replay, confirmation consumed once, archived state and both versions
-- rechecked, active pointers rechecked, irreversible job and one audit event.
CREATE FUNCTION braid.request_resource_purge(p_org uuid,p_actor uuid,p_operation uuid,p_confirmation uuid,p_project uuid,p_mapping uuid,
    p_project_version bigint,p_mapping_version bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE kind text:=CASE WHEN p_mapping IS NULL THEN 'project' ELSE 'mapping' END; j braid.deletion_jobs%ROWTYPE;
    p braid.resource_lifecycles%ROWTYPE; m braid.resource_lifecycles%ROWTYPE; at_time timestamptz; active bigint;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    IF p_actor IS NULL OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_confirmation IS NULL
        OR p_confirmation='00000000-0000-0000-0000-000000000000' OR p_project IS NULL OR p_project='00000000-0000-0000-0000-000000000000'
        OR p_mapping='00000000-0000-0000-0000-000000000000' OR p_project_version IS NULL OR p_project_version<1
        OR (p_mapping IS NULL)<>(p_mapping_version IS NULL) OR p_mapping_version<1 THEN RETURN jsonb_build_object('outcome','invalidRequest'); END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    IF NOT braid.resource_deletion_authorized(p_org,p_actor,NULL,NULL) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND requested_by=p_actor AND operation_id=p_operation;
    IF FOUND THEN
        IF ROW(j.resource_kind,j.project_id,j.mapping_id,j.confirmation_id,j.expected_project_version,j.expected_mapping_version)
            IS DISTINCT FROM ROW(kind,p_project,p_mapping,p_confirmation,p_project_version,p_mapping_version) THEN
            RETURN jsonb_build_object('outcome','conflict');
        END IF;
        RETURN jsonb_build_object('outcome','replayed','operationId',p_operation,'job',braid.deletion_job_projection(j));
    END IF;
    IF NOT braid.administration_authorized(p_org,p_actor,p_project)
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.projects WHERE organization_id=p_org AND id=p_project)
        OR (p_mapping IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND id=p_mapping)) THEN
        RETURN jsonb_build_object('outcome','refused');
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND confirmation_id=p_confirmation) THEN
        RETURN jsonb_build_object('outcome','conflict');
    END IF;
    SELECT * INTO p FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='project' AND resource_id=p_project FOR UPDATE;
    IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    IF p_mapping IS NOT NULL THEN
        SELECT * INTO m FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='mapping' AND resource_id=p_mapping
            AND project_id=p_project FOR UPDATE;
        IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    END IF;
    IF p.version<>p_project_version OR (p_mapping IS NOT NULL AND m.version<>p_mapping_version) THEN
        RETURN jsonb_build_object('outcome','preconditionFailed');
    END IF;
    -- Only an effectively archived resource can be deleted, and only once.
    IF NOT (p.state='archived' OR coalesce(m.state='archived',false)) OR EXISTS(SELECT 1 FROM ONLY braid.deletion_jobs WHERE organization_id=p_org
        AND project_id=p_project AND (mapping_id IS NULL OR mapping_id IS NOT DISTINCT FROM p_mapping)) THEN
        RETURN jsonb_build_object('outcome','conflict');
    END IF;
    IF p_mapping IS NULL AND EXISTS(SELECT 1 FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND project_id=p_project
        AND mapping_id IS NOT NULL AND state<>'completed') THEN RETURN jsonb_build_object('outcome','activeWork'); END IF;
    active:=braid.purge_active_releases(p_org,p_project,p_mapping);
    IF active>0 THEN RETURN jsonb_build_object('outcome','activeRelease','activeReleases',active::text); END IF;
    at_time:=clock_timestamp();
    INSERT INTO braid.deletion_jobs(organization_id,job_id,resource_kind,project_id,mapping_id,requested_by,operation_id,confirmation_id,plan_version,
        expected_project_version,expected_mapping_version,state,requested_at,changed_at,next_attempt_at)
        VALUES(p_org,gen_random_uuid(),kind,p_project,p_mapping,p_actor,p_operation,p_confirmation,1,p_project_version,p_mapping_version,
            'quiescing',at_time,at_time,at_time) RETURNING * INTO j;
    PERFORM braid.purge_audit(j,'DeleteRequested','accepted',p_actor,at_time);
    RETURN jsonb_build_object('outcome','accepted','operationId',p_operation,'job',braid.deletion_job_projection(j));
END;
$body$;

CREATE FUNCTION braid.read_deletion_job(p_org uuid,p_actor uuid,p_job uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid OR p_actor IS NULL OR p_job IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND job_id=p_job;
    IF NOT FOUND OR NOT braid.deletion_job_readable(j,p_actor) THEN RETURN NULL; END IF;
    RETURN braid.deletion_job_projection(j);
END;
$body$;

-- Recovery of an unknown commit by the original requester's operation identity.
CREATE FUNCTION braid.read_resource_purge_operation(p_org uuid,p_actor uuid,p_operation uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid OR p_actor IS NULL OR p_operation IS NULL
        OR NOT braid.resource_deletion_authorized(p_org,p_actor,NULL,NULL) THEN RETURN NULL; END IF;
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND requested_by=p_actor AND operation_id=p_operation;
    IF NOT FOUND THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('outcome','recorded','operationId',p_operation,'job',braid.deletion_job_projection(j));
END;
$body$;

-- The job that covers a resource, for lifecycle reads by a project reader.
CREATE FUNCTION braid.read_resource_purge(p_org uuid,p_actor uuid,p_project uuid,p_mapping uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid OR p_actor IS NULL OR p_project IS NULL
        OR NOT braid.resource_deletion_authorized(p_org,p_actor,p_project,ARRAY['viewer','runner','publisher','administrator']) THEN RETURN NULL; END IF;
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND project_id=p_project AND (mapping_id IS NULL OR mapping_id=p_mapping)
        ORDER BY mapping_id NULLS FIRST LIMIT 1;
    IF NOT FOUND THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('jobId',j.job_id,'state',j.state);
END;
$body$;

-- The worker discovers one due job without locking, then takes its organization
-- gate and claims it with a fresh generation. Expired claims are reclaimable.
CREATE FUNCTION braid.claim_deletion_job(p_instance uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE candidate record; j braid.deletion_jobs%ROWTYPE; at_time timestamptz;
BEGIN
    IF session_user<>'braid_lifecycle_worker' OR current_user<>'braid_migrator' OR p_instance IS NULL
        OR p_instance='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'Lifecycle worker required.' USING ERRCODE='42501'; END IF;
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle work requires read committed transactions.' USING ERRCODE='25001';
    END IF;
    SELECT organization_id,job_id INTO candidate FROM ONLY braid.deletion_jobs
        WHERE state IN ('quiescing','inventory','catalogPurging','objectsPending') AND next_attempt_at<=clock_timestamp()
            AND (claim_expires_at IS NULL OR claim_expires_at<=clock_timestamp())
        ORDER BY next_attempt_at,job_id LIMIT 1;
    IF NOT FOUND THEN RETURN NULL; END IF;
    PERFORM braid.lifecycle_gate(candidate.organization_id,true);
    PERFORM set_config('braid.organization_id',candidate.organization_id::text,true);
    at_time:=clock_timestamp();
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=candidate.organization_id AND job_id=candidate.job_id
        AND state IN ('quiescing','inventory','catalogPurging','objectsPending') AND next_attempt_at<=at_time
        AND (claim_expires_at IS NULL OR claim_expires_at<=at_time) FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    UPDATE braid.deletion_jobs SET claim_generation=claim_generation+1,claim_instance=p_instance,claim_token=gen_random_uuid(),
        claim_expires_at=at_time+interval '120 seconds',version=version+1
        WHERE organization_id=j.organization_id AND job_id=j.job_id RETURNING * INTO j;
    INSERT INTO braid.deletion_job_attempts(organization_id,job_id,generation,sequence,instance_id,state,step,outcome,transaction_id,recorded_at)
        VALUES(j.organization_id,j.job_id,j.claim_generation,1,p_instance,j.state,j.step,'claimed',pg_current_xact_id(),at_time);
    RETURN jsonb_build_object('organizationId',j.organization_id,'jobId',j.job_id,'generation',j.claim_generation::text,
        'token',j.claim_token,'leaseExpiresAtUtc',j.claim_expires_at,'state',j.state);
END;
$body$;

-- Locks and verifies the exact live claim after the organization gate.
CREATE FUNCTION braid.lock_deletion_claim(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid) RETURNS braid.deletion_jobs
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE;
BEGIN
    IF session_user<>'braid_lifecycle_worker' OR current_user<>'braid_migrator' OR p_org IS NULL OR p_job IS NULL THEN
        RAISE EXCEPTION 'Lifecycle worker required.' USING ERRCODE='42501';
    END IF;
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle work requires read committed transactions.' USING ERRCODE='25001';
    END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    PERFORM set_config('braid.organization_id',p_org::text,true);
    SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND job_id=p_job FOR UPDATE;
    IF NOT FOUND OR j.claim_instance IS DISTINCT FROM p_instance OR j.claim_generation IS DISTINCT FROM p_generation
        OR j.claim_token IS DISTINCT FROM p_token OR j.claim_expires_at<=clock_timestamp() THEN RETURN NULL; END IF;
    RETURN j;
END;
$body$;

CREATE FUNCTION braid.record_deletion_step(j braid.deletion_jobs,p_outcome text) RETURNS void
LANGUAGE sql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    INSERT INTO braid.deletion_job_attempts(organization_id,job_id,generation,sequence,instance_id,state,step,outcome,transaction_id,recorded_at)
    SELECT j.organization_id,j.job_id,j.claim_generation,coalesce(max(a.sequence),0)+1,j.claim_instance,j.state,j.step,p_outcome,pg_current_xact_id(),clock_timestamp()
    FROM ONLY braid.deletion_job_attempts a WHERE a.organization_id=j.organization_id AND a.job_id=j.job_id AND a.generation=j.claim_generation
$body$;

-- One family of the closure, in dependency order. Retained evidence is copied in
-- the same statement sequence that removes its source, so a retry after an
-- unknown commit neither duplicates nor loses a copy. Strongly connected
-- components are removed in one statement, never by nulling a reference.
CREATE FUNCTION braid.purge_catalog_step(j braid.deletion_jobs) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE org uuid:=j.organization_id; project uuid:=j.project_id; mapping uuid:=j.mapping_id; at_time timestamptz:=clock_timestamp(); n integer:=0; c integer;
BEGIN
    IF j.step=1 THEN
        -- Chat: retire every identity, then remove content; revision-bound and focused conversations alike.
        UPDATE braid.chat_identities i SET erased_at=statement_timestamp(),retirement_policy_id='resource-deletion',retirement_policy_version=1,
            retirement_not_before='9999-12-31 23:59:59.999999+00'
            FROM ONLY braid.chat_conversations x WHERE x.organization_id=org
                AND ((x.project_id=project AND (mapping IS NULL OR x.mapping_id=mapping)) OR (x.focus_project_id=project AND (mapping IS NULL OR x.focus_mapping_id=mapping)))
                AND i.organization_id=x.organization_id AND i.creator_id=x.creator_id AND i.conversation_id=x.conversation_id AND i.erased_at IS NULL;
        DELETE FROM braid.chat_conversations x WHERE x.organization_id=org
            AND ((x.project_id=project AND (mapping IS NULL OR x.mapping_id=mapping)) OR (x.focus_project_id=project AND (mapping IS NULL OR x.focus_mapping_id=mapping)));
        SET CONSTRAINTS braid.chat_identity_erasure IMMEDIATE;
    ELSIF j.step=2 THEN
        DELETE FROM braid.catalog_search_report_index WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
    ELSIF j.step=3 THEN
        -- Notifications and automation; project-wide settings only with the project.
        DELETE FROM braid.notification_delivery_attempts a USING ONLY braid.notification_deliveries d, ONLY braid.notification_events e
            WHERE a.organization_id=org AND d.organization_id=org AND e.organization_id=org AND a.delivery_id=d.delivery_id AND d.event_id=e.event_id
                AND e.project_id=project AND (mapping IS NULL OR e.mapping_id=mapping);
        DELETE FROM braid.notification_deliveries d USING ONLY braid.notification_events e
            WHERE d.organization_id=org AND e.organization_id=org AND d.event_id=e.event_id AND e.project_id=project AND (mapping IS NULL OR e.mapping_id=mapping);
        DELETE FROM braid.notifications x USING ONLY braid.notification_events e
            WHERE x.organization_id=org AND e.organization_id=org AND x.event_id=e.event_id AND e.project_id=project AND (mapping IS NULL OR e.mapping_id=mapping);
        DELETE FROM braid.notification_events WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.automation_evaluations WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.automation_suites WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.automation_mappings WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.notification_endpoint_windows w WHERE w.organization_id=org AND w.project_id=project AND (mapping IS NULL OR EXISTS(
            SELECT 1 FROM ONLY braid.local_release_targets t WHERE t.organization_id=org AND t.target_id=w.target_id AND t.mapping_id=mapping));
        IF mapping IS NULL THEN
            DELETE FROM braid.automation_projects WHERE organization_id=org AND project_id=project;
            DELETE FROM braid.notification_subscriptions WHERE organization_id=org AND project_id=project;
            DELETE FROM braid.notification_destinations WHERE organization_id=org AND project_id=project;
        END IF;
    ELSIF j.step=4 THEN
        -- Releases, grants and invocations: source audits are copied first; a current pointer blocks.
        IF braid.purge_active_releases(org,project,mapping)>0 THEN RAISE EXCEPTION 'active release' USING ERRCODE='PWD03'; END IF;
        IF EXISTS(SELECT 1 FROM ONLY braid.local_releases r JOIN ONLY braid.build_qualifications q ON q.organization_id=r.organization_id AND q.qualification_id=r.qualification_id
            WHERE r.organization_id=org AND ((q.project_id=project AND (mapping IS NULL OR q.mapping_id=mapping))
                <> (r.project_id=project AND (mapping IS NULL OR r.mapping_id=mapping)))) THEN
            RAISE EXCEPTION 'release ancestry' USING ERRCODE='PWD04';
        END IF;
        INSERT INTO braid.retained_invocation_audit SELECT a.*,j.job_id,i.project_id,i.mapping_id FROM ONLY braid.machine_invocation_audit a
            JOIN ONLY braid.machine_invocations i USING(organization_id,target_id,provider,tenant_id,object_id,request_id)
            WHERE i.organization_id=org AND i.project_id=project AND (mapping IS NULL OR i.mapping_id=mapping);
        GET DIAGNOSTICS c=ROW_COUNT; n:=n+c;
        INSERT INTO braid.retained_grant_audit SELECT a.*,j.job_id,g.project_id,g.mapping_id,g.target_id FROM ONLY braid.invocation_grant_audit a
            JOIN ONLY braid.invocation_grants g USING(organization_id,grant_id)
            WHERE g.organization_id=org AND g.project_id=project AND (mapping IS NULL OR g.mapping_id=mapping);
        GET DIAGNOSTICS c=ROW_COUNT; n:=n+c;
        INSERT INTO braid.retained_release_audit SELECT a.*,j.job_id,r.project_id,r.mapping_id,r.target_id FROM ONLY braid.local_release_audit a
            JOIN ONLY braid.local_releases r USING(organization_id,release_id)
            WHERE r.organization_id=org AND r.project_id=project AND (mapping IS NULL OR r.mapping_id=mapping);
        GET DIAGNOSTICS c=ROW_COUNT; n:=n+c;
        DELETE FROM braid.invocation_operations_metadata x USING ONLY braid.machine_invocations i
            WHERE x.organization_id=org AND i.organization_id=org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id)
                AND i.project_id=project AND (mapping IS NULL OR i.mapping_id=mapping);
        DELETE FROM braid.machine_invocation_results x USING ONLY braid.machine_invocations i
            WHERE x.organization_id=org AND i.organization_id=org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id)
                AND i.project_id=project AND (mapping IS NULL OR i.mapping_id=mapping);
        DELETE FROM braid.machine_invocation_audit x USING ONLY braid.machine_invocations i
            WHERE x.organization_id=org AND i.organization_id=org AND (x.target_id,x.provider,x.tenant_id,x.object_id,x.request_id)=(i.target_id,i.provider,i.tenant_id,i.object_id,i.request_id)
                AND i.project_id=project AND (mapping IS NULL OR i.mapping_id=mapping);
        DELETE FROM braid.machine_invocations WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.invocation_grant_audit x USING ONLY braid.invocation_grants g
            WHERE x.organization_id=org AND g.organization_id=org AND x.grant_id=g.grant_id AND g.project_id=project AND (mapping IS NULL OR g.mapping_id=mapping);
        DELETE FROM braid.invocation_grants WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.release_target_events x USING ONLY braid.local_release_targets t
            WHERE x.organization_id=org AND t.organization_id=org AND x.target_id=t.target_id AND t.project_id=project AND (mapping IS NULL OR t.mapping_id=mapping);
        DELETE FROM braid.release_target_operations x USING ONLY braid.local_release_targets t
            WHERE x.organization_id=org AND t.organization_id=org AND x.target_id=t.target_id AND t.project_id=project AND (mapping IS NULL OR t.mapping_id=mapping);
        DELETE FROM braid.local_release_audit x USING ONLY braid.local_releases r
            WHERE x.organization_id=org AND r.organization_id=org AND x.release_id=r.release_id AND r.project_id=project AND (mapping IS NULL OR r.mapping_id=mapping);
        DELETE FROM braid.local_releases WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.local_release_targets WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        UPDATE braid.deletion_jobs SET retained_audit_count=retained_audit_count+n,version=version+1 WHERE organization_id=org AND job_id=j.job_id;
    ELSIF j.step=5 THEN
        -- Builds: artifact bytes are copied with their scope before publications go.
        INSERT INTO braid.retained_build_artifacts(organization_id,build_id,approval_id,artifact_digest,artifact_body,state,created_at,
            job_id,project_id,mapping_id,revision_id,actor_id,environment_id,environment_version,retained_at)
            SELECT b.organization_id,b.build_id,b.approval_id,b.artifact_digest,b.artifact_body,b.state,b.created_at,
                j.job_id,a.project_id,a.mapping_id,a.revision_id,a.actor_id,a.environment_id,a.environment_version,at_time
            FROM ONLY braid.build_publications b JOIN ONLY braid.build_approvals a ON a.organization_id=b.organization_id AND a.approval_id=b.approval_id
            WHERE b.organization_id=org AND a.project_id=project AND (mapping IS NULL OR a.mapping_id=mapping);
        GET DIAGNOSTICS n=ROW_COUNT;
        IF EXISTS(SELECT 1 FROM ONLY braid.retained_build_artifacts r WHERE r.organization_id=org AND r.job_id=j.job_id
            AND encode(sha256(r.artifact_body),'hex')<>r.artifact_digest) THEN RAISE EXCEPTION 'artifact digest' USING ERRCODE='PWD04'; END IF;
        DELETE FROM braid.build_qualifications q USING ONLY braid.build_approvals a
            WHERE q.organization_id=org AND a.organization_id=org AND q.build_id=a.build_id AND a.project_id=project AND (mapping IS NULL OR a.mapping_id=mapping);
        DELETE FROM braid.build_publications b USING ONLY braid.build_approvals a
            WHERE b.organization_id=org AND a.organization_id=org AND b.approval_id=a.approval_id AND a.project_id=project AND (mapping IS NULL OR a.mapping_id=mapping);
        DELETE FROM braid.build_runs r USING ONLY braid.build_approvals a
            WHERE r.organization_id=org AND a.organization_id=org AND r.approval_id=a.approval_id AND a.project_id=project AND (mapping IS NULL OR a.mapping_id=mapping);
        DELETE FROM braid.build_approvals WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        IF mapping IS NULL THEN
            INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,retired_at)
                SELECT DISTINCT organization_id,'buildEnvironment',environment_id,project_id,at_time FROM ONLY braid.build_environments
                WHERE organization_id=org AND project_id=project ON CONFLICT DO NOTHING;
            DELETE FROM braid.build_environments WHERE organization_id=org AND project_id=project;
        END IF;
        UPDATE braid.deletion_jobs SET retained_artifact_count=retained_artifact_count+n,version=version+1 WHERE organization_id=org AND job_id=j.job_id;
    ELSIF j.step=6 THEN
        DELETE FROM braid.regression_runs WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,retired_at)
            SELECT organization_id,'regressionCase',case_id,project_id,mapping_id,at_time FROM ONLY braid.regression_cases
            WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) ON CONFLICT DO NOTHING;
        -- The header's head reference is deferred, so versions go first.
        DELETE FROM braid.regression_case_versions WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.regression_cases WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
    ELSIF j.step=7 THEN
        -- Repeating a removed preview request key cannot execute it again.
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,submitter_id,request_key,retired_at)
            SELECT organization_id,'preview',run_id,project_id,mapping_id,submitter_id,request_key,at_time FROM ONLY braid.preview_runs
            WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) ON CONFLICT DO NOTHING;
        DELETE FROM braid.preview_runs WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
    ELSIF j.step=8 THEN
        DELETE FROM braid.export_reviews WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
        DELETE FROM braid.revision_import_provenance x USING ONLY braid.revision_ingestions i
            WHERE x.organization_id=org AND i.organization_id=org AND x.project_id=i.project_id AND x.operation_id=i.operation_id
                AND i.project_id=project AND (mapping IS NULL OR i.future_mapping_id=mapping);
        IF mapping IS NULL THEN DELETE FROM braid.import_reviews WHERE organization_id=org AND project_id=project; END IF;
    ELSIF j.step=9 THEN
        -- Maintenance history is detached into retained copies with its identities and attempts.
        INSERT INTO braid.retained_object_publications SELECT x.*,j.job_id FROM ONLY braid.private_object_publications x
            WHERE x.organization_id=org AND x.project_id=project AND (mapping IS NULL OR x.mapping_id=mapping);
        INSERT INTO braid.retained_object_retirements SELECT x.*,j.job_id FROM ONLY braid.private_object_retirements x
            WHERE x.organization_id=org AND x.project_id=project AND (mapping IS NULL OR x.mapping_id=mapping);
        INSERT INTO braid.retained_object_cleanup_attempts SELECT x.*,j.job_id FROM ONLY braid.private_object_cleanup_attempts x
            WHERE x.organization_id=org AND x.project_id=project AND (mapping IS NULL OR x.mapping_id=mapping);
        WITH attempts AS (DELETE FROM braid.private_object_cleanup_attempts WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) RETURNING 1),
            retirements AS (DELETE FROM braid.private_object_retirements WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) RETURNING 1)
        DELETE FROM braid.private_object_publications WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
    ELSIF j.step=10 THEN
        WITH attempts AS (DELETE FROM braid.revision_derivation_attempts WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) RETURNING 1)
        DELETE FROM braid.revision_derivations WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping);
    ELSIF j.step=11 THEN
        -- The mapping, its head, revisions and every create/append operation are one component.
        WITH mappings AS (DELETE FROM braid.mappings WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR id=mapping) RETURNING 1),
            revisions AS (DELETE FROM braid.mapping_revisions WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR mapping_id=mapping) RETURNING 1),
            attempts AS (DELETE FROM braid.revision_ingestion_attempts a USING ONLY braid.revision_ingestions i
                WHERE a.organization_id=org AND i.organization_id=org AND a.project_id=i.project_id AND a.operation_id=i.operation_id
                    AND i.project_id=project AND (mapping IS NULL OR i.future_mapping_id=mapping) RETURNING 1)
        DELETE FROM braid.revision_ingestions WHERE organization_id=org AND project_id=project AND (mapping IS NULL OR future_mapping_id=mapping);
    ELSIF j.step=12 AND mapping IS NULL THEN
        DELETE FROM braid.project_payload_read_grants WHERE organization_id=org AND project_id=project;
        DELETE FROM braid.project_creations WHERE organization_id=org AND project_id=project;
        DELETE FROM braid.project_memberships WHERE organization_id=org AND project_id=project;
        DELETE FROM braid.projects WHERE organization_id=org AND id=project;
    END IF;
    RETURN n;
END;
$body$;

-- Any row of the closure that survived the purge; zero is required before the
-- catalog is reported removed.
CREATE FUNCTION braid.purge_residue(p_org uuid,p_project uuid,p_mapping uuid) RETURNS bigint
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT (SELECT count(*) FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.revision_ingestions WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR future_mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.chat_conversations WHERE organization_id=p_org
            AND ((project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping)) OR (focus_project_id=p_project AND (p_mapping IS NULL OR focus_mapping_id=p_mapping))))
        + (SELECT count(*) FROM ONLY braid.preview_runs WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.regression_cases WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.build_approvals WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.local_release_targets WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.private_object_publications WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.revision_derivations WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.automation_suites WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.notification_events WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.catalog_search_report_index WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + (SELECT count(*) FROM ONLY braid.export_reviews WHERE organization_id=p_org AND project_id=p_project AND (p_mapping IS NULL OR mapping_id=p_mapping))
        + CASE WHEN p_mapping IS NULL THEN (SELECT count(*) FROM ONLY braid.projects WHERE organization_id=p_org AND id=p_project)
            + (SELECT count(*) FROM ONLY braid.project_memberships WHERE organization_id=p_org AND project_id=p_project)
            + (SELECT count(*) FROM ONLY braid.build_environments WHERE organization_id=p_org AND project_id=p_project)
            + (SELECT count(*) FROM ONLY braid.import_reviews WHERE organization_id=p_org AND project_id=p_project) ELSE 0 END
$body$;

CREATE FUNCTION braid.block_deletion_job(j braid.deletion_jobs,p_blocker text) RETURNS braid.deletion_jobs
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE at_time timestamptz:=clock_timestamp();
BEGIN
    UPDATE braid.deletion_jobs SET state='blocked',blocker=p_blocker,changed_at=at_time,version=version+1
        WHERE organization_id=j.organization_id AND job_id=j.job_id RETURNING * INTO j;
    PERFORM braid.record_deletion_step(j,'blocked');
    PERFORM braid.purge_audit(j,'DeleteBlocked',p_blocker,NULL,at_time);
    RETURN j;
END;
$body$;

-- One bounded step of the claimed job in its own short transaction. Waiting
-- for live work, inventory, each closure family and final completion are
-- separate steps; storage I/O happens between object claims and finishes.
CREATE FUNCTION braid.advance_deletion_job(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; at_time timestamptz; n integer; pending bigint; blocked bigint;
BEGIN
    j:=braid.lock_deletion_claim(p_org,p_job,p_instance,p_generation,p_token);
    IF j.job_id IS NULL THEN RETURN jsonb_build_object('outcome','claimLost'); END IF;
    at_time:=clock_timestamp();
    IF j.state='quiescing' THEN
        IF braid.purge_live_work(p_org,j.project_id,j.mapping_id)>0 THEN
            UPDATE braid.deletion_jobs SET next_attempt_at=at_time+interval '15 seconds',version=version+1
                WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
            PERFORM braid.record_deletion_step(j,'waiting');
            RETURN jsonb_build_object('outcome','waiting','state',j.state);
        END IF;
        UPDATE braid.deletion_jobs SET state='inventory',changed_at=at_time,version=version+1 WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
    ELSIF j.state='inventory' THEN
        IF braid.purge_active_releases(p_org,j.project_id,j.mapping_id)>0 THEN
            j:=braid.block_deletion_job(j,'activeRelease');
            RETURN jsonb_build_object('outcome','blocked','state',j.state);
        END IF;
        -- Every object a revision, ingestion, report attempt or tracked publication could
        -- have written, frozen from immutable metadata before any of it is removed.
        INSERT INTO braid.deletion_objects(organization_id,job_id,object_target_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,next_attempt_at)
            SELECT DISTINCT p_org,p_job,gen_random_uuid(),i.project_id,i.future_mapping_id,i.future_revision_id,'native',i.native_object_id,'final_object',at_time
            FROM ONLY braid.revision_ingestions i WHERE i.organization_id=p_org AND i.project_id=j.project_id AND (j.mapping_id IS NULL OR i.future_mapping_id=j.mapping_id)
            ON CONFLICT DO NOTHING;
        INSERT INTO braid.deletion_objects(organization_id,job_id,object_target_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,next_attempt_at)
            SELECT p_org,p_job,gen_random_uuid(),x.project_id,x.mapping_id,x.id,'native',x.native_object_id,'final_object',at_time
            FROM ONLY braid.mapping_revisions x WHERE x.organization_id=p_org AND x.project_id=j.project_id AND (j.mapping_id IS NULL OR x.mapping_id=j.mapping_id)
            ON CONFLICT DO NOTHING;
        INSERT INTO braid.deletion_objects(organization_id,job_id,object_target_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,next_attempt_at)
            SELECT p_org,p_job,gen_random_uuid(),a.project_id,a.mapping_id,a.revision_id,'derived',a.candidate_object_id,'final_object',at_time
            FROM ONLY braid.revision_derivation_attempts a WHERE a.organization_id=p_org AND a.project_id=j.project_id AND (j.mapping_id IS NULL OR a.mapping_id=j.mapping_id)
            ON CONFLICT DO NOTHING;
        INSERT INTO braid.deletion_objects(organization_id,job_id,object_target_id,project_id,mapping_id,revision_id,object_class,object_id,target_kind,
                publication_id,publication,next_attempt_at)
            SELECT p_org,p_job,gen_random_uuid(),x.project_id,x.mapping_id,x.revision_id,x.object_class,x.object_id,'tracked_temporary',
                x.publication_id,braid.private_publication_record_v1(x),at_time
            FROM ONLY braid.private_object_publications x WHERE x.organization_id=p_org AND x.project_id=j.project_id AND (j.mapping_id IS NULL OR x.mapping_id=j.mapping_id)
            ON CONFLICT DO NOTHING;
        SELECT count(*) INTO n FROM ONLY braid.deletion_objects WHERE organization_id=p_org AND job_id=p_job;
        UPDATE braid.deletion_jobs SET state='catalogPurging',step=1,object_count=n,changed_at=at_time,version=version+1
            WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
    ELSIF j.state='catalogPurging' THEN
        -- The step row admits this transaction's guarded deletes for this organization only.
        PERFORM braid.record_deletion_step(j,'advanced');
        BEGIN
            PERFORM braid.purge_catalog_step(j);
        EXCEPTION WHEN SQLSTATE 'PWD03' THEN
            j:=braid.block_deletion_job(j,'activeRelease');
            RETURN jsonb_build_object('outcome','blocked','state',j.state);
        WHEN SQLSTATE 'PWD04' OR foreign_key_violation OR check_violation THEN
            j:=braid.block_deletion_job(j,'integrity');
            RETURN jsonb_build_object('outcome','blocked','state',j.state);
        END;
        SELECT * INTO j FROM ONLY braid.deletion_jobs WHERE organization_id=p_org AND job_id=p_job;
        IF j.step<12 THEN
            UPDATE braid.deletion_jobs SET step=step+1,changed_at=at_time,version=version+1 WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
            RETURN jsonb_build_object('outcome','advanced','state',j.state);
        END IF;
        IF braid.purge_residue(p_org,j.project_id,j.mapping_id)<>0 THEN
            j:=braid.block_deletion_job(j,'integrity');
            RETURN jsonb_build_object('outcome','blocked','state',j.state);
        END IF;
        UPDATE braid.deletion_jobs SET state='objectsPending',catalog_removed_at=at_time,changed_at=at_time,version=version+1
            WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
        PERFORM braid.purge_audit(j,'CatalogRemoved','removed',NULL,at_time);
        RETURN jsonb_build_object('outcome','advanced','state',j.state);
    ELSIF j.state='objectsPending' THEN
        SELECT count(*) FILTER(WHERE disposition='pending'),count(*) FILTER(WHERE disposition='blocked') INTO pending,blocked
            FROM ONLY braid.deletion_objects WHERE organization_id=p_org AND job_id=p_job;
        IF blocked>0 THEN
            j:=braid.block_deletion_job(j,'storage');
            RETURN jsonb_build_object('outcome','blocked','state',j.state);
        ELSIF pending>0 THEN
            PERFORM braid.record_deletion_step(j,'waiting');
            RETURN jsonb_build_object('outcome','objects','state',j.state);
        END IF;
        PERFORM braid.record_deletion_step(j,'completed');
        UPDATE braid.deletion_jobs SET state='completed',completed_at=at_time,changed_at=at_time,claim_instance=NULL,claim_token=NULL,claim_expires_at=NULL,
            version=version+1 WHERE organization_id=p_org AND job_id=p_job RETURNING * INTO j;
        PERFORM braid.purge_audit(j,'DeleteCompleted','completed',NULL,at_time);
        RETURN jsonb_build_object('outcome','completed','state',j.state);
    ELSE
        RETURN jsonb_build_object('outcome','claimLost');
    END IF;
    PERFORM braid.record_deletion_step(j,'advanced');
    RETURN jsonb_build_object('outcome','advanced','state',j.state);
END;
$body$;

-- A bounded page of due object targets, each with a fresh exact lease. Storage
-- work begins only after this commit is acknowledged.
CREATE FUNCTION braid.claim_deletion_objects(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid,p_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; at_time timestamptz; result jsonb;
BEGIN
    j:=braid.lock_deletion_claim(p_org,p_job,p_instance,p_generation,p_token);
    IF j.job_id IS NULL OR j.state<>'objectsPending' OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 16 THEN RETURN '[]'::jsonb; END IF;
    at_time:=clock_timestamp();
    WITH due AS (SELECT o.object_target_id FROM ONLY braid.deletion_objects o WHERE o.organization_id=p_org AND o.job_id=p_job
            AND o.disposition='pending' AND o.next_attempt_at<=at_time AND (o.claim_expires_at IS NULL OR o.claim_expires_at<=at_time)
            ORDER BY o.next_attempt_at,o.object_target_id LIMIT p_limit FOR UPDATE SKIP LOCKED),
        claimed AS (UPDATE braid.deletion_objects o SET claim_generation=o.claim_generation+1,claim_instance=p_instance,claim_token=gen_random_uuid(),
                claim_expires_at=at_time+interval '60 seconds'
            FROM due WHERE o.organization_id=p_org AND o.job_id=p_job AND o.object_target_id=due.object_target_id RETURNING o.*),
        attempts AS (INSERT INTO braid.deletion_object_attempts(organization_id,job_id,object_target_id,generation,instance_id,lease_token,started_at,lease_expires_at)
            SELECT organization_id,job_id,object_target_id,claim_generation,claim_instance,claim_token,at_time,claim_expires_at FROM claimed RETURNING 1)
    SELECT coalesce(jsonb_agg(jsonb_build_object('objectTargetId',c.object_target_id,'generation',c.claim_generation::text,'token',c.claim_token,
            'leaseExpiresAtUtc',c.claim_expires_at,'projectId',c.project_id,'mappingId',c.mapping_id,'revisionId',c.revision_id,
            'objectClass',c.object_class,'objectId',c.object_id,'targetKind',c.target_kind,'publicationId',c.publication_id,'publication',c.publication)
            ORDER BY c.object_target_id),'[]'::jsonb) INTO result FROM claimed c;
    RETURN result;
END;
$body$;

-- Records the actual physical observation for that exact target lease only.
-- Deleted and Missing are terminal; Busy and Io retry with a bounded backoff;
-- unsafe or unsupported storage blocks for operator resolution.
CREATE FUNCTION braid.finish_deletion_object(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid,
    p_object uuid,p_object_generation bigint,p_object_token uuid,p_observation text) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; o braid.deletion_objects%ROWTYPE; at_time timestamptz;
BEGIN
    IF p_observation IS NULL OR p_observation NOT IN ('deleted','missing','busy','unsupported','unsafeStorage','io') THEN RETURN 'invalidRequest'; END IF;
    j:=braid.lock_deletion_claim(p_org,p_job,p_instance,p_generation,p_token);
    IF j.job_id IS NULL THEN RETURN 'claimLost'; END IF;
    at_time:=clock_timestamp();
    SELECT * INTO o FROM ONLY braid.deletion_objects WHERE organization_id=p_org AND job_id=p_job AND object_target_id=p_object FOR UPDATE;
    IF NOT FOUND OR o.disposition<>'pending' OR o.claim_generation IS DISTINCT FROM p_object_generation OR o.claim_token IS DISTINCT FROM p_object_token
        OR o.claim_instance IS DISTINCT FROM p_instance OR o.claim_expires_at<=at_time THEN RETURN 'claimLost'; END IF;
    UPDATE braid.deletion_object_attempts SET finished_at=at_time,observation=p_observation
        WHERE organization_id=p_org AND job_id=p_job AND object_target_id=p_object AND generation=p_object_generation;
    UPDATE braid.deletion_objects SET observation=p_observation,attempts=attempts+1,claim_instance=NULL,claim_token=NULL,claim_expires_at=NULL,
        disposition=CASE WHEN p_observation IN ('deleted','missing') THEN p_observation
            WHEN p_observation IN ('unsupported','unsafeStorage') OR attempts+1>=8 THEN 'blocked' ELSE 'pending' END,
        finished_at=CASE WHEN p_observation IN ('deleted','missing') THEN at_time END,
        next_attempt_at=CASE WHEN p_observation IN ('busy','io') THEN at_time+interval '30 seconds' ELSE next_attempt_at END
        WHERE organization_id=p_org AND job_id=p_job AND object_target_id=p_object;
    RETURN 'recorded';
END;
$body$;

-- Ends this worker's claim. Waiting or pending objects make the job due again.
CREATE FUNCTION braid.release_deletion_job(p_org uuid,p_job uuid,p_instance uuid,p_generation bigint,p_token uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE j braid.deletion_jobs%ROWTYPE; due timestamptz;
BEGIN
    j:=braid.lock_deletion_claim(p_org,p_job,p_instance,p_generation,p_token);
    IF j.job_id IS NULL THEN RETURN false; END IF;
    SELECT min(next_attempt_at) INTO due FROM ONLY braid.deletion_objects WHERE organization_id=p_org AND job_id=p_job AND disposition='pending';
    PERFORM braid.record_deletion_step(j,'released');
    UPDATE braid.deletion_jobs SET claim_instance=NULL,claim_token=NULL,claim_expires_at=NULL,version=version+1,
        next_attempt_at=CASE WHEN state='objectsPending' THEN greatest(next_attempt_at,coalesce(due,clock_timestamp())) ELSE next_attempt_at END
        WHERE organization_id=p_org AND job_id=p_job;
    RETURN true;
END;
$body$;

REVOKE ALL ON FUNCTION braid.purge_record_guard(),braid.catalog_purge_admitted(uuid),braid.purge_identity_guard(),
    braid.purge_active_releases(uuid,uuid,uuid),braid.purge_live_work(uuid,uuid,uuid),braid.deletion_job_projection(braid.deletion_jobs),
    braid.deletion_job_readable(braid.deletion_jobs,uuid),braid.purge_audit(braid.deletion_jobs,text,text,uuid,timestamp with time zone),
    braid.preview_resource_purge(uuid,uuid,uuid,uuid),braid.request_resource_purge(uuid,uuid,uuid,uuid,uuid,uuid,bigint,bigint),
    braid.read_deletion_job(uuid,uuid,uuid),braid.read_resource_purge_operation(uuid,uuid,uuid),braid.read_resource_purge(uuid,uuid,uuid,uuid),
    braid.claim_deletion_job(uuid),braid.lock_deletion_claim(uuid,uuid,uuid,bigint,uuid),braid.record_deletion_step(braid.deletion_jobs,text),
    braid.purge_catalog_step(braid.deletion_jobs),braid.purge_residue(uuid,uuid,uuid),braid.block_deletion_job(braid.deletion_jobs,text),
    braid.advance_deletion_job(uuid,uuid,uuid,bigint,uuid),braid.claim_deletion_objects(uuid,uuid,uuid,bigint,uuid,integer),
    braid.finish_deletion_object(uuid,uuid,uuid,bigint,uuid,uuid,bigint,uuid,text),braid.release_deletion_job(uuid,uuid,uuid,bigint,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.preview_resource_purge(uuid,uuid,uuid,uuid),braid.request_resource_purge(uuid,uuid,uuid,uuid,uuid,uuid,bigint,bigint),
    braid.read_deletion_job(uuid,uuid,uuid),braid.read_resource_purge_operation(uuid,uuid,uuid),braid.read_resource_purge(uuid,uuid,uuid,uuid) TO braid_administration;
GRANT USAGE ON SCHEMA braid TO braid_lifecycle_worker;
GRANT EXECUTE ON FUNCTION braid.claim_deletion_job(uuid),braid.advance_deletion_job(uuid,uuid,uuid,bigint,uuid),
    braid.claim_deletion_objects(uuid,uuid,uuid,bigint,uuid,integer),braid.finish_deletion_object(uuid,uuid,uuid,bigint,uuid,uuid,bigint,uuid,text),
    braid.release_deletion_job(uuid,uuid,uuid,bigint,uuid) TO braid_lifecycle_worker;

-- Each guard of a purged table gains one exact prelude after its first block
-- opening that admits DELETE (and, for chat identities, the erasure update)
-- only inside a worker step transaction. Every other line stays unchanged.
DO $guards$
DECLARE entry record; source text; definition text; offset_at integer;
    deletion text:=$p$    IF TG_OP='DELETE' AND current_user='braid_migrator' THEN
        IF braid.catalog_purge_admitted(OLD.organization_id) THEN RETURN OLD; END IF;
    END IF;
$p$;
BEGIN
    FOR entry IN SELECT * FROM (VALUES
        ('automation_record_guard',deletion),('guard_build_record',deletion),('guard_build_run',deletion),('guard_qualification',deletion),
        ('guard_chat_conversation',deletion),
        ('guard_chat_identity',$p$    IF TG_OP='UPDATE' AND current_user='braid_migrator' THEN
        IF OLD.erased_at IS NULL AND NEW.erased_at IS NOT NULL
            AND (to_jsonb(NEW)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before'])=
                (to_jsonb(OLD)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before']) THEN
            -- Only the owner may call the admission check; restricted sessions skip it.
            IF braid.catalog_purge_admitted(OLD.organization_id) THEN RETURN NEW; END IF;
        END IF;
    END IF;
$p$),
        ('guard_export_reviews_transition',deletion),('guard_import_reviews_transition',deletion),('guard_local_release',deletion),
        ('guard_machine_invocation',deletion),('guard_mapping_revisions_transition',deletion),('guard_mappings_transition',deletion),
        ('guard_operations_target',deletion),('guard_private_object_cleanup_attempts_v1',deletion),('guard_private_object_publications_v1',deletion),
        ('guard_private_object_retirements_v1',deletion),('guard_project_creations_transition',deletion),
        ('guard_project_payload_read_grants_transition',deletion),('guard_regression_case',deletion),('guard_regression_run',deletion),
        ('guard_regression_version',deletion),('guard_release_operations',deletion),('guard_revision_derivation_attempts_transition',deletion),
        ('guard_revision_derivations_transition',deletion),('guard_revision_import_provenance_transition',deletion),
        ('guard_revision_ingestion_attempts_transition',deletion),('guard_revision_ingestions_transition',deletion)
    ) inventory(name,prelude) LOOP
        SELECT p.prosrc,pg_get_functiondef(p.oid) INTO STRICT source,definition
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=entry.name;
        offset_at:=strpos(source,E'\nBEGIN\n');
        IF offset_at=0 THEN RAISE EXCEPTION 'Purge guard has no canonical block: %',entry.name; END IF;
        EXECUTE replace(definition,source,overlay(source PLACING E'\nBEGIN\n'||entry.prelude FROM offset_at FOR 7));
    END LOOP;
END;
$guards$;
