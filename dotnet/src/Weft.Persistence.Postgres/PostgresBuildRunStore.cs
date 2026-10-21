using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Separate actor and machine profiles; native/artifact bytes never cross the dispatch queue.</summary>
public sealed class PostgresBuildRunStore(string connectionString, BuildRunDatabaseProfile profile) : IBuildRunStore, IBuildRunQueue, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<BuildRunSubmitOutcome> SubmitAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return (BuildRunSubmitOutcome)await WriteAsync(actor, "submit", intent.Revision.OrganizationId, intent.BuildId, intent.CopyCanonicalBytes(), token);
    }
    public async Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default) =>
        await WriteAsync(actor, "cancel", organizationId, buildId, null, token) == 0;

    private async Task<int> WriteAsync(ActorContext actor, string operation, Guid org, Guid id, byte[]? body, CancellationToken token)
    {
        Require(BuildRunDatabaseProfile.Writer); Id(id);
        await using var tx = await BeginAsync(org, actor, true, token);
        await using var command = tx.CreateCommand("SELECT weft.write_build_run(@op,@org,@id,@body)");
        command.Parameters.AddWithValue("op", operation); Scope(command, org, id);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)body ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not int result || result is < 0 or > 4) throw Invalid();
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<BuildRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid buildId, CancellationToken token = default)
    {
        if (profile == BuildRunDatabaseProfile.Dispatcher) throw Invalid();
        Id(buildId);
        await using var tx = await BeginAsync(organizationId, actor, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM weft.read_build_run(@org,@id)");
        Scope(command, organizationId, buildId);
        BuildRunSnapshot? value = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var owner = new PrincipalId(rows.GetGuid(0));
                var intent = Intent(rows.GetFieldValue<byte[]>(1), organizationId, buildId);
                var state = rows.GetString(2) switch
                {
                    "queued" => BuildRunState.Queued,
                    "running" => BuildRunState.Running,
                    "succeeded" => BuildRunState.Succeeded,
                    "failed" => BuildRunState.Failed,
                    "canceled" => BuildRunState.Canceled,
                    "exhausted" => BuildRunState.Exhausted,
                    "revoked" => BuildRunState.Revoked,
                    "uncertain" => BuildRunState.Uncertain,
                    _ => throw Invalid()
                };
                var attempt = rows.GetInt32(3);
                var created = new DateTimeOffset(rows.GetDateTime(4));
                var outcome = rows.IsDBNull(5) ? (BuildExecutionOutcome?)null : (BuildExecutionOutcome)rows.GetInt32(5);
                BuildArtifactReference? artifact = null;
                if (rows.IsDBNull(6) != rows.IsDBNull(7)) throw Invalid();
                if (!rows.IsDBNull(6) && !rows.IsDBNull(7))
                    artifact = BuildArtifactReference.Restore(intent, Sha256Digest.Parse(rows.GetString(6)), rows.GetInt32(7));
                if (owner != actor.PrincipalId || attempt is < 0 or > BuildRunPolicy.MaximumAttempts ||
                    (state == BuildRunState.Running && attempt == 0) ||
                    (state == BuildRunState.Succeeded ? outcome is not (BuildExecutionOutcome.Created or BuildExecutionOutcome.Replayed) :
                        state == BuildRunState.Failed ? outcome is not (BuildExecutionOutcome.Refused or BuildExecutionOutcome.Unsupported or BuildExecutionOutcome.Conflict or BuildExecutionOutcome.IntegrityFailure) : outcome is not null) ||
                    (state is BuildRunState.Succeeded or BuildRunState.Uncertain) != (artifact is not null) || await rows.ReadAsync(token)) throw Invalid();
                value = new(owner, intent, state, attempt, created, outcome, artifact);
            }
        }
        if (value is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM weft.read_build_run(@org,@id))";
            if (await command.ExecuteScalarAsync(token) is not true) value = null;
        }
        await tx.CommitAsync(token);
        return value;
    }

    public async Task<BuildRunClaim?> ClaimAsync(Guid organizationId, Guid buildId, Guid workerId, CancellationToken token = default)
    {
        Require(BuildRunDatabaseProfile.Dispatcher); Id(buildId); Id(workerId);
        await using var tx = await BeginAsync(organizationId, null, true, token);
        await using var command = tx.CreateCommand("SELECT * FROM weft.claim_build_run(@org,@id,@worker)");
        Scope(command, organizationId, buildId); command.Parameters.AddWithValue("worker", workerId);
        BuildRunClaim? claim = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                claim = new(new(rows.GetGuid(0)), Intent(rows.GetFieldValue<byte[]>(1), organizationId, buildId), workerId,
                    rows.GetGuid(2), rows.GetInt32(3), new(rows.GetDateTime(4)));
                if (await rows.ReadAsync(token)) throw Invalid();
            }
        }
        await tx.CommitAsync(token);
        return claim;
    }

    public Task<bool> RenewAsync(BuildRunClaim claim, CancellationToken token = default) => TransitionAsync(claim, "renew", null, null, null, token);
    public Task<bool> ReleaseAsync(BuildRunClaim claim, CancellationToken token = default) => TransitionAsync(claim, "release", null, null, null, token);
    public Task<bool> CompleteAsync(BuildRunClaim claim, BuildExecutionResult result, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Outcome) || result.Outcome is BuildExecutionOutcome.Unavailable or BuildExecutionOutcome.Busy) throw Invalid();
        var publication = result.Publication;
        if (result.Outcome is BuildExecutionOutcome.Created or BuildExecutionOutcome.Replayed)
        {
            if (publication is null || publication.Actor != claim.Actor || !publication.Artifact.MatchesIntent(claim.Intent) ||
                publication.Object.Id.Scope != claim.Intent.Revision || publication.Object.Id.ObjectId != claim.Intent.BuildId ||
                publication.Object.Id.Class != PrivateObjectClass.DerivedArtifact || publication.Object.Digest != publication.Artifact.ContentDigest ||
                publication.Object.ByteCount != publication.Artifact.ByteCount) throw Invalid();
        }
        else if (publication is not null) throw Invalid();
        return TransitionAsync(claim, "finish", (int)result.Outcome, publication?.Artifact.ContentDigest.Value,
            publication is null ? null : checked((int)publication.Artifact.ByteCount), token);
    }

    private async Task<bool> TransitionAsync(BuildRunClaim claim, string operation, int? outcome, string? digest, int? bytes, CancellationToken token)
    {
        Require(BuildRunDatabaseProfile.Dispatcher);
        ArgumentNullException.ThrowIfNull(claim);
        var org = claim.Intent.Revision.OrganizationId;
        await using var tx = await BeginAsync(org, null, true, token);
        await using var command = tx.CreateCommand("SELECT weft.transition_build_run(@op,@org,@id,@owner,@intent,@worker,@claim,@attempt,@outcome,@digest,@bytes)");
        command.Parameters.AddWithValue("op", operation); Scope(command, org, claim.Intent.BuildId);
        command.Parameters.AddWithValue("owner", claim.Actor.Value); command.Parameters.AddWithValue("intent", claim.Intent.Fingerprint.Value);
        command.Parameters.AddWithValue("worker", claim.WorkerId); command.Parameters.AddWithValue("claim", claim.Token);
        command.Parameters.AddWithValue("attempt", claim.Attempt);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Integer, (object?)outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Text, (object?)digest ?? DBNull.Value);
        command.Parameters.AddWithValue("bytes", NpgsqlDbType.Integer, (object?)bytes ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not bool value) throw Invalid();
        await tx.CommitAsync(token);
        return value;
    }

    public async Task<IReadOnlyList<BuildRunKey>> DiscoverAsync(int maximum, CancellationToken token = default)
    {
        Require(BuildRunDatabaseProfile.Dispatcher);
        if (maximum is < 1 or > BuildRunPolicy.MaximumDiscovery) throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var tx = await BeginAsync(Guid.Empty, null, false, token);
        await using var command = new NpgsqlCommand("SELECT * FROM weft.discover_build_runs(@maximum)", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("maximum", maximum);
        var values = new List<BuildRunKey>();
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                if (values.Count >= maximum) throw Invalid();
                var org = rows.GetGuid(0); var id = rows.GetGuid(1); Id(org); Id(id);
                values.Add(new(org, id));
            }
        if (values.Distinct().Count() != values.Count) throw Invalid();
        await tx.CommitAsync(token);
        return values.ToArray();
    }

    private async Task<PostgresTransaction> BeginAsync(Guid org, ActorContext? actor, bool write, CancellationToken token)
    {
        if (profile == BuildRunDatabaseProfile.Dispatcher ? actor is not null : actor is null || org == Guid.Empty) throw Invalid();
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await BuildRunRoleGuard.ValidateAsync(connection, transaction, profile, token);
            if (actor is not null)
            {
                await using var identity = new NpgsqlCommand("SELECT set_config('weft.build_principal_id',@actor,true)", connection, transaction);
                identity.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync(token);
            }
            var result = new PostgresTransaction(connection, transaction, org);
            if (org != Guid.Empty) await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }
    private static BuildIntent Intent(byte[] body, Guid org, Guid id)
    {
        if (body.Length is < 1 or > 1048576) throw Invalid();
        var value = BuildIntentCodec.Decode(body);
        if (value.Revision.OrganizationId != org || value.BuildId != id) throw Invalid();
        return value;
    }
    private static void Scope(NpgsqlCommand command, Guid org, Guid id) { command.Parameters.AddWithValue("org", org); command.Parameters.AddWithValue("id", id); }
    private void Require(BuildRunDatabaseProfile value) { if (profile != value) throw Invalid(); }
    private static void Id(Guid value) { if (value == Guid.Empty) throw Invalid(); }
    private static InvalidOperationException Invalid() => new("Build run persistence contract refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
