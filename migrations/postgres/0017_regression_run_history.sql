CREATE INDEX regression_run_history ON braid.regression_runs
    (organization_id,project_id,mapping_id,submitter_id,created_at DESC,run_id DESC);

CREATE FUNCTION braid.list_regression_run_history(org uuid, project uuid, mapping uuid, after_id uuid, maximum integer)
RETURNS TABLE(run_id uuid, case_id uuid, version bigint, revision_id uuid, state text, attempt integer, created_at timestamptz)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE actor uuid;
BEGIN
    IF session_user NOT IN ('braid_run_reader','braid_run_writer') OR current_user<>'braid_migrator'
        OR org IS NULL OR org IS DISTINCT FROM NULLIF(current_setting('braid.organization_id',true),'')::uuid THEN RETURN; END IF;
    actor:=NULLIF(current_setting('braid.run_principal_id',true),'')::uuid;
    IF braid.regression_run_authorized(org,project,actor) IS NOT TRUE THEN RETURN; END IF;
    IF maximum IS NULL OR maximum NOT BETWEEN 1 AND 101 THEN RAISE EXCEPTION 'Run history page is invalid.' USING ERRCODE='22023'; END IF;
    RETURN QUERY SELECT r.run_id,r.case_id,r.version,r.revision_id,r.state,r.attempt,r.created_at
        FROM ONLY braid.regression_runs r WHERE r.organization_id=org AND r.project_id=project AND r.mapping_id=mapping
            AND r.submitter_id=actor AND (after_id IS NULL OR (r.created_at,r.run_id)<(
                SELECT x.created_at,x.run_id FROM ONLY braid.regression_runs x WHERE x.organization_id=org AND x.project_id=project
                    AND x.mapping_id=mapping AND x.submitter_id=actor AND x.run_id=after_id))
        ORDER BY r.created_at DESC,r.run_id DESC LIMIT maximum;
END;
$body$;
REVOKE ALL ON FUNCTION braid.list_regression_run_history(uuid,uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.list_regression_run_history(uuid,uuid,uuid,uuid,integer) TO braid_run_reader,braid_run_writer;
