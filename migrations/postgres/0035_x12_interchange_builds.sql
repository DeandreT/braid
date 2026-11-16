-- Bind explicit MapForce-interchange builds to the same exact document evidence rules.
CREATE OR REPLACE FUNCTION braid.qualification_valid(org uuid, project uuid, mapping uuid, key uuid, actor uuid, proof bytea, completion bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE q jsonb; b record; r record; h bigint; header_raw json; header jsonb; result jsonb; item record; expected jsonb;
    replay text; canonical text; separator text:=''; raw bytea; total bigint:=0; boundary_count integer;
    intent jsonb; documents boolean; replay_inputs text; media text;
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
    intent:=convert_from(b.intent_body,'UTF8')::jsonb;
    documents:=intent->>'profileId' IN ('braid.ferrule-csharp-document.v1','braid.ferrule-csharp-x12-interchanges.v1');
    IF intent->>'profileId'='braid.ferrule-csharp-x12-interchanges.v1' AND NOT EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'format'='x12') THEN RETURN false; END IF;
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
        OR header->'runtime'->>'profile' IS DISTINCT FROM (CASE WHEN documents THEN 'braid.ferrule-document-preview.v1' ELSE 'braid.ferrule-json-preview.v1' END)
        OR NOT braid.build_evidence_passes(org,b.evidence_id,actor,b.native_digest,
            intent->>'backendId',intent->>'engineRevision',intent->>'engineBuild',intent->>'adapterVersion') THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind' IN ('primaryOutput','namedOutput');
    IF boundary_count<>jsonb_array_length(header->'expected') OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e
        WHERE e->>'kind' IN ('primaryOutput','namedOutput') AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'expected') x WHERE x->'name'=e->'name'
                AND (NOT documents OR coalesce(x->>'mediaType','application/json')=CASE e->>'format' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' END))) THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput';
    IF boundary_count<>jsonb_array_length(header->'inputs')-1 OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput' AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'inputs') x WHERE x->'name'=e->'name')) THEN RETURN false; END IF;
    replay_inputs:=(header_raw->'inputs')::text;
    IF documents THEN
        replay_inputs:='['; separator:='';
        FOR item IN SELECT value FROM json_array_elements(header_raw->'inputs') LOOP
            SELECT CASE e->>'format' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' END INTO media
                FROM jsonb_array_elements(intent->'boundaries') e WHERE e->'name'=item.value::jsonb->'name' AND e->>'kind' IN ('primaryInput','namedInput');
            IF media IS NULL THEN RETURN false; END IF;
            replay_inputs:=replay_inputs||separator||left(item.value::text,length(item.value::text)-1)||',"mediaType":'||braid.build_json_text(media,false)||'}'; separator:=',';
        END LOOP;
        replay_inputs:=replay_inputs||']'; separator:='';
    END IF;
    replay:='{"target":"all","targetName":null,"currentDateTime":'||(header_raw->'currentDateTime')::text||',"limits":'||(header_raw->'limits')::text
        ||',"inputs":'||replay_inputs||',"parameters":'||(header_raw->'parameters')::text||'}';
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
            OR (item.value->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE
            OR coalesce(item.value->>'mediaType','application/json') IS DISTINCT FROM coalesce(expected->>'mediaType','application/json') THEN RETURN false; END IF;
        raw:=decode(translate(item.value->>'content','-_','+/')||repeat('=',(4-length(item.value->>'content')%4)%4),'base64');
        total:=total+octet_length(raw);
        IF total>(header->'limits'->>'maximumOutputBytes')::bigint OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer
            OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256'
            OR translate(rtrim(replace(encode(raw,'base64'),chr(10),''),'='),'+/','-_') IS DISTINCT FROM item.value->>'content' THEN RETURN false; END IF;
        canonical:=canonical||separator||'{"name":'||braid.build_json_text(item.value->>'name',false)||',"content":"'||(item.value->>'content')||'"'; separator:=',';
        IF expected->>'mediaType' IN ('application/edi-x12','application/edifact') THEN canonical:=canonical||',"mediaType":'||braid.build_json_text(expected->>'mediaType',false); END IF;
        canonical:=canonical||'}';
    END LOOP;
    RETURN convert_to(canonical||']}', 'UTF8')=completion;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
