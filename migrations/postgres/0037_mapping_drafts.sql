-- Author-private, exact-base operation drafts and recoverable edit provenance.
-- Runtime credentials must be separately provisioned CONNECT-only logins.
DO $roles$
DECLARE r record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user
        OR current_setting('server_encoding') <> 'UTF8' OR current_setting('standard_conforming_strings') <> 'on' THEN
        RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Mapping drafts require the accepted migration environment.';
    END IF;
    IF (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('weft_draft_reader','weft_draft_writer')) <> 2 THEN
        RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Mapping drafts require dedicated restricted logins.';
    END IF;
    FOR r IN SELECT * FROM pg_catalog.pg_roles WHERE rolname IN ('weft_draft_reader','weft_draft_writer') LOOP
        IF NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
            OR NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CONNECT')
            OR pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                AND (n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE')))
            OR pg_catalog.has_schema_privilege(r.oid,'weft','USAGE')
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relkind IN ('r','p','v','m','f') AND
                    (pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_proc f JOIN pg_catalog.pg_namespace n ON n.oid=f.pronamespace
                WHERE n.nspname='weft' AND pg_catalog.has_function_privilege(r.oid,f.oid,'EXECUTE'))
            OR EXISTS(SELECT 1 FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
                WHERE d.defaclnamespace IN (0,(SELECT oid FROM pg_catalog.pg_namespace WHERE nspname='weft')) AND a.grantee IN (0,r.oid)) THEN
            RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Mapping draft logins require CONNECT-only provisioning.';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE weft.mapping_drafts (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    id uuid NOT NULL DEFAULT pg_catalog.gen_random_uuid(),
    mapping_id uuid NOT NULL,
    author_principal_id uuid NOT NULL,
    creation_key uuid NOT NULL,
    creation_base_revision_id uuid NOT NULL,
    base_revision_id uuid NOT NULL,
    base_sequence bigint NOT NULL CHECK(base_sequence > 0),
    base_digest text COLLATE "C" NOT NULL CHECK(base_digest ~ '^[0-9a-f]{64}$'),
    base_native_object_id uuid NOT NULL,
    base_native_byte_count bigint NOT NULL CHECK(base_native_byte_count BETWEEN 1 AND 4194304),
    state text COLLATE "C" NOT NULL DEFAULT 'open' CHECK(state IN ('open','saving','saved','discarded','expired')),
    version bigint NOT NULL DEFAULT 0 CHECK(version >= 0),
    operations bytea,
    operations_digest text COLLATE "C",
    operation_count integer NOT NULL DEFAULT 0 CHECK(operation_count BETWEEN 0 AND 2000),
    saving_key uuid,
    saving_filename text COLLATE "C",
    saving_operation_id uuid,
    saved_revision_id uuid,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    changed_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    expires_at timestamptz NOT NULL DEFAULT statement_timestamp()+interval '30 days',
    PRIMARY KEY(organization_id,project_id,id),
    UNIQUE(organization_id,project_id,author_principal_id,creation_key),
    FOREIGN KEY(organization_id,project_id,mapping_id) REFERENCES weft.mappings(organization_id,project_id,id),
    FOREIGN KEY(organization_id,project_id,mapping_id,base_revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id),
    FOREIGN KEY(organization_id,project_id,mapping_id,creation_base_revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id),
    FOREIGN KEY(organization_id,project_id,author_principal_id) REFERENCES weft.project_memberships(organization_id,project_id,principal_id),
    FOREIGN KEY(organization_id,project_id,mapping_id,saved_revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id),
    CHECK(id <> '00000000-0000-0000-0000-000000000000' AND creation_key <> '00000000-0000-0000-0000-000000000000'),
    CHECK((operations IS NULL)=(operations_digest IS NULL)
        AND (operations IS NULL OR (octet_length(operations) BETWEEN 1 AND 1048576 AND operations_digest=encode(sha256(operations),'hex')))
        AND ((state IN ('open','saving'))=(operations IS NOT NULL)) AND (operations IS NOT NULL OR operation_count=0)),
    CHECK((saving_key IS NULL)=(saving_filename IS NULL) AND (state <> 'saving' OR saving_key IS NOT NULL)
        AND (saving_operation_id IS NULL OR saving_key IS NOT NULL) AND (state='saved')=(saved_revision_id IS NOT NULL)),
    CHECK(isfinite(created_at) AND isfinite(changed_at) AND changed_at >= created_at AND isfinite(expires_at) AND expires_at > changed_at)
);
CREATE UNIQUE INDEX mapping_drafts_one_active_author ON weft.mapping_drafts(organization_id,project_id,mapping_id,author_principal_id) WHERE state IN ('open','saving');
CREATE UNIQUE INDEX mapping_drafts_save_identity ON weft.mapping_drafts(organization_id,project_id,author_principal_id,saving_key) WHERE saving_key IS NOT NULL;
CREATE INDEX mapping_drafts_expiry ON weft.mapping_drafts(expires_at) WHERE state='open';

