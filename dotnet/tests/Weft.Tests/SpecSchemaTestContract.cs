using System.Text.RegularExpressions;
using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

internal static class SpecSchemaTestContract
{
    internal const string Checksum = "4d0d549c31cd208ea672c11273928f2b5d6dd3d977f427c6e3772911ec18db9b";
    internal static readonly string[] Tables = ["spec_stores", "spec_bundles", "spec_sources", "spec_objects", "spec_temporaries", "spec_temporary_seals"];
    internal static readonly string[] Roles = ["weft_spec_reader", "weft_spec_writer", "weft_spec_maintenance"];
    internal static readonly string[] Functions = Regex.Matches(SpecSchemaAdmission.MigrationSql + DocumentEvidenceSchemaAdmission.MigrationSql, @"CREATE FUNCTION weft\.(spec_\w+)\(")
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
