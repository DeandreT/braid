-- Document comparisons are recomputed by a separately credentialed verifier.
-- The ordinary run writer can submit observations, never a document pass.
DO $role$
DECLARE r record;
BEGIN
    SELECT * INTO r FROM pg_roles WHERE rolname='braid_comparison_settler';
    IF NOT FOUND OR NOT r.rolcanlogin OR r.rolsuper OR r.rolinherit OR r.rolcreatedb OR r.rolcreaterole
        OR r.rolreplication OR r.rolbypassrls OR r.rolconfig IS NOT NULL OR r.rolvaliduntil IS NOT NULL
        OR NOT has_database_privilege(r.oid,current_database(),'CONNECT')
        OR has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
        OR EXISTS(SELECT 1 FROM pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
        OR EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE')
            OR n.nspname='braid' AND has_schema_privilege(r.oid,n.oid,'USAGE'))
        OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
            AND (c.relowner=r.oid OR has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
        OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND (p.proowner=r.oid OR has_function_privilege(r.oid,p.oid,'EXECUTE'))) THEN
        RAISE EXCEPTION 'Comparison settlement requires its separate CONNECT-only login.' USING ERRCODE='42501';
    END IF;
END;
$role$;

ALTER TABLE braid.automation_evaluations
    ADD COLUMN pending_comparison bytea,
    ADD COLUMN pending_comparison_digest text,
    ADD COLUMN comparison_policy text,
    ADD COLUMN comparison_outcome text,
    ADD CONSTRAINT automation_pending_comparison CHECK (
        (pending_comparison IS NULL)=(pending_comparison_digest IS NULL)
        AND (pending_comparison IS NULL OR (state='running' AND octet_length(pending_comparison) BETWEEN 1 AND 12582912
            AND encode(sha256(pending_comparison),'hex')=pending_comparison_digest))),
    ADD CONSTRAINT automation_verified_comparison CHECK (
        (comparison_policy IS NULL)=(comparison_outcome IS NULL)
        AND (comparison_policy IS NULL OR (comparison_policy IN ('braid.regression.exact-bytes.v1','braid.regression.edi-segments.v1')
            AND comparison_outcome IN ('passed','failed','notEvaluated')
            AND state=CASE WHEN comparison_outcome='passed' THEN 'passed' ELSE 'failed' END)));

CREATE FUNCTION braid.automation_comparison_header(envelope bytea) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE n integer; header text; boundary integer;
BEGIN
    IF envelope IS NULL OR octet_length(envelope)<4 THEN RETURN NULL; END IF;
    n:=get_byte(envelope,0)*16777216+get_byte(envelope,1)*65536+get_byte(envelope,2)*256+get_byte(envelope,3);
    IF n NOT BETWEEN 1 AND 262144 OR n>octet_length(envelope)-4 THEN RETURN NULL; END IF;
    header:=convert_from(substring(envelope FROM 5 FOR n),'UTF8');
    -- The canonical codec writes policy/runtime/limits before parameters. Do not
    -- ask PostgreSQL JSON to decode parameter strings (which may contain U+0000).
    -- The trusted C# decoder later verifies the complete envelope and its digest.
    boundary:=strpos(header,',"parameters":');
    IF boundary=0 THEN RETURN NULL; END IF;
    RETURN (substring(header FROM 1 FOR boundary-1)||'}')::jsonb;
EXCEPTION WHEN OTHERS THEN RETURN NULL;
END;
$body$;
REVOKE ALL ON FUNCTION braid.automation_comparison_header(bytea) FROM PUBLIC;

