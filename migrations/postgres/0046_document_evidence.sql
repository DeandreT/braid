-- Structural evidence admission and durable actor-scoped creation discovery.
-- Historical scripts 0001-0045 remain immutable.
DO $history$
BEGIN
    IF current_user<>'braid_migrator' OR session_user<>current_user
        OR NOT EXISTS(SELECT 1 FROM braid.schema_migrations WHERE version=44 AND checksum='78c86ad3b972cab04e972004a5e2ba5a9ad983ae3037867f70011aa91e7a5cd8')
        OR NOT EXISTS(SELECT 1 FROM braid.schema_migrations WHERE version=45 AND checksum='7756574c6fd95a5799fe1fea20c8055f627d55ba36dd41a4071180c9dbb36906') THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Document evidence requires its exact migration prefix.';
    END IF;
END;
$history$;

ALTER TABLE braid.spec_sources DROP CONSTRAINT spec_sources_format_check;
ALTER TABLE braid.spec_sources ADD CONSTRAINT spec_sources_format_check CHECK(format BETWEEN 0 AND 6);
ALTER TABLE braid.spec_sources ADD CONSTRAINT spec_sources_format_identity UNIQUE(organization_id,project_id,bundle_id,id,version,format);

-- Pin format with a real FK, including direct privileged writes and later drift.
ALTER TABLE braid.spec_objects ADD COLUMN source_format integer;
UPDATE braid.spec_objects o SET source_format=s.format FROM braid.spec_sources s
    WHERE s.organization_id=o.organization_id AND s.project_id=o.project_id AND s.bundle_id=o.bundle_id
        AND s.id=o.source_id AND s.version=o.source_version;
ALTER TABLE braid.spec_objects ALTER COLUMN source_format SET NOT NULL;
ALTER TABLE braid.spec_objects ADD CONSTRAINT spec_objects_source_format_fkey
    FOREIGN KEY(organization_id,project_id,bundle_id,source_id,source_version,source_format)
    REFERENCES braid.spec_sources(organization_id,project_id,bundle_id,id,version,format);
ALTER TABLE braid.spec_objects DROP CONSTRAINT spec_objects_check1;
ALTER TABLE braid.spec_objects ADD CONSTRAINT spec_objects_check1 CHECK(
    (kind=1 AND extractor_version IS NULL AND profile_digest IS NULL AND extraction_outcome IS NULL AND source_digest IS NULL)
    OR (kind=2 AND extractor_version>0 AND profile_digest ~ '^[0-9a-f]{64}$' AND extraction_outcome BETWEEN 1 AND 5
        AND source_digest ~ '^[0-9a-f]{64}$' AND extractor_version IS NOT NULL AND profile_digest IS NOT NULL
        AND extraction_outcome IS NOT NULL AND source_digest IS NOT NULL
        AND (extraction_outcome<>5 OR (source_format IN (4,5) AND extractor_version=3) OR (source_format=6 AND extractor_version=4))));

ALTER TABLE braid.creation_sources ADD COLUMN extraction_outcome integer NOT NULL DEFAULT 1;
ALTER TABLE braid.creation_sources DROP CONSTRAINT creation_sources_check;
ALTER TABLE braid.creation_sources ADD CONSTRAINT creation_sources_check CHECK(
    source_version>0 AND source_count BETWEEN 0 AND 10485760 AND evidence_count BETWEEN 1 AND 10485760
    AND format BETWEEN 0 AND 6 AND extractor_version>0
    AND (extraction_outcome=1 OR (extraction_outcome=5 AND
        ((format IN (4,5) AND extractor_version=3) OR (format=6 AND extractor_version=4)))));

ALTER TABLE braid.chat_payload_audit DROP CONSTRAINT chat_payload_audit_source_kind_check;
ALTER TABLE braid.chat_payload_audit ADD CONSTRAINT chat_payload_audit_source_kind_check CHECK(source_kind BETWEEN 1 AND 5);

