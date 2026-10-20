using System.Data;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Dedicated resetting pool for per-case durable runs. It never grants sample or production-data access.</summary>
public sealed class PostgresRegressionRunStore : IRegressionRunStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly bool writer;

    public PostgresRegressionRunStore(string connectionString, bool writer)
    {
        source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
        this.writer = writer;
    }

    public async Task<RegressionRunSubmitOutcome> SubmitAsync(RegressionRunSubmission submission, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.Actor);
        ArgumentNullException.ThrowIfNull(submission.Case);
        RequireId(submission.RunId);
        var reference = submission.Case;
        await using var tx = await BeginAsync(reference.Revision.OrganizationId, submission.Actor, write: true, token);
        await using var command = tx.CreateCommand("SELECT weft.submit_regression_run(@org,@project,@mapping,@id,@case,@version,@creator,@revision,@native,@digest,@bytes)");
        Scope(command, reference.Revision.OrganizationId, submission.RunId);
        Add(command, "project", reference.Revision.ProjectId);
        Add(command, "mapping", reference.Revision.MappingId);
        Add(command, "case", reference.CaseId);
        command.Parameters.AddWithValue("version", reference.Version);
        Add(command, "creator", reference.Owner.Value);
        Add(command, "revision", reference.Revision.RevisionId);
        command.Parameters.AddWithValue("native", reference.NativeDigest.Value);
        command.Parameters.AddWithValue("digest", reference.ContentDigest.Value);
        command.Parameters.AddWithValue("bytes", reference.ByteCount);
        var outcome = await command.ExecuteScalarAsync(token);
        if (outcome is not int value || value is < 0 or > 4) throw Integrity();
        await tx.CommitAsync(token);
        return (RegressionRunSubmitOutcome)value;
    }

    public async Task<RegressionRunClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default)
    {
        RequireId(worker);
        await using var tx = await BeginAsync(organizationId, null, write: true, token);
        await using var command = tx.CreateCommand("SELECT * FROM weft.claim_regression_run(@org,@id,@worker)");
        Scope(command, organizationId, runId);
        Add(command, "worker", worker);
        RegressionRunClaimResult result;
        await using (var row = await command.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) throw Integrity();
            var outcome = row.GetInt32(0);
            if (outcome is < 0 or > 3) throw Integrity();
            RegressionRunClaim? claim = null;
            if (outcome == 0)
            {
                var reference = new RegressionCaseReference(row.GetGuid(6), row.GetInt64(7), new(row.GetGuid(8)),
                    new(organizationId, row.GetGuid(4), row.GetGuid(5), row.GetGuid(9)), Digest(row.GetString(10)), Digest(row.GetString(11)), row.GetInt32(12));
                claim = new(runId, new(row.GetGuid(13)), reference, row.GetInt32(1), worker, row.GetGuid(2), new(row.GetDateTime(3)));
                RequireClaim(claim);
            }
            result = new((RegressionRunClaimOutcome)outcome, claim);
            if (await row.ReadAsync(token)) throw Integrity();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public Task<bool> ObserveAsync(RegressionRunClaim claim, CancellationToken token = default) =>
        ClaimOperationAsync(claim, "observe_regression_run", null, token);

    public Task<bool> ReleaseAsync(RegressionRunClaim claim, CancellationToken token = default) =>
        ClaimOperationAsync(claim, "release_regression_run", null, token);

    public Task<bool> CompleteAsync(RegressionRunClaim claim, RegressionCaseExecution completion, CancellationToken token = default)
    {
        RequireClaim(claim);
        ArgumentNullException.ThrowIfNull(completion);
        var bytes = new RegressionRunResultCodec().Encode(claim.RunId, claim.Case, completion, token);
        return ClaimOperationAsync(claim, "finish_regression_run", bytes, token);
    }

    public async Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(organizationId, actor, write: true, token);
        await using var command = tx.CreateCommand("SELECT weft.cancel_regression_run(@org,@id)");
        Scope(command, organizationId, runId);
        var result = await command.ExecuteScalarAsync(token);
        if (result is not bool value) throw Integrity();
        await tx.CommitAsync(token);
        return value;
    }

    public async Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(organizationId, actor, write: false, token);
        await using var command = tx.CreateCommand("""
            SELECT project_id,mapping_id,submitter_id,case_id,version,creator_id,revision_id,native_digest,
                case_digest,case_bytes,state,attempt,created_at,result_digest,result_bytes,result_body
            FROM weft.read_regression_run(@org,@id)
            """);
        Scope(command, organizationId, runId);
        RegressionRunSnapshot? result = null;
        await using (var row = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token))
        {
            if (await row.ReadAsync(token))
            {
                var project = row.GetGuid(0);
                var mapping = row.GetGuid(1);
                var submitter = new PrincipalId(row.GetGuid(2));
                if (submitter != actor.PrincipalId) throw Integrity();
                var caseId = row.GetGuid(3);
                var version = row.GetInt64(4);
                var owner = new PrincipalId(row.GetGuid(5));
                var scope = new RevisionScope(organizationId, project, mapping, row.GetGuid(6));
                var reference = new RegressionCaseReference(caseId, version, owner, scope,
                    Digest(row.GetString(7)), Digest(row.GetString(8)), row.GetInt32(9));
                var state = row.GetString(10) switch
                {
                    "queued" => RegressionRunState.Queued,
                    "running" => RegressionRunState.Running,
                    "completed" => RegressionRunState.Completed,
                    "canceled" => RegressionRunState.Canceled,
                    "exhausted" => RegressionRunState.AttemptsExhausted,
                    "revoked" => RegressionRunState.AuthorityRevoked,
                    _ => throw Integrity()
                };
                var attempt = row.GetInt32(11);
                var created = new DateTimeOffset(row.GetDateTime(12));
                var digest = row.IsDBNull(13) ? null : Digest(row.GetString(13));
                byte[]? bytes = null;
                if (!row.IsDBNull(14))
                {
                    var count = row.GetInt32(14);
                    if (count is < 1 or > RegressionRunResultCodec.MaximumBytes) throw Integrity();
                    bytes = new byte[count];
                    await using var stream = row.GetStream(15);
                    await stream.ReadExactlyAsync(bytes, token);
                    if (await stream.ReadAsync(new byte[1], token) != 0) throw Integrity();
                }
                result = new(runId, submitter, reference, state, attempt, created, bytes, digest);
                if (await row.ReadAsync(token)) throw Integrity();
            }
        }
        if (result is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM weft.read_regression_run(@org,@id))";
            if (await command.ExecuteScalarAsync(token) is not true) result = null;
        }
        await tx.CommitAsync(token);
        return result;
    }

    private async Task<bool> ClaimOperationAsync(RegressionRunClaim claim, string operation, byte[]? bytes, CancellationToken token)
    {
        RequireClaim(claim);
        await using var tx = await BeginAsync(claim.Case.Revision.OrganizationId, null, write: true, token);
        var arguments = bytes is null ? "@org,@id,@worker,@token,@generation" : "@org,@id,@worker,@token,@generation,@body,@sha,@length";
        await using var command = tx.CreateCommand($"SELECT weft.{operation}({arguments})");
        Scope(command, claim.Case.Revision.OrganizationId, claim.RunId);
        Add(command, "worker", claim.Worker);
        Add(command, "token", claim.Token);
        command.Parameters.AddWithValue("generation", claim.Attempt);
        if (bytes is not null)
        {
            command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, bytes);
            command.Parameters.AddWithValue("sha", Sha256Digest.FromBytes(bytes).Value);
            command.Parameters.AddWithValue("length", bytes.Length);
        }
        var result = await command.ExecuteScalarAsync(token);
        if (result is not bool value) throw Integrity();
        await tx.CommitAsync(token);
        return value;
    }

    private async Task<PostgresTransaction> BeginAsync(Guid organization, ActorContext? actor, bool write, CancellationToken token)
    {
        RequireId(organization);
        token.ThrowIfCancellationRequested();
        if (write && !writer) throw new InvalidOperationException("Regression run writes require the writer profile.");
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write)
            {
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await readOnly.ExecuteNonQueryAsync(token);
            }
            await RegressionRunRoleGuard.ValidateAsync(connection, transaction, writer, token);
            await using (var settings = new NpgsqlCommand("""
                SELECT set_config('statement_timeout','15000',true),set_config('lock_timeout','5000',true),
                    set_config('weft.run_principal_id',@actor,true)
                """, connection, transaction))
            {
                settings.Parameters.AddWithValue("actor", actor?.PrincipalId.Value.ToString("D") ?? "");
                await settings.ExecuteNonQueryAsync(token);
            }
            var result = new PostgresTransaction(connection, transaction, organization);
            await result.BindOrganizationAsync(organization, token);
            return result;
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void Scope(NpgsqlCommand command, Guid organization, Guid id)
    {
        RequireId(organization);
        RequireId(id);
        Add(command, "org", organization);
        Add(command, "id", id);
    }
    private static void Add(NpgsqlCommand command, string name, Guid value) => command.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);
    private static void RequireId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A run identity is required."); }
    private static void RequireClaim(RegressionRunClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(claim.Case);
        ArgumentNullException.ThrowIfNull(claim.Submitter);
        RequireId(claim.RunId); RequireId(claim.Worker); RequireId(claim.Token);
        if (claim.Attempt is < 1 or > 3) throw new ArgumentException("Run attempt refused.");
    }
    private static Sha256Digest Digest(string value)
    {
        var result = Sha256Digest.Parse(value);
        return result.Value == value ? result : throw Integrity();
    }
    private static InvalidOperationException Integrity() => new("Regression run integrity check failed.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
