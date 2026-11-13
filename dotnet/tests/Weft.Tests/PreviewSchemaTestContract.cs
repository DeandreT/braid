namespace Weft.Tests;

/// <summary>
/// The exact additive delta of migration 0012, projected out of older conservation snapshots. Migration 0013
/// only adds the separate message schema, which no older snapshot observes; the read supplement adds reader functions only.
/// </summary>
internal static class PreviewSchemaTestContract
{
    internal const string MessagesMigrationChecksum = "0a258f8b711a42fed1f80366979cb1b5ea182e3545ac94289634184e5e21ad47";
    internal const string MigrationChecksum = "f11b094fbd09a42baa20473fca3d4ba6aa374da27550eee9da21a83827f588a9";
    internal const string ReadsMigrationChecksum = "40479634ca37c26c483e9a3d8864214291c63e92cb3502bb17f985a0e8ece46a";
    internal static readonly string[] Tables = ["preview_documents", "preview_runs", Weft.Persistence.Postgres.XmlPreviewSchemaAdmission.Table];
    internal static readonly string[] FunctionNames =
    [
        "preview_authorized", "preview_cancel", "preview_claim", "preview_complete", "preview_history", "preview_input",
        "preview_input_documents", "preview_inputs", "preview_observe", "preview_output", "preview_outputs", "preview_read",
        "preview_release", "preview_submit", "preview_complete_csv", "preview_csv_outputs", "preview_csv_input", "guard_csv_preview_document",
        "csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical", Weft.Persistence.Postgres.DocumentJsonSchemaAdmission.FunctionName,
        .. Weft.Persistence.Postgres.XmlPreviewSchemaAdmission.Functions
    ];
    internal static readonly string[] Roles = ["weft_preview_reader", "weft_preview_writer"];
    // Child FKs whose internal RI triggers land on older parent relations.
    internal static readonly string[] ParentForeignKeys = ["preview_runs_revision_fkey", "preview_runs_submitter_fkey"];
}
