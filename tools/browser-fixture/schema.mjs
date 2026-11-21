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
  "0034_document_previews.sql",
  "0035_x12_interchange_builds.sql",
  "0036_automation_comparison_settlement.sql",
  "0037_mapping_drafts.sql",
  "0038_mapping_draft_previews.sql",
  "0039_chat_mapping_writes.sql",
  "0040_chat_mapping_write_journal.sql",
  "0041_csv_documents.sql",
  "0042_xml_documents.sql",
  "0043_chat_edit_proposals.sql",
  "0044_spec_bundles.sql",
  "0045_mapping_creation.sql",
  "0046_document_evidence.sql",
  "0047_document_json_contract.sql",
  "0048_host_orders.sql",
  "0049_replica_readiness.sql",
  "0050_trigger_work.sql",
  "0051_trigger_schedules.sql",
  "0052_trigger_operations.sql",
  "0053_as2_send_fence.sql",
  "0054_pilot_quarantine.sql",
  "0055_audit_log.sql",
  "0056_ai_usage_quotas.sql",
  "0057_organization_switching.sql",
  "0058_resource_lifecycle.sql",
  "0059_resource_deletion.sql",
  "0060_retention_policies.sql",
  "0061_resource_purge.sql",
  "0062_retention_enforcement.sql",
  "0063_retention_completion.sql",
  "0064_order_retention_constraints.sql",
  "0065_native_document_profiles.sql",
  "0066_project_creation_retention_hooks.sql",
  "0067_compiled_xml_builds.sql",
  "0068_compiled_native_documents.sql",
  "0069_portable_workbook.sql",
]);

export function matchesBrowserFixtureMigrationJournal(rows, sourceHashes) {
  return (
    rows.length === browserFixtureMigrations.length &&
    rows.every((row, index) => {
      const expected =
        sourceHashes["migrations/postgres/" + browserFixtureMigrations[index]];
      return (
        String(row.version) === String(index + 1) &&
        typeof expected === "string" &&
        expected.length === 64 &&
        /^[a-f0-9]{64}$/.test(expected) &&
        row.checksum === expected
      );
    })
  );
}

export function browserFixtureRowsQuery(tables) {
  if (
    tables.length === 0 ||
    new Set(tables).size !== tables.length ||
    tables.some((name) => !/^[a-z][a-z0-9_]*$/.test(name))
  )
    throw new Error("Invalid fixture table inventory");

  // PostgreSQL caps function calls at 100 arguments. Aggregate one entry per
  // table without dropping any of the protected tables or changing row order.
  const entries = tables.map((table, index) => {
    const rows =
      table === "build_publications"
        ? "(SELECT coalesce(jsonb_agg((to_jsonb(r)-'artifact_body')||jsonb_build_object('artifact_body_sha256',encode(sha256(r.artifact_body),'hex'),'artifact_body_bytes',octet_length(r.artifact_body)) ORDER BY r.organization_id,r.build_id),'[]'::jsonb) FROM braid.build_publications r)"
        : `(SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb) FROM braid.${table} r)`;
    return `(${index},'${table}',${rows})`;
  });
  return (
    "SELECT json_object_agg(entry.name, entry.contents ORDER BY entry.ordinal) FROM (VALUES " +
    entries.join(",") +
    ") AS entry(ordinal,name,contents)"
  );
}

export const browserFixtureProtectedTables = Object.freeze([
  "audit_log_events",
  "ai_usage_turns",
  "ai_usage_failures",
  "ai_usage_coverage",
  "ai_quota_state",
  "ai_quota_limits",
  "ai_quota_reservations",
  "creation_proposals",
  "creation_sources",
  "creation_approvals",
  "spec_stores",
  "spec_bundles",
  "spec_sources",
  "spec_objects",
  "spec_temporaries",
  "spec_temporary_seals",
  "chat_edit_proposals",
  "chat_edit_content",
  "chat_edit_approvals",
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
  "mapping_drafts",
  "mapping_draft_checkpoints",
  "revision_edit_provenance",
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
