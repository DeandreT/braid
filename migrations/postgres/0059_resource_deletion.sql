-- Individual erasure of terminal previews, regression cases, unreferenced build
-- environments and creator-owned conversations. Each deletion is one short
-- transaction under the exclusive organization gate with a durable receipt, an
-- identity tombstone where an identity could otherwise be reused and one audit
-- event. Append-only guards admit DELETE only for the exact item named by a
-- receipt written in the same transaction by these owner-defined functions.
CREATE TABLE braid.resource_deletions (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    actor_id uuid NOT NULL CHECK(actor_id<>'00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    resource_kind text COLLATE "C" NOT NULL CHECK(resource_kind IN ('preview','regressionCase','buildEnvironment','conversation')),
    resource_id uuid NOT NULL CHECK(resource_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid CHECK(project_id<>'00000000-0000-0000-0000-000000000000'),
    mapping_id uuid CHECK(mapping_id<>'00000000-0000-0000-0000-000000000000'),
    expected_version bigint CHECK(expected_version>0),
    created_at timestamptz NOT NULL CHECK(isfinite(created_at)),
    transaction_id xid8 NOT NULL,
    PRIMARY KEY(organization_id,actor_id,operation_id),
    CHECK((resource_kind IN ('regressionCase','buildEnvironment'))=(expected_version IS NOT NULL)),
    CHECK(resource_kind='conversation' OR project_id IS NOT NULL),
    CHECK(resource_kind='conversation' OR (resource_kind='buildEnvironment')=(mapping_id IS NULL)),
    CHECK(mapping_id IS NULL OR project_id IS NOT NULL)
);
CREATE INDEX resource_deletion_transaction ON braid.resource_deletions(organization_id,transaction_id);
-- No names, payloads, request documents or digests: only the retired identity.
CREATE TABLE braid.resource_identity_tombstones (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    resource_kind text COLLATE "C" NOT NULL CHECK(resource_kind IN ('preview','regressionCase','buildEnvironment')),
    resource_id uuid NOT NULL CHECK(resource_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL CHECK(project_id<>'00000000-0000-0000-0000-000000000000'),
    mapping_id uuid CHECK(mapping_id<>'00000000-0000-0000-0000-000000000000'),
    submitter_id uuid CHECK(submitter_id<>'00000000-0000-0000-0000-000000000000'),
    request_key uuid CHECK(request_key<>'00000000-0000-0000-0000-000000000000'),
    retired_at timestamptz NOT NULL CHECK(isfinite(retired_at)),
    UNIQUE NULLS NOT DISTINCT(organization_id,resource_kind,project_id,mapping_id,resource_id),
    UNIQUE(organization_id,submitter_id,request_key),
    CHECK((resource_kind='buildEnvironment')=(mapping_id IS NULL)),
    CHECK((resource_kind='preview')=(submitter_id IS NOT NULL) AND (submitter_id IS NULL)=(request_key IS NULL))
);
ALTER TABLE braid.resource_deletions ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.resource_identity_tombstones ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.resource_deletions USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.resource_identity_tombstones USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.resource_deletions,braid.resource_identity_tombstones FROM PUBLIC;
CREATE TRIGGER resource_deletion_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.resource_deletions FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER resource_deletion_no_truncate BEFORE TRUNCATE ON braid.resource_deletions FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER resource_tombstone_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.resource_identity_tombstones FOR EACH ROW EXECUTE FUNCTION braid.lifecycle_guard();
CREATE TRIGGER resource_tombstone_no_truncate BEFORE TRUNCATE ON braid.resource_identity_tombstones FOR EACH STATEMENT EXECUTE FUNCTION braid.lifecycle_guard();

-- Whether this transaction's receipt names exactly this item. A receipt can only
-- be written by delete_resource_item, so no session setting can admit a DELETE.
CREATE FUNCTION braid.item_deletion_admitted(p_kind text,p_org uuid,p_project uuid,p_mapping uuid,p_resource uuid,p_creator uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT session_user='braid_administration' AND EXISTS(SELECT 1 FROM ONLY braid.resource_deletions d
        WHERE d.organization_id=p_org AND d.transaction_id=pg_current_xact_id() AND d.resource_kind=p_kind AND d.resource_id=p_resource
            AND CASE WHEN p_kind='conversation' THEN d.actor_id=p_creator
                ELSE d.project_id=p_project AND d.mapping_id IS NOT DISTINCT FROM p_mapping END)
$body$;

-- An erased case or environment identity can never be created again.
CREATE FUNCTION braid.item_identity_available(p_kind text,p_org uuid,p_project uuid,p_mapping uuid,p_resource uuid) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF EXISTS(SELECT 1 FROM ONLY braid.resource_identity_tombstones t WHERE t.organization_id=p_org AND t.resource_kind=p_kind
        AND t.project_id=p_project AND t.mapping_id IS NOT DISTINCT FROM p_mapping AND t.resource_id=p_resource) THEN
        RAISE EXCEPTION 'The resource identity was deleted.' USING ERRCODE='PWD01';
    END IF;
    RETURN true;
END;
$body$;

-- Repeating a deleted preview's request key cannot execute it again.
CREATE FUNCTION braid.preview_identity_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF EXISTS(SELECT 1 FROM ONLY braid.resource_identity_tombstones t WHERE t.organization_id=NEW.organization_id
        AND (t.submitter_id=NEW.submitter_id AND t.request_key=NEW.request_key
            OR t.resource_kind='preview' AND t.project_id=NEW.project_id AND t.mapping_id=NEW.mapping_id AND t.resource_id=NEW.run_id)) THEN
        RAISE EXCEPTION 'The preview request was deleted.' USING ERRCODE='PWD01';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER preview_identity_guard BEFORE INSERT ON braid.preview_runs FOR EACH ROW EXECUTE FUNCTION braid.preview_identity_guard();

CREATE FUNCTION braid.resource_deletion_authorized(p_org uuid,p_actor uuid,p_project uuid,p_roles text[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY braid.principals x JOIN ONLY braid.organization_memberships o ON o.principal_id=x.id
        WHERE x.id=p_actor AND x.active AND o.organization_id=p_org AND o.active
            AND (p_project IS NULL OR EXISTS(SELECT 1 FROM ONLY braid.project_memberships m WHERE m.organization_id=p_org
                AND m.project_id=p_project AND m.principal_id=p_actor AND m.active AND m.role=ANY(p_roles))))
$body$;

CREATE FUNCTION braid.resource_deletion_receipt(p_org uuid,p_actor uuid,p_operation uuid,p_outcome text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT jsonb_build_object('outcome',p_outcome,'operationId',d.operation_id,'resourceKind',d.resource_kind,'resourceId',d.resource_id,
        'projectId',d.project_id,'mappingId',d.mapping_id,'recordedAtUtc',d.created_at)
    FROM ONLY braid.resource_deletions d WHERE d.organization_id=p_org AND d.actor_id=p_actor AND d.operation_id=p_operation
$body$;

-- Authority follows the item: the preview submitter, the conversation creator,
-- a project publisher or administrator for a case and a project administrator
-- for an environment. Archive does not prevent erasure. Absent and unreadable
-- items are indistinguishable; blockers are reported only after authority.
CREATE FUNCTION braid.delete_resource_item(p_org uuid,p_actor uuid,p_operation uuid,p_kind text,p_project uuid,p_mapping uuid,p_resource uuid,p_expected bigint) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE receipt braid.resource_deletions%ROWTYPE; run braid.preview_runs%ROWTYPE; c braid.chat_conversations%ROWTYPE;
    project uuid:=p_project; mapping uuid:=p_mapping; head bigint; first_count bigint; second_count bigint; at_time timestamptz;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN
        RETURN jsonb_build_object('outcome','refused');
    END IF;
    IF p_actor IS NULL OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_resource IS NULL
        OR p_resource='00000000-0000-0000-0000-000000000000' OR p_kind IS NULL
        OR p_kind NOT IN ('preview','regressionCase','buildEnvironment','conversation')
        OR (p_kind IN ('regressionCase','buildEnvironment'))<>(p_expected IS NOT NULL) OR p_expected<1
        OR (p_kind IN ('regressionCase','buildEnvironment'))<>(p_project IS NOT NULL) OR (p_kind='regressionCase')<>(p_mapping IS NOT NULL)
        OR p_project='00000000-0000-0000-0000-000000000000' OR p_mapping='00000000-0000-0000-0000-000000000000' THEN
        RETURN jsonb_build_object('outcome','invalidRequest');
    END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    IF NOT braid.resource_deletion_authorized(p_org,p_actor,NULL,NULL) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT * INTO receipt FROM ONLY braid.resource_deletions WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF FOUND THEN
        IF ROW(receipt.resource_kind,receipt.resource_id,receipt.expected_version) IS DISTINCT FROM ROW(p_kind,p_resource,p_expected)
            OR (p_project IS NOT NULL AND ROW(receipt.project_id,receipt.mapping_id) IS DISTINCT FROM ROW(p_project,p_mapping)) THEN
            RETURN jsonb_build_object('outcome','conflict');
        END IF;
        RETURN braid.resource_deletion_receipt(p_org,p_actor,p_operation,'replayed');
    END IF;
    IF p_kind='preview' THEN
        SELECT * INTO run FROM ONLY braid.preview_runs WHERE organization_id=p_org AND run_id=p_resource FOR UPDATE;
        IF NOT FOUND OR run.submitter_id<>p_actor OR NOT braid.preview_authorized(p_org,run.project_id,p_actor) THEN
            RETURN jsonb_build_object('outcome','refused');
        END IF;
        IF run.state NOT IN (3,4) THEN RETURN jsonb_build_object('outcome','activeWork'); END IF;
        project:=run.project_id; mapping:=run.mapping_id;
    ELSIF p_kind='regressionCase' THEN
        IF NOT braid.resource_deletion_authorized(p_org,p_actor,p_project,ARRAY['publisher','administrator']) THEN
            RETURN jsonb_build_object('outcome','refused');
        END IF;
        SELECT head_version INTO head FROM ONLY braid.regression_cases
            WHERE organization_id=p_org AND project_id=p_project AND mapping_id=p_mapping AND case_id=p_resource FOR UPDATE;
        IF NOT FOUND THEN RETURN jsonb_build_object('outcome','refused'); END IF;
        IF head<>p_expected THEN RETURN jsonb_build_object('outcome','preconditionFailed'); END IF;
        SELECT count(*) INTO first_count FROM ONLY braid.regression_runs
            WHERE organization_id=p_org AND project_id=p_project AND mapping_id=p_mapping AND case_id=p_resource;
        SELECT count(*) INTO second_count FROM ONLY braid.automation_evaluations
            WHERE organization_id=p_org AND project_id=p_project AND mapping_id=p_mapping AND case_id=p_resource;
        IF first_count+second_count>0 THEN
            RETURN jsonb_build_object('outcome','referencedResource','references',
                jsonb_build_object('regressionRuns',first_count::text,'automationEvaluations',second_count::text));
        END IF;
    ELSIF p_kind='buildEnvironment' THEN
        IF NOT braid.administration_authorized(p_org,p_actor,p_project) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
        -- Lock every version before the final anti-reference check.
        PERFORM 1 FROM ONLY braid.build_environments
            WHERE organization_id=p_org AND project_id=p_project AND environment_id=p_resource ORDER BY version FOR UPDATE;
        SELECT max(version) INTO head FROM ONLY braid.build_environments
            WHERE organization_id=p_org AND project_id=p_project AND environment_id=p_resource;
        IF head IS NULL THEN RETURN jsonb_build_object('outcome','refused'); END IF;
        IF head<>p_expected THEN RETURN jsonb_build_object('outcome','preconditionFailed'); END IF;
        SELECT count(*) INTO first_count FROM ONLY braid.build_approvals
            WHERE organization_id=p_org AND project_id=p_project AND environment_id=p_resource;
        SELECT count(*) INTO second_count FROM ONLY braid.local_release_targets
            WHERE organization_id=p_org AND project_id=p_project AND environment_id=p_resource;
        IF first_count+second_count>0 THEN
            RETURN jsonb_build_object('outcome','referencedResource','references',
                jsonb_build_object('buildApprovals',first_count::text,'releaseTargets',second_count::text));
        END IF;
    ELSE
        SELECT * INTO c FROM ONLY braid.chat_conversations
            WHERE organization_id=p_org AND creator_id=p_actor AND conversation_id=p_resource FOR UPDATE;
        -- The same project read roles as conversation admission; a workspace conversation needs only the organization.
        IF NOT FOUND OR NOT braid.resource_deletion_authorized(p_org,p_actor,c.project_id,ARRAY['viewer','runner','publisher','administrator']) THEN
            RETURN jsonb_build_object('outcome','refused');
        END IF;
        IF EXISTS(SELECT 1 FROM ONLY braid.chat_turns t WHERE t.organization_id=p_org AND t.creator_id=p_actor
            AND t.conversation_id=p_resource AND t.phase=1) THEN
            RETURN jsonb_build_object('outcome','activeWork');
        END IF;
        -- A workspace focus is not recorded: it would disclose private conversation context.
        project:=c.project_id; mapping:=c.mapping_id;
    END IF;
    at_time:=clock_timestamp();
    INSERT INTO braid.resource_deletions(organization_id,actor_id,operation_id,resource_kind,resource_id,project_id,mapping_id,
        expected_version,created_at,transaction_id)
        VALUES(p_org,p_actor,p_operation,p_kind,p_resource,project,mapping,p_expected,at_time,pg_current_xact_id());
    IF p_kind='preview' THEN
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,submitter_id,request_key,retired_at)
            VALUES(p_org,'preview',p_resource,project,mapping,run.submitter_id,run.request_key,at_time);
        DELETE FROM braid.preview_runs WHERE organization_id=p_org AND run_id=p_resource;
    ELSIF p_kind='regressionCase' THEN
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,retired_at)
            VALUES(p_org,'regressionCase',p_resource,project,mapping,at_time);
        -- The header's head reference is deferred, so versions go first.
        DELETE FROM braid.regression_case_versions WHERE organization_id=p_org AND project_id=project AND mapping_id=mapping AND case_id=p_resource;
        DELETE FROM braid.regression_cases WHERE organization_id=p_org AND project_id=project AND mapping_id=mapping AND case_id=p_resource;
    ELSIF p_kind='buildEnvironment' THEN
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,retired_at)
            VALUES(p_org,'buildEnvironment',p_resource,project,at_time);
        DELETE FROM braid.build_environments WHERE organization_id=p_org AND project_id=project AND environment_id=p_resource;
    ELSE
        -- Chat identities stay permanently retired; usage and quota records are independent.
        UPDATE braid.chat_identities SET erased_at=statement_timestamp(),retirement_policy_id='owner-deletion',retirement_policy_version=1,
            retirement_not_before='9999-12-31 23:59:59.999999+00'
            WHERE organization_id=p_org AND creator_id=p_actor AND conversation_id=p_resource AND erased_at IS NULL;
        DELETE FROM braid.chat_conversations WHERE organization_id=p_org AND creator_id=p_actor AND conversation_id=p_resource;
        -- Verify the erasure here as the owner, not at commit under the restricted session.
        SET CONSTRAINTS braid.chat_identity_erasure IMMEDIATE;
    END IF;
    PERFORM braid.audit_log_append('resource_deletions',p_actor::text||':'||p_operation::text,p_org,project,mapping,p_actor,'user',
        'administration',p_kind||'Deleted',p_resource,'deleted',at_time,false);
    RETURN braid.resource_deletion_receipt(p_org,p_actor,p_operation,'deleted');
END;
$body$;

-- Recovery for an unknown commit by the original actor while still admitted to
-- the organization. The receipt outlives the item and never repeats the change.
CREATE FUNCTION braid.read_resource_deletion(p_org uuid,p_actor uuid,p_operation uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS NULL OR p_operation IS NULL OR NOT braid.resource_deletion_authorized(p_org,p_actor,NULL,NULL) THEN RETURN NULL; END IF;
    RETURN braid.resource_deletion_receipt(p_org,p_actor,p_operation,'recorded');
END;
$body$;
REVOKE ALL ON FUNCTION braid.item_deletion_admitted(text,uuid,uuid,uuid,uuid,uuid),braid.item_identity_available(text,uuid,uuid,uuid,uuid),
    braid.preview_identity_guard(),braid.resource_deletion_authorized(uuid,uuid,uuid,text[]),braid.resource_deletion_receipt(uuid,uuid,uuid,text),
    braid.delete_resource_item(uuid,uuid,uuid,text,uuid,uuid,uuid,bigint),braid.read_resource_deletion(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.delete_resource_item(uuid,uuid,uuid,text,uuid,uuid,uuid,bigint),braid.read_resource_deletion(uuid,uuid,uuid) TO braid_administration;

-- Each guard gains one exact prelude after its first block opening. The helpers
-- are reached only inside owner-defined functions, never from restricted DML.
-- The inventory is the admitted contract; every other line stays unchanged.
DO $guards$
DECLARE entry record; source text; definition text; offset_at integer;
BEGIN
    FOR entry IN SELECT * FROM (VALUES
        ('guard_regression_case',$p$    IF current_user='braid_migrator' THEN
        IF TG_OP='DELETE' THEN
            IF braid.item_deletion_admitted('regressionCase',OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.case_id,NULL) THEN RETURN OLD; END IF;
        ELSIF TG_OP='INSERT' THEN
            PERFORM braid.item_identity_available('regressionCase',NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.case_id);
        END IF;
    END IF;
$p$),
        ('guard_regression_version',$p$    IF current_user='braid_migrator' AND TG_OP='DELETE' THEN
        IF braid.item_deletion_admitted('regressionCase',OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.case_id,NULL) THEN RETURN OLD; END IF;
    END IF;
$p$),
        ('guard_build_record',$p$    IF current_user='braid_migrator' AND TG_TABLE_NAME='build_environments' THEN
        IF TG_OP='DELETE' THEN
            IF braid.item_deletion_admitted('buildEnvironment',OLD.organization_id,OLD.project_id,NULL,OLD.environment_id,NULL) THEN RETURN OLD; END IF;
        ELSIF TG_OP='INSERT' THEN
            PERFORM braid.item_identity_available('buildEnvironment',NEW.organization_id,NEW.project_id,NULL,NEW.environment_id);
        END IF;
    END IF;
$p$),
        ('guard_chat_conversation',$p$    IF current_user='braid_migrator' AND TG_OP='DELETE' THEN
        IF braid.item_deletion_admitted('conversation',OLD.organization_id,NULL,NULL,OLD.conversation_id,OLD.creator_id) THEN RETURN OLD; END IF;
    END IF;
$p$),
        ('guard_chat_identity',$p$    IF current_user='braid_migrator' AND TG_OP='UPDATE' THEN
        IF OLD.erased_at IS NULL AND NEW.erased_at IS NOT NULL
            AND (to_jsonb(NEW)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before'])=
                (to_jsonb(OLD)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before']) THEN
            IF braid.item_deletion_admitted('conversation',OLD.organization_id,NULL,NULL,OLD.conversation_id,OLD.creator_id) THEN RETURN NEW; END IF;
        END IF;
    END IF;
$p$)
    ) inventory(name,prelude) LOOP
        SELECT p.prosrc,pg_get_functiondef(p.oid) INTO STRICT source,definition
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=entry.name;
        offset_at:=strpos(source,E'\nBEGIN\n');
        IF offset_at=0 THEN RAISE EXCEPTION 'Deletion guard has no canonical block: %',entry.name; END IF;
        EXECUTE replace(definition,source,overlay(source PLACING E'\nBEGIN\n'||entry.prelude FROM offset_at FOR 7));
    END LOOP;
END;
$guards$;
