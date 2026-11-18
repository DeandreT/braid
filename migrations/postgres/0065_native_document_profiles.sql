-- Saved native document profiles. Existing JSON, EDI, CSV, XML and draft rows keep their encoding.
CREATE FUNCTION braid.native_document_key(p_profile text) RETURNS text
LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT CASE p_profile WHEN 'braid.ferrule-flextext-preview.v1' THEN 'flextext' WHEN 'braid.ferrule-xlsx-preview.v1' THEN 'xlsx' WHEN 'braid.ferrule-protobuf-preview.v1' THEN 'protobuf' WHEN 'braid.ferrule-xbrl-preview.v1' THEN 'xbrl' END
$body$;
REVOKE ALL ON FUNCTION braid.native_document_key(text) FROM PUBLIC;

CREATE FUNCTION braid.native_document_media(p_profile text) RETURNS text
LANGUAGE sql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT CASE braid.native_document_key(p_profile) WHEN 'flextext' THEN 'text/plain' WHEN 'xlsx' THEN 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' WHEN 'protobuf' THEN 'application/x-protobuf' WHEN 'xbrl' THEN 'application/xbrl+xml' END
$body$;
REVOKE ALL ON FUNCTION braid.native_document_media(text) FROM PUBLIC;

CREATE FUNCTION braid.native_settings_valid(value jsonb, p_profile text) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    RETURN coalesce(braid.native_document_key(p_profile) IS NOT NULL AND jsonb_typeof(value)='object'
        AND value->>'schema'='braid.'||braid.native_document_key(p_profile)||'-settings.v1'
        AND (SELECT count(*)=2 FROM jsonb_object_keys(value))
        AND jsonb_typeof(value->'optionsJson')='string' AND octet_length(value->>'optionsJson') BETWEEN 2 AND 16384
        AND jsonb_typeof((value->>'optionsJson')::jsonb)='object'
        AND braid.document_json_unique((value->>'optionsJson')::json),false);
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;
REVOKE ALL ON FUNCTION braid.native_settings_valid(jsonb,text) FROM PUBLIC;

ALTER TABLE braid.preview_runs ADD COLUMN native_profile text
    CHECK (native_profile IS NULL OR braid.native_document_key(native_profile) IS NOT NULL);
ALTER TABLE braid.preview_documents ADD COLUMN native_settings jsonb;
ALTER TABLE braid.preview_runs ADD CONSTRAINT preview_runs_native_profile_distinct
    CHECK (native_profile IS NULL OR NOT csv_profile);
ALTER TABLE braid.preview_runs DROP CONSTRAINT preview_runs_inputs_check;
ALTER TABLE braid.preview_runs ADD CONSTRAINT preview_runs_inputs_check CHECK (
    (NOT csv_profile AND native_profile IS NULL AND input_count BETWEEN 1 AND 16 AND input_bytes BETWEEN input_count AND 8388608)
    OR ((csv_profile OR native_profile IS NOT NULL) AND draft_id IS NULL AND input_count=1 AND input_bytes BETWEEN 0 AND 8388608));
ALTER TABLE braid.preview_documents DROP CONSTRAINT preview_documents_kind_check;
ALTER TABLE braid.preview_documents ADD CONSTRAINT preview_documents_kind_check CHECK (
    (native_settings IS NOT NULL AND NOT csv_input AND csv_dialect IS NULL AND ordinal=0 AND media_type IS NOT NULL
        AND ((direction=1 AND name IS NULL AND target_name IS NULL AND target_name_truncated IS NULL AND records_written IS NULL)
        OR (direction=2 AND name='primary-output' AND target_name IS NOT NULL AND octet_length(target_name)<=2048 AND target_name_truncated IS NOT NULL AND records_written IS NOT NULL AND records_written>=0)))
    OR (native_settings IS NULL AND (
    (direction=1 AND (byte_count>0 OR csv_input) AND target_name IS NULL AND target_name_truncated IS NULL
        AND records_written IS NULL
        AND ((NOT csv_input AND media_type IS NULL AND csv_dialect IS NULL)
            OR (csv_input AND braid.csv_dialect_valid(csv_dialect,media_type)))
        AND (ordinal=0)=(name IS NULL) AND (name IS NULL OR octet_length(name) BETWEEN 1 AND 256)
        AND (NOT csv_input OR ordinal=0))
    OR (direction=2 AND NOT csv_input AND name ~ '^(primary-output|named-output:(0|[1-9][0-9]{0,19}))$'
        AND target_name IS NOT NULL AND octet_length(target_name)<=2048 AND target_name_truncated IS NOT NULL
        AND media_type IS NOT NULL AND records_written IS NOT NULL AND records_written>=0
        AND ((media_type IN ('application/json','application/edi-x12','application/edifact') AND csv_dialect IS NULL)
            OR (name='primary-output' AND ordinal=0 AND braid.csv_dialect_valid(csv_dialect,media_type))))
    -- Migration 0038's retained native checkpoint remains private, non-CSV and bounded.
    OR (direction=3 AND ordinal=0 AND name IS NULL AND target_name IS NULL
        AND target_name_truncated IS NULL AND media_type IS NULL AND records_written IS NULL
        AND byte_count BETWEEN 1 AND 4194304 AND NOT csv_input AND csv_dialect IS NULL))));