CREATE TABLE weft.mapping_draft_checkpoints (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    draft_id uuid NOT NULL,
    version bigint NOT NULL CHECK(version >= 0),
    native bytea NOT NULL CHECK(octet_length(native) BETWEEN 1 AND 4194304),
    native_digest text COLLATE "C" NOT NULL CHECK(native_digest=encode(sha256(native),'hex')),
    validation_evidence bytea NOT NULL CHECK(octet_length(validation_evidence) BETWEEN 1 AND 8388608),
    validation_report bytea NOT NULL CHECK(octet_length(validation_report) BETWEEN 1 AND 8388608),
    projection_report bytea NOT NULL CHECK(octet_length(projection_report) BETWEEN 1 AND 16777216),
    is_valid boolean NOT NULL,
    total_issue_count integer NOT NULL CHECK(total_issue_count BETWEEN 0 AND 32768),
    engine_revision text COLLATE "C" NOT NULL CHECK(octet_length(engine_revision) BETWEEN 1 AND 256),
    worker_build text COLLATE "C" NOT NULL CHECK(octet_length(worker_build) BETWEEN 1 AND 2048),
    adapter_build text COLLATE "C" NOT NULL CHECK(octet_length(adapter_build) BETWEEN 1 AND 256),
    PRIMARY KEY(organization_id,project_id,draft_id),
    FOREIGN KEY(organization_id,project_id,draft_id) REFERENCES weft.mapping_drafts(organization_id,project_id,id),
    CHECK(is_valid=(total_issue_count=0))
);

