-- Catalog reads use a separate profile; ingestion and ownership runtime grants
-- remain unchanged. The administrator provisions this login before migration.
GRANT USAGE ON SCHEMA braid TO braid_catalog_reader;
GRANT SELECT ON braid.organizations, braid.principals,
    braid.organization_memberships, braid.projects, braid.project_memberships,
    braid.mappings, braid.mapping_revisions TO braid_catalog_reader;
GRANT SELECT (organization_id, project_id, operation_id, display_filename)
    ON braid.revision_ingestions TO braid_catalog_reader;