CREATE FUNCTION braid.guard_native_preview_document() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE v_profile text;
BEGIN
    SELECT native_profile INTO STRICT v_profile FROM ONLY braid.preview_runs WHERE organization_id=NEW.organization_id AND run_id=NEW.run_id;
    IF (v_profile IS NULL) IS DISTINCT FROM (NEW.native_settings IS NULL) OR (v_profile IS NOT NULL AND
        (NOT braid.native_settings_valid(NEW.native_settings,v_profile) OR NEW.media_type IS DISTINCT FROM braid.native_document_media(v_profile))) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Native document profile mismatch.';
    END IF;
    RETURN NEW;
END;
$body$;
REVOKE ALL ON FUNCTION braid.guard_native_preview_document() FROM PUBLIC;
CREATE TRIGGER guard_native_preview_document BEFORE INSERT OR UPDATE ON braid.preview_documents
    FOR EACH ROW EXECUTE FUNCTION braid.guard_native_preview_document();

CREATE FUNCTION braid.preview_submit_native(p_org uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_actor uuid,
    p_key uuid,p_request text,p_clock text,p_attempts integer,p_retention integer,p_names text[],p_contents bytea[])
RETURNS TABLE(outcome text,run_id uuid)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.mapping_revisions%ROWTYPE; existing braid.preview_runs%ROWTYPE;
    created uuid:=gen_random_uuid(); j jsonb; fingerprint text; settings jsonb; v_profile text; v_key text;