CREATE TABLE weft.revision_edit_provenance (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    draft_id uuid NOT NULL,
    author_principal_id uuid NOT NULL,
    base_revision_id uuid NOT NULL,
    draft_version bigint NOT NULL CHECK(draft_version >= 0),
    operations bytea NOT NULL CHECK(octet_length(operations) BETWEEN 1 AND 1048576),
    operations_digest text COLLATE "C" NOT NULL CHECK(operations_digest=encode(sha256(operations),'hex')),
    operation_count integer NOT NULL CHECK(operation_count BETWEEN 1 AND 2000),
    PRIMARY KEY(organization_id,project_id,mapping_id,revision_id),
    UNIQUE(organization_id,project_id,draft_id),
    FOREIGN KEY(organization_id,project_id,mapping_id,revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id),
    FOREIGN KEY(organization_id,project_id,mapping_id,base_revision_id) REFERENCES weft.mapping_revisions(organization_id,project_id,mapping_id,id),
    FOREIGN KEY(organization_id,project_id,draft_id) REFERENCES weft.mapping_drafts(organization_id,project_id,id),
    FOREIGN KEY(organization_id,project_id,author_principal_id) REFERENCES weft.project_memberships(organization_id,project_id,principal_id)
);
ALTER TABLE weft.mapping_drafts ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.mapping_draft_checkpoints ENABLE ROW LEVEL SECURITY;
ALTER TABLE weft.revision_edit_provenance ENABLE ROW LEVEL SECURITY;
CREATE POLICY mapping_drafts_organization_scope ON weft.mapping_drafts
    USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY mapping_draft_checkpoints_organization_scope ON weft.mapping_draft_checkpoints
    USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE POLICY revision_edit_provenance_organization_scope ON weft.revision_edit_provenance
    USING(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK(organization_id=NULLIF(current_setting('weft.organization_id',true),'')::uuid);
REVOKE ALL ON weft.mapping_drafts,weft.mapping_draft_checkpoints,weft.revision_edit_provenance FROM PUBLIC;

CREATE FUNCTION weft.draft_authorized(p_org uuid,p_project uuid,p_actor uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT EXISTS(SELECT 1 FROM ONLY weft.principals p
        JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=p_org AND o.active
        JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=p_org AND m.project_id=p_project AND m.active
        WHERE p.id=p_actor AND p.active AND m.role IN ('publisher','administrator'))
$body$;

CREATE FUNCTION weft.draft_base(p_org uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_actor uuid,p_head boolean)
RETURNS SETOF weft.mapping_revisions LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT r.* FROM ONLY weft.mapping_revisions r JOIN ONLY weft.mappings m
        ON m.organization_id=r.organization_id AND m.project_id=r.project_id AND m.id=r.mapping_id
    WHERE r.organization_id=p_org AND r.project_id=p_project AND r.mapping_id=p_mapping AND r.id=p_revision
        AND m.backend_id='ferrule' AND (NOT p_head OR m.head_revision_id=p_revision) AND weft.draft_authorized(p_org,p_project,p_actor)
$body$;

CREATE FUNCTION weft.draft_read(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid)
RETURNS SETOF weft.mapping_drafts LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT d.* FROM ONLY weft.mapping_drafts d WHERE d.organization_id=p_org AND d.project_id=p_project AND d.mapping_id=p_mapping
        AND (p_draft IS NULL OR d.id=p_draft) AND d.author_principal_id=p_actor
        AND (d.state<>'open' OR d.expires_at>statement_timestamp()) AND d.state<>'expired'
        AND weft.draft_authorized(p_org,p_project,p_actor)
    ORDER BY d.created_at,d.id
$body$;

CREATE FUNCTION weft.draft_list_admitted(p_org uuid,p_project uuid,p_mapping uuid,p_actor uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT weft.draft_authorized(p_org,p_project,p_actor) AND EXISTS(SELECT 1 FROM ONLY weft.mappings m
        WHERE m.organization_id=p_org AND m.project_id=p_project AND m.id=p_mapping AND m.backend_id='ferrule')
$body$;

CREATE FUNCTION weft.draft_observe_creation(p_org uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_actor uuid,p_key uuid)
RETURNS TABLE(outcome text,draft_id uuid) LANGUAGE plpgsql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE;
BEGIN
    IF NOT weft.draft_list_admitted(p_org,p_project,p_mapping,p_actor) THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.author_principal_id=p_actor AND x.creation_key=p_key;
    IF FOUND THEN
        RETURN QUERY SELECT CASE WHEN d.mapping_id=p_mapping AND d.creation_base_revision_id=p_revision THEN 'replayed' ELSE 'conflict' END,d.id; RETURN;
    END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.author_principal_id=p_actor
        AND (x.state='saving' OR (x.state='open' AND x.expires_at>statement_timestamp()));
    IF FOUND THEN RETURN QUERY SELECT 'draftExists'::text,d.id; END IF;
END;
$body$;

CREATE FUNCTION weft.draft_checkpoint_read(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint)
RETURNS SETOF weft.mapping_draft_checkpoints LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT c.* FROM ONLY weft.mapping_draft_checkpoints c
    JOIN weft.draft_read(p_org,p_project,p_mapping,p_draft,p_actor) d ON d.id=c.draft_id AND d.organization_id=c.organization_id AND d.project_id=c.project_id
    WHERE d.state IN ('open','saving') AND c.version=p_version AND d.version=p_version
$body$;

CREATE FUNCTION weft.draft_checkpoint_summary(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid)
RETURNS TABLE(version bigint,native_digest text,native_byte_count integer,is_valid boolean,total_issue_count integer)
LANGUAGE sql STABLE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    SELECT c.version,c.native_digest,octet_length(c.native),c.is_valid,c.total_issue_count FROM ONLY weft.mapping_draft_checkpoints c
    JOIN weft.draft_read(p_org,p_project,p_mapping,p_draft,p_actor) d ON d.id=c.draft_id AND d.organization_id=c.organization_id AND d.project_id=c.project_id
    WHERE d.state IN ('open','saving') AND c.version=d.version
$body$;

-- Internal trusted report write; no runtime role receives execute on this helper.
CREATE FUNCTION weft.draft_checkpoint_write(p_org uuid,p_project uuid,p_draft uuid,p_version bigint,p_native bytea,
    p_evidence bytea,p_validation bytea,p_projection bytea,p_valid boolean,p_issues integer,p_engine text,p_worker text,p_adapter text)
RETURNS void LANGUAGE sql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
    INSERT INTO weft.mapping_draft_checkpoints VALUES(p_org,p_project,p_draft,p_version,p_native,encode(sha256(p_native),'hex'),
        p_evidence,p_validation,p_projection,p_valid,p_issues,p_engine,p_worker,p_adapter)
    ON CONFLICT(organization_id,project_id,draft_id) DO UPDATE SET version=excluded.version,native=excluded.native,
        native_digest=excluded.native_digest,validation_evidence=excluded.validation_evidence,validation_report=excluded.validation_report,
        projection_report=excluded.projection_report,is_valid=excluded.is_valid,total_issue_count=excluded.total_issue_count,
        engine_revision=excluded.engine_revision,worker_build=excluded.worker_build,adapter_build=excluded.adapter_build
$body$;

CREATE FUNCTION weft.draft_create(p_org uuid,p_project uuid,p_mapping uuid,p_revision uuid,p_actor uuid,p_key uuid,
    p_native bytea,p_evidence bytea,p_validation bytea,p_projection bytea,p_valid boolean,p_issues integer,p_engine text,p_worker text,p_adapter text)
RETURNS TABLE(outcome text,draft_id uuid) LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE r weft.mapping_revisions%ROWTYPE; d weft.mapping_drafts%ROWTYPE; created uuid;
BEGIN
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='A draft creation key is required.'; END IF;
    SELECT * INTO r FROM weft.draft_base(p_org,p_project,p_mapping,p_revision,p_actor,false);
    IF NOT FOUND THEN RETURN QUERY SELECT 'notAuthorizedOrNotFound'::text,NULL::uuid; RETURN; END IF;
    -- Creation keys are project/author-scoped, including requests for different mappings.
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_org::text||p_project::text||p_actor::text,0));
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.author_principal_id=p_actor AND x.creation_key=p_key;
    IF FOUND THEN
        RETURN QUERY SELECT CASE WHEN d.mapping_id=p_mapping AND d.creation_base_revision_id=p_revision THEN 'replayed' ELSE 'conflict' END,d.id; RETURN;
    END IF;
    DELETE FROM weft.mapping_draft_checkpoints c USING weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping
        AND x.author_principal_id=p_actor AND x.state='open' AND x.expires_at<=statement_timestamp()
        AND c.organization_id=x.organization_id AND c.project_id=x.project_id AND c.draft_id=x.id;
    UPDATE weft.mapping_drafts x SET state='expired',operations=NULL,operations_digest=NULL,operation_count=0
        WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.author_principal_id=p_actor
        AND x.state='open' AND x.expires_at<=statement_timestamp();
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.author_principal_id=p_actor AND x.state IN ('open','saving');
    IF FOUND THEN RETURN QUERY SELECT 'draftExists'::text,d.id; RETURN; END IF;
    INSERT INTO weft.mapping_drafts(organization_id,project_id,mapping_id,author_principal_id,creation_key,creation_base_revision_id,
        base_revision_id,base_sequence,base_digest,base_native_object_id,base_native_byte_count,operations,operations_digest)
    VALUES(p_org,p_project,p_mapping,p_actor,p_key,p_revision,p_revision,r.sequence,r.native_digest,r.native_object_id,r.native_byte_count,
        convert_to('{"format":"ferrule.edit.v1","operations":[]}','UTF8'),encode(sha256(convert_to('{"format":"ferrule.edit.v1","operations":[]}','UTF8')),'hex')) RETURNING id INTO created;
    PERFORM weft.draft_checkpoint_write(p_org,p_project,created,0,p_native,p_evidence,p_validation,p_projection,p_valid,p_issues,p_engine,p_worker,p_adapter);
    RETURN QUERY SELECT 'created'::text,created;
END;
$body$;

CREATE FUNCTION weft.draft_put_operations(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint,p_operations bytea,p_count integer)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE;
BEGIN
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.id=p_draft AND x.author_principal_id=p_actor FOR UPDATE;
    IF NOT FOUND OR d.state='expired' OR (d.state='open' AND d.expires_at<=statement_timestamp()) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    IF d.version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF d.state<>'open' THEN RETURN 'conflict'; END IF;
    IF p_operations IS NULL OR p_count IS NULL THEN RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='A bounded operation log is required.'; END IF;
    IF d.operations=p_operations AND d.operation_count=p_count THEN RETURN 'replayed'; END IF;
    DELETE FROM weft.mapping_draft_checkpoints c WHERE c.organization_id=p_org AND c.project_id=p_project AND c.draft_id=p_draft;
    UPDATE weft.mapping_drafts x SET operations=p_operations,operations_digest=encode(sha256(p_operations),'hex'),operation_count=p_count,
        version=d.version+1,changed_at=statement_timestamp(),expires_at=statement_timestamp()+interval '30 days',saving_key=NULL,saving_filename=NULL,saving_operation_id=NULL
        WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    RETURN 'succeeded';
END;
$body$;

CREATE FUNCTION weft.draft_put_checkpoint(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint,
    p_native bytea,p_evidence bytea,p_validation bytea,p_projection bytea,p_valid boolean,p_issues integer,p_engine text,p_worker text,p_adapter text)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE;
BEGIN
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.id=p_draft AND x.author_principal_id=p_actor FOR UPDATE;
    IF NOT FOUND OR d.state='expired' OR (d.state='open' AND d.expires_at<=statement_timestamp()) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    IF d.version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF d.state<>'open' THEN RETURN 'conflict'; END IF;
    PERFORM weft.draft_checkpoint_write(p_org,p_project,p_draft,p_version,p_native,p_evidence,p_validation,p_projection,p_valid,p_issues,p_engine,p_worker,p_adapter);
    RETURN 'succeeded';
END;
$body$;

CREATE FUNCTION weft.draft_rebase(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint,p_revision uuid,
    p_native bytea,p_evidence bytea,p_validation bytea,p_projection bytea,p_valid boolean,p_issues integer,p_engine text,p_worker text,p_adapter text)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE; r weft.mapping_revisions%ROWTYPE; head uuid;
BEGIN
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.id=p_draft AND x.author_principal_id=p_actor FOR UPDATE;
    IF NOT FOUND OR d.state='expired' OR (d.state='open' AND d.expires_at<=statement_timestamp()) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    IF d.version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF d.state<>'open' THEN RETURN 'conflict'; END IF;
    SELECT m.head_revision_id INTO head FROM ONLY weft.mappings m WHERE m.organization_id=p_org AND m.project_id=p_project AND m.id=p_mapping FOR SHARE;
    IF head IS DISTINCT FROM p_revision THEN RETURN 'conflict'; END IF;
    SELECT * INTO r FROM weft.draft_base(p_org,p_project,p_mapping,p_revision,p_actor,true);
    IF NOT FOUND THEN RETURN 'conflict'; END IF;
    UPDATE weft.mapping_drafts x SET base_revision_id=r.id,base_sequence=r.sequence,base_digest=r.native_digest,
        base_native_object_id=r.native_object_id,base_native_byte_count=r.native_byte_count,version=d.version+1,
        changed_at=statement_timestamp(),expires_at=statement_timestamp()+interval '30 days',saving_key=NULL,saving_filename=NULL,saving_operation_id=NULL
        WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    PERFORM weft.draft_checkpoint_write(p_org,p_project,p_draft,d.version+1,p_native,p_evidence,p_validation,p_projection,p_valid,p_issues,p_engine,p_worker,p_adapter);
    RETURN 'succeeded';
END;
$body$;

CREATE FUNCTION weft.draft_begin_save(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint,p_key uuid,p_filename text)
RETURNS text LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE; i weft.revision_ingestions%ROWTYPE; checkpoint_row weft.mapping_draft_checkpoints%ROWTYPE;
BEGIN
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.id=p_draft AND x.author_principal_id=p_actor FOR UPDATE;
    IF NOT FOUND OR d.state='expired' OR (d.state='open' AND d.expires_at<=statement_timestamp()) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    IF d.version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF d.state='saving' THEN RETURN CASE WHEN d.saving_key=p_key AND d.saving_filename=p_filename THEN 'replayed' ELSE 'conflict' END; END IF;
    IF d.state<>'open' THEN RETURN 'conflict'; END IF;
    IF d.operation_count=0 THEN RETURN 'emptyOperations'; END IF;
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.mapping_draft_checkpoints c WHERE c.organization_id=p_org AND c.project_id=p_project AND c.draft_id=p_draft AND c.version=p_version) THEN RETURN 'checkpointRequired'; END IF;
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000' OR p_filename IS NULL OR octet_length(p_filename) NOT BETWEEN 1 AND 255 OR p_filename IN ('.','..') OR p_filename ~ '[/\\[:cntrl:]]' THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Save identity or filename is invalid.';
    END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_org::text||p_project::text||p_actor::text||p_key::text,0));
    IF EXISTS(SELECT 1 FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project
        AND x.author_principal_id=p_actor AND x.saving_key=p_key AND x.id<>p_draft) THEN RETURN 'conflict'; END IF;
    SELECT * INTO i FROM ONLY weft.revision_ingestions r WHERE r.organization_id=p_org AND r.project_id=p_project
        AND r.actor_principal_id=p_actor AND r.idempotency_key=p_key;
    IF FOUND THEN
        IF d.saving_key IS DISTINCT FROM p_key THEN RETURN 'conflict'; END IF;
        SELECT * INTO STRICT checkpoint_row FROM ONLY weft.mapping_draft_checkpoints x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.draft_id=p_draft AND x.version=p_version;
        IF i.operation_kind IS DISTINCT FROM 'append' OR i.target_mapping_id IS DISTINCT FROM p_mapping
            OR i.head_condition IS DISTINCT FROM 'equal' OR i.expected_head_revision_id IS DISTINCT FROM d.base_revision_id
            OR i.native_digest IS DISTINCT FROM checkpoint_row.native_digest OR i.native_byte_count IS DISTINCT FROM octet_length(checkpoint_row.native)
            OR i.display_filename IS DISTINCT FROM p_filename THEN RETURN 'conflict'; END IF;
    END IF;
    UPDATE weft.mapping_drafts x SET state='saving',saving_key=p_key,saving_filename=p_filename,saving_operation_id=NULL
        WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    RETURN 'succeeded';
