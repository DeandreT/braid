-- Reversible project/mapping archive. Live identities start at generation one;
-- archive and restore each advance the write
-- epoch, so authoring admitted under an older epoch can never publish again.
-- Deployed runtime, invocation and grant paths are deliberately not fenced.
CREATE TABLE braid.resource_lifecycles (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    resource_kind text COLLATE "C" NOT NULL CHECK(resource_kind IN ('project','mapping')),
    resource_id uuid NOT NULL CHECK(resource_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL CHECK(project_id<>'00000000-0000-0000-0000-000000000000'),
    mapping_id uuid CHECK(mapping_id<>'00000000-0000-0000-0000-000000000000'),
    state text COLLATE "C" NOT NULL CHECK(state IN ('active','archived')),
    version bigint NOT NULL CHECK(version>0),
    write_epoch bigint NOT NULL CHECK(write_epoch>0),
    changed_at timestamptz NOT NULL CHECK(isfinite(changed_at)),
    archived_at timestamptz CHECK(isfinite(archived_at)),
    archived_by uuid CHECK(archived_by<>'00000000-0000-0000-0000-000000000000'),
    PRIMARY KEY(organization_id,resource_kind,resource_id),
    CHECK((resource_kind='project')=(mapping_id IS NULL)),
    CHECK(resource_id=coalesce(mapping_id,project_id)),
    CHECK((state='archived')=(archived_at IS NOT NULL) AND (archived_at IS NULL)=(archived_by IS NULL))
);
CREATE UNIQUE INDEX resource_lifecycle_scope ON braid.resource_lifecycles(organization_id,project_id,mapping_id) NULLS NOT DISTINCT;
INSERT INTO braid.resource_lifecycles(organization_id,resource_kind,resource_id,project_id,mapping_id,state,version,write_epoch,changed_at)
    SELECT organization_id,'project',id,id,NULL,'active',1,1,created_at FROM ONLY braid.projects
    UNION ALL SELECT organization_id,'mapping',id,project_id,id,'active',1,1,created_at FROM ONLY braid.mappings;
-- A create reserves its mapping identity before any catalog mapping exists.
-- Preserve those identities as well, including unfinished schema-41 admissions.
-- An ancestry collision fails the migration rather than accepting another scope.
INSERT INTO braid.resource_lifecycles(organization_id,resource_kind,resource_id,project_id,mapping_id,state,version,write_epoch,changed_at)
    SELECT i.organization_id,'mapping',i.future_mapping_id,i.project_id,i.future_mapping_id,'active',1,1,i.created_at
    FROM ONLY braid.revision_ingestions i WHERE i.operation_kind='create' AND NOT EXISTS(
        SELECT 1 FROM ONLY braid.mappings m WHERE m.organization_id=i.organization_id AND m.project_id=i.project_id AND m.id=i.future_mapping_id);
CREATE TABLE braid.lifecycle_operations (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    actor_id uuid NOT NULL CHECK(actor_id<>'00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    resource_kind text COLLATE "C" NOT NULL CHECK(resource_kind IN ('project','mapping')),
    project_id uuid NOT NULL, mapping_id uuid,
    action text COLLATE "C" NOT NULL CHECK(action IN ('archive','restore')),
    expected_project_version bigint NOT NULL CHECK(expected_project_version>0),
    expected_mapping_version bigint CHECK(expected_mapping_version>0),
    project_version bigint NOT NULL CHECK(project_version>0),
    mapping_version bigint CHECK(mapping_version>0),
    created_at timestamptz NOT NULL CHECK(isfinite(created_at)),
    PRIMARY KEY(organization_id,actor_id,operation_id),
    CHECK((resource_kind='project')=(mapping_id IS NULL) AND (mapping_id IS NULL)=(expected_mapping_version IS NULL)
        AND (mapping_id IS NULL)=(mapping_version IS NULL))
);
CREATE TABLE braid.lifecycle_work_admissions (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    work_kind text COLLATE "C" NOT NULL CHECK(work_kind IN ('ingestion','build','release','preview','regression','derivation','conversation','chat','automation','publication')),
    work_id uuid NOT NULL CHECK(work_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid,
    project_epoch bigint NOT NULL CHECK(project_epoch>0),
    mapping_epoch bigint NOT NULL CHECK(mapping_epoch>0),
    admitted_at timestamptz NOT NULL CHECK(isfinite(admitted_at)),
    PRIMARY KEY(organization_id,work_kind,work_id)
);
ALTER TABLE braid.resource_lifecycles ENABLE ROW LEVEL SECURITY;
CREATE TABLE braid.lifecycle_work_scopes (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    work_id uuid NOT NULL CHECK(work_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL CHECK(project_id<>'00000000-0000-0000-0000-000000000000'),
    mapping_id uuid CHECK(mapping_id<>'00000000-0000-0000-0000-000000000000'),
    project_epoch bigint NOT NULL CHECK(project_epoch>0), mapping_epoch bigint NOT NULL CHECK(mapping_epoch>0),
    UNIQUE NULLS NOT DISTINCT(organization_id,work_id,project_id,mapping_id)
);
-- Existing workspace disclosures belong to epoch one, not to a renewed turn.
-- A resolved legacy focus is itself an admitted scope, even without a citation.
-- Do not reclassify an originally unavailable focus using today's membership:
-- it disclosed no focus and must remain an organization-scoped turn.
INSERT INTO braid.lifecycle_work_scopes(organization_id,work_id,project_id,mapping_id,project_epoch,mapping_epoch)
    SELECT DISTINCT t.organization_id,t.turn_id,c.focus_project_id,c.focus_mapping_id,1,1
    FROM ONLY braid.chat_turns t JOIN ONLY braid.chat_conversations c
        USING(organization_id,creator_id,conversation_id)
    WHERE c.conversation_kind=2 AND c.focus_project_id IS NOT NULL
        AND t.original_envelope::jsonb#>>'{focus,state}'='2';
INSERT INTO braid.lifecycle_work_scopes(organization_id,work_id,project_id,mapping_id,project_epoch,mapping_epoch)
    SELECT DISTINCT organization_id,turn_id,(metadata_json::jsonb->>'project')::uuid,
        (metadata_json::jsonb->>'mapping')::uuid,1,1 FROM ONLY braid.chat_events WHERE kind IN (13,14) AND metadata_json::jsonb->>'project' IS NOT NULL
    ON CONFLICT DO NOTHING;
ALTER TABLE braid.lifecycle_work_scopes ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.lifecycle_work_scopes USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.lifecycle_work_scopes FROM PUBLIC;
ALTER TABLE braid.lifecycle_operations ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.lifecycle_work_admissions ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.resource_lifecycles USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.lifecycle_operations USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.lifecycle_work_admissions USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.resource_lifecycles,braid.lifecycle_operations,braid.lifecycle_work_admissions FROM PUBLIC;

CREATE FUNCTION braid.lifecycle_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE') OR (TG_OP='UPDATE' AND TG_TABLE_NAME<>'resource_lifecycles') THEN
        RAISE EXCEPTION 'Lifecycle records are owner-maintained.' USING ERRCODE='42501';
    END IF;
    -- Only resource_lifecycles reaches an update; its fields resolve only here.
    IF TG_OP='UPDATE' THEN
        IF ROW(NEW.organization_id,NEW.resource_kind,NEW.resource_id,NEW.project_id,NEW.mapping_id) IS DISTINCT FROM
            ROW(OLD.organization_id,OLD.resource_kind,OLD.resource_id,OLD.project_id,OLD.mapping_id)
            OR NEW.version<>OLD.version+1 OR NEW.write_epoch<>OLD.write_epoch+1 OR NEW.state=OLD.state THEN
            RAISE EXCEPTION 'Lifecycle transitions are single-step and keep their ancestry.' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER resource_lifecycle_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.resource_lifecycles FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER resource_lifecycle_no_truncate BEFORE TRUNCATE ON braid.resource_lifecycles FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_operation_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.lifecycle_operations FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_operation_no_truncate BEFORE TRUNCATE ON braid.lifecycle_operations FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_admission_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.lifecycle_work_admissions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_admission_no_truncate BEFORE TRUNCATE ON braid.lifecycle_work_admissions FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_scope_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.lifecycle_work_scopes FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER lifecycle_scope_no_truncate BEFORE TRUNCATE ON braid.lifecycle_work_scopes FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();

-- The organization gate is a namespaced transaction lock. Lifecycle changes hold
-- it exclusively from transaction start; every other mutation takes it shared
-- before application row locks, so independent writes never serialize.
-- A hash collision only adds serialization and never grants authority.
CREATE FUNCTION braid.lifecycle_gate(p_org uuid,p_exclusive boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF p_org IS NULL OR p_exclusive IS NULL THEN RAISE EXCEPTION 'Lifecycle gate requires an organization.' USING ERRCODE='22023'; END IF;
    IF p_exclusive THEN PERFORM pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||p_org::text));
    ELSE PERFORM pg_advisory_xact_lock_shared(1464158804,hashtext('braid.lifecycle:'||p_org::text)); END IF;
END;
$body$;

CREATE FUNCTION braid.lifecycle_assert_active(p_org uuid,p_project uuid,p_mapping uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    PERFORM braid.lifecycle_gate(p_org,false);
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='project' AND resource_id=p_project)
        OR (p_mapping IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='mapping' AND resource_id=p_mapping AND project_id=p_project)) THEN
        RAISE EXCEPTION 'Lifecycle identity unavailable.' USING ERRCODE='42501';
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND state='archived'
        AND ((resource_kind='project' AND resource_id=p_project) OR (resource_kind='mapping' AND project_id=p_project AND resource_id=p_mapping))) THEN
        RAISE EXCEPTION 'The project or mapping is archived.' USING ERRCODE='PWA01';
    END IF;
END;
$body$;

CREATE FUNCTION braid.lifecycle_identity() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE kind text; identity uuid; project uuid;
BEGIN
    PERFORM braid.lifecycle_gate(NEW.organization_id,false);
    -- Projects get their identity inside create_project_v1: the creation tables keep no triggers.
    kind:='mapping'; project:=NEW.project_id;
    IF TG_TABLE_NAME='revision_ingestions' THEN identity:=NEW.future_mapping_id; ELSE identity:=NEW.id; END IF;
    INSERT INTO braid.resource_lifecycles(organization_id,resource_kind,resource_id,project_id,mapping_id,state,version,write_epoch,changed_at)
        VALUES(NEW.organization_id,kind,identity,project,CASE WHEN kind='mapping' THEN identity END,'active',1,1,clock_timestamp()) ON CONFLICT DO NOTHING;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.resource_lifecycles WHERE organization_id=NEW.organization_id AND resource_kind=kind AND resource_id=identity AND project_id=project) THEN
        RAISE EXCEPTION 'Lifecycle identity belongs to another scope.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER lifecycle_identity AFTER INSERT ON braid.mappings FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_identity();
