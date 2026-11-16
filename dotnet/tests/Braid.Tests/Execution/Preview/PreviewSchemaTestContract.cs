namespace Braid.Tests;

/// <summary>
/// The exact additive delta of migration 0012, projected out of older conservation snapshots. Migration 0013
/// only adds the separate message schema, which no older snapshot observes; the read supplement adds reader functions only.
/// </summary>
internal static class PreviewSchemaTestContract
{
    internal const string MessagesMigrationChecksum = "69f14fdff6abd13341c8e6824ed5483a0a13aa123356668921f0c89f70986f4a";
    internal const string MigrationChecksum = "251e80e82248bea519be98508b3d1ae90a8a009253a8af85e2ff6692084bcd1a";
    internal const string ReadsMigrationChecksum = "b0c5bb1e58f8791f16d225dc10d1770552ba35a103c2fc5b30960f86c0caa6f7";
    internal static readonly string[] Tables = ["preview_documents", "preview_runs", Braid.Persistence.Postgres.XmlPreviewSchemaAdmission.Table];
    internal static readonly string[] FunctionNames =
    [
        "preview_authorized", "preview_cancel", "preview_claim", "preview_complete", "preview_history", "preview_input",
        "preview_input_documents", "preview_inputs", "preview_observe", "preview_output", "preview_outputs", "preview_read",
        "preview_release", "preview_submit", "preview_complete_csv", "preview_csv_outputs", "preview_csv_input", "guard_csv_preview_document",
        "csv_dialect_valid", "csv_dialect_char_canonical", "csv_dialect_canonical", Braid.Persistence.Postgres.DocumentJsonSchemaAdmission.FunctionName,
        .. Braid.Persistence.Postgres.XmlPreviewSchemaAdmission.Functions
    ];
    internal static readonly string[] Roles = ["braid_preview_reader", "braid_preview_writer"];
    // Child FKs whose internal RI triggers land on older parent relations.
    internal static readonly string[] ParentForeignKeys = ["preview_runs_revision_fkey", "preview_runs_submitter_fkey"];
}
