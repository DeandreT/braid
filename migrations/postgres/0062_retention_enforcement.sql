-- Retention enforcement, release target retirement and request-key tombstones.
-- * An administrator retires a target: it is disabled, its exact active pointer is
--   cleared and the target event history advances. Release and build history stay.
-- * The lifecycle worker erases preview runs and conversations whose organization
--   policy expired them. Only configured expire policies act, and only on data
--   created after the policy took effect unless it was applied to existing data.
-- * Ingestion and project-creation request keys removed by a permanent deletion
--   are retired, so repeating such a request can never create fresh work.
-- Guards admit these changes only in a transaction holding the exact receipt or
-- sweep row written by the owner functions below.
CREATE TABLE braid.release_target_retirements (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    actor_id uuid NOT NULL CHECK(actor_id<>'00000000-0000-0000-0000-000000000000'),
    operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'),
    project_id uuid NOT NULL, mapping_id uuid NOT NULL, target_id uuid NOT NULL,
    expected_version bigint NOT NULL CHECK(expected_version>0),
    active_release_id uuid NOT NULL,
    created_at timestamptz NOT NULL CHECK(isfinite(created_at)),
    transaction_id xid8 NOT NULL,
    PRIMARY KEY(organization_id,actor_id,operation_id)
);
CREATE INDEX release_target_retirement_transaction ON braid.release_target_retirements(organization_id,transaction_id);
CREATE TABLE braid.retention_sweeps (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    sweep_id uuid NOT NULL CHECK(sweep_id<>'00000000-0000-0000-0000-000000000000'),
    family text COLLATE "C" NOT NULL CHECK(family IN ('preview','conversation')),
    policy_version bigint NOT NULL CHECK(policy_version>0),
    instance_id uuid NOT NULL,
    swept_at timestamptz NOT NULL CHECK(isfinite(swept_at)),
    transaction_id xid8 NOT NULL,
    PRIMARY KEY(organization_id,sweep_id)
);
CREATE INDEX retention_sweep_transaction ON braid.retention_sweeps(organization_id,transaction_id);
-- No payloads, names or fingerprints: only the retired client request identity.
CREATE TABLE braid.retired_request_keys (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    request_kind text COLLATE "C" NOT NULL CHECK(request_kind IN ('ingestion','projectCreation')),
    project_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    operation_kind text COLLATE "C",
    idempotency_key uuid NOT NULL,
    retired_at timestamptz NOT NULL CHECK(isfinite(retired_at)),
    UNIQUE NULLS NOT DISTINCT(organization_id,request_kind,project_id,actor_id,operation_kind,idempotency_key),
    CHECK((request_kind='ingestion')=(operation_kind IS NOT NULL))
);

CREATE FUNCTION braid.enforcement_record_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP<>'INSERT' THEN
        RAISE EXCEPTION 'Enforcement records are append-only.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;

DO $policies$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['release_target_retirements','retention_sweeps','retired_request_keys'] LOOP
        EXECUTE format('ALTER TABLE braid.%I ENABLE ROW LEVEL SECURITY',t);
        EXECUTE format($p$CREATE POLICY organization_scope ON braid.%I USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid)$p$,t);
        EXECUTE format('REVOKE ALL ON braid.%I FROM PUBLIC',t);
        EXECUTE format('CREATE TRIGGER enforcement_record_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.%I FOR EACH ROW EXECUTE FUNCTION braid.enforcement_record_guard()',t);
        EXECUTE format('CREATE TRIGGER enforcement_record_no_truncate BEFORE TRUNCATE ON braid.%I FOR EACH STATEMENT EXECUTE FUNCTION braid.enforcement_record_guard()',t);
    END LOOP;
END;
$policies$;

