-- Draft preview subjects retain their checkpoint independently of the editable draft.
ALTER TABLE weft.mapping_drafts ADD CONSTRAINT mapping_drafts_mapping_identity UNIQUE(organization_id,project_id,mapping_id,id);
ALTER TABLE weft.preview_runs ALTER COLUMN revision_id DROP NOT NULL;
ALTER TABLE weft.preview_runs ALTER COLUMN native_object_id DROP NOT NULL;
ALTER TABLE weft.preview_runs ADD COLUMN draft_id uuid, ADD COLUMN draft_version bigint, ADD COLUMN draft_base_revision_id uuid;
ALTER TABLE weft.preview_runs ADD CONSTRAINT preview_runs_draft_fkey FOREIGN KEY(organization_id,project_id,mapping_id,draft_id)
    REFERENCES weft.mapping_drafts(organization_id,project_id,mapping_id,id);
ALTER TABLE weft.preview_runs ADD CONSTRAINT preview_runs_draft_base_fkey FOREIGN KEY(organization_id,project_id,mapping_id,draft_base_revision_id)
    REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id);
ALTER TABLE weft.preview_runs ADD CONSTRAINT preview_runs_subject_check CHECK(
    (revision_id IS NOT NULL AND native_object_id IS NOT NULL AND draft_id IS NULL AND draft_version IS NULL AND draft_base_revision_id IS NULL)
    OR(revision_id IS NULL AND native_object_id IS NULL AND draft_id IS NOT NULL AND draft_version IS NOT NULL AND draft_version>=0 AND draft_base_revision_id IS NOT NULL));

-- Preserve every existing input/output media profile while admitting exactly one
-- private native snapshot document at direction 3, ordinal 0.
DO $native_documents$
DECLARE expression text; constraint_name text;
BEGIN
    FOREACH constraint_name IN ARRAY ARRAY['preview_documents_position_check','preview_documents_kind_check'] LOOP
        SELECT pg_catalog.pg_get_expr(c.conbin,c.conrelid) INTO STRICT expression FROM pg_catalog.pg_constraint c
            WHERE c.conrelid='weft.preview_documents'::regclass AND c.conname=constraint_name;
        EXECUTE format('ALTER TABLE weft.preview_documents DROP CONSTRAINT %I',constraint_name);
        EXECUTE format('ALTER TABLE weft.preview_documents ADD CONSTRAINT %I CHECK ((%s) OR (direction=3 AND ordinal=0 AND name IS NULL AND target_name IS NULL AND target_name_truncated IS NULL AND media_type IS NULL AND records_written IS NULL AND byte_count BETWEEN 1 AND 4194304))',constraint_name,expression);
    END LOOP;
END;
$native_documents$;

CREATE FUNCTION weft.preview_subject_authorized(p_org uuid,p_run uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY weft.preview_runs r WHERE r.organization_id=p_org AND r.run_id=p_run AND
        ((r.draft_id IS NULL AND weft.preview_authorized(p_org,r.project_id,r.submitter_id))
        OR(r.draft_id IS NOT NULL AND weft.draft_authorized(p_org,r.project_id,r.submitter_id))))
$body$;

-- Replace only the authority predicate. Existing lease,
-- cancellation, ordering, media handling and permission grants remain unchanged.
DO $preview_subject_functions$
DECLARE f record; definition text;
BEGIN
    FOR f IN SELECT p.oid,p.proname FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname='weft' AND p.proname IN ('preview_claim','preview_cancel','preview_read','preview_outputs','preview_output') LOOP
        definition := pg_catalog.pg_get_functiondef(f.oid);
        definition := replace(definition,'weft.preview_authorized(p_org,run.project_id,run.submitter_id)','weft.preview_subject_authorized(p_org,p_run)');
        definition := replace(definition,'weft.preview_authorized(p_org,run.project_id,p_actor)','weft.preview_subject_authorized(p_org,p_run)');
        definition := replace(definition,'weft.preview_authorized(p_org,r.project_id,p_actor)','weft.preview_subject_authorized(p_org,p_run)');
        IF definition=pg_catalog.pg_get_functiondef(f.oid) THEN RAISE EXCEPTION 'Preview authority predicate not found in %',f.proname; END IF;
        EXECUTE definition;
    END LOOP;