END;
$body$;

-- Recovery is based on the actual immutable ingestion receipt, not on a caller's claim.
CREATE FUNCTION weft.draft_reconcile_internal(p_org uuid,p_project uuid,p_draft uuid) RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE; i weft.revision_ingestions%ROWTYPE; c weft.mapping_draft_checkpoints%ROWTYPE;
BEGIN
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft AND x.state='saving' FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO i FROM ONLY weft.revision_ingestions r WHERE r.organization_id=p_org AND r.project_id=p_project AND r.actor_principal_id=d.author_principal_id
        AND r.idempotency_key=d.saving_key;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO c FROM ONLY weft.mapping_draft_checkpoints x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.draft_id=p_draft AND x.version=d.version;
    IF NOT FOUND OR i.operation_kind IS DISTINCT FROM 'append' OR i.target_mapping_id IS DISTINCT FROM d.mapping_id
        OR i.head_condition IS DISTINCT FROM 'equal' OR i.expected_head_revision_id IS DISTINCT FROM d.base_revision_id OR i.native_digest IS DISTINCT FROM c.native_digest
        OR i.native_byte_count IS DISTINCT FROM octet_length(c.native) OR i.display_filename IS DISTINCT FROM d.saving_filename THEN
        -- Another request by this author may have won the same idempotency key
        -- between freezing the draft and reserving ingestion. Never adopt it.
        UPDATE weft.mapping_drafts x SET state='open',saving_key=NULL,saving_filename=NULL,saving_operation_id=NULL
            WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
        RETURN;
    END IF;
    UPDATE weft.mapping_drafts x SET saving_operation_id=i.operation_id WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    IF i.phase='committed' THEN
        INSERT INTO weft.revision_edit_provenance VALUES(p_org,p_project,d.mapping_id,i.committed_revision_id,d.id,d.author_principal_id,d.base_revision_id,d.version,d.operations,d.operations_digest,d.operation_count)
            ON CONFLICT(organization_id,project_id,mapping_id,revision_id) DO NOTHING;
        DELETE FROM weft.mapping_draft_checkpoints x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.draft_id=p_draft;
        UPDATE weft.mapping_drafts x SET state='saved',saved_revision_id=i.committed_revision_id,operations=NULL,operations_digest=NULL,operation_count=0
            WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    ELSIF i.phase IN ('head_conflict','rejected','attempt_exhausted') THEN
        UPDATE weft.mapping_drafts x SET state='open' WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    END IF;
