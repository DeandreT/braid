-- Independent trusted execution credentials. These roles have no direct table access.
DO $roles$
DECLARE n text; r record;
BEGIN
    FOREACH n IN ARRAY ARRAY['braid_qualification_reader','braid_qualification_writer'] LOOP
        SELECT * INTO r FROM pg_catalog.pg_roles WHERE rolname=n;
        IF current_user<>'braid_migrator' OR session_user<>current_user OR NOT FOUND OR NOT r.rolcanlogin
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
            OR pg_catalog.has_schema_privilege(r.oid,'braid','USAGE') THEN
            RAISE EXCEPTION 'Independent qualification roles required.' USING ERRCODE='42501';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE braid.build_qualifications (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, mapping_id uuid NOT NULL,
    qualification_id uuid NOT NULL CHECK(qualification_id<>'00000000-0000-0000-0000-000000000000'), actor_id uuid NOT NULL,
    build_id uuid NOT NULL, evidence_id uuid NOT NULL,
    proof_body bytea NOT NULL, proof_digest text NOT NULL,
    result_body bytea NOT NULL, result_digest text NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT statement_timestamp() CHECK(isfinite(created_at)),
    PRIMARY KEY(organization_id,qualification_id),
    FOREIGN KEY(organization_id,build_id) REFERENCES braid.build_runs(organization_id,build_id),
    FOREIGN KEY(organization_id,evidence_id) REFERENCES braid.regression_runs(organization_id,run_id),
    CHECK(octet_length(proof_body) BETWEEN 1 AND 16384 AND encode(sha256(proof_body),'hex')=proof_digest),
    CHECK(octet_length(result_body) BETWEEN 1 AND 12582912 AND encode(sha256(result_body),'hex')=result_digest)
);
ALTER TABLE braid.build_qualifications ENABLE ROW LEVEL SECURITY;
CREATE POLICY organization_scope ON braid.build_qualifications USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.build_qualifications FROM PUBLIC;

