namespace Weft.Tests;

/// <summary>The exact additive delta of migration 0012, projected out of older conservation snapshots.</summary>
internal static class PreviewSchemaTestContract
{
    internal const string MigrationChecksum = "f11b094fbd09a42baa20473fca3d4ba6aa374da27550eee9da21a83827f588a9";
    internal static readonly string[] Tables = ["preview_documents", "preview_runs"];
    internal static readonly string[] FunctionNames =
    [
        "preview_authorized", "preview_cancel", "preview_claim", "preview_complete", "preview_inputs", "preview_observe",
        "preview_output", "preview_outputs", "preview_read", "preview_release", "preview_submit"
    ];
    internal static readonly string[] Roles = ["weft_preview_reader", "weft_preview_writer"];
    // Child FKs whose internal RI triggers land on older parent relations.
    internal static readonly string[] ParentForeignKeys = ["preview_runs_revision_fkey", "preview_runs_submitter_fkey"];
}
