-- XML previews retain a separate private document envelope. Existing document rows,
-- checks, grants and draft checkpoint entry-point bodies are not rewritten.
CREATE FUNCTION braid.xml_metadata_valid(value jsonb, input_role boolean) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE hints jsonb; pair jsonb;
BEGIN
    IF jsonb_typeof(value) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(value))<>3
        OR value->>'schema' IS DISTINCT FROM 'braid.xml-boundary.v1'
        OR value->>'safetyProfile' IS DISTINCT FROM 'braid.xml-safe.v1' OR NOT value ? 'schemaHints' THEN RETURN false; END IF;
    hints:=value->'schemaHints';
    IF hints='null'::jsonb THEN RETURN true; END IF;
    IF input_role OR jsonb_typeof(hints) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(hints))<>2
        OR NOT hints ? 'noNamespaceLocation' OR jsonb_typeof(hints->'locations') IS DISTINCT FROM 'array'
        OR jsonb_array_length(hints->'locations')>32 THEN RETURN false; END IF;
    IF hints->'noNamespaceLocation'<>'null'::jsonb AND (jsonb_typeof(hints->'noNamespaceLocation')<>'string'
        OR octet_length(hints->>'noNamespaceLocation') NOT BETWEEN 1 AND 4096
        OR hints->>'noNamespaceLocation' ~ '[\x01-\x20]') THEN RETURN false; END IF;
    IF hints->'noNamespaceLocation'='null'::jsonb AND jsonb_array_length(hints->'locations')=0 THEN RETURN false; END IF;
    FOR pair IN SELECT * FROM jsonb_array_elements(hints->'locations') LOOP
        IF jsonb_typeof(pair) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(pair))<>2
            OR jsonb_typeof(pair->'namespace') IS DISTINCT FROM 'string' OR jsonb_typeof(pair->'location') IS DISTINCT FROM 'string'
            OR octet_length(pair->>'namespace') NOT BETWEEN 1 AND 4096 OR octet_length(pair->>'location') NOT BETWEEN 1 AND 4096
            OR pair->>'namespace' ~ '[\x01-\x20]' OR pair->>'location' ~ '[\x01-\x20]' THEN RETURN false; END IF;
    END LOOP;
    RETURN NOT EXISTS(SELECT 1 FROM jsonb_array_elements(hints->'locations') p GROUP BY p->>'namespace' HAVING count(*)>1);
EXCEPTION WHEN data_exception THEN RETURN false;
END;
$body$;

CREATE TABLE braid.xml_preview_documents (
    organization_id uuid NOT NULL,
    run_id uuid NOT NULL,
    direction integer NOT NULL,
    ordinal integer NOT NULL,
    name text,
    target_name text,
    target_name_truncated boolean,
    records_written bigint,
    metadata text NOT NULL,
    content bytea NOT NULL,
    digest text NOT NULL,
    byte_count integer NOT NULL,
    metadata_digest text NOT NULL,
    envelope_digest text NOT NULL,
    CONSTRAINT xml_preview_documents_pkey PRIMARY KEY(organization_id,run_id,direction,ordinal),
    CONSTRAINT xml_preview_documents_run_fkey FOREIGN KEY(organization_id,run_id)
        REFERENCES braid.preview_runs(organization_id,run_id) ON DELETE CASCADE
);
ALTER TABLE braid.xml_preview_documents ENABLE ROW LEVEL SECURITY;
CREATE POLICY xml_preview_documents_organization_scope ON braid.xml_preview_documents
    USING (organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid)
    WITH CHECK (organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid);
REVOKE ALL ON braid.xml_preview_documents FROM PUBLIC;

