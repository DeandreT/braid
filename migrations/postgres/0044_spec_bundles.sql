-- Requires the real 0042 XML and 0043 proposal migrations. Never install against a fabricated sequence.
DO $roles$
DECLARE r record;
BEGIN
    IF current_user <> 'weft_migrator' OR session_user <> current_user OR current_setting('server_encoding') <> 'UTF8'
        OR current_setting('standard_conforming_strings') <> 'on'
        OR NOT EXISTS(SELECT 1 FROM weft.schema_migrations WHERE version=42)
        OR NOT EXISTS(SELECT 1 FROM weft.schema_migrations WHERE version=43) THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification storage requires its ordered migration environment.';
    END IF;
    IF (SELECT count(*) FROM pg_roles WHERE rolname IN ('weft_spec_reader','weft_spec_writer','weft_spec_maintenance')) <> 3 THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification storage requires separately provisioned logins.';
    END IF;
    FOR r IN SELECT * FROM pg_roles WHERE rolname IN ('weft_spec_reader','weft_spec_writer','weft_spec_maintenance') LOOP
        IF NOT r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
            OR NOT has_database_privilege(r.oid,current_database(),'CONNECT')
            OR has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
            OR has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
            OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
            OR EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname NOT LIKE 'pg_temp_%' AND n.nspname NOT LIKE 'pg_toast_temp_%'
                AND (n.nspowner=r.oid OR has_schema_privilege(r.oid,n.oid,'CREATE')))
            OR has_schema_privilege(r.oid,'weft','USAGE')
            OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='weft'
                AND c.relkind IN ('r','p','v','m','f') AND (has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
            OR EXISTS(SELECT 1 FROM pg_proc f JOIN pg_namespace n ON n.oid=f.pronamespace WHERE n.nspname='weft' AND has_function_privilege(r.oid,f.oid,'EXECUTE')) THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification logins require CONNECT-only provisioning.';
        END IF;
    END LOOP;
END;
$roles$;

CREATE TABLE weft.spec_stores (
    id uuid PRIMARY KEY CHECK(id <> '00000000-0000-0000-0000-000000000000'),
    kind text COLLATE "C" NOT NULL CHECK(kind IN ('blob','filesystem')),
    location_digest text COLLATE "C" NOT NULL UNIQUE CHECK(location_digest ~ '^[0-9a-f]{64}$'),
    active boolean NOT NULL DEFAULT false
);
-- Store activation/provisioning is owner-only, not a runtime capability or a migration side effect.
CREATE TABLE weft.spec_bundles (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, id uuid NOT NULL CHECK(id <> '00000000-0000-0000-0000-000000000000'),
    creator_id uuid NOT NULL, creation_key uuid NOT NULL CHECK(creation_key <> '00000000-0000-0000-0000-000000000000'),
    version bigint NOT NULL DEFAULT 1 CHECK(version>0), tombstoned boolean NOT NULL DEFAULT false,
    tombstone_key uuid, tombstone_version bigint,
    PRIMARY KEY(organization_id,project_id,id), UNIQUE(organization_id,project_id,creator_id,creation_key),
    FOREIGN KEY(organization_id,project_id) REFERENCES weft.projects(organization_id,id),
    FOREIGN KEY(creator_id) REFERENCES weft.principals(id),
    CHECK(tombstoned=(tombstone_key IS NOT NULL) AND tombstoned=(tombstone_version IS NOT NULL))
);
CREATE TABLE weft.spec_sources (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, bundle_id uuid NOT NULL,
    id uuid NOT NULL CHECK(id <> '00000000-0000-0000-0000-000000000000'), version bigint NOT NULL CHECK(version>0),
    format integer NOT NULL CHECK(format BETWEEN 0 AND 3), digest text COLLATE "C" NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    byte_count integer NOT NULL CHECK(byte_count BETWEEN 0 AND 10485760), tombstoned boolean NOT NULL DEFAULT false,
    tombstone_key uuid, tombstone_version bigint,
    PRIMARY KEY(organization_id,project_id,bundle_id,id,version),
    FOREIGN KEY(organization_id,project_id,bundle_id) REFERENCES weft.spec_bundles(organization_id,project_id,id),
    CHECK((tombstone_key IS NULL)=(tombstone_version IS NULL))
);
CREATE TABLE weft.spec_objects (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, bundle_id uuid NOT NULL,
    id uuid NOT NULL CHECK(id <> '00000000-0000-0000-0000-000000000000'), source_id uuid NOT NULL, source_version bigint NOT NULL,
    kind integer NOT NULL CHECK(kind IN (1,2)), digest text COLLATE "C" NOT NULL CHECK(digest ~ '^[0-9a-f]{64}$'),
    byte_count integer NOT NULL CHECK(byte_count BETWEEN 0 AND 10485760), store_id uuid NOT NULL REFERENCES weft.spec_stores(id),
    owner_id uuid NOT NULL REFERENCES weft.principals(id), attempt_id uuid NOT NULL CHECK(attempt_id <> '00000000-0000-0000-0000-000000000000'),
    state text COLLATE "C" NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','published','retiring','retired')),
    retirement_id uuid, extractor_version integer, profile_digest text COLLATE "C", extraction_outcome integer,
    source_digest text COLLATE "C", reservation_version bigint NOT NULL CHECK(reservation_version>0),
    PRIMARY KEY(organization_id,project_id,bundle_id,id), UNIQUE(store_id,attempt_id), UNIQUE(store_id,retirement_id),
    FOREIGN KEY(organization_id,project_id,bundle_id,source_id,source_version) REFERENCES weft.spec_sources(organization_id,project_id,bundle_id,id,version),
    CHECK((state IN ('retiring','retired'))=(retirement_id IS NOT NULL)),
    CHECK((kind=1 AND extractor_version IS NULL AND profile_digest IS NULL AND extraction_outcome IS NULL AND source_digest IS NULL)
        OR (kind=2 AND extractor_version>0 AND profile_digest ~ '^[0-9a-f]{64}$' AND extraction_outcome BETWEEN 1 AND 4 AND source_digest ~ '^[0-9a-f]{64}$'
            AND extractor_version IS NOT NULL AND profile_digest IS NOT NULL AND extraction_outcome IS NOT NULL AND source_digest IS NOT NULL))
);
CREATE UNIQUE INDEX spec_original_once ON weft.spec_objects(organization_id,project_id,bundle_id,source_id,source_version) WHERE kind=1;
CREATE INDEX spec_retirement_queue ON weft.spec_objects(store_id,organization_id,project_id,bundle_id,id) WHERE state='retiring';
CREATE TABLE weft.spec_temporaries (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, bundle_id uuid NOT NULL, object_id uuid NOT NULL,
    id uuid NOT NULL CHECK(id <> '00000000-0000-0000-0000-000000000000'), attempt_id uuid NOT NULL,
    phase integer NOT NULL CHECK(phase BETWEEN 1 AND 3), evidence bytea NOT NULL CHECK(octet_length(evidence)=196),
    seal_digest text COLLATE "C" NOT NULL CHECK(seal_digest ~ '^[0-9a-f]{64}$'), previous_digest text COLLATE "C",
    retirement_id uuid, retired boolean NOT NULL DEFAULT false,
    PRIMARY KEY(organization_id,project_id,bundle_id,object_id,id),
    FOREIGN KEY(organization_id,project_id,bundle_id,object_id) REFERENCES weft.spec_objects(organization_id,project_id,bundle_id,id),
    CHECK((phase=1)=(previous_digest IS NULL)), CHECK(NOT retired OR retirement_id IS NOT NULL)
);
CREATE TABLE weft.spec_temporary_seals (
    organization_id uuid NOT NULL, project_id uuid NOT NULL, bundle_id uuid NOT NULL, object_id uuid NOT NULL, temporary_id uuid NOT NULL,
    phase integer NOT NULL CHECK(phase BETWEEN 1 AND 3), evidence bytea NOT NULL CHECK(octet_length(evidence)=196),
    seal_digest text COLLATE "C" NOT NULL CHECK(seal_digest ~ '^[0-9a-f]{64}$'), previous_digest text COLLATE "C",
    PRIMARY KEY(organization_id,project_id,bundle_id,object_id,temporary_id,phase),
    FOREIGN KEY(organization_id,project_id,bundle_id,object_id,temporary_id) REFERENCES weft.spec_temporaries(organization_id,project_id,bundle_id,object_id,id),
    CHECK((phase=1)=(previous_digest IS NULL))
);

DO $tables$
DECLARE n text;
BEGIN
    FOREACH n IN ARRAY ARRAY['spec_bundles','spec_sources','spec_objects','spec_temporaries','spec_temporary_seals'] LOOP
        EXECUTE format('ALTER TABLE weft.%I ENABLE ROW LEVEL SECURITY',n);
        EXECUTE format('CREATE POLICY %I ON weft.%I USING(organization_id=NULLIF(current_setting(''weft.organization_id'',true),'''')::uuid) WITH CHECK(organization_id=NULLIF(current_setting(''weft.organization_id'',true),'''')::uuid)',n||'_scope',n);
    END LOOP;
END;
$tables$;
REVOKE ALL ON weft.spec_stores,weft.spec_bundles,weft.spec_sources,weft.spec_objects,weft.spec_temporaries,weft.spec_temporary_seals FROM PUBLIC;

-- Every authority row is SHARE-locked through the containing transaction/callback, so revocation cannot pass unnoticed.
-- Explicit payload grant is required in addition to project membership. This grants NO AI-sharing capability.
CREATE FUNCTION weft.spec_authorize(p_actor uuid,p_org uuid,p_project uuid,p_write boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    PERFORM p.id FROM ONLY weft.principals p
        JOIN ONLY weft.organization_memberships o ON o.principal_id=p.id AND o.organization_id=p_org AND o.active
        JOIN ONLY weft.project_memberships m ON m.principal_id=p.id AND m.organization_id=p_org AND m.project_id=p_project AND m.active
        JOIN ONLY weft.project_payload_read_grants g ON g.principal_id=p.id AND g.organization_id=p_org AND g.project_id=p_project AND g.active
        JOIN ONLY weft.projects j ON j.organization_id=p_org AND j.id=p_project
        WHERE p.id=p_actor AND p.active AND (NOT p_write OR m.role IN ('publisher','administrator')) FOR SHARE OF p,o,m,g,j;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification content access is unavailable.'; END IF;
END;
$body$;
CREATE FUNCTION weft.spec_bundle_create(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_key uuid) RETURNS SETOF weft.spec_bundles
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE b weft.spec_bundles;
BEGIN
    PERFORM weft.spec_authorize(p_actor,p_org,p_project,true);
    INSERT INTO weft.spec_bundles(organization_id,project_id,id,creator_id,creation_key,version,tombstoned,tombstone_key,tombstone_version)
        VALUES(p_org,p_project,p_bundle,p_actor,p_key,1,false,NULL,NULL) ON CONFLICT DO NOTHING;
    SELECT * INTO b FROM ONLY weft.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND creator_id=p_actor AND creation_key=p_key FOR UPDATE;
    IF NOT FOUND OR b.id<>p_bundle THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Specification creation conflicts.'; END IF;
    RETURN NEXT b;
END;
$body$;
CREATE FUNCTION weft.spec_store_admit(p_store uuid,p_kind text,p_location text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    PERFORM 1 FROM ONLY weft.spec_stores WHERE id=p_store AND kind=p_kind AND location_digest=p_location AND active FOR SHARE;
    RETURN FOUND;
END;
$body$;
CREATE FUNCTION weft.spec_bundle_authorize(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_write boolean) RETURNS SETOF weft.spec_bundles
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    PERFORM weft.spec_authorize(p_actor,p_org,p_project,p_write);
    RETURN QUERY SELECT * FROM ONLY weft.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle AND NOT tombstoned FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification bundle unavailable.'; END IF;
END;
$body$;
CREATE FUNCTION weft.spec_reserve(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_source uuid,p_source_version bigint,
    p_format integer,p_object uuid,p_kind integer,p_digest text,p_count integer,p_store uuid,p_attempt uuid,p_version bigint,
    p_extractor integer,p_profile text,p_outcome integer,p_source_digest text) RETURNS SETOF weft.spec_objects
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE b weft.spec_bundles; s weft.spec_sources; o weft.spec_objects;
BEGIN
    PERFORM weft.spec_authorize(p_actor,p_org,p_project,true);
    PERFORM 1 FROM ONLY weft.spec_stores WHERE id=p_store AND active FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification store unavailable.'; END IF;
    SELECT * INTO b FROM ONLY weft.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle FOR UPDATE;
    IF NOT FOUND OR b.tombstoned THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification bundle unavailable.'; END IF;
    SELECT * INTO o FROM ONLY weft.spec_objects WHERE store_id=p_store AND attempt_id=p_attempt;
    IF FOUND THEN
        IF ROW(o.organization_id,o.project_id,o.bundle_id,o.source_id,o.source_version,o.id,o.kind,o.digest,o.byte_count,o.owner_id,o.reservation_version,o.extractor_version,o.profile_digest,o.extraction_outcome,o.source_digest)
            IS DISTINCT FROM ROW(p_org,p_project,p_bundle,p_source,p_source_version,p_object,p_kind,p_digest,p_count,p_actor,p_version,p_extractor,p_profile,p_outcome,p_source_digest)
            OR o.state IN ('retiring','retired') OR NOT EXISTS(SELECT 1 FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_source AND version=p_source_version AND format=p_format AND NOT tombstoned) THEN
            RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Specification reservation conflicts.';
        END IF;
        RETURN NEXT o; RETURN;
    END IF;
    IF b.version<>p_version THEN RAISE EXCEPTION USING ERRCODE='40001',MESSAGE='Specification version changed.'; END IF;
    IF (SELECT count(*) FROM ONLY weft.spec_objects WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)>=128 THEN
        RAISE EXCEPTION USING ERRCODE='54000',MESSAGE='Specification object limit exceeded.';
    END IF;
    SELECT * INTO s FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_source AND version=p_source_version;
    IF p_kind=1 THEN
        IF FOUND THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Specification source already reserved.'; END IF;
        IF (SELECT count(*) FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)>=20
            OR (SELECT coalesce(sum(byte_count),0) FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)+p_count>52428800 THEN
            RAISE EXCEPTION USING ERRCODE='54000',MESSAGE='Specification source limit exceeded.';
        END IF;
        INSERT INTO weft.spec_sources(organization_id,project_id,bundle_id,id,version,format,digest,byte_count,tombstoned,tombstone_key,tombstone_version)
            VALUES(p_org,p_project,p_bundle,p_source,p_source_version,p_format,p_digest,p_count,false,NULL,NULL);
    ELSE
        IF NOT FOUND OR s.tombstoned OR s.digest IS DISTINCT FROM p_source_digest OR s.format<>p_format OR NOT EXISTS(
            SELECT 1 FROM ONLY weft.spec_objects WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND source_id=p_source AND source_version=p_source_version AND kind=1 AND state='published') THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification extraction source unavailable.';
        END IF;
    END IF;
    INSERT INTO weft.spec_objects(organization_id,project_id,bundle_id,id,source_id,source_version,kind,digest,byte_count,store_id,owner_id,attempt_id,
        reservation_version,extractor_version,profile_digest,extraction_outcome,source_digest,state,retirement_id)
        VALUES(p_org,p_project,p_bundle,p_object,p_source,p_source_version,p_kind,p_digest,p_count,p_store,p_actor,p_attempt,p_version,p_extractor,p_profile,p_outcome,p_source_digest,'pending',NULL) RETURNING * INTO o;
    UPDATE weft.spec_bundles SET version=version+1 WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle;
    RETURN NEXT o;
END;
$body$;

-- This gate remains locked for the ENTIRE physical callback and database acknowledgement in the .NET registry.
CREATE FUNCTION weft.spec_object_gate(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_object uuid,p_claim uuid,p_mode text) RETURNS SETOF weft.spec_objects
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE b weft.spec_bundles; o weft.spec_objects; s weft.spec_sources;
BEGIN
    IF p_mode NOT IN ('publish','read','retire') OR p_mode IS NULL THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Invalid specification operation.'; END IF;
    IF session_user='weft_spec_reader' AND p_mode<>'read' THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Reader is read-only.'; END IF;
    IF session_user='weft_spec_maintenance' THEN
        IF p_mode<>'retire' OR p_actor IS NOT NULL THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Maintenance is retirement-only.'; END IF;
    ELSE PERFORM weft.spec_authorize(p_actor,p_org,p_project,p_mode<>'read'); END IF;
    SELECT * INTO b FROM ONLY weft.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification unavailable.'; END IF;
    SELECT * INTO o FROM ONLY weft.spec_objects WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_object FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification unavailable.'; END IF;
    SELECT * INTO s FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=o.source_id AND version=o.source_version;
    PERFORM 1 FROM ONLY weft.spec_stores WHERE id=o.store_id AND active FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification store unavailable.'; END IF;
    IF (p_mode='publish' AND (b.tombstoned OR s.tombstoned OR o.state NOT IN ('pending','published') OR o.owner_id<>p_actor OR o.attempt_id IS DISTINCT FROM p_claim))
        OR (p_mode='read' AND (b.tombstoned OR s.tombstoned OR o.state<>'published' OR p_claim IS NOT NULL))
        OR (p_mode='retire' AND (NOT s.tombstoned OR o.state NOT IN ('retiring','retired') OR o.retirement_id IS DISTINCT FROM p_claim)) THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification lifecycle admission refused.';
    END IF;
    RETURN NEXT o;
END;
$body$;
CREATE FUNCTION weft.spec_object_complete(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_object uuid,p_claim uuid,p_mode text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    IF p_mode NOT IN ('publish','retire') OR p_mode IS NULL THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Invalid completion.'; END IF;
    PERFORM weft.spec_object_gate(p_actor,p_org,p_project,p_bundle,p_object,p_claim,p_mode);
    IF p_mode='retire' AND EXISTS(SELECT 1 FROM ONLY weft.spec_temporaries WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object AND NOT retired) THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Temporary retirement remains outstanding.';
    END IF;
    UPDATE weft.spec_objects SET state=CASE WHEN p_mode='publish' THEN 'published' ELSE 'retired' END
        WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_object;
END;
$body$;
CREATE FUNCTION weft.spec_tombstone(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_source uuid,p_source_version bigint,p_version bigint,p_key uuid) RETURNS SETOF weft.spec_bundles
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE b weft.spec_bundles; s weft.spec_sources;
BEGIN
    IF p_key IS NULL OR p_key='00000000-0000-0000-0000-000000000000' OR (p_source IS NULL)<>(p_source_version IS NULL) THEN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid tombstone request.'; END IF;
    PERFORM weft.spec_authorize(p_actor,p_org,p_project,true);
    SELECT * INTO b FROM ONLY weft.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification unavailable.'; END IF;
    IF p_source IS NULL AND b.tombstone_key=p_key AND b.tombstone_version=p_version THEN RETURN NEXT b; RETURN; END IF;
    IF p_source IS NOT NULL THEN
        SELECT * INTO s FROM ONLY weft.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_source AND version=p_source_version;
        IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification unavailable.'; END IF;
        IF s.tombstone_key=p_key AND s.tombstone_version=p_version THEN RETURN NEXT b; RETURN; END IF;
    END IF;
    IF b.tombstoned OR b.version<>p_version OR (p_source IS NOT NULL AND s.tombstoned) THEN RAISE EXCEPTION USING ERRCODE='40001',MESSAGE='Specification version changed.'; END IF;
    UPDATE weft.spec_sources SET tombstoned=true,
        tombstone_key=CASE WHEN p_source IS NOT NULL THEN p_key ELSE tombstone_key END,
        tombstone_version=CASE WHEN p_source IS NOT NULL THEN p_version ELSE tombstone_version END
        WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND (p_source IS NULL OR (id=p_source AND version=p_source_version));
    UPDATE weft.spec_objects SET state='retiring',retirement_id=coalesce(retirement_id,gen_random_uuid())
        WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND state IN ('pending','published')
            AND (p_source IS NULL OR (source_id=p_source AND source_version=p_source_version));
    UPDATE weft.spec_temporaries t SET retirement_id=coalesce(t.retirement_id,gen_random_uuid())
        FROM weft.spec_objects o WHERE o.organization_id=p_org AND o.project_id=p_project AND o.bundle_id=p_bundle
            AND o.organization_id=t.organization_id AND o.project_id=t.project_id AND o.bundle_id=t.bundle_id AND o.id=t.object_id
            AND o.state IN ('retiring','retired');
    UPDATE weft.spec_bundles SET version=version+1,tombstoned=(p_source IS NULL),
        tombstone_key=CASE WHEN p_source IS NULL THEN p_key ELSE tombstone_key END,
        tombstone_version=CASE WHEN p_source IS NULL THEN p_version ELSE tombstone_version END
        WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle RETURNING * INTO b;
    RETURN NEXT b;
END;
$body$;
CREATE FUNCTION weft.spec_retirements(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_store uuid) RETURNS SETOF weft.spec_objects
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    IF session_user='weft_spec_maintenance' THEN
        IF p_actor IS NOT NULL THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Invalid maintenance identity.'; END IF;
    ELSE PERFORM weft.spec_authorize(p_actor,p_org,p_project,true); END IF;
    RETURN QUERY SELECT o.* FROM ONLY weft.spec_objects o WHERE o.organization_id=p_org AND o.project_id=p_project AND o.bundle_id=p_bundle AND o.store_id=p_store
        AND (o.state='retiring' OR (o.state='retired' AND EXISTS(SELECT 1 FROM ONLY weft.spec_temporaries t WHERE t.organization_id=o.organization_id AND t.project_id=o.project_id
            AND t.bundle_id=o.bundle_id AND t.object_id=o.id AND t.retirement_id IS NOT NULL AND NOT t.retired))) ORDER BY o.id LIMIT 100;
END;
$body$;

CREATE FUNCTION weft.spec_temp_advance(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_object uuid,p_attempt uuid,
    p_temp uuid,p_phase integer,p_previous text,p_evidence bytea) RETURNS SETOF weft.spec_temporaries
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
DECLARE o weft.spec_objects; t weft.spec_temporaries; seal text;
BEGIN
    SELECT * INTO o FROM weft.spec_object_gate(p_actor,p_org,p_project,p_bundle,p_object,p_attempt,'publish');
    IF NOT EXISTS(SELECT 1 FROM ONLY weft.spec_stores WHERE id=o.store_id AND kind='filesystem') THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Native temporary requires filesystem store.'; END IF;
    IF o.state<>'pending' OR p_phase NOT BETWEEN 1 AND 3 OR p_phase IS NULL OR octet_length(p_evidence)<>196 OR p_evidence IS NULL
        OR substring(p_evidence from 1 for 4)<>convert_to('WST1','UTF8') THEN RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid temporary evidence.'; END IF;
    IF (p_phase=1)<>(substring(p_evidence from 169 for 28)=decode(repeat('00',28),'hex'))
        OR (p_phase>1 AND (substring(p_evidence from 177 for 8)=decode(repeat('00',8),'hex') OR substring(p_evidence from 185 for 8)=decode(repeat('00',8),'hex'))) THEN
        RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Temporary file evidence does not match phase.';
    END IF;
    seal=encode(sha256(convert_to(concat_ws('|','weft.spec-seal.v1',o.store_id,p_org,p_project,p_bundle,o.source_id,o.source_version,p_object,o.kind,o.digest,o.byte_count,p_attempt,p_temp,p_phase,coalesce(p_previous,'')),'UTF8')||p_evidence),'hex');
    SELECT * INTO t FROM ONLY weft.spec_temporaries WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object AND id=p_temp;
    IF FOUND THEN
        IF t.phase=p_phase AND t.attempt_id=p_attempt AND t.seal_digest=seal AND t.retirement_id IS NULL THEN RETURN NEXT t; RETURN; END IF;
        IF t.phase+1<>p_phase OR t.attempt_id<>p_attempt OR t.seal_digest IS DISTINCT FROM p_previous OR t.retirement_id IS NOT NULL
            OR substring(t.evidence from 1 for 168)<>substring(p_evidence from 1 for 168)
            OR (p_phase=3 AND t.evidence<>p_evidence) THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Temporary transition conflicts.'; END IF;
        UPDATE weft.spec_temporaries SET phase=p_phase,evidence=p_evidence,previous_digest=p_previous,seal_digest=seal
            WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object AND id=p_temp RETURNING * INTO t;
    ELSE
        IF p_phase<>1 OR p_previous IS NOT NULL OR (SELECT count(*) FROM ONLY weft.spec_temporaries WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object)>=32 THEN
            RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Temporary reservation unavailable.';
        END IF;
        INSERT INTO weft.spec_temporaries VALUES(p_org,p_project,p_bundle,p_object,p_temp,p_attempt,p_phase,p_evidence,seal,p_previous,NULL,false) RETURNING * INTO t;
    END IF;
    INSERT INTO weft.spec_temporary_seals VALUES(p_org,p_project,p_bundle,p_object,p_temp,p_phase,p_evidence,seal,p_previous);
    RETURN NEXT t;
END;
$body$;
CREATE FUNCTION weft.spec_temp_read(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_object uuid,p_claim uuid,p_retiring boolean) RETURNS SETOF weft.spec_temporaries
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    PERFORM weft.spec_object_gate(p_actor,p_org,p_project,p_bundle,p_object,p_claim,CASE WHEN p_retiring THEN 'retire' ELSE 'publish' END);
    RETURN QUERY SELECT * FROM ONLY weft.spec_temporaries WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object ORDER BY id LIMIT 32;
END;
$body$;
CREATE FUNCTION weft.spec_temp_complete(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_object uuid,p_retirement uuid,p_temp uuid,p_temp_retirement uuid,p_seal text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
BEGIN
    PERFORM weft.spec_object_gate(p_actor,p_org,p_project,p_bundle,p_object,p_retirement,'retire');
    UPDATE weft.spec_temporaries SET retired=true WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND object_id=p_object
        AND id=p_temp AND retirement_id=p_temp_retirement AND seal_digest=p_seal;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Temporary retirement unavailable.'; END IF;
END;
$body$;

CREATE FUNCTION weft.spec_schema_version() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp AS $body$
SELECT checksum FROM ONLY weft.schema_migrations WHERE version=44
$body$;

DO $grants$
DECLARE f record;
BEGIN
    FOR f IN SELECT oid,proname FROM pg_proc WHERE pronamespace='weft'::regnamespace AND proname LIKE 'spec\_%' ESCAPE '\' LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.oid::regprocedure);
        IF f.proname<>'spec_authorize' THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO weft_spec_writer',f.oid::regprocedure);
        END IF;
        IF f.proname IN ('spec_bundle_authorize','spec_object_gate','spec_schema_version','spec_store_admit') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO weft_spec_reader',f.oid::regprocedure);
        END IF;
        IF f.proname IN ('spec_object_gate','spec_object_complete','spec_retirements','spec_temp_read','spec_temp_complete','spec_schema_version','spec_store_admit') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO weft_spec_maintenance',f.oid::regprocedure);
        END IF;
    END LOOP;
END;
$grants$;
GRANT USAGE ON SCHEMA weft TO weft_spec_reader,weft_spec_writer,weft_spec_maintenance;
