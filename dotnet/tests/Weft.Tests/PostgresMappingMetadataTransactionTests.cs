using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataTransactionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task MetadataCommitsReuseOneBackendAcrossOrganizationsAndReturnAnUnboundConnection()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var other = await test.CommitCreateAsync(new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB));
        var scopeB = PostgresCatalogTestSupport.Target(other);
        var originalA = await test.ImmutableSnapshotAsync();
        var originalB = await test.ImmutableSnapshotAsync(other);
        var backend = await PostgresMappingMetadataTestSupport.BackendAsync(test.Database.DataSource);
        var changedA = await test.ReplaceAsync(1, "Organization A edit", "A updated description");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, changedA.Outcome);
        Assert.Equal(test.Scope, changedA.Metadata!.Scope);
        Assert.Equal(2, changedA.Metadata.Version);
        await AssertUnboundAsync(test.Database, backend);
        var changedB = await test.Writer.ReplaceAsync(test.Actor, scopeB, new(scopeB, 1), new("Organization B edit", "B updated description"));
        Assert.Equal(MappingMetadataWriteOutcome.Updated, changedB.Outcome);
        Assert.Equal(scopeB, changedB.Metadata!.Scope);
        Assert.Equal(2, changedB.Metadata.Version);
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal("Organization A edit", (await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope))!.Name);
        Assert.Equal("Organization B edit", (await test.Reader.GetMappingMetadataAsync(test.Actor, scopeB))!.Name);
        Assert.Equal(originalA, await test.ImmutableSnapshotAsync());
        Assert.Equal(originalB, await test.ImmutableSnapshotAsync(other));
    }

    [PostgresFact]
    public async Task RollbackAndIncompleteDisposalUndoRealMetadataChangesAndReuseTheSameBackend()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var before = await test.RowSnapshotAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        var backend = await PostgresMappingMetadataTestSupport.BackendAsync(test.Database.DataSource);
        await using (var rollback = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(rollback, "SELECT pg_backend_pid()"));
            await MutateAsync(rollback, test.Scope, "Rolled back metadata");
            Assert.Equal("Rolled back metadata", await ScalarAsync<string>(rollback,
                "SELECT name FROM weft.mappings WHERE organization_id=$1 AND project_id=$2 AND id=$3",
                test.Scope.OrganizationId, test.Scope.ProjectId, test.Scope.MappingId));
            await rollback.RollbackAsync();
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(before, await test.RowSnapshotAsync());
        await using (var other = await test.Database.BeginTransactionAsync(test.Seed.OrganizationB))
        {
            Assert.Equal(backend, await ScalarAsync<int>(other, "SELECT pg_backend_pid()"));
            Assert.Equal(test.Seed.OrganizationB.ToString("D"), await ScalarAsync<string>(other, "SHOW weft.organization_id"));
            Assert.Equal(0L, await ScalarAsync<long>(other, "SELECT count(*) FROM weft.mappings"));
            await other.RollbackAsync();
        }
        await using (var abandoned = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(abandoned, "SELECT pg_backend_pid()"));
            await MutateAsync(abandoned, test.Scope, "Abandoned metadata");
            using var queuedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.ReplaceAsync(
                1, "Queued metadata", "Canceled before a connection is available", queuedCancellation.Token));
            Assert.Equal(backend, await ScalarAsync<int>(abandoned, "SELECT pg_backend_pid()"));
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(before, await test.RowSnapshotAsync());
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
        Assert.Equal(MappingMetadataWriteOutcome.Updated, (await test.ReplaceAsync(1, "After rollback", "Usable pool")).Outcome);
        await AssertUnboundAsync(test.Database, backend);
    }

    [PostgresFact]
    public async Task ReadWriteScopeUsesBoundedLocalTimeoutsSingleBindingAndRejectsPresetOrChangedContext()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var before = await test.RowSnapshotAsync();
        var backend = await PostgresMappingMetadataTestSupport.BackendAsync(test.Database.DataSource);
        var preset = new NpgsqlConnectionStringBuilder(test.Case.MetadataConnectionString)
        {
            Options = $"-c weft.organization_id={test.Seed.OrganizationB:D}"
        };
        await using (var unsafePool = new PostgresMappingMetadataDatabase(preset.ConnectionString))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => unsafePool.BeginTransactionAsync(test.Scope.OrganizationId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresMappingMetadataWriter(unsafePool).ReplaceAsync(
                test.Actor, test.Scope, new(test.Scope, 1), new("Preset context edit", "Must be refused")));
        }
        await using (var scope = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(scope, "SELECT pg_backend_pid()"));
            Assert.Equal("off", await ScalarAsync<string>(scope, "SHOW transaction_read_only"));
            Assert.Equal("read committed", await ScalarAsync<string>(scope, "SHOW transaction_isolation"));
            Assert.Equal("1s", await ScalarAsync<string>(scope, "SHOW lock_timeout"));
            Assert.Equal("5s", await ScalarAsync<string>(scope, "SHOW statement_timeout"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(test.Seed.OrganizationB));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.BindOrganizationAsync(test.Scope.OrganizationId));
            Assert.Equal(test.Scope.OrganizationId.ToString("D"), await ScalarAsync<string>(scope, "SHOW weft.organization_id"));
            Assert.Equal(0L, await ScalarAsync<long>(scope, "SELECT count(*) FROM weft.mappings WHERE organization_id=$1", test.Seed.OrganizationB));
            await using (var tamper = scope.CreateCommand("SELECT set_config('weft.organization_id',$1,true)"))
            {
                tamper.Parameters.AddWithValue(test.Seed.OrganizationB.ToString("D"));
                await tamper.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RequireOrganizationAsync(test.Scope.OrganizationId, CancellationToken.None));
            await scope.RollbackAsync();
            Assert.Throws<InvalidOperationException>(() => scope.CreateCommand("SELECT 1"));
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(before, await test.RowSnapshotAsync());
    }

    [PostgresFact]
    public async Task CancellationOfAnObservedRowBlockedWriterAndSqlFailureReleaseTheOnlyConnection()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var before = await test.RowSnapshotAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        var backend = await PostgresMappingMetadataTestSupport.BackendAsync(test.Database.DataSource);
        using var cancellation = new CancellationTokenSource();
        await using (var blocker = await PostgresMetadataRowBlocker.CreateAsync(test.Case, test.Scope))
        {
            var pending = test.ReplaceAsync(1, "Canceled metadata", "Must not be committed", cancellation.Token);
            try
            {
                await test.WaitForBlockedAsync(blocker, [backend], [pending]);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            finally
            {
                cancellation.Cancel();
                await blocker.ReleaseAsync();
                // Drain the canceled request even if a preceding assertion failed.
                try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception) { }
            }
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(before, await test.RowSnapshotAsync());
        await using (var failed = await test.Database.BeginTransactionAsync(test.Scope.OrganizationId))
        {
            Assert.Equal(backend, await ScalarAsync<int>(failed, "SELECT pg_backend_pid()"));
            await using var division = failed.CreateCommand("SELECT 1/0");
            var error = await Assert.ThrowsAsync<PostgresException>(() => division.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
        }
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(before, await test.RowSnapshotAsync());
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
        var recovered = await test.ReplaceAsync(1, "After cancellation and SQL failure", "Usable writer");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, recovered.Outcome);
        Assert.Equal(2, recovered.Metadata!.Version);
        await AssertUnboundAsync(test.Database, backend);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    [PostgresFact]
    public async Task FailedRoleAdmissionReleasesTheSameBackendBeforeBindingAndRecoversAfterExactGrantRestore()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var before = await test.RowSnapshotAsync();
        var backend = await PostgresMappingMetadataTestSupport.BackendAsync(test.Database.DataSource);
        await test.Case.ExecuteAsync("REVOKE UPDATE(updated_at) ON weft.mappings FROM weft_catalog_editor");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.ReplaceAsync(1, "Forbidden update", "Missing grant"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Database.BeginTransactionAsync(test.Seed.OrganizationB));
            await AssertUnboundAsync(test.Database, backend);
        }
        finally { await test.Case.ExecuteAsync("GRANT UPDATE(updated_at) ON weft.mappings TO weft_catalog_editor"); }
        Assert.Equal(before, await test.RowSnapshotAsync());
        var result = await test.ReplaceAsync(1, "Restored metadata writer", "Exact grants restored");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, result.Outcome);
        await AssertUnboundAsync(test.Database, backend);
    }

    private static async Task MutateAsync(PostgresTransaction transaction, MappingScope scope, string name)
    {
        await using var command = transaction.CreateCommand("""
            UPDATE weft.mappings SET name=$1,version=version+1,updated_at=greatest(updated_at,clock_timestamp())
            WHERE organization_id=$2 AND project_id=$3 AND id=$4
            """);
        Add(command, name, scope.OrganizationId, scope.ProjectId, scope.MappingId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<T> ScalarAsync<T>(PostgresTransaction transaction, string sql, params object[] values)
    {
        await using var command = transaction.CreateCommand(sql);
        Add(command, values);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected metadata transaction scalar."));
    }

    private static async Task AssertUnboundAsync(PostgresMappingMetadataDatabase database, int expectedBackend)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var backend = new NpgsqlCommand("SELECT pg_backend_pid()", connection))
            Assert.Equal(expectedBackend, await backend.ExecuteScalarAsync());
        await using (var context = new NpgsqlCommand("SELECT NULLIF(current_setting('weft.organization_id',true),'')", connection))
            Assert.Equal(DBNull.Value, await context.ExecuteScalarAsync());
        foreach (var (setting, expected) in new[] { ("transaction_read_only", "off"), ("lock_timeout", "0"), ("statement_timeout", "0") })
        {
            await using var command = new NpgsqlCommand($"SHOW {setting}", connection);
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
        foreach (var table in new[] { "organizations", "organization_memberships", "projects", "project_memberships", "mappings" })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM weft.{table}", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    private static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
}
