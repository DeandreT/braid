using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class DocumentEvidenceSchemaAdmission
{
    internal static string MigrationSql => MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 46).Sql;
    internal static string MigrationChecksum => MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 46).Checksum;
    internal const string SpecChecksum = "78c86ad3b972cab04e972004a5e2ba5a9ad983ae3037867f70011aa91e7a5cd8";
    internal const string CreationChecksum = "7756574c6fd95a5799fe1fea20c8055f627d55ba36dd41a4071180c9dbb36906";

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var history = new NpgsqlCommand("SELECT braid.document_evidence_schema_version()", connection, transaction);
        if (await history.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string checksum || checksum != MigrationChecksum ||
            await CreationSchemaAdmission.ShapeAsync(connection, transaction, ct).ConfigureAwait(false) != CreationSchemaAdmission.ShapeDigest)
            throw new InvalidOperationException("Document evidence schema admission failed.");
    }
}
