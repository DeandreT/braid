using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildRunSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Fresh_build_dispatch_schema_has_the_exact_qualified_contract()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(19).ToArray()))
            await prefix.MigrateAsync();
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new Npgsql.NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        var publication = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRoleGuard.Tables, default);
        var runs = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, BuildRunRoleGuard.Tables, default);
        output.WriteLine("PublicationFingerprint=" + publication);
        output.WriteLine("RunFingerprint=" + runs);
        Assert.Equal(BuildRunRoleGuard.PublicationFingerprint, publication);
        Assert.Equal(BuildRunRoleGuard.RunFingerprint, runs);
        await transaction.CommitAsync();
    }

    [PostgresFact]
    public async Task Missing_dispatch_profiles_preserve_the_qualified_schema18_controls()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionBuildRunProfiles: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(18).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => test.MigrateAsync());
        Assert.Equal(18L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        await using var legacy = new PostgresBuildStore(test.RegressionConnectionString("braid_build_reader"), false);
        Assert.Null(await legacy.ReadEnvironmentAsync(new(new(Guid.NewGuid())),
            new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1)));
    }

    [PostgresFact]
    public async Task Actor_and_dispatcher_credentials_cannot_adopt_each_others_functions_or_table_access()
    {
        await using var test = await fixture.CreateCaseAsync();
        foreach (var role in new[] { "braid_build_run_reader", "braid_build_run_writer", "braid_build_dispatcher" })
        {
            await using var connection = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(test.RegressionConnectionString(role)) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT has_table_privilege(current_user,'braid.build_runs','SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'),
                    has_function_privilege(current_user,'braid.discover_build_runs(integer)','EXECUTE'),
                    has_function_privilege(current_user,'braid.write_build_run(text,uuid,uuid,bytea)','EXECUTE'),
                    has_function_privilege(current_user,'braid.read_build_run(uuid,uuid)','EXECUTE')
                """, connection);
            await using var rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.False(rows.GetBoolean(0));
            Assert.Equal(role == "braid_build_dispatcher", rows.GetBoolean(1));
            Assert.Equal(role == "braid_build_run_writer", rows.GetBoolean(2));
            Assert.Equal(role != "braid_build_dispatcher", rows.GetBoolean(3));
        }
    }
}
