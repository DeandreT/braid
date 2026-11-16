using System.Text.RegularExpressions;
using Npgsql;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

internal static class SpecSchemaTestContract
{
    internal const string Checksum = "78c86ad3b972cab04e972004a5e2ba5a9ad983ae3037867f70011aa91e7a5cd8";
    internal static readonly string[] Tables = ["spec_stores", "spec_bundles", "spec_sources", "spec_objects", "spec_temporaries", "spec_temporary_seals"];
    internal static readonly string[] Roles = ["braid_spec_reader", "braid_spec_writer", "braid_spec_maintenance"];
    internal static readonly string[] Functions = Regex.Matches(SpecSchemaAdmission.MigrationSql + DocumentEvidenceSchemaAdmission.MigrationSql, @"CREATE FUNCTION braid\.(spec_\w+)\(")
        .Select(match => match.Groups[1].Value).ToArray();

    internal static async Task AdmitAsync(PostgresCase test, CancellationToken token)
    {
        Assert.Equal(Checksum, MigrationRunner.EmbeddedMigrations[43].Checksum);
        foreach (var (role, profile) in new[] { (Roles[0], SpecDatabaseProfile.Reader), (Roles[1], SpecDatabaseProfile.Writer), (Roles[2], SpecDatabaseProfile.Maintenance) })
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.RegressionConnectionString(role)) { Pooling = false }.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await SpecSchemaAdmission.ValidateAsync(connection, transaction, profile, token);
        }
    }
}