CREATE TRIGGER aa_lifecycle_identity BEFORE INSERT ON braid.revision_ingestions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_identity();

CREATE FUNCTION braid.lifecycle_fence() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE project uuid; mapping uuid; v_kind text; work uuid; publish boolean:=false; observed record; admitted braid.lifecycle_work_admissions%ROWTYPE;
    c braid.chat_conversations%ROWTYPE; j jsonb;
BEGIN
    IF TG_TABLE_NAME='mappings' THEN
        -- Head moves are fenced by their revision publication.
        IF ROW(NEW.name,NEW.description) IS NOT DISTINCT FROM ROW(OLD.name,OLD.description) THEN RETURN NEW; END IF;
        project:=NEW.project_id; mapping:=NEW.id;
    ELSIF TG_TABLE_NAME='revision_ingestions' THEN
        -- A create admits the mapping its publication will insert.
        project:=NEW.project_id; mapping:=coalesce(NEW.target_mapping_id,NEW.future_mapping_id); v_kind:='ingestion'; work:=NEW.operation_id;
        publish:=TG_OP='UPDATE';
        IF TG_OP='UPDATE' AND NEW.phase IN ('rejected','head_conflict','attempt_exhausted') THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='revision_ingestion_attempts' THEN
        project:=NEW.project_id; v_kind:='ingestion'; work:=NEW.operation_id; publish:=true;
        SELECT future_mapping_id INTO mapping FROM ONLY braid.revision_ingestions
            WHERE organization_id=NEW.organization_id AND project_id=project AND operation_id=work;
        IF TG_OP='UPDATE' AND NEW.completion_kind='operational_failure' THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='mapping_revisions' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='ingestion'; work:=NEW.ingestion_operation_id; publish:=true;
    ELSIF TG_TABLE_NAME IN ('regression_cases','regression_case_versions') THEN
        IF TG_TABLE_NAME='regression_cases' AND TG_OP='UPDATE' THEN
            IF NEW.head_version=OLD.head_version THEN RETURN NEW; END IF;
        END IF;
        project:=NEW.project_id; mapping:=NEW.mapping_id;
    ELSIF TG_TABLE_NAME='preview_runs' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='preview'; work:=NEW.run_id; publish:=TG_OP='UPDATE';
        -- Requesting cancellation keeps the original running lease intact and
        -- may change no other field, including successful output or metadata.
        IF TG_OP='UPDATE' AND OLD.state=2 AND NEW.cancel_requested
            AND (to_jsonb(NEW)-'cancel_requested') IS NOT DISTINCT FROM (to_jsonb(OLD)-'cancel_requested') THEN RETURN NEW; END IF;
        IF TG_OP='UPDATE' AND (NEW.state=4 OR (NEW.state=3 AND NEW.outcome<>'succeeded' AND NEW.output_count IS NULL)) THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='regression_runs' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='regression'; work:=NEW.run_id; publish:=TG_OP='UPDATE';
        IF TG_OP='UPDATE' AND NEW.state IN ('canceled','revoked','exhausted') THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='build_environments' THEN
        project:=NEW.project_id;
        IF TG_OP='UPDATE' AND NOT NEW.active THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME IN ('revision_derivations','revision_derivation_attempts') THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id;
        IF TG_TABLE_NAME='revision_derivation_attempts' THEN
            v_kind:='derivation'; work:=NEW.derivation_id; publish:=TG_OP='UPDATE';
            IF TG_OP='UPDATE' AND NEW.completion_kind='failed' AND NOT NEW.result_usable THEN RETURN NEW; END IF;
        ELSIF TG_OP='UPDATE' AND NEW.selected_derivation_id IS DISTINCT FROM OLD.selected_derivation_id AND NEW.selected_derivation_id IS NOT NULL THEN
            v_kind:='derivation'; work:=NEW.selected_derivation_id; publish:=true;
        END IF;
    ELSIF TG_TABLE_NAME IN ('import_reviews','export_reviews','revision_import_provenance','catalog_search_report_index','automation_projects','automation_mappings') THEN
        project:=NEW.project_id; mapping:=(to_jsonb(NEW)->>'mapping_id')::uuid;
    ELSIF TG_TABLE_NAME='automation_suites' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='automation'; work:=NEW.suite_id; publish:=TG_OP='UPDATE';
        IF TG_OP='UPDATE' AND NEW.state='failed' THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='automation_evaluations' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='automation'; work:=NEW.suite_id; publish:=true;
        IF TG_OP='UPDATE' AND NEW.state IN ('failed','revoked','exhausted') AND NEW.result_body IS NULL THEN RETURN NEW; END IF;
    ELSIF TG_TABLE_NAME='private_object_publications' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; publish:=true;
        IF NEW.owner_kind='native_ingestion' THEN v_kind:='ingestion'; work:=NEW.ingestion_operation_id;
        ELSE v_kind:='derivation'; work:=NEW.derivation_id; END IF;
    ELSIF TG_TABLE_NAME='chat_conversations' THEN
        -- Expiry erasure is a control-plane operation; no content is added by it.
        IF TG_OP='DELETE' THEN RETURN OLD; END IF;
        project:=coalesce(NEW.project_id,NEW.focus_project_id); mapping:=coalesce(NEW.mapping_id,NEW.focus_mapping_id);
        v_kind:='conversation'; work:=NEW.conversation_id;
    ELSIF TG_TABLE_NAME IN ('chat_turns','chat_rounds','chat_events') THEN
        SELECT * INTO c FROM ONLY braid.chat_conversations WHERE organization_id=NEW.organization_id
            AND creator_id=NEW.creator_id AND conversation_id=NEW.conversation_id;
        project:=c.project_id; mapping:=c.mapping_id;
        -- A withdrawn optional focus is unavailable, not project authority and
        -- not a lifecycle-state oracle for a new organization-scoped turn.
        IF project IS NULL AND c.focus_project_id IS NOT NULL
            AND braid.chat_authorized(NEW.organization_id,c.focus_project_id,NEW.creator_id) IS TRUE THEN
            project:=c.focus_project_id; mapping:=c.focus_mapping_id;
        END IF;
        v_kind:='chat'; work:=NEW.turn_id; publish:=TG_TABLE_NAME<>'chat_turns' OR TG_OP='UPDATE';
        -- Terminal failure/cancellation and acknowledged round accounting never
        -- authorize another provider round or store new prompts/tool output.
        IF TG_TABLE_NAME='chat_turns' THEN
            IF TG_OP='UPDATE' THEN
                IF NEW.phase IN (3,4,5) AND NEW.event_bytes=OLD.event_bytes THEN RETURN NEW; END IF;
                -- Counter preparation is not publication. Deferred ledger conservation
                -- requires the exact event/round before commit; the event fence below
                -- permits only accounting, never prompts, tool output or citations.
                IF NEW.phase=1 AND NEW.round_count=OLD.round_count THEN RETURN NEW; END IF;
            END IF;
        ELSIF TG_TABLE_NAME='chat_rounds' THEN
            IF TG_OP='UPDATE' AND NEW.outcome<>1 THEN RETURN NEW; END IF;
        END IF;
        IF TG_TABLE_NAME='chat_events' THEN
            IF NEW.kind IN (6,7,8,9,10,12) THEN RETURN NEW; END IF;
            IF NEW.kind IN (13,14) THEN
                j:=NEW.metadata_json::jsonb;
                -- Guide citations are organization-scoped and disclose no project.
                IF j->>'project' IS NOT NULL THEN
                    PERFORM braid.lifecycle_assert_active(NEW.organization_id,(j->>'project')::uuid,(j->>'mapping')::uuid);
                    INSERT INTO braid.lifecycle_work_scopes(organization_id,work_id,project_id,mapping_id,project_epoch,mapping_epoch)
                        SELECT NEW.organization_id,work,(j->>'project')::uuid,(j->>'mapping')::uuid,p.write_epoch,coalesce(m.write_epoch,1)
                        FROM ONLY braid.resource_lifecycles p LEFT JOIN ONLY braid.resource_lifecycles m ON m.organization_id=p.organization_id
                            AND m.resource_kind='mapping' AND m.resource_id=(j->>'mapping')::uuid AND m.project_id=p.project_id
                        WHERE p.organization_id=NEW.organization_id AND p.resource_kind='project' AND p.resource_id=(j->>'project')::uuid
                        ON CONFLICT DO NOTHING;
                END IF;
            END IF;
        END IF;
    ELSIF TG_TABLE_NAME='build_approvals' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='build'; work:=NEW.build_id;
    ELSIF TG_TABLE_NAME IN ('build_runs','build_publications','build_qualifications') THEN
        SELECT a.project_id,a.mapping_id INTO project,mapping FROM ONLY braid.build_approvals a
            WHERE a.organization_id=NEW.organization_id AND a.build_id=NEW.build_id;
        IF NOT FOUND THEN RETURN NEW; END IF; -- The original foreign key reports the missing approval.
        v_kind:='build'; work:=NEW.build_id; publish:=true;
    ELSIF TG_TABLE_NAME='local_releases' THEN
        project:=NEW.project_id; mapping:=NEW.mapping_id; v_kind:='release'; work:=NEW.release_id;
    ELSIF TG_TABLE_NAME='local_release_targets' THEN
        -- Disabling, grants and invocation keep serving the existing pointer.
        IF TG_OP='UPDATE' THEN
            IF NEW.active_release_id IS NULL OR (NEW.active_release_id IS NOT DISTINCT FROM OLD.active_release_id AND NOT (NEW.enabled AND NOT OLD.enabled)) THEN RETURN NEW; END IF;
        END IF;
        project:=NEW.project_id; mapping:=NEW.mapping_id;
        IF TG_OP='UPDATE' AND NEW.active_release_id IS DISTINCT FROM OLD.active_release_id THEN v_kind:='release'; work:=NEW.active_release_id; publish:=true; END IF;
    ELSE
        RAISE EXCEPTION 'Unknown lifecycle fence.' USING ERRCODE='22023';
    END IF;
    IF TG_TABLE_NAME IN ('chat_turns','chat_rounds','chat_events') THEN
        PERFORM braid.lifecycle_gate(NEW.organization_id,false);
        IF EXISTS(SELECT 1 FROM ONLY braid.lifecycle_work_scopes s
            LEFT JOIN ONLY braid.resource_lifecycles p ON p.organization_id=s.organization_id AND p.resource_kind='project' AND p.resource_id=s.project_id
            LEFT JOIN ONLY braid.resource_lifecycles m ON m.organization_id=s.organization_id AND m.resource_kind='mapping' AND m.resource_id=s.mapping_id AND m.project_id=s.project_id
            WHERE s.organization_id=NEW.organization_id AND s.work_id=work AND (p.resource_id IS NULL OR p.state='archived'
                OR p.write_epoch<>s.project_epoch OR (s.mapping_id IS NOT NULL AND (m.resource_id IS NULL OR m.state='archived' OR m.write_epoch<>s.mapping_epoch)))) THEN
            RAISE EXCEPTION 'The turn contains an archived or stale authoring scope.' USING ERRCODE='PWA01';
        END IF;
    END IF;
    -- An unfocused workspace conversation is organization-scoped. Its resolved
    -- citations and data-sharing events check their own exact scopes above.
    IF project IS NULL AND TG_TABLE_NAME IN ('chat_conversations','chat_turns','chat_rounds','chat_events') THEN
        -- Previously admitted project-scoped work still owns its original epoch
        -- fence even after focus authority is withdrawn. New unavailable-focus
        -- turns have no such admission and remain organization-scoped.
        SELECT a.project_id,a.mapping_id INTO project,mapping FROM ONLY braid.lifecycle_work_admissions a
            WHERE a.organization_id=NEW.organization_id AND a.work_kind=v_kind AND a.work_id=work;
        IF NOT FOUND THEN RETURN NEW; END IF;
    END IF;
    -- The owning table's guard refuses an untrusted transaction profile first, with its own refusal.
    IF TG_TABLE_NAME IN ('private_object_publications','mapping_revisions','revision_ingestions','revision_ingestion_attempts','revision_derivations','revision_derivation_attempts') THEN
        PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    END IF;
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle fences require read committed transactions.' USING ERRCODE='42501';
    END IF;
    PERFORM braid.lifecycle_gate(NEW.organization_id,false);
    -- A separate statement after the gate wait observes any committed archive.
    -- Effective state is the intersection of project and mapping lifecycle.
    SELECT coalesce(p.write_epoch,1) AS project_epoch,coalesce(m.write_epoch,1) AS mapping_epoch,
        coalesce(p.state='archived',false) OR coalesce(m.state='archived',false) AS archived INTO observed
    FROM (SELECT 1) one
    LEFT JOIN ONLY braid.resource_lifecycles p ON p.organization_id=NEW.organization_id AND p.resource_kind='project' AND p.resource_id=project
    LEFT JOIN ONLY braid.resource_lifecycles m ON mapping IS NOT NULL AND m.organization_id=NEW.organization_id AND m.resource_kind='mapping'
        AND m.resource_id=mapping AND m.project_id=project;
    IF observed.archived THEN
        RAISE EXCEPTION 'The project or mapping is archived.' USING ERRCODE='PWA01';
    END IF;
    PERFORM braid.lifecycle_assert_active(NEW.organization_id,project,mapping);
    IF v_kind IS NULL THEN RETURN NEW; END IF;
    IF publish THEN
        SELECT * INTO admitted FROM ONLY braid.lifecycle_work_admissions
            WHERE organization_id=NEW.organization_id AND work_kind=v_kind AND work_id=work;
        IF FOUND AND ROW(admitted.project_id,admitted.mapping_id) IS DISTINCT FROM ROW(project,mapping) THEN
            RAISE EXCEPTION 'The work was admitted for another scope.' USING ERRCODE='42501';
        END IF;
        -- Work admitted before this migration was admitted at the first epoch.
        IF coalesce(admitted.project_epoch,1)<>observed.project_epoch OR coalesce(admitted.mapping_epoch,1)<>observed.mapping_epoch THEN
            RAISE EXCEPTION 'The work was admitted before an archive.' USING ERRCODE='PWA01';
        END IF;
    ELSE
        INSERT INTO braid.lifecycle_work_admissions(organization_id,work_kind,work_id,project_id,mapping_id,project_epoch,mapping_epoch,admitted_at)
            VALUES(NEW.organization_id,v_kind,work,project,mapping,observed.project_epoch,observed.mapping_epoch,clock_timestamp())
            ON CONFLICT DO NOTHING;
        IF NOT FOUND AND NOT EXISTS(SELECT 1 FROM ONLY braid.lifecycle_work_admissions WHERE organization_id=NEW.organization_id
            AND work_kind=v_kind AND work_id=work AND project_id=project AND mapping_id IS NOT DISTINCT FROM mapping) THEN
            RAISE EXCEPTION 'The work was admitted for another scope.' USING ERRCODE='42501';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER lifecycle_fence BEFORE UPDATE ON braid.mappings FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.revision_ingestions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.revision_ingestion_attempts FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.mapping_revisions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.regression_cases FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.regression_case_versions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.preview_runs FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.regression_runs FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.build_approvals FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.build_runs FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.build_publications FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.build_qualifications FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.local_releases FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.local_release_targets FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.build_environments FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.revision_derivations FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