CREATE FUNCTION braid.guard_xml_preview_document() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF TG_OP<>'INSERT' OR NEW.direction NOT IN (1,2) OR NEW.ordinal NOT BETWEEN 0 AND (CASE WHEN NEW.direction=1 THEN 15 ELSE 63 END)
        OR octet_length(NEW.content) NOT BETWEEN 1 AND 8388608 OR octet_length(NEW.metadata)+15>32768
        OR NEW.digest IS DISTINCT FROM encode(sha256(NEW.content),'hex') OR NEW.byte_count IS DISTINCT FROM octet_length(NEW.content)
        OR NEW.metadata_digest IS DISTINCT FROM encode(sha256(convert_to(NEW.metadata,'UTF8')),'hex')
        OR NOT braid.document_json_unique(NEW.metadata::json) OR NOT braid.xml_metadata_valid(NEW.metadata::jsonb,NEW.direction=1)
        OR NOT EXISTS(SELECT 1 FROM ONLY braid.preview_runs r WHERE r.organization_id=NEW.organization_id AND r.run_id=NEW.run_id
            AND r.request_document::jsonb->>'schema'='braid.xml-preview-request.v1' AND NOT r.csv_profile AND r.draft_id IS NULL)
        OR (NEW.direction=1 AND ((NEW.ordinal=0) IS DISTINCT FROM (NEW.name IS NULL) OR NEW.target_name IS NOT NULL
            OR NEW.target_name_truncated IS NOT NULL OR NEW.records_written IS NOT NULL
            OR (NEW.name IS NOT NULL AND octet_length(NEW.name) NOT BETWEEN 1 AND 256)))
        OR (NEW.direction=2 AND (NEW.name IS NULL OR NEW.name !~ '^(primary-output|named-output:(0|[1-9][0-9]{0,19}))$'
            OR NEW.target_name IS NULL OR octet_length(NEW.target_name)>2048 OR NEW.target_name_truncated IS NULL
            OR NEW.records_written IS NULL OR NEW.records_written<0)) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview document envelope.';
    END IF;
    NEW.envelope_digest:=encode(sha256(convert_to(jsonb_build_array('braid.xml-preview-document.v1',NEW.organization_id,NEW.run_id,
        NEW.direction,NEW.ordinal,NEW.name,NEW.target_name,NEW.target_name_truncated,NEW.records_written,NEW.metadata,NEW.digest,NEW.byte_count)::text,'UTF8')),'hex');
    RETURN NEW;
END;
$body$;
CREATE TRIGGER guard_xml_preview_document BEFORE INSERT OR UPDATE ON braid.xml_preview_documents
    FOR EACH ROW EXECUTE FUNCTION braid.guard_xml_preview_document();

CREATE FUNCTION braid.preview_submit_xml(p_org uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_actor uuid,
    p_key uuid,p_request text,p_clock text,p_attempts integer,p_retention integer,p_names text[],p_contents bytea[])