CREATE OR REPLACE FUNCTION braid.automation_result_passes(org uuid, suite uuid, case_key uuid, result bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE c braid.regression_case_versions%ROWTYPE; e braid.automation_evaluations%ROWTYPE;
    header json; observed jsonb; n integer; item jsonb; baseline jsonb; content bytea;
BEGIN
    SELECT * INTO e FROM ONLY braid.automation_evaluations x WHERE x.organization_id=org AND x.suite_id=suite AND x.case_id=case_key;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT * INTO c FROM ONLY braid.regression_case_versions v WHERE v.organization_id=e.organization_id
        AND v.project_id=e.project_id AND v.mapping_id=e.mapping_id AND v.case_id=e.case_id AND v.version=e.version;
    IF NOT FOUND OR result IS NULL OR octet_length(c.envelope)<4 THEN RETURN false; END IF;
    n:=get_byte(c.envelope,0)*16777216+get_byte(c.envelope,1)*65536+get_byte(c.envelope,2)*256+get_byte(c.envelope,3);
    IF n NOT BETWEEN 1 AND 262144 THEN RETURN false; END IF;
    IF braid.automation_comparison_header(c.envelope)->>'schema'='braid.document-regression-case.v1' THEN
        RETURN e.state='passed' AND e.comparison_outcome='passed' AND e.comparison_policy=braid.automation_comparison_header(c.envelope)->>'comparisonPolicy'
            AND e.result_body=result AND e.result_digest=encode(sha256(result),'hex');
    END IF;
    header:=convert_from(substring(c.envelope FROM 5 FOR n),'UTF8')::json;
    IF header->>'schema' IS DISTINCT FROM 'braid.regression-case.v1'
        OR header->>'comparisonPolicy' IS DISTINCT FROM 'braid.regression.exact-bytes.v1' THEN RETURN false; END IF;
    observed:=convert_from(result,'UTF8')::jsonb;
    IF observed->>'schema' IS DISTINCT FROM 'braid.regression-run-result.v1' OR observed->>'runId' IS DISTINCT FROM suite::text
        OR observed->>'caseDigest' IS DISTINCT FROM c.content_digest OR observed->>'outcome' IS DISTINCT FROM '0'
        OR observed->>'previewStatus' IS DISTINCT FROM '0' OR jsonb_typeof(observed->'outputs') IS DISTINCT FROM 'array'
        OR json_array_length(header->'expected')=0 OR json_array_length(header->'expected')<>jsonb_array_length(observed->'outputs') THEN RETURN false; END IF;
    FOR item IN SELECT value FROM jsonb_array_elements(observed->'outputs') LOOP
        SELECT value::jsonb INTO baseline FROM json_array_elements(header->'expected') WHERE value::jsonb->'name'=item->'name';
        IF NOT FOUND OR (item->>'content'~'^[A-Za-z0-9_-]*$') IS NOT TRUE
            OR coalesce(item->>'mediaType','application/json')<>'application/json' THEN RETURN false; END IF;
        content:=decode(translate(item->>'content','-_','+/')||repeat('=',(4-length(item->>'content')%4)%4),'base64');
        IF octet_length(content)<>(baseline->>'byteCount')::integer OR encode(sha256(content),'hex') IS DISTINCT FROM baseline->>'sha256' THEN RETURN false; END IF;
    END LOOP;
    RETURN NOT EXISTS(SELECT 1 FROM json_array_elements(header->'expected') x WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(observed->'outputs') y WHERE x::jsonb->'name'=y->'name'));
EXCEPTION WHEN OTHERS THEN RETURN false;
END;
$body$;

CREATE OR REPLACE FUNCTION braid.automation_gate(org uuid, project uuid, mapping uuid, revision uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(org::text||project::text,336));
    IF NOT coalesce((SELECT a.require_regression FROM ONLY braid.automation_projects a WHERE a.organization_id=org AND a.project_id=project),false) THEN RETURN true; END IF;
    RETURN EXISTS(SELECT 1 FROM ONLY braid.automation_suites s WHERE s.organization_id=org AND s.project_id=project
        AND s.mapping_id=mapping AND s.revision_id=revision AND s.state='passed'
        AND NOT EXISTS(SELECT 1 FROM ONLY braid.automation_suites newer WHERE newer.organization_id=org AND newer.project_id=project
            AND newer.mapping_id=mapping AND newer.revision_id=revision AND (newer.created_at,newer.suite_id)>(s.created_at,s.suite_id))
        AND NOT EXISTS(SELECT 1 FROM ONLY braid.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=mapping
            AND NOT EXISTS(SELECT 1 FROM ONLY braid.automation_evaluations e WHERE e.organization_id=org AND e.suite_id=s.suite_id
                AND e.case_id=c.case_id AND e.version=c.head_version AND e.state='passed'
                AND braid.automation_result_passes(org,s.suite_id,c.case_id,e.result_body) IS TRUE))
        AND EXISTS(SELECT 1 FROM ONLY braid.regression_cases c WHERE c.organization_id=org AND c.project_id=project AND c.mapping_id=mapping));
