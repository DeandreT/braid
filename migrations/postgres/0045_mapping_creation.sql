-- Immutable project creation proposals; approval is durable before a separate fencing transaction.
DO $roles$
DECLARE r record;
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>current_user OR current_setting('server_encoding')<>'UTF8'
        OR NOT EXISTS(SELECT 1 FROM braid.schema_migrations WHERE version=44) THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation requires its ordered migration environment.';
    END IF;
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('braid_creation_reader','braid_creation_writer','braid_creation_approver'))<>3 THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation logins must be provisioned separately.';
    END IF;
    FOR r IN SELECT * FROM pg_roles WHERE rolname IN ('braid_creation_reader','braid_creation_writer','braid_creation_approver') LOOP
        IF NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
            OR NOT has_database_privilege(r.oid,current_database(),'CONNECT')
            OR has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                AND (n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE')))
            OR has_schema_privilege(r.oid,'braid','USAGE')
            OR EXISTS(SELECT 1 FROM pg_class c WHERE c.relnamespace='braid'::regnamespace AND c.relkind IN ('r','p','v','m','f')
                AND (has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER') OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_proc f WHERE f.pronamespace='braid'::regnamespace AND has_function_privilege(r.oid,f.oid,'EXECUTE')) THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation requires CONNECT-only logins.';
        END IF;
    END LOOP;
END;
$roles$;

-- V1 has one immutable proposal/snapshot per authoring session (id). No mutable session head.
CREATE TABLE braid.creation_proposals (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, id uuid NOT NULL CHECK(id<>'00000000-0000-0000-0000-000000000000'),
    author_id uuid NOT NULL, request_key uuid NOT NULL CHECK(request_key<>'00000000-0000-0000-0000-000000000000'), bundle_id uuid NOT NULL,
    fingerprint text COLLATE "C" NOT NULL CHECK(fingerprint ~ '^[0-9a-f]{64}$'), snapshot_digest text COLLATE "C" NOT NULL CHECK(snapshot_digest ~ '^[0-9a-f]{64}$'),
    payload bytea NOT NULL CHECK(octet_length(payload) BETWEEN 1 AND 67108864), payload_digest text COLLATE "C" NOT NULL CHECK(payload_digest=encode(sha256(payload),'hex')),
    native_digest text COLLATE "C" NOT NULL CHECK(native_digest ~ '^[0-9a-f]{64}$'), native_count integer NOT NULL CHECK(native_count BETWEEN 1 AND 4194304),
    backend text COLLATE "C" NOT NULL CHECK(backend='ferrule'), filename text NOT NULL, name text NOT NULL, description text NOT NULL,
    critical_assumptions text[] NOT NULL CHECK(cardinality(critical_assumptions)<=64), critical_questions boolean NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(organization_id,project_id,id), UNIQUE(organization_id,project_id,author_id,request_key),
    FOREIGN KEY(organization_id,project_id,bundle_id) REFERENCES braid.spec_bundles(organization_id,project_id,id),
    FOREIGN KEY(author_id) REFERENCES braid.principals(id),
    CHECK(char_length(name) BETWEEN 1 AND 200 AND name=btrim(name) AND char_length(description)<=4000),
    CHECK(octet_length(filename) BETWEEN 1 AND 255 AND filename NOT IN ('.','..') AND filename !~ '[/\\[:cntrl:]]')
);
CREATE TABLE braid.creation_sources (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, proposal_id uuid NOT NULL, bundle_id uuid NOT NULL,
    source_id uuid NOT NULL, source_version bigint NOT NULL, original_id uuid NOT NULL, extraction_id uuid NOT NULL,
    source_digest text COLLATE "C" NOT NULL, source_count integer NOT NULL, format integer NOT NULL,
    extractor_version integer NOT NULL, profile_digest text COLLATE "C" NOT NULL, evidence_digest text COLLATE "C" NOT NULL, evidence_count integer NOT NULL,
    PRIMARY KEY(organization_id,project_id,proposal_id,source_id),
    FOREIGN KEY(organization_id,project_id,proposal_id) REFERENCES braid.creation_proposals(organization_id,project_id,id),
    FOREIGN KEY(organization_id,project_id,bundle_id,original_id) REFERENCES braid.spec_objects(organization_id,project_id,bundle_id,id),
    FOREIGN KEY(organization_id,project_id,bundle_id,extraction_id) REFERENCES braid.spec_objects(organization_id,project_id,bundle_id,id),
    CHECK(source_version>0 AND source_count BETWEEN 0 AND 10485760 AND evidence_count BETWEEN 1 AND 10485760 AND format BETWEEN 0 AND 3 AND extractor_version>0),
    CHECK(source_digest ~ '^[0-9a-f]{64}$' AND profile_digest ~ '^[0-9a-f]{64}$' AND evidence_digest ~ '^[0-9a-f]{64}$')
);
CREATE TABLE braid.creation_approvals (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, proposal_id uuid NOT NULL,
    fingerprint text COLLATE "C" NOT NULL, author_id uuid NOT NULL, request_key uuid NOT NULL, assumptions text[] NOT NULL,
    approved_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(organization_id,project_id,proposal_id),
    FOREIGN KEY(organization_id,project_id,proposal_id) REFERENCES braid.creation_proposals(organization_id,project_id,id)
);
DO $tables$
DECLARE n text;
BEGIN
    FOREACH n IN ARRAY ARRAY['creation_proposals','creation_sources','creation_approvals'] LOOP
        EXECUTE format('ALTER TABLE braid.%I ENABLE ROW LEVEL SECURITY',n);
        EXECUTE format('CREATE POLICY %I ON braid.%I USING(organization_id=NULLIF(current_setting(''braid.organization_id'',true),'''')::uuid) WITH CHECK(organization_id=NULLIF(current_setting(''braid.organization_id'',true),'''')::uuid)',n||'_scope',n);
        EXECUTE format('REVOKE ALL ON braid.%I FROM PUBLIC',n);
    END LOOP;
END;
$tables$;

-- Explicit order, NOWAIT: an ingestion transaction may already own its operation row.
-- Never wait behind a queued revoker while its caller holds the outer lease.
CREATE FUNCTION braid.creation_authorize(a uuid,o uuid,j uuid,w boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    PERFORM 1 FROM ONLY braid.principals WHERE id=a AND active FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation unavailable.'; END IF;
    PERFORM 1 FROM ONLY braid.organization_memberships WHERE organization_id=o AND principal_id=a AND active FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation unavailable.'; END IF;
    PERFORM 1 FROM ONLY braid.project_memberships WHERE organization_id=o AND project_id=j AND principal_id=a AND active AND (NOT w OR role IN ('publisher','administrator')) FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation unavailable.'; END IF;
    PERFORM 1 FROM ONLY braid.project_payload_read_grants WHERE organization_id=o AND project_id=j AND principal_id=a AND active FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation unavailable.'; END IF;
    PERFORM 1 FROM ONLY braid.projects WHERE organization_id=o AND id=j FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation unavailable.'; END IF;
END;
$body$;

CREATE FUNCTION braid.creation_snapshot_check(a uuid,o uuid,j uuid,b uuid,selection jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE x jsonb; original braid.spec_objects; extracted braid.spec_objects; store uuid;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    IF jsonb_typeof(selection) IS DISTINCT FROM 'array' OR jsonb_array_length(selection) NOT BETWEEN 1 AND 20 THEN
        RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid creation selection.';
    END IF;
    -- Store lifecycle fences precede the bundle fence. Identity/state are rechecked below.
    FOR store IN SELECT DISTINCT ob.store_id FROM ONLY braid.spec_objects ob
        WHERE ob.organization_id=o AND ob.project_id=j AND ob.bundle_id=b AND ob.id IN (
            SELECT (v->>'original')::uuid FROM jsonb_array_elements(selection) v UNION ALL SELECT (v->>'extraction')::uuid FROM jsonb_array_elements(selection) v) ORDER BY ob.store_id LOOP
        PERFORM 1 FROM ONLY braid.spec_stores WHERE id=store AND active FOR SHARE NOWAIT;
        IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation store unavailable.'; END IF;
    END LOOP;
    PERFORM 1 FROM ONLY braid.spec_bundles WHERE organization_id=o AND project_id=j AND id=b AND NOT tombstoned FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation snapshot unavailable.'; END IF;
    FOR x IN SELECT * FROM jsonb_array_elements(selection) LOOP
        PERFORM 1 FROM ONLY braid.spec_sources WHERE organization_id=o AND project_id=j AND bundle_id=b
            AND id=(x->>'source')::uuid AND version=(x->>'version')::bigint AND NOT tombstoned
            AND digest=x->>'sourceDigest' AND byte_count=(x->>'sourceCount')::integer AND format=(x->>'format')::integer;
        IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation source unavailable.'; END IF;
        SELECT * INTO original FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=(x->>'original')::uuid;
        IF NOT FOUND OR original.source_id IS DISTINCT FROM (x->>'source')::uuid OR original.source_version IS DISTINCT FROM (x->>'version')::bigint
            OR original.kind<>1 OR original.state<>'published' OR original.digest IS DISTINCT FROM x->>'sourceDigest' OR original.byte_count IS DISTINCT FROM (x->>'sourceCount')::integer THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation original unavailable.';
        END IF;
        SELECT * INTO extracted FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=(x->>'extraction')::uuid;
        IF NOT FOUND OR extracted.source_id<>original.source_id OR extracted.source_version<>original.source_version OR extracted.kind<>2 OR extracted.state<>'published'
            OR extracted.extraction_outcome<>1 OR extracted.source_digest<>original.digest OR extracted.extractor_version IS DISTINCT FROM (x->>'extractor')::integer
            OR extracted.profile_digest IS DISTINCT FROM x->>'profile' OR extracted.digest IS DISTINCT FROM x->>'evidence' OR extracted.byte_count IS DISTINCT FROM (x->>'evidenceCount')::integer THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation extraction unavailable.';
        END IF;
    END LOOP;
END;
$body$;

-- Trusted registry lookup. Deterministic object selection is then pinned in creation_sources.
CREATE FUNCTION braid.creation_select(a uuid,o uuid,j uuid,b uuid,selection jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE x jsonb; result jsonb='[]'; original uuid; extracted uuid;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    IF jsonb_typeof(selection) IS DISTINCT FROM 'array' OR jsonb_array_length(selection) NOT BETWEEN 1 AND 20 THEN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid selection.'; END IF;
    FOR x IN SELECT * FROM jsonb_array_elements(selection) LOOP
        SELECT id INTO original FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b
            AND source_id=(x->>'source')::uuid AND source_version=(x->>'version')::bigint AND kind=1 AND state='published' ORDER BY id LIMIT 1;
        SELECT id INTO extracted FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b
            AND source_id=(x->>'source')::uuid AND source_version=(x->>'version')::bigint AND kind=2 AND state='published'
            AND extractor_version=(x->>'extractor')::integer AND profile_digest=x->>'profile' AND digest=x->>'evidence' AND extraction_outcome=1 ORDER BY id LIMIT 1;
        result=result||jsonb_build_array(x||jsonb_build_object('original',original,'extraction',extracted,
            'sourceCount',coalesce((x->>'sourceCount')::integer,(SELECT byte_count FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=original)),
            'evidenceCount',coalesce((x->>'evidenceCount')::integer,(SELECT byte_count FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=extracted))));
    END LOOP;
    PERFORM braid.creation_snapshot_check(a,o,j,b,result);
    RETURN result;
END;
$body$;

CREATE FUNCTION braid.creation_selection(o uuid,j uuid,id uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
SELECT jsonb_agg(jsonb_build_object('source',source_id,'version',source_version,'original',original_id,'extraction',extraction_id,
    'sourceDigest',source_digest,'sourceCount',source_count,'format',format,'extractor',extractor_version,'profile',profile_digest,'evidence',evidence_digest,'evidenceCount',evidence_count) ORDER BY source_id)
FROM ONLY braid.creation_sources WHERE organization_id=o AND project_id=j AND proposal_id=id
$body$;

CREATE FUNCTION braid.creation_retain(a uuid,o uuid,j uuid,id uuid,k uuid,b uuid,f text,s text,content bytea,n text,nc integer,backend text,filename text,name text,description text,assumptions text[],questions boolean,selection jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE p braid.creation_proposals; x jsonb; chosen jsonb;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    -- Same lock is taken by every create reservation, closing retain-vs-reserve key races.
    PERFORM pg_advisory_xact_lock(hashtextextended(concat_ws('/',o,j,a,k),4521));
    SELECT * INTO p FROM ONLY braid.creation_proposals WHERE organization_id=o AND project_id=j AND (creation_proposals.id=creation_retain.id OR (author_id=a AND request_key=k));
    IF FOUND THEN
        IF p.id<>id OR p.author_id<>a OR p.request_key<>k OR p.fingerprint<>f OR p.payload<>content THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Creation replay conflicts.'; END IF;
        PERFORM braid.creation_snapshot_check(a,o,j,p.bundle_id,braid.creation_selection(o,j,id));
        RETURN;
    END IF;
    IF EXISTS(SELECT 1 FROM ONLY braid.revision_ingestions WHERE organization_id=o AND project_id=j AND actor_principal_id=a AND idempotency_key=k AND operation_kind='create') THEN
        RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Creation key already used.';
    END IF;
    IF assumptions IS NULL OR questions IS NULL OR cardinality(assumptions)>64 OR EXISTS(SELECT 1 FROM unnest(assumptions) v WHERE v IS NULL OR v !~ '^[A-Za-z0-9]{1,64}$')
        OR cardinality(assumptions)<>(SELECT count(DISTINCT v) FROM unnest(assumptions) v) THEN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid review facts.'; END IF;
    chosen=braid.creation_select(a,o,j,b,selection);
    INSERT INTO braid.creation_proposals(organization_id,project_id,id,author_id,request_key,bundle_id,fingerprint,snapshot_digest,payload,payload_digest,native_digest,native_count,backend,filename,name,description,critical_assumptions,critical_questions)
        VALUES(o,j,id,a,k,b,f,s,content,encode(sha256(content),'hex'),n,nc,backend,filename,name,description,assumptions,questions);
    FOR x IN SELECT * FROM jsonb_array_elements(chosen) LOOP
        INSERT INTO braid.creation_sources VALUES(o,j,id,b,(x->>'source')::uuid,(x->>'version')::bigint,(x->>'original')::uuid,(x->>'extraction')::uuid,
            x->>'sourceDigest',(x->>'sourceCount')::integer,(x->>'format')::integer,(x->>'extractor')::integer,x->>'profile',x->>'evidence',(x->>'evidenceCount')::integer);
    END LOOP;
END;
$body$;

CREATE FUNCTION braid.creation_read(a uuid,o uuid,j uuid,id uuid) RETURNS TABLE(content bytea,fingerprint text,assumptions text[])
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    PERFORM braid.creation_authorize(a,o,j,false);
    -- MVCC only: retained bytes remain reviewable after spec staleness; content revocation still denies.
    RETURN QUERY SELECT p.payload,p.fingerprint,d.assumptions FROM ONLY braid.creation_proposals p LEFT JOIN ONLY braid.creation_approvals d
        ON d.organization_id=p.organization_id AND d.project_id=p.project_id AND d.proposal_id=p.id
        WHERE p.organization_id=o AND p.project_id=j AND p.id=creation_read.id AND p.author_id=a;
END;
$body$;

CREATE FUNCTION braid.creation_approve(a uuid,o uuid,j uuid,id uuid,f text,accepted text[]) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE p braid.creation_proposals; d braid.creation_approvals;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    SELECT * INTO p FROM ONLY braid.creation_proposals WHERE organization_id=o AND project_id=j AND creation_proposals.id=creation_approve.id AND author_id=a;
    IF NOT FOUND OR p.fingerprint<>f OR p.critical_questions OR accepted IS DISTINCT FROM p.critical_assumptions THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation decision refused.'; END IF;
    PERFORM braid.creation_snapshot_check(a,o,j,p.bundle_id,braid.creation_selection(o,j,id));
    INSERT INTO braid.creation_approvals VALUES(o,j,id,f,a,p.request_key,accepted,clock_timestamp()) ON CONFLICT DO NOTHING;
    SELECT * INTO STRICT d FROM ONLY braid.creation_approvals WHERE organization_id=o AND project_id=j AND proposal_id=id;
    IF d.fingerprint<>f OR d.author_id<>a OR d.request_key<>p.request_key OR d.assumptions<>accepted THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Creation decision conflicts.'; END IF;
END;
$body$;

CREATE FUNCTION braid.creation_fence(a uuid,o uuid,j uuid,id uuid,f text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE p braid.creation_proposals;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    SELECT p0.* INTO p FROM ONLY braid.creation_proposals p0 JOIN ONLY braid.creation_approvals d
        ON d.organization_id=p0.organization_id AND d.project_id=p0.project_id AND d.proposal_id=p0.id
        AND d.author_id=p0.author_id AND d.fingerprint=p0.fingerprint AND d.request_key=p0.request_key AND d.assumptions=p0.critical_assumptions
        WHERE p0.organization_id=o AND p0.project_id=j AND p0.id=creation_fence.id AND p0.author_id=a AND p0.fingerprint=f AND NOT p0.critical_questions;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation approval unavailable.'; END IF;
    PERFORM braid.creation_snapshot_check(a,o,j,p.bundle_id,braid.creation_selection(o,j,id));
END;
$body$;

CREATE FUNCTION braid.creation_ingestion_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE p braid.creation_proposals;
BEGIN
    IF NEW.operation_kind<>'create' OR (TG_OP='UPDATE' AND (NEW.phase<>'committed' OR OLD.phase='committed')) THEN RETURN NEW; END IF;
    IF TG_OP='INSERT' THEN PERFORM pg_advisory_xact_lock(hashtextextended(concat_ws('/',NEW.organization_id,NEW.project_id,NEW.actor_principal_id,NEW.idempotency_key),4521)); END IF;
    SELECT * INTO p FROM ONLY braid.creation_proposals WHERE organization_id=NEW.organization_id AND project_id=NEW.project_id
        AND author_id=NEW.actor_principal_id AND request_key=NEW.idempotency_key;
    IF NOT FOUND THEN RETURN NEW; END IF;
    IF ROW(NEW.native_digest,NEW.native_byte_count,NEW.backend_id,NEW.display_filename,NEW.create_name,NEW.create_description)
        IS DISTINCT FROM ROW(p.native_digest,p.native_count::bigint,p.backend,p.filename,p.name,p.description) THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation ingestion intent mismatch.';
    END IF;
    PERFORM braid.creation_fence(p.author_id,p.organization_id,p.project_id,p.id,p.fingerprint);
    RETURN NEW;
END;
$body$;
CREATE TRIGGER revision_ingestions_creation_guard BEFORE INSERT OR UPDATE ON braid.revision_ingestions FOR EACH ROW EXECUTE FUNCTION braid.creation_ingestion_guard();

CREATE FUNCTION braid.creation_schema_version() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
SELECT checksum FROM ONLY braid.schema_migrations WHERE version=45
$body$;

DO $grants$
DECLARE f record;
BEGIN
    FOR f IN SELECT oid,proname FROM pg_proc WHERE pronamespace='braid'::regnamespace AND proname LIKE 'creation\_%' ESCAPE '\' LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.oid::regprocedure);
        IF f.proname IN ('creation_read','creation_schema_version') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO braid_creation_reader,braid_creation_writer,braid_creation_approver',f.oid::regprocedure); END IF;
        IF f.proname IN ('creation_retain','creation_select') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO braid_creation_writer',f.oid::regprocedure); END IF;
        IF f.proname IN ('creation_approve','creation_fence') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO braid_creation_approver',f.oid::regprocedure); END IF;
    END LOOP;
END;
$grants$;
GRANT USAGE ON SCHEMA braid TO braid_creation_reader,braid_creation_writer,braid_creation_approver;
