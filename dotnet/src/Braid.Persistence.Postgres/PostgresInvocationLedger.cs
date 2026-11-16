using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>The machine invocation ledger through the restricted invocation-writer login. It never reads payload inputs.</summary>
public sealed class PostgresInvocationLedger(string connectionString) : IMachineInvocationLedger, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public Task<MachineInvocationClaim> ClaimAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest, CancellationToken token = default) =>
        ClaimCoreAsync(key, applicationId, requestDigest, null, token);

    public Task<MachineInvocationClaim> ClaimWithMetadataAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest, int inputBytes, CancellationToken token = default) =>
        ClaimCoreAsync(key, applicationId, requestDigest, inputBytes, token);

    private async Task<MachineInvocationClaim> ClaimCoreAsync(MachineInvocationKey key, Guid applicationId, Sha256Digest requestDigest, int? inputBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(requestDigest);
        if (applicationId == Guid.Empty) throw new ArgumentException("A client application ID is required.", nameof(applicationId));
        await using var tx = await BeginAsync(key.Target.Mapping.OrganizationId, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.claim_machine_invocation(@org,@project,@mapping,@environment,@version,@target,@issuer,@tenant,@principal,@application,@request,@digest)");
        Arguments(command, key);
        command.Parameters.AddWithValue("application", applicationId);
        command.Parameters.AddWithValue("digest", requestDigest.Value);
        MachineInvocationClaim result;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (!await rows.ReadAsync(token)) throw Invalid();
            var status = rows.GetInt32(0);
            result = status switch
            {
                0 => new(MachineInvocationClaimStatus.Claimed, rows.GetInt32(1), rows.GetGuid(2), new PrincipalId(rows.GetGuid(3))),
                1 => new(MachineInvocationClaimStatus.Recorded, rows.GetInt32(1), Outcome: Outcome(rows.GetString(5)),
                    ReleaseId: rows.IsDBNull(6) ? null : rows.GetGuid(6), RetainedResponse: rows.IsDBNull(7) ? null : rows.GetFieldValue<byte[]>(7)),
                2 => new(MachineInvocationClaimStatus.Conflict),
                3 => new(MachineInvocationClaimStatus.InProgress, rows.GetInt32(1)),
                4 => new(MachineInvocationClaimStatus.Refused),
                5 => new(MachineInvocationClaimStatus.NoRelease),
                _ => throw Invalid()
            };
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        if (result.Status == MachineInvocationClaimStatus.Claimed && (result.Attempt is < 1 or > MachineInvocationPolicy.MaximumAttempts || result.ClaimId == Guid.Empty))
            throw Invalid();
        // A recorded outcome is final; only a completed one can have a retained response.
        if (result.Status == MachineInvocationClaimStatus.Recorded && (result.Outcome is null or MachineInvocationOutcome.Unavailable or
                MachineInvocationOutcome.Busy or MachineInvocationOutcome.ReleaseChanged || result.RetainedResponse is { } retained &&
                (result.Outcome != MachineInvocationOutcome.Completed || retained.Length > MachineInvocationPolicy.MaximumRetainedBytes)))
            throw Invalid();
        if (inputBytes is { } size && result.Status is MachineInvocationClaimStatus.Claimed or MachineInvocationClaimStatus.Recorded &&
            await ReleaseOperationsRoleGuard.InstalledAsync(tx.Connection, tx.Transaction, token))
        {
            await using var metadata = tx.CreateCommand("SELECT braid.record_invocation_metadata(@org,@target,@issuer,@tenant,@principal,@request,@application,@digest,@bytes,@replayed)");
            Arguments(metadata, key);
            metadata.Parameters.AddWithValue("application", applicationId);
            metadata.Parameters.AddWithValue("digest", requestDigest.Value);
            metadata.Parameters.AddWithValue("bytes", size);
            metadata.Parameters.AddWithValue("replayed", result.Status == MachineInvocationClaimStatus.Recorded);
            if (await metadata.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<MachineInvocationFinishStatus> FinishAsync(MachineInvocationKey key, Guid claimId, Guid applicationId, MachineInvocationOutcome outcome,
        MachineInvocationProvenance? provenance, string? clock, byte[]? response, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (claimId == Guid.Empty || applicationId == Guid.Empty || !Enum.IsDefined(outcome) ||
            outcome is MachineInvocationOutcome.Interrupted or MachineInvocationOutcome.AttemptsExhausted ||
            (outcome == MachineInvocationOutcome.Completed) != (response is not null) ||
            outcome == MachineInvocationOutcome.Completed && (provenance is null || clock is null) ||
            response?.Length is < 1 or > MachineInvocationPolicy.MaximumRetainedBytes)
            throw new ArgumentException("Invalid machine invocation outcome.");
        await using var tx = await BeginAsync(key.Target.Mapping.OrganizationId, token);
        await using var command = tx.CreateCommand("SELECT braid.finish_machine_invocation(@org,@project,@mapping,@environment,@version,@target,@issuer,@tenant,@principal,@request,@claim,@application,@finding,@release,@qualification,@build,@intent,@artifact,@revision,@image,@clock,@body)");
        Arguments(command, key);
        command.Parameters.AddWithValue("claim", claimId);
        command.Parameters.AddWithValue("application", applicationId);
        command.Parameters.AddWithValue("finding", Name(outcome));
        command.Parameters.AddWithValue("release", NpgsqlDbType.Uuid, (object?)provenance?.ReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("qualification", NpgsqlDbType.Uuid, (object?)provenance?.QualificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("build", NpgsqlDbType.Uuid, (object?)provenance?.BuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Text, (object?)provenance?.BuildIntentDigest.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("artifact", NpgsqlDbType.Text, (object?)provenance?.ArtifactDigest.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Uuid, (object?)provenance?.RevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("image", NpgsqlDbType.Text, (object?)provenance?.RuntimeImage ?? DBNull.Value);
        command.Parameters.AddWithValue("clock", NpgsqlDbType.Text, (object?)clock ?? DBNull.Value);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)response ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token) switch
        {
            0 => MachineInvocationFinishStatus.Recorded,
            1 => MachineInvocationFinishStatus.Revoked,
            2 => MachineInvocationFinishStatus.Superseded,
            _ => throw Invalid()
        };
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<int> PurgeExpiredAsync(ReleaseTargetReference target, int maximum, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 1000);
        await using var tx = await BeginAsync(target.Mapping.OrganizationId, token);
        await using var command = tx.CreateCommand("SELECT braid.purge_machine_invocation_results(@org,@target,@maximum)");
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId); command.Parameters.AddWithValue("target", target.TargetId);
        command.Parameters.AddWithValue("maximum", maximum);
        if (await command.ExecuteScalarAsync(token) is not int removed || removed < 0 || removed > maximum) throw Invalid();
        await tx.CommitAsync(token);
        return removed;
    }

    private async Task<PostgresTransaction> BeginAsync(Guid org, CancellationToken token)
    {
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            await MachineInvocationRoleGuard.ValidateAsync(connection, transaction, grants: false, token);
            var result = new PostgresTransaction(connection, transaction, org);
            await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static void Arguments(NpgsqlCommand command, MachineInvocationKey key)
    {
        PostgresInvocationGrantStore.Scope(command, key.Target);
        command.Parameters.AddWithValue("issuer", key.Caller.Provider);
        command.Parameters.AddWithValue("tenant", key.Caller.TenantId);
        command.Parameters.AddWithValue("principal", key.Caller.ObjectId);
        command.Parameters.AddWithValue("request", key.RequestId);
    }

    internal static string Name(MachineInvocationOutcome outcome) => outcome switch
    {
        MachineInvocationOutcome.Completed => "completed",
        MachineInvocationOutcome.Refused => "refused",
        MachineInvocationOutcome.InvalidInput => "invalidInput",
        MachineInvocationOutcome.InvalidParameter => "invalidParameter",
        MachineInvocationOutcome.ExecutionFailed => "executionFailed",
        MachineInvocationOutcome.ResourceLimit => "resourceLimit",
        MachineInvocationOutcome.IntegrityFailure => "integrityFailure",
        MachineInvocationOutcome.Unsupported => "unsupported",
        MachineInvocationOutcome.Unavailable => "unavailable",
        MachineInvocationOutcome.Busy => "busy",
        MachineInvocationOutcome.ReleaseChanged => "releaseChanged",
        MachineInvocationOutcome.Interrupted => "interrupted",
        MachineInvocationOutcome.AttemptsExhausted => "attemptsExhausted",
        _ => throw Invalid()
    };

    private static MachineInvocationOutcome Outcome(string value)
    {
        foreach (var outcome in Enum.GetValues<MachineInvocationOutcome>())
            if (Name(outcome) == value) return outcome;
        throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Machine invocation persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
