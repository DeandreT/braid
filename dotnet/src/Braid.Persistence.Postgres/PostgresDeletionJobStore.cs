using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>
/// The restricted lifecycle worker pool. Every call is one short read committed
/// transaction through an owner-defined entry point; the pool has no table access.
/// </summary>
public sealed class PostgresDeletionJobStore(string connectionString) : IDeletionJobStore, IRetentionSweepStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<DeletionJobClaim?> ClaimAsync(Guid instanceId, CancellationToken token)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("Worker instance required.", nameof(instanceId));
        return await RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT braid.claim_deletion_job($1)::text", connection, transaction);
            command.Parameters.AddWithValue(instanceId);
            if (await command.ExecuteScalarAsync(token) is not string text) return (DeletionJobClaim?)null;
            using var json = Parse(text);
            var row = json.RootElement;
            return new DeletionJobClaim(Id(row, "organizationId"), Id(row, "jobId"), instanceId, Count(row, "generation"), Id(row, "token"),
                row.GetProperty("leaseExpiresAtUtc").GetDateTimeOffset(), State(Text(row, "state")));
        }, token);
    }

    internal const int BatchSize = 500;

    public async Task<DeletionJobStepOutcome> AdvanceAsync(DeletionJobClaim claim, CancellationToken token)
    {
        // Large families leave in bounded batches, each its own short transaction; the step removes the rest.
        while (await BatchAsync(claim, token) > 0) { }
        return await StepAsync(claim, token);
    }

    private Task<long> BatchAsync(DeletionJobClaim claim, CancellationToken token) =>
        RunAsync(false, async (connection, transaction) =>
        {
            await using var command = Claimed("SELECT braid.purge_catalog_batch($1,$2,$3,$4,$5,$6)::text", connection, transaction, claim);
            command.Parameters.AddWithValue(BatchSize);
            if (await command.ExecuteScalarAsync(token) is not string text) throw Invalid();
            using var json = Parse(text);
            return Text(json.RootElement, "outcome") is "batched" && long.TryParse(Text(json.RootElement, "removed"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var removed) ? removed : 0;
        }, token);

    private Task<DeletionJobStepOutcome> StepAsync(DeletionJobClaim claim, CancellationToken token) =>
        RunAsync(false, async (connection, transaction) =>
        {
            await using var command = Claimed("SELECT braid.advance_deletion_job($1,$2,$3,$4,$5)::text", connection, transaction, claim);
            if (await command.ExecuteScalarAsync(token) is not string text) throw Invalid();
            using var json = Parse(text);
            return Text(json.RootElement, "outcome") switch
            {
                "advanced" => DeletionJobStepOutcome.Advanced,
                "waiting" => DeletionJobStepOutcome.Waiting,
                "objects" => DeletionJobStepOutcome.Objects,
                "blocked" => DeletionJobStepOutcome.Blocked,
                "completed" => DeletionJobStepOutcome.Completed,
                "claimLost" => DeletionJobStepOutcome.ClaimLost,
                _ => throw Invalid()
            };
        }, token);

    public Task<IReadOnlyList<CatalogObjectDeletionClaim>> ClaimObjectsAsync(DeletionJobClaim claim, int limit, CancellationToken token) =>
        RunAsync(false, async (connection, transaction) =>
        {
            await using var command = Claimed("SELECT braid.claim_deletion_objects($1,$2,$3,$4,$5,$6)::text", connection, transaction, claim);
            command.Parameters.AddWithValue(limit);
            if (await command.ExecuteScalarAsync(token) is not string text || text.Length > 4_194_304) throw Invalid();
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var result = new List<CatalogObjectDeletionClaim>();
            foreach (var row in json.RootElement.EnumerateArray())
            {
                var scope = new RevisionScope(claim.OrganizationId, Id(row, "projectId"), Id(row, "mappingId"), Id(row, "revisionId"));
                var @object = PrivateObjectId.Restore(scope, Id(row, "objectId"), Text(row, "objectClass") switch
                {
                    "native" => PrivateObjectClass.NativeArtifact,
                    "derived" => PrivateObjectClass.DerivedArtifact,
                    _ => throw Invalid()
                });
                var temporary = Text(row, "targetKind") switch { "final_object" => false, "tracked_temporary" => true, _ => throw Invalid() };
                var target = new ObjectCleanupTarget(@object, temporary ? CleanupTargetKind.TrackedTemporary : CleanupTargetKind.FinalObject,
                    temporary ? Id(row, "publicationId") : null);
                var publication = temporary ? PrivateObjectMaintenancePersistence.Publication(row.GetProperty("publication")) : null;
                result.Add(new(claim.JobId, Id(row, "objectTargetId"), Count(row, "generation"), claim.InstanceId, Id(row, "token"),
                    row.GetProperty("leaseExpiresAtUtc").GetDateTimeOffset(), target, publication));
            }
            return (IReadOnlyList<CatalogObjectDeletionClaim>)result;
        }, token);

    public Task<bool> FinishObjectAsync(DeletionJobClaim claim, CatalogObjectDeletionClaim target, PrivateObjectRetirementOutcome observed, CancellationToken token) =>
        RunAsync(false, async (connection, transaction) =>
        {
            await using var command = Claimed("SELECT braid.finish_deletion_object($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection, transaction, claim);
            command.Parameters.AddWithValue(target.ObjectTargetId);
            command.Parameters.AddWithValue(target.Generation);
            command.Parameters.AddWithValue(target.Token);
            command.Parameters.AddWithValue(observed switch
            {
                PrivateObjectRetirementOutcome.Deleted => "deleted",
                PrivateObjectRetirementOutcome.Missing => "missing",
                PrivateObjectRetirementOutcome.Busy => "busy",
                PrivateObjectRetirementOutcome.Unsupported => "unsupported",
                PrivateObjectRetirementOutcome.UnsafeStorage => "unsafeStorage",
                PrivateObjectRetirementOutcome.Io => "io",
                _ => throw new ArgumentOutOfRangeException(nameof(observed))
            });
            return (await command.ExecuteScalarAsync(token) as string) switch
            {
                "recorded" => true,
                "claimLost" => false,
                _ => throw Invalid()
            };
        }, token);

    public Task ReleaseAsync(DeletionJobClaim claim, CancellationToken token) =>
        RunAsync(false, async (connection, transaction) =>
        {
            await using var command = Claimed("SELECT braid.release_deletion_job($1,$2,$3,$4,$5)", connection, transaction, claim);
            return await command.ExecuteScalarAsync(token) is true;
        }, token);

    public async Task<int> SweepAsync(Guid instanceId, int limit, CancellationToken token) =>
        await SweepAsync("sweep_retention", instanceId, limit, token) + await SweepAsync("sweep_retention_history", instanceId, limit, token);

    private Task<int> SweepAsync(string function, Guid instanceId, int limit, CancellationToken token) =>
        RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand($"SELECT braid.{function}($1,$2)::text", connection, transaction);
            command.Parameters.AddWithValue(instanceId);
            command.Parameters.AddWithValue(limit);
            if (await command.ExecuteScalarAsync(token) is not string text) throw Invalid();
            using var json = Parse(text);
            var erased = Text(json.RootElement, "erased");
            return erased == "0" ? 0 : (int)Count(json.RootElement, "erased");
        }, token);

    private async Task<T> RunAsync<T>(bool validate, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='60s'; SET LOCAL lock_timeout='10s'; SET LOCAL TimeZone='UTC'", connection, transaction))
            await settings.ExecuteNonQueryAsync(token);
        // The exact restricted login and schema are admitted once per claim, before any work.
        if (validate) await PurgeSchemaAdmission.ValidateWorkerAsync(connection, transaction, token);
        var result = await work(connection, transaction);
        await transaction.CommitAsync(token);
        return result;
    }

    private static NpgsqlCommand Claimed(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, DeletionJobClaim claim)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(claim.OrganizationId);
        command.Parameters.AddWithValue(claim.JobId);
        command.Parameters.AddWithValue(claim.InstanceId);
        command.Parameters.AddWithValue(claim.Generation);
        command.Parameters.AddWithValue(claim.Token);
        return command;
    }

    private static DeletionJobState State(string value) => value switch
    {
        "quiescing" => DeletionJobState.Quiescing,
        "inventory" => DeletionJobState.Inventory,
        "catalogPurging" => DeletionJobState.CatalogPurging,
        "objectsPending" => DeletionJobState.ObjectsPending,
        _ => throw Invalid()
    };

    private static JsonDocument Parse(string text) =>
        text.Length <= 4096 ? JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 }) : throw Invalid();
    private static string Text(JsonElement row, string name) =>
        row.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw Invalid();
    private static Guid Id(JsonElement row, string name) =>
        Guid.TryParseExact(Text(row, name), "D", out var id) && id != Guid.Empty ? id : throw Invalid();
    private static long Count(JsonElement row, string name) =>
        long.TryParse(Text(row, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : throw Invalid();
    private static InvalidOperationException Invalid() => new("Deletion job metadata is unavailable.");

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