END;
$body$;

CREATE FUNCTION weft.draft_reconcile(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid) RETURNS void
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
BEGIN
    IF weft.draft_authorized(p_org,p_project,p_actor) AND EXISTS(SELECT 1 FROM ONLY weft.mapping_drafts d WHERE d.organization_id=p_org AND d.project_id=p_project AND d.mapping_id=p_mapping AND d.id=p_draft AND d.author_principal_id=p_actor) THEN
        PERFORM weft.draft_reconcile_internal(p_org,p_project,p_draft);
    END IF;
END;
$body$;

CREATE FUNCTION weft.draft_discard(p_org uuid,p_project uuid,p_mapping uuid,p_draft uuid,p_actor uuid,p_version bigint) RETURNS text
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d weft.mapping_drafts%ROWTYPE;
BEGIN
    IF NOT weft.draft_authorized(p_org,p_project,p_actor) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    PERFORM weft.draft_reconcile(p_org,p_project,p_mapping,p_draft,p_actor);
    SELECT * INTO d FROM ONLY weft.mapping_drafts x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.mapping_id=p_mapping AND x.id=p_draft AND x.author_principal_id=p_actor FOR UPDATE;
    IF NOT FOUND OR d.state='expired' OR (d.state='open' AND d.expires_at<=statement_timestamp()) THEN RETURN 'notAuthorizedOrNotFound'; END IF;
    IF d.version<>p_version THEN RETURN 'preconditionFailed'; END IF;
    IF d.state='discarded' THEN RETURN 'replayed'; END IF;
    IF d.state<>'open' THEN RETURN 'conflict'; END IF;
    DELETE FROM weft.mapping_draft_checkpoints x WHERE x.organization_id=p_org AND x.project_id=p_project AND x.draft_id=p_draft;
    UPDATE weft.mapping_drafts x SET state='discarded',operations=NULL,operations_digest=NULL,operation_count=0
        WHERE x.organization_id=p_org AND x.project_id=p_project AND x.id=p_draft;
    RETURN 'succeeded';