END;
$body$;

CREATE OR REPLACE FUNCTION braid.automation_claim_evaluation() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE e braid.automation_evaluations%ROWTYPE; s braid.automation_suites%ROWTYPE; c braid.regression_case_versions%ROWTYPE;
BEGIN
    IF session_user<>'braid_run_writer' THEN RAISE EXCEPTION 'Automation worker required.' USING ERRCODE='42501'; END IF;
    SELECT * INTO e FROM ONLY braid.automation_evaluations x WHERE x.state IN ('queued','running') AND x.eligible_at<=statement_timestamp()
        AND (x.token IS NULL OR x.lease_expires<=statement_timestamp()) ORDER BY x.eligible_at,x.evaluation_id FOR UPDATE SKIP LOCKED LIMIT 1;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO s FROM ONLY braid.automation_suites x WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id;
    PERFORM set_config('braid.organization_id',e.organization_id::text,true);
    IF e.attempt>=3 OR braid.regression_run_authorized(e.organization_id,e.project_id,s.actor_id) IS NOT TRUE THEN
        UPDATE braid.automation_evaluations x SET state=CASE WHEN e.attempt>=3 THEN 'exhausted' ELSE 'revoked' END,token=NULL,lease_expires=NULL,
            pending_comparison=NULL,pending_comparison_digest=NULL
            WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id AND x.case_id=e.case_id;
        PERFORM braid.automation_settle_suite(e.organization_id,e.suite_id); RETURN NULL;
    END IF;
    SELECT * INTO c FROM ONLY braid.regression_case_versions x WHERE x.organization_id=e.organization_id AND x.project_id=e.project_id AND x.mapping_id=e.mapping_id AND x.case_id=e.case_id AND x.version=e.version;
    e.token:=gen_random_uuid(); e.attempt:=e.attempt+1;
    UPDATE braid.automation_evaluations x SET state='running',attempt=e.attempt,token=e.token,lease_expires=statement_timestamp()+interval '120 seconds',
        pending_comparison=NULL,pending_comparison_digest=NULL
        WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id AND x.case_id=e.case_id;
    RETURN jsonb_build_object('organizationId',e.organization_id,'projectId',e.project_id,'mappingId',e.mapping_id,'suiteId',e.suite_id,'caseId',e.case_id,'version',e.version,
        'actorId',s.actor_id,'originalRevisionId',c.revision_id,'creatorId',c.creator_id,'nativeDigest',c.native_digest,'caseDigest',c.content_digest,'caseBytes',c.byte_count,
        'targetDigest',s.native_digest,'targetRevisionId',s.revision_id,'token',e.token,'attempt',e.attempt,'evaluationId',e.evaluation_id);
END;
$body$;

