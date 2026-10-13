-- Metadata edits use a separate column-limited profile. Existing ownership,
-- ingestion and catalog reader grants remain unchanged. Provision the editor
-- login explicitly before applying this migration.
CREATE OR REPLACE FUNCTION weft.guard_mappings_transition() RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Mapping deletion is unavailable.' USING ERRCODE = '23514';
    ELSIF TG_OP = 'INSERT' THEN
        IF NEW.head_sequence <> 1 OR NEW.version <> 1 THEN
            RAISE EXCEPTION 'A mapping requires its initial committed head.' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF (to_jsonb(NEW) - ARRAY['name','description','version','head_revision_id','head_sequence','updated_at']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['name','description','version','head_revision_id','head_sequence','updated_at'])
            OR NEW.updated_at < OLD.updated_at THEN
            RAISE EXCEPTION 'Mapping identity and monotonic time facts are immutable.' USING ERRCODE = '23514';
        END IF;
        IF ROW(NEW.head_revision_id,NEW.head_sequence) IS DISTINCT FROM ROW(OLD.head_revision_id,OLD.head_sequence) THEN
            IF ROW(NEW.name,NEW.description,NEW.version) IS DISTINCT FROM ROW(OLD.name,OLD.description,OLD.version)
                OR NEW.head_revision_id IS NOT DISTINCT FROM OLD.head_revision_id THEN
                RAISE EXCEPTION 'Head advancement must preserve mapping metadata.' USING ERRCODE = '23514';
            END IF;
            IF OLD.head_sequence = 9223372036854775807 THEN
                RAISE EXCEPTION 'Mapping head sequence is exhausted.' USING ERRCODE = '23514';
            END IF;
            IF NEW.head_sequence <> OLD.head_sequence + 1 THEN
                RAISE EXCEPTION 'Mapping head advancement must be consecutive.' USING ERRCODE = '23514';
            END IF;
        ELSIF ROW(NEW.name,NEW.description) IS DISTINCT FROM ROW(OLD.name,OLD.description) THEN
            IF OLD.version = 9223372036854775807 THEN
                RAISE EXCEPTION 'Mapping metadata version is exhausted.' USING ERRCODE = '23514';
            END IF;
            IF NEW.version <> OLD.version + 1 THEN
                RAISE EXCEPTION 'Mapping metadata advancement must be consecutive.' USING ERRCODE = '23514';
            END IF;
        ELSE
            RAISE EXCEPTION 'A mapping update requires a meaningful head or metadata change.' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$guard$;

GRANT USAGE ON SCHEMA weft TO weft_catalog_editor;
GRANT SELECT ON weft.organizations, weft.principals,
    weft.organization_memberships, weft.projects, weft.project_memberships,
    weft.mappings TO weft_catalog_editor;
GRANT UPDATE (name, description, version, updated_at) ON weft.mappings TO weft_catalog_editor;
