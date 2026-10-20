-- Build credentials are independent of case, run and catalog credentials.
DO $build_roles$
DECLARE n text; r record;
BEGIN
    FOREACH n IN ARRAY ARRAY['weft_build_reader','weft_build_writer'] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=n;
        IF current_user<>'weft_migrator' OR session_user<>current_user OR NOT FOUND OR NOT r.rolcanlogin
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
            OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE') THEN
            RAISE EXCEPTION 'Build roles must be provisioned independently.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$build_roles$;

CREATE TABLE weft.build_environments (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, environment_id uuid NOT NULL,
    version bigint NOT NULL CHECK(version>0), profile jsonb NOT NULL, active boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,project_id,environment_id,version),
    CHECK(environment_id<>'00000000-0000-0000-0000-000000000000' AND jsonb_typeof(profile)='object')
);
CREATE UNIQUE INDEX build_environment_active ON weft.build_environments(organization_id,project_id,environment_id) WHERE active;

CREATE TABLE weft.build_approvals (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, approval_id uuid NOT NULL,
    build_id uuid NOT NULL, actor_id uuid NOT NULL, mapping_id uuid NOT NULL, revision_id uuid NOT NULL,
    environment_id uuid NOT NULL, environment_version bigint NOT NULL,
    intent_digest text NOT NULL CHECK(intent_digest~'^[0-9a-f]{64}$'), intent_body bytea NOT NULL,
    evidence_id uuid NOT NULL, evidence_digest text NOT NULL CHECK(evidence_digest~'^[0-9a-f]{64}$'),
    native_id uuid NOT NULL, native_digest text NOT NULL CHECK(native_digest~'^[0-9a-f]{64}$'), native_bytes bigint NOT NULL CHECK(native_bytes>0),
    revoked boolean NOT NULL DEFAULT false, created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,approval_id),
    UNIQUE(organization_id,build_id),
    FOREIGN KEY(organization_id,project_id,environment_id,environment_version) REFERENCES weft.build_environments(organization_id,project_id,environment_id,version),
    CHECK(octet_length(intent_body) BETWEEN 1 AND 1048576 AND encode(sha256(intent_body),'hex')=intent_digest)
);

CREATE TABLE weft.build_publications (
    organization_id uuid NOT NULL, build_id uuid NOT NULL, approval_id uuid NOT NULL,
    artifact_digest text NOT NULL CHECK(artifact_digest~'^[0-9a-f]{64}$'), artifact_body bytea NOT NULL,
    state integer NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 2),
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY(organization_id,build_id),
    FOREIGN KEY(organization_id,approval_id) REFERENCES weft.build_approvals(organization_id,approval_id),
    CHECK(octet_length(artifact_body) BETWEEN 1 AND 33554432 AND encode(sha256(artifact_body),'hex')=artifact_digest)
);

ALTER TABLE weft.build_environments ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.build_approvals ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.build_publications ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON weft.build_environments USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.build_approvals USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY organization_scope ON weft.build_publications USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.build_environments,weft.build_approvals,weft.build_publications FROM PUBLIC;