CREATE OR REPLACE FUNCTION braid.automation_finish_evaluation(org uuid, suite uuid, case_key uuid, claim uuid, generation integer, result bytea) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE pending braid.automation_evaluations%ROWTYPE; actor uuid; envelope bytea; n integer; header json;
BEGIN
    IF session_user<>'braid_run_writer' OR (result IS NOT NULL AND octet_length(result) NOT BETWEEN 1 AND 12582912) THEN RETURN false; END IF;
    SELECT * INTO pending FROM ONLY braid.automation_evaluations e
        WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key AND e.token=claim
            AND e.attempt=generation AND e.state='running' AND e.lease_expires>statement_timestamp() FOR UPDATE;
    IF NOT FOUND OR pending.pending_comparison IS NOT NULL THEN RETURN false; END IF;
    SELECT s.actor_id INTO actor FROM ONLY braid.automation_suites s WHERE s.organization_id=org AND s.suite_id=suite;
    IF braid.regression_run_authorized(org,pending.project_id,actor) IS NOT TRUE THEN
        UPDATE braid.automation_evaluations e SET state='revoked',token=NULL,lease_expires=NULL
            WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key;
        PERFORM braid.automation_settle_suite(org,suite); RETURN false;
    END IF;
    IF result IS NOT NULL THEN
        SELECT c.envelope INTO envelope FROM ONLY braid.regression_case_versions c WHERE c.organization_id=org AND c.project_id=pending.project_id
            AND c.mapping_id=pending.mapping_id AND c.case_id=case_key AND c.version=pending.version;
        BEGIN
            n:=get_byte(envelope,0)*16777216+get_byte(envelope,1)*65536+get_byte(envelope,2)*256+get_byte(envelope,3);
            IF n BETWEEN 1 AND 262144 THEN header:=braid.automation_comparison_header(envelope)::json; END IF;
        EXCEPTION WHEN OTHERS THEN header:=NULL;
        END;
        IF header->>'schema'='braid.document-regression-case.v1' AND header->>'comparisonPolicy' IN ('braid.regression.exact-bytes.v1','braid.regression.edi-segments.v1') THEN
            UPDATE braid.automation_evaluations e SET pending_comparison=result,pending_comparison_digest=encode(sha256(result),'hex')
                WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key;
            RETURN true;
        END IF;
    END IF;
    UPDATE braid.automation_evaluations e SET state=CASE WHEN result IS NULL THEN 'queued' WHEN braid.automation_result_passes(org,suite,case_key,result) THEN 'passed' ELSE 'failed' END,
        token=NULL,lease_expires=NULL,result_body=result,result_digest=CASE WHEN result IS NULL THEN NULL ELSE encode(sha256(result),'hex') END,
        eligible_at=statement_timestamp()+CASE WHEN result IS NULL THEN make_interval(secs=>least(30*generation,90)) ELSE interval '0' END
        WHERE e.organization_id=org AND e.suite_id=suite AND e.case_id=case_key AND e.token=claim AND e.attempt=generation AND e.state='running' AND e.lease_expires>statement_timestamp();
    IF NOT FOUND THEN RETURN false; END IF;
    PERFORM braid.automation_settle_suite(org,suite); RETURN true;
END;
$body$;

