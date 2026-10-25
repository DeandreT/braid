-- Administrative authority is separate from all project roles. This login receives
-- only bounded entry points, never direct table access or the migration credential.
DO $roles$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='weft_administration';
    IF current_user<>'weft_migrator' OR session_user<>current_user OR NOT FOUND
        OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole
        OR r.rolreplication OR r.rolbypassrls OR r.rolconfig IS NOT NULL
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY') THEN
        RAISE EXCEPTION 'Independent administration login required.' USING ERRCODE='42501';
    END IF;
END;
$roles$;

CREATE TABLE weft.organization_administration_roles (
    organization_id uuid NOT NULL, principal_id uuid NOT NULL,
    role text NOT NULL CHECK(role IN ('member','administrator')),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0),
    PRIMARY KEY(organization_id,principal_id),
    FOREIGN KEY(organization_id,principal_id) REFERENCES weft.organization_memberships(organization_id,principal_id)
);
CREATE TABLE weft.administration_state (
    organization_id uuid PRIMARY KEY REFERENCES weft.organizations(id),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0),
    tenant_id uuid CHECK(tenant_id<>'00000000-0000-0000-0000-000000000000')
);
INSERT INTO weft.administration_state(organization_id) SELECT id FROM weft.organizations;
CREATE TABLE weft.organization_invitations (
    organization_id uuid NOT NULL REFERENCES weft.organizations(id),
    invitation_id uuid NOT NULL CHECK(invitation_id<>'00000000-0000-0000-0000-000000000000'),
    secret_hash text NOT NULL UNIQUE CHECK(secret_hash~'^[0-9a-f]{64}$'),
    tenant_id uuid NOT NULL, expected_object_id uuid,
    project_roles jsonb NOT NULL DEFAULT '[]' CHECK(jsonb_typeof(project_roles)='array' AND jsonb_array_length(project_roles)<=32),
    invited_by uuid NOT NULL REFERENCES weft.principals(id),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(), expires_at timestamptz NOT NULL,
    revoked_at timestamptz, redeemed_at timestamptz, redeemed_principal_id uuid REFERENCES weft.principals(id),
    redeemed_object_id uuid,
    PRIMARY KEY(organization_id,invitation_id),
    CHECK(isfinite(created_at) AND isfinite(expires_at) AND expires_at>created_at
        AND expires_at<=created_at+interval '7 days'),
    CHECK((redeemed_at IS NULL)=(redeemed_principal_id IS NULL) AND (redeemed_at IS NULL)=(redeemed_object_id IS NULL))
);
CREATE TABLE weft.administration_audit (
    organization_id uuid NOT NULL REFERENCES weft.organizations(id),
    sequence bigint GENERATED ALWAYS AS IDENTITY,
    actor_id uuid NOT NULL REFERENCES weft.principals(id),
    operation text NOT NULL, target_id uuid NOT NULL, project_id uuid,
    before_state jsonb, after_state jsonb,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(organization_id,sequence)
);
ALTER TABLE weft.administration_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.organization_administration_roles ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.organization_invitations ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.administration_audit ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON weft.administration_state USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.organization_administration_roles USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.organization_invitations USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.administration_audit USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.administration_state,weft.organization_administration_roles,weft.organization_invitations,weft.administration_audit FROM PUBLIC;

CREATE FUNCTION weft.administration_audit_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    IF TG_OP<>'INSERT' OR current_user<>'weft_migrator' THEN
        RAISE EXCEPTION 'Administrative history is append-only.' USING ERRCODE='42501';
    END IF;
    RETURN NEW;
END;
$body$;
CREATE TRIGGER administration_audit_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.administration_audit
    FOR EACH ROW EXECUTE FUNCTION weft.administration_audit_guard();
CREATE TRIGGER administration_audit_no_truncate BEFORE TRUNCATE ON weft.administration_audit
    FOR EACH STATEMENT EXECUTE FUNCTION weft.administration_audit_guard();

CREATE FUNCTION weft.administration_authorized(org uuid, actor uuid, project uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY weft.principals p JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id
        WHERE p.id=actor AND p.active AND o.organization_id=org AND o.active
        AND ((project IS NULL AND EXISTS(SELECT 1 FROM ONLY weft.organization_administration_roles r
            WHERE r.organization_id=org AND r.principal_id=actor AND r.role='administrator')) OR (project IS NOT NULL AND EXISTS(
            SELECT 1 FROM ONLY weft.project_memberships m WHERE m.organization_id=org AND m.project_id=project
                AND m.principal_id=actor AND m.active AND m.role='administrator'))));
