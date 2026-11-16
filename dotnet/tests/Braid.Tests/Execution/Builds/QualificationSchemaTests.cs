using Npgsql;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class QualificationSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Qualified_schema_is_exact_and_credentials_are_separate()
    {
        await using var test = await fixture.CreateCaseAsync();
        await using (var connection = await test.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, QualificationRoleGuard.Tables, default);
            output.WriteLine("QualificationFingerprint=" + fingerprint);
            Assert.Equal(QualificationRoleGuard.Fingerprint, fingerprint);
        }
        await using var reader = new PostgresBuildQualificationStore(test.RegressionConnectionString("braid_qualification_reader"), false);
        Assert.Null(await reader.ReadAsync(new(new(Guid.NewGuid())), new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task Missing_credentials_leave_schema20_unchanged()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionQualifications: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(20).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
        Assert.Equal(20L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        await using var reader = new PostgresBuildMetadataReader(test.RegressionConnectionString("braid_build_metadata_reader"));
        Assert.Null(await reader.ReadEnvironmentAsync(new(new(Guid.NewGuid())), new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1)));
    }
}