ALTER TABLE braid.chat_turns DROP CONSTRAINT chat_turns_max_rounds_check;
ALTER TABLE braid.chat_turns ADD CONSTRAINT chat_turns_max_rounds_check CHECK(max_rounds BETWEEN 1 AND 5);
ALTER TABLE braid.chat_rounds DROP CONSTRAINT chat_rounds_ordinal_check;
ALTER TABLE braid.chat_rounds ADD CONSTRAINT chat_rounds_ordinal_check CHECK(ordinal BETWEEN 1 AND 5);

-- Extend the pinned validator with a closed creation-proposal citation shape.
DO $disclosure$
DECLARE body text;
BEGIN
    SELECT prosrc INTO STRICT body FROM pg_proc WHERE oid='braid.chat_metadata_valid(integer,text,uuid)'::regprocedure;
    IF encode(sha256(convert_to(body,'UTF8')),'hex')<>'1c6102c35aad5735abac4e0d18f5cdc3d54d82766e347d84053ca54eb0a37af8' THEN
        RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Document disclosure requires the exact prior validator.';
    END IF;
    body=replace(body,$old$j->>'source' !~ '^[1-4]$'$old$,$new$j->>'source' !~ '^[1-5]$'$new$);
    body=replace(body,'ELSIF kind=13 THEN',$creation_citation$ELSIF kind=13 THEN
        IF j->>'kind'='12' THEN
            IF keys IS DISTINCT FROM ARRAY['attempt','digest','entry','key','kind','mapping','ordinal','organization','project','proposalFingerprint','resource','revision','section','subject','targetKind','v','version']::text[]
                OR jsonb_typeof(j->'kind') IS DISTINCT FROM 'number'
                OR jsonb_typeof(j->'proposalFingerprint') IS DISTINCT FROM 'string'
                OR j->>'proposalFingerprint' !~ '^[0-9a-f]{64}$' THEN RETURN false; END IF;
            FOREACH k IN ARRAY ARRAY['organization','project','subject']::text[] LOOP
                IF jsonb_typeof(j->k) IS DISTINCT FROM 'string'
                    OR j->>k !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    OR (j->>k)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN false; END IF;
            END LOOP;
            FOREACH k IN ARRAY ARRAY['attempt','digest','entry','key','mapping','ordinal','resource','revision','section','targetKind','version']::text[] LOOP
                IF j->k IS DISTINCT FROM 'null'::jsonb THEN RETURN false; END IF;
            END LOOP;
            RETURN true;
        END IF;$creation_citation$);
    EXECUTE format('CREATE OR REPLACE FUNCTION braid.chat_metadata_valid(kind integer,value text,expected_round uuid) RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS %L',body);
END;
$disclosure$;

