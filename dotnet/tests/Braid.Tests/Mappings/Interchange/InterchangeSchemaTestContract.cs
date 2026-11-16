namespace Braid.Tests;

internal static class InterchangeSchemaTestContract
{
    internal const string MigrationChecksum = "1097d2a50822d2cdfe3466d9117eeb78aa87b8d0bfee88b9fbc15e2a5e44772d";
    internal static readonly string[] Tables = ["revision_import_provenance", "import_reviews", "export_reviews"];
    internal static readonly string[] Functions =
        ["guard_revision_import_provenance_transition", "guard_import_reviews_transition", "guard_export_reviews_transition"];
}
