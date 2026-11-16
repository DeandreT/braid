using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReleaseOperationsSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Operations_schema_and_function_contracts_are_pinned()
    {
        await using var test = await fixture.CreateCaseAsync();
        output.WriteLine("PostgreSQL=" + await test.ScalarAsync<string>("SHOW server_version"));
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var release = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, default);
        var operations = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ReleaseOperationsRoleGuard.Tables, default);
        var administration = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AdministrationRoleGuard.AuthorityTables, default);
        output.WriteLine("Release=" + release);
        output.WriteLine("Operations=" + operations);
        output.WriteLine("Administration=" + administration);
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, ReleaseOperationsRoleGuard.Names,
            BuildRoleGuard.LoadFunctions("0031_release_operations.sql", ReleaseOperationsRoleGuard.Names.Length), default);
        Assert.Equal(ReleaseOperationsRoleGuard.ReleaseFingerprint, release);
        Assert.Equal(ReleaseOperationsRoleGuard.OperationsFingerprint, operations);
        Assert.Equal(AdministrationRoleGuard.Fingerprint, administration);
    }
}