-- Match the fixed UTF-8 JSON / UTF-16 ordinal contract used by BuildIntent, including Unicode names.
CREATE FUNCTION weft.build_json_text(value text, sort_key boolean) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE c text; cp integer; units integer[]; u integer; result text:=''; nonspace boolean:=false;
BEGIN
    IF value IS NULL THEN RETURN CASE WHEN sort_key THEN '' ELSE 'null' END; END IF;
    IF octet_length(value)>256 THEN RAISE EXCEPTION 'Build text limit.' USING ERRCODE='22023'; END IF;
    FOR c IN SELECT regexp_split_to_table(value,'') LOOP
        cp:=ascii(c);
        IF cp<32 OR cp BETWEEN 127 AND 159 THEN RAISE EXCEPTION 'Build text refused.' USING ERRCODE='22023'; END IF;
        IF cp NOT IN (32,160,5760,8232,8233,8239,8287,12288) AND cp NOT BETWEEN 8192 AND 8202 THEN nonspace:=true; END IF;
        units:=CASE WHEN cp>65535 THEN ARRAY[55296+(cp-65536)/1024,56320+(cp-65536)%1024] ELSE ARRAY[cp] END;
        IF sort_key THEN
            FOREACH u IN ARRAY units LOOP result:=result||upper(lpad(to_hex(u),4,'0')); END LOOP;
        ELSIF cp=92 THEN result:=result||chr(92)||chr(92);
        ELSIF cp BETWEEN 32 AND 126 AND cp NOT IN (34,38,39,43,60,62,96) THEN result:=result||c;
        ELSE FOREACH u IN ARRAY units LOOP result:=result||chr(92)||'u'||upper(lpad(to_hex(u),4,'0')); END LOOP;
        END IF;
    END LOOP;
    IF NOT nonspace THEN RAISE EXCEPTION 'Build text is empty.' USING ERRCODE='22023'; END IF;
    RETURN CASE WHEN sort_key THEN result ELSE chr(34)||result||chr(34) END;
END;
$body$;
REVOKE ALL ON FUNCTION weft.build_json_text(text,boolean) FROM PUBLIC;