RETURNS TABLE(outcome text, run_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.mapping_revisions%ROWTYPE; existing braid.preview_runs%ROWTYPE;
    created uuid:=gen_random_uuid(); j jsonb; fingerprint text; n integer:=cardinality(p_contents); total bigint; i integer;
BEGIN
    IF p_org IS NULL OR p_project IS NULL OR p_mapping IS NULL OR p_revision IS NULL OR p_actor IS NULL OR p_key IS NULL
        OR p_request IS NULL OR octet_length(p_request) NOT BETWEEN 2 AND 65536 OR p_clock IS NULL OR p_attempts IS NULL
        OR p_retention IS NULL OR p_attempts NOT BETWEEN 1 AND 8 OR p_retention NOT BETWEEN 60 AND 604800
        OR n IS NULL OR n NOT BETWEEN 1 AND 16 OR cardinality(p_names) IS DISTINCT FROM n OR p_names[1] IS NOT NULL
        OR array_lower(p_names,1)<>1 OR array_lower(p_contents,1)<>1 OR array_ndims(p_names)<>1 OR array_ndims(p_contents)<>1
        OR EXISTS(SELECT 1 FROM unnest(p_contents) b WHERE b IS NULL OR octet_length(b)=0) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview submission.';
    END IF;
    IF NOT braid.preview_authorized(p_org,p_project,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    SELECT * INTO r FROM ONLY braid.mapping_revisions WHERE organization_id=p_org AND project_id=p_project AND mapping_id=p_mapping AND id=p_revision;
    IF NOT FOUND THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    IF NOT braid.document_json_unique(p_request::json) THEN RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Duplicate XML preview fields.'; END IF;
    j:=p_request::jsonb;
    SELECT sum(octet_length(b)) INTO total FROM unnest(p_contents) b;
    IF j->>'schema' IS DISTINCT FROM 'braid.xml-preview-request.v1' OR jsonb_typeof(j) IS DISTINCT FROM 'object'
        OR (SELECT count(*) FROM jsonb_object_keys(j))<>9 OR j->>'organizationId' IS DISTINCT FROM p_org::text
        OR j->>'projectId' IS DISTINCT FROM p_project::text OR j->>'mappingId' IS DISTINCT FROM p_mapping::text
        OR j->>'revisionId' IS DISTINCT FROM p_revision::text OR jsonb_typeof(j->'inputs') IS DISTINCT FROM 'array'
        OR jsonb_array_length(j->'inputs')<>n OR total>8388608
        OR ((j->'target' IN ('{"kind":"all"}'::jsonb,'{"kind":"primary"}'::jsonb)) IS NOT TRUE AND
            (jsonb_typeof(j->'target') IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(j->'target'))<>2
                OR j->'target'->>'kind' IS DISTINCT FROM 'named' OR jsonb_typeof(j->'target'->'name') IS DISTINCT FROM 'string'
                OR octet_length(j->'target'->>'name') NOT BETWEEN 1 AND 256)) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview request envelope.';
    END IF;
    FOR i IN 1..n LOOP
        IF NOT braid.xml_metadata_valid(j->'inputs'->(i-1)->'xmlMetadata',true)
            OR j->'inputs'->(i-1) IS DISTINCT FROM jsonb_build_object('name',p_names[i],'byteLength',octet_length(p_contents[i]),
                'sha256',encode(sha256(p_contents[i]),'hex'),'xmlMetadata',j->'inputs'->(i-1)->'xmlMetadata')
            OR (i>1 AND (p_names[i] IS NULL OR octet_length(p_names[i]) NOT BETWEEN 1 AND 256)) THEN
            RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview input declaration.';
        END IF;
    END LOOP;
    IF (SELECT sum(octet_length((d->'xmlMetadata')::text)+15)>32768 FROM json_array_elements(p_request::json->'inputs') d)
        OR EXISTS(SELECT 1 FROM unnest(p_names) x WHERE x IS NOT NULL GROUP BY x HAVING count(*)>1)
        OR jsonb_typeof(j->'parameters') IS DISTINCT FROM 'array' OR jsonb_array_length(j->'parameters')>32
        OR jsonb_typeof(j->'limits') IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(j->'limits'))<>4
        OR ((j->'limits'->>'maximumInputBytes')::integer BETWEEN total AND 8388608) IS NOT TRUE
        OR ((j->'limits'->>'maximumOutputBytes')::integer BETWEEN 1 AND 8388608) IS NOT TRUE
        OR ((j->'limits'->>'maximumArtifacts')::integer BETWEEN 1 AND 64) IS NOT TRUE
        OR ((j->'limits'->>'maximumTextBytes')::integer BETWEEN 64 AND 2048) IS NOT TRUE
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'parameters') p WHERE jsonb_typeof(p) IS DISTINCT FROM 'object'
            OR (SELECT count(*) FROM jsonb_object_keys(p))<>3 OR jsonb_typeof(p->'name') IS DISTINCT FROM 'string'
            OR octet_length(p->>'name') NOT BETWEEN 1 AND 256 OR jsonb_typeof(p->'value') IS DISTINCT FROM 'string'
            OR octet_length(p->>'value')>4096 OR (p->>'kind' IN ('string','integer','float','boolean')) IS NOT TRUE
            OR (p->>'kind'='boolean' AND (p->>'value' IN ('true','false')) IS NOT TRUE))
        OR EXISTS(SELECT 1 FROM jsonb_array_elements(j->'parameters') p GROUP BY p->>'name' HAVING count(*)>1) THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview limits or parameters.';
    END IF;
    fingerprint:=encode(sha256(convert_to(p_request,'UTF8')),'hex');
    SELECT * INTO existing FROM ONLY braid.preview_runs WHERE organization_id=p_org AND submitter_id=p_actor AND request_key=p_key;
    IF FOUND THEN
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.revision_id=p_revision AND existing.request_fingerprint=fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id; ELSE RETURN QUERY SELECT 'conflict'::text,NULL::uuid; END IF;
        RETURN;
    END IF;
    -- The base has no archive lifecycle: current preview authority and the exact
    -- saved revision above are the submission fence, repeated before the write.
    IF NOT braid.preview_authorized(p_org,p_project,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    INSERT INTO braid.preview_runs(organization_id,run_id,project_id,mapping_id,revision_id,submitter_id,request_key,request_document,request_fingerprint,
        current_date_time,native_object_id,native_digest,native_byte_count,input_count,input_bytes,max_attempts,expires_at)
    VALUES(p_org,created,p_project,p_mapping,p_revision,p_actor,p_key,p_request,fingerprint,p_clock,r.native_object_id,r.native_digest,r.native_byte_count,
        n,total,p_attempts,clock_timestamp()+make_interval(secs=>p_retention))
    ON CONFLICT ON CONSTRAINT preview_runs_request_key DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO existing FROM ONLY braid.preview_runs WHERE organization_id=p_org AND submitter_id=p_actor AND request_key=p_key;
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.revision_id=p_revision AND existing.request_fingerprint=fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id; ELSE RETURN QUERY SELECT 'conflict'::text,NULL::uuid; END IF;
        RETURN;
    END IF;
    FOR i IN 1..n LOOP
        INSERT INTO braid.xml_preview_documents(organization_id,run_id,direction,ordinal,name,metadata,content,digest,byte_count,metadata_digest)
        VALUES(p_org,created,1,i-1,p_names[i],(p_request::json->'inputs'->(i-1)->'xmlMetadata')::text,p_contents[i],
            encode(sha256(p_contents[i]),'hex'),octet_length(p_contents[i]),encode(sha256(convert_to((p_request::json->'inputs'->(i-1)->'xmlMetadata')::text,'UTF8')),'hex'));
    END LOOP;
    RETURN QUERY SELECT 'created'::text,created;
END;
$body$;

CREATE FUNCTION braid.preview_complete_xml(p_org uuid,p_run uuid,p_token uuid,p_outcome text,p_code text,p_message text,p_truncated boolean,
    p_subject text,p_subject_truncated boolean,p_confirmed boolean,p_engine text,p_build text,p_adapter text,p_profile text,p_records bigint,
    p_boundaries text[],p_names text[],p_name_truncated boolean[],p_media text[],p_row_counts bigint[],p_contents bytea[],p_metadata text[])
RETURNS text LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE r braid.preview_runs%ROWTYPE; n integer:=coalesce(cardinality(p_contents),0); total bigint; i integer;
BEGIN
    SELECT * INTO r FROM ONLY braid.preview_runs WHERE organization_id=p_org AND run_id=p_run FOR UPDATE;
    -- Validate after acquiring the row lock, so cancellation, a replacement claim
    -- and competing completion cannot publish a second output set.
    IF NOT FOUND OR r.state<>2 OR r.lease_token IS DISTINCT FROM p_token OR r.lease_expires_at<=clock_timestamp() THEN RETURN 'claimLost'; END IF;
    IF r.request_document::jsonb->>'schema' IS DISTINCT FROM 'braid.xml-preview-request.v1' OR p_outcome IS NULL
        OR (p_outcome='succeeded') IS DISTINCT FROM (n>0) OR n>64
        OR n<>coalesce(cardinality(p_boundaries),0) OR n<>coalesce(cardinality(p_names),0) OR n<>coalesce(cardinality(p_name_truncated),0)
        OR n<>coalesce(cardinality(p_media),0) OR n<>coalesce(cardinality(p_row_counts),0) OR n<>coalesce(cardinality(p_metadata),0)
        OR (p_profile IS NOT NULL AND p_profile<>'braid.ferrule-xml-preview.v1') THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview completion.';
    END IF;
    IF r.cancel_requested THEN p_outcome:='canceled'; n:=0;
    ELSIF NOT braid.preview_subject_authorized(p_org,p_run) THEN p_outcome:='authorityRevoked'; n:=0; END IF;
    IF n>0 THEN
        SELECT sum(octet_length(b)) INTO total FROM unnest(p_contents) b;
        IF total>(r.request_document::jsonb->'limits'->>'maximumOutputBytes')::integer
            OR n>(r.request_document::jsonb->'limits'->>'maximumArtifacts')::integer OR p_confirmed IS DISTINCT FROM true
            OR p_profile IS DISTINCT FROM 'braid.ferrule-xml-preview.v1'
            OR (SELECT sum(octet_length(m)+15) FROM unnest(p_metadata) m)+
                (SELECT coalesce(sum(octet_length(d.metadata)+15),0) FROM ONLY braid.xml_preview_documents d WHERE d.organization_id=p_org AND d.run_id=p_run AND d.direction=1)>32768
            OR EXISTS(SELECT 1 FROM unnest(p_boundaries) b GROUP BY b HAVING count(*)>1)
            OR ((r.request_document::jsonb->'target'->>'kind')='primary' AND (n<>1 OR p_boundaries[1] IS DISTINCT FROM 'primary-output'))
            OR ((r.request_document::jsonb->'target'->>'kind')='all' AND p_boundaries[1] IS DISTINCT FROM 'primary-output')
            OR ((r.request_document::jsonb->'target'->>'kind')='named' AND (n<>1 OR p_boundaries[1]='primary-output')) THEN
            RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview output set.';
        END IF;
        FOR i IN 1..n LOOP
            IF p_media[i] IS DISTINCT FROM 'application/xml' OR p_metadata[i] IS NULL OR p_contents[i] IS NULL
                OR (i>1 AND (p_boundaries[i] !~ '^named-output:(0|[1-9][0-9]{0,19})$'
                    OR (i>2 AND substring(p_boundaries[i] from 14)::numeric<=substring(p_boundaries[i-1] from 14)::numeric))) THEN
                RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid XML preview output.';
            END IF;
            INSERT INTO braid.xml_preview_documents(organization_id,run_id,direction,ordinal,name,target_name,target_name_truncated,records_written,metadata,content,digest,byte_count,metadata_digest)
            VALUES(p_org,p_run,2,i-1,p_boundaries[i],p_names[i],p_name_truncated[i],p_row_counts[i],p_metadata[i],p_contents[i],
                encode(sha256(p_contents[i]),'hex'),octet_length(p_contents[i]),encode(sha256(convert_to(p_metadata[i],'UTF8')),'hex'));
        END LOOP;
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
        records_written=CASE WHEN n>0 THEN p_records END,output_count=CASE WHEN n>0 THEN n END,output_bytes=CASE WHEN n>0 THEN total END,
        lease_token=NULL,lease_owner=NULL,lease_expires_at=NULL,finished_at=clock_timestamp()
    WHERE organization_id=p_org AND run_id=p_run;
    RETURN 'completed';
END;
$body$;

CREATE FUNCTION braid.preview_xml_inputs(p_org uuid,p_run uuid,p_token uuid)
RETURNS TABLE(ordinal integer, name text, digest text, content bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT d.ordinal,d.name,
        CASE WHEN d.metadata_digest=encode(sha256(convert_to(d.metadata,'UTF8')),'hex')
            AND d.envelope_digest=encode(sha256(convert_to(jsonb_build_array('braid.xml-preview-document.v1',d.organization_id,d.run_id,
                d.direction,d.ordinal,d.name,d.target_name,d.target_name_truncated,d.records_written,d.metadata,d.digest,d.byte_count)::text,'UTF8')),'hex')
            AND d.byte_count=octet_length(d.content)
            AND (SELECT count(*) FROM ONLY braid.xml_preview_documents a WHERE a.organization_id=p_org AND a.run_id=p_run AND a.direction=1)=r.input_count
            THEN d.digest END,d.content FROM ONLY braid.xml_preview_documents d
    JOIN ONLY braid.preview_runs r ON r.organization_id=d.organization_id AND r.run_id=d.run_id
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.state=2 AND r.lease_token=p_token
        AND r.lease_expires_at>clock_timestamp() AND d.direction=1 ORDER BY d.ordinal
$body$;

CREATE FUNCTION braid.preview_xml_documents(p_org uuid,p_run uuid,p_actor uuid,p_direction integer)
RETURNS TABLE(ordinal integer, name text, target_name text, target_name_truncated boolean, records_written bigint, metadata text, digest text, byte_count integer, content bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT d.ordinal,d.name,d.target_name,d.target_name_truncated,d.records_written,
        CASE WHEN d.metadata_digest=encode(sha256(convert_to(d.metadata,'UTF8')),'hex')
            AND d.envelope_digest=encode(sha256(convert_to(jsonb_build_array('braid.xml-preview-document.v1',d.organization_id,d.run_id,
                d.direction,d.ordinal,d.name,d.target_name,d.target_name_truncated,d.records_written,d.metadata,d.digest,d.byte_count)::text,'UTF8')),'hex')
            AND (SELECT count(*) FROM ONLY braid.xml_preview_documents a WHERE a.organization_id=p_org AND a.run_id=p_run AND a.direction=p_direction)=
                (CASE WHEN p_direction=1 THEN r.input_count ELSE r.output_count END)
            AND (SELECT sum(octet_length(a.content)) FROM ONLY braid.xml_preview_documents a WHERE a.organization_id=p_org AND a.run_id=p_run AND a.direction=p_direction)=
                (CASE WHEN p_direction=1 THEN r.input_bytes ELSE r.output_bytes END)
            THEN d.metadata END,d.digest,d.byte_count,d.content
    FROM ONLY braid.xml_preview_documents d JOIN ONLY braid.preview_runs r ON r.organization_id=d.organization_id AND r.run_id=d.run_id
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.submitter_id=p_actor AND d.direction=p_direction
        AND (p_direction=1 OR r.outcome='succeeded') AND braid.preview_authorized(p_org,r.project_id,p_actor) ORDER BY d.ordinal
$body$;

REVOKE ALL ON FUNCTION braid.xml_metadata_valid(jsonb,boolean),braid.guard_xml_preview_document(),
    braid.preview_submit_xml(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    braid.preview_complete_xml(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]),
    braid.preview_xml_inputs(uuid,uuid,uuid),braid.preview_xml_documents(uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.preview_submit_xml(uuid,uuid,uuid,uuid,uuid,uuid,text,text,integer,integer,text[],bytea[]),
    braid.preview_complete_xml(uuid,uuid,uuid,text,text,text,boolean,text,boolean,boolean,text,text,text,text,bigint,text[],text[],boolean[],text[],bigint[],bytea[],text[]),
    braid.preview_xml_inputs(uuid,uuid,uuid) TO braid_preview_writer;
GRANT EXECUTE ON FUNCTION braid.preview_xml_documents(uuid,uuid,uuid,integer) TO braid_preview_reader,braid_preview_writer;
