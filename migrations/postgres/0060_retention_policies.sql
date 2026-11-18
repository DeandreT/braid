-- Organization retention policy for the four erasable families. There are no
-- default durations: a family without a recorded version is "not configured" and
-- holds its data. Versions are append-only, so every item can be judged against
-- the policy that was in effect for it. A version applies only to data whose
-- basis time follows its effective time unless the administrator explicitly
-- applied it to existing data. Audit, usage, quota, identity tombstones, fences
-- and retained evidence keep their own fixed retention and are not families.
CREATE TABLE braid.organization_retention_state (
    organization_id uuid PRIMARY KEY REFERENCES braid.organizations(id),
    generation bigint NOT NULL DEFAULT 1 CHECK(generation>0)
);
CREATE TABLE braid.organization_retention_policies (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id),
    family text COLLATE "C" NOT NULL CHECK(family IN ('preview','regressionRun','conversation','invocationHistory')),
    version bigint NOT NULL CHECK(version>0),
    mode text COLLATE "C" NOT NULL CHECK(mode IN ('retain','expire')),
    duration_seconds bigint CHECK(duration_seconds BETWEEN 3600 AND 315360000),
    applies_to_existing boolean NOT NULL,
    effective_at timestamptz NOT NULL CHECK(isfinite(effective_at)),
    changed_by uuid NOT NULL CHECK(changed_by<>'00000000-0000-0000-0000-000000000000'),
    PRIMARY KEY(organization_id,family,version),
    CHECK((mode='expire')=(duration_seconds IS NOT NULL)),
    CHECK(mode='expire' OR NOT applies_to_existing)
);
ALTER TABLE braid.organization_retention_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE braid.organization_retention_policies ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.organization_retention_state USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON braid.organization_retention_policies USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.organization_retention_state,braid.organization_retention_policies FROM PUBLIC;

CREATE FUNCTION braid.retention_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR TG_OP IN ('DELETE','TRUNCATE')
        OR (TG_OP='UPDATE' AND TG_TABLE_NAME<>'organization_retention_state') THEN
        RAISE EXCEPTION 'Retention policy history is append-only.' USING ERRCODE='42501';
    END IF;
    -- Only the state row reaches an update; its fields resolve only here.
    IF TG_OP='UPDATE' THEN
        IF NEW.organization_id<>OLD.organization_id OR NEW.generation<>OLD.generation+1 THEN
            RAISE EXCEPTION 'Retention generations advance by one.' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER retention_state_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.organization_retention_state FOR EACH ROW EXECUTE FUNCTION braid.retention_guard();
CREATE TRIGGER retention_state_no_truncate BEFORE TRUNCATE ON braid.organization_retention_state FOR EACH STATEMENT EXECUTE FUNCTION braid.retention_guard();
CREATE TRIGGER retention_policy_guard BEFORE INSERT OR UPDATE OR DELETE ON braid.organization_retention_policies FOR EACH ROW EXECUTE FUNCTION braid.retention_guard();
CREATE TRIGGER retention_policy_no_truncate BEFORE TRUNCATE ON braid.organization_retention_policies FOR EACH STATEMENT EXECUTE FUNCTION braid.retention_guard();