CREATE FUNCTION weft.build_intent_canonical(j jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE k text; b jsonb; result text:='{'; separator text:=''; boundary_separator text:='';
BEGIN
    FOREACH k IN ARRAY ARRAY['schema','buildId','organizationId','projectId','mappingId','revisionId','nativeDigest','backendId','engineRevision','engineBuild','adapterVersion','profileId','profileVersion','runtime','runtimeVersion','artifactFormat','maximumArtifactBytes','environmentId','environmentVersion','approvalId','regressionEvidenceId'] LOOP
        result:=result||separator||chr(34)||k||chr(34)||':'; separator:=',';
        IF k IN ('profileVersion','maximumArtifactBytes','environmentVersion') THEN result:=result||((j->>k)::bigint)::text;
        ELSE result:=result||weft.build_json_text(j->>k,false); END IF;
    END LOOP;
    result:=result||',"boundaries":[';
    FOR b IN SELECT value FROM jsonb_array_elements(j->'boundaries') ORDER BY
        CASE value->>'kind' WHEN 'primaryInput' THEN 0 WHEN 'namedInput' THEN 1 WHEN 'primaryOutput' THEN 2 WHEN 'namedOutput' THEN 3 ELSE 4 END,
        weft.build_json_text(value->>'name',true) COLLATE "C" LOOP
        result:=result||boundary_separator||'{"kind":'||weft.build_json_text(b->>'kind',false)||',"name":'||weft.build_json_text(b->>'name',false)||',"format":'||weft.build_json_text(b->>'format',false)||'}';
        boundary_separator:=',';
    END LOOP;
    RETURN result||']}';
END;
$body$;
REVOKE ALL ON FUNCTION weft.build_intent_canonical(jsonb) FROM PUBLIC;

CREATE FUNCTION weft.build_authorized(org uuid, project uuid, actor uuid, roles text[]) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT org=NULLIF(current_setting('weft.organization_id',true),'')::uuid
        AND actor=NULLIF(current_setting('weft.build_principal_id',true),'')::uuid
        AND EXISTS(SELECT 1 FROM ONLY weft.principals p
            JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=org AND o.active
            JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=org AND m.project_id=project AND m.active
            JOIN ONLY weft.projects pr ON pr.organization_id=org AND pr.id=project
            WHERE p.id=actor AND p.active AND m.role=ANY(roles))
$body$;
REVOKE ALL ON FUNCTION weft.build_authorized(uuid,uuid,uuid,text[]) FROM PUBLIC;

-- Recompute exact output identities from the immutable baseline and actual result bytes.
CREATE FUNCTION weft.build_evidence_passes(org uuid, run_key uuid, actor uuid, native_sha text, backend text, engine text, build text, adapter text) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r record; header jsonb; result jsonb; expected jsonb; actual jsonb; raw bytea; h bigint; seen text[]:=ARRAY[]::text[]; k text;
BEGIN
    SELECT x.*,c.envelope INTO r FROM ONLY weft.regression_runs x JOIN ONLY weft.regression_case_versions c
        ON c.organization_id=x.organization_id AND c.project_id=x.project_id AND c.mapping_id=x.mapping_id
        AND c.case_id=x.case_id AND c.version=x.version AND c.content_digest=x.case_digest
        WHERE x.organization_id=org AND x.run_id=run_key AND x.submitter_id=actor AND x.state='completed' AND x.native_digest=native_sha;
    IF NOT FOUND OR octet_length(r.envelope)<4 THEN RETURN false; END IF;
    h:=get_byte(r.envelope,0)::bigint*16777216+get_byte(r.envelope,1)*65536+get_byte(r.envelope,2)*256+get_byte(r.envelope,3);
    IF h NOT BETWEEN 1 AND 262144 OR h>octet_length(r.envelope)-4 THEN RETURN false; END IF;
    header:=convert_from(substring(r.envelope FROM 5 FOR h::integer),'UTF8')::jsonb;
    result:=convert_from(r.result_body,'UTF8')::jsonb;
    IF header->>'comparisonPolicy' IS DISTINCT FROM 'weft.regression.exact-bytes.v1' OR header->'runtime'->>'backendId' IS DISTINCT FROM backend
        OR header->'runtime'->>'engineRevision' IS DISTINCT FROM engine OR header->'runtime'->>'backendBuild' IS DISTINCT FROM build
        OR header->'runtime'->>'adapterBuild' IS DISTINCT FROM adapter
        OR result->>'schema' IS DISTINCT FROM 'weft.regression-run-result.v1' OR result->>'runId' IS DISTINCT FROM run_key::text
        OR result->>'caseDigest' IS DISTINCT FROM r.case_digest OR result->>'outcome' IS DISTINCT FROM '0' OR result->>'previewStatus' IS DISTINCT FROM '0'
        OR jsonb_typeof(header->'expected') IS DISTINCT FROM 'array' OR jsonb_typeof(result->'outputs') IS DISTINCT FROM 'array'
        OR jsonb_array_length(header->'expected') NOT BETWEEN 1 AND 64
        OR jsonb_array_length(header->'expected')<>jsonb_array_length(result->'outputs') THEN RETURN false; END IF;
    FOR expected IN SELECT value FROM jsonb_array_elements(header->'expected') LOOP
        k:=coalesce(expected->>'name',chr(1));
        IF k=ANY(seen) THEN RETURN false; END IF;
        seen:=array_append(seen,k);
        SELECT value INTO actual FROM jsonb_array_elements(result->'outputs') WHERE value->'name'=expected->'name';
        IF NOT FOUND OR (SELECT count(*) FROM jsonb_array_elements(result->'outputs') WHERE value->'name'=expected->'name')<>1 THEN RETURN false; END IF;
        IF jsonb_typeof(actual->'content') IS DISTINCT FROM 'string' OR (actual->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE THEN RETURN false; END IF;
        raw:=decode(translate(actual->>'content','-_','+/')||repeat('=',(4-length(actual->>'content')%4)%4),'base64');
        IF raw IS NULL OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256' THEN RETURN false; END IF;
    END LOOP;
    RETURN true;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
REVOKE ALL ON FUNCTION weft.build_evidence_passes(uuid,uuid,uuid,text,text,text,text,text) FROM PUBLIC;

CREATE FUNCTION weft.build_approval_active(org uuid, key uuid, actor uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY weft.build_approvals a JOIN ONLY weft.build_environments e
        ON e.organization_id=a.organization_id AND e.project_id=a.project_id AND e.environment_id=a.environment_id AND e.version=a.environment_version AND e.active
        JOIN ONLY weft.regression_runs r ON r.organization_id=a.organization_id AND r.run_id=a.evidence_id AND r.result_digest=a.evidence_digest AND r.state='completed'
        JOIN ONLY weft.mapping_revisions m ON m.organization_id=a.organization_id AND m.project_id=a.project_id AND m.mapping_id=a.mapping_id
            AND m.id=a.revision_id AND m.native_object_id=a.native_id AND m.native_digest=a.native_digest AND m.native_byte_count=a.native_bytes
        WHERE a.organization_id=org AND a.approval_id=key AND a.actor_id=actor AND NOT a.revoked
            AND weft.build_authorized(org,a.project_id,actor,ARRAY['publisher','administrator'])
            AND weft.build_evidence_passes(org,a.evidence_id,actor,a.native_digest,m.backend_id,
                convert_from(a.intent_body,'UTF8')::jsonb->>'engineRevision',convert_from(a.intent_body,'UTF8')::jsonb->>'engineBuild',convert_from(a.intent_body,'UTF8')::jsonb->>'adapterVersion'))
$body$;
REVOKE ALL ON FUNCTION weft.build_approval_active(uuid,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION weft.guard_build_record() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF current_user<>'weft_migrator' OR session_user<>'weft_build_writer' OR TG_OP IN ('DELETE','TRUNCATE') THEN
        RAISE EXCEPTION 'Build mutation refused.' USING ERRCODE='42501';
    END IF;
    IF TG_OP='INSERT' THEN
        IF NEW.created_at<>statement_timestamp() THEN RAISE EXCEPTION 'Build audit refused.' USING ERRCODE='23514'; END IF;
    ELSIF TG_TABLE_NAME='build_environments' THEN
        IF (to_jsonb(NEW)-'active')<>(to_jsonb(OLD)-'active') OR NOT OLD.active OR NEW.active THEN RAISE EXCEPTION 'Environment version is immutable.' USING ERRCODE='23514'; END IF;
    ELSIF TG_TABLE_NAME='build_approvals' THEN
        IF (to_jsonb(NEW)-'revoked')<>(to_jsonb(OLD)-'revoked') OR OLD.revoked OR NOT NEW.revoked THEN RAISE EXCEPTION 'Build approval is immutable.' USING ERRCODE='23514'; END IF;
    ELSE
        IF (to_jsonb(NEW)-'state')<>(to_jsonb(OLD)-'state') OR NEW.state<>OLD.state+1 THEN RAISE EXCEPTION 'Build artifact is immutable.' USING ERRCODE='23514'; END IF;
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION weft.guard_build_record() FROM PUBLIC;
CREATE TRIGGER build_environment_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.build_environments FOR EACH ROW EXECUTE FUNCTION weft.guard_build_record();
CREATE TRIGGER build_environment_truncate_guard BEFORE TRUNCATE ON weft.build_environments FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_build_record();
CREATE TRIGGER build_approval_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.build_approvals FOR EACH ROW EXECUTE FUNCTION weft.guard_build_record();
CREATE TRIGGER build_approval_truncate_guard BEFORE TRUNCATE ON weft.build_approvals FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_build_record();
CREATE TRIGGER build_publication_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.build_publications FOR EACH ROW EXECUTE FUNCTION weft.guard_build_record();
CREATE TRIGGER build_publication_truncate_guard BEFORE TRUNCATE ON weft.build_publications FOR EACH STATEMENT EXECUTE FUNCTION weft.guard_build_record();

CREATE FUNCTION weft.read_build_record(operation text, org uuid, project uuid, key uuid, revision bigint)
RETURNS TABLE(document jsonb, intent bytea, artifact bytea)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid;
BEGIN
    IF session_user NOT IN ('weft_build_reader','weft_build_writer') OR current_user<>'weft_migrator'
        OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN; END IF;
    actor:=NULLIF(current_setting('weft.build_principal_id',true),'')::uuid;
    IF operation='environment' THEN
        IF NOT weft.build_authorized(org,project,actor,ARRAY['publisher','administrator']) THEN RETURN; END IF;
        RETURN QUERY SELECT to_jsonb(e),NULL::bytea,NULL::bytea FROM ONLY weft.build_environments e
            WHERE e.organization_id=org AND e.project_id=project AND e.environment_id=key AND e.version=revision AND e.active;
    ELSIF operation='approval' THEN
        IF NOT weft.build_approval_active(org,key,actor) THEN RETURN; END IF;
        RETURN QUERY SELECT to_jsonb(a)-'intent_body',a.intent_body,NULL::bytea FROM ONLY weft.build_approvals a WHERE a.organization_id=org AND a.approval_id=key;
    ELSIF operation IN ('staged','published','object') THEN
        RETURN QUERY SELECT jsonb_build_object('approval',to_jsonb(a)-'intent_body','artifactDigest',p.artifact_digest,'state',p.state),a.intent_body,p.artifact_body
            FROM ONLY weft.build_publications p JOIN ONLY weft.build_approvals a ON a.organization_id=p.organization_id AND a.approval_id=p.approval_id
            WHERE p.organization_id=org AND p.build_id=key AND (operation='staged' OR operation='published' AND p.state=2 OR operation='object' AND p.state>=1)
                AND weft.build_approval_active(org,a.approval_id,actor);
    ELSE RAISE EXCEPTION 'Unknown build read.' USING ERRCODE='22023'; END IF;
END;
$body$;
REVOKE ALL ON FUNCTION weft.read_build_record(text,uuid,uuid,uuid,bigint) FROM PUBLIC;

CREATE FUNCTION weft.write_build_record(operation text, org uuid, project uuid, key uuid, revision bigint, info jsonb, intent bytea, artifact bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; j jsonb; n text; a weft.build_approvals%ROWTYPE; p weft.build_publications%ROWTYPE; e weft.build_environments%ROWTYPE; m weft.mapping_revisions%ROWTYPE;
BEGIN
    IF session_user<>'weft_build_writer' OR current_user<>'weft_migrator' THEN RAISE EXCEPTION 'Build writer required.' USING ERRCODE='42501'; END IF;
    IF org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid THEN RETURN 3; END IF;
    actor:=NULLIF(current_setting('weft.build_principal_id',true),'')::uuid;
    PERFORM 1 FROM ONLY weft.principals WHERE id=actor FOR UPDATE;
    PERFORM 1 FROM ONLY weft.organizations WHERE id=org FOR SHARE;
    PERFORM 1 FROM ONLY weft.organization_memberships WHERE organization_id=org AND principal_id=actor FOR SHARE;
    -- The project lock also serializes environment versions, approvals and staging in that project.
    PERFORM 1 FROM ONLY weft.projects WHERE organization_id=org AND id=project FOR UPDATE;
    PERFORM 1 FROM ONLY weft.project_memberships WHERE organization_id=org AND project_id=project AND principal_id=actor FOR SHARE;
    IF weft.build_authorized(org,project,actor,ARRAY['publisher','administrator']) IS NOT TRUE THEN RETURN 3; END IF;
    IF operation='environment' THEN
        IF weft.build_authorized(org,project,actor,ARRAY['administrator']) IS NOT TRUE OR revision IS NULL OR revision<1
            OR jsonb_typeof(info) IS DISTINCT FROM 'object' OR info->>'maximumArtifactBytes' IS NULL
            OR (info->>'maximumArtifactBytes')::bigint NOT BETWEEN 1 AND 33554432 THEN RETURN 3; END IF;
        IF (SELECT count(*) FROM jsonb_object_keys(info))<>6 OR jsonb_typeof(info->'version') IS DISTINCT FROM 'number'
            OR jsonb_typeof(info->'maximumArtifactBytes') IS DISTINCT FROM 'number'
            OR (info->>'version')::integer<1 THEN RETURN 3; END IF;
        FOREACH n IN ARRAY ARRAY['id','runtime','runtimeVersion','artifactFormat'] LOOP
            IF jsonb_typeof(info->n) IS DISTINCT FROM 'string' OR octet_length(info->>n) NOT BETWEEN 1 AND 128
                OR info->>n~'[[:cntrl:]]' THEN RETURN 3; END IF;
            PERFORM weft.build_json_text(info->>n,false);
        END LOOP;
        SELECT * INTO e FROM ONLY weft.build_environments WHERE organization_id=org AND project_id=project AND environment_id=key AND version=revision;
        IF FOUND THEN IF e.profile=info THEN RETURN 1; ELSE RETURN 2; END IF; END IF;
        IF revision<>coalesce((SELECT max(version) FROM ONLY weft.build_environments WHERE organization_id=org AND project_id=project AND environment_id=key),0)+1 THEN RETURN 2; END IF;
        UPDATE weft.build_environments SET active=false WHERE organization_id=org AND project_id=project AND environment_id=key AND active;
        INSERT INTO weft.build_environments(organization_id,project_id,environment_id,version,profile,created_by) VALUES(org,project,key,revision,info,actor);
        RETURN 0;
    ELSIF operation='approve' THEN
        IF intent IS NULL OR octet_length(intent) NOT BETWEEN 1 AND 1048576 THEN RETURN 3; END IF;
        j:=convert_from(intent,'UTF8')::jsonb;
        IF jsonb_typeof(j) IS DISTINCT FROM 'object' THEN RETURN 3; END IF;
        IF (SELECT count(*) FROM jsonb_object_keys(j))<>22 THEN RETURN 3; END IF;
        FOREACH n IN ARRAY ARRAY['schema','buildId','organizationId','projectId','mappingId','revisionId','nativeDigest','backendId','engineRevision','engineBuild','adapterVersion','profileId','runtime','runtimeVersion','artifactFormat','environmentId','approvalId','regressionEvidenceId'] LOOP
            IF jsonb_typeof(j->n) IS DISTINCT FROM 'string' OR length(j->>n)=0 THEN RETURN 3; END IF;
        END LOOP;
        FOREACH n IN ARRAY ARRAY['backendId','engineRevision','engineBuild','adapterVersion','profileId','runtime','runtimeVersion','artifactFormat'] LOOP
            IF octet_length(j->>n)>(CASE WHEN n IN ('engineRevision','engineBuild') THEN 256 ELSE 128 END) THEN RETURN 3; END IF;
        END LOOP;
        IF jsonb_typeof(j->'boundaries') IS DISTINCT FROM 'array' OR jsonb_typeof(j->'profileVersion') IS DISTINCT FROM 'number'
            OR jsonb_typeof(j->'environmentVersion') IS DISTINCT FROM 'number' OR jsonb_typeof(j->'maximumArtifactBytes') IS DISTINCT FROM 'number' THEN RETURN 3; END IF;
        FOREACH n IN ARRAY ARRAY['buildId','organizationId','projectId','mappingId','revisionId','environmentId','approvalId','regressionEvidenceId'] LOOP
            IF j->>n='00000000-0000-0000-0000-000000000000' OR ((j->>n)::uuid)::text IS DISTINCT FROM j->>n THEN RETURN 3; END IF;
        END LOOP;
        IF jsonb_array_length(j->'boundaries') NOT BETWEEN 1 AND 128 OR (j->>'profileVersion')::integer<1 OR (j->>'environmentVersion')::bigint<1 THEN RETURN 3; END IF;
        IF EXISTS(SELECT 1 FROM jsonb_array_elements(j->'boundaries') b WHERE jsonb_typeof(b) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(b))<>3 OR b->>'kind' NOT IN ('primaryInput','namedInput','primaryOutput','namedOutput')
            OR jsonb_typeof(b->'kind') IS DISTINCT FROM 'string' OR jsonb_typeof(b->'format') IS DISTINCT FROM 'string'
            OR octet_length(b->>'format') NOT BETWEEN 1 AND 128
            OR ((b->>'kind') IN ('primaryInput','primaryOutput') AND b->'name' IS DISTINCT FROM 'null'::jsonb)
            OR ((b->>'kind') IN ('namedInput','namedOutput') AND (jsonb_typeof(b->'name') IS DISTINCT FROM 'string' OR octet_length(b->>'name') NOT BETWEEN 1 AND 256)))
            OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'boundaries') b GROUP BY b->>'kind',b->'name' HAVING count(*)>1) THEN RETURN 3; END IF;
        IF j->>'schema' IS DISTINCT FROM 'weft.build-intent.v1' OR j->>'organizationId' IS DISTINCT FROM org::text OR j->>'projectId' IS DISTINCT FROM project::text OR j->>'approvalId' IS DISTINCT FROM key::text THEN RETURN 3; END IF;
        IF convert_to(weft.build_intent_canonical(j),'UTF8') IS DISTINCT FROM intent THEN RETURN 3; END IF;
        SELECT * INTO e FROM ONLY weft.build_environments WHERE organization_id=org AND project_id=project AND environment_id=(j->>'environmentId')::uuid AND version=(j->>'environmentVersion')::bigint AND active;
        IF NOT FOUND OR e.profile<>jsonb_build_object('id',j->>'profileId','version',(j->>'profileVersion')::integer,'runtime',j->>'runtime','runtimeVersion',j->>'runtimeVersion','artifactFormat',j->>'artifactFormat','maximumArtifactBytes',(j->>'maximumArtifactBytes')::bigint) THEN RETURN 3; END IF;
        SELECT * INTO m FROM ONLY weft.mapping_revisions WHERE organization_id=org AND project_id=project AND mapping_id=(j->>'mappingId')::uuid AND id=(j->>'revisionId')::uuid AND native_digest=j->>'nativeDigest' AND backend_id=j->>'backendId';
        IF NOT FOUND OR weft.build_evidence_passes(org,(j->>'regressionEvidenceId')::uuid,actor,m.native_digest,m.backend_id,j->>'engineRevision',j->>'engineBuild',j->>'adapterVersion') IS NOT TRUE
            OR NOT EXISTS(SELECT 1 FROM ONLY weft.regression_runs WHERE organization_id=org AND run_id=(j->>'regressionEvidenceId')::uuid AND result_digest=info->>'evidenceDigest'
                AND project_id=project AND mapping_id=m.mapping_id AND revision_id=m.id) THEN RETURN 3; END IF;
        SELECT * INTO a FROM ONLY weft.build_approvals WHERE organization_id=org AND (approval_id=key OR build_id=(j->>'buildId')::uuid);
        IF FOUND THEN IF a.actor_id=actor AND a.intent_body=intent AND NOT a.revoked THEN RETURN 1; ELSE RETURN 2; END IF; END IF;
        INSERT INTO weft.build_approvals(organization_id,project_id,approval_id,build_id,actor_id,mapping_id,revision_id,environment_id,environment_version,intent_digest,intent_body,evidence_id,evidence_digest,native_id,native_digest,native_bytes)
            VALUES(org,project,key,(j->>'buildId')::uuid,actor,m.mapping_id,m.id,e.environment_id,e.version,encode(sha256(intent),'hex'),intent,(j->>'regressionEvidenceId')::uuid,info->>'evidenceDigest',m.native_object_id,m.native_digest,m.native_byte_count)
            ON CONFLICT DO NOTHING;
        IF FOUND THEN RETURN 0; END IF;
        RETURN 2;
    ELSIF operation='revoke' THEN
        SELECT * INTO a FROM ONLY weft.build_approvals WHERE organization_id=org AND project_id=project AND approval_id=key FOR UPDATE;
        IF NOT FOUND OR (a.actor_id<>actor AND NOT weft.build_authorized(org,project,actor,ARRAY['administrator'])) THEN RETURN 3; END IF;
        IF NOT a.revoked THEN UPDATE weft.build_approvals SET revoked=true WHERE organization_id=org AND approval_id=key; END IF;
        RETURN 0;
    ELSIF operation IN ('stage','object','publish') THEN
        SELECT * INTO a FROM ONLY weft.build_approvals WHERE organization_id=org AND project_id=project AND build_id=key FOR SHARE;
        IF NOT FOUND OR a.intent_body IS DISTINCT FROM intent OR weft.build_approval_active(org,a.approval_id,actor) IS NOT TRUE THEN RETURN 3; END IF;
        SELECT * INTO p FROM ONLY weft.build_publications WHERE organization_id=org AND build_id=key FOR UPDATE;
        IF operation='stage' THEN
            IF FOUND THEN RETURN 1; END IF;
            IF artifact IS NULL OR octet_length(artifact) NOT BETWEEN 1 AND least(33554432,(convert_from(intent,'UTF8')::jsonb->>'maximumArtifactBytes')::bigint)
                OR encode(sha256(artifact),'hex') IS DISTINCT FROM info->>'artifactDigest' THEN RETURN 3; END IF;
            IF (SELECT count(*) FROM ONLY weft.build_publications b JOIN ONLY weft.build_approvals x ON x.organization_id=b.organization_id AND x.approval_id=b.approval_id
                JOIN ONLY weft.build_environments v ON v.organization_id=x.organization_id AND v.project_id=x.project_id AND v.environment_id=x.environment_id AND v.version=x.environment_version
                WHERE b.organization_id=org AND x.actor_id=actor AND b.state<2 AND NOT x.revoked AND v.active)>=32 THEN RETURN 3; END IF;
            INSERT INTO weft.build_publications(organization_id,build_id,approval_id,artifact_digest,artifact_body) VALUES(org,key,a.approval_id,info->>'artifactDigest',artifact);
            RETURN 0;
        END IF;
        IF NOT FOUND OR p.artifact_digest IS DISTINCT FROM info->>'artifactDigest' THEN RETURN 3; END IF;
        IF operation='object' THEN
            IF p.artifact_body IS DISTINCT FROM artifact THEN RETURN 2; END IF;
            IF p.state=0 THEN UPDATE weft.build_publications SET state=1 WHERE organization_id=org AND build_id=key; RETURN 0; END IF;
            RETURN 1;
        END IF;
        IF p.state=0 THEN RETURN 3; END IF;
        IF p.state=2 THEN RETURN 1; END IF;
        UPDATE weft.build_publications SET state=2 WHERE organization_id=org AND build_id=key;
        RETURN 0;
    ELSE RAISE EXCEPTION 'Unknown build write.' USING ERRCODE='22023'; END IF;
EXCEPTION WHEN data_exception THEN RETURN 3;
END;
$body$;
REVOKE ALL ON FUNCTION weft.write_build_record(text,uuid,uuid,uuid,bigint,jsonb,bytea,bytea) FROM PUBLIC;
GRANT USAGE ON SCHEMA weft TO weft_build_reader,weft_build_writer;
GRANT EXECUTE ON FUNCTION weft.read_build_record(text,uuid,uuid,uuid,bigint) TO weft_build_reader,weft_build_writer;
GRANT EXECUTE ON FUNCTION weft.write_build_record(text,uuid,uuid,uuid,bigint,jsonb,bytea,bytea) TO weft_build_writer;