END;
$preview_subject_functions$;

CREATE FUNCTION weft.preview_submit_draft(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint,
    p_request_key uuid,p_request_document text,p_clock text,p_max_attempts integer,p_retention_seconds integer,p_names text[],p_contents bytea[])
RETURNS TABLE(outcome text,run_id uuid)
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE; c weft.mapping_draft_checkpoints%ROWTYPE; existing weft.preview_runs%ROWTYPE;
    created uuid := pg_catalog.gen_random_uuid(); fingerprint text; total bigint := 0; item integer;
BEGIN
    IF p_org IS NULL OR p_project IS NULL OR p_mapping IS NULL OR p_draft IS NULL OR p_actor IS NULL OR p_version IS NULL OR p_version<0
        OR p_request_key IS NULL OR p_request_key='00000000-0000-0000-0000-000000000000' OR p_request_document IS NULL OR p_clock IS NULL
        OR p_names IS NULL OR p_contents IS NULL OR array_ndims(p_names)<>1 OR array_ndims(p_contents)<>1
        OR array_lower(p_names,1)<>1 OR array_lower(p_contents,1)<>1
        OR pg_catalog.cardinality(p_names)<>pg_catalog.cardinality(p_contents) OR pg_catalog.cardinality(p_contents) NOT BETWEEN 1 AND 16
        OR p_names[1] IS NOT NULL OR p_max_attempts IS NULL OR p_retention_seconds IS NULL
        OR p_max_attempts NOT BETWEEN 1 AND 8 OR p_retention_seconds NOT BETWEEN 60 AND 604800 THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Draft preview submission arguments are invalid.';
    END IF;
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    fingerprint := encode(sha256(convert_to(p_request_document,'UTF8')),'hex');
    -- An idempotent replay reads the run's immutable subject, not today's draft.
    SELECT * INTO existing FROM ONLY weft.preview_runs r WHERE r.organization_id=p_org AND r.submitter_id=p_actor AND r.request_key=p_request_key;
    IF FOUND THEN
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.draft_id=p_draft AND existing.draft_version=p_version AND existing.request_fingerprint=fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id; RETURN;
        END IF;
        RETURN QUERY SELECT 'conflict'::text,NULL::uuid; RETURN;
    END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping
        AND x.id=p_draft AND x.author_principal_id=p_actor AND x.state IN ('open','saving') AND x.version=p_version
        AND (x.state='saving' OR x.expires_at>statement_timestamp()) FOR SHARE;
    IF NOT FOUND THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    SELECT * INTO c FROM ONLY weft.mapping_draft_checkpoints x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.draft_id=p_draft AND x.version=p_version FOR SHARE;
    IF NOT FOUND THEN RETURN QUERY SELECT 'conflict'::text,NULL::uuid; RETURN; END IF;
    FOR item IN 1..pg_catalog.cardinality(p_contents) LOOP
        IF p_contents[item] IS NULL OR (item>1 AND p_names[item] IS NULL) THEN
            RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Draft preview inputs are invalid.';
        END IF;
        total := total+octet_length(p_contents[item]);
    END LOOP;
    INSERT INTO weft.preview_runs(organization_id,run_id,project_id,mapping_id,revision_id,submitter_id,request_key,request_document,request_fingerprint,
        current_date_time,native_object_id,native_digest,native_byte_count,input_count,input_bytes,max_attempts,expires_at,draft_id,draft_version,draft_base_revision_id)
    VALUES(p_org,created,p_project,p_mapping,NULL,p_actor,p_request_key,p_request_document,fingerprint,p_clock,NULL,c.native_digest,octet_length(c.native),
        pg_catalog.cardinality(p_contents),total,p_max_attempts,statement_timestamp()+pg_catalog.make_interval(secs=>p_retention_seconds),p_draft,p_version,d.base_revision_id)
    ON CONFLICT ON CONSTRAINT preview_runs_request_key DO NOTHING;
    IF NOT FOUND THEN
        SELECT * INTO existing FROM ONLY weft.preview_runs r WHERE r.organization_id=p_org AND r.submitter_id=p_actor AND r.request_key=p_request_key;
        IF existing.project_id=p_project AND existing.mapping_id=p_mapping AND existing.draft_id=p_draft
            AND existing.draft_version=p_version AND existing.request_fingerprint=fingerprint THEN
            RETURN QUERY SELECT 'replayed'::text,existing.run_id; RETURN;
        END IF;
        RETURN QUERY SELECT 'conflict'::text,NULL::uuid; RETURN;
    END IF;
    INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,digest,byte_count,content)
        VALUES(p_org,created,3,0,c.native_digest,octet_length(c.native),c.native);
    FOR item IN 1..pg_catalog.cardinality(p_contents) LOOP
        INSERT INTO weft.preview_documents(organization_id,run_id,direction,ordinal,name,digest,byte_count,content)
            VALUES(p_org,created,1,item-1,p_names[item],encode(sha256(p_contents[item]),'hex'),octet_length(p_contents[item]),p_contents[item]);
    END LOOP;
    RETURN QUERY SELECT 'created'::text,created;
