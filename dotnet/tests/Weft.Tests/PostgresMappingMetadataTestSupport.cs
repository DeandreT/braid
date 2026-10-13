using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL metadata operations; initial revisions use explicitly synthetic C1 evidence.</summary>
internal sealed class PostgresMappingMetadataTestSupport : IAsyncDisposable
{
    private readonly PostgresCatalogTestSupport catalog;
    internal PostgresCase Case => catalog.Case;
    internal PostgresSeedData Seed => catalog.Seed;
    internal ActorContext Actor => catalog.Actor;
    internal ProjectScope Project => catalog.Project;
    internal PostgresCatalogReader Reader => catalog.Reader;
    internal PostgresCatalogDatabase CatalogDatabase => catalog.Database;
    internal PostgresIngestionDatabase IngestionDatabase => catalog.IngestionDatabase;
    internal PostgresRevisionIngestionStore Ingestion => catalog.Store;
    internal PostgresMappingMetadataDatabase Database { get; }
    internal PostgresMappingMetadataWriter Writer { get; }
    internal IngestionReceipt Original { get; }
    internal MappingScope Scope => PostgresCatalogTestSupport.Target(Original);

    private PostgresMappingMetadataTestSupport(PostgresCatalogTestSupport catalog, IngestionReceipt original)
    {
        this.catalog = catalog;
        Original = original;
        Database = new(Case.MetadataConnectionString);
        Writer = new(Database);
    }

    internal static async Task<PostgresMappingMetadataTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        try { return new(catalog, await catalog.CommitCreateAsync("Original synthetic metadata", "Original description", filename: "original.native.json")); }
        catch { await catalog.DisposeAsync(); throw; }
    }

    internal Task<IngestionReceipt> CommitCreateAsync(ProjectScope? project = null, ActorContext? actor = null) =>
        catalog.CommitCreateAsync(project: project, actor: actor);

    internal Task<IngestionReceipt> CommitAppendAsync(IngestionReceipt? mapping = null, bool valid = true) =>
        catalog.CommitAppendAsync(mapping ?? Original, valid);

    internal async Task<PostgresMetadataReadyAppend> ReadyAppendAsync(HeadCondition? head = null)
    {
        var body = PostgresIngestionTestSupport.SyntheticBody;
        var intent = IngestionIntent.ForAppend(Scope, Original.Facts.BackendProfile.BackendId, "synthetic append.native.json",
            head ?? new AnyHead(), Sha256Digest.FromBytes(body), body.LongLength);
        var reserved = await Ingestion.ReserveAsync(Actor, Project, Guid.NewGuid(), intent,
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var facts = reserved.Receipt!.Facts;
        var claimed = await Ingestion.ClaimAsync(Actor, Project, facts.OperationId, Guid.NewGuid());
        var claim = Assert.IsType<IngestionClaim>(claimed.Claim);
        Assert.Equal(IngestionStoreOutcome.Applied, (await Ingestion.MarkStagedAsync(Actor, claim, facts.NativeObject)).Outcome);
        var completed = await Ingestion.CompleteAttemptAsync(Actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts));
        Assert.Equal(IngestionPhase.Ready, completed.Receipt!.Phase);
        return new(facts, claim);
    }

    internal Task<MappingMetadataWriteResult> ReplaceAsync(long expectedVersion, string name, string description,
        CancellationToken cancellationToken = default) => Writer.ReplaceAsync(Actor, Scope, new(Scope, expectedVersion), new(name, description), cancellationToken);

    internal async Task<CatalogMappingSummary> HeaderAsync() => (await Reader.GetMappingAsync(Actor, Scope))!.Summary;

    internal Task<string> RowSnapshotAsync() => Case.ScalarAsync<string>(
        "SELECT to_jsonb(mapping)::text FROM weft.mappings mapping WHERE organization_id=$1 AND project_id=$2 AND id=$3",
        Scope.OrganizationId, Scope.ProjectId, Scope.MappingId);

    internal Task<string[]> ImmutableSnapshotAsync(IngestionReceipt? receipt = null)
    {
        var facts = (receipt ?? Original).Facts;
        return Case.ScalarAsync<string[]>("""
            SELECT ARRAY[
              (SELECT to_jsonb(revision)::text FROM weft.mapping_revisions revision
               WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND id=$4),
              (SELECT to_jsonb(ingestion)::text FROM weft.revision_ingestions ingestion
               WHERE organization_id=$1 AND project_id=$2 AND operation_id=$5),
              (SELECT jsonb_agg(to_jsonb(attempt) ORDER BY generation)::text FROM weft.revision_ingestion_attempts attempt
               WHERE organization_id=$1 AND project_id=$2 AND operation_id=$5)]
            """, facts.Project.OrganizationId, facts.Project.ProjectId, facts.FutureRevision.MappingId,
            facts.FutureRevision.RevisionId, facts.OperationId);
    }

    internal async Task SetSyntheticVersionAsync(long version, bool aheadTimestamp = false)
    {
        // An explicitly synthetic counter edge in this owned case. Product calls
        // start only after the exact accepted transition guard is restored.
        await using var connection = await Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand("ALTER TABLE weft.mappings DISABLE TRIGGER mappings_transition_guard", connection, transaction))
            await disable.ExecuteNonQueryAsync();
        await using (var update = new NpgsqlCommand("UPDATE weft.mappings SET version=$1" +
            (aheadTimestamp ? ",updated_at=clock_timestamp()+interval '1 day'" : "") +
            " WHERE organization_id=$2 AND project_id=$3 AND id=$4", connection, transaction))
        {
            foreach (var value in new object[] { version, Scope.OrganizationId, Scope.ProjectId, Scope.MappingId }) update.Parameters.Add(new NpgsqlParameter { Value = value });
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction)) await flush.ExecuteNonQueryAsync();
        await using (var enable = new NpgsqlCommand("ALTER TABLE weft.mappings ENABLE TRIGGER mappings_transition_guard", connection, transaction)) await enable.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal static async Task<int> BackendAsync(NpgsqlDataSource pool)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(pool.ConnectionString).MaxPoolSize);
        await using var connection = await pool.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    internal async Task WaitForBlockedAsync(PostgresMetadataRowBlocker blocker, int[] contenders, Task[] operations)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            foreach (var operation in operations)
            {
                if (!operation.IsCompleted) continue;
                await operation;
                throw new InvalidOperationException("A metadata contender completed before its intended PostgreSQL lock wait.");
            }
            var graph = new Dictionary<int, int[]>();
            await using (var command = Case.Owner.CreateCommand("SELECT pid,pg_blocking_pids(pid) FROM unnest($1::integer[]) pid"))
            {
                command.Parameters.AddWithValue(contenders);
                await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                while (await reader.ReadAsync(deadline.Token)) graph.Add(reader.GetInt32(0), reader.GetFieldValue<int[]>(1));
            }
            if (contenders.All(pid => ReachesBlocker(pid, blocker.Backend, graph, new HashSet<int>()))) return;
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    private static bool ReachesBlocker(int pid, int blocker, IReadOnlyDictionary<int, int[]> graph, HashSet<int> seen)
    {
        if (pid == blocker) return true;
        if (!seen.Add(pid) || !graph.TryGetValue(pid, out var blocking)) return false;
        return blocking.Any(next => ReachesBlocker(next, blocker, graph, seen));
    }

    internal static void AssertIdentityUnchanged(CatalogMappingSummary original, CatalogMappingSummary current)
    {
        Assert.Equal(original.Scope, current.Scope);
        Assert.Equal(original.BackendId, current.BackendId);
        Assert.Equal(original.Creator, current.Creator);
        Assert.Equal(original.Owner, current.Owner);
        Assert.Equal(original.CreatedAtUtc, current.CreatedAtUtc);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await catalog.DisposeAsync();
    }
}