-- The original BEFORE guard issues the attempt UUID. Capture that identity only
-- after it exists; the transaction/statement gate already precedes row locks.
CREATE TRIGGER lifecycle_fence AFTER INSERT OR UPDATE ON braid.revision_derivation_attempts FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.import_reviews FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.export_reviews FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.revision_import_provenance FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.catalog_search_report_index FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.automation_projects FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.automation_mappings FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.automation_suites FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.automation_evaluations FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.private_object_publications FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE OR DELETE ON braid.chat_conversations FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.chat_turns FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT OR UPDATE ON braid.chat_rounds FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();
CREATE TRIGGER lifecycle_fence BEFORE INSERT ON braid.chat_events FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_fence();

-- Any active project member may observe lifecycle; only the exact-project
-- administrator may change it. Absent and unreadable resources are identical.
CREATE FUNCTION braid.read_resource_lifecycle(p_org uuid,p_actor uuid,p_project uuid,p_mapping uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE p braid.resource_lifecycles%ROWTYPE; m braid.resource_lifecycles%ROWTYPE; administrator boolean;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS NULL OR p_project IS NULL THEN RETURN NULL; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.principals x JOIN ONLY braid.organization_memberships o ON o.principal_id=x.id
        JOIN ONLY braid.project_memberships pm ON pm.organization_id=o.organization_id AND pm.principal_id=x.id
        WHERE x.id=p_actor AND x.active AND o.organization_id=p_org AND o.active AND pm.project_id=p_project AND pm.active
            AND pm.role IN ('viewer','runner','publisher','administrator')) THEN RETURN NULL; END IF;
    IF p_mapping IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND id=p_mapping)
        THEN RETURN NULL; END IF;
    SELECT * INTO p FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='project' AND resource_id=p_project;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF p_mapping IS NOT NULL THEN
        SELECT * INTO m FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='mapping' AND resource_id=p_mapping AND project_id=p_project;
        IF NOT FOUND THEN RETURN NULL; END IF;
    END IF;
    administrator:=braid.administration_authorized(p_org,p_actor,p_project);
    RETURN jsonb_build_object('resourceKind',CASE WHEN p_mapping IS NULL THEN 'project' ELSE 'mapping' END,
        'projectId',p_project,'mappingId',p_mapping,
        'state',CASE WHEN p_mapping IS NULL THEN coalesce(p.state,'active') ELSE coalesce(m.state,'active') END,
        'effectiveState',CASE WHEN coalesce(p.state,'active')='archived' OR coalesce(m.state,'active')='archived' THEN 'archived' ELSE 'active' END,
        'projectVersion',coalesce(p.version,1)::text,'mappingVersion',CASE WHEN p_mapping IS NOT NULL THEN coalesce(m.version,1)::text END,
        'archivedAtUtc',CASE WHEN p_mapping IS NULL THEN p.archived_at ELSE m.archived_at END,
        'canArchive',administrator AND coalesce(CASE WHEN p_mapping IS NULL THEN p.state ELSE m.state END,'active')='active',
        'canRestore',administrator AND coalesce(CASE WHEN p_mapping IS NULL THEN p.state ELSE m.state END,'active')='archived');