END;
$body$;

CREATE FUNCTION weft.draft_expire(p_limit integer) RETURNS integer
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
AS $body$
DECLARE d record; expired integer := 0;
BEGIN
    IF p_limit NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='A bounded expiry batch is required.'; END IF;
    FOR d IN SELECT organization_id,project_id,id FROM ONLY weft.mapping_drafts WHERE state='saving' ORDER BY created_at LIMIT p_limit FOR UPDATE SKIP LOCKED LOOP
        PERFORM weft.draft_reconcile_internal(d.organization_id,d.project_id,d.id);
    END LOOP;
    FOR d IN SELECT organization_id,project_id,id FROM ONLY weft.mapping_drafts WHERE state='open' AND expires_at<=statement_timestamp() ORDER BY expires_at LIMIT p_limit FOR UPDATE SKIP LOCKED LOOP
        DELETE FROM weft.mapping_draft_checkpoints x WHERE x.organization_id=d.organization_id AND x.project_id=d.project_id AND x.draft_id=d.id;
        UPDATE weft.mapping_drafts x SET state='expired',operations=NULL,operations_digest=NULL,operation_count=0 WHERE x.organization_id=d.organization_id AND x.project_id=d.project_id AND x.id=d.id;
        expired := expired+1;
    END LOOP;
    RETURN expired;
END;
$body$;

-- No direct table privileges, helper execution, inherited memberships or public defaults.
DO $grants$
DECLARE f record;
BEGIN
    FOR f IN SELECT p.oid::regprocedure AS identity,p.proname FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='weft' AND p.proname LIKE 'draft\_%' ESCAPE '\' LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.identity);
        IF f.proname IN ('draft_base','draft_read','draft_list_admitted','draft_observe_creation','draft_checkpoint_read','draft_checkpoint_summary') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO weft_draft_reader,weft_draft_writer',f.identity);
        ELSIF f.proname NOT IN ('draft_authorized','draft_checkpoint_write','draft_reconcile_internal') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO weft_draft_writer',f.identity);
        END IF;
    END LOOP;
END;
$grants$;
GRANT USAGE ON SCHEMA weft TO weft_draft_reader,weft_draft_writer;