CREATE FUNCTION braid.automation_read_comparison() RETURNS TABLE(organization_id uuid, suite_id uuid, case_id uuid, version bigint, creator_id uuid, project_id uuid, mapping_id uuid, original_revision_id uuid, native_digest text, content_digest text, byte_count integer, envelope bytea, result_body bytea, result_digest text, claim_id uuid, generation integer, comparison_policy text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE e braid.automation_evaluations%ROWTYPE; s braid.automation_suites%ROWTYPE; c braid.regression_case_versions%ROWTYPE;
BEGIN
    IF session_user<>'braid_comparison_settler' THEN RAISE EXCEPTION 'Comparison verifier required.' USING ERRCODE='42501'; END IF;
    SELECT * INTO e FROM ONLY braid.automation_evaluations x WHERE x.state='running' AND x.pending_comparison IS NOT NULL
        AND x.lease_expires>statement_timestamp() ORDER BY x.eligible_at,x.evaluation_id FOR UPDATE SKIP LOCKED LIMIT 1;
    IF NOT FOUND THEN RETURN; END IF;
    PERFORM set_config('braid.organization_id',e.organization_id::text,true);
    SELECT * INTO s FROM ONLY braid.automation_suites x WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id;
    IF braid.regression_run_authorized(e.organization_id,e.project_id,s.actor_id) IS NOT TRUE THEN
        UPDATE braid.automation_evaluations x SET state='revoked',token=NULL,lease_expires=NULL,pending_comparison=NULL,pending_comparison_digest=NULL
            WHERE x.organization_id=e.organization_id AND x.suite_id=e.suite_id AND x.case_id=e.case_id;
        PERFORM braid.automation_settle_suite(e.organization_id,e.suite_id); RETURN;
    END IF;
    SELECT * INTO c FROM ONLY braid.regression_case_versions x WHERE x.organization_id=e.organization_id AND x.project_id=e.project_id
        AND x.mapping_id=e.mapping_id AND x.case_id=e.case_id AND x.version=e.version;
    RETURN QUERY SELECT e.organization_id,e.suite_id,e.case_id,e.version,c.creator_id,e.project_id,e.mapping_id,c.revision_id,
        c.native_digest,c.content_digest,c.byte_count,c.envelope,e.pending_comparison,e.pending_comparison_digest,e.token,e.attempt,
        braid.automation_comparison_header(c.envelope)->>'comparisonPolicy';
END;
$body$;

CREATE FUNCTION braid.automation_complete_comparison(org uuid, suite uuid, case_key uuid, claim uuid, generation integer, case_digest text, body_digest text, policy text, decision text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
AS $body$
DECLARE e braid.automation_evaluations%ROWTYPE; c braid.regression_case_versions%ROWTYPE; actor uuid; n integer; header json;
BEGIN
    IF session_user<>'braid_comparison_settler' OR decision IS NULL OR decision NOT IN ('passed','failed','notEvaluated')
        OR policy IS NULL OR policy NOT IN ('braid.regression.exact-bytes.v1','braid.regression.edi-segments.v1') THEN RETURN false; END IF;
    SELECT * INTO e FROM ONLY braid.automation_evaluations x WHERE x.organization_id=org AND x.suite_id=suite AND x.case_id=case_key
        AND x.state='running' AND x.token=claim AND x.attempt=generation AND x.lease_expires>statement_timestamp()
        AND x.pending_comparison IS NOT NULL AND x.pending_comparison_digest=body_digest FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    SELECT * INTO c FROM ONLY braid.regression_case_versions x WHERE x.organization_id=org AND x.project_id=e.project_id
        AND x.mapping_id=e.mapping_id AND x.case_id=case_key AND x.version=e.version AND x.content_digest=case_digest;
    IF NOT FOUND THEN RETURN false; END IF;
    n:=get_byte(c.envelope,0)*16777216+get_byte(c.envelope,1)*65536+get_byte(c.envelope,2)*256+get_byte(c.envelope,3);
    header:=braid.automation_comparison_header(c.envelope)::json;
    IF header->>'schema' IS DISTINCT FROM 'braid.document-regression-case.v1' OR policy IS DISTINCT FROM header->>'comparisonPolicy' THEN RETURN false; END IF;
    SELECT s.actor_id INTO actor FROM ONLY braid.automation_suites s WHERE s.organization_id=org AND s.suite_id=suite;
    IF braid.regression_run_authorized(org,e.project_id,actor) IS NOT TRUE THEN
        UPDATE braid.automation_evaluations x SET state='revoked',token=NULL,lease_expires=NULL,pending_comparison=NULL,pending_comparison_digest=NULL
            WHERE x.organization_id=org AND x.suite_id=suite AND x.case_id=case_key;
        PERFORM braid.automation_settle_suite(org,suite); RETURN false;
    END IF;
    UPDATE braid.automation_evaluations x SET state=CASE WHEN decision='passed' THEN 'passed' ELSE 'failed' END,
        token=NULL,lease_expires=NULL,result_body=e.pending_comparison,result_digest=e.pending_comparison_digest,
        pending_comparison=NULL,pending_comparison_digest=NULL,comparison_policy=policy,comparison_outcome=decision
        WHERE x.organization_id=org AND x.suite_id=suite AND x.case_id=case_key;
    PERFORM braid.automation_settle_suite(org,suite); RETURN true;
END;
$body$;

REVOKE ALL ON FUNCTION braid.automation_read_comparison(),braid.automation_complete_comparison(uuid,uuid,uuid,uuid,integer,text,text,text,text) FROM PUBLIC;
GRANT USAGE ON SCHEMA braid TO braid_comparison_settler;
GRANT EXECUTE ON FUNCTION braid.automation_read_comparison(),braid.automation_complete_comparison(uuid,uuid,uuid,uuid,integer,text,text,text,text) TO braid_comparison_settler;
