export const browserFixtureRolePairs = Object.freeze([
  ["braid_migrator", "MIGRATOR"],
  ["braid_runtime", "RUNTIME"],
  ["braid_ingest", "INGEST"],
  ["braid_catalog_reader", "CATALOG"],
  ["braid_catalog_editor", "METADATA"],
  ["braid_project_creator", "CREATOR"],
  ["braid_report_reader", "REPORT"],
  ["braid_deriver", "DERIVATION"],
  ["braid_maintenance", "MAINTENANCE"],
  ["braid_identity_reader", "IDENTITY"],
  ["braid_payload_permission_reader", "PAYLOAD_PERMISSION"],
  ["braid_session_reader", "SESSION_READER"],
  ["braid_session_writer", "SESSION_WRITER"],
  ["braid_chat_reader", "CHAT_READER"],
  ["braid_chat_writer", "CHAT_WRITER"],
  ["braid_chat_approver", "CHAT_APPROVER"],
  ["braid_chat_lifecycle", "CHAT_LIFECYCLE"],
  ["braid_preview_reader", "PREVIEW_READER"],
  ["braid_preview_writer", "PREVIEW_WRITER"],
  ["braid_case_reader", "CASE_READER"],
  ["braid_case_writer", "CASE_WRITER"],
  ["braid_run_reader", "RUN_READER"],
  ["braid_run_writer", "RUN_WRITER"],
  ["braid_comparison_settler", "COMPARISON"],
  ["braid_build_reader", "BUILD_READER"],
  ["braid_build_writer", "BUILD_WRITER"],
  ["braid_build_run_reader", "BUILD_RUN_READER"],
  ["braid_build_run_writer", "BUILD_RUN_WRITER"],
  ["braid_build_dispatcher", "BUILD_DISPATCHER"],
  ["braid_build_metadata_reader", "BUILD_METADATA"],
  ["braid_qualification_reader", "QUALIFICATION_READER"],
  ["braid_qualification_writer", "QUALIFICATION_WRITER"],
  ["braid_release_reader", "RELEASE_READER"],
  ["braid_release_writer", "RELEASE_WRITER"],
  ["braid_invocation_grant_writer", "INVOCATION_GRANT_WRITER"],
  ["braid_invocation_writer", "INVOCATION_WRITER"],
  ["braid_api_audit_writer", "API_AUDIT_WRITER"],
  ["braid_trigger_worker", "TRIGGER_WORKER"],
  ["braid_administration", "ADMINISTRATION"],
  ["braid_draft_reader", "DRAFT_READER"],
  ["braid_draft_writer", "DRAFT_WRITER"],
]);

// The initializer provisions these roles for migrations but the fixture never
// supplies their opt-in credentials.
export const browserFixtureUncredentialedRoles = Object.freeze([
  "braid_creation_approver",
  "braid_creation_reader",
  "braid_creation_writer",
  "braid_lifecycle_worker",
  "braid_organization_discovery",
  "braid_spec_maintenance",
  "braid_spec_reader",
  "braid_spec_writer",
]);

export function matchesBrowserFixtureRoles(roles) {
  const expected = new Map([
    ...browserFixtureRolePairs.map(([name]) => [name, true]),
    ...browserFixtureUncredentialedRoles.map((name) => [name, false]),
  ]);
  return (
    Array.isArray(roles) &&
    roles.length === expected.size &&
    roles.every((role) => {
      if (
        !role ||
        !expected.has(role.name) ||
        role.hasPassword !== expected.get(role.name) ||
        role.login !== true ||
        [
          "super",
          "createDb",
          "createRole",
          "inherit",
          "replication",
          "bypass",
        ].some((key) => role[key] !== false)
      )
        return false;
      expected.delete(role.name);
      return true;
    })
  );
}
