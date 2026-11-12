-- CSV is a separate, explicitly selected document profile. Existing private rows,
-- envelopes, payload bytes, digests and legacy function entry points are retained.
-- Seven fields, including the version tag (quote is present even when disabled).
CREATE FUNCTION weft.csv_dialect_valid(d jsonb, media text) RETURNS boolean
LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT coalesce(jsonb_typeof(d)='object' AND d->>'schema'='weft.csv-dialect.v1'
        AND (SELECT count(*)=7 FROM jsonb_object_keys(d))
        AND jsonb_typeof(d->'delimiter')='string' AND octet_length(d->>'delimiter')=1
        AND ascii(d->>'delimiter') BETWEEN 1 AND 127 AND d->>'delimiter' NOT IN (chr(10),chr(13))
        AND jsonb_typeof(d->'quoteDisabled')='boolean' AND jsonb_typeof(d->'hasHeaderRow')='boolean'
        AND jsonb_typeof(d->'utf8Bom')='boolean' AND jsonb_typeof(d->'preserveEmptyStrings')='boolean'
        AND ((d->'quoteDisabled'='true'::jsonb AND d->'quote'='null'::jsonb)
            OR (d->'quoteDisabled'='false'::jsonb AND jsonb_typeof(d->'quote')='string'
                AND octet_length(d->>'quote')=1 AND ascii(d->>'quote') BETWEEN 33 AND 126 AND d->>'quote'<>d->>'delimiter'))
        AND media=CASE WHEN d->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END, false)
$body$;
-- Exact Utf8JsonWriter default escaping for one admitted ASCII dialect character.
-- Delimiters may be whitespace/control characters; ordinary build strings retain
-- their stricter nonempty/noncontrol policy in build_json_text.
CREATE FUNCTION weft.csv_dialect_char_canonical(value text) RETURNS text
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE cp integer; escaped text;
BEGIN
    IF value IS NULL THEN RETURN 'null'; END IF;
    IF octet_length(value)<>1 THEN RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV dialect character.'; END IF;
    cp:=ascii(value);
    IF cp NOT BETWEEN 1 AND 127 OR cp IN (10,13) THEN RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV dialect character.'; END IF;
    IF cp=8 THEN escaped:=chr(92)||'b';
    ELSIF cp=9 THEN escaped:=chr(92)||'t';
    ELSIF cp=12 THEN escaped:=chr(92)||'f';
    ELSIF cp=92 THEN escaped:=chr(92)||chr(92);
    ELSIF cp<32 OR cp=127 OR cp IN (34,38,39,43,60,62,96) THEN escaped:=chr(92)||'u'||upper(lpad(to_hex(cp),4,'0'));
    ELSE escaped:=value;
    END IF;
    RETURN chr(34)||escaped||chr(34);
