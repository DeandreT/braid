// Keep the fixture's complete schema admission explicit: an unreviewed migration
// must fail before it creates a database, rather than silently changing a gate.
export const browserFixtureMigrations = Object.freeze([
  "0001_project_ownership.sql",
  "0002_catalog_ingestion.sql",
  "0003_catalog_reads.sql",
  "0004_mapping_metadata.sql",
  "0005_project_creation.sql",
  "0006_revision_derivations.sql",
  "0007_private_object_maintenance.sql",
  "0008_external_identity_bindings.sql",
  "0009_project_payload_read_grants.sql",
  "0010_server_sessions.sql",
  "0011_private_chat.sql",
  "0012_preview_runs.sql",
  "0013_preview_messages.sql",
  "0014_regression_cases.sql",
  "0015_regression_runs.sql",
  "0016_regression_dispatch.sql",
  "0017_regression_run_history.sql",
  "0018_build_publication.sql",
  "0019_build_dispatch.sql",
  "0020_build_metadata.sql",
  "0021_build_qualification.sql",
  "0022_local_releases.sql",
  "0023_private_publication_storage_kind.sql",
  "0024_machine_invocation.sql",
  "0025_api_request_audit.sql",
  "0026_account_display_metadata.sql",
  "0027_administration.sql",
  "0028_interchange_provenance.sql",
  "0029_catalog_search.sql",
  "0030_regression_automation.sql",
  "0031_release_operations.sql",
  "0032_workspace_chat.sql",
  "0033_preview_reads.sql",
]);

export const browserFixtureProtectedTables = Object.freeze([
  "automation_projects",
  "automation_mappings",
  "automation_suites",
  "automation_evaluations",
  "notification_subscriptions",
  "notification_events",
  "notifications",
  "notification_destinations",
  "notification_deliveries",
  "notification_delivery_attempts",
  "notification_endpoint_windows",
  "organization_notifications",
  "build_qualifications",
  "local_release_targets",
  "local_releases",
  "local_release_audit",
  "invocation_grants",
  "invocation_grant_audit",
  "machine_invocations",
  "machine_invocation_audit",
  "machine_invocation_results",
  "api_request_audit",
  "administration_state",
  "organization_administration_roles",
  "organization_invitations",
  "administration_audit",
  "account_display_metadata",
  "release_target_operations",
  "release_target_events",
  "invocation_operations_metadata",
  "import_reviews",
  "export_reviews",
  "revision_import_provenance",
]);

/** Read-side regression hosts may derive the same saved reports that the UI reads. */
export function regressionReadProfile(options) {
  return (
    options.regressionScenario === true &&
    options.kind === "read" &&
    options.writer === false &&
    options.uploadOnly === false &&
    options.metadataEdits === false &&
    options.creationOnly === false &&
    (options.reportMode === null || options.reportMode === "full")
  );
}
