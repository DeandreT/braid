-- Submitter-only preview history and submitted-input reads for the existing preview reader. The same fresh
-- authority as preview_read applies; unauthorized and missing runs are indistinguishable. No table grant changes.
-- Regression run metadata is projected separately so chat admission never opens its private completion body.
DO $preview_reads_owner$
BEGIN
    IF current_user <> 'braid_migrator' OR session_user <> current_user THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Preview reads require the separately provisioned owner login.';
    END IF;
END;
$preview_reads_owner$;

CREATE INDEX preview_runs_history ON braid.preview_runs(organization_id,project_id,mapping_id,submitter_id,created_at,run_id);

-- Newest first; after_run continues after a run of the same mapping and submitter.
CREATE FUNCTION braid.preview_history(p_org uuid, p_project uuid, p_mapping uuid, p_actor uuid, p_after_run uuid, p_limit integer)
RETURNS TABLE(run_id uuid, revision_id uuid, state integer, outcome text, created_at timestamptz, finished_at timestamptz,
    expires_at timestamptz)
LANGUAGE plpgsql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,braid,pg_temp
AS $braid_preview_history$
BEGIN
    IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 101 THEN
        RAISE EXCEPTION 'Preview history page is invalid.' USING ERRCODE = '22023';
    END IF;
    IF braid.preview_authorized(p_org,p_project,p_actor) IS NOT TRUE THEN RETURN; END IF;
    RETURN QUERY SELECT r.run_id, r.revision_id, r.state, r.outcome, r.created_at, r.finished_at, r.expires_at
        FROM ONLY braid.preview_runs r
        WHERE r.organization_id = p_org AND r.project_id = p_project AND r.mapping_id = p_mapping AND r.submitter_id = p_actor
            AND (p_after_run IS NULL OR (r.created_at,r.run_id) < (SELECT x.created_at,x.run_id FROM ONLY braid.preview_runs x
                WHERE x.organization_id = p_org AND x.project_id = p_project AND x.mapping_id = p_mapping
                    AND x.submitter_id = p_actor AND x.run_id = p_after_run))
        ORDER BY r.created_at DESC, r.run_id DESC LIMIT p_limit;
END;
$braid_preview_history$;

CREATE FUNCTION braid.preview_input_documents(p_org uuid, p_run uuid, p_actor uuid)
RETURNS TABLE(ordinal integer, name text, digest text, byte_count integer)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,braid,pg_temp
AS $braid_preview_input_documents$
    SELECT d.ordinal, d.name, d.digest, d.byte_count FROM ONLY braid.preview_documents d
    JOIN ONLY braid.preview_runs r ON r.organization_id = d.organization_id AND r.run_id = d.run_id
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.submitter_id = p_actor AND d.direction = 1
        AND braid.preview_authorized(p_org,r.project_id,p_actor)
    ORDER BY d.ordinal
$braid_preview_input_documents$;

CREATE FUNCTION braid.preview_input(p_org uuid, p_run uuid, p_actor uuid, p_ordinal integer)
RETURNS TABLE(digest text, content bytea)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path = pg_catalog,braid,pg_temp
AS $braid_preview_input$
    SELECT d.digest, d.content FROM ONLY braid.preview_documents d
    JOIN ONLY braid.preview_runs r ON r.organization_id = d.organization_id AND r.run_id = d.run_id
    WHERE r.organization_id = p_org AND r.run_id = p_run AND r.submitter_id = p_actor AND d.direction = 1
        AND d.ordinal = p_ordinal AND braid.preview_authorized(p_org,r.project_id,p_actor)
$braid_preview_input$;

REVOKE ALL ON FUNCTION braid.preview_history(uuid,uuid,uuid,uuid,uuid,integer), braid.preview_input_documents(uuid,uuid,uuid),
    braid.preview_input(uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.preview_history(uuid,uuid,uuid,uuid,uuid,integer), braid.preview_input_documents(uuid,uuid,uuid),
    braid.preview_input(uuid,uuid,uuid,integer) TO braid_preview_reader;

CREATE FUNCTION braid.read_regression_run_metadata(org uuid, id uuid)
RETURNS TABLE(project_id uuid, mapping_id uuid, submitter_id uuid, case_id uuid, version bigint,
    revision_id uuid, state text, attempt integer, created_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT r.project_id,r.mapping_id,r.submitter_id,r.case_id,r.version,r.revision_id,r.state,r.attempt,r.created_at
    FROM ONLY braid.regression_runs r
    WHERE session_user IN ('braid_run_reader','braid_run_writer') AND r.organization_id=org AND r.run_id=id
        AND r.submitter_id=NULLIF(current_setting('braid.run_principal_id',true),'')::uuid
        AND braid.regression_run_authorized(org,r.project_id,r.submitter_id)
$body$;
REVOKE ALL ON FUNCTION braid.read_regression_run_metadata(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_regression_run_metadata(uuid,uuid) TO braid_run_reader,braid_run_writer;

-- A citation refers to a retained version, not necessarily the current case head.
-- Project readers can verify that identity without reading any sample envelope.
CREATE FUNCTION braid.read_regression_case_metadata(org uuid, project uuid, mapping uuid, id uuid, requested_version bigint)
RETURNS TABLE(case_id uuid, version bigint, creator_id uuid, revision_id uuid, name text, saved_by uuid, created_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
    SELECT v.case_id,v.version,v.creator_id,v.revision_id,v.name,v.saved_by,v.created_at
    FROM ONLY braid.regression_case_versions v
    WHERE current_user='braid_migrator' AND session_user IN ('braid_case_reader','braid_case_writer')
        AND braid.case_authorized(org,project,NULLIF(current_setting('braid.case_principal_id',true),'')::uuid,ARRAY['viewer','runner','publisher','administrator'])
        AND v.organization_id=org AND v.project_id=project AND v.mapping_id=mapping AND v.case_id=id AND v.version=requested_version
$body$;
REVOKE ALL ON FUNCTION braid.read_regression_case_metadata(uuid,uuid,uuid,uuid,bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.read_regression_case_metadata(uuid,uuid,uuid,uuid,bigint) TO braid_case_reader,braid_case_writer;