END;
$body$;
CREATE FUNCTION weft.csv_dialect_canonical(d jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF NOT weft.csv_dialect_valid(d,CASE WHEN d->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV dialect.';
    END IF;
    RETURN '{"schema":'||weft.build_json_text(d->>'schema',false)||',"delimiter":'||weft.csv_dialect_char_canonical(d->>'delimiter')
        ||',"quote":'||weft.csv_dialect_char_canonical(d->>'quote')||',"quoteDisabled":'||(d->'quoteDisabled')::text
        ||',"hasHeaderRow":'||(d->'hasHeaderRow')::text||',"utf8Bom":'||(d->'utf8Bom')::text
        ||',"preserveEmptyStrings":'||(d->'preserveEmptyStrings')::text||'}';
END;
$body$;
REVOKE ALL ON FUNCTION weft.csv_dialect_valid(jsonb,text),weft.csv_dialect_char_canonical(text),weft.csv_dialect_canonical(jsonb) FROM PUBLIC;

ALTER TABLE weft.preview_runs ADD COLUMN csv_profile boolean NOT NULL DEFAULT false;
ALTER TABLE weft.preview_documents ADD COLUMN csv_input boolean NOT NULL DEFAULT false;
ALTER TABLE weft.preview_documents ADD COLUMN csv_dialect jsonb;
ALTER TABLE weft.preview_runs DROP CONSTRAINT preview_runs_inputs_check;
ALTER TABLE weft.preview_runs ADD CONSTRAINT preview_runs_inputs_check CHECK (
    (NOT csv_profile AND input_count BETWEEN 1 AND 16 AND input_bytes BETWEEN input_count AND 8388608)
    OR (csv_profile AND draft_id IS NULL AND input_count=1 AND input_bytes BETWEEN 0 AND 8388608));
ALTER TABLE weft.preview_documents DROP CONSTRAINT preview_documents_kind_check;
ALTER TABLE weft.preview_documents ADD CONSTRAINT preview_documents_kind_check CHECK (
    (direction=1 AND (byte_count>0 OR csv_input) AND target_name IS NULL AND target_name_truncated IS NULL
        AND records_written IS NULL
        AND ((NOT csv_input AND media_type IS NULL AND csv_dialect IS NULL)
            OR (csv_input AND weft.csv_dialect_valid(csv_dialect,media_type)))
        AND (ordinal=0)=(name IS NULL) AND (name IS NULL OR octet_length(name) BETWEEN 1 AND 256)
        AND (NOT csv_input OR ordinal=0))
    OR (direction=2 AND NOT csv_input AND name ~ '^(primary-output|named-output:(0|[1-9][0-9]{0,19}))$'
        AND target_name IS NOT NULL AND octet_length(target_name)<=2048 AND target_name_truncated IS NOT NULL
        AND media_type IS NOT NULL AND records_written IS NOT NULL AND records_written>=0
        AND ((media_type IN ('application/json','application/edi-x12','application/edifact') AND csv_dialect IS NULL)
            OR (name='primary-output' AND ordinal=0 AND weft.csv_dialect_valid(csv_dialect,media_type))))
    -- Migration 0038's retained native checkpoint remains private, non-CSV and bounded.
    OR (direction=3 AND ordinal=0 AND name IS NULL AND target_name IS NULL
        AND target_name_truncated IS NULL AND media_type IS NULL AND records_written IS NULL
        AND byte_count BETWEEN 1 AND 4194304 AND NOT csv_input AND csv_dialect IS NULL));

CREATE OR REPLACE FUNCTION weft.preview_submit(p_org uuid, p_project uuid, p_mapping uuid, p_revision uuid, p_actor uuid,
    p_request_key uuid, p_request_document text, p_clock text, p_max_attempts integer, p_retention_seconds integer,
    p_names text[], p_contents bytea[])
RETURNS TABLE(outcome text, run_id uuid)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $weft_preview_submit$
DECLARE
    revision_row weft.mapping_revisions%ROWTYPE;
    existing weft.preview_runs%ROWTYPE;
    created uuid := pg_catalog.gen_random_uuid();
    fingerprint text;
    total bigint := 0;
    item integer;
    csv boolean:=false;
    request jsonb;
BEGIN
    IF p_org IS NULL OR p_project IS NULL OR p_mapping IS NULL OR p_revision IS NULL OR p_actor IS NULL
        OR p_request_key IS NULL OR p_request_document IS NULL OR p_clock IS NULL OR p_names IS NULL OR p_contents IS NULL
        OR pg_catalog.cardinality(p_names) <> pg_catalog.cardinality(p_contents)
        OR pg_catalog.cardinality(p_contents) NOT BETWEEN 1 AND 16 OR p_names[1] IS NOT NULL
        OR p_max_attempts NOT BETWEEN 1 AND 8 OR p_retention_seconds NOT BETWEEN 60 AND 604800 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview submission arguments are invalid.';
    END IF;
    IF NOT weft.preview_authorized(p_org,p_project,p_actor) THEN
        RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO revision_row FROM ONLY weft.mapping_revisions r
        WHERE r.organization_id = p_org AND r.project_id = p_project AND r.mapping_id = p_mapping AND r.id = p_revision;
    IF NOT FOUND THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text, NULL::uuid; RETURN; END IF;
    -- Old opaque request documents retain their previous behavior. Only the new
    -- schema can admit a zero-byte input, and its descriptor must prove presence.
    BEGIN request:=p_request_document::jsonb; csv:=coalesce(request->>'schema'='weft.csv-preview-request.v1',false);
    EXCEPTION WHEN invalid_text_representation THEN csv:=false; END;
    IF csv AND (cardinality(p_contents)<>1 OR request->>'organizationId' IS DISTINCT FROM p_org::text
        OR request->>'projectId' IS DISTINCT FROM p_project::text OR request->>'mappingId' IS DISTINCT FROM p_mapping::text
        OR request->>'revisionId' IS DISTINCT FROM p_revision::text
        OR (request->'target'->>'kind' IN ('all','primary')) IS NOT TRUE
        OR NOT weft.csv_dialect_valid(request->'inputs'->0->'csvDialect',CASE WHEN request->'inputs'->0->'csvDialect'->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END)
        OR request->'inputs' IS DISTINCT FROM jsonb_build_array(jsonb_build_object('name',NULL,'present',true,
            'byteLength',octet_length(p_contents[1]),'sha256',encode(sha256(p_contents[1]),'hex'),
            'csvDialect',request->'inputs'->0->'csvDialect'))) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV preview request.';
    END IF;
    fingerprint := encode(sha256(convert_to(p_request_document,'UTF8')),'hex');
    FOR item IN 1 .. pg_catalog.cardinality(p_contents) LOOP
        IF p_contents[item] IS NULL OR (item > 1 AND p_names[item] IS NULL) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Preview submission arguments are invalid.';
        END IF;
        total := total + octet_length(p_contents[item]);
    END LOOP;
    INSERT INTO weft.preview_runs(organization_id,run_id,project_id,mapping_id,revision_id,submitter_id,request_key,
        request_document,request_fingerprint,current_date_time,native_object_id,native_digest,native_byte_count,
        input_count,input_bytes,max_attempts,expires_at,csv_profile)
    VALUES (p_org,created,p_project,p_mapping,p_revision,p_actor,p_request_key,p_request_document,fingerprint,p_clock,
        revision_row.native_object_id,revision_row.native_digest,revision_row.native_byte_count,
        pg_catalog.cardinality(p_contents),total,p_max_attempts,
        statement_timestamp() + pg_catalog.make_interval(secs => p_retention_seconds),csv)
    ON CONFLICT ON CONSTRAINT preview_runs_request_key DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO existing FROM ONLY weft.preview_runs r
            WHERE r.organization_id = p_org AND r.submitter_id = p_actor AND r.request_key = p_request_key;
        IF existing.project_id = p_project AND existing.mapping_id = p_mapping AND existing.revision_id = p_revision
            AND existing.request_fingerprint = fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text, existing.run_id; RETURN;
        END IF;
        RETURN QUERY SELECT 'conflict'::text, NULL::uuid; RETURN;
    END IF;
    FOR item IN 1 .. pg_catalog.cardinality(p_contents) LOOP
        INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,name,digest,byte_count,content,csv_input,csv_dialect,media_type)
        VALUES (p_org,created,1,item - 1,p_names[item],encode(sha256(p_contents[item]),'hex'),
            octet_length(p_contents[item]),p_contents[item],csv,
            CASE WHEN csv THEN request->'inputs'->0->'csvDialect' END,
            CASE WHEN csv THEN CASE WHEN request->'inputs'->0->'csvDialect'->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END END);
    END LOOP;
    RETURN QUERY SELECT 'created'::text, created;
END;
$weft_preview_submit$;

-- This separate entry point cannot reinterpret an existing JSON/EDI run as CSV.
CREATE FUNCTION weft.preview_complete_csv(p_org uuid, p_run uuid, p_token uuid, p_outcome text, p_failure_code text,
    p_failure_message text, p_failure_message_truncated boolean, p_failure_subject text, p_failure_subject_truncated boolean,
    p_confirmed boolean, p_engine_revision text, p_backend_build text, p_adapter_build text, p_profile text,
    p_records_written bigint, p_boundaries text[], p_target_names text[], p_target_names_truncated boolean[],
    p_media_types text[], p_records bigint[], p_contents bytea[], p_dialects text[])
RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $csv$
DECLARE run weft.preview_runs%ROWTYPE; items integer:=coalesce(cardinality(p_contents),0);
BEGIN
    IF p_org IS NULL OR p_run IS NULL OR p_token IS NULL OR p_outcome IS NULL OR p_profile IS DISTINCT FROM 'weft.ferrule-csv-preview.v1'
        OR (p_outcome='succeeded')<>(items=1) OR items>1
        OR items<>coalesce(cardinality(p_boundaries),0) OR items<>coalesce(cardinality(p_target_names),0)
        OR items<>coalesce(cardinality(p_target_names_truncated),0) OR items<>coalesce(cardinality(p_media_types),0)
        OR items<>coalesce(cardinality(p_records),0) OR items<>coalesce(cardinality(p_dialects),0) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV preview completion.';
    END IF;
    SELECT * INTO run FROM ONLY weft.preview_runs r WHERE r.organization_id=p_org AND r.run_id=p_run FOR UPDATE;
    IF NOT FOUND OR run.state<>2 OR run.lease_token IS DISTINCT FROM p_token THEN RETURN 'claimLost'; END IF;
    IF NOT run.csv_profile THEN RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='CSV profile mismatch.'; END IF;
    IF items=1 THEN
        IF p_boundaries[1] IS DISTINCT FROM 'primary-output' OR NOT weft.csv_dialect_valid(p_dialects[1]::jsonb,p_media_types[1]) THEN
            RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV output descriptor.';
        END IF;
        INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,name,target_name,target_name_truncated,
            media_type,records_written,digest,byte_count,content,csv_dialect)
        VALUES(p_org,p_run,2,0,p_boundaries[1],p_target_names[1],p_target_names_truncated[1],p_media_types[1],p_records[1],
            encode(sha256(p_contents[1]),'hex'),octet_length(p_contents[1]),p_contents[1],p_dialects[1]::jsonb);
    END IF;
    UPDATE weft.preview_runs r SET state=CASE WHEN p_outcome='canceled' THEN 4 ELSE 3 END,outcome=p_outcome,
        failure_code=p_failure_code,failure_message=p_failure_message,failure_message_truncated=p_failure_message_truncated,
        failure_subject=p_failure_subject,failure_subject_truncated=p_failure_subject_truncated,
        provenance_confirmed=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_confirmed END,
        engine_revision=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_engine_revision END,
        backend_build=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_backend_build END,
        adapter_build=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_adapter_build END,
        profile=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_profile END,
        records_written=CASE WHEN items>0 THEN p_records_written END,output_count=CASE WHEN items>0 THEN items END,
        output_bytes=CASE WHEN items>0 THEN octet_length(p_contents[1]) END,
        lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,finished_at=statement_timestamp()
        WHERE r.organization_id=p_org AND r.run_id=p_run;
    RETURN 'completed';