CREATE FUNCTION braid.qualification_canonical(j jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE k text; result text:='{'; separator text:='';
BEGIN
    FOREACH k IN ARRAY ARRAY['schema','qualificationId','actorId','buildId','intentDigest','artifactDigest','artifactBytes','runtimeImage','evidenceId','evidenceDigest','caseId','caseVersion','caseDigest','replayDigest','policy','resultDigest','resultBytes'] LOOP
        result:=result||separator||'"'||k||'":'; separator:=',';
        IF k IN ('artifactBytes','caseVersion','resultBytes') THEN result:=result||((j->>k)::bigint)::text;
        ELSE result:=result||braid.build_json_text(j->>k,false); END IF;
    END LOOP;
    RETURN result||'}';
END;
$body$;
REVOKE ALL ON FUNCTION braid.qualification_canonical(jsonb) FROM PUBLIC;

-- Recompute both original and compiled exact-byte comparisons; no pass flag is stored.
CREATE FUNCTION braid.qualification_valid(org uuid, project uuid, mapping uuid, key uuid, actor uuid, proof bytea, completion bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE q jsonb; b record; r record; h bigint; header_raw json; header jsonb; result jsonb; item record; expected jsonb;
    replay text; canonical text; separator text:=''; raw bytea; total bigint:=0; boundary_count integer;
BEGIN
    IF octet_length(proof) NOT BETWEEN 1 AND 16384 OR octet_length(completion) NOT BETWEEN 1 AND 12582912 THEN RETURN false; END IF;
    q:=convert_from(proof,'UTF8')::jsonb;
    IF convert_to(braid.qualification_canonical(q),'UTF8') IS DISTINCT FROM proof
        OR q->>'schema' IS DISTINCT FROM 'braid.build-qualification.v1' OR q->>'policy' IS DISTINCT FROM 'braid.regression.exact-bytes.v1'
        OR q->>'qualificationId' IS DISTINCT FROM key::text OR q->>'actorId' IS DISTINCT FROM actor::text
        OR (q->>'runtimeImage'~'^sha256:[0-9a-f]{64}$') IS NOT TRUE
        OR q->>'resultDigest' IS DISTINCT FROM encode(sha256(completion),'hex')
        OR (q->>'resultBytes')::bigint IS DISTINCT FROM octet_length(completion) THEN RETURN false; END IF;
    SELECT x.*,a.mapping_id,a.revision_id,a.evidence_id,a.evidence_digest,a.native_digest,p.state publication_state,
        p.artifact_digest publication_digest,octet_length(p.artifact_body) publication_bytes
        INTO b FROM ONLY braid.build_runs x
        JOIN ONLY braid.build_approvals a ON a.organization_id=x.organization_id AND a.build_id=x.build_id AND a.approval_id=x.approval_id
        JOIN ONLY braid.build_publications p ON p.organization_id=x.organization_id AND p.build_id=x.build_id AND p.approval_id=x.approval_id
        WHERE x.organization_id=org AND x.project_id=project AND x.build_id=(q->>'buildId')::uuid
            AND x.actor_id=actor AND a.actor_id=actor AND a.mapping_id=mapping AND a.intent_body=x.intent_body;
    IF NOT FOUND OR b.state<>'succeeded' OR b.outcome NOT IN (0,1) OR b.attempt NOT BETWEEN 1 AND 3
        OR b.publication_state<>2 OR b.publication_digest IS DISTINCT FROM b.artifact_digest OR b.publication_bytes IS DISTINCT FROM b.artifact_bytes
        OR q->>'intentDigest' IS DISTINCT FROM b.intent_digest OR q->>'artifactDigest' IS DISTINCT FROM b.artifact_digest
        OR (q->>'artifactBytes')::bigint IS DISTINCT FROM b.artifact_bytes OR q->>'evidenceId' IS DISTINCT FROM b.evidence_id::text
        OR q->>'evidenceDigest' IS DISTINCT FROM b.evidence_digest THEN RETURN false; END IF;
    SELECT x.*,c.envelope,c.creator_id case_owner INTO r FROM ONLY braid.regression_runs x
        JOIN ONLY braid.regression_case_versions c ON c.organization_id=x.organization_id AND c.project_id=x.project_id
            AND c.mapping_id=x.mapping_id AND c.case_id=x.case_id AND c.version=x.version AND c.content_digest=x.case_digest
        WHERE x.organization_id=org AND x.project_id=project AND x.mapping_id=mapping AND x.run_id=b.evidence_id
            AND x.submitter_id=actor AND x.state='completed' AND x.revision_id=b.revision_id AND x.native_digest=b.native_digest;
    IF NOT FOUND OR r.case_owner<>actor OR r.attempt NOT BETWEEN 1 AND 3 OR r.result_digest IS DISTINCT FROM b.evidence_digest
        OR q->>'caseId' IS DISTINCT FROM r.case_id::text OR (q->>'caseVersion')::bigint IS DISTINCT FROM r.version
        OR q->>'caseDigest' IS DISTINCT FROM r.case_digest OR octet_length(r.envelope)<4 THEN RETURN false; END IF;
    h:=get_byte(r.envelope,0)::bigint*16777216+get_byte(r.envelope,1)*65536+get_byte(r.envelope,2)*256+get_byte(r.envelope,3);
    IF h NOT BETWEEN 1 AND 262144 OR h>octet_length(r.envelope)-4 THEN RETURN false; END IF;
    header_raw:=convert_from(substring(r.envelope FROM 5 FOR h::integer),'UTF8')::json; header:=header_raw::jsonb;
    IF header->'target' IS DISTINCT FROM '{"kind":"all","name":null}'::jsonb
        OR header->'runtime'->>'profile' IS DISTINCT FROM 'braid.ferrule-json-preview.v1'
        OR NOT braid.build_evidence_passes(org,b.evidence_id,actor,b.native_digest,
            convert_from(b.intent_body,'UTF8')::jsonb->>'backendId',convert_from(b.intent_body,'UTF8')::jsonb->>'engineRevision',
            convert_from(b.intent_body,'UTF8')::jsonb->>'engineBuild',convert_from(b.intent_body,'UTF8')::jsonb->>'adapterVersion') THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(convert_from(b.intent_body,'UTF8')::jsonb->'boundaries') e
        WHERE e->>'kind' IN ('primaryOutput','namedOutput');
    IF boundary_count<>jsonb_array_length(header->'expected') OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(convert_from(b.intent_body,'UTF8')::jsonb->'boundaries') e
        WHERE e->>'kind' IN ('primaryOutput','namedOutput') AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'expected') x WHERE x->'name'=e->'name')) THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(convert_from(b.intent_body,'UTF8')::jsonb->'boundaries') e
        WHERE e->>'kind'='namedInput';
    IF boundary_count<>jsonb_array_length(header->'inputs')-1 OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(convert_from(b.intent_body,'UTF8')::jsonb->'boundaries') e
        WHERE e->>'kind'='namedInput' AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'inputs') x WHERE x->'name'=e->'name')) THEN RETURN false; END IF;
    replay:='{"target":"all","targetName":null,"currentDateTime":'||(header_raw->'currentDateTime')::text||',"limits":'||(header_raw->'limits')::text
        ||',"inputs":'||(header_raw->'inputs')::text||',"parameters":'||(header_raw->'parameters')::text||'}';
    IF q->>'replayDigest' IS DISTINCT FROM encode(sha256(convert_to(replay,'UTF8')),'hex') THEN RETURN false; END IF;
    result:=convert_from(completion,'UTF8')::jsonb;
    IF result->>'schema' IS DISTINCT FROM 'braid.regression-run-result.v1' OR result->>'runId' IS DISTINCT FROM key::text
        OR result->>'caseDigest' IS DISTINCT FROM r.case_digest OR result->>'outcome' IS DISTINCT FROM '0' OR result->>'previewStatus' IS DISTINCT FROM '0'
        OR jsonb_typeof(result->'outputs') IS DISTINCT FROM 'array' OR jsonb_array_length(result->'outputs')<>jsonb_array_length(header->'expected')
        OR jsonb_array_length(result->'outputs') NOT BETWEEN 1 AND (header->'limits'->>'maximumArtifacts')::integer THEN RETURN false; END IF;
    canonical:='{"schema":"braid.regression-run-result.v1","runId":"'||key::text||'","caseDigest":"'||r.case_digest||'","outcome":0,"previewStatus":0,"outputs":[';
    FOR item IN SELECT value,ordinality FROM jsonb_array_elements(result->'outputs') WITH ORDINALITY LOOP
        expected:=header->'expected'->(item.ordinality::integer-1);
        IF item.value->'name' IS DISTINCT FROM expected->'name' OR jsonb_typeof(item.value->'content') IS DISTINCT FROM 'string'
            OR (item.value->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE THEN RETURN false; END IF;
        raw:=decode(translate(item.value->>'content','-_','+/')||repeat('=',(4-length(item.value->>'content')%4)%4),'base64');
        total:=total+octet_length(raw);
        IF total>(header->'limits'->>'maximumOutputBytes')::bigint OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer
            OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256'
            OR translate(rtrim(replace(encode(raw,'base64'),chr(10),''),'='),'+/','-_') IS DISTINCT FROM item.value->>'content' THEN RETURN false; END IF;
        canonical:=canonical||separator||'{"name":'||braid.build_json_text(item.value->>'name',false)||',"content":"'||(item.value->>'content')||'"}'; separator:=',';
    END LOOP;
    RETURN convert_to(canonical||']}', 'UTF8')=completion;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
REVOKE ALL ON FUNCTION braid.qualification_valid(uuid,uuid,uuid,uuid,uuid,bytea,bytea) FROM PUBLIC;

CREATE FUNCTION braid.guard_qualification() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>'braid_qualification_writer' OR TG_OP<>'INSERT' THEN
        RAISE EXCEPTION 'Qualification evidence is immutable.' USING ERRCODE='42501';
    END IF;
    IF NEW.created_at<>statement_timestamp() OR NOT braid.build_approval_active(NEW.organization_id,
        (SELECT approval_id FROM ONLY braid.build_runs WHERE organization_id=NEW.organization_id AND build_id=NEW.build_id),NEW.actor_id)
        OR NOT braid.qualification_valid(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.qualification_id,NEW.actor_id,NEW.proof_body,NEW.result_body) THEN
        RAISE EXCEPTION 'Qualification evidence refused.' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_qualification() FROM PUBLIC;
CREATE TRIGGER qualification_immutable BEFORE INSERT OR UPDATE OR DELETE ON braid.build_qualifications FOR EACH ROW EXECUTE FUNCTION braid.guard_qualification();
CREATE TRIGGER qualification_no_truncate BEFORE TRUNCATE ON braid.build_qualifications FOR EACH STATEMENT EXECUTE FUNCTION braid.guard_qualification();

CREATE FUNCTION braid.write_qualification(org uuid, project uuid, mapping uuid, key uuid, proof bytea, completion bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid:=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid; q jsonb; old braid.build_qualifications%ROWTYPE; a braid.build_approvals%ROWTYPE;
BEGIN
    IF session_user<>'braid_qualification_writer' OR key IS NULL OR key='00000000-0000-0000-0000-000000000000'
        OR octet_length(proof) NOT BETWEEN 1 AND 16384 OR octet_length(completion) NOT BETWEEN 1 AND 12582912
        OR NOT braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) THEN RETURN 3; END IF;
    q:=convert_from(proof,'UTF8')::jsonb;
    a:=braid.lock_build_run_authority(org,(q->>'buildId')::uuid);
    IF a.actor_id IS DISTINCT FROM actor OR a.project_id IS DISTINCT FROM project OR a.mapping_id IS DISTINCT FROM mapping
        OR NOT braid.build_authorized(org,project,actor,ARRAY['publisher','administrator']) THEN RETURN 3; END IF;
    SELECT * INTO old FROM ONLY braid.build_qualifications WHERE organization_id=org AND qualification_id=key;
    IF FOUND THEN
        IF old.actor_id<>actor OR old.project_id<>project OR old.mapping_id<>mapping THEN RETURN 3; END IF;
        RETURN CASE WHEN old.proof_body=proof AND old.result_body=completion THEN 1 ELSE 2 END;
    END IF;
    IF NOT braid.build_approval_active(org,a.approval_id,actor) OR NOT braid.qualification_valid(org,project,mapping,key,actor,proof,completion) THEN RETURN 3; END IF;
    INSERT INTO braid.build_qualifications(organization_id,project_id,mapping_id,qualification_id,actor_id,build_id,evidence_id,proof_body,proof_digest,result_body,result_digest)
        VALUES(org,project,mapping,key,actor,a.build_id,a.evidence_id,proof,encode(sha256(proof),'hex'),completion,encode(sha256(completion),'hex'))
        ON CONFLICT(organization_id,qualification_id) DO NOTHING;
    IF FOUND THEN RETURN 0; END IF;
    SELECT * INTO old FROM ONLY braid.build_qualifications WHERE organization_id=org AND qualification_id=key;
    IF NOT FOUND OR old.actor_id<>actor OR old.project_id<>project OR old.mapping_id<>mapping THEN RETURN 3; END IF;
    RETURN CASE WHEN old.proof_body=proof AND old.result_body=completion THEN 1 ELSE 2 END;
EXCEPTION WHEN data_exception THEN RETURN 3;
END;
$body$;
REVOKE ALL ON FUNCTION braid.write_qualification(uuid,uuid,uuid,uuid,bytea,bytea) FROM PUBLIC;

CREATE FUNCTION braid.read_qualification(org uuid, project uuid, mapping uuid, key uuid)
RETURNS TABLE(proof bytea, completion bytea, intent bytea, artifact_digest text, artifact_bytes integer, build_attempt integer, build_created timestamp with time zone, build_outcome integer, case_owner uuid, case_bytes integer, envelope bytea, evidence_attempt integer, evidence_created timestamp with time zone, evidence bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT q.proof_body,q.result_body,b.intent_body,b.artifact_digest,b.artifact_bytes,b.attempt,b.created_at,b.outcome,
        c.creator_id,c.byte_count,c.envelope,r.attempt,r.created_at,r.result_body
    FROM ONLY braid.build_qualifications q
    JOIN ONLY braid.build_runs b ON b.organization_id=q.organization_id AND b.build_id=q.build_id AND b.actor_id=q.actor_id AND b.state='succeeded'
    JOIN ONLY braid.regression_runs r ON r.organization_id=q.organization_id AND r.run_id=q.evidence_id AND r.submitter_id=q.actor_id AND r.state='completed'
    JOIN ONLY braid.regression_case_versions c ON c.organization_id=r.organization_id AND c.project_id=r.project_id AND c.mapping_id=r.mapping_id
        AND c.case_id=r.case_id AND c.version=r.version AND c.content_digest=r.case_digest
    WHERE session_user IN ('braid_qualification_reader','braid_qualification_writer') AND q.organization_id=org AND q.project_id=project AND q.mapping_id=mapping
        AND q.qualification_id=key AND q.actor_id=NULLIF(current_setting('braid.build_principal_id',true),'')::uuid
        AND braid.build_authorized(org,project,q.actor_id,ARRAY['publisher','administrator'])
$body$;
REVOKE ALL ON FUNCTION braid.read_qualification(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT USAGE ON SCHEMA braid TO braid_qualification_reader,braid_qualification_writer;
GRANT EXECUTE ON FUNCTION braid.read_qualification(uuid,uuid,uuid,uuid) TO braid_qualification_reader,braid_qualification_writer;
GRANT EXECUTE ON FUNCTION braid.write_qualification(uuid,uuid,uuid,uuid,bytea,bytea) TO braid_qualification_writer;
