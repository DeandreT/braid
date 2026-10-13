-- Catalog reads use a separate profile; ingestion and ownership runtime grants
-- remain unchanged. The administrator provisions this login before migration.
GRANT USAGE ON SCHEMA weft TO weft_catalog_reader;
GRANT SELECT ON weft.organizations, weft.principals,
    weft.organization_memberships, weft.projects, weft.project_memberships,
    weft.mappings, weft.mapping_revisions TO weft_catalog_reader;
GRANT SELECT (organization_id, project_id, operation_id, display_filename)
    ON weft.revision_ingestions TO weft_catalog_reader;