END;
$body$;

CREATE FUNCTION weft.preview_draft_subject(p_org uuid,p_run uuid,p_actor uuid)
RETURNS TABLE(draft_id uuid,draft_version bigint,base_revision_id uuid,native_digest text,native_byte_count integer)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT r.draft_id,r.draft_version,r.draft_base_revision_id,r.native_digest,r.native_byte_count::integer FROM ONLY weft.preview_runs r
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.submitter_id=p_actor AND r.draft_id IS NOT NULL AND weft.preview_subject_authorized(p_org,p_run)
$body$;

CREATE FUNCTION weft.preview_claim_native(p_org uuid,p_run uuid,p_token uuid)
RETURNS TABLE(draft_id uuid,draft_version bigint,base_revision_id uuid,native_digest text,content bytea)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT r.draft_id,r.draft_version,r.draft_base_revision_id,d.digest,d.content FROM ONLY weft.preview_runs r JOIN ONLY weft.preview_documents d
        ON d.organization_id=r.organization_id AND d.run_id=r.run_id AND d.direction=3 AND d.ordinal=0
    WHERE r.organization_id=p_org AND r.run_id=p_run AND r.state=2 AND r.lease_token=p_token AND r.draft_id IS NOT NULL
        AND d.digest=r.native_digest AND d.byte_count=r.native_byte_count
$body$;

REVOKE ALL ON FUNCTION weft.preview_subject_authorized(uuid,uuid),weft.preview_submit_draft(uuid,uuid,uuid,uuid,uuid,bigint,uuid,text,text,integer,integer,text[],bytea[]),
    weft.preview_draft_subject(uuid,uuid,uuid),weft.preview_claim_native(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.preview_submit_draft(uuid,uuid,uuid,uuid,uuid,bigint,uuid,text,text,integer,integer,text[],bytea[]),
    weft.preview_claim_native(uuid,uuid,uuid) TO weft_preview_writer;
GRANT EXECUTE ON FUNCTION weft.preview_draft_subject(uuid,uuid,uuid) TO weft_preview_reader;

CREATE FUNCTION weft.preview_draft_subject_by_key(p_org uuid,p_project uuid,p_mapping uuid,p_actor uuid,p_key uuid)
RETURNS TABLE(draft_id uuid,draft_version bigint,base_revision_id uuid,native_digest text,native_byte_count integer)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT r.draft_id,r.draft_version,r.draft_base_revision_id,r.native_digest,r.native_byte_count::integer FROM ONLY weft.preview_runs r
    WHERE r.organization_id=p_org AND r.project_id=p_project AND r.mapping_id=p_mapping AND r.submitter_id=p_actor
        AND r.request_key=p_key AND r.draft_id IS NOT NULL AND weft.preview_subject_authorized(p_org,r.run_id)
$body$;
REVOKE ALL ON FUNCTION weft.preview_draft_subject_by_key(uuid,uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.preview_draft_subject_by_key(uuid,uuid,uuid,uuid,uuid) TO weft_preview_reader;