internal sealed record PostgresMetadataReadyAppend(ReservedIngestionFacts Facts, IngestionClaim Claim);

internal sealed class PostgresMetadataRowBlocker : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private bool released;
    internal int Backend { get; }
    private PostgresMetadataRowBlocker(NpgsqlConnection connection, NpgsqlTransaction transaction, int backend)
    { this.connection = connection; this.transaction = transaction; Backend = backend; }

    internal static async Task<PostgresMetadataRowBlocker> CreateAsync(PostgresCase test, MappingScope scope)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            try
            {
                await using var command = new NpgsqlCommand("SELECT id,pg_backend_pid() FROM weft.mappings WHERE organization_id=$1 AND project_id=$2 AND id=$3 FOR NO KEY UPDATE", connection, transaction);
                foreach (var value in new[] { scope.OrganizationId, scope.ProjectId, scope.MappingId }) command.Parameters.AddWithValue(value);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                return new(connection, transaction, reader.GetInt32(1));
            }
            catch { await transaction.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal async Task ReleaseAsync()
    {
        if (released) return;
        await transaction.RollbackAsync();
        released = true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}

internal sealed class PostgresMetadataWriterGate(IMappingMetadataWriter inner, RevisionIngestionGate gate) : IMappingMetadataWriter
{
    public async Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope,
        MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken = default)
    {
        await gate.HoldAsync(cancellationToken);
        return await inner.ReplaceAsync(actor, scope, expectedVersion, edit, cancellationToken);
    }
}