$body$;

-- Bootstrap principals may await their first stable binding. Once such bindings
-- exist, a principal with no enabled identity must not count as a usable successor.
CREATE FUNCTION weft.administration_identity_available(org uuid, principal uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
    SELECT NOT EXISTS(SELECT 1 FROM ONLY weft.external_identity_bindings b
        JOIN ONLY weft.administration_state s ON s.organization_id=org AND b.authority=s.tenant_id::text
        WHERE b.principal_id=principal AND b.provider='entra.oid')
    OR EXISTS(SELECT 1 FROM ONLY weft.external_identity_bindings b
        JOIN ONLY weft.administration_state s ON s.organization_id=org AND b.authority=s.tenant_id::text
        WHERE b.principal_id=principal AND b.provider='entra.oid' AND b.enabled);
$body$;

-- Called with the organization row locked. Removing admission or a global principal
-- must preserve both the last organization administrator and every affected project.
CREATE FUNCTION weft.administration_can_deactivate(org uuid, target uuid, project uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
    SELECT (project IS NOT NULL OR NOT EXISTS(SELECT 1 FROM ONLY weft.organization_memberships o
        JOIN ONLY weft.organization_administration_roles r USING(organization_id,principal_id)
        WHERE o.organization_id=org AND o.principal_id=target AND o.active AND r.role='administrator')
        OR EXISTS(SELECT 1 FROM ONLY weft.organization_memberships o JOIN ONLY weft.principals p ON p.id=o.principal_id
            JOIN ONLY weft.organization_administration_roles r ON r.organization_id=o.organization_id AND r.principal_id=o.principal_id
            WHERE o.organization_id=org AND o.principal_id<>target AND o.active AND r.role='administrator' AND p.active
                AND weft.administration_identity_available(org,p.id)))
    AND NOT EXISTS(SELECT 1 FROM ONLY weft.project_memberships m
        WHERE m.organization_id=org AND m.principal_id=target AND m.active AND m.role='administrator'
            AND (project IS NULL OR m.project_id=project)
            AND NOT EXISTS(SELECT 1 FROM ONLY weft.project_memberships other
                JOIN ONLY weft.organization_memberships o ON o.organization_id=other.organization_id AND o.principal_id=other.principal_id AND o.active
                JOIN ONLY weft.principals p ON p.id=other.principal_id AND p.active
                WHERE other.organization_id=org AND other.project_id=m.project_id AND other.principal_id<>target
                    AND other.active AND other.role='administrator' AND weft.administration_identity_available(org,p.id)));
$body$;

CREATE FUNCTION weft.read_administration(org uuid, actor uuid, project uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE result jsonb; v bigint;
BEGIN
    PERFORM 1 FROM ONLY weft.organizations WHERE id=org FOR SHARE;
    PERFORM 1 FROM ONLY weft.principals WHERE id=actor FOR SHARE;
    PERFORM 1 FROM ONLY weft.organization_memberships WHERE organization_id=org AND principal_id=actor FOR SHARE;
    IF project IS NOT NULL THEN
        PERFORM 1 FROM ONLY weft.project_memberships WHERE organization_id=org AND project_id=project AND principal_id=actor FOR SHARE;
    END IF;
    IF session_user<>'weft_administration' OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR NOT weft.administration_authorized(org,actor,project) THEN RETURN NULL; END IF;
    SELECT version INTO v FROM ONLY weft.administration_state WHERE organization_id=org;
    IF v IS NULL THEN RETURN NULL; END IF;
    IF project IS NULL THEN
        SELECT jsonb_build_object('version',v,'members',COALESCE((SELECT jsonb_agg(x) FROM (
            SELECT o.principal_id AS "principalId",o.active,COALESCE(r.role,'member') AS role,COALESCE(r.version,1) AS version,p.active AS "principalActive",
                (SELECT COALESCE(jsonb_agg(jsonb_build_object('bindingId',b.binding_id,'provider',b.provider,'authority',b.authority,
                    'subject',b.subject,'enabled',b.enabled,'version',b.version)),'[]') FROM ONLY weft.external_identity_bindings b WHERE b.principal_id=p.id
                    AND b.provider='entra.oid' AND b.authority=(SELECT tenant_id::text FROM ONLY weft.administration_state WHERE organization_id=org)) AS bindings
            FROM ONLY weft.organization_memberships o JOIN ONLY weft.principals p ON p.id=o.principal_id
            LEFT JOIN ONLY weft.organization_administration_roles r USING(organization_id,principal_id)
            WHERE o.organization_id=org ORDER BY o.principal_id LIMIT 1000) x),'[]'),
            'invitations',COALESCE((SELECT jsonb_agg(x) FROM (SELECT invitation_id AS "invitationId",tenant_id AS "tenantId",
                expected_object_id AS "expectedObjectId",project_roles AS "projectRoles",expires_at AS "expiresAt",
                revoked_at AS "revokedAt",redeemed_at AS "redeemedAt" FROM ONLY weft.organization_invitations
                WHERE organization_id=org ORDER BY created_at DESC,invitation_id LIMIT 1000) x),'[]'),
            'audit',COALESCE((SELECT jsonb_agg(x) FROM (SELECT sequence,actor_id AS "actorId",operation,target_id AS "targetId",project_id AS "projectId",
                before_state AS "before",after_state AS "after",recorded_at AS "recordedAt" FROM ONLY weft.administration_audit
                WHERE organization_id=org ORDER BY sequence DESC LIMIT 100) x),'[]')) INTO result;
    ELSE
        SELECT jsonb_build_object('version',v,'members',COALESCE((SELECT jsonb_agg(x) FROM (
            SELECT m.principal_id AS "principalId",m.active,m.role,m.version,COALESCE(g.active,false) AS "payloadRead",
                COALESCE(g.version,0) AS "payloadVersion" FROM ONLY weft.project_memberships m
            LEFT JOIN ONLY weft.project_payload_read_grants g USING(organization_id,project_id,principal_id)
            WHERE m.organization_id=org AND m.project_id=project ORDER BY m.principal_id LIMIT 1000) x),'[]'),
            'grants',COALESCE((SELECT jsonb_agg(x) FROM (SELECT g.grant_id AS "grantId",g.target_id AS "targetId",g.mapping_id AS "mappingId",
                g.tenant_id AS "tenantId",g.object_id AS "objectId",g.granted_at AS "grantedAt",g.revoked_at AS "revokedAt"
                FROM ONLY weft.invocation_grants g WHERE g.organization_id=org AND g.project_id=project ORDER BY g.grant_id LIMIT 1000) x),'[]')) INTO result;
    END IF;
    RETURN result;
END;
$body$;

CREATE FUNCTION weft.write_administration(org uuid, actor uuid, expected bigint, op text, project uuid, target uuid, data jsonb) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE v bigint; before_data jsonb; after_data jsonb; entry jsonb; binding weft.external_identity_bindings%ROWTYPE;
    grant_row weft.invocation_grants%ROWTYPE; release_target weft.local_release_targets%ROWTYPE;
    member weft.organization_memberships%ROWTYPE; active_value boolean; role_value text;
BEGIN
    IF session_user<>'weft_administration' OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR target IS NULL OR target='00000000-0000-0000-0000-000000000000' THEN RETURN 'refused'; END IF;
    PERFORM 1 FROM ONLY weft.organizations WHERE id=org FOR UPDATE;
    INSERT INTO weft.administration_state(organization_id) VALUES(org) ON CONFLICT DO NOTHING;
    SELECT version INTO v FROM ONLY weft.administration_state WHERE organization_id=org FOR UPDATE;
    -- Lock authority through commit; all administration writers share the organization gate.
    PERFORM 1 FROM ONLY weft.principals WHERE id=actor OR id=target ORDER BY id FOR UPDATE;
    PERFORM 1 FROM ONLY weft.organization_memberships WHERE organization_id=org AND (principal_id=actor OR principal_id=target) ORDER BY principal_id FOR UPDATE;
    PERFORM 1 FROM ONLY weft.organization_administration_roles WHERE organization_id=org AND principal_id=actor FOR SHARE;
    IF project IS NOT NULL THEN
        PERFORM 1 FROM ONLY weft.projects WHERE organization_id=org AND id=project FOR KEY SHARE;
        PERFORM 1 FROM ONLY weft.project_memberships WHERE organization_id=org AND project_id=project AND principal_id=actor FOR SHARE;
    END IF;
    IF NOT weft.administration_authorized(org,actor,project) THEN RETURN 'refused'; END IF;
    IF v IS DISTINCT FROM expected THEN RETURN 'preconditionFailed'; END IF;
    IF v=9223372036854775807 THEN RETURN 'conflict'; END IF;
    IF op='organizationMember' AND project IS NULL THEN
        active_value:=(data->>'active')::boolean; role_value:=data->>'role';
        IF active_value IS NULL OR role_value NOT IN ('member','administrator') THEN RETURN 'invalidRequest'; END IF;
        SELECT * INTO member FROM ONLY weft.organization_memberships WHERE organization_id=org AND principal_id=target;
        SELECT to_jsonb(o)||jsonb_build_object('role',COALESCE(r.role,'member')) INTO before_data FROM ONLY weft.organization_memberships o
            LEFT JOIN ONLY weft.organization_administration_roles r USING(organization_id,principal_id) WHERE o.organization_id=org AND o.principal_id=target;
        IF member.principal_id IS NOT NULL AND (NOT active_value OR (before_data->>'role'='administrator' AND role_value<>'administrator')) THEN
            IF NOT active_value AND NOT weft.administration_can_deactivate(org,target,NULL) THEN RETURN 'lastAdministrator'; END IF;
            IF before_data->>'role'='administrator' AND NOT EXISTS(SELECT 1 FROM ONLY weft.organization_memberships o JOIN ONLY weft.principals p ON p.id=o.principal_id AND p.active
                JOIN ONLY weft.organization_administration_roles r USING(organization_id,principal_id)
                WHERE o.organization_id=org AND o.principal_id<>target AND o.active AND r.role='administrator'
                    AND weft.administration_identity_available(org,p.id)) THEN RETURN 'lastAdministrator'; END IF;
        END IF;
        -- Admit only an existing principal; invitation redemption creates a new one.
        IF NOT EXISTS(SELECT 1 FROM ONLY weft.principals WHERE id=target AND active) THEN RETURN 'refused'; END IF;
        INSERT INTO weft.organization_memberships(organization_id,principal_id,active) VALUES(org,target,active_value)
            ON CONFLICT(organization_id,principal_id) DO UPDATE SET active=EXCLUDED.active;
        INSERT INTO weft.organization_administration_roles(organization_id,principal_id,role) VALUES(org,target,role_value)
            ON CONFLICT(organization_id,principal_id) DO UPDATE SET role=EXCLUDED.role,version=organization_administration_roles.version+1;
        SELECT to_jsonb(o)||jsonb_build_object('role',r.role,'version',r.version) INTO after_data FROM ONLY weft.organization_memberships o
            JOIN ONLY weft.organization_administration_roles r USING(organization_id,principal_id) WHERE o.organization_id=org AND o.principal_id=target;
    ELSIF op='projectMember' AND project IS NOT NULL THEN
        active_value:=(data->>'active')::boolean; role_value:=data->>'role';
        IF active_value IS NULL OR role_value NOT IN ('viewer','runner','publisher','administrator') THEN RETURN 'invalidRequest'; END IF;
        IF active_value AND NOT EXISTS(SELECT 1 FROM ONLY weft.organization_memberships o JOIN ONLY weft.principals p ON p.id=o.principal_id AND p.active
            WHERE o.organization_id=org AND o.principal_id=target AND o.active) THEN RETURN 'refused'; END IF;
        SELECT to_jsonb(m) INTO before_data FROM ONLY weft.project_memberships m WHERE organization_id=org AND project_id=project AND principal_id=target FOR UPDATE;
        IF (NOT active_value OR role_value<>'administrator') AND NOT weft.administration_can_deactivate(org,target,project) THEN RETURN 'lastAdministrator'; END IF;
        INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active) VALUES(org,project,target,role_value,active_value)
            ON CONFLICT(organization_id,project_id,principal_id) DO UPDATE SET active=EXCLUDED.active,role=EXCLUDED.role,version=project_memberships.version+1;
        SELECT to_jsonb(m) INTO after_data FROM ONLY weft.project_memberships m WHERE organization_id=org AND project_id=project AND principal_id=target;
    ELSIF op='payloadRead' AND project IS NOT NULL THEN
        active_value:=(data->>'active')::boolean;
        IF active_value IS NULL THEN RETURN 'invalidRequest'; END IF;
        IF active_value AND NOT EXISTS(SELECT 1 FROM ONLY weft.project_memberships m
            JOIN ONLY weft.organization_memberships o ON o.organization_id=m.organization_id AND o.principal_id=m.principal_id AND o.active
            JOIN ONLY weft.principals p ON p.id=m.principal_id AND p.active
            WHERE m.organization_id=org AND m.project_id=project AND m.principal_id=target AND m.active) THEN RETURN 'refused'; END IF;
        SELECT to_jsonb(g) INTO before_data FROM ONLY weft.project_payload_read_grants g WHERE organization_id=org AND project_id=project AND principal_id=target FOR UPDATE;
        IF before_data IS NULL AND NOT active_value THEN RETURN 'unchanged'; END IF;
        IF before_data->>'active'=active_value::text THEN RETURN 'unchanged'; END IF;
        IF before_data IS NULL THEN
            INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active) VALUES(org,project,target,true);
        ELSE
            UPDATE ONLY weft.project_payload_read_grants SET active=active_value,version=version+1
                WHERE organization_id=org AND project_id=project AND principal_id=target;
        END IF;
        SELECT to_jsonb(g) INTO after_data FROM ONLY weft.project_payload_read_grants g WHERE organization_id=org AND project_id=project AND principal_id=target;
    ELSIF op IN ('principalDisable','bindingDisable') AND project IS NULL THEN
        IF op='bindingDisable' THEN
            SELECT * INTO binding FROM ONLY weft.external_identity_bindings WHERE binding_id=target FOR UPDATE;
            IF binding.provider IS DISTINCT FROM 'entra.oid' OR binding.authority IS DISTINCT FROM
                (SELECT tenant_id::text FROM ONLY weft.administration_state WHERE organization_id=org) THEN RETURN 'refused'; END IF;
            target:=binding.principal_id;
            PERFORM 1 FROM ONLY weft.principals WHERE id=target FOR UPDATE;
        END IF;
        IF target IS NULL OR NOT EXISTS(SELECT 1 FROM ONLY weft.organization_memberships WHERE organization_id=org AND principal_id=target)
            OR EXISTS(SELECT 1 FROM ONLY weft.organization_memberships WHERE organization_id<>org AND principal_id=target) THEN RETURN 'refused'; END IF;
        IF (op='principalDisable' OR NOT EXISTS(SELECT 1 FROM ONLY weft.external_identity_bindings
            WHERE principal_id=target AND enabled AND provider='entra.oid' AND authority=binding.authority AND binding_id<>binding.binding_id))
            AND NOT weft.administration_can_deactivate(org,target,NULL) THEN RETURN 'lastAdministrator'; END IF;
        IF op='principalDisable' THEN
            SELECT to_jsonb(p) INTO before_data FROM ONLY weft.principals p WHERE id=target;
            IF before_data->>'active'='false' THEN RETURN 'unchanged'; END IF;
            UPDATE ONLY weft.principals SET active=false WHERE id=target;
            SELECT to_jsonb(p) INTO after_data FROM ONLY weft.principals p WHERE id=target;
        ELSE
            before_data:=to_jsonb(binding);
            IF NOT binding.enabled THEN RETURN 'unchanged'; END IF;
            UPDATE ONLY weft.external_identity_bindings SET enabled=false,version=version+1,disabled_at=clock_timestamp() WHERE binding_id=binding.binding_id;
            SELECT to_jsonb(b) INTO after_data FROM ONLY weft.external_identity_bindings b WHERE binding_id=binding.binding_id;
            target:=binding.binding_id;
        END IF;
    ELSIF op='invite' AND project IS NULL THEN
        IF EXISTS(SELECT 1 FROM ONLY weft.organization_invitations WHERE organization_id=org AND invitation_id=target) THEN RETURN 'conflict'; END IF;
        IF NOT EXISTS(SELECT 1 FROM ONLY weft.administration_state WHERE organization_id=org AND tenant_id=(data->>'tenantId')::uuid) THEN RETURN 'refused'; END IF;
        IF jsonb_typeof(data->'projectRoles') IS DISTINCT FROM 'array' OR jsonb_array_length(data->'projectRoles')>32 THEN RETURN 'invalidRequest'; END IF;
        FOR entry IN SELECT * FROM jsonb_array_elements(data->'projectRoles') LOOP
            IF NOT weft.administration_authorized(org,actor,(entry->>'projectId')::uuid) OR entry->>'role' NOT IN ('viewer','runner','publisher','administrator') THEN RETURN 'refused'; END IF;
        END LOOP;
        INSERT INTO weft.organization_invitations(organization_id,invitation_id,secret_hash,tenant_id,expected_object_id,project_roles,invited_by,expires_at)
            VALUES(org,target,data->>'secretHash',(data->>'tenantId')::uuid,(data->>'expectedObjectId')::uuid,data->'projectRoles',actor,(data->>'expiresAt')::timestamptz);
        SELECT to_jsonb(i)-'secret_hash' INTO after_data FROM ONLY weft.organization_invitations i WHERE organization_id=org AND invitation_id=target;
    ELSIF op='inviteRevoke' AND project IS NULL THEN
        SELECT to_jsonb(i)-'secret_hash' INTO before_data FROM ONLY weft.organization_invitations i WHERE organization_id=org AND invitation_id=target FOR UPDATE;
        IF before_data IS NULL THEN RETURN 'refused'; END IF;
        IF before_data->>'redeemed_at' IS NOT NULL THEN RETURN 'conflict'; END IF;
        IF before_data->>'revoked_at' IS NOT NULL THEN RETURN 'unchanged'; END IF;
        UPDATE ONLY weft.organization_invitations SET revoked_at=clock_timestamp() WHERE organization_id=org AND invitation_id=target;
        SELECT to_jsonb(i)-'secret_hash' INTO after_data FROM ONLY weft.organization_invitations i WHERE organization_id=org AND invitation_id=target;
    ELSIF op IN ('invocationGrant','invocationRevoke') AND project IS NOT NULL THEN
        PERFORM set_config('weft.build_principal_id',actor::text,true);
        SELECT * INTO grant_row FROM ONLY weft.invocation_grants WHERE organization_id=org AND grant_id=target;
        SELECT * INTO release_target FROM ONLY weft.local_release_targets WHERE organization_id=org
            AND target_id=(CASE WHEN op='invocationGrant' THEN (data->>'targetId')::uuid ELSE grant_row.target_id END) FOR UPDATE;
        IF release_target.target_id IS NULL OR release_target.project_id<>project THEN RETURN 'refused'; END IF;
        SELECT * INTO grant_row FROM ONLY weft.invocation_grants WHERE organization_id=org AND grant_id=target FOR UPDATE;
        IF op='invocationGrant' THEN
            IF grant_row.grant_id IS NOT NULL THEN RETURN 'conflict'; END IF;
            IF NOT release_target.enabled THEN RETURN 'refused'; END IF;
            IF EXISTS(SELECT 1 FROM ONLY weft.invocation_grants g WHERE g.organization_id=org AND g.target_id=release_target.target_id
                AND g.provider='entra' AND g.tenant_id=(data->>'tenantId')::uuid AND g.object_id=(data->>'objectId')::uuid AND g.revoked_at IS NULL) THEN RETURN 'conflict'; END IF;
            IF NOT EXISTS(SELECT 1 FROM ONLY weft.administration_state WHERE organization_id=org AND tenant_id=(data->>'tenantId')::uuid)
                OR NOT EXISTS(SELECT 1 FROM ONLY weft.local_release_targets WHERE organization_id=org AND project_id=project AND target_id=(data->>'targetId')::uuid)
                OR (data->>'objectId')::uuid='00000000-0000-0000-0000-000000000000' THEN RETURN 'refused'; END IF;
            INSERT INTO weft.invocation_grants(organization_id,project_id,mapping_id,target_id,grant_id,provider,tenant_id,object_id,granted_by)
                SELECT org,project,t.mapping_id,t.target_id,target,'entra',(data->>'tenantId')::uuid,(data->>'objectId')::uuid,actor
                FROM ONLY weft.local_release_targets t WHERE t.organization_id=org AND t.project_id=project AND t.target_id=(data->>'targetId')::uuid;
            INSERT INTO weft.invocation_grant_audit(organization_id,grant_id,sequence,kind,actor_id) VALUES(org,target,1,'granted',actor);
        ELSE
            IF grant_row.grant_id IS NULL OR grant_row.project_id<>project THEN RETURN 'refused'; END IF;
            IF grant_row.revoked_at IS NOT NULL THEN RETURN 'unchanged'; END IF;
            before_data:=to_jsonb(grant_row);
            UPDATE ONLY weft.invocation_grants SET revoked_by=actor,revoked_at=statement_timestamp() WHERE organization_id=org AND grant_id=target;
            INSERT INTO weft.invocation_grant_audit(organization_id,grant_id,sequence,kind,actor_id) VALUES(org,target,2,'revoked',actor);
        END IF;
        SELECT to_jsonb(g) INTO after_data FROM ONLY weft.invocation_grants g WHERE organization_id=org AND grant_id=target;
    ELSE RETURN 'invalidRequest';
    END IF;
    INSERT INTO weft.administration_audit(organization_id,actor_id,operation,target_id,project_id,before_state,after_state)
        VALUES(org,actor,op,target,project,before_data,after_data);
    UPDATE ONLY weft.administration_state SET version=version+1 WHERE organization_id=org;
    RETURN 'updated';
END;
$body$;

-- Keep the existing invocation ledger guard unchanged outside administrative
-- grant records. The application login still cannot mutate any table directly.
CREATE OR REPLACE FUNCTION weft.guard_machine_invocation() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF current_user<>'weft_migrator' OR TG_OP='TRUNCATE'
        OR (session_user<>(CASE WHEN TG_TABLE_NAME LIKE 'invocation_grant%' THEN 'weft_invocation_grant_writer' ELSE 'weft_invocation_writer' END)
            AND NOT(session_user='weft_administration' AND TG_TABLE_NAME IN ('invocation_grants','invocation_grant_audit'))) THEN
        RAISE EXCEPTION 'Machine invocation mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_TABLE_NAME IN ('invocation_grant_audit','machine_invocation_audit') THEN
        IF TG_OP<>'INSERT' OR NEW.recorded_at<>statement_timestamp() THEN RAISE EXCEPTION 'Machine invocation audit is immutable.' USING ERRCODE='42501'; END IF;
    ELSIF TG_TABLE_NAME='invocation_grants' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.revoked_by IS NOT NULL OR NEW.revoked_at IS NOT NULL OR NEW.granted_at<>statement_timestamp()
                OR weft.build_authorized(NEW.organization_id,NEW.project_id,NEW.granted_by,ARRAY['administrator']) IS NOT TRUE THEN
                RAISE EXCEPTION 'Invocation grant refused.' USING ERRCODE='42501';
            END IF;
        ELSIF TG_OP<>'UPDATE' OR ROW(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.target_id,NEW.grant_id,NEW.provider,NEW.tenant_id,NEW.object_id,NEW.granted_by,NEW.granted_at)
                IS DISTINCT FROM ROW(OLD.organization_id,OLD.project_id,OLD.mapping_id,OLD.target_id,OLD.grant_id,OLD.provider,OLD.tenant_id,OLD.object_id,OLD.granted_by,OLD.granted_at)
            OR OLD.revoked_at IS NOT NULL OR NEW.revoked_at IS DISTINCT FROM statement_timestamp()
            OR weft.build_authorized(NEW.organization_id,NEW.project_id,NEW.revoked_by,ARRAY['administrator']) IS NOT TRUE THEN
            RAISE EXCEPTION 'Invocation grant history is immutable.' USING ERRCODE='42501';
        END IF;
    ELSIF TG_TABLE_NAME='machine_invocation_results' THEN
        IF TG_OP='INSERT' THEN
            IF NEW.retained_at<>statement_timestamp() OR NOT EXISTS(SELECT 1 FROM ONLY weft.machine_invocations i
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

CREATE FUNCTION weft.redeem_organization_invitation(org uuid, secret text, tenant uuid, object_id uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE invitation weft.organization_invitations%ROWTYPE; b weft.external_identity_bindings%ROWTYPE; principal uuid; entry jsonb;
BEGIN
    IF session_user<>'weft_administration' OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR tenant IS NULL OR object_id IS NULL OR object_id='00000000-0000-0000-0000-000000000000' THEN RETURN false; END IF;
    PERFORM 1 FROM ONLY weft.organizations WHERE id=org FOR UPDATE;
    SELECT * INTO invitation FROM ONLY weft.organization_invitations WHERE organization_id=org AND secret_hash=secret FOR UPDATE;
    IF NOT FOUND OR invitation.tenant_id<>tenant OR invitation.revoked_at IS NOT NULL
        OR (invitation.expected_object_id IS NOT NULL AND invitation.expected_object_id<>object_id) THEN RETURN false; END IF;
    IF invitation.redeemed_at IS NOT NULL THEN RETURN invitation.redeemed_object_id=object_id; END IF;
    IF invitation.expires_at<=clock_timestamp() OR NOT weft.administration_authorized(org,invitation.invited_by,NULL) THEN RETURN false; END IF;
    FOR entry IN SELECT * FROM jsonb_array_elements(invitation.project_roles) LOOP
        IF NOT weft.administration_authorized(org,invitation.invited_by,(entry->>'projectId')::uuid) THEN RETURN false; END IF;
    END LOOP;
    -- Serializes admission of the same stable identity across organizations.
    PERFORM pg_advisory_xact_lock(hashtextextended(tenant::text||':'||object_id::text,198329));
    SELECT * INTO b FROM ONLY weft.external_identity_bindings WHERE provider='entra.oid' AND authority=tenant::text AND subject=object_id::text FOR UPDATE;
    IF FOUND THEN
        -- Serialize the global active check and new admission with principal disable.
        PERFORM 1 FROM ONLY weft.principals WHERE id=b.principal_id FOR SHARE;
        IF NOT b.enabled OR NOT EXISTS(SELECT 1 FROM ONLY weft.principals WHERE id=b.principal_id AND active) THEN RETURN false; END IF;
        principal:=b.principal_id;
    ELSE
        principal:=gen_random_uuid();
        INSERT INTO weft.principals(id) VALUES(principal);
        INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES(gen_random_uuid(),'entra.oid',tenant::text,object_id::text,principal);
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY weft.organization_memberships WHERE organization_id=org AND principal_id=principal AND NOT active)
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(invitation.project_roles) e JOIN ONLY weft.project_memberships m
            ON m.organization_id=org AND m.project_id=(e->>'projectId')::uuid AND m.principal_id=principal
            WHERE NOT m.active OR m.role<>e->>'role') THEN
        -- No writes have escaped: the adapter rolls back a refused redemption.
        RETURN false;
    END IF;
    INSERT INTO weft.organization_memberships(organization_id,principal_id) VALUES(org,principal) ON CONFLICT DO NOTHING;
    FOR entry IN SELECT * FROM jsonb_array_elements(invitation.project_roles) LOOP
        INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role)
            VALUES(org,(entry->>'projectId')::uuid,principal,entry->>'role') ON CONFLICT DO NOTHING;
    END LOOP;
    UPDATE ONLY weft.organization_invitations SET redeemed_at=clock_timestamp(),redeemed_principal_id=principal,redeemed_object_id=object_id
        WHERE organization_id=org AND invitation_id=invitation.invitation_id;
    INSERT INTO weft.administration_audit(organization_id,actor_id,operation,target_id,before_state,after_state)
        VALUES(org,principal,'inviteRedeem',invitation.invitation_id,jsonb_build_object('redeemed',false),
            jsonb_build_object('redeemed',true,'principalId',principal,'tenantId',tenant,'objectId',object_id,'projectRoles',invitation.project_roles));
    UPDATE ONLY weft.administration_state SET version=version+1 WHERE organization_id=org;
    RETURN true;
END;
$body$;

REVOKE ALL ON FUNCTION weft.administration_audit_guard(),weft.administration_authorized(uuid,uuid,uuid),
    weft.administration_identity_available(uuid,uuid),
    weft.administration_can_deactivate(uuid,uuid,uuid),weft.read_administration(uuid,uuid,uuid),
    weft.write_administration(uuid,uuid,bigint,text,uuid,uuid,jsonb),weft.redeem_organization_invitation(uuid,text,uuid,uuid) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_administration;
GRANT EXECUTE ON FUNCTION weft.read_administration(uuid,uuid,uuid),weft.write_administration(uuid,uuid,bigint,text,uuid,uuid,jsonb),
    weft.redeem_organization_invitation(uuid,text,uuid,uuid) TO weft_administration;