END;
$csv$;
CREATE FUNCTION weft.preview_csv_outputs(p_org uuid,p_run uuid,p_actor uuid)
RETURNS TABLE(ordinal integer,csv_dialect jsonb)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $csv$
    SELECT d.ordinal,d.csv_dialect FROM ONLY weft.preview_documents d JOIN ONLY weft.preview_runs r
        ON r.organization_id=d.organization_id AND r.run_id=d.run_id
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.submitter_id=p_actor AND d.direction=2
        AND weft.preview_subject_authorized(p_org,p_run) ORDER BY d.ordinal
$csv$;
REVOKE ALL ON FUNCTION weft.preview_complete_csv(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]),
    weft.preview_csv_outputs(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.preview_complete_csv(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]) TO weft_preview_writer;
GRANT EXECUTE ON FUNCTION weft.preview_csv_outputs(uuid,uuid,uuid) TO weft_preview_reader,weft_preview_writer;

CREATE FUNCTION weft.preview_csv_input(p_org uuid,p_run uuid,p_actor uuid,p_ordinal integer)
RETURNS TABLE(media_type text,csv_dialect jsonb)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,weft,pg_temp
AS $csv$
    SELECT coalesce(d.media_type,'application/json'),d.csv_dialect FROM ONLY weft.preview_documents d
    JOIN ONLY weft.preview_runs r ON r.organization_id=d.organization_id AND r.run_id=d.run_id
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.submitter_id=p_actor AND d.direction=1 AND d.ordinal=p_ordinal
        AND weft.preview_subject_authorized(p_org,p_run)
