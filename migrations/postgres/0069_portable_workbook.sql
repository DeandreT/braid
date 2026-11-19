-- Extend only the previously admitted native build routines. Historical pins remain readable.
DO $guard$ BEGIN IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_catalog.pg_proc WHERE pronamespace='braid'::regnamespace AND proname='native_build_key') IS DISTINCT FROM '1e34b776d9a8d52369612b5591ac134c40d1eb9a584f524d0889e40549ba16b5' THEN RAISE EXCEPTION 'Unexpected native_build_key predecessor.'; END IF; END $guard$;
CREATE OR REPLACE FUNCTION braid.native_build_key(profile text) RETURNS text LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS $body$
SELECT CASE profile WHEN 'braid.ferrule-csharp-flextext.v1' THEN 'flextext' WHEN 'braid.ferrule-csharp-protobuf.v1' THEN 'protobuf' WHEN 'braid.ferrule-csharp-xbrl.v1' THEN 'xbrl' WHEN 'braid.ferrule-csharp-xlsx.v1' THEN 'xlsx' END
$body$;
DO $guard$ BEGIN IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_catalog.pg_proc WHERE pronamespace='braid'::regnamespace AND proname='build_intent_canonical') IS DISTINCT FROM 'fdd7512abb8d778dd4b1d6da08840ddcbf4ade19c90c0d80929b89bc833cf5d8' THEN RAISE EXCEPTION 'Unexpected build_intent_canonical predecessor.'; END IF; END $guard$;
CREATE OR REPLACE FUNCTION braid.build_intent_canonical(j jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE k text; b jsonb; result text:='{'; separator text:=''; boundary_separator text:=''; csv boolean; xml_build boolean; native_key text;
BEGIN
    native_key:=braid.native_build_key(j->>'profileId');
    xml_build:=j->>'profileId'='braid.ferrule-csharp-xml.v1';
    csv:=j->>'profileId'='braid.ferrule-csharp-csv.v1';
    FOREACH k IN ARRAY ARRAY['schema','buildId','organizationId','projectId','mappingId','revisionId','nativeDigest','backendId','engineRevision','engineBuild','adapterVersion','profileId','profileVersion','runtime','runtimeVersion','artifactFormat','maximumArtifactBytes','environmentId','environmentVersion','approvalId','regressionEvidenceId'] LOOP
        result:=result||separator||chr(34)||k||chr(34)||':'; separator:=',';
        IF k IN ('profileVersion','maximumArtifactBytes','environmentVersion') THEN result:=result||((j->>k)::bigint)::text;
        ELSE result:=result||braid.build_json_text(j->>k,false); END IF;
    END LOOP;
    IF native_key IS NOT NULL AND (j->>'backendId' IS DISTINCT FROM 'ferrule' OR (j->>'engineRevision' IS DISTINCT FROM '9149905759b4f65baf043b975624e45805a50142' AND (native_key='xlsx' OR j->>'engineRevision' IS DISTINCT FROM '796eb8230561f200ca81c153c946a8fe08884e75')) OR j->>'adapterVersion' IS DISTINCT FROM 'braid.ferrule-'||native_key||'-preview-adapter.v1'
        OR (j->>'engineBuild'~'^sha256:[0-9a-f]{64}$') IS NOT TRUE OR j->>'profileVersion' IS DISTINCT FROM '1' OR j->>'runtime' IS DISTINCT FROM 'dotnet' OR j->>'runtimeVersion' IS DISTINCT FROM '10.0.12' OR j->>'artifactFormat' IS DISTINCT FROM 'braid.dotnet-assembly.v1' OR j->>'maximumArtifactBytes' IS DISTINCT FROM '33554432'
        OR (SELECT count(*)<>2 OR count(*) FILTER(WHERE v->>'kind'='primaryInput')<>1 OR count(*) FILTER(WHERE v->>'kind'='primaryOutput')<>1 OR bool_or(v->'name' IS DISTINCT FROM 'null'::jsonb OR lower(v->>'format') IS DISTINCT FROM native_key OR NOT braid.native_settings_valid(v->'documentSettings','braid.ferrule-'||native_key||'-preview.v1')) FROM jsonb_array_elements(j->'boundaries') v)) THEN RAISE EXCEPTION 'Invalid native byte build.' USING ERRCODE='22023'; END IF;
    IF xml_build AND (j->>'backendId' IS DISTINCT FROM 'ferrule' OR j->>'engineRevision' NOT IN ('740af0c38ede7d814a17146bab2bffd7301320c9','796eb8230561f200ca81c153c946a8fe08884e75','9149905759b4f65baf043b975624e45805a50142') OR j->>'adapterVersion' IS DISTINCT FROM 'braid.ferrule-xml-preview-adapter.v1'
        OR (j->>'engineBuild'~'^sha256:[0-9a-f]{64}$') IS NOT TRUE OR j->>'profileVersion' IS DISTINCT FROM '1' OR j->>'runtime' IS DISTINCT FROM 'dotnet'
        OR j->>'runtimeVersion' IS DISTINCT FROM '10.0.12' OR j->>'artifactFormat' IS DISTINCT FROM 'braid.dotnet-assembly.v1' OR j->>'maximumArtifactBytes' IS DISTINCT FROM '33554432'
        OR (SELECT count(*) FILTER(WHERE v->>'kind'='primaryInput')<>1 OR count(*) FILTER(WHERE v->>'kind'='primaryOutput')<>1
            OR count(*) FILTER(WHERE v->>'kind' IN ('primaryInput','namedInput'))>16 OR count(*) FILTER(WHERE v->>'kind' IN ('primaryOutput','namedOutput'))>64
            OR sum(octet_length(braid.build_xml_metadata(v->'xmlMetadata',v->>'kind' IN ('primaryInput','namedInput')))+15)>32768 FROM jsonb_array_elements(j->'boundaries') v)) THEN
        RAISE EXCEPTION 'Invalid XML build identity or boundaries.' USING ERRCODE='22023'; END IF;
    IF csv AND (jsonb_array_length(j->'boundaries')<>2 OR
        (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') x WHERE x->>'kind'='primaryInput' AND x->'name'='null'::jsonb)<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') x WHERE x->>'kind'='primaryOutput' AND x->'name'='null'::jsonb)<>1) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV build boundaries.';
    END IF;
    result:=result||',"boundaries":[';
    FOR b IN SELECT value FROM jsonb_array_elements(j->'boundaries') ORDER BY
        CASE value->>'kind' WHEN 'primaryInput' THEN 0 WHEN 'namedInput' THEN 1 WHEN 'primaryOutput' THEN 2 WHEN 'namedOutput' THEN 3 ELSE 4 END,
        braid.build_json_text(value->>'name',true) COLLATE "C" LOOP
        result:=result||boundary_separator||'{"kind":'||braid.build_json_text(b->>'kind',false)||',"name":'||braid.build_json_text(b->>'name',false)||',"format":'||braid.build_json_text(b->>'format',false);
        IF native_key IS NOT NULL THEN
            result:=result||',"documentSettings":'||braid.build_native_settings(b->'documentSettings');
        ELSIF xml_build THEN
            result:=result||',"xmlMetadata":'||braid.build_xml_metadata(b->'xmlMetadata',b->>'kind' IN ('primaryInput','namedInput'));
        ELSIF csv THEN
            IF b->>'format' NOT IN ('csv','tsv') OR NOT braid.csv_dialect_valid(b->'csvDialect',
                CASE b->>'format' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' END) THEN
                RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV build dialect.';
            END IF;
            result:=result||',"csvDialect":'||braid.csv_dialect_canonical(b->'csvDialect');
        ELSIF b ? 'csvDialect' THEN
            RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Unexpected CSV build dialect.';
        END IF;
        result:=result||'}'; boundary_separator:=',';
    END LOOP;
    RETURN result||']}';
END;
$body$;
DO $guard$ BEGIN IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_catalog.pg_proc WHERE pronamespace='braid'::regnamespace AND proname='build_evidence_passes') IS DISTINCT FROM '92171b5a8cd4c2daf9af9bd48cb4f9165cb328ad15fed2a9ba73f62193561f76' THEN RAISE EXCEPTION 'Unexpected build_evidence_passes predecessor.'; END IF; END $guard$;
CREATE OR REPLACE FUNCTION braid.build_evidence_passes(org uuid, run_key uuid, actor uuid, native_sha text, backend text, engine text, build text, adapter text) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r record; header jsonb; result jsonb; expected jsonb; actual jsonb; raw bytea; h bigint; seen text[]:=ARRAY[]::text[]; k text; documents boolean; csv boolean; xml_build boolean; native_key text;
BEGIN
    SELECT x.*,c.envelope INTO r FROM ONLY braid.regression_runs x JOIN ONLY braid.regression_case_versions c
        ON c.organization_id=x.organization_id AND c.project_id=x.project_id AND c.mapping_id=x.mapping_id
        AND c.case_id=x.case_id AND c.version=x.version AND c.content_digest=x.case_digest
        WHERE x.organization_id=org AND x.run_id=run_key AND x.submitter_id=actor AND x.state='completed' AND x.native_digest=native_sha;
    IF NOT FOUND OR octet_length(r.envelope)<4 THEN RETURN false; END IF;
    h:=get_byte(r.envelope,0)::bigint*16777216+get_byte(r.envelope,1)*65536+get_byte(r.envelope,2)*256+get_byte(r.envelope,3);
    IF h NOT BETWEEN 1 AND 262144 OR h>octet_length(r.envelope)-4 THEN RETURN false; END IF;
    header:=convert_from(substring(r.envelope FROM 5 FOR h::integer),'UTF8')::jsonb;
    result:=convert_from(r.result_body,'UTF8')::jsonb;
    native_key:=braid.native_document_key(header->'runtime'->>'profile');
    xml_build:=header->>'schema'='braid.xml-regression-case.v1';
    documents:=header->>'schema'='braid.document-regression-case.v1';
    csv:=header->>'schema'='braid.csv-regression-case.v1';
    IF (native_key IS NOT NULL AND (header->>'schema' IS DISTINCT FROM 'braid.'||native_key||'-regression-case.v1' OR backend IS DISTINCT FROM 'ferrule' OR (engine IS DISTINCT FROM '9149905759b4f65baf043b975624e45805a50142' AND (native_key='xlsx' OR engine IS DISTINCT FROM '796eb8230561f200ca81c153c946a8fe08884e75')) OR adapter IS DISTINCT FROM 'braid.ferrule-'||native_key||'-preview-adapter.v1' OR header->'target' IS DISTINCT FROM '{"kind":"all","name":null}'::jsonb OR jsonb_array_length(header->'inputs')<>1 OR jsonb_array_length(header->'expected')<>1 OR header->'inputs'->0->'present' IS DISTINCT FROM 'true'::jsonb OR NOT braid.native_settings_valid(header->'inputs'->0->'documentSettings',header->'runtime'->>'profile'))) OR documents IS NULL OR csv IS NULL OR (documents AND (header->'runtime'->>'profile' IS DISTINCT FROM 'braid.ferrule-document-preview.v1'
            OR header->'comparisonOptions' IS DISTINCT FROM 'null'::jsonb))
        OR (csv AND (header->'runtime'->>'profile' IS DISTINCT FROM 'braid.ferrule-csv-preview.v1'
            OR header->'target' IS DISTINCT FROM '{"kind":"all","name":null}'::jsonb
            OR jsonb_array_length(header->'inputs')<>1 OR header->'inputs'->0->'present' IS DISTINCT FROM 'true'::jsonb
            OR NOT braid.csv_dialect_valid(header->'inputs'->0->'csvDialect',CASE WHEN header->'inputs'->0->'csvDialect'->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END)))
        OR (xml_build AND (backend IS DISTINCT FROM 'ferrule' OR engine NOT IN ('740af0c38ede7d814a17146bab2bffd7301320c9','796eb8230561f200ca81c153c946a8fe08884e75','9149905759b4f65baf043b975624e45805a50142') OR adapter IS DISTINCT FROM 'braid.ferrule-xml-preview-adapter.v1' OR header->'runtime'->>'profile' IS DISTINCT FROM 'braid.ferrule-xml-preview.v1' OR header->'target' IS DISTINCT FROM '{"kind":"all","name":null}'::jsonb
            OR EXISTS(SELECT FROM jsonb_array_elements(header->'inputs') i WHERE NOT braid.xml_metadata_valid(i->'xmlMetadata',true))))
        OR (native_key IS NULL AND NOT xml_build AND NOT documents AND NOT csv AND (header->>'schema' IS DISTINCT FROM 'braid.regression-case.v1'
            OR header->'runtime'->>'profile' IS DISTINCT FROM 'braid.ferrule-json-preview.v1'))
        OR header->>'comparisonPolicy' IS DISTINCT FROM 'braid.regression.exact-bytes.v1' OR header->'runtime'->>'backendId' IS DISTINCT FROM backend
        OR header->'runtime'->>'engineRevision' IS DISTINCT FROM engine OR header->'runtime'->>'backendBuild' IS DISTINCT FROM build
        OR header->'runtime'->>'adapterBuild' IS DISTINCT FROM adapter
        OR result->>'schema' IS DISTINCT FROM (CASE WHEN native_key IS NOT NULL THEN 'braid.'||native_key||'-regression-run-result.v1' WHEN xml_build THEN 'braid.xml-regression-run-result.v1' WHEN csv THEN 'braid.csv-regression-run-result.v1' ELSE 'braid.regression-run-result.v1' END)
        OR result->>'runId' IS DISTINCT FROM run_key::text
        OR result->>'caseDigest' IS DISTINCT FROM r.case_digest OR result->>'outcome' IS DISTINCT FROM '0' OR result->>'previewStatus' IS DISTINCT FROM '0'
        OR jsonb_typeof(header->'expected') IS DISTINCT FROM 'array' OR jsonb_typeof(result->'outputs') IS DISTINCT FROM 'array'
        OR jsonb_array_length(header->'expected') NOT BETWEEN 1 AND 64
        OR (csv AND jsonb_array_length(header->'expected')<>1)
        OR jsonb_array_length(header->'expected')<>jsonb_array_length(result->'outputs') THEN RETURN false; END IF;
    FOR expected IN SELECT value FROM jsonb_array_elements(header->'expected') LOOP
        k:=coalesce(expected->>'name',chr(1));
        IF k=ANY(seen) THEN RETURN false; END IF;
        seen:=array_append(seen,k);
        SELECT value INTO actual FROM jsonb_array_elements(result->'outputs') WHERE value->'name'=expected->'name';
        IF NOT FOUND OR (SELECT count(*) FROM jsonb_array_elements(result->'outputs') WHERE value->'name'=expected->'name')<>1 THEN RETURN false; END IF;
        IF (native_key IS NOT NULL AND (expected->>'mediaType' IS DISTINCT FROM braid.native_document_media(header->'runtime'->>'profile') OR NOT braid.native_settings_valid(expected->'documentSettings',header->'runtime'->>'profile') OR actual->'documentSettings' IS DISTINCT FROM expected->'documentSettings')) OR (documents AND coalesce(expected->>'mediaType','') NOT IN ('application/json','application/edi-x12','application/edifact'))
            OR (csv AND (expected->'name' IS DISTINCT FROM 'null'::jsonb OR NOT braid.csv_dialect_valid(expected->'csvDialect',expected->>'mediaType')
                OR actual->'csvDialect' IS DISTINCT FROM expected->'csvDialect'))
            OR (NOT csv AND (expected ? 'csvDialect' OR actual ? 'csvDialect'))
            OR (xml_build AND (expected->>'mediaType' IS DISTINCT FROM 'application/xml' OR NOT braid.xml_metadata_valid(expected->'xmlMetadata',false) OR actual->'xmlMetadata' IS DISTINCT FROM expected->'xmlMetadata'))
            OR (NOT xml_build AND (expected ? 'xmlMetadata' OR actual ? 'xmlMetadata')) OR (native_key IS NULL AND NOT xml_build AND NOT documents AND NOT csv AND expected ? 'mediaType')
            OR coalesce(actual->>'mediaType','application/json') IS DISTINCT FROM coalesce(expected->>'mediaType','application/json')
            OR jsonb_typeof(actual->'content') IS DISTINCT FROM 'string' OR (actual->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE THEN RETURN false; END IF;
        raw:=decode(translate(actual->>'content','-_','+/')||repeat('=',(4-length(actual->>'content')%4)%4),'base64');
        IF raw IS NULL OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256' THEN RETURN false; END IF;
    END LOOP;
    RETURN true;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
DO $guard$ BEGIN IF (SELECT encode(sha256(convert_to(prosrc,'UTF8')),'hex') FROM pg_catalog.pg_proc WHERE pronamespace='braid'::regnamespace AND proname='qualification_valid') IS DISTINCT FROM '63141017681de11344994551ef3ed8f54d22cb981c83656497699ab6de63ce1e' THEN RAISE EXCEPTION 'Unexpected qualification_valid predecessor.'; END IF; END $guard$;
CREATE OR REPLACE FUNCTION braid.qualification_valid(org uuid, project uuid, mapping uuid, key uuid, actor uuid, proof bytea, completion bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE q jsonb; b record; r record; h bigint; header_raw json; header jsonb; result jsonb; item record; expected jsonb;
    replay text; canonical text; separator text:=''; raw bytea; total bigint:=0; boundary_count integer;
    intent jsonb; documents boolean; csv boolean; xml_build boolean; native_key text; replay_inputs text; media text; boundary jsonb;
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
    native_key:=braid.native_build_key(intent->>'profileId');
    xml_build:=intent->>'profileId'='braid.ferrule-csharp-xml.v1';
    csv:=intent->>'profileId'='braid.ferrule-csharp-csv.v1';
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
        OR header->'runtime'->>'profile' IS DISTINCT FROM (CASE WHEN native_key IS NOT NULL THEN 'braid.ferrule-'||native_key||'-preview.v1' WHEN xml_build THEN 'braid.ferrule-xml-preview.v1' WHEN csv THEN 'braid.ferrule-csv-preview.v1' WHEN documents THEN 'braid.ferrule-document-preview.v1' ELSE 'braid.ferrule-json-preview.v1' END)
        OR NOT braid.build_evidence_passes(org,b.evidence_id,actor,b.native_digest,
            intent->>'backendId',intent->>'engineRevision',intent->>'engineBuild',intent->>'adapterVersion') THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind' IN ('primaryOutput','namedOutput');
    IF boundary_count<>jsonb_array_length(header->'expected') OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e
        WHERE e->>'kind' IN ('primaryOutput','namedOutput') AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'expected') x WHERE x->'name'=e->'name'
                AND (NOT documents AND NOT csv OR coalesce(x->>'mediaType','application/json')=CASE e->>'format' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' WHEN 'xml' THEN 'application/xml' END)
                AND (native_key IS NULL OR x->'documentSettings'=e->'documentSettings') AND (NOT csv OR x->'csvDialect'=e->'csvDialect') AND (NOT xml_build OR x->'xmlMetadata'=e->'xmlMetadata'))) THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput';
    IF boundary_count<>jsonb_array_length(header->'inputs')-1 OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput' AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'inputs') x WHERE x->'name'=e->'name')) THEN RETURN false; END IF;
    IF csv AND (jsonb_array_length(intent->'boundaries')<>2 OR jsonb_array_length(header->'inputs')<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='primaryInput')<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='primaryOutput')<>1) THEN RETURN false; END IF;
    replay_inputs:=(header_raw->'inputs')::text;
    IF native_key IS NOT NULL OR documents OR csv OR xml_build THEN
        replay_inputs:='['; separator:='';
        FOR item IN SELECT value FROM json_array_elements(header_raw->'inputs') LOOP
            SELECT e INTO boundary FROM jsonb_array_elements(intent->'boundaries') e
                WHERE e->'name'=item.value::jsonb->'name' AND e->>'kind' IN ('primaryInput','namedInput');
            media:=CASE boundary->>'format' WHEN 'flexText' THEN 'text/plain' WHEN 'xlsx' THEN 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' WHEN 'protobuf' THEN 'application/x-protobuf' WHEN 'xbrl' THEN 'application/xbrl+xml' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' WHEN 'xml' THEN 'application/xml' END;
            IF media IS NULL THEN RETURN false; END IF;
            IF native_key IS NOT NULL THEN
                IF item.value::jsonb->'documentSettings' IS DISTINCT FROM boundary->'documentSettings' THEN RETURN false; END IF;
                replay_inputs:=replay_inputs||separator||'{"name":null,"byteCount":'||(item.value->>'byteCount')||',"sha256":'||braid.build_json_text(item.value->>'sha256',false)||',"mediaType":'||braid.build_json_text(media,false)||'}';
            ELSIF xml_build THEN
                IF item.value::jsonb->'xmlMetadata' IS DISTINCT FROM boundary->'xmlMetadata' THEN RETURN false; END IF;
                replay_inputs:=replay_inputs||separator||'{"name":'||braid.build_json_text(item.value->>'name',false)||',"byteCount":'||(item.value->>'byteCount')
                    ||',"sha256":'||braid.build_json_text(item.value->>'sha256',false)||',"mediaType":"application/xml","xmlMetadata":'||braid.build_xml_metadata(boundary->'xmlMetadata',true)||'}';
            ELSIF csv THEN
                IF item.value::jsonb->'present' IS DISTINCT FROM 'true'::jsonb OR item.value::jsonb->'csvDialect' IS DISTINCT FROM boundary->'csvDialect'
                    OR NOT braid.csv_dialect_valid(boundary->'csvDialect',media) THEN RETURN false; END IF;
                replay_inputs:=replay_inputs||separator||'{"name":'||braid.build_json_text(item.value->>'name',false)
                    ||',"byteCount":'||(item.value->>'byteCount')||',"sha256":'||braid.build_json_text(item.value->>'sha256',false)
                    ||',"mediaType":'||braid.build_json_text(media,false)||',"csvDialect":'||braid.csv_dialect_canonical(boundary->'csvDialect')||'}';
            ELSE
                replay_inputs:=replay_inputs||separator||left(item.value::text,length(item.value::text)-1)||',"mediaType":'||braid.build_json_text(media,false)||'}';
            END IF;
            separator:=',';
        END LOOP;
        replay_inputs:=replay_inputs||']'; separator:='';
    END IF;
    replay:='{"target":"all","targetName":null,"currentDateTime":'||(header_raw->'currentDateTime')::text||',"limits":'||(header_raw->'limits')::text
        ||',"inputs":'||replay_inputs||',"parameters":'||(header_raw->'parameters')::text||'}';
    IF q->>'replayDigest' IS DISTINCT FROM encode(sha256(convert_to(replay,'UTF8')),'hex') THEN RETURN false; END IF;
    result:=convert_from(completion,'UTF8')::jsonb;
    IF result->>'schema' IS DISTINCT FROM (CASE WHEN native_key IS NOT NULL THEN 'braid.'||native_key||'-regression-run-result.v1' WHEN xml_build THEN 'braid.xml-regression-run-result.v1' WHEN csv THEN 'braid.csv-regression-run-result.v1' ELSE 'braid.regression-run-result.v1' END) OR result->>'runId' IS DISTINCT FROM key::text
        OR result->>'caseDigest' IS DISTINCT FROM r.case_digest OR result->>'outcome' IS DISTINCT FROM '0' OR result->>'previewStatus' IS DISTINCT FROM '0'
        OR jsonb_typeof(result->'outputs') IS DISTINCT FROM 'array' OR jsonb_array_length(result->'outputs')<>jsonb_array_length(header->'expected')
        OR jsonb_array_length(result->'outputs') NOT BETWEEN 1 AND (header->'limits'->>'maximumArtifacts')::integer THEN RETURN false; END IF;
    canonical:='{"schema":'||braid.build_json_text(result->>'schema',false)||',"runId":"'||key::text||'","caseDigest":"'||r.case_digest||'","outcome":0,"previewStatus":0,"outputs":[';
    FOR item IN SELECT value,ordinality FROM jsonb_array_elements(result->'outputs') WITH ORDINALITY LOOP
        expected:=header->'expected'->(item.ordinality::integer-1);
        IF item.value->'name' IS DISTINCT FROM expected->'name' OR jsonb_typeof(item.value->'content') IS DISTINCT FROM 'string'
            OR (item.value->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE
            OR coalesce(item.value->>'mediaType','application/json') IS DISTINCT FROM coalesce(expected->>'mediaType','application/json')
            OR native_key IS NOT NULL AND item.value->'documentSettings' IS DISTINCT FROM expected->'documentSettings'
            OR xml_build AND item.value->'xmlMetadata' IS DISTINCT FROM expected->'xmlMetadata'
            OR csv AND item.value->'csvDialect' IS DISTINCT FROM expected->'csvDialect'
            OR NOT csv AND item.value ? 'csvDialect' THEN RETURN false; END IF;
        raw:=decode(translate(item.value->>'content','-_','+/')||repeat('=',(4-length(item.value->>'content')%4)%4),'base64');
        total:=total+octet_length(raw);
        IF total>(header->'limits'->>'maximumOutputBytes')::bigint OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer
            OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256'
            OR translate(rtrim(replace(encode(raw,'base64'),chr(10),''),'='),'+/','-_') IS DISTINCT FROM item.value->>'content' THEN RETURN false; END IF;
        canonical:=canonical||separator||'{"name":'||braid.build_json_text(item.value->>'name',false)||',"content":"'||(item.value->>'content')||'"'; separator:=',';
        IF native_key IS NOT NULL OR expected->>'mediaType' IN ('application/edi-x12','application/edifact','text/csv','text/tab-separated-values','application/xml') THEN canonical:=canonical||',"mediaType":'||braid.build_json_text(expected->>'mediaType',false); END IF;
        IF native_key IS NOT NULL THEN canonical:=canonical||',"documentSettings":'||braid.build_native_settings(expected->'documentSettings'); END IF;
        IF xml_build THEN canonical:=canonical||',"xmlMetadata":'||braid.build_xml_metadata(expected->'xmlMetadata',false); END IF;
        IF csv THEN canonical:=canonical||',"csvDialect":'||braid.csv_dialect_canonical(expected->'csvDialect'); END IF;
        canonical:=canonical||'}';
    END LOOP;
    RETURN convert_to(canonical||']}', 'UTF8')=completion;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
