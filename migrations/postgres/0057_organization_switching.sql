-- Organization discovery is separate from all business authority. This login
-- receives only the bounded discovery entry point, never direct table access or
-- the migration credential.
DO $roles$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='braid_organization_discovery';
    IF current_user<>'braid_migrator' OR session_user<>current_user OR NOT FOUND
        OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole
        OR r.rolreplication OR r.rolbypassrls OR r.rolconfig IS NOT NULL
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY') THEN
        RAISE EXCEPTION 'Independent organization discovery login required.' USING ERRCODE='42501';
    END IF;
END;
$roles$;

-- Suspension and reactivation are indistinguishable by active checks alone. Each
-- retained membership keeps one positive incarnation that only the membership
-- trigger may create or advance.
CREATE TABLE braid.organization_admission_state (
    organization_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    version bigint NOT NULL DEFAULT 1 CHECK(version>0),
    PRIMARY KEY(organization_id,principal_id),
    FOREIGN KEY(organization_id,principal_id) REFERENCES braid.organization_memberships(organization_id,principal_id)
);
INSERT INTO braid.organization_admission_state(organization_id,principal_id)
    SELECT organization_id,principal_id FROM braid.organization_memberships;
ALTER TABLE braid.organization_admission_state ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.organization_admission_state
    USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.organization_admission_state FROM PUBLIC;

-- The backfill above precedes this guard; every later change arrives only through
-- the membership trigger, which opens and closes this transaction-local marker.
CREATE FUNCTION braid.organization_admission_state_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    IF TG_TABLE_NAME='organization_admission_state' AND TG_LEVEL='ROW'
        AND current_setting('braid.admission_state_write',true)='1' THEN
        IF TG_OP='INSERT' THEN
            RETURN NEW;
        END IF;
        IF TG_OP='UPDATE' AND NEW.organization_id=OLD.organization_id
            AND NEW.principal_id=OLD.principal_id AND NEW.version=OLD.version+1 THEN
            RETURN NEW;
        END IF;
    END IF;
    RAISE EXCEPTION 'Organization admission state is retained history.' USING ERRCODE='42501';
END;
$body$;
CREATE TRIGGER organization_admission_state_guard BEFORE INSERT OR UPDATE OR DELETE
    ON braid.organization_admission_state FOR EACH ROW EXECUTE FUNCTION braid.organization_admission_state_guard();
CREATE TRIGGER organization_admission_state_no_truncate BEFORE TRUNCATE ON braid.organization_admission_state
    FOR EACH STATEMENT EXECUTE FUNCTION braid.organization_admission_state_guard();

-- One incarnation per actual admission change: creation starts at one, each
-- activity transition advances exactly once, and no-op updates advance nothing.
CREATE FUNCTION braid.organization_admission_incarnation() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    PERFORM set_config('braid.admission_state_write','1',true);
    IF TG_OP='INSERT' THEN
        INSERT INTO braid.organization_admission_state(organization_id,principal_id)
            VALUES(NEW.organization_id,NEW.principal_id);
        PERFORM set_config('braid.admission_state_write','',true);
        RETURN NEW;
    END IF;
    IF NEW.active IS DISTINCT FROM OLD.active THEN
        UPDATE ONLY braid.organization_admission_state SET version=version+1
            WHERE organization_id=NEW.organization_id AND principal_id=NEW.principal_id;
        IF NOT FOUND THEN
            RAISE EXCEPTION 'Organization admission state is missing.' USING ERRCODE='23502';
        END IF;
    END IF;
    PERFORM set_config('braid.admission_state_write','',true);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER organization_admission_incarnation AFTER INSERT OR UPDATE ON braid.organization_memberships
    FOR EACH ROW EXECUTE FUNCTION braid.organization_admission_incarnation();
CREATE TRIGGER organization_admission_membership_retained BEFORE DELETE ON braid.organization_memberships
    FOR EACH ROW EXECUTE FUNCTION braid.organization_admission_state_guard();

