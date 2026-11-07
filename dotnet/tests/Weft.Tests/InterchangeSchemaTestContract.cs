namespace Weft.Tests;

internal static class InterchangeSchemaTestContract
{
    internal const string MigrationChecksum = "07fc1195130198cd1f0bf79737c088ea208a33d0d6933d920ae7ec8c9843d086";
    internal static readonly string[] Tables = ["revision_import_provenance", "import_reviews", "export_reviews"];
    internal static readonly string[] Functions =
        ["guard_revision_import_provenance_transition", "guard_import_reviews_transition", "guard_export_reviews_transition"];
}