END;
$body$;

CREATE FUNCTION braid.lifecycle_receipt(p_org uuid,p_actor uuid,p_operation uuid,p_outcome text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT jsonb_build_object('outcome',p_outcome,'operationId',o.operation_id,'resourceKind',o.resource_kind,
        'projectId',o.project_id,'mappingId',o.mapping_id,'action',o.action,
        'state',CASE o.action WHEN 'archive' THEN 'archived' ELSE 'active' END,
        'projectVersion',o.project_version::text,'mappingVersion',o.mapping_version::text,'recordedAtUtc',o.created_at)
    FROM ONLY braid.lifecycle_operations o WHERE o.organization_id=p_org AND o.actor_id=p_actor AND o.operation_id=p_operation
$body$;

-- One short transaction: exclusive gate, fresh authority after the wait, exact
-- receipt replay, single-step transition, durable receipt and one audit event.
CREATE FUNCTION braid.change_resource_lifecycle(p_org uuid,p_actor uuid,p_operation uuid,p_project uuid,p_mapping uuid,
    p_action text,p_project_version bigint,p_mapping_version bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE kind text:=CASE WHEN p_mapping IS NULL THEN 'project' ELSE 'mapping' END; resource uuid:=coalesce(p_mapping,p_project);
    receipt braid.lifecycle_operations%ROWTYPE; p braid.resource_lifecycles%ROWTYPE; m braid.resource_lifecycles%ROWTYPE;
    target braid.resource_lifecycles%ROWTYPE; at_time timestamptz;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN
        RETURN jsonb_build_object('outcome','refused');
    END IF;
    IF p_actor IS NULL OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_project IS NULL
        OR p_project='00000000-0000-0000-0000-000000000000' OR p_mapping='00000000-0000-0000-0000-000000000000'
        OR p_action IS NULL OR p_action NOT IN ('archive','restore') OR p_project_version IS NULL OR p_project_version<1
        OR (p_mapping IS NULL)<>(p_mapping_version IS NULL) OR p_mapping_version<1 THEN
        RETURN jsonb_build_object('outcome','invalidRequest');
    END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    IF NOT braid.administration_authorized(p_org,p_actor,p_project) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT * INTO receipt FROM ONLY braid.lifecycle_operations WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF FOUND THEN
        IF ROW(receipt.resource_kind,receipt.project_id,receipt.mapping_id,receipt.action,receipt.expected_project_version,receipt.expected_mapping_version)
            IS DISTINCT FROM ROW(kind,p_project,p_mapping,p_action,p_project_version,p_mapping_version) THEN
            RETURN jsonb_build_object('outcome','conflict');
        END IF;
        RETURN braid.lifecycle_receipt(p_org,p_actor,p_operation,'replayed');
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.projects WHERE organization_id=p_org AND id=p_project)
        OR (p_mapping IS NOT NULL AND NOT EXISTS(SELECT 1 FROM ONLY braid.mappings WHERE organization_id=p_org AND project_id=p_project AND id=p_mapping)) THEN
        RETURN jsonb_build_object('outcome','refused');
    END IF;
    SELECT * INTO p FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='project' AND resource_id=p_project
        FOR UPDATE;
    IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    IF p_mapping IS NOT NULL THEN
        SELECT * INTO m FROM ONLY braid.resource_lifecycles WHERE organization_id=p_org AND resource_kind='mapping' AND resource_id=p_mapping
            AND project_id=p_project FOR UPDATE;
        IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    END IF;
    IF coalesce(p.version,1)<>p_project_version OR (p_mapping IS NOT NULL AND m.version<>p_mapping_version) THEN
        RETURN jsonb_build_object('outcome','preconditionFailed');
    END IF;
    target:=CASE WHEN p_mapping IS NULL THEN p ELSE m END;
    IF (p_action='archive')<>(target.state='active') THEN RETURN jsonb_build_object('outcome','conflict'); END IF;
    IF target.version=9223372036854775807 OR target.write_epoch=9223372036854775807 THEN
        RAISE EXCEPTION 'Lifecycle generation overflow.' USING ERRCODE='22003';
    END IF;
    at_time:=clock_timestamp();
    UPDATE braid.resource_lifecycles SET state=CASE p_action WHEN 'archive' THEN 'archived' ELSE 'active' END,
        version=version+1,write_epoch=write_epoch+1,changed_at=at_time,
        archived_at=CASE p_action WHEN 'archive' THEN at_time END,archived_by=CASE p_action WHEN 'archive' THEN p_actor END
        WHERE organization_id=p_org AND resource_kind=kind AND resource_id=resource;
    INSERT INTO braid.lifecycle_operations(organization_id,actor_id,operation_id,resource_kind,project_id,mapping_id,action,
        expected_project_version,expected_mapping_version,project_version,mapping_version,created_at)
        VALUES(p_org,p_actor,p_operation,kind,p_project,p_mapping,p_action,p_project_version,p_mapping_version,
            CASE WHEN p_mapping IS NULL THEN p_project_version+1 ELSE p_project_version END,p_mapping_version+1,at_time);
    PERFORM braid.audit_log_append('resource_lifecycles',p_actor::text||':'||p_operation::text,p_org,p_project,p_mapping,p_actor,'user',
        'administration',kind||CASE p_action WHEN 'archive' THEN 'Archived' ELSE 'Restored' END,resource,
        CASE p_action WHEN 'archive' THEN 'archived' ELSE 'restored' END,at_time,false);
    RETURN braid.lifecycle_receipt(p_org,p_actor,p_operation,CASE p_action WHEN 'archive' THEN 'archived' ELSE 'restored' END);
END;
$body$;

-- Recovery for an unknown commit: the original actor reads its own receipt under
-- fresh exact-project administration. Reading never repeats the mutation.
CREATE FUNCTION braid.read_lifecycle_operation(p_org uuid,p_actor uuid,p_operation uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE receipt braid.lifecycle_operations%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS NULL OR p_operation IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO receipt FROM ONLY braid.lifecycle_operations WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF NOT FOUND OR NOT braid.administration_authorized(p_org,p_actor,receipt.project_id) THEN RETURN NULL; END IF;
    RETURN braid.lifecycle_receipt(p_org,p_actor,p_operation,'recorded');
END;
$body$;

-- Whether admitted work may still take effect: 'archived' while the effective
-- scope is archived, 'stale' when its admission predates an archive and restore
-- or names another scope, otherwise 'current'.
CREATE FUNCTION braid.lifecycle_work_state(p_org uuid,p_kind text,p_work uuid,p_project uuid,p_mapping uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE observed record; admitted braid.lifecycle_work_admissions%ROWTYPE;
BEGIN
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle fences require read committed transactions.' USING ERRCODE='42501';
    END IF;
    PERFORM braid.lifecycle_gate(p_org,false);
    SELECT coalesce(p.write_epoch,1) AS project_epoch,coalesce(m.write_epoch,1) AS mapping_epoch,
        coalesce(p.state='archived',false) OR coalesce(m.state='archived',false) AS archived INTO observed
    FROM (SELECT 1) one
    LEFT JOIN ONLY braid.resource_lifecycles p ON p.organization_id=p_org AND p.resource_kind='project' AND p.resource_id=p_project
    LEFT JOIN ONLY braid.resource_lifecycles m ON p_mapping IS NOT NULL AND m.organization_id=p_org AND m.resource_kind='mapping'
        AND m.resource_id=p_mapping AND m.project_id=p_project;
    IF observed.archived THEN RETURN 'archived'; END IF;
    SELECT * INTO admitted FROM ONLY braid.lifecycle_work_admissions WHERE organization_id=p_org AND work_kind=p_kind AND work_id=p_work;
    IF (FOUND AND ROW(admitted.project_id,admitted.mapping_id) IS DISTINCT FROM ROW(p_project,p_mapping))
        OR coalesce(admitted.project_epoch,1)<>observed.project_epoch OR coalesce(admitted.mapping_epoch,1)<>observed.mapping_epoch THEN
        RETURN 'stale';
    END IF;
    RETURN 'current';
END;
$body$;

-- Schema 19's claim, revoking a run whose scope is archived or whose admission
-- predates an archive so discovery and lease recovery never return it.
CREATE OR REPLACE FUNCTION braid.claim_build_run(org uuid, id uuid, worker uuid)
RETURNS TABLE(actor uuid, intent bytea, claim uuid, attempt integer, lease timestamp with time zone)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE; r braid.build_runs%ROWTYPE; p braid.build_publications%ROWTYPE; now_at timestamp with time zone;
BEGIN
    IF session_user<>'braid_build_dispatcher' OR current_user<>'braid_migrator' OR worker IS NULL OR worker='00000000-0000-0000-0000-000000000000'
        OR coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')<>'' THEN RAISE EXCEPTION 'Unbound build dispatcher required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN; END IF;
    a:=braid.lock_build_run_authority(org,id);
    IF a.actor_id IS NULL THEN RETURN; END IF;
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id;
    IF NOT FOUND OR r.state NOT IN ('queued','running') THEN RETURN; END IF;
    now_at:=clock_timestamp();
    IF (r.state='queued' AND r.eligible_at>now_at) OR (r.state='running' AND r.lease_expires>now_at AND r.execution_deadline>now_at) THEN RETURN; END IF;
    IF braid.build_approval_active(org,a.approval_id,a.actor_id) IS NOT TRUE
        OR braid.lifecycle_work_state(org,'build',id,a.project_id,a.mapping_id)<>'current' THEN
        UPDATE braid.build_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN;
    END IF;
    IF r.attempt>=3 THEN
        SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
        UPDATE braid.build_runs SET state=CASE WHEN FOUND THEN 'uncertain' ELSE 'exhausted' END,
            artifact_digest=CASE WHEN FOUND THEN p.artifact_digest ELSE NULL END,artifact_bytes=CASE WHEN FOUND THEN octet_length(p.artifact_body) ELSE NULL END,
            worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN;
    END IF;
    UPDATE braid.build_runs SET state='running',attempt=r.attempt+1,worker_id=worker,claim_token=gen_random_uuid(),
        lease_expires=now_at+interval '90 seconds',execution_deadline=now_at+interval '6 minutes' WHERE organization_id=org AND build_id=id;
    RETURN QUERY SELECT x.actor_id,x.intent_body,x.claim_token,x.attempt,x.lease_expires FROM ONLY braid.build_runs x WHERE x.organization_id=org AND x.build_id=id;
END;
$body$;

-- Schema 19's transition, revoking a running build at its next renewal or
-- settlement once its scope is archived or its admission is stale.
CREATE OR REPLACE FUNCTION braid.transition_build_run(operation text, org uuid, id uuid, owner uuid, intent_sha text, worker uuid, claim uuid, generation integer, result integer, digest text, bytes integer) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE a braid.build_approvals%ROWTYPE; r braid.build_runs%ROWTYPE; p braid.build_publications%ROWTYPE; now_at timestamp with time zone;
BEGIN
    IF session_user<>'braid_build_dispatcher' OR current_user<>'braid_migrator'
        OR coalesce(nullif(current_setting('braid.build_principal_id',true),''),'')<>'' THEN RAISE EXCEPTION 'Unbound build dispatcher required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN false; END IF;
    a:=braid.lock_build_run_authority(org,id);
    IF a.actor_id IS NULL OR a.actor_id IS DISTINCT FROM owner OR a.intent_digest IS DISTINCT FROM intent_sha THEN RETURN false; END IF;
    SELECT * INTO r FROM ONLY braid.build_runs WHERE organization_id=org AND build_id=id;
    now_at:=clock_timestamp();
    IF NOT FOUND OR r.state<>'running' OR r.worker_id IS DISTINCT FROM worker OR r.claim_token IS DISTINCT FROM claim
        OR r.attempt IS DISTINCT FROM generation OR r.lease_expires<=now_at OR r.execution_deadline<=now_at THEN RETURN false; END IF;
    IF braid.build_approval_active(org,a.approval_id,a.actor_id) IS NOT TRUE
        OR braid.lifecycle_work_state(org,'build',id,a.project_id,a.mapping_id)<>'current' THEN
        UPDATE braid.build_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN false;
    END IF;
    IF operation='renew' THEN
        UPDATE braid.build_runs SET lease_expires=greatest(r.lease_expires,least(now_at+interval '90 seconds',r.execution_deadline)) WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSIF operation='release' THEN
        SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
        UPDATE braid.build_runs SET state=CASE WHEN r.attempt<3 THEN 'queued' WHEN FOUND THEN 'uncertain' ELSE 'exhausted' END,
            artifact_digest=CASE WHEN r.attempt=3 AND FOUND THEN p.artifact_digest ELSE NULL END,
            artifact_bytes=CASE WHEN r.attempt=3 AND FOUND THEN octet_length(p.artifact_body) ELSE NULL END,
            eligible_at=now_at+interval '2 seconds',worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSIF operation='finish' THEN
        IF result IS NULL OR result NOT BETWEEN 0 AND 5 THEN RETURN false; END IF;
        IF result IN (0,1) THEN
            SELECT * INTO p FROM ONLY braid.build_publications WHERE organization_id=org AND build_id=id AND state=2;
            IF NOT FOUND OR p.approval_id<>a.approval_id OR p.artifact_digest IS DISTINCT FROM digest
                OR octet_length(p.artifact_body) IS DISTINCT FROM bytes OR encode(sha256(p.artifact_body),'hex') IS DISTINCT FROM digest THEN RETURN false; END IF;
        ELSIF digest IS NOT NULL OR bytes IS NOT NULL THEN RETURN false; END IF;
        UPDATE braid.build_runs SET state=CASE WHEN result IN (0,1) THEN 'succeeded' ELSE 'failed' END,outcome=result,
            artifact_digest=digest,artifact_bytes=bytes,worker_id=NULL,claim_token=NULL,lease_expires=NULL,execution_deadline=NULL WHERE organization_id=org AND build_id=id;
        RETURN true;
    ELSE RAISE EXCEPTION 'Unknown build transition.' USING ERRCODE='22023'; END IF;
END;
$body$;

-- Schema 22's apply, settling a release whose scope is archived or whose
-- admission is stale as failed without moving the target pointer. An archived
-- scope is reported through a transaction-local setting so the outcome codes
-- recorded by release operations keep their meaning.
CREATE OR REPLACE FUNCTION braid.transition_local_release(operation text, org uuid, project uuid, mapping uuid, environment uuid, target_version bigint, target uuid, key uuid) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; r braid.local_releases%ROWTYPE; t braid.local_release_targets%ROWTYPE;
    q braid.build_qualifications%ROWTYPE; reason text:='none'; state text:='active'; event text:='activated'; outcome integer:=0; lifecycle text;
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
    IF operation='apply' THEN lifecycle:=braid.lifecycle_work_state(org,'release',key,project,mapping); END IF;
    IF operation='cancel' THEN state:='canceled'; event:='canceled'; outcome:=2;
    ELSIF lifecycle<>'current' THEN reason:='targetUnavailable';
        IF lifecycle='archived' THEN PERFORM set_config('braid.lifecycle_refused','archived',true); END IF;
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

REVOKE ALL ON FUNCTION braid.lifecycle_guard(),braid.lifecycle_gate(uuid,boolean),braid.lifecycle_fence(),
    braid.read_resource_lifecycle(uuid,uuid,uuid,uuid),braid.lifecycle_receipt(uuid,uuid,uuid,text),
    braid.change_resource_lifecycle(uuid,uuid,uuid,uuid,uuid,text,bigint,bigint),braid.read_lifecycle_operation(uuid,uuid,uuid),
    braid.lifecycle_work_state(uuid,text,uuid,uuid,uuid),braid.lifecycle_assert_active(uuid,uuid,uuid),braid.lifecycle_identity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_resource_lifecycle(uuid,uuid,uuid,uuid),braid.change_resource_lifecycle(uuid,uuid,uuid,uuid,uuid,text,bigint,bigint),
    braid.read_lifecycle_operation(uuid,uuid,uuid) TO braid_administration;
-- Catalog lists and search filter archived resources inside their own statement.
GRANT SELECT ON braid.resource_lifecycles TO braid_catalog_reader;

-- Archive settlement is a control-plane operation, not provider/compiler
-- authority. It never records successful output or grants a replacement lease.
-- It settles only archive-fenced work; requested preview cancellation, revoked
-- authority and expiry stay with the preview functions and their outcomes.
CREATE FUNCTION braid.lifecycle_settle_background(p_org uuid,p_kind text,p_work uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE scope record;
BEGIN
    IF p_org IS NULL OR p_work IS NULL THEN RETURN; END IF;
    PERFORM braid.lifecycle_gate(p_org,false);
    IF p_kind='preview' THEN
        SELECT project_id,mapping_id INTO scope FROM ONLY braid.preview_runs WHERE organization_id=p_org AND run_id=p_work AND state IN (1,2);
        IF FOUND AND braid.lifecycle_work_state(p_org,p_kind,p_work,scope.project_id,scope.mapping_id)<>'current' THEN
            UPDATE braid.preview_runs SET state=4,outcome='canceled',lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,finished_at=clock_timestamp()
                WHERE organization_id=p_org AND run_id=p_work AND state IN (1,2);
        END IF;
    ELSIF p_kind='regression' THEN
        SELECT project_id,mapping_id INTO scope FROM ONLY braid.regression_runs WHERE organization_id=p_org AND run_id=p_work AND state IN ('queued','running');
        IF FOUND AND braid.lifecycle_work_state(p_org,p_kind,p_work,scope.project_id,scope.mapping_id)<>'current' THEN
            UPDATE braid.regression_runs SET state='revoked',worker_id=NULL,claim_token=NULL,lease_expires=NULL
                WHERE organization_id=p_org AND run_id=p_work AND state IN ('queued','running');
        END IF;
    ELSIF p_kind='automation' THEN
        SELECT project_id,mapping_id INTO scope FROM ONLY braid.automation_suites WHERE organization_id=p_org AND suite_id=p_work AND state='queued';
        IF FOUND AND braid.lifecycle_work_state(p_org,p_kind,p_work,scope.project_id,scope.mapping_id)<>'current' THEN
            UPDATE braid.automation_evaluations SET state='revoked',token=NULL,lease_expires=NULL,pending_comparison=NULL,pending_comparison_digest=NULL
                WHERE organization_id=p_org AND suite_id=p_work AND state IN ('queued','running');
            PERFORM braid.automation_settle_suite(p_org,p_work);
        END IF;
    ELSE RAISE EXCEPTION 'Unknown lifecycle background family.' USING ERRCODE='22023'; END IF;
END;
$body$;
REVOKE ALL ON FUNCTION braid.lifecycle_settle_background(uuid,text,uuid) FROM PUBLIC;

-- A global dispatcher discovers one identity without locking, then takes that
-- organization's gate. The actual claim query is restricted to that identity
-- and runs after the wait, never selecting a different organization mid-lock.
CREATE FUNCTION braid.lifecycle_dispatch_gate(p_kind text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE candidate record;
BEGIN
    IF p_kind='evaluation' THEN
        SELECT organization_id,evaluation_id AS id,suite_id INTO candidate FROM ONLY braid.automation_evaluations
            WHERE state IN ('queued','running') AND eligible_at<=clock_timestamp() AND (token IS NULL OR lease_expires<=clock_timestamp())
            ORDER BY eligible_at,evaluation_id LIMIT 1;
    ELSIF p_kind='comparison' THEN
        SELECT organization_id,evaluation_id AS id,suite_id INTO candidate FROM ONLY braid.automation_evaluations
            WHERE state='running' AND pending_comparison IS NOT NULL AND lease_expires>clock_timestamp() ORDER BY eligible_at,evaluation_id LIMIT 1;
    ELSIF p_kind='delivery' THEN
        SELECT organization_id,delivery_id AS id,NULL::uuid AS suite_id INTO candidate FROM ONLY braid.notification_deliveries
            WHERE state IN ('queued','running') AND eligible_at<=clock_timestamp() AND (token IS NULL OR lease_expires<=clock_timestamp())
            ORDER BY eligible_at,delivery_id LIMIT 1;
    ELSE RAISE EXCEPTION 'Unknown lifecycle dispatcher family.' USING ERRCODE='22023'; END IF;
    IF FOUND THEN
        PERFORM braid.lifecycle_gate(candidate.organization_id,false);
        PERFORM set_config('braid.organization_id',candidate.organization_id::text,true);
        PERFORM set_config('braid.lifecycle_dispatch_id',candidate.id::text,true);
        IF candidate.suite_id IS NOT NULL THEN PERFORM braid.lifecycle_settle_background(candidate.organization_id,'automation',candidate.suite_id); END IF;
    ELSE
        PERFORM set_config('braid.lifecycle_dispatch_id','',true);
    END IF;
END;
$body$;
REVOKE ALL ON FUNCTION braid.lifecycle_dispatch_gate(text) FROM PUBLIC;

-- Semantic ports acquire the gate before their first authority, quota or work
-- lock. Keep the original signatures, ownership, attributes and grants. The
-- exact source transformation and this inventory are admitted by the adapter.
DO $entrypoints$
DECLARE entry record; identity regprocedure; source text; definition text; changed text; offset_at integer; prelude text;
BEGIN
    FOR entry IN SELECT * FROM (VALUES
        ('create_project_v1','p_organization_id'),
        ('index_catalog_report','organization'),
        ('write_administration','org'),
        ('redeem_organization_invitation','org'),
        ('write_ai_quota','p_org'),
        ('reserve_ai_quota','p_org'),
        ('ai_quota_require_reservation','body_only'),
        ('guard_chat_conversation','body_only'),
        ('guard_chat_child','body_only'),
        ('save_regression_case','org'),
        ('submit_regression_run','org'),
        ('claim_regression_run','org'),
        ('finish_regression_run','org'),
        ('release_regression_run','org'),
        ('cancel_regression_run','org'),
        ('write_build_record','org'),
        ('lock_build_run_authority','org'),
        ('write_build_run','org'),
        ('claim_build_run','org'),
        ('transition_build_run','org'),
        ('write_qualification','org'),
        ('lock_local_release_scope','org'),
        ('write_local_target','org'),
        ('write_local_release','org'),
        ('transition_local_release','org'),
        ('operate_release_target','org'),
        ('write_invocation_grant','org'),
        ('claim_machine_invocation','org'),
        ('finish_machine_invocation','org'),
        ('automation_write','org'),
        ('automation_settle_suite','org'),
        ('automation_finish_evaluation','org'),
        ('automation_complete_comparison','org'),
        ('automation_finish_delivery','org'),
        ('automation_claim_evaluation','global_evaluation'),
        ('automation_read_comparison','global_comparison'),
        ('automation_claim_delivery','global_delivery'),
        ('chat_reserve_identity','p_org'),
        ('chat_lifecycle_close','p_org'),
        ('chat_erase_expired','p_org'),
        ('chat_lifecycle_erase','p_org'),
        ('preview_submit','p_org'),
        ('preview_submit_xml','p_org'),
        ('preview_submit_draft','p_org'),
        ('preview_claim','p_org'),
        ('preview_complete','p_org'),
        ('preview_complete_csv','p_org'),
        ('preview_complete_xml','p_org'),
        ('preview_release','p_org'),
        ('preview_cancel','p_org')
    ) inventory(name,organization_argument) LOOP
        SELECT p.oid::regprocedure,p.prosrc,pg_get_functiondef(p.oid) INTO STRICT identity,source,definition
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=entry.name;
        offset_at:=strpos(source,E'\nBEGIN\n');
        IF offset_at=0 THEN RAISE EXCEPTION 'Lifecycle entrypoint has no canonical block: %',identity; END IF;
        IF entry.organization_argument='body_only' THEN
            changed:=source;
        ELSIF entry.organization_argument LIKE 'global_%' THEN
            changed:=overlay(source PLACING E'\nBEGIN\n    PERFORM braid.lifecycle_dispatch_gate('''||substring(entry.organization_argument FROM 8)||E''');\n' FROM offset_at FOR 7);
            changed:=replace(changed,'WHERE x.state',CASE WHEN entry.name='automation_claim_delivery'
                THEN 'WHERE x.delivery_id=NULLIF(current_setting(''braid.lifecycle_dispatch_id'',true),'''')::uuid AND x.state'
                ELSE 'WHERE x.evaluation_id=NULLIF(current_setting(''braid.lifecycle_dispatch_id'',true),'''')::uuid AND x.state' END);
        ELSE
            prelude:=E'\nBEGIN\n    PERFORM pg_advisory_xact_lock_shared(1464158804,hashtext(''braid.lifecycle:''||'||entry.organization_argument||E'::text));\n';
            IF entry.name IN ('preview_claim','preview_complete','preview_complete_csv','preview_complete_xml','preview_release') THEN
                prelude:=prelude||E'    PERFORM braid.lifecycle_settle_background(p_org,''preview'',p_run);\n';
                IF entry.name<>'preview_claim' THEN
                    prelude:=prelude||E'    IF NOT EXISTS(SELECT 1 FROM ONLY braid.preview_runs WHERE organization_id=p_org AND run_id=p_run AND state=2 AND lease_token=p_token AND lease_expires_at>clock_timestamp()) THEN RETURN ''claimLost''; END IF;\n';
                END IF;
            ELSIF entry.name IN ('claim_regression_run','finish_regression_run','release_regression_run') THEN
                prelude:=prelude||E'    PERFORM braid.lifecycle_settle_background(org,''regression'',id);\n';
            ELSIF entry.name IN ('automation_finish_evaluation','automation_complete_comparison') THEN
                prelude:=prelude||E'    PERFORM braid.lifecycle_settle_background(org,''automation'',suite);\n';
            END IF;
            changed:=overlay(source PLACING prelude FROM offset_at FOR 7);
        END IF;
        -- Deadline clocks are sampled only after the early gate, not at statement
        -- arrival. Keep audit/activation timestamp equality contracts unchanged.
        IF entry.name IN ('claim_regression_run','finish_regression_run','release_regression_run',
            'preview_submit','preview_submit_xml','preview_claim','preview_complete','preview_complete_csv','preview_complete_xml','preview_release','preview_cancel',
            'automation_claim_evaluation','automation_finish_evaluation','automation_read_comparison','automation_complete_comparison','automation_claim_delivery','automation_finish_delivery','reserve_ai_quota') THEN
            changed:=replace(changed,'statement_timestamp()','clock_timestamp()');
        END IF;
        IF entry.name='create_project_v1' THEN
            changed:=replace(changed,E'VALUES(p_organization_id,v_project_id,p_name,p_description,p_actor_id,v_created_at,v_created_at,1);\n',
                E'VALUES(p_organization_id,v_project_id,p_name,p_description,p_actor_id,v_created_at,v_created_at,1);\n'||
                E'            INSERT INTO braid.resource_lifecycles(organization_id,resource_kind,resource_id,project_id,mapping_id,state,version,write_epoch,changed_at)\n'||
                E'                VALUES(p_organization_id,''project'',v_project_id,v_project_id,NULL,''active'',1,1,v_created_at);\n');
            IF strpos(changed,'braid.resource_lifecycles')=0 THEN RAISE EXCEPTION 'Project creation has no canonical project insert.'; END IF;
        END IF;
        IF entry.name='preview_claim' THEN
            changed:=replace(changed,E'run_id = p_run FOR UPDATE;\n',E'run_id = p_run FOR UPDATE;\n    observed_at := clock_timestamp();\n');
        END IF;
        IF entry.name='preview_submit' THEN
            prelude:=E'    SELECT * INTO existing FROM ONLY braid.preview_runs WHERE organization_id=p_org AND submitter_id=p_actor AND request_key='||
                CASE WHEN entry.name='preview_submit' THEN 'p_request_key' ELSE 'p_key' END||E';\n    IF FOUND THEN\n        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.revision_id=p_revision AND existing.request_fingerprint=fingerprint'||
                E' THEN\n            RETURN QUERY SELECT ''replayed''::text,existing.run_id;\n        ELSE RETURN QUERY SELECT ''conflict''::text,NULL::uuid; END IF;\n        RETURN;\n    END IF;\n';
            changed:=replace(changed,'    INSERT INTO braid.preview_runs(',prelude||'    INSERT INTO braid.preview_runs(');
        END IF;
        IF entry.name='ai_quota_require_reservation' THEN
            changed:=replace(changed,'IF admitted IS NULL OR NEW.created_at<>admitted THEN','IF admitted IS NULL THEN');
            changed:=replace(changed,'    RETURN NEW;',E'    NEW.created_at:=admitted;\n    RETURN NEW;');
        ELSIF entry.name='reserve_ai_quota' THEN
            changed:=replace(changed,'    IF p_turn IS NULL',E'    IF coalesce(c.project_id,c.focus_project_id) IS NOT NULL\n        AND braid.chat_authorized(p_org,coalesce(c.project_id,c.focus_project_id),p_actor) IS TRUE THEN\n        PERFORM braid.lifecycle_assert_active(p_org,coalesce(c.project_id,c.focus_project_id),coalesce(c.mapping_id,c.focus_mapping_id));\n    END IF;\n    IF p_turn IS NULL');
        ELSIF entry.name='guard_chat_child' THEN
            -- The earlier owner-defined quota trigger assigns the exact immutable
            -- reservation time. The invoker guard gains no private-table grant.
            changed:=replace(changed,'NEW.created_at<>statement_timestamp()','NEW.created_at>clock_timestamp()');
        END IF;
        IF entry.name IN ('guard_chat_conversation','guard_chat_child') THEN
            changed:=replace(changed,'statement_timestamp()>=','clock_timestamp()>=');
            changed:=replace(changed,'statement_timestamp()<','clock_timestamp()<');
            changed:=replace(changed,'<=statement_timestamp()','<=clock_timestamp()');
        END IF;
        EXECUTE replace(definition,source,changed);
    END LOOP;
END;
$entrypoints$;

-- Direct-SQL adapters bind the organization and take the same gate at transaction
-- start. These admitted statement guards are defense in depth before DML row locks.
CREATE FUNCTION braid.lifecycle_statement_gate() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE org uuid:=NULLIF(current_setting('braid.organization_id',true),'')::uuid;
BEGIN
    IF org IS NOT NULL THEN PERFORM braid.lifecycle_gate(org,false); END IF;
    RETURN NULL;
END;
$body$;
REVOKE ALL ON FUNCTION braid.lifecycle_statement_gate() FROM PUBLIC;
DO $statements$
DECLARE relation text;
BEGIN
    FOREACH relation IN ARRAY ARRAY['mappings','revision_ingestions','revision_ingestion_attempts','mapping_revisions',
        'revision_derivations','revision_derivation_attempts','private_object_publications','import_reviews','export_reviews',
        'revision_import_provenance','chat_identities','chat_conversations','chat_turns','chat_rounds','chat_events'] LOOP
        EXECUTE format('CREATE TRIGGER lifecycle_statement_gate BEFORE INSERT OR UPDATE OR DELETE ON braid.%I FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_statement_gate()',relation);
    END LOOP;
END;
$statements$;