-- The trusted application credential presents already verified identity. The
-- function confirms the current binding tuple and projects only what a selected
-- organization page needs, in canonical organization order.
CREATE FUNCTION braid.discover_organization_admissions(binding uuid, binding_version bigint, principal uuid, after uuid, fetch_limit integer)
RETURNS TABLE(organization_id uuid, organization_name text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    IF session_user<>'braid_organization_discovery' OR binding IS NULL OR binding_version IS NULL
        OR binding_version<1 OR principal IS NULL OR fetch_limit IS NULL OR fetch_limit<1 OR fetch_limit>101
        OR binding='00000000-0000-0000-0000-000000000000' OR principal='00000000-0000-0000-0000-000000000000'
        OR after='00000000-0000-0000-0000-000000000000' THEN
        RAISE EXCEPTION 'Organization discovery refused.' USING ERRCODE='42501';
    END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY braid.external_identity_bindings b JOIN ONLY braid.principals p ON p.id=b.principal_id
        WHERE b.binding_id=discover_organization_admissions.binding AND b.version=discover_organization_admissions.binding_version
            AND b.principal_id=discover_organization_admissions.principal AND b.enabled
            AND p.id=discover_organization_admissions.principal AND p.active) THEN
        RAISE EXCEPTION 'Organization discovery identity refused.' USING ERRCODE='42501';
    END IF;
    RETURN QUERY
        SELECT membership.organization_id, organization.name
        FROM ONLY braid.organization_memberships membership
        JOIN ONLY braid.organizations organization ON organization.id=membership.organization_id
        WHERE membership.principal_id=discover_organization_admissions.principal AND membership.active
            AND (discover_organization_admissions.after IS NULL OR membership.organization_id>discover_organization_admissions.after)
        ORDER BY membership.organization_id LIMIT fetch_limit;
END;
$body$;

-- Historical consumption is not fresh admission. A replayed redemption is
-- acknowledged only while the original binding, principal, admission and granted
-- project memberships still hold; it never revives or adds anything.
CREATE OR REPLACE FUNCTION braid.redeem_organization_invitation(org uuid, secret text, tenant uuid, object_id uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE invitation braid.organization_invitations%ROWTYPE; b braid.external_identity_bindings%ROWTYPE; principal uuid; entry jsonb;
BEGIN
    IF session_user<>'braid_administration' OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid
        OR tenant IS NULL OR object_id IS NULL OR object_id='00000000-0000-0000-0000-000000000000' THEN RETURN false; END IF;
    PERFORM 1 FROM ONLY braid.organizations WHERE id=org FOR UPDATE;
    SELECT * INTO invitation FROM ONLY braid.organization_invitations WHERE organization_id=org AND secret_hash=secret FOR UPDATE;
    IF NOT FOUND OR invitation.tenant_id<>tenant OR invitation.revoked_at IS NOT NULL
        OR (invitation.expected_object_id IS NOT NULL AND invitation.expected_object_id<>object_id) THEN RETURN false; END IF;
    IF invitation.redeemed_at IS NOT NULL THEN
        RETURN invitation.redeemed_object_id=object_id
            AND EXISTS(SELECT 1 FROM ONLY braid.external_identity_bindings current_binding
                JOIN ONLY braid.principals current_principal ON current_principal.id=current_binding.principal_id
                WHERE current_binding.principal_id=invitation.redeemed_principal_id AND current_binding.provider='entra.oid'
                    AND current_binding.authority=tenant::text AND current_binding.subject=object_id::text
                    AND current_binding.enabled AND current_principal.active)
            AND EXISTS(SELECT 1 FROM ONLY braid.organization_memberships m
                WHERE m.organization_id=org AND m.principal_id=invitation.redeemed_principal_id AND m.active)
            AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(invitation.project_roles) e
                WHERE NOT EXISTS(SELECT 1 FROM ONLY braid.project_memberships pm
                    WHERE pm.organization_id=org AND pm.project_id=(e->>'projectId')::uuid
                        AND pm.principal_id=invitation.redeemed_principal_id AND pm.active AND pm.role=e->>'role'));
    END IF;
    IF invitation.expires_at<=clock_timestamp() OR NOT braid.administration_authorized(org,invitation.invited_by,NULL) THEN RETURN false; END IF;
    FOR entry IN SELECT * FROM jsonb_array_elements(invitation.project_roles) LOOP
        IF NOT braid.administration_authorized(org,invitation.invited_by,(entry->>'projectId')::uuid) THEN RETURN false; END IF;
    END LOOP;
    -- Serializes admission of the same stable identity across organizations.
    PERFORM pg_advisory_xact_lock(hashtextextended(tenant::text||':'||object_id::text,198329));
    SELECT * INTO b FROM ONLY braid.external_identity_bindings WHERE provider='entra.oid' AND authority=tenant::text AND subject=object_id::text FOR UPDATE;
    IF FOUND THEN
        -- Serialize the global active check and new admission with principal disable.
        PERFORM 1 FROM ONLY braid.principals WHERE id=b.principal_id FOR SHARE;
        IF NOT b.enabled OR NOT EXISTS(SELECT 1 FROM ONLY braid.principals WHERE id=b.principal_id AND active) THEN RETURN false; END IF;
        principal:=b.principal_id;
    ELSE
        principal:=gen_random_uuid();
        INSERT INTO braid.principals(id) VALUES(principal);
        INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES(gen_random_uuid(),'entra.oid',tenant::text,object_id::text,principal);
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.organization_memberships WHERE organization_id=org AND principal_id=principal AND NOT active)
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(invitation.project_roles) e JOIN ONLY braid.project_memberships m
            ON m.organization_id=org AND m.project_id=(e->>'projectId')::uuid AND m.principal_id=principal
            WHERE NOT m.active OR m.role<>e->>'role') THEN
        -- No writes have escaped: the adapter rolls back a refused redemption.
        RETURN false;
    END IF;
    INSERT INTO braid.organization_memberships(organization_id,principal_id) VALUES(org,principal) ON CONFLICT DO NOTHING;
    FOR entry IN SELECT * FROM jsonb_array_elements(invitation.project_roles) LOOP
        INSERT INTO braid.project_memberships(organization_id,project_id,principal_id,role)
            VALUES(org,(entry->>'projectId')::uuid,principal,entry->>'role') ON CONFLICT DO NOTHING;
    END LOOP;
    UPDATE ONLY braid.organization_invitations SET redeemed_at=clock_timestamp(),redeemed_principal_id=principal,redeemed_object_id=object_id
        WHERE organization_id=org AND invitation_id=invitation.invitation_id;
    INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id,before_state,after_state)
        VALUES(org,principal,'inviteRedeem',invitation.invitation_id,jsonb_build_object('redeemed',false),
            jsonb_build_object('redeemed',true,'principalId',principal,'tenantId',tenant,'objectId',object_id,'projectRoles',invitation.project_roles));
    UPDATE ONLY braid.administration_state SET version=version+1 WHERE organization_id=org;
    RETURN true;