-- Whether this transaction holds the exact retirement receipt for this target.
CREATE FUNCTION braid.target_retirement_admitted(p_org uuid,p_target uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT session_user='braid_administration' AND EXISTS(SELECT 1 FROM ONLY braid.release_target_retirements r
        WHERE r.organization_id=p_org AND r.target_id=p_target AND r.transaction_id=pg_current_xact_id())
$body$;

-- Whether this transaction is a lifecycle worker retention sweep for this organization.
CREATE FUNCTION braid.retention_sweep_admitted(p_org uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT session_user='braid_lifecycle_worker' AND EXISTS(SELECT 1 FROM ONLY braid.retention_sweeps s
        WHERE s.organization_id=p_org AND s.transaction_id=pg_current_xact_id())
$body$;

-- A purged ingestion or project creation leaves its client key retired; the same
-- key can never reserve fresh work afterwards.
CREATE FUNCTION braid.request_key_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP='DELETE' THEN
        IF TG_TABLE_NAME='revision_ingestions' THEN
            INSERT INTO braid.retired_request_keys(organization_id,request_kind,project_id,actor_id,operation_kind,idempotency_key,retired_at)
                VALUES(OLD.organization_id,'ingestion',OLD.project_id,OLD.actor_principal_id,OLD.operation_kind,OLD.idempotency_key,clock_timestamp())
                ON CONFLICT DO NOTHING;
        ELSE
            INSERT INTO braid.retired_request_keys(organization_id,request_kind,project_id,actor_id,idempotency_key,retired_at)
                VALUES(OLD.organization_id,'projectCreation',OLD.project_id,OLD.original_actor_id,OLD.idempotency_key,clock_timestamp())
                ON CONFLICT DO NOTHING;
        END IF;
        RETURN OLD;
    END IF;
    IF TG_TABLE_NAME='revision_ingestions' THEN
        IF EXISTS(SELECT 1 FROM ONLY braid.retired_request_keys k WHERE k.organization_id=NEW.organization_id AND k.request_kind='ingestion'
            AND k.project_id=NEW.project_id AND k.actor_id=NEW.actor_principal_id AND k.operation_kind=NEW.operation_kind
            AND k.idempotency_key=NEW.idempotency_key) THEN
            RAISE EXCEPTION 'The request was deleted.' USING ERRCODE='PWD01';
        END IF;
    ELSE
        IF EXISTS(SELECT 1 FROM ONLY braid.retired_request_keys k WHERE k.organization_id=NEW.organization_id AND k.request_kind='projectCreation'
            AND k.actor_id=NEW.original_actor_id AND k.idempotency_key=NEW.idempotency_key) THEN
            RAISE EXCEPTION 'The request was deleted.' USING ERRCODE='PWD01';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER request_key_guard BEFORE INSERT OR DELETE ON braid.revision_ingestions FOR EACH ROW EXECUTE FUNCTION braid.request_key_guard();
CREATE TRIGGER request_key_guard BEFORE INSERT OR DELETE ON braid.project_creations FOR EACH ROW EXECUTE FUNCTION braid.request_key_guard();

CREATE FUNCTION braid.retirement_receipt(r braid.release_target_retirements,p_outcome text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT jsonb_build_object('outcome',p_outcome,'operationId',r.operation_id,'projectId',r.project_id,'mappingId',r.mapping_id,
        'targetId',r.target_id,'retiredReleaseId',r.active_release_id,
        'targetVersion',(SELECT max(e.version) FROM ONLY braid.release_target_events e WHERE e.organization_id=r.organization_id AND e.target_id=r.target_id)::text,
        'recordedAtUtc',r.created_at)
$body$;

-- One short transaction: exclusive gate, exact-project administrator after the
-- wait, exact receipt replay, exact target version and active pointer, then the
-- target is disabled and its pointer cleared with one audit event. Release, build
-- and activation history are kept. Archive does not prevent retirement.
CREATE FUNCTION braid.retire_release_target(p_org uuid,p_actor uuid,p_operation uuid,p_project uuid,p_target uuid,
    p_expected bigint,p_active_release uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.release_target_retirements%ROWTYPE; t braid.local_release_targets%ROWTYPE; current_version bigint; at_time timestamptz;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    IF p_actor IS NULL OR p_operation IS NULL OR p_operation='00000000-0000-0000-0000-000000000000' OR p_project IS NULL OR p_target IS NULL
        OR p_expected IS NULL OR p_expected<1 OR p_active_release IS NULL THEN RETURN jsonb_build_object('outcome','invalidRequest'); END IF;
    PERFORM braid.lifecycle_gate(p_org,true);
    IF NOT braid.administration_authorized(p_org,p_actor,p_project) THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT * INTO r FROM ONLY braid.release_target_retirements WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF FOUND THEN
        IF ROW(r.project_id,r.target_id,r.expected_version,r.active_release_id) IS DISTINCT FROM ROW(p_project,p_target,p_expected,p_active_release) THEN
            RETURN jsonb_build_object('outcome','conflict');
        END IF;
        RETURN braid.retirement_receipt(r,'replayed');
    END IF;
    SELECT * INTO t FROM ONLY braid.local_release_targets WHERE organization_id=p_org AND target_id=p_target FOR UPDATE;
    IF NOT FOUND OR t.project_id<>p_project THEN RETURN jsonb_build_object('outcome','refused'); END IF;
    SELECT max(version) INTO current_version FROM ONLY braid.release_target_events WHERE organization_id=p_org AND target_id=p_target;
    IF current_version IS DISTINCT FROM p_expected THEN RETURN jsonb_build_object('outcome','preconditionFailed'); END IF;
    IF t.active_release_id IS DISTINCT FROM p_active_release THEN RETURN jsonb_build_object('outcome','conflict'); END IF;
    at_time:=clock_timestamp();
    INSERT INTO braid.release_target_retirements(organization_id,actor_id,operation_id,project_id,mapping_id,target_id,expected_version,
        active_release_id,created_at,transaction_id)
        VALUES(p_org,p_actor,p_operation,p_project,t.mapping_id,p_target,p_expected,p_active_release,at_time,pg_current_xact_id()) RETURNING * INTO r;
    UPDATE braid.local_release_targets SET enabled=false,active_release_id=NULL WHERE organization_id=p_org AND target_id=p_target;
    PERFORM braid.audit_log_append('release_target_retirements',p_actor::text||':'||p_operation::text,p_org,p_project,t.mapping_id,p_actor,'user',
        'release','releaseTargetRetired',p_target,'retired',at_time,false);
    RETURN braid.retirement_receipt(r,'retired');
END;
$body$;

CREATE FUNCTION braid.read_target_retirement(p_org uuid,p_actor uuid,p_operation uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.release_target_retirements%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid OR p_actor IS NULL OR p_operation IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO r FROM ONLY braid.release_target_retirements WHERE organization_id=p_org AND actor_id=p_actor AND operation_id=p_operation;
    IF NOT FOUND OR NOT braid.resource_deletion_authorized(p_org,p_actor,NULL,NULL) THEN RETURN NULL; END IF;
    RETURN braid.retirement_receipt(r,'recorded');
END;
$body$;

-- One bounded sweep of one family in one organization: the worker discovers a due
-- item without locking, takes that organization's gate, rereads the current
-- policy and due time after the wait and erases at most p_limit items. Previews
-- keep their request identity; conversations retire every chat identity. Active
-- work is skipped and never cancelled. Usage, quota and audit stay untouched.
CREATE FUNCTION braid.sweep_retention(p_instance uuid,p_limit integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE candidate record; p braid.organization_retention_policies%ROWTYPE; at_time timestamptz; n integer:=0; sweep uuid:=gen_random_uuid();
    items uuid[]; creators uuid[];
BEGIN
    IF session_user<>'braid_lifecycle_worker' OR current_user<>'braid_migrator' OR p_instance IS NULL OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Lifecycle worker required.' USING ERRCODE='42501';
    END IF;
    IF current_setting('transaction_isolation')<>'read committed' THEN
        RAISE EXCEPTION 'Lifecycle work requires read committed transactions.' USING ERRCODE='25001';
    END IF;
    SELECT x.organization_id,x.family INTO candidate FROM (
        SELECT DISTINCT ON (o.organization_id,o.family) o.* FROM ONLY braid.organization_retention_policies o
        WHERE o.family IN ('preview','conversation') ORDER BY o.organization_id,o.family,o.version DESC) x
    WHERE x.mode='expire' AND CASE x.family
        WHEN 'preview' THEN EXISTS(SELECT 1 FROM ONLY braid.preview_runs r WHERE r.organization_id=x.organization_id AND r.state IN (3,4)
            AND r.finished_at IS NOT NULL AND (x.applies_to_existing OR r.created_at>=x.effective_at)
            AND r.finished_at+make_interval(secs=>x.duration_seconds)<=clock_timestamp())
        ELSE EXISTS(SELECT 1 FROM ONLY braid.chat_conversations c WHERE c.organization_id=x.organization_id
            AND (x.applies_to_existing OR c.created_at>=x.effective_at) AND c.created_at+make_interval(secs=>x.duration_seconds)<=clock_timestamp()
            AND NOT EXISTS(SELECT 1 FROM ONLY braid.chat_turns t WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id
                AND t.conversation_id=c.conversation_id AND t.phase=1)) END
    ORDER BY x.organization_id,x.family LIMIT 1;
    IF NOT FOUND THEN RETURN jsonb_build_object('erased','0'); END IF;
    PERFORM braid.lifecycle_gate(candidate.organization_id,true);
    PERFORM set_config('braid.organization_id',candidate.organization_id::text,true);
    -- The policy current after the wait decides; a change to retain wins.
    SELECT * INTO p FROM ONLY braid.organization_retention_policies WHERE organization_id=candidate.organization_id AND family=candidate.family
        ORDER BY version DESC LIMIT 1;
    IF p.mode<>'expire' THEN RETURN jsonb_build_object('erased','0'); END IF;
    at_time:=clock_timestamp();
    INSERT INTO braid.retention_sweeps(organization_id,sweep_id,family,policy_version,instance_id,swept_at,transaction_id)
        VALUES(p.organization_id,sweep,p.family,p.version,p_instance,at_time,pg_current_xact_id());
    IF p.family='preview' THEN
        SELECT array_agg(run_id) INTO items FROM (SELECT r.run_id FROM ONLY braid.preview_runs r
            WHERE r.organization_id=p.organization_id AND r.state IN (3,4) AND r.finished_at IS NOT NULL
                AND (p.applies_to_existing OR r.created_at>=p.effective_at) AND r.finished_at+make_interval(secs=>p.duration_seconds)<=at_time
            ORDER BY r.finished_at,r.run_id LIMIT p_limit FOR UPDATE) due;
        INSERT INTO braid.resource_identity_tombstones(organization_id,resource_kind,resource_id,project_id,mapping_id,submitter_id,request_key,retired_at)
            SELECT r.organization_id,'preview',r.run_id,r.project_id,r.mapping_id,r.submitter_id,r.request_key,at_time
            FROM ONLY braid.preview_runs r WHERE r.organization_id=p.organization_id AND r.run_id=ANY(items) ON CONFLICT DO NOTHING;
        DELETE FROM braid.preview_runs r WHERE r.organization_id=p.organization_id AND r.run_id=ANY(items);
        GET DIAGNOSTICS n=ROW_COUNT;
    ELSE
        SELECT array_agg(conversation_id),array_agg(creator_id) INTO items,creators FROM (SELECT c.conversation_id,c.creator_id FROM ONLY braid.chat_conversations c
            WHERE c.organization_id=p.organization_id AND (p.applies_to_existing OR c.created_at>=p.effective_at)
                AND c.created_at+make_interval(secs=>p.duration_seconds)<=at_time
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.chat_turns t WHERE t.organization_id=c.organization_id AND t.creator_id=c.creator_id
                    AND t.conversation_id=c.conversation_id AND t.phase=1)
            ORDER BY c.created_at,c.conversation_id LIMIT p_limit FOR UPDATE) due;
        UPDATE braid.chat_identities i SET erased_at=statement_timestamp(),retirement_policy_id='organization-retention',retirement_policy_version=p.version::integer,
            retirement_not_before='9999-12-31 23:59:59.999999+00'
            FROM unnest(items,creators) d(item,creator)
            WHERE i.organization_id=p.organization_id AND i.creator_id=d.creator AND i.conversation_id=d.item AND i.erased_at IS NULL;
        DELETE FROM braid.chat_conversations c USING unnest(items,creators) d(item,creator)
            WHERE c.organization_id=p.organization_id AND c.creator_id=d.creator AND c.conversation_id=d.item;
        GET DIAGNOSTICS n=ROW_COUNT;
        SET CONSTRAINTS braid.chat_identity_erasure IMMEDIATE;
    END IF;
    PERFORM braid.audit_log_append('retention_sweeps',sweep::text,p.organization_id,NULL,NULL,NULL,'system','administration',
        'retentionApplied',p.organization_id,p.family||'.'||n::text,at_time,false);
    RETURN jsonb_build_object('organizationId',p.organization_id,'family',p.family,'policyVersion',p.version::text,'erased',n::text);
END;
$body$;

REVOKE ALL ON FUNCTION braid.enforcement_record_guard(),braid.target_retirement_admitted(uuid,uuid),braid.retention_sweep_admitted(uuid),
    braid.request_key_guard(),braid.retirement_receipt(braid.release_target_retirements,text),
    braid.retire_release_target(uuid,uuid,uuid,uuid,uuid,bigint,uuid),braid.read_target_retirement(uuid,uuid,uuid),
    braid.sweep_retention(uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.retire_release_target(uuid,uuid,uuid,uuid,uuid,bigint,uuid),braid.read_target_retirement(uuid,uuid,uuid) TO braid_administration;
GRANT EXECUTE ON FUNCTION braid.sweep_retention(uuid,integer) TO braid_lifecycle_worker;

-- Exact preludes, nested so restricted sessions never call the owner-only checks.
DO $guards$
DECLARE entry record; source text; definition text; offset_at integer;
BEGIN
    FOR entry IN SELECT * FROM (VALUES
        ('guard_operations_target',$p$    IF TG_OP='UPDATE' AND current_user='braid_migrator' THEN
        IF NEW.active_release_id IS NULL AND NOT NEW.enabled AND (to_jsonb(NEW)-ARRAY['enabled','active_release_id'])=(to_jsonb(OLD)-ARRAY['enabled','active_release_id']) THEN
            IF braid.target_retirement_admitted(OLD.organization_id,OLD.target_id) THEN RETURN NEW; END IF;
        END IF;
    END IF;
$p$),
        ('guard_release_operations',$p$    IF TG_OP='INSERT' AND current_user='braid_migrator' AND TG_TABLE_NAME='release_target_events' THEN
        IF braid.target_retirement_admitted(NEW.organization_id,NEW.target_id) THEN RETURN NEW; END IF;
    END IF;
$p$),
        ('guard_chat_conversation',$p$    IF TG_OP='DELETE' AND current_user='braid_migrator' THEN
        IF braid.retention_sweep_admitted(OLD.organization_id) THEN RETURN OLD; END IF;
    END IF;
$p$),
        ('guard_chat_identity',$p$    IF TG_OP='UPDATE' AND current_user='braid_migrator' THEN
        IF OLD.erased_at IS NULL AND NEW.erased_at IS NOT NULL
            AND (to_jsonb(NEW)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before'])=
                (to_jsonb(OLD)-ARRAY['erased_at','retirement_policy_id','retirement_policy_version','retirement_not_before']) THEN
            IF braid.retention_sweep_admitted(OLD.organization_id) THEN RETURN NEW; END IF;
        END IF;
    END IF;
$p$)
    ) inventory(name,prelude) LOOP
        SELECT p.prosrc,pg_get_functiondef(p.oid) INTO STRICT source,definition
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid' AND p.proname=entry.name;
        offset_at:=strpos(source,E'\nBEGIN\n');
        IF offset_at=0 THEN RAISE EXCEPTION 'Enforcement guard has no canonical block: %',entry.name; END IF;
        EXECUTE replace(definition,source,overlay(source PLACING E'\nBEGIN\n'||entry.prelude FROM offset_at FOR 7));
    END LOOP;
END;
$guards$;
