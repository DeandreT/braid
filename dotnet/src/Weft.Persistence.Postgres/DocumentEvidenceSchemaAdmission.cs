using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class DocumentEvidenceSchemaAdmission
{
    internal static string MigrationSql => MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 46).Sql;
    internal static string MigrationChecksum => MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 46).Checksum;
    internal const string SpecChecksum = "4d0d549c31cd208ea672c11273928f2b5d6dd3d977f427c6e3772911ec18db9b";
    internal const string CreationChecksum = "b431accbe354bfcb9a3332729752bf32f7f0297fbaacb51bd0b466ed58054b30";

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var history = new NpgsqlCommand("SELECT weft.document_evidence_schema_version()", connection, transaction);
        if (await history.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string checksum || checksum != MigrationChecksum ||
            await CreationSchemaAdmission.ShapeAsync(connection, transaction, ct).ConfigureAwait(false) != CreationSchemaAdmission.ShapeDigest)
            throw new InvalidOperationException("Document evidence schema admission failed.");
    }
}