END;
$body$;

REVOKE ALL ON FUNCTION braid.organization_admission_state_guard(),braid.organization_admission_incarnation(),
    braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) FROM PUBLIC;
GRANT USAGE ON SCHEMA braid TO braid_organization_discovery;
GRANT EXECUTE ON FUNCTION braid.discover_organization_admissions(uuid,bigint,uuid,uuid,integer) TO braid_organization_discovery;
-- Runtime admission reads join the incarnation through the existing scoped read.
GRANT SELECT ON braid.organization_admission_state TO braid_catalog_reader;

-- Project creation admits its source relations exactly; memberships now carry
-- exactly the two admission-incarnation triggers above.
CREATE OR REPLACE FUNCTION braid.assert_project_creation_contract_v1() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog,braid,pg_temp
AS $braid_assert_project_creation_contract_v1$
DECLARE
    v_tables text[] := ARRAY['organizations','principals','organization_memberships','projects',
        'project_memberships','organization_project_creation_capabilities','project_creations'];
    v_table record;
    v_expected_expression text;
BEGIN
    IF current_user <> 'braid_migrator' OR current_setting('server_encoding') <> 'UTF8'
       OR current_setting('standard_conforming_strings') <> 'on'
       OR current_setting('session_replication_role') <> 'origin' OR current_setting('row_security') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its accepted execution profile.';
    END IF;

    -- Owner-held relation locks protect source DDL, not an ACL/role/function commit fence.
    LOCK TABLE ONLY braid.organizations, ONLY braid.principals, ONLY braid.organization_memberships,
        ONLY braid.projects, ONLY braid.project_memberships,
        ONLY braid.organization_project_creation_capabilities, ONLY braid.project_creations IN ACCESS SHARE MODE;
    IF (SELECT count(*) FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
        WHERE n.nspname='braid' AND r.relname=ANY(v_tables)) <> cardinality(v_tables)
       OR EXISTS (
        SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
        JOIN pg_catalog.pg_roles ro ON ro.oid=r.relowner JOIN pg_catalog.pg_roles no ON no.oid=n.nspowner
        WHERE n.nspname='braid' AND r.relname=ANY(v_tables)
          AND (r.relkind <> 'r' OR r.relpersistence <> 'p' OR r.relispartition
               OR ro.rolname <> 'braid_migrator' OR no.rolname <> 'braid_migrator'
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=r.oid OR i.inhparent=r.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite w WHERE w.ev_class=r.oid))
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation source relations are inconsistent.';
    END IF;

    -- Fixed expected columns are source-derived; dropped/reordered/extra columns refuse.
    IF EXISTS (
        WITH expected(table_name,ordinal,column_name,type_name,default_expression) AS (VALUES
            ('organizations',1,'id','uuid',NULL::text),
            ('organizations',2,'name','text',NULL::text),
            ('organizations',3,'created_at','timestamp with time zone','CURRENT_TIMESTAMP'),
            ('principals',1,'id','uuid',NULL::text),
            ('principals',2,'active','boolean','true'),
            ('principals',3,'created_at','timestamp with time zone','CURRENT_TIMESTAMP'),
            ('organization_memberships',1,'organization_id','uuid',NULL::text),
            ('organization_memberships',2,'principal_id','uuid',NULL::text),
            ('organization_memberships',3,'active','boolean','true'),
            ('projects',1,'organization_id','uuid',NULL::text),
            ('projects',2,'id','uuid',NULL::text),
            ('projects',3,'name','text',NULL::text),
            ('projects',4,'description','text','''''::text'),
            ('projects',5,'creator_principal_id','uuid',NULL::text),
            ('projects',6,'created_at','timestamp with time zone','CURRENT_TIMESTAMP'),
            ('projects',7,'updated_at','timestamp with time zone','CURRENT_TIMESTAMP'),
            ('projects',8,'version','bigint','1'),
            ('project_memberships',1,'organization_id','uuid',NULL::text),
            ('project_memberships',2,'project_id','uuid',NULL::text),
            ('project_memberships',3,'principal_id','uuid',NULL::text),
            ('project_memberships',4,'role','text',NULL::text),
            ('project_memberships',5,'active','boolean','true'),
            ('project_memberships',6,'version','bigint','1'),
            ('organization_project_creation_capabilities',1,'organization_id','uuid',NULL::text),
            ('organization_project_creation_capabilities',2,'principal_id','uuid',NULL::text),
            ('organization_project_creation_capabilities',3,'can_create_projects','boolean','false'),
            ('project_creations',1,'organization_id','uuid',NULL::text),
            ('project_creations',2,'idempotency_key','uuid',NULL::text),
            ('project_creations',3,'original_actor_id','uuid',NULL::text),
            ('project_creations',4,'project_id','uuid',NULL::text),
            ('project_creations',5,'fingerprint_version','integer',NULL::text),
            ('project_creations',6,'fingerprint','text',NULL::text),
            ('project_creations',7,'name','text',NULL::text),
            ('project_creations',8,'description','text',NULL::text),
            ('project_creations',9,'created_at','timestamp with time zone',NULL::text),
            ('project_creations',10,'initial_project_version','bigint',NULL::text),
            ('project_creations',11,'initial_membership_role','text',NULL::text),
            ('project_creations',12,'initial_membership_active','boolean',NULL::text),
            ('project_creations',13,'initial_membership_version','bigint',NULL::text)
        ), actual AS (
            SELECT r.relname::text table_name,a.attnum::integer ordinal,a.attname::text column_name,
                pg_catalog.format_type(a.atttypid,a.atttypmod) type_name,
                pg_catalog.pg_get_expr(d.adbin,d.adrelid) default_expression,
                a.attnotnull,a.attisdropped,a.attidentity,a.attgenerated,a.attndims,a.attinhcount,a.attislocal,
                a.attcollation=ty.typcollation accepted_collation,tn.nspname type_schema
            FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid
            JOIN pg_catalog.pg_type ty ON ty.oid=a.atttypid JOIN pg_catalog.pg_namespace tn ON tn.oid=ty.typnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE n.nspname='braid' AND r.relname=ANY(v_tables) AND a.attnum>0
        )
        SELECT 1 FROM expected e FULL JOIN actual a ON a.table_name=e.table_name AND a.ordinal=e.ordinal
        WHERE e.table_name IS NULL OR a.table_name IS NULL OR a.column_name <> e.column_name
          OR a.type_name <> e.type_name OR a.default_expression IS DISTINCT FROM e.default_expression
          OR NOT a.attnotnull OR a.attisdropped OR a.attidentity <> '' OR a.attgenerated <> ''
          OR a.attndims <> 0 OR a.attinhcount <> 0 OR NOT a.attislocal OR NOT a.accepted_collation OR a.type_schema <> 'pg_catalog'
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation source columns are inconsistent.';
    END IF;

    -- Exact CHECK text was calibrated from these trusted declarations on pristine
    -- PostgreSQL 16.15 with pg_get_constraintdef(oid,false). UTF8 hex preserves
    -- the canonical Unicode/control characters without SQL-literal ambiguity.
    IF EXISTS (
        WITH expected(table_name,constraint_name,kind,key_columns,referenced_table,referenced_columns,check_definition) AS (VALUES
            ('organizations','organizations_pkey','p',ARRAY['id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('organizations','organizations_id_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((id <> ''00000000-0000-0000-0000-000000000000''::uuid))'),
            ('organizations','organizations_name_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))'),
            ('principals','principals_pkey','p',ARRAY['id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('principals','principals_id_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((id <> ''00000000-0000-0000-0000-000000000000''::uuid))'),
            ('organization_memberships','organization_memberships_pkey','p',ARRAY['organization_id','principal_id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('organization_memberships','organization_memberships_organization_id_fkey','f',ARRAY['organization_id']::text[],'organizations',ARRAY['id']::text[],NULL::text),
            ('organization_memberships','organization_memberships_principal_id_fkey','f',ARRAY['principal_id']::text[],'principals',ARRAY['id']::text[],NULL::text),
            ('projects','projects_pkey','p',ARRAY['organization_id','id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('projects','projects_organization_id_fkey','f',ARRAY['organization_id']::text[],'organizations',ARRAY['id']::text[],NULL::text),
            ('projects','projects_organization_id_creator_principal_id_fkey','f',ARRAY['organization_id','creator_principal_id']::text[],'organization_memberships',ARRAY['organization_id','principal_id']::text[],NULL::text),
            ('projects','projects_id_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((id <> ''00000000-0000-0000-0000-000000000000''::uuid))'),
            ('projects','projects_name_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))'),
            ('projects','projects_description_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((char_length(description) <= 4000))'),
            ('projects','projects_version_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((version > 0))'),
            ('projects','projects_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((updated_at >= created_at))'),
            ('project_memberships','project_memberships_pkey','p',ARRAY['organization_id','project_id','principal_id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('project_memberships','project_memberships_organization_id_project_id_fkey','f',ARRAY['organization_id','project_id']::text[],'projects',ARRAY['organization_id','id']::text[],NULL::text),
            ('project_memberships','project_memberships_organization_id_principal_id_fkey','f',ARRAY['organization_id','principal_id']::text[],'organization_memberships',ARRAY['organization_id','principal_id']::text[],NULL::text),
            ('project_memberships','project_memberships_role_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((role = ANY (ARRAY[''viewer''::text, ''runner''::text, ''publisher''::text, ''administrator''::text])))'),
            ('project_memberships','project_memberships_version_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],'CHECK ((version > 0))'),
            ('organization_project_creation_capabilities','organization_project_creation_capabilities_pkey','p',ARRAY['organization_id','principal_id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('organization_project_creation_capabilities','organization_project_creation_capabilities_membership_fkey','f',ARRAY['organization_id','principal_id']::text[],'organization_memberships',ARRAY['organization_id','principal_id']::text[],NULL::text),
            ('project_creations','project_creations_pkey','p',ARRAY['organization_id','idempotency_key']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('project_creations','project_creations_project_key','u',ARRAY['organization_id','project_id']::text[],NULL::text,ARRAY[]::text[],NULL::text),
            ('project_creations','project_creations_project_fkey','f',ARRAY['organization_id','project_id']::text[],'projects',ARRAY['organization_id','id']::text[],NULL::text),
            ('project_creations','project_creations_actor_fkey','f',ARRAY['organization_id','original_actor_id']::text[],'organization_memberships',ARRAY['organization_id','principal_id']::text[],NULL::text),
            ('project_creations','project_creations_key_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b2028286964656d706f74656e63795f6b6579203c3e202730303030303030302d303030302d303030302d303030302d303030303030303030303030273a3a757569642929','hex'),'UTF8')),
            ('project_creations','project_creations_project_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b20282870726f6a6563745f6964203c3e202730303030303030302d303030302d303030302d303030302d303030303030303030303030273a3a757569642929','hex'),'UTF8')),
            ('project_creations','project_creations_fingerprint_version_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b20282866696e6765727072696e745f76657273696f6e203d20312929','hex'),'UTF8')),
            ('project_creations','project_creations_fingerprint_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b20282866696e6765727072696e74207e20275e5b302d39612d665d7b36347d24273a3a746578742929','hex'),'UTF8')),
            ('project_creations','project_creations_name_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b2028282828636861725f6c656e677468286e616d6529203e3d20312920414e442028636861725f6c656e677468286e616d6529203c3d20323030292920414e4420286f637465745f6c656e677468286e616d6529203c3d203830302920414e442028286e616d6520434f4c4c4154452022432229203d2028627472696d286e616d652c2027090a0b0c0d20c285c2a0e19a80e28080e28081e28082e28083e28084e28085e28086e28087e28088e28089e2808ae280a8e280a9e280afe2819fe38080273a3a746578742920434f4c4c4154452022432229292929','hex'),'UTF8')),
            ('project_creations','project_creations_description_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b20282828636861725f6c656e677468286465736372697074696f6e29203c3d20343030302920414e4420286f637465745f6c656e677468286465736372697074696f6e29203c3d203136303030292929','hex'),'UTF8')),
            ('project_creations','project_creations_initial_project_version_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b202828696e697469616c5f70726f6a6563745f76657273696f6e203d20312929','hex'),'UTF8')),
            ('project_creations','project_creations_initial_membership_role_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b202828696e697469616c5f6d656d626572736869705f726f6c65203d202761646d696e6973747261746f72273a3a746578742929','hex'),'UTF8')),
            ('project_creations','project_creations_initial_membership_active_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b2028696e697469616c5f6d656d626572736869705f61637469766529','hex'),'UTF8')),
            ('project_creations','project_creations_initial_membership_version_check','c',ARRAY[]::text[],NULL::text,ARRAY[]::text[],pg_catalog.convert_from(pg_catalog.decode('434845434b202828696e697469616c5f6d656d626572736869705f76657273696f6e203d20312929','hex'),'UTF8'))
        ), actual AS (
            SELECT r.relname::text table_name,c.conname::text constraint_name,c.contype::text kind,
                ARRAY(SELECT a.attname::text FROM pg_catalog.unnest(c.conkey) WITH ORDINALITY k(attnum,ord)
                      JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum ORDER BY k.ord) key_columns,
                CASE WHEN c.contype='f' THEN rr.relname::text END referenced_table,
                ARRAY(SELECT a.attname::text FROM pg_catalog.unnest(c.confkey) WITH ORDINALITY k(attnum,ord)
                      JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum ORDER BY k.ord) referenced_columns,
                CASE WHEN c.contype='c' THEN pg_catalog.pg_get_constraintdef(c.oid,false) END check_definition,
                c.convalidated,c.condeferrable,c.condeferred,c.connoinherit,c.confdeltype,c.confupdtype,c.confmatchtype,
                rn.nspname referenced_schema,c.oid constraint_oid,c.conrelid,c.confrelid
            FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
            LEFT JOIN pg_catalog.pg_class rr ON rr.oid=c.confrelid
            LEFT JOIN pg_catalog.pg_namespace rn ON rn.oid=rr.relnamespace
            WHERE n.nspname='braid' AND r.relname=ANY(v_tables)
        )
        SELECT 1 FROM expected e FULL JOIN actual a ON a.table_name=e.table_name AND a.constraint_name=e.constraint_name
        WHERE e.table_name IS NULL OR a.table_name IS NULL OR a.kind <> e.kind
          OR NOT a.convalidated OR a.condeferrable OR a.condeferred
          OR (e.kind = 'c' AND a.connoinherit)
          OR (e.kind <> 'c' AND a.key_columns IS DISTINCT FROM e.key_columns)
          OR (e.kind = 'c' AND a.check_definition IS DISTINCT FROM e.check_definition)
          OR (e.kind = 'f' AND (a.referenced_schema IS DISTINCT FROM 'braid'
              OR a.referenced_table IS DISTINCT FROM e.referenced_table
              OR a.referenced_columns IS DISTINCT FROM e.referenced_columns
              OR a.confdeltype <> 'r' OR a.confupdtype <> 'a' OR a.confmatchtype <> 's'
              OR (SELECT count(*) FROM pg_catalog.pg_trigger t WHERE t.tgconstraint=a.constraint_oid AND t.tgisinternal) <> 4
              OR EXISTS (SELECT 1 FROM pg_catalog.pg_trigger t
                  WHERE t.tgconstraint=a.constraint_oid
                    AND (NOT t.tgisinternal OR t.tgenabled <> 'O'
                         OR t.tgrelid NOT IN (a.conrelid,a.confrelid)))))
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation source constraints are inconsistent.';
    END IF;

    FOR v_table IN
        SELECT r.oid,r.relname,r.relrowsecurity,r.relforcerowsecurity,
            (SELECT count(*) FROM pg_catalog.pg_policy p WHERE p.polrelid=r.oid) policy_count
        FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
        WHERE n.nspname='braid' AND r.relname=ANY(v_tables)
    LOOP
        IF v_table.relname='principals' THEN
            IF v_table.relrowsecurity OR v_table.relforcerowsecurity OR v_table.policy_count <> 0 THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation organization policies are inconsistent.';
            END IF;
        ELSE
            v_expected_expression := '(' || (CASE WHEN v_table.relname='organizations' THEN 'id' ELSE 'organization_id' END)
                || ' = (NULLIF(current_setting(''braid.organization_id''::text, true), ''''::text))::uuid)';
            IF NOT v_table.relrowsecurity OR v_table.relforcerowsecurity OR v_table.policy_count <> 1
               OR NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_policy p WHERE p.polrelid=v_table.oid
                  AND p.polname=v_table.relname || '_organization_scope' AND p.polcmd='*' AND p.polpermissive
                  AND p.polroles=ARRAY[0::oid]
                  AND pg_catalog.pg_get_expr(p.polqual,p.polrelid,false)=v_expected_expression
                  AND pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false)=v_expected_expression
            ) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation organization policies are inconsistent.';
            END IF;
        END IF;
        IF (SELECT count(*) FROM pg_catalog.pg_trigger t WHERE t.tgrelid=v_table.oid AND NOT t.tgisinternal)
            <> (CASE v_table.relname WHEN 'project_creations' THEN 1 WHEN 'organization_memberships' THEN 2 ELSE 0 END) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation transition triggers are inconsistent.';
        END IF;
    END LOOP;
    IF (SELECT count(*) FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc f ON f.oid=t.tgfoid
        JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace JOIN pg_catalog.pg_roles o ON o.oid=f.proowner
        WHERE t.tgrelid='braid.organization_memberships'::regclass AND NOT t.tgisinternal AND t.tgenabled='O'
          AND t.tgnargs=0 AND t.tgattr::text='' AND t.tgqual IS NULL AND t.tgconstraint=0
          AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
          AND n.nspname='braid' AND o.rolname='braid_migrator' AND f.pronargs=0
          AND ((t.tgname='organization_admission_incarnation' AND t.tgtype=21 AND f.proname='organization_admission_incarnation')
            OR (t.tgname='organization_admission_membership_retained' AND t.tgtype=11
                AND f.proname='organization_admission_state_guard'))) <> 2 THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation transition triggers are inconsistent.';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc f ON f.oid=t.tgfoid
        JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace JOIN pg_catalog.pg_roles o ON o.oid=f.proowner
        JOIN pg_catalog.pg_language l ON l.oid=f.prolang
        WHERE t.tgrelid='braid.project_creations'::regclass AND NOT t.tgisinternal
          AND t.tgname='project_creations_transition_guard' AND t.tgtype=31 AND t.tgenabled='O'
          AND t.tgnargs=0 AND t.tgattr::text='' AND t.tgqual IS NULL
          AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
          AND n.nspname='braid' AND f.proname='guard_project_creations_transition' AND o.rolname='braid_migrator'
          AND l.lanname='plpgsql' AND NOT f.prosecdef AND f.prokind='f' AND f.provolatile='v'
          AND f.proparallel='u' AND NOT f.proisstrict AND NOT f.proleakproof AND f.pronargs=0
          AND f.prorettype='pg_catalog.trigger'::regtype AND NOT f.proretset
          AND f.proconfig=ARRAY['search_path=pg_catalog, braid, pg_temp']
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation transition triggers are inconsistent.';
    END IF;

    -- Creator rights are rechecked here too: direct routine calls cannot bypass
    -- the application pool's EXECUTE-only role admission. Filled from guard SQL.
    IF (
        WITH creator AS (
            SELECT role.* FROM pg_catalog.pg_roles role WHERE role.rolname = 'braid_project_creator'
        ), reachable_roles AS (
            SELECT role.* FROM pg_catalog.pg_roles role
            WHERE role.rolname = 'braid_project_creator'
               OR pg_catalog.pg_has_role('braid_project_creator', role.oid, 'MEMBER')
        ), application_relations AS (
            SELECT relation.* FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'braid' AND relation.relkind IN ('r','p','v','m','f')
        ), expected_functions(signature, creator_execute) AS (
            VALUES ('braid.get_project_creation_admission_v1(uuid,uuid)', true),
                   ('braid.create_project_v1(uuid,uuid,uuid,text,text)', true),
                   ('braid.assert_project_creation_contract_v1()', false),
                   ('braid.project_creation_fingerprint_v1(uuid,uuid,uuid,text,text)', false),
                   ('braid.guard_project_creations_transition()', false)
        ), installed_functions AS (
            SELECT expected.signature, expected.creator_execute, function.* FROM expected_functions expected
            LEFT JOIN pg_catalog.pg_proc function ON function.oid = pg_catalog.to_regprocedure(expected.signature)
        )
        SELECT (SELECT count(*) FROM creator) = 1
          AND (SELECT count(*) FROM reachable_roles) = 1
          AND (SELECT rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                      AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication FROM creator)
          AND pg_catalog.has_database_privilege('braid_project_creator', current_database(), 'CONNECT')
          AND NOT pg_catalog.has_database_privilege('public', current_database(), 'CONNECT,CREATE,TEMPORARY')
          AND pg_catalog.has_schema_privilege('braid_project_creator', 'braid', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('public', 'braid', 'USAGE,CREATE')
          AND (SELECT owner.rolname = 'braid_migrator' FROM pg_catalog.pg_namespace namespace
               JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'braid')
          AND NOT EXISTS (
              SELECT 1 FROM reachable_roles role
              WHERE role.rolsuper OR role.rolbypassrls OR role.rolcreaterole OR role.rolcreatedb OR role.rolreplication
                OR role.rolname IN ('braid_migrator','braid_runtime','braid_ingest','braid_catalog_reader','braid_catalog_editor',
                                   'pg_read_all_data','pg_write_all_data','pg_read_server_files','pg_write_server_files',
                                   'pg_execute_server_program','pg_signal_backend','pg_maintain')
                OR pg_catalog.has_database_privilege(role.oid, current_database(), 'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                OR pg_catalog.has_parameter_privilege(role.oid, 'session_replication_role', 'SET,ALTER SYSTEM')
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_auth_members membership
              WHERE membership.member IN (SELECT oid FROM reachable_roles)
                 OR membership.roleid IN (SELECT oid FROM reachable_roles)
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_database database_row CROSS JOIN reachable_roles role
              WHERE database_row.datdba = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_namespace namespace CROSS JOIN reachable_roles role
              WHERE namespace.nspname NOT LIKE 'pg_temp_%' AND namespace.nspname NOT LIKE 'pg_toast_temp_%'
                AND (namespace.nspowner = role.oid OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE')
                     OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'USAGE WITH GRANT OPTION'))
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_class relation CROSS JOIN reachable_roles role WHERE relation.relowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_proc function CROSS JOIN reachable_roles role WHERE function.proowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_type type_row CROSS JOIN reachable_roles role WHERE type_row.typowner = role.oid
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_shdepend dependency
              WHERE dependency.refclassid = 'pg_catalog.pg_authid'::regclass AND dependency.deptype = 'o'
                AND dependency.refobjid IN (SELECT oid FROM reachable_roles)
          )
          AND NOT EXISTS (
              SELECT 1 FROM application_relations relation CROSS JOIN reachable_roles role
              WHERE pg_catalog.has_table_privilege(role.oid, relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                 OR pg_catalog.has_any_column_privilege(role.oid, relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
          )
          AND NOT EXISTS (
              SELECT 1 FROM application_relations relation
              WHERE pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                 OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_class sequence JOIN pg_catalog.pg_namespace namespace ON namespace.oid = sequence.relnamespace
              WHERE namespace.nspname = 'braid' AND sequence.relkind = 'S'
                AND CASE WHEN sequence.relkind = 'S' THEN
                    (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_sequence_privilege(role.oid, sequence.oid, 'USAGE,SELECT,UPDATE')))
                    ELSE false END
          )
          AND (SELECT count(*) FROM installed_functions WHERE oid IS NOT NULL) = 5
          AND (SELECT count(*) FROM installed_functions
               WHERE creator_execute AND pg_catalog.has_function_privilege('braid_project_creator', oid, 'EXECUTE')) = 2
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
              WHERE namespace.nspname = 'braid'
                AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE WITH GRANT OPTION')
                                   OR (pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')
                                       AND NOT EXISTS (SELECT 1 FROM installed_functions accepted
                                                       WHERE accepted.oid = function.oid AND accepted.creator_execute))))
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function CROSS JOIN pg_catalog.pg_roles old_role
              WHERE old_role.rolname IN ('braid_runtime','braid_ingest','braid_catalog_reader','braid_catalog_editor')
                AND pg_catalog.has_function_privilege(old_role.oid, function.oid, 'EXECUTE')
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function
              CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(function.proacl, pg_catalog.acldefault('f', function.proowner))) grant_entry
              WHERE grant_entry.grantor <> function.proowner OR grant_entry.privilege_type <> 'EXECUTE' OR grant_entry.is_grantable
                 OR NOT (grant_entry.grantee = function.proowner
                         OR (function.creator_execute AND grant_entry.grantee = (SELECT oid FROM creator)))
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function
              WHERE (SELECT count(*) FROM pg_catalog.aclexplode(COALESCE(function.proacl, pg_catalog.acldefault('f', function.proowner))))
                    <> CASE WHEN function.creator_execute THEN 2 ELSE 1 END
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_default_acl defaults
              CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) grant_entry
              WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'braid'))
                AND (defaults.defaclrole IN (SELECT oid FROM reachable_roles)
                     OR grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles))
          )
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_parameter_acl parameter_acl
              CROSS JOIN LATERAL pg_catalog.aclexplode(parameter_acl.paracl) grant_entry
              WHERE grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles)
          )
    ) IS DISTINCT FROM true THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its exact execute-only privilege profile.';
    END IF;
END;
$braid_assert_project_creation_contract_v1$;
REVOKE ALL ON FUNCTION braid.assert_project_creation_contract_v1()
    FROM PUBLIC,braid_runtime,braid_ingest,braid_catalog_reader,braid_catalog_editor,braid_project_creator;