CREATE OR REPLACE FUNCTION braid.spec_reserve(p_actor uuid,p_org uuid,p_project uuid,p_bundle uuid,p_source uuid,p_source_version bigint,
    p_format integer,p_object uuid,p_kind integer,p_digest text,p_count integer,p_store uuid,p_attempt uuid,p_version bigint,
    p_extractor integer,p_profile text,p_outcome integer,p_source_digest text) RETURNS SETOF braid.spec_objects
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE b braid.spec_bundles; s braid.spec_sources; o braid.spec_objects;
BEGIN
    PERFORM braid.spec_authorize(p_actor,p_org,p_project,true);
    PERFORM 1 FROM ONLY braid.spec_stores WHERE id=p_store AND active FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification store unavailable.'; END IF;
    SELECT * INTO b FROM ONLY braid.spec_bundles WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle FOR UPDATE;
    IF NOT FOUND OR b.tombstoned THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification bundle unavailable.'; END IF;
    SELECT * INTO o FROM ONLY braid.spec_objects WHERE store_id=p_store AND attempt_id=p_attempt;
    IF FOUND THEN
        IF ROW(o.organization_id,o.project_id,o.bundle_id,o.source_id,o.source_version,o.id,o.kind,o.digest,o.byte_count,o.owner_id,o.reservation_version,o.extractor_version,o.profile_digest,o.extraction_outcome,o.source_digest,o.source_format)
            IS DISTINCT FROM ROW(p_org,p_project,p_bundle,p_source,p_source_version,p_object,p_kind,p_digest,p_count,p_actor,p_version,p_extractor,p_profile,p_outcome,p_source_digest,p_format)
            OR o.state IN ('retiring','retired') OR NOT EXISTS(SELECT 1 FROM ONLY braid.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_source AND version=p_source_version AND format=p_format AND NOT tombstoned) THEN
            RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Specification reservation conflicts.';
        END IF;
        RETURN NEXT o; RETURN;
    END IF;
    IF b.version<>p_version THEN RAISE EXCEPTION USING ERRCODE='40001',MESSAGE='Specification version changed.'; END IF;
    IF (SELECT count(*) FROM ONLY braid.spec_objects WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)>=128 THEN
        RAISE EXCEPTION USING ERRCODE='54000',MESSAGE='Specification object limit exceeded.';
    END IF;
    SELECT * INTO s FROM ONLY braid.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND id=p_source AND version=p_source_version;
    IF p_kind=1 THEN
        IF FOUND THEN RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Specification source already reserved.'; END IF;
        IF (SELECT count(*) FROM ONLY braid.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)>=20
            OR (SELECT coalesce(sum(byte_count),0) FROM ONLY braid.spec_sources WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle)+p_count>52428800 THEN
            RAISE EXCEPTION USING ERRCODE='54000',MESSAGE='Specification source limit exceeded.';
        END IF;
        INSERT INTO braid.spec_sources(organization_id,project_id,bundle_id,id,version,format,digest,byte_count,tombstoned,tombstone_key,tombstone_version)
            VALUES(p_org,p_project,p_bundle,p_source,p_source_version,p_format,p_digest,p_count,false,NULL,NULL);
    ELSE
        IF NOT FOUND OR s.tombstoned OR s.digest IS DISTINCT FROM p_source_digest OR s.format<>p_format OR NOT EXISTS(
            SELECT 1 FROM ONLY braid.spec_objects WHERE organization_id=p_org AND project_id=p_project AND bundle_id=p_bundle AND source_id=p_source AND source_version=p_source_version AND kind=1 AND state='published') THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification extraction source unavailable.';
        END IF;
    END IF;
    INSERT INTO braid.spec_objects(organization_id,project_id,bundle_id,id,source_id,source_version,kind,digest,byte_count,store_id,owner_id,attempt_id,
        reservation_version,extractor_version,profile_digest,extraction_outcome,source_digest,state,retirement_id,source_format)
        VALUES(p_org,p_project,p_bundle,p_object,p_source,p_source_version,p_kind,p_digest,p_count,p_store,p_actor,p_attempt,p_version,p_extractor,p_profile,p_outcome,p_source_digest,'pending',NULL,p_format) RETURNING * INTO o;
    UPDATE braid.spec_bundles SET version=version+1 WHERE organization_id=p_org AND project_id=p_project AND id=p_bundle;
    RETURN NEXT o;
END;
$body$;

