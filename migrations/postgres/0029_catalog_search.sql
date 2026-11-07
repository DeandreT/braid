-- The DBA installs pg_trgm and removes PUBLIC execution first. PostgreSQL trusted
-- extension routines are bootstrap-owned, so the migrator cannot revoke their ACLs.
DO $weft_require_search_extension$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_extension e
        JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
        JOIN pg_catalog.pg_roles r ON r.oid = n.nspowner
        WHERE e.extname = 'pg_trgm' AND n.nspname = 'weft_search_extensions' AND r.rolname = 'weft_migrator')
        OR pg_catalog.has_schema_privilege('public','weft_search_extensions','USAGE,CREATE')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'weft_search_extensions' AND pg_catalog.has_function_privilege('public',p.oid,'EXECUTE')) THEN
        RAISE EXCEPTION 'Catalog search requires DBA-provisioned private pg_trgm without PUBLIC schema or function privileges.' USING ERRCODE='42501';
    END IF;
END;
$weft_require_search_extension$;

CREATE INDEX projects_search_trgm ON weft.projects USING gin
    ((lower((name || E'\n' || description) COLLATE "C")) weft_search_extensions.gin_trgm_ops);
CREATE INDEX mappings_search_trgm ON weft.mappings USING gin
    ((lower((name || E'\n' || description || E'\n' || backend_id) COLLATE "C")) weft_search_extensions.gin_trgm_ops);
CREATE INDEX mapping_revisions_search_trgm ON weft.mapping_revisions USING gin
    ((lower(native_digest COLLATE "C")) weft_search_extensions.gin_trgm_ops);
CREATE INDEX revision_ingestions_filename_search_trgm ON weft.revision_ingestions USING gin
    ((lower(display_filename COLLATE "C")) weft_search_extensions.gin_trgm_ops);

CREATE TABLE weft.catalog_search_report_index (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    derivation_id uuid NOT NULL,
    derived_digest text NOT NULL CHECK (derived_digest ~ '^[0-9a-f]{64}$'),
    terms jsonb NOT NULL CHECK (jsonb_typeof(terms) = 'array' AND jsonb_array_length(terms) <= 8192),
    search_text text NOT NULL,
    PRIMARY KEY (organization_id, project_id, mapping_id, revision_id, derivation_id),
    FOREIGN KEY (organization_id, project_id, mapping_id, revision_id, derivation_id)
        REFERENCES weft.revision_derivation_attempts(organization_id, project_id, mapping_id, revision_id, derivation_id)
);
ALTER TABLE weft.catalog_search_report_index ENABLE ROW LEVEL SECURITY;
CREATE POLICY catalog_search_report_index_organization_scope ON weft.catalog_search_report_index
    USING (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid);
CREATE INDEX catalog_search_report_index_trgm ON weft.catalog_search_report_index USING gin
    ((lower(search_text COLLATE "C")) weft_search_extensions.gin_trgm_ops);

-- A safe metadata-only view observes every retained usable projection, including reports
-- published before this migration. Publication and repair need no fallible secondary write.
CREATE VIEW weft.catalog_search_reports WITH (security_barrier = true) AS
SELECT a.organization_id, a.project_id, a.mapping_id, a.revision_id, a.derivation_id, a.derived_digest
FROM weft.revision_derivation_attempts a JOIN weft.revision_derivations d
    USING (organization_id, project_id, mapping_id, revision_id, cache_entry_id)
WHERE a.organization_id = NULLIF(current_setting('weft.organization_id',true),'')::uuid
    AND a.result_usable AND d.operation_kind = 'projection';
GRANT SELECT ON weft.catalog_search_reports, weft.catalog_search_report_index TO weft_catalog_reader;

CREATE FUNCTION weft.index_catalog_report(actor uuid, organization uuid, project uuid, mapping uuid,
    revision uuid, derivation uuid, digest text, entries jsonb) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, weft, pg_temp
AS $weft_index_catalog_report$
BEGIN
    IF organization IS DISTINCT FROM NULLIF(current_setting('weft.organization_id',true),'')::uuid
        OR NOT EXISTS (SELECT 1 FROM weft.principals p
            JOIN weft.organization_memberships o ON o.principal_id = p.id AND o.active
            JOIN weft.project_memberships m ON m.organization_id = o.organization_id AND m.principal_id = p.id AND m.active
            WHERE p.id = actor AND p.active AND o.organization_id = organization AND m.project_id = project
                AND m.role IN ('viewer','runner','publisher','administrator'))
        OR NOT EXISTS (SELECT 1 FROM weft.catalog_search_reports r
            WHERE r.organization_id = organization AND r.project_id = project AND r.mapping_id = mapping
                AND r.revision_id = revision AND r.derivation_id = derivation AND r.derived_digest = digest) THEN
        RETURN false;
    END IF;
    IF entries IS NULL OR jsonb_typeof(entries) <> 'array' OR jsonb_array_length(entries) > 8192
        OR octet_length(entries::text) > 16777216 THEN
        RAISE EXCEPTION 'Invalid bounded catalog index';
    END IF;
    IF EXISTS (SELECT 1 FROM jsonb_array_elements(entries) e WHERE jsonb_typeof(e) <> 'object'
        OR NOT (e ?& ARRAY['kind','id','title','path']) OR (SELECT count(*) FROM jsonb_object_keys(e)) <> 4
        OR e->>'kind' NOT IN ('4','5') OR jsonb_typeof(e->'kind') <> 'number'
        OR jsonb_typeof(e->'id') <> 'string' OR octet_length(e->>'id') NOT BETWEEN 1 AND 2048
        OR jsonb_typeof(e->'title') <> 'string' OR octet_length(e->>'title') NOT BETWEEN 1 AND 2048
        OR jsonb_typeof(e->'path') <> 'string' OR octet_length(e->>'path') > 16777216)
        OR EXISTS (SELECT 1 FROM jsonb_array_elements(entries) e GROUP BY e->>'kind',e->>'id' HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Invalid catalog index resources';
    END IF;
    INSERT INTO weft.catalog_search_report_index
        (organization_id, project_id, mapping_id, revision_id, derivation_id, derived_digest, terms, search_text)
    SELECT organization, project, mapping, revision, derivation, digest, entries,
        COALESCE(string_agg((e->>'title') || E'\n' || (e->>'path'), E'\n'), '')
    FROM jsonb_array_elements(entries) e
    ON CONFLICT DO NOTHING;
    RETURN true;
END;
$weft_index_catalog_report$;
REVOKE ALL ON FUNCTION weft.index_catalog_report(uuid,uuid,uuid,uuid,uuid,uuid,text,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION weft.index_catalog_report(uuid,uuid,uuid,uuid,uuid,uuid,text,jsonb) TO weft_deriver;
