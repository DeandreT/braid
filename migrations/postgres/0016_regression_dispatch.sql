-- The committed run row is also the durable queue entry; discovery contains no sample data.
CREATE INDEX regression_runs_dispatch ON braid.regression_runs
    ((coalesce(lease_expires,eligible_at)),created_at,organization_id,run_id)
    WHERE state IN ('queued','running');

CREATE FUNCTION braid.list_ready_regression_runs(maximum integer)
RETURNS TABLE(organization_id uuid, run_id uuid)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    IF session_user<>'braid_run_writer' OR current_user<>'braid_migrator'
        OR NULLIF(current_setting('braid.organization_id',true),'') IS NOT NULL
        OR NULLIF(current_setting('braid.run_principal_id',true),'') IS NOT NULL THEN
        RAISE EXCEPTION 'Unscoped regression dispatcher required.' USING ERRCODE='42501';
    END IF;
    IF maximum IS NULL OR maximum NOT BETWEEN 1 AND 16 THEN
        RAISE EXCEPTION 'Regression dispatch page is invalid.' USING ERRCODE='22023';
    END IF;
    RETURN QUERY SELECT r.organization_id,r.run_id FROM ONLY braid.regression_runs r
        WHERE r.state IN ('queued','running') AND coalesce(r.lease_expires,r.eligible_at)<=statement_timestamp()
        ORDER BY coalesce(r.lease_expires,r.eligible_at),r.created_at,r.organization_id,r.run_id LIMIT maximum;
END;
$body$;
REVOKE ALL ON FUNCTION braid.list_ready_regression_runs(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.list_ready_regression_runs(integer) TO braid_run_writer;
