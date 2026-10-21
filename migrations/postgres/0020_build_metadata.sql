-- Retained metadata queries are distinct from active approval admission and artifact publication.
DO $role$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname='weft_build_metadata_reader';
    IF current_user<>'weft_migrator' OR session_user<>current_user OR NOT FOUND OR NOT r.rolcanlogin
        OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls
        OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL
        OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
        OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
            AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
        OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE'))
        OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE') THEN
        RAISE EXCEPTION 'Independent build metadata reader required.' USING ERRCODE='42501';
    END IF;
END;
$role$;

CREATE INDEX build_approval_history ON weft.build_approvals(organization_id,project_id,mapping_id,actor_id,created_at DESC,approval_id DESC);
CREATE INDEX build_run_history ON weft.build_runs(organization_id,project_id,actor_id,created_at DESC,build_id DESC);

-- One page-control row precedes list items. No native/artifact bytes or claim tokens are returned.
CREATE FUNCTION weft.read_build_metadata(operation text, org uuid, project uuid, mapping uuid, key uuid, revision bigint, maximum integer, after_id uuid, after_version bigint)
RETURNS TABLE(document jsonb, intent bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; at_time timestamp with time zone;
BEGIN
    IF session_user<>'weft_build_metadata_reader' OR current_user<>'weft_migrator'
        OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN; END IF;
    actor:=NULLIF(current_setting('weft.build_principal_id',true),'')::uuid;
    IF weft.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN; END IF;
    IF operation='environment' THEN
        IF key IS NULL OR revision IS NULL OR revision<1 THEN RETURN; END IF;
        RETURN QUERY SELECT to_jsonb(e),NULL::bytea FROM ONLY weft.build_environments e
            WHERE e.organization_id=org AND e.project_id=project AND e.environment_id=key AND e.version=revision;
    ELSIF operation='approval' THEN
        RETURN QUERY SELECT (to_jsonb(a)-'intent_body')||jsonb_build_object('environment_active',e.active),a.intent_body
            FROM ONLY weft.build_approvals a JOIN ONLY weft.build_environments e
                ON e.organization_id=a.organization_id AND e.project_id=a.project_id AND e.environment_id=a.environment_id AND e.version=a.environment_version
            WHERE a.organization_id=org AND a.project_id=project AND a.mapping_id=mapping AND a.approval_id=key AND a.actor_id=actor;
    ELSIF operation IN ('environments','approvals','runs') THEN
        IF maximum IS NULL OR maximum NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Build metadata page bound required.' USING ERRCODE='22023'; END IF;
        IF operation='environments' THEN
            IF (after_id IS NULL AND after_version IS NOT NULL) OR (after_id IS NOT NULL AND (after_version IS NULL OR after_version<1 OR
                NOT EXISTS(SELECT 1 FROM ONLY weft.build_environments e WHERE e.organization_id=org AND e.project_id=project AND e.environment_id=after_id AND e.version=after_version))) THEN RETURN; END IF;
            RETURN QUERY SELECT jsonb_build_object('page',true),NULL::bytea;
            RETURN QUERY SELECT to_jsonb(e),NULL::bytea FROM ONLY weft.build_environments e
                WHERE e.organization_id=org AND e.project_id=project AND (after_id IS NULL OR e.environment_id>after_id OR e.environment_id=after_id AND e.version<after_version)
                ORDER BY e.environment_id,e.version DESC LIMIT maximum+1;
        ELSIF operation='approvals' THEN
            IF after_version IS NOT NULL THEN RETURN; END IF;
            IF after_id IS NOT NULL THEN
                SELECT a.created_at INTO at_time FROM ONLY weft.build_approvals a WHERE a.organization_id=org AND a.project_id=project
                    AND a.mapping_id=mapping AND a.actor_id=actor AND a.approval_id=after_id;
                IF NOT FOUND THEN RETURN; END IF;
            END IF;
            RETURN QUERY SELECT jsonb_build_object('page',true),NULL::bytea;
            RETURN QUERY SELECT (to_jsonb(a)-'intent_body')||jsonb_build_object('environment_active',e.active),a.intent_body
                FROM ONLY weft.build_approvals a JOIN ONLY weft.build_environments e
                    ON e.organization_id=a.organization_id AND e.project_id=a.project_id AND e.environment_id=a.environment_id AND e.version=a.environment_version
                WHERE a.organization_id=org AND a.project_id=project AND a.mapping_id=mapping AND a.actor_id=actor
                    AND (after_id IS NULL OR (a.created_at,a.approval_id)<(at_time,after_id))
                ORDER BY a.created_at DESC,a.approval_id DESC LIMIT maximum+1;
        ELSE
            IF after_version IS NOT NULL THEN RETURN; END IF;
            IF after_id IS NOT NULL THEN
                SELECT r.created_at INTO at_time FROM ONLY weft.build_runs r JOIN ONLY weft.build_approvals a
                    ON a.organization_id=r.organization_id AND a.build_id=r.build_id
                    WHERE r.organization_id=org AND r.project_id=project AND r.actor_id=actor AND a.mapping_id=mapping AND r.build_id=after_id;
                IF NOT FOUND THEN RETURN; END IF;
            END IF;
            RETURN QUERY SELECT jsonb_build_object('page',true),NULL::bytea;
            RETURN QUERY SELECT jsonb_build_object('organization_id',r.organization_id,'project_id',r.project_id,'build_id',r.build_id,
                    'actor_id',r.actor_id,'approval_id',r.approval_id,'intent_digest',r.intent_digest,'state',r.state,'attempt',r.attempt,
                    'created_at',r.created_at,'outcome',r.outcome,'artifact_digest',r.artifact_digest,'artifact_bytes',r.artifact_bytes),r.intent_body
                FROM ONLY weft.build_runs r JOIN ONLY weft.build_approvals a ON a.organization_id=r.organization_id AND a.build_id=r.build_id
                WHERE r.organization_id=org AND r.project_id=project AND r.actor_id=actor AND a.mapping_id=mapping
                    AND (after_id IS NULL OR (r.created_at,r.build_id)<(at_time,after_id))
                ORDER BY r.created_at DESC,r.build_id DESC LIMIT maximum+1;
        END IF;
    ELSE RAISE EXCEPTION 'Unknown build metadata operation.' USING ERRCODE='22023'; END IF;
END;
$body$;
REVOKE ALL ON FUNCTION weft.read_build_metadata(text,uuid,uuid,uuid,uuid,bigint,integer,uuid,bigint) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_build_metadata_reader;
GRANT EXECUTE ON FUNCTION weft.read_build_metadata(text,uuid,uuid,uuid,uuid,bigint,integer,uuid,bigint) TO weft_build_metadata_reader;