CREATE OR REPLACE FUNCTION braid.creation_snapshot_check(a uuid,o uuid,j uuid,b uuid,selection jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE x jsonb; original braid.spec_objects; extracted braid.spec_objects; store uuid;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
    IF jsonb_typeof(selection) IS DISTINCT FROM 'array' OR jsonb_array_length(selection) NOT BETWEEN 1 AND 20 THEN
        RAISE EXCEPTION USING ERRCODE='23514',MESSAGE='Invalid creation selection.';
    END IF;
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
            OR original.source_format IS DISTINCT FROM (x->>'format')::integer OR original.kind<>1 OR original.state<>'published'
            OR original.digest IS DISTINCT FROM x->>'sourceDigest' OR original.byte_count IS DISTINCT FROM (x->>'sourceCount')::integer THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation original unavailable.';
        END IF;
        SELECT * INTO extracted FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=(x->>'extraction')::uuid;
        IF NOT FOUND OR extracted.source_id<>original.source_id OR extracted.source_version<>original.source_version
            OR extracted.source_format<>original.source_format OR extracted.kind<>2 OR extracted.state<>'published'
            OR extracted.extraction_outcome IS DISTINCT FROM (x->>'outcome')::integer
            OR NOT (extracted.extraction_outcome=1 OR (extracted.extraction_outcome=5 AND
                ((extracted.source_format IN (4,5) AND extracted.extractor_version=3) OR (extracted.source_format=6 AND extracted.extractor_version=4))))
            OR extracted.source_digest<>original.digest OR extracted.extractor_version IS DISTINCT FROM (x->>'extractor')::integer
            OR extracted.profile_digest IS DISTINCT FROM x->>'profile' OR extracted.digest IS DISTINCT FROM x->>'evidence' OR extracted.byte_count IS DISTINCT FROM (x->>'evidenceCount')::integer THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Creation extraction unavailable.';
        END IF;
    END LOOP;
END;
$body$;

CREATE OR REPLACE FUNCTION braid.creation_select(a uuid,o uuid,j uuid,b uuid,selection jsonb) RETURNS jsonb
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
            AND extractor_version=(x->>'extractor')::integer AND profile_digest=x->>'profile' AND digest=x->>'evidence'
            AND extraction_outcome=coalesce((x->>'outcome')::integer,1) ORDER BY id LIMIT 1;
        result=result||jsonb_build_array(x||jsonb_build_object('original',original,'extraction',extracted,'outcome',coalesce((x->>'outcome')::integer,1),
            'sourceCount',coalesce((x->>'sourceCount')::integer,(SELECT byte_count FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=original)),
            'evidenceCount',coalesce((x->>'evidenceCount')::integer,(SELECT byte_count FROM ONLY braid.spec_objects WHERE organization_id=o AND project_id=j AND bundle_id=b AND id=extracted))));
    END LOOP;
    PERFORM braid.creation_snapshot_check(a,o,j,b,result);
    RETURN result;
END;
$body$;

CREATE OR REPLACE FUNCTION braid.creation_selection(o uuid,j uuid,id uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
SELECT jsonb_agg(jsonb_build_object('source',source_id,'version',source_version,'original',original_id,'extraction',extraction_id,
    'sourceDigest',source_digest,'sourceCount',source_count,'format',format,'extractor',extractor_version,'profile',profile_digest,'evidence',evidence_digest,'evidenceCount',evidence_count,'outcome',extraction_outcome) ORDER BY source_id)
FROM ONLY braid.creation_sources WHERE organization_id=o AND project_id=j AND proposal_id=id
$body$;

CREATE OR REPLACE FUNCTION braid.creation_retain(a uuid,o uuid,j uuid,id uuid,k uuid,b uuid,f text,s text,content bytea,n text,nc integer,backend text,filename text,name text,description text,assumptions text[],questions boolean,selection jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
DECLARE p braid.creation_proposals; x jsonb; chosen jsonb;
BEGIN
    PERFORM braid.creation_authorize(a,o,j,true);
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
            x->>'sourceDigest',(x->>'sourceCount')::integer,(x->>'format')::integer,(x->>'extractor')::integer,x->>'profile',x->>'evidence',(x->>'evidenceCount')::integer,(x->>'outcome')::integer);
    END LOOP;
END;
$body$;

CREATE INDEX creation_proposals_discovery ON braid.creation_proposals(organization_id,project_id,author_id,id);

CREATE FUNCTION braid.creation_proposals_discover_v1(actor uuid,org uuid,project uuid,after_proposal uuid,"limit" integer)
RETURNS TABLE(proposal_id uuid,fingerprint text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    IF actor IS NULL OR org IS NULL OR project IS NULL OR '00000000-0000-0000-0000-000000000000'::uuid IN (actor,org,project,after_proposal)
        OR "limit" IS NULL OR "limit" NOT BETWEEN 1 AND 20 THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid creation discovery limit.';
    END IF;
    PERFORM braid.creation_authorize(actor,org,project,false);
    RETURN QUERY SELECT p.id,p.fingerprint FROM ONLY braid.creation_proposals p
        WHERE p.organization_id=org AND p.project_id=project AND p.author_id=actor
            AND (after_proposal IS NULL OR p.id>after_proposal) ORDER BY p.id LIMIT "limit";
END;
$body$;

CREATE FUNCTION braid.spec_selection_discover_v1(actor uuid,org uuid,project uuid,store uuid,after_bundle uuid,after_object uuid,"limit" integer)
RETURNS TABLE(bundle_id uuid,extraction_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
BEGIN
    IF actor IS NULL OR org IS NULL OR project IS NULL OR store IS NULL
        OR '00000000-0000-0000-0000-000000000000'::uuid IN (actor,org,project,store,after_bundle,after_object)
        OR (after_bundle IS NULL)<>(after_object IS NULL) OR "limit" IS NULL OR "limit" NOT BETWEEN 1 AND 20 THEN
        RAISE EXCEPTION USING ERRCODE='22023',MESSAGE='Invalid specification discovery cursor or limit.';
    END IF;
    PERFORM braid.creation_authorize(actor,org,project,false);
    PERFORM 1 FROM ONLY braid.spec_stores st WHERE st.id=store AND st.active FOR SHARE NOWAIT;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Specification store unavailable.'; END IF;
    RETURN QUERY SELECT b.id,e.id FROM ONLY braid.spec_bundles b JOIN ONLY braid.spec_sources s
        ON s.organization_id=b.organization_id AND s.project_id=b.project_id AND s.bundle_id=b.id AND NOT s.tombstoned
        JOIN ONLY braid.spec_objects e ON e.organization_id=s.organization_id AND e.project_id=s.project_id AND e.bundle_id=s.bundle_id
            AND e.source_id=s.id AND e.source_version=s.version AND e.source_format=s.format AND e.source_digest=s.digest
            AND e.kind=2 AND e.state='published' AND e.store_id=store
        WHERE b.organization_id=org AND b.project_id=project AND NOT b.tombstoned
            AND (e.extraction_outcome=1 OR (e.extraction_outcome=5 AND
                ((s.format IN (4,5) AND e.extractor_version=3) OR (s.format=6 AND e.extractor_version=4))))
            AND EXISTS(SELECT 1 FROM ONLY braid.spec_objects original WHERE original.organization_id=s.organization_id
                AND original.project_id=s.project_id AND original.bundle_id=s.bundle_id AND original.source_id=s.id
                AND original.source_version=s.version AND original.source_format=s.format AND original.kind=1
                AND original.state='published' AND original.store_id=store AND original.digest=s.digest AND original.byte_count=s.byte_count)
            AND (after_bundle IS NULL OR ROW(b.id,e.id)>ROW(after_bundle,after_object))
        ORDER BY b.id,e.id LIMIT "limit" FOR SHARE OF b NOWAIT;
END;
$body$;

REVOKE ALL ON FUNCTION braid.creation_proposals_discover_v1(uuid,uuid,uuid,uuid,integer),
    braid.spec_selection_discover_v1(uuid,uuid,uuid,uuid,uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.creation_proposals_discover_v1(uuid,uuid,uuid,uuid,integer),
    braid.spec_selection_discover_v1(uuid,uuid,uuid,uuid,uuid,uuid,integer) TO braid_spec_reader;

CREATE FUNCTION braid.document_evidence_schema_version() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp AS $body$
SELECT checksum FROM ONLY braid.schema_migrations WHERE version=46
    AND EXISTS(SELECT 1 FROM ONLY braid.schema_migrations WHERE version=44 AND checksum='78c86ad3b972cab04e972004a5e2ba5a9ad983ae3037867f70011aa91e7a5cd8')
    AND EXISTS(SELECT 1 FROM ONLY braid.schema_migrations WHERE version=45 AND checksum='7756574c6fd95a5799fe1fea20c8055f627d55ba36dd41a4071180c9dbb36906')
$body$;
REVOKE ALL ON FUNCTION braid.document_evidence_schema_version() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION braid.document_evidence_schema_version() TO braid_creation_reader,braid_creation_writer,braid_creation_approver,
    braid_spec_reader,braid_spec_writer,braid_spec_maintenance;