BEGIN
    PERFORM braid.lifecycle_gate(p_org,false);
    IF p_org IS NULL OR p_project IS NULL OR p_mapping IS NULL OR p_revision IS NULL OR p_actor IS NULL OR p_key IS NULL
        OR p_request IS NULL OR octet_length(p_request) NOT BETWEEN 2 AND 65536 OR p_clock IS NULL OR p_attempts IS NULL
        OR p_retention IS NULL OR p_attempts NOT BETWEEN 1 AND 8 OR p_retention NOT BETWEEN 60 AND 604800
        OR array_dims(p_names) IS DISTINCT FROM '[1:1]' OR array_dims(p_contents) IS DISTINCT FROM '[1:1]'
        OR p_names[1] IS NOT NULL OR p_contents[1] IS NULL OR octet_length(p_contents[1])>8388608 THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid native document submission.';
    END IF;
    IF NOT braid.preview_authorized(p_org,p_project,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    SELECT * INTO r FROM ONLY braid.mapping_revisions WHERE organization_id=p_org AND project_id=p_project AND mapping_id=p_mapping AND id=p_revision;
    IF NOT FOUND THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    IF NOT braid.document_json_unique(p_request::json) THEN RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Duplicate request fields.'; END IF;
    j:=p_request::jsonb; settings:=j->'inputs'->0->'documentSettings';
    v_key:=CASE j->>'schema' WHEN 'braid.flextext-preview-request.v1' THEN 'flextext' WHEN 'braid.xlsx-preview-request.v1' THEN 'xlsx' WHEN 'braid.protobuf-preview-request.v1' THEN 'protobuf' WHEN 'braid.xbrl-preview-request.v1' THEN 'xbrl' END;
    v_profile:='braid.ferrule-'||v_key||'-preview.v1';
    IF v_key IS NULL OR (v_key='xlsx' AND octet_length(p_contents[1])=0) OR jsonb_typeof(j) IS DISTINCT FROM 'object'
        OR (SELECT count(*) FROM jsonb_object_keys(j))<>9 OR j->>'organizationId' IS DISTINCT FROM p_org::text
        OR j->>'projectId' IS DISTINCT FROM p_project::text OR j->>'mappingId' IS DISTINCT FROM p_mapping::text
        OR j->>'revisionId' IS DISTINCT FROM p_revision::text OR (j->'target' IN ('{"kind":"all"}'::jsonb,'{"kind":"primary"}'::jsonb)) IS NOT TRUE
        OR NOT braid.native_settings_valid(settings,v_profile)
        OR j->'inputs' IS DISTINCT FROM jsonb_build_array(jsonb_build_object('name',NULL,'present',true,'byteLength',octet_length(p_contents[1]),
            'sha256',encode(sha256(p_contents[1]),'hex'),'documentSettings',settings)) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid native document request envelope.';
    END IF;
    IF jsonb_typeof(j->'parameters') IS DISTINCT FROM 'array' OR jsonb_array_length(j->'parameters')>32
        OR jsonb_typeof(j->'limits') IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(j->'limits'))<>4
        OR ((j->'limits'->>'maximumInputBytes')::integer BETWEEN greatest(octet_length(p_contents[1]),1) AND 8388608) IS NOT TRUE
        OR ((j->'limits'->>'maximumOutputBytes')::integer BETWEEN 1 AND 8388608) IS NOT TRUE
        OR ((j->'limits'->>'maximumArtifacts')::integer BETWEEN 1 AND 64) IS NOT TRUE
        OR ((j->'limits'->>'maximumTextBytes')::integer BETWEEN 64 AND 2048) IS NOT TRUE
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'parameters') p WHERE jsonb_typeof(p) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(p))<>3 OR jsonb_typeof(p->'name') IS DISTINCT FROM 'string'
            OR octet_length(p->>'name') NOT BETWEEN 1 AND 256 OR jsonb_typeof(p->'value') IS DISTINCT FROM 'string'
            OR octet_length(p->>'value')>4096 OR (p->>'kind' IN ('string','integer','float','boolean')) IS NOT TRUE)
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'parameters') p GROUP BY p->>'name' HAVING count(*)>1) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid native document request limits or parameters.';
    END IF;
    fingerprint:=encode(sha256(convert_to(p_request,'UTF8')),'hex');
    SELECT * INTO existing FROM ONLY braid.preview_runs WHERE organization_id=p_org AND submitter_id=p_actor AND request_key=p_key;
    IF FOUND THEN
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.revision_id=p_revision AND existing.request_fingerprint=fingerprint AND existing.native_profile=v_profile THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id;
        ELSE RETURN QUERY SELECT 'conflict'::text,NULL::uuid; END IF;
        RETURN;
    END IF;
    IF NOT braid.preview_authorized(p_org,p_project,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    INSERT INTO braid.preview_runs(organization_id,run_id,project_id,mapping_id,revision_id,submitter_id,request_key,request_document,request_fingerprint,
        current_date_time,native_object_id,native_digest,native_byte_count,input_count,input_bytes,max_attempts,expires_at,native_profile)
    VALUES(p_org,created,p_project,p_mapping,p_revision,p_actor,p_key,p_request,fingerprint,p_clock,r.native_object_id,r.native_digest,r.native_byte_count,
        1,octet_length(p_contents[1]),p_attempts,statement_timestamp()+make_interval(secs=>p_retention),v_profile)
    ON CONFLICT ON CONSTRAINT preview_runs_request_key DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO existing FROM ONLY braid.preview_runs WHERE organization_id=p_org AND submitter_id=p_actor AND request_key=p_key;
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.revision_id=p_revision AND existing.request_fingerprint=fingerprint
            AND existing.native_profile IS NOT DISTINCT FROM v_profile THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id; ELSE RETURN QUERY SELECT 'conflict'::text,NULL::uuid; END IF;
        RETURN;
    END IF;
    INSERT INTO braid.preview_documents(organization_id,run_id,direction,ordinal,digest,byte_count,content,media_type,native_settings)
    VALUES(p_org,created,1,0,encode(sha256(p_contents[1]),'hex'),octet_length(p_contents[1]),p_contents[1],braid.native_document_media(v_profile),settings);
    RETURN QUERY SELECT 'created'::text,created;
END;
$body$;

CREATE FUNCTION braid.preview_complete_native(p_org uuid,p_run uuid,p_token uuid,p_outcome text,p_code text,p_message text,p_truncated boolean,
    p_subject text,p_subject_truncated boolean,p_confirmed boolean,p_engine text,p_build text,p_adapter text,p_profile text,p_records bigint,
    p_boundaries text[],p_names text[],p_name_truncated boolean[],p_media text[],p_row_counts bigint[],p_contents bytea[],p_settings text[])
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.preview_runs%ROWTYPE; n integer:=coalesce(cardinality(p_contents),0);
BEGIN
    PERFORM braid.lifecycle_gate(p_org,false);
    PERFORM braid.lifecycle_settle_background(p_org,'preview',p_run);
    SELECT * INTO r FROM ONLY braid.preview_runs WHERE organization_id=p_org AND run_id=p_run FOR UPDATE;
    IF NOT FOUND OR r.state<>2 OR r.lease_token IS DISTINCT FROM p_token OR r.lease_expires_at<=clock_timestamp() THEN RETURN 'claimLost'; END IF;
    IF r.native_profile IS NULL OR p_outcome IS NULL OR (p_outcome='succeeded') IS DISTINCT FROM (n=1) OR n>1
        OR n<>coalesce(cardinality(p_boundaries),0) OR n<>coalesce(cardinality(p_names),0) OR n<>coalesce(cardinality(p_name_truncated),0)
        OR n<>coalesce(cardinality(p_media),0) OR n<>coalesce(cardinality(p_row_counts),0) OR n<>coalesce(cardinality(p_settings),0)
        OR (p_profile IS NOT NULL AND p_profile<>r.native_profile) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid native document completion.';
    END IF;
    IF r.cancel_requested THEN p_outcome:='canceled'; n:=0;
    ELSIF NOT braid.preview_authorized(p_org,r.project_id,r.submitter_id) THEN p_outcome:='authorityRevoked'; n:=0; END IF;
    IF n=1 THEN
        IF p_boundaries[1] IS DISTINCT FROM 'primary-output' OR p_media[1] IS DISTINCT FROM braid.native_document_media(r.native_profile)
            OR NOT braid.native_settings_valid(p_settings[1]::jsonb,r.native_profile) OR NOT braid.document_json_unique(p_settings[1]::json)
            OR p_contents[1] IS NULL OR p_names[1] IS NULL OR p_name_truncated[1] IS NULL
            OR p_row_counts[1] IS NULL OR p_row_counts[1]<0 OR p_records IS DISTINCT FROM p_row_counts[1]
            OR octet_length(p_contents[1])>(r.request_document::jsonb->'limits'->>'maximumOutputBytes')::integer
            OR p_confirmed IS DISTINCT FROM true
            OR p_profile IS DISTINCT FROM r.native_profile THEN
            RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid native document output.';
        END IF;
        INSERT INTO braid.preview_documents(organization_id,run_id,direction,ordinal,name,target_name,target_name_truncated,media_type,records_written,digest,byte_count,content,native_settings)
        VALUES(p_org,p_run,2,0,p_boundaries[1],p_names[1],p_name_truncated[1],p_media[1],p_row_counts[1],encode(sha256(p_contents[1]),'hex'),octet_length(p_contents[1]),p_contents[1],p_settings[1]::jsonb);
    END IF;
    UPDATE braid.preview_runs SET state=CASE WHEN p_outcome='canceled' THEN 4 ELSE 3 END,outcome=p_outcome,
        failure_code=CASE WHEN p_outcome IN ('canceled','authorityRevoked') THEN NULL ELSE p_code END,
        failure_message=CASE WHEN p_outcome IN ('canceled','authorityRevoked') THEN NULL ELSE p_message END,
        failure_message_truncated=CASE WHEN p_outcome IN ('canceled','authorityRevoked') THEN NULL ELSE p_truncated END,
        failure_subject=CASE WHEN p_outcome IN ('canceled','authorityRevoked') THEN NULL ELSE p_subject END,
        failure_subject_truncated=CASE WHEN p_outcome IN ('canceled','authorityRevoked') THEN NULL ELSE p_subject_truncated END,
        provenance_confirmed=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_confirmed END,
        engine_revision=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_engine END,backend_build=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_build END,
        adapter_build=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_adapter END,profile=CASE WHEN p_outcome='canceled' THEN NULL ELSE p_profile END,
        records_written=CASE WHEN n=1 THEN p_records END,output_count=CASE WHEN n=1 THEN 1 END,output_bytes=CASE WHEN n=1 THEN octet_length(p_contents[1]) END,
        lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,finished_at=statement_timestamp()
    WHERE organization_id=p_org AND run_id=p_run;
    RETURN 'completed';
END;
$body$;

CREATE FUNCTION braid.preview_native_metadata(p_org uuid,p_run uuid,p_actor uuid,p_direction integer,p_ordinal integer)
RETURNS TABLE(settings jsonb) LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT d.native_settings FROM ONLY braid.preview_documents d JOIN ONLY braid.preview_runs r
        ON r.organization_id=d.organization_id AND r.run_id=d.run_id
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.submitter_id=p_actor AND r.native_profile IS NOT NULL
        AND d.direction=p_direction AND d.ordinal=p_ordinal AND braid.preview_authorized(p_org,r.project_id,p_actor)
$body$;
REVOKE ALL ON FUNCTION braid.preview_submit_native(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    braid.preview_complete_native(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]),
    braid.preview_native_metadata(uuid,uuid,uuid,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.preview_submit_native(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    braid.preview_complete_native(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]) TO braid_preview_writer;
GRANT EXECUTE ON FUNCTION braid.preview_native_metadata(uuid,uuid,uuid,integer,integer) TO braid_preview_reader,braid_preview_writer;