$csv$;
REVOKE ALL ON FUNCTION weft.preview_csv_input(uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.preview_csv_input(uuid,uuid,uuid,integer) TO weft_preview_reader,weft_preview_writer;

CREATE FUNCTION weft.guard_csv_preview_document() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $csv$
DECLARE csv boolean;
BEGIN
    SELECT r.csv_profile INTO STRICT csv FROM ONLY weft.preview_runs r WHERE r.organization_id=NEW.organization_id AND r.run_id=NEW.run_id;
    IF (NEW.direction=1 AND NEW.csv_input IS DISTINCT FROM csv)
        OR (NEW.direction=2 AND (NEW.csv_dialect IS NOT NULL) IS DISTINCT FROM csv)
        OR (NEW.direction=3 AND csv) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Preview document profile mismatch.';
    END IF;
    RETURN NEW;
END;
$csv$;
REVOKE ALL ON FUNCTION weft.guard_csv_preview_document() FROM PUBLIC;
CREATE TRIGGER guard_csv_preview_document BEFORE INSERT OR UPDATE ON weft.preview_documents
    FOR EACH ROW EXECUTE FUNCTION weft.guard_csv_preview_document();

-- Keep every original lock, authority, evidence, replay and publication guard.
-- Only the CSV profile gains a closed four-member boundary descriptor.
CREATE OR REPLACE FUNCTION weft.write_build_record(operation text, org uuid, project uuid, key uuid, revision bigint, info jsonb, intent bytea, artifact bytea) RETURNS integer
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE actor uuid; j jsonb; n text; a weft.build_approvals%ROWTYPE; p weft.build_publications%ROWTYPE; e weft.build_environments%ROWTYPE; m weft.mapping_revisions%ROWTYPE; csv boolean;
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
        csv:=j->>'profileId'='weft.ferrule-csharp-csv.v1';
        IF EXISTS(SELECT 1 FROM jsonb_array_elements(j->'boundaries') b WHERE jsonb_typeof(b) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(b))<>(CASE WHEN csv THEN 4 ELSE 3 END) OR b->>'kind' NOT IN ('primaryInput','namedInput','primaryOutput','namedOutput')
            OR jsonb_typeof(b->'kind') IS DISTINCT FROM 'string' OR jsonb_typeof(b->'format') IS DISTINCT FROM 'string'
            OR octet_length(b->>'format') NOT BETWEEN 1 AND 128
            OR ((b->>'kind') IN ('primaryInput','primaryOutput') AND b->'name' IS DISTINCT FROM 'null'::jsonb)
            OR ((b->>'kind') IN ('namedInput','namedOutput') AND (jsonb_typeof(b->'name') IS DISTINCT FROM 'string' OR octet_length(b->>'name') NOT BETWEEN 1 AND 256))
            OR (csv AND (b->>'kind' NOT IN ('primaryInput','primaryOutput') OR b->>'format' NOT IN ('csv','tsv')
                OR NOT weft.csv_dialect_valid(b->'csvDialect',CASE b->>'format' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' END)))
            OR (NOT csv AND b ? 'csvDialect'))
            OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'boundaries') b GROUP BY b->>'kind',b->'name' HAVING count(*)>1) THEN RETURN 3; END IF;
        IF csv AND (jsonb_array_length(j->'boundaries')<>2 OR
            (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') b WHERE b->>'kind'='primaryInput')<>1 OR
            (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') b WHERE b->>'kind'='primaryOutput')<>1) THEN RETURN 3; END IF;
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

CREATE OR REPLACE FUNCTION weft.build_intent_canonical(j jsonb) RETURNS text
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE k text; b jsonb; result text:='{'; separator text:=''; boundary_separator text:=''; csv boolean;
BEGIN
    csv:=j->>'profileId'='weft.ferrule-csharp-csv.v1';
    FOREACH k IN ARRAY ARRAY['schema','buildId','organizationId','projectId','mappingId','revisionId','nativeDigest','backendId','engineRevision','engineBuild','adapterVersion','profileId','profileVersion','runtime','runtimeVersion','artifactFormat','maximumArtifactBytes','environmentId','environmentVersion','approvalId','regressionEvidenceId'] LOOP
        result:=result||separator||chr(34)||k||chr(34)||':'; separator:=',';
        IF k IN ('profileVersion','maximumArtifactBytes','environmentVersion') THEN result:=result||((j->>k)::bigint)::text;
        ELSE result:=result||weft.build_json_text(j->>k,false); END IF;
    END LOOP;
    IF csv AND (jsonb_array_length(j->'boundaries')<>2 OR
        (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') x WHERE x->>'kind'='primaryInput' AND x->'name'='null'::jsonb)<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(j->'boundaries') x WHERE x->>'kind'='primaryOutput' AND x->'name'='null'::jsonb)<>1) THEN
        RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV build boundaries.';
    END IF;
    result:=result||',"boundaries":[';
    FOR b IN SELECT value FROM jsonb_array_elements(j->'boundaries') ORDER BY
        CASE value->>'kind' WHEN 'primaryInput' THEN 0 WHEN 'namedInput' THEN 1 WHEN 'primaryOutput' THEN 2 WHEN 'namedOutput' THEN 3 ELSE 4 END,
        weft.build_json_text(value->>'name',true) COLLATE "C" LOOP
        result:=result||boundary_separator||'{"kind":'||weft.build_json_text(b->>'kind',false)||',"name":'||weft.build_json_text(b->>'name',false)||',"format":'||weft.build_json_text(b->>'format',false);
        IF csv THEN
            IF b->>'format' NOT IN ('csv','tsv') OR NOT weft.csv_dialect_valid(b->'csvDialect',
                CASE b->>'format' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' END) THEN
                RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Invalid CSV build dialect.';
            END IF;
            result:=result||',"csvDialect":'||weft.csv_dialect_canonical(b->'csvDialect');
        ELSIF b ? 'csvDialect' THEN
            RAISE EXCEPTION USING ERRCODE='22023', MESSAGE='Unexpected CSV build dialect.';
        END IF;
        result:=result||'}'; boundary_separator:=',';
    END LOOP;
    RETURN result||']}';
END;
$body$;

-- Byte equality remains mandatory for build evidence; row comparison never qualifies a build.
CREATE OR REPLACE FUNCTION weft.build_evidence_passes(org uuid, run_key uuid, actor uuid, native_sha text, backend text, engine text, build text, adapter text) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r record; header jsonb; result jsonb; expected jsonb; actual jsonb; raw bytea; h bigint; seen text[]:=ARRAY[]::text[]; k text; documents boolean; csv boolean;
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
    documents:=header->>'schema'='weft.document-regression-case.v1';
    csv:=header->>'schema'='weft.csv-regression-case.v1';
    IF documents IS NULL OR csv IS NULL OR (documents AND (header->'runtime'->>'profile' IS DISTINCT FROM 'weft.ferrule-document-preview.v1'
            OR header->'comparisonOptions' IS DISTINCT FROM 'null'::jsonb))
        OR (csv AND (header->'runtime'->>'profile' IS DISTINCT FROM 'weft.ferrule-csv-preview.v1'
            OR header->'target' IS DISTINCT FROM '{"kind":"all","name":null}'::jsonb
            OR jsonb_array_length(header->'inputs')<>1 OR header->'inputs'->0->'present' IS DISTINCT FROM 'true'::jsonb
            OR NOT weft.csv_dialect_valid(header->'inputs'->0->'csvDialect',CASE WHEN header->'inputs'->0->'csvDialect'->>'delimiter'=chr(9) THEN 'text/tab-separated-values' ELSE 'text/csv' END)))
        OR (NOT documents AND NOT csv AND (header->>'schema' IS DISTINCT FROM 'weft.regression-case.v1'
            OR header->'runtime'->>'profile' IS DISTINCT FROM 'weft.ferrule-json-preview.v1'))
        OR header->>'comparisonPolicy' IS DISTINCT FROM 'weft.regression.exact-bytes.v1' OR header->'runtime'->>'backendId' IS DISTINCT FROM backend
        OR header->'runtime'->>'engineRevision' IS DISTINCT FROM engine OR header->'runtime'->>'backendBuild' IS DISTINCT FROM build
        OR header->'runtime'->>'adapterBuild' IS DISTINCT FROM adapter
        OR result->>'schema' IS DISTINCT FROM (CASE WHEN csv THEN 'weft.csv-regression-run-result.v1' ELSE 'weft.regression-run-result.v1' END)
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
        IF (documents AND coalesce(expected->>'mediaType','') NOT IN ('application/json','application/edi-x12','application/edifact'))
            OR (csv AND (expected->'name' IS DISTINCT FROM 'null'::jsonb OR NOT weft.csv_dialect_valid(expected->'csvDialect',expected->>'mediaType')
                OR actual->'csvDialect' IS DISTINCT FROM expected->'csvDialect'))
            OR (NOT csv AND (expected ? 'csvDialect' OR actual ? 'csvDialect'))
            OR (NOT documents AND NOT csv AND expected ? 'mediaType')
            OR coalesce(actual->>'mediaType','application/json') IS DISTINCT FROM coalesce(expected->>'mediaType','application/json')
            OR jsonb_typeof(actual->'content') IS DISTINCT FROM 'string' OR (actual->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE THEN RETURN false; END IF;
        raw:=decode(translate(actual->>'content','-_','+/')||repeat('=',(4-length(actual->>'content')%4)%4),'base64');
        IF raw IS NULL OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256' THEN RETURN false; END IF;
    END LOOP;
    RETURN true;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;

CREATE OR REPLACE FUNCTION weft.qualification_valid(org uuid, project uuid, mapping uuid, key uuid, actor uuid, proof bytea, completion bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE q jsonb; b record; r record; h bigint; header_raw json; header jsonb; result jsonb; item record; expected jsonb;
    replay text; canonical text; separator text:=''; raw bytea; total bigint:=0; boundary_count integer;
    intent jsonb; documents boolean; csv boolean; replay_inputs text; media text; boundary jsonb;
BEGIN
    IF octet_length(proof) NOT BETWEEN 1 AND 16384 OR octet_length(completion) NOT BETWEEN 1 AND 12582912 THEN RETURN false; END IF;
    q:=convert_from(proof,'UTF8')::jsonb;
    IF convert_to(weft.qualification_canonical(q),'UTF8') IS DISTINCT FROM proof
        OR q->>'schema' IS DISTINCT FROM 'weft.build-qualification.v1' OR q->>'policy' IS DISTINCT FROM 'weft.regression.exact-bytes.v1'
        OR q->>'qualificationId' IS DISTINCT FROM key::text OR q->>'actorId' IS DISTINCT FROM actor::text
        OR (q->>'runtimeImage'~'^sha256:[0-9a-f]{64}$') IS NOT TRUE
        OR q->>'resultDigest' IS DISTINCT FROM encode(sha256(completion),'hex')
        OR (q->>'resultBytes')::bigint IS DISTINCT FROM octet_length(completion) THEN RETURN false; END IF;
    SELECT x.*,a.mapping_id,a.revision_id,a.evidence_id,a.evidence_digest,a.native_digest,p.state publication_state,
        p.artifact_digest publication_digest,octet_length(p.artifact_body) publication_bytes
        INTO b FROM ONLY weft.build_runs x
        JOIN ONLY weft.build_approvals a ON a.organization_id=x.organization_id AND a.build_id=x.build_id AND a.approval_id=x.approval_id
        JOIN ONLY weft.build_publications p ON p.organization_id=x.organization_id AND p.build_id=x.build_id AND p.approval_id=x.approval_id
        WHERE x.organization_id=org AND x.project_id=project AND x.build_id=(q->>'buildId')::uuid
            AND x.actor_id=actor AND a.actor_id=actor AND a.mapping_id=mapping AND a.intent_body=x.intent_body;
    IF NOT FOUND OR b.state<>'succeeded' OR b.outcome NOT IN (0,1) OR b.attempt NOT BETWEEN 1 AND 3
        OR b.publication_state<>2 OR b.publication_digest IS DISTINCT FROM b.artifact_digest OR b.publication_bytes IS DISTINCT FROM b.artifact_bytes
        OR q->>'intentDigest' IS DISTINCT FROM b.intent_digest OR q->>'artifactDigest' IS DISTINCT FROM b.artifact_digest
        OR (q->>'artifactBytes')::bigint IS DISTINCT FROM b.artifact_bytes OR q->>'evidenceId' IS DISTINCT FROM b.evidence_id::text
        OR q->>'evidenceDigest' IS DISTINCT FROM b.evidence_digest THEN RETURN false; END IF;
    intent:=convert_from(b.intent_body,'UTF8')::jsonb;
    csv:=intent->>'profileId'='weft.ferrule-csharp-csv.v1';
    documents:=intent->>'profileId' IN ('weft.ferrule-csharp-document.v1','weft.ferrule-csharp-x12-interchanges.v1');
    IF intent->>'profileId'='weft.ferrule-csharp-x12-interchanges.v1' AND NOT EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'format'='x12') THEN RETURN false; END IF;
    SELECT x.*,c.envelope,c.creator_id case_owner INTO r FROM ONLY weft.regression_runs x
        JOIN ONLY weft.regression_case_versions c ON c.organization_id=x.organization_id AND c.project_id=x.project_id
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
        OR header->'runtime'->>'profile' IS DISTINCT FROM (CASE WHEN csv THEN 'weft.ferrule-csv-preview.v1' WHEN documents THEN 'weft.ferrule-document-preview.v1' ELSE 'weft.ferrule-json-preview.v1' END)
        OR NOT weft.build_evidence_passes(org,b.evidence_id,actor,b.native_digest,
            intent->>'backendId',intent->>'engineRevision',intent->>'engineBuild',intent->>'adapterVersion') THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind' IN ('primaryOutput','namedOutput');
    IF boundary_count<>jsonb_array_length(header->'expected') OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e
        WHERE e->>'kind' IN ('primaryOutput','namedOutput') AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'expected') x WHERE x->'name'=e->'name'
                AND (NOT documents AND NOT csv OR coalesce(x->>'mediaType','application/json')=CASE e->>'format' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' END)
                AND (NOT csv OR x->'csvDialect'=e->'csvDialect'))) THEN RETURN false; END IF;
    SELECT count(*) INTO boundary_count FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput';
    IF boundary_count<>jsonb_array_length(header->'inputs')-1 OR EXISTS(
        SELECT 1 FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='namedInput' AND NOT EXISTS(
            SELECT 1 FROM jsonb_array_elements(header->'inputs') x WHERE x->'name'=e->'name')) THEN RETURN false; END IF;
    IF csv AND (jsonb_array_length(intent->'boundaries')<>2 OR jsonb_array_length(header->'inputs')<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='primaryInput')<>1 OR
        (SELECT count(*) FROM jsonb_array_elements(intent->'boundaries') e WHERE e->>'kind'='primaryOutput')<>1) THEN RETURN false; END IF;
    replay_inputs:=(header_raw->'inputs')::text;
    IF documents OR csv THEN
        replay_inputs:='['; separator:='';
        FOR item IN SELECT value FROM json_array_elements(header_raw->'inputs') LOOP
            SELECT e INTO boundary FROM jsonb_array_elements(intent->'boundaries') e
                WHERE e->'name'=item.value::jsonb->'name' AND e->>'kind' IN ('primaryInput','namedInput');
            media:=CASE boundary->>'format' WHEN 'json' THEN 'application/json' WHEN 'x12' THEN 'application/edi-x12' WHEN 'edifact' THEN 'application/edifact' WHEN 'csv' THEN 'text/csv' WHEN 'tsv' THEN 'text/tab-separated-values' END;
            IF media IS NULL THEN RETURN false; END IF;
            IF csv THEN
                IF item.value::jsonb->'present' IS DISTINCT FROM 'true'::jsonb OR item.value::jsonb->'csvDialect' IS DISTINCT FROM boundary->'csvDialect'
                    OR NOT weft.csv_dialect_valid(boundary->'csvDialect',media) THEN RETURN false; END IF;
                replay_inputs:=replay_inputs||separator||'{"name":'||weft.build_json_text(item.value->>'name',false)
                    ||',"byteCount":'||(item.value->>'byteCount')||',"sha256":'||weft.build_json_text(item.value->>'sha256',false)
                    ||',"mediaType":'||weft.build_json_text(media,false)||',"csvDialect":'||weft.csv_dialect_canonical(boundary->'csvDialect')||'}';
            ELSE
                replay_inputs:=replay_inputs||separator||left(item.value::text,length(item.value::text)-1)||',"mediaType":'||weft.build_json_text(media,false)||'}';
            END IF;
            separator:=',';
        END LOOP;
        replay_inputs:=replay_inputs||']'; separator:='';
    END IF;
    replay:='{"target":"all","targetName":null,"currentDateTime":'||(header_raw->'currentDateTime')::text||',"limits":'||(header_raw->'limits')::text
        ||',"inputs":'||replay_inputs||',"parameters":'||(header_raw->'parameters')::text||'}';
    IF q->>'replayDigest' IS DISTINCT FROM encode(sha256(convert_to(replay,'UTF8')),'hex') THEN RETURN false; END IF;
    result:=convert_from(completion,'UTF8')::jsonb;
    IF result->>'schema' IS DISTINCT FROM (CASE WHEN csv THEN 'weft.csv-regression-run-result.v1' ELSE 'weft.regression-run-result.v1' END) OR result->>'runId' IS DISTINCT FROM key::text
        OR result->>'caseDigest' IS DISTINCT FROM r.case_digest OR result->>'outcome' IS DISTINCT FROM '0' OR result->>'previewStatus' IS DISTINCT FROM '0'
        OR jsonb_typeof(result->'outputs') IS DISTINCT FROM 'array' OR jsonb_array_length(result->'outputs')<>jsonb_array_length(header->'expected')
        OR jsonb_array_length(result->'outputs') NOT BETWEEN 1 AND (header->'limits'->>'maximumArtifacts')::integer THEN RETURN false; END IF;
    canonical:='{"schema":'||weft.build_json_text(result->>'schema',false)||',"runId":"'||key::text||'","caseDigest":"'||r.case_digest||'","outcome":0,"previewStatus":0,"outputs":[';
    FOR item IN SELECT value,ordinality FROM jsonb_array_elements(result->'outputs') WITH ORDINALITY LOOP
        expected:=header->'expected'->(item.ordinality::integer-1);
        IF item.value->'name' IS DISTINCT FROM expected->'name' OR jsonb_typeof(item.value->'content') IS DISTINCT FROM 'string'
            OR (item.value->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE
            OR coalesce(item.value->>'mediaType','application/json') IS DISTINCT FROM coalesce(expected->>'mediaType','application/json')
            OR csv AND item.value->'csvDialect' IS DISTINCT FROM expected->'csvDialect'
            OR NOT csv AND item.value ? 'csvDialect' THEN RETURN false; END IF;
        raw:=decode(translate(item.value->>'content','-_','+/')||repeat('=',(4-length(item.value->>'content')%4)%4),'base64');
        total:=total+octet_length(raw);
        IF total>(header->'limits'->>'maximumOutputBytes')::bigint OR octet_length(raw) IS DISTINCT FROM (expected->>'byteCount')::integer
            OR encode(sha256(raw),'hex') IS DISTINCT FROM expected->>'sha256'
            OR translate(rtrim(replace(encode(raw,'base64'),chr(10),''),'='),'+/','-_') IS DISTINCT FROM item.value->>'content' THEN RETURN false; END IF;
        canonical:=canonical||separator||'{"name":'||weft.build_json_text(item.value->>'name',false)||',"content":"'||(item.value->>'content')||'"'; separator:=',';
        IF expected->>'mediaType' IN ('application/edi-x12','application/edifact','text/csv','text/tab-separated-values') THEN canonical:=canonical||',"mediaType":'||weft.build_json_text(expected->>'mediaType',false); END IF;
        IF csv THEN canonical:=canonical||',"csvDialect":'||weft.csv_dialect_canonical(expected->'csvDialect'); END IF;
        canonical:=canonical||'}';
    END LOOP;
    RETURN convert_to(canonical||']}', 'UTF8')=completion;
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
