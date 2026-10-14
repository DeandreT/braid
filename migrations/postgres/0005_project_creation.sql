-- Additive creation authority. No predecessor shape, policy, trigger or grant changes.
DO $weft_require_creator$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_roles
        WHERE rolname = 'weft_project_creator' AND rolcanlogin AND NOT rolinherit
          AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb
          AND NOT rolcreaterole AND NOT rolreplication
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its dedicated restricted login.';
    END IF;
END;
$weft_require_creator$;

CREATE TABLE weft.organization_project_creation_capabilities (
    organization_id uuid NOT NULL,
    principal_id uuid NOT NULL,
    can_create_projects boolean NOT NULL DEFAULT false,
    CONSTRAINT organization_project_creation_capabilities_pkey PRIMARY KEY (organization_id,principal_id),
    CONSTRAINT organization_project_creation_capabilities_membership_fkey
        FOREIGN KEY (organization_id,principal_id)
        REFERENCES weft.organization_memberships(organization_id,principal_id) ON DELETE RESTRICT
);

CREATE TABLE weft.project_creations (
    organization_id uuid NOT NULL,
    idempotency_key uuid NOT NULL,
    original_actor_id uuid NOT NULL,
    project_id uuid NOT NULL,
    fingerprint_version integer NOT NULL,
    fingerprint text NOT NULL,
    name text NOT NULL,
    description text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    initial_project_version bigint NOT NULL,
    initial_membership_role text NOT NULL,
    initial_membership_active boolean NOT NULL,
    initial_membership_version bigint NOT NULL,
    CONSTRAINT project_creations_pkey PRIMARY KEY (organization_id,idempotency_key),
    CONSTRAINT project_creations_project_key UNIQUE (organization_id,project_id),
    CONSTRAINT project_creations_project_fkey FOREIGN KEY (organization_id,project_id)
        REFERENCES weft.projects(organization_id,id) ON DELETE RESTRICT,
    CONSTRAINT project_creations_actor_fkey FOREIGN KEY (organization_id,original_actor_id)
        REFERENCES weft.organization_memberships(organization_id,principal_id) ON DELETE RESTRICT,
    CONSTRAINT project_creations_key_check CHECK (idempotency_key <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT project_creations_project_check CHECK (project_id <> '00000000-0000-0000-0000-000000000000'),
    CONSTRAINT project_creations_fingerprint_version_check CHECK (fingerprint_version = 1),
    CONSTRAINT project_creations_fingerprint_check CHECK (fingerprint ~ '^[0-9a-f]{64}$'),
    CONSTRAINT project_creations_name_check CHECK (
        char_length(name) BETWEEN 1 AND 200 AND octet_length(name) <= 800
        AND name COLLATE "C" = btrim(name,U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') COLLATE "C"),
    CONSTRAINT project_creations_description_check CHECK (char_length(description) <= 4000 AND octet_length(description) <= 16000),
    CONSTRAINT project_creations_initial_project_version_check CHECK (initial_project_version = 1),
    CONSTRAINT project_creations_initial_membership_role_check CHECK (initial_membership_role = 'administrator'),
    CONSTRAINT project_creations_initial_membership_active_check CHECK (initial_membership_active),
    CONSTRAINT project_creations_initial_membership_version_check CHECK (initial_membership_version = 1)
);

ALTER TABLE weft.organization_project_creation_capabilities ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_project_creation_capabilities_organization_scope ON weft.organization_project_creation_capabilities
    USING (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid);
ALTER TABLE weft.project_creations ENABLE ROW LEVEL SECURITY;
CREATE POLICY project_creations_organization_scope ON weft.project_creations
    USING (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid);

REVOKE ALL ON weft.organization_project_creation_capabilities,weft.project_creations FROM PUBLIC;
REVOKE ALL ON weft.organization_project_creation_capabilities,weft.project_creations
    FROM weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;
GRANT USAGE ON SCHEMA weft TO weft_project_creator;

CREATE FUNCTION weft.project_creation_fingerprint_v1(
    p_organization_id uuid,p_actor_id uuid,p_idempotency_key uuid,p_name text,p_description text
) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE STRICT SECURITY INVOKER
SET search_path = pg_catalog,weft,pg_temp
AS $weft_project_creation_fingerprint_v1$
DECLARE
    v_tag bytea := pg_catalog.convert_to('weft.project-creation-intent','UTF8');
    v_name bytea := pg_catalog.convert_to(p_name,'UTF8');
    v_description bytea := pg_catalog.convert_to(p_description,'UTF8');
    v_bytes bytea;
BEGIN
    IF pg_catalog.octet_length(v_name) > 800 OR pg_catalog.octet_length(v_description) > 16000 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Project creation metadata is outside its accepted bounds.';
    END IF;
    v_bytes := pg_catalog.decode(pg_catalog.lpad(pg_catalog.to_hex(pg_catalog.octet_length(v_tag)),8,'0'),'hex')
        || v_tag || pg_catalog.decode('00000001','hex')
        || pg_catalog.decode(pg_catalog.replace(p_organization_id::text,'-',''),'hex')
        || pg_catalog.decode(pg_catalog.replace(p_actor_id::text,'-',''),'hex')
        || pg_catalog.decode(pg_catalog.replace(p_idempotency_key::text,'-',''),'hex')
        || pg_catalog.decode(pg_catalog.lpad(pg_catalog.to_hex(pg_catalog.octet_length(v_name)),8,'0'),'hex') || v_name
        || pg_catalog.decode(pg_catalog.lpad(pg_catalog.to_hex(pg_catalog.octet_length(v_description)),8,'0'),'hex') || v_description;
    RETURN pg_catalog.encode(pg_catalog.sha256(v_bytes),'hex');
END;
$weft_project_creation_fingerprint_v1$;
REVOKE ALL ON FUNCTION weft.project_creation_fingerprint_v1(uuid,uuid,uuid,text,text)
    FROM PUBLIC,weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;

CREATE FUNCTION weft.assert_project_creation_contract_v1() RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog,weft,pg_temp
AS $weft_assert_project_creation_contract_v1$
DECLARE
    v_tables text[] := ARRAY['organizations','principals','organization_memberships','projects',
        'project_memberships','organization_project_creation_capabilities','project_creations'];
    v_table record;
    v_expected_expression text;
BEGIN
    IF current_user <> 'weft_migrator' OR current_setting('server_encoding') <> 'UTF8'
       OR current_setting('standard_conforming_strings') <> 'on'
       OR current_setting('session_replication_role') <> 'origin' OR current_setting('row_security') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its accepted execution profile.';
    END IF;

    -- Owner-held relation locks protect source DDL, not an ACL/role/function commit fence.
    LOCK TABLE ONLY weft.organizations, ONLY weft.principals, ONLY weft.organization_memberships,
        ONLY weft.projects, ONLY weft.project_memberships,
        ONLY weft.organization_project_creation_capabilities, ONLY weft.project_creations IN ACCESS SHARE MODE;
    IF (SELECT count(*) FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
        WHERE n.nspname='weft' AND r.relname=ANY(v_tables)) <> cardinality(v_tables)
       OR EXISTS (
        SELECT 1 FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
        JOIN pg_catalog.pg_roles ro ON ro.oid=r.relowner JOIN pg_catalog.pg_roles no ON no.oid=n.nspowner
        WHERE n.nspname='weft' AND r.relname=ANY(v_tables)
          AND (r.relkind <> 'r' OR r.relpersistence <> 'p' OR r.relispartition
               OR ro.rolname <> 'weft_migrator' OR no.rolname <> 'weft_migrator'
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
            WHERE n.nspname='weft' AND r.relname=ANY(v_tables) AND a.attnum>0
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
            WHERE n.nspname='weft' AND r.relname=ANY(v_tables)
        )
        SELECT 1 FROM expected e FULL JOIN actual a ON a.table_name=e.table_name AND a.constraint_name=e.constraint_name
        WHERE e.table_name IS NULL OR a.table_name IS NULL OR a.kind <> e.kind
          OR NOT a.convalidated OR a.condeferrable OR a.condeferred
          OR (e.kind = 'c' AND a.connoinherit)
          OR (e.kind <> 'c' AND a.key_columns IS DISTINCT FROM e.key_columns)
          OR (e.kind = 'c' AND a.check_definition IS DISTINCT FROM e.check_definition)
          OR (e.kind = 'f' AND (a.referenced_schema IS DISTINCT FROM 'weft'
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
        WHERE n.nspname='weft' AND r.relname=ANY(v_tables)
    LOOP
        IF v_table.relname='principals' THEN
            IF v_table.relrowsecurity OR v_table.relforcerowsecurity OR v_table.policy_count <> 0 THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation organization policies are inconsistent.';
            END IF;
        ELSE
            v_expected_expression := '(' || (CASE WHEN v_table.relname='organizations' THEN 'id' ELSE 'organization_id' END)
                || ' = (NULLIF(current_setting(''weft.organization_id''::text, true), ''''::text))::uuid)';
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
            <> (CASE WHEN v_table.relname='project_creations' THEN 1 ELSE 0 END) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation transition triggers are inconsistent.';
        END IF;
    END LOOP;
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_proc f ON f.oid=t.tgfoid
        JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace JOIN pg_catalog.pg_roles o ON o.oid=f.proowner
        JOIN pg_catalog.pg_language l ON l.oid=f.prolang
        WHERE t.tgrelid='weft.project_creations'::regclass AND NOT t.tgisinternal
          AND t.tgname='project_creations_transition_guard' AND t.tgtype=31 AND t.tgenabled='O'
          AND t.tgnargs=0 AND t.tgattr::text='' AND t.tgqual IS NULL
          AND NOT t.tgdeferrable AND NOT t.tginitdeferred AND t.tgoldtable IS NULL AND t.tgnewtable IS NULL
          AND n.nspname='weft' AND f.proname='guard_project_creations_transition' AND o.rolname='weft_migrator'
          AND l.lanname='plpgsql' AND NOT f.prosecdef AND f.prokind='f' AND f.provolatile='v'
          AND f.proparallel='u' AND NOT f.proisstrict AND NOT f.proleakproof AND f.pronargs=0
          AND f.prorettype='pg_catalog.trigger'::regtype AND NOT f.proretset
          AND f.proconfig=ARRAY['search_path=pg_catalog, weft, pg_temp']
    ) THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation transition triggers are inconsistent.';
    END IF;

    -- Creator rights are rechecked here too: direct routine calls cannot bypass
    -- the application pool's EXECUTE-only role admission. Filled from guard SQL.
    IF (
        WITH creator AS (
            SELECT role.* FROM pg_catalog.pg_roles role WHERE role.rolname = 'weft_project_creator'
        ), reachable_roles AS (
            SELECT role.* FROM pg_catalog.pg_roles role
            WHERE role.rolname = 'weft_project_creator'
               OR pg_catalog.pg_has_role('weft_project_creator', role.oid, 'MEMBER')
        ), application_relations AS (
            SELECT relation.* FROM pg_catalog.pg_class relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            WHERE namespace.nspname = 'weft' AND relation.relkind IN ('r','p','v','m','f')
        ), expected_functions(signature, creator_execute) AS (
            VALUES ('weft.get_project_creation_admission_v1(uuid,uuid)', true),
                   ('weft.create_project_v1(uuid,uuid,uuid,text,text)', true),
                   ('weft.assert_project_creation_contract_v1()', false),
                   ('weft.project_creation_fingerprint_v1(uuid,uuid,uuid,text,text)', false),
                   ('weft.guard_project_creations_transition()', false)
        ), installed_functions AS (
            SELECT expected.signature, expected.creator_execute, function.* FROM expected_functions expected
            LEFT JOIN pg_catalog.pg_proc function ON function.oid = pg_catalog.to_regprocedure(expected.signature)
        )
        SELECT (SELECT count(*) FROM creator) = 1
          AND (SELECT count(*) FROM reachable_roles) = 1
          AND (SELECT rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                      AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication FROM creator)
          AND pg_catalog.has_database_privilege('weft_project_creator', current_database(), 'CONNECT')
          AND NOT pg_catalog.has_database_privilege('public', current_database(), 'CONNECT,CREATE,TEMPORARY')
          AND pg_catalog.has_schema_privilege('weft_project_creator', 'weft', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('public', 'weft', 'USAGE,CREATE')
          AND (SELECT owner.rolname = 'weft_migrator' FROM pg_catalog.pg_namespace namespace
               JOIN pg_catalog.pg_roles owner ON owner.oid = namespace.nspowner WHERE namespace.nspname = 'weft')
          AND NOT EXISTS (
              SELECT 1 FROM reachable_roles role
              WHERE role.rolsuper OR role.rolbypassrls OR role.rolcreaterole OR role.rolcreatedb OR role.rolreplication
                OR role.rolname IN ('weft_migrator','weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor',
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
              WHERE namespace.nspname = 'weft' AND sequence.relkind = 'S'
                AND (pg_catalog.has_sequence_privilege('public', sequence.oid, 'USAGE,SELECT,UPDATE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_sequence_privilege(role.oid, sequence.oid, 'USAGE,SELECT,UPDATE')))
          )
          AND (SELECT count(*) FROM installed_functions WHERE oid IS NOT NULL) = 5
          AND (SELECT count(*) FROM installed_functions
               WHERE creator_execute AND pg_catalog.has_function_privilege('weft_project_creator', oid, 'EXECUTE')) = 2
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
              WHERE namespace.nspname = 'weft'
                AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                     OR EXISTS (SELECT 1 FROM reachable_roles role
                                WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE WITH GRANT OPTION')
                                   OR (pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')
                                       AND NOT EXISTS (SELECT 1 FROM installed_functions accepted
                                                       WHERE accepted.oid = function.oid AND accepted.creator_execute))))
          )
          AND NOT EXISTS (
              SELECT 1 FROM installed_functions function CROSS JOIN pg_catalog.pg_roles old_role
              WHERE old_role.rolname IN ('weft_runtime','weft_ingest','weft_catalog_reader','weft_catalog_editor')
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
              WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'weft'))
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
$weft_assert_project_creation_contract_v1$;
REVOKE ALL ON FUNCTION weft.assert_project_creation_contract_v1()
    FROM PUBLIC,weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;

CREATE FUNCTION weft.guard_project_creations_transition() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER
SET search_path = pg_catalog,weft,pg_temp
AS $weft_guard_project_creations_transition$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Project creation facts are immutable.';
    END IF;
    IF NEW.fingerprint_version <> 1 OR NEW.fingerprint IS DISTINCT FROM
        weft.project_creation_fingerprint_v1(NEW.organization_id,NEW.original_actor_id,NEW.idempotency_key,NEW.name,NEW.description)
       OR NEW.initial_project_version <> 1 OR NEW.initial_membership_role <> 'administrator'
       OR NOT NEW.initial_membership_active OR NEW.initial_membership_version <> 1
       OR NOT EXISTS (
        SELECT 1 FROM weft.projects p JOIN weft.project_memberships m
          ON m.organization_id=p.organization_id AND m.project_id=p.id AND m.principal_id=p.creator_principal_id
        WHERE p.organization_id=NEW.organization_id AND p.id=NEW.project_id
          AND p.creator_principal_id=NEW.original_actor_id
          AND p.name COLLATE "C"=NEW.name COLLATE "C" AND p.description COLLATE "C"=NEW.description COLLATE "C"
          AND p.created_at=NEW.created_at AND p.updated_at=NEW.created_at AND p.version=1
          AND m.role='administrator' AND m.active AND m.version=1
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Project creation facts must match their atomic initial records.';
    END IF;
    RETURN NEW;
END;
$weft_guard_project_creations_transition$;
REVOKE ALL ON FUNCTION weft.guard_project_creations_transition()
    FROM PUBLIC,weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;
CREATE TRIGGER project_creations_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.project_creations
    FOR EACH ROW EXECUTE FUNCTION weft.guard_project_creations_transition();

CREATE FUNCTION weft.get_project_creation_admission_v1(p_organization_id uuid,p_actor_id uuid)
RETURNS TABLE(outcome text,organization_id uuid,principal_id uuid,can_create_projects boolean)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY DEFINER
SET search_path = pg_catalog,weft,pg_temp
AS $weft_get_project_creation_admission_v1$
BEGIN
    IF p_organization_id IS NULL OR p_actor_id IS NULL
       OR p_organization_id='00000000-0000-0000-0000-000000000000'
       OR p_actor_id='00000000-0000-0000-0000-000000000000' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Project creation identifiers must be nonempty.';
    END IF;
    IF session_user <> 'weft_project_creator' OR current_user <> 'weft_migrator'
       OR current_setting('transaction_isolation') <> 'read committed' OR current_setting('transaction_read_only') <> 'off'
       OR current_setting('client_encoding') <> 'UTF8'
       OR current_setting('standard_conforming_strings') <> 'on'
       OR current_setting('lock_timeout') <> '1s' OR current_setting('statement_timeout') <> '5s'
       OR current_setting('weft.organization_id',true) IS DISTINCT FROM p_organization_id::text THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its accepted bound execution profile.';
    END IF;
    PERFORM weft.assert_project_creation_contract_v1();
    -- An advisory observation only: no authority row lock survives body admission.
    SELECT o.id,p.id,COALESCE(c.can_create_projects,false)
    INTO organization_id,principal_id,can_create_projects
    FROM weft.organizations o JOIN weft.organization_memberships m ON m.organization_id=o.id
    JOIN weft.principals p ON p.id=m.principal_id AND p.active
    LEFT JOIN weft.organization_project_creation_capabilities c
      ON c.organization_id=m.organization_id AND c.principal_id=m.principal_id
    WHERE o.id=p_organization_id AND p.id=p_actor_id AND m.active;
    IF NOT FOUND THEN
        outcome := 'not_admitted';
    ELSE
        outcome := CASE WHEN can_create_projects THEN 'admitted' ELSE 'capability_denied' END;
    END IF;
    RETURN NEXT;
END;
$weft_get_project_creation_admission_v1$;
REVOKE ALL ON FUNCTION weft.get_project_creation_admission_v1(uuid,uuid)
    FROM PUBLIC,weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;
GRANT EXECUTE ON FUNCTION weft.get_project_creation_admission_v1(uuid,uuid) TO weft_project_creator;

CREATE FUNCTION weft.create_project_v1(
    p_organization_id uuid,p_actor_id uuid,p_idempotency_key uuid,p_name text,p_description text
)
RETURNS TABLE(outcome text,organization_id uuid,idempotency_key uuid,original_actor_id uuid,project_id uuid,
    fingerprint_version integer,fingerprint text,name text,description text,created_at timestamp with time zone,
    initial_project_version bigint,initial_membership_role text,initial_membership_active boolean,initial_membership_version bigint)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY DEFINER
SET search_path = pg_catalog,weft,pg_temp
AS $weft_create_project_v1$
DECLARE
    v_active boolean;
    v_allowed boolean;
    v_receipt weft.project_creations%ROWTYPE;
    v_fingerprint text;
    v_project_id uuid;
    v_created_at timestamp with time zone;
    v_schema text;
    v_table text;
    v_constraint text;
    v_lost_key boolean := false;
BEGIN
    IF p_organization_id IS NULL OR p_actor_id IS NULL OR p_idempotency_key IS NULL
       OR p_organization_id='00000000-0000-0000-0000-000000000000'
       OR p_actor_id='00000000-0000-0000-0000-000000000000'
       OR p_idempotency_key='00000000-0000-0000-0000-000000000000' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Project creation identifiers must be nonempty.';
    END IF;
    IF session_user <> 'weft_project_creator' OR current_user <> 'weft_migrator'
       OR current_setting('transaction_isolation') <> 'read committed' OR current_setting('transaction_read_only') <> 'off'
       OR current_setting('client_encoding') <> 'UTF8'
       OR current_setting('standard_conforming_strings') <> 'on'
       OR current_setting('lock_timeout') <> '1s' OR current_setting('statement_timeout') <> '5s'
       OR current_setting('weft.organization_id',true) IS DISTINCT FROM p_organization_id::text THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Project creation requires its accepted bound execution profile.';
    END IF;
    PERFORM weft.assert_project_creation_contract_v1();
    IF p_organization_id IS NULL OR p_actor_id IS NULL OR p_idempotency_key IS NULL
       OR p_organization_id='00000000-0000-0000-0000-000000000000'
       OR p_actor_id='00000000-0000-0000-0000-000000000000'
       OR p_idempotency_key='00000000-0000-0000-0000-000000000000'
       OR p_name IS NULL OR p_description IS NULL
       OR pg_catalog.char_length(p_name) NOT BETWEEN 1 AND 200 OR pg_catalog.octet_length(p_name)>800
       OR p_name COLLATE "C" <> pg_catalog.btrim(p_name,U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') COLLATE "C"
       OR pg_catalog.char_length(p_description)>4000 OR pg_catalog.octet_length(p_description)>16000 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Project creation requires accepted bounded metadata and identifiers.';
    END IF;
    v_fingerprint := weft.project_creation_fingerprint_v1(p_organization_id,p_actor_id,p_idempotency_key,p_name,p_description);

    -- Shared authority locks are outside the insertion subtransaction. Distinct
    -- keys can proceed together; committed revocation before each lock denies.
    PERFORM o.id FROM weft.organizations o WHERE o.id=p_organization_id FOR KEY SHARE OF o;
    IF NOT FOUND THEN outcome := 'not_admitted'; RETURN NEXT; RETURN; END IF;
    SELECT p.active INTO v_active FROM weft.principals p WHERE p.id=p_actor_id FOR SHARE OF p;
    IF NOT FOUND OR NOT v_active THEN outcome := 'not_admitted'; RETURN NEXT; RETURN; END IF;
    SELECT m.active INTO v_active FROM weft.organization_memberships m
        WHERE m.organization_id=p_organization_id AND m.principal_id=p_actor_id FOR SHARE OF m;
    IF NOT FOUND OR NOT v_active THEN outcome := 'not_admitted'; RETURN NEXT; RETURN; END IF;
    SELECT c.can_create_projects INTO v_allowed FROM weft.organization_project_creation_capabilities c
        WHERE c.organization_id=p_organization_id AND c.principal_id=p_actor_id FOR SHARE OF c;
    IF NOT FOUND OR NOT v_allowed THEN outcome := 'capability_denied'; RETURN NEXT; RETURN; END IF;

    SELECT c.* INTO v_receipt FROM weft.project_creations c
        WHERE c.organization_id=p_organization_id AND c.idempotency_key=p_idempotency_key;
    IF NOT FOUND THEN
        BEGIN
            v_project_id := pg_catalog.gen_random_uuid();
            IF v_project_id='00000000-0000-0000-0000-000000000000' THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation generated an invalid identity.';
            END IF;
            v_created_at := pg_catalog.clock_timestamp();
            INSERT INTO weft.projects(organization_id,id,name,description,creator_principal_id,created_at,updated_at,version)
                VALUES(p_organization_id,v_project_id,p_name,p_description,p_actor_id,v_created_at,v_created_at,1);
            INSERT INTO weft.project_memberships(organization_id,project_id,principal_id,role,active,version)
                VALUES(p_organization_id,v_project_id,p_actor_id,'administrator',true,1);
            INSERT INTO weft.project_creations AS c(organization_id,idempotency_key,original_actor_id,project_id,
                fingerprint_version,fingerprint,name,description,created_at,initial_project_version,
                initial_membership_role,initial_membership_active,initial_membership_version)
                VALUES(p_organization_id,p_idempotency_key,p_actor_id,v_project_id,1,v_fingerprint,p_name,p_description,
                    v_created_at,1,'administrator',true,1) RETURNING c.* INTO v_receipt;
            outcome := 'created';
        EXCEPTION WHEN unique_violation THEN
            GET STACKED DIAGNOSTICS v_schema=SCHEMA_NAME,v_table=TABLE_NAME,v_constraint=CONSTRAINT_NAME;
            IF v_schema IS DISTINCT FROM 'weft' OR v_table IS DISTINCT FROM 'project_creations'
               OR v_constraint IS DISTINCT FROM 'project_creations_pkey' THEN
                RAISE;
            END IF;
            v_lost_key := true;
        END;
        IF v_lost_key THEN
            -- Outside the completed insertion subtransaction, all three
            -- temporary writes have rolled back. This distinct VOLATILE query
            -- gets a fresh ReadCommitted snapshot after the winning commit.
            SELECT c.* INTO v_receipt FROM weft.project_creations c
                WHERE c.organization_id=p_organization_id AND c.idempotency_key=p_idempotency_key;
            IF NOT FOUND THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation conflict has no committed original facts.';
            END IF;
        END IF;
    END IF;
    IF v_receipt.fingerprint_version <> 1 OR v_receipt.fingerprint IS DISTINCT FROM
        weft.project_creation_fingerprint_v1(v_receipt.organization_id,v_receipt.original_actor_id,
            v_receipt.idempotency_key,v_receipt.name,v_receipt.description)
       OR v_receipt.initial_project_version <> 1 OR v_receipt.initial_membership_role <> 'administrator'
       OR NOT v_receipt.initial_membership_active OR v_receipt.initial_membership_version <> 1 THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'Project creation original facts are inconsistent.';
    END IF;
    IF v_receipt.original_actor_id <> p_actor_id
       OR v_receipt.fingerprint COLLATE "C" <> v_fingerprint COLLATE "C"
       OR v_receipt.name COLLATE "C" <> p_name COLLATE "C"
       OR v_receipt.description COLLATE "C" <> p_description COLLATE "C" THEN
        outcome := 'idempotency_conflict'; RETURN NEXT; RETURN;
    END IF;
    IF outcome IS NULL THEN outcome := 'replayed'; END IF;
    organization_id := v_receipt.organization_id;
    idempotency_key := v_receipt.idempotency_key;
    original_actor_id := v_receipt.original_actor_id;
    project_id := v_receipt.project_id;
    fingerprint_version := v_receipt.fingerprint_version;
    fingerprint := v_receipt.fingerprint;
    name := v_receipt.name;
    description := v_receipt.description;
    created_at := v_receipt.created_at;
    initial_project_version := v_receipt.initial_project_version;
    initial_membership_role := v_receipt.initial_membership_role;
    initial_membership_active := v_receipt.initial_membership_active;
    initial_membership_version := v_receipt.initial_membership_version;
    RETURN NEXT;
END;
$weft_create_project_v1$;
REVOKE ALL ON FUNCTION weft.create_project_v1(uuid,uuid,uuid,text,text)
    FROM PUBLIC,weft_runtime,weft_ingest,weft_catalog_reader,weft_catalog_editor,weft_project_creator;
GRANT EXECUTE ON FUNCTION weft.create_project_v1(uuid,uuid,uuid,text,text) TO weft_project_creator;