-- Organization administrators only; project roles and chat ownership grant nothing here.
CREATE FUNCTION braid.read_retention_policies(p_org uuid,p_actor uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR p_actor IS NULL OR NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('organizationId',p_org,
        'version',coalesce((SELECT generation FROM ONLY braid.organization_retention_state WHERE organization_id=p_org),1)::text,
        'policies',(SELECT jsonb_agg(jsonb_build_object('family',f.family,'mode',coalesce(p.mode,'notConfigured'),
            'durationSeconds',p.duration_seconds::text,'policyVersion',p.version::text,
            'appliesToExisting',coalesce(p.applies_to_existing,false),'effectiveAtUtc',p.effective_at) ORDER BY f.ordinal)
            FROM unnest(ARRAY['preview','regressionRun','conversation','invocationHistory']) WITH ORDINALITY f(family,ordinal)
            LEFT JOIN LATERAL(SELECT * FROM ONLY braid.organization_retention_policies x
                WHERE x.organization_id=p_org AND x.family=f.family ORDER BY x.version DESC LIMIT 1) p ON true));
END;
$body$;

-- Replaces the complete four-family policy in one short transaction: exclusive
-- organization gate, authority after the wait, strong generation precondition,
-- one appended version and one audit event per changed family. A configured
-- family cannot return to "not configured"; choose retain instead.
CREATE FUNCTION braid.replace_retention_policies(p_org uuid,p_actor uuid,p_generation bigint,
    p_families text[],p_modes text[],p_durations bigint[],p_existing boolean[]) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE current_generation bigint; i integer; at_time timestamptz; changed integer:=0;
    latest braid.organization_retention_policies%ROWTYPE;
BEGIN
    IF session_user<>'braid_administration' OR current_user<>'braid_migrator'
        OR p_org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN 'refused'; END IF;
    IF p_actor IS NULL OR p_generation IS NULL OR p_generation<1
        OR cardinality(p_families) IS DISTINCT FROM 4 OR cardinality(p_modes) IS DISTINCT FROM 4
        OR cardinality(p_durations) IS DISTINCT FROM 4 OR cardinality(p_existing) IS DISTINCT FROM 4
        OR p_families IS DISTINCT FROM ARRAY['preview','regressionRun','conversation','invocationHistory']::text[] THEN
        RETURN 'invalidRequest';
    END IF;
    FOR i IN 1..4 LOOP
        IF p_modes[i] IS NULL OR p_modes[i] NOT IN ('notConfigured','retain','expire') OR p_existing[i] IS NULL
            OR (p_modes[i]='expire')<>(p_durations[i] IS NOT NULL) OR p_durations[i] NOT BETWEEN 3600 AND 315360000
            OR (p_existing[i] AND p_modes[i]<>'expire') THEN RETURN 'invalidRequest'; END IF;
    END LOOP;
    PERFORM braid.lifecycle_gate(p_org,true);
    IF NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN 'refused'; END IF;
    INSERT INTO braid.organization_retention_state(organization_id) VALUES(p_org) ON CONFLICT DO NOTHING;
    SELECT generation INTO STRICT current_generation FROM ONLY braid.organization_retention_state WHERE organization_id=p_org FOR UPDATE;
    IF NOT braid.administration_authorized(p_org,p_actor,NULL) THEN RETURN 'refused'; END IF;
    IF current_generation<>p_generation THEN RETURN 'preconditionFailed'; END IF;
    IF current_generation=9223372036854775807 THEN RAISE EXCEPTION 'Retention generation overflow.' USING ERRCODE='22003'; END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.organization_retention_policies x WHERE x.organization_id=p_org
        AND x.family=ANY(SELECT p_families[k] FROM generate_series(1,4) k WHERE p_modes[k]='notConfigured')) THEN RETURN 'conflict'; END IF;
    at_time:=clock_timestamp();
    FOR i IN 1..4 LOOP
        SELECT * INTO latest FROM ONLY braid.organization_retention_policies
            WHERE organization_id=p_org AND family=p_families[i] ORDER BY version DESC LIMIT 1;
        IF p_modes[i]='notConfigured' THEN
            IF FOUND THEN RETURN 'conflict'; END IF;
            CONTINUE;
        END IF;
        IF FOUND AND latest.mode=p_modes[i] AND latest.duration_seconds IS NOT DISTINCT FROM p_durations[i]
            AND (latest.applies_to_existing OR NOT p_existing[i]) THEN CONTINUE; END IF;
        INSERT INTO braid.organization_retention_policies(organization_id,family,version,mode,duration_seconds,applies_to_existing,effective_at,changed_by)
            VALUES(p_org,p_families[i],coalesce(latest.version,0)+1,p_modes[i],p_durations[i],p_existing[i],at_time,p_actor);
        PERFORM braid.audit_log_append('organization_retention_policies',p_org::text||':'||p_families[i]||':'||(coalesce(latest.version,0)+1)::text,
            p_org,NULL,NULL,p_actor,'user','administration','retentionChanged',p_org,
            p_families[i]||'.'||p_modes[i]||CASE WHEN p_existing[i] THEN '.existing' ELSE '' END,at_time,false);
        changed:=changed+1;
    END LOOP;
    IF changed=0 THEN RETURN 'unchanged'; END IF;
    UPDATE braid.organization_retention_state SET generation=generation+1 WHERE organization_id=p_org;
    RETURN 'updated';
END;
$body$;
REVOKE ALL ON FUNCTION braid.retention_guard(),braid.read_retention_policies(uuid,uuid),
    braid.replace_retention_policies(uuid,uuid,bigint,text[],text[],bigint[],boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_retention_policies(uuid,uuid),
    braid.replace_retention_policies(uuid,uuid,bigint,text[],text[],bigint[],boolean[]) TO braid_administration;
