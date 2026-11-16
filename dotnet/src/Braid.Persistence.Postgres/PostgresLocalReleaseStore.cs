using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Atomic local target-pointer changes and immutable audit. This adapter never contacts an external deployment service.</summary>
public sealed partial class PostgresLocalReleaseStore(string connectionString, bool writer) : ILocalReleaseStore, IReleaseOperationsStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReleaseWriteOutcome> RegisterAsync(ActorContext actor, LocalReleaseTarget target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return (ReleaseWriteOutcome)await WriteAsync(actor, target.Reference, null, "register", target.CopyCanonicalBytes(), token);
    }
    public async Task<bool> DisableAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token = default) =>
        await WriteAsync(actor, target, null, "disable", null, token) is 0 or 1;

    public async Task<ReleaseWriteOutcome> RequestAsync(ActorContext actor, ReleaseIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Actor != actor.PrincipalId) throw Invalid();
        return (ReleaseWriteOutcome)await WriteAsync(actor, intent.Target, intent.ReleaseId, "request", intent.CopyCanonicalBytes(), token);
    }
    public async Task<ReleaseTransitionOutcome> ApplyAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default) =>
        (ReleaseTransitionOutcome)await WriteAsync(actor, target, releaseId, "apply", null, token);
    public async Task<ReleaseTransitionOutcome> CancelAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default) =>
        (ReleaseTransitionOutcome)await WriteAsync(actor, target, releaseId, "cancel", null, token);

    private async Task<int> WriteAsync(ActorContext actor, ReleaseTargetReference target, Guid? id, string operation, byte[]? body, CancellationToken token)
    {
        if (!writer || id == Guid.Empty) throw Invalid();
        await using var tx = await BeginAsync(actor, target.Mapping.OrganizationId, true, token);
        var sql = operation switch
        {
            "register" or "disable" => "SELECT braid.write_local_target(@op,@org,@project,@mapping,@environment,@version,@target,@body)",
            "request" => "SELECT braid.write_local_release(@org,@project,@mapping,@environment,@version,@target,@id,@body)",
            _ => "SELECT braid.transition_local_release(@op,@org,@project,@mapping,@environment,@version,@target,@id)"
        };
        await using var command = tx.CreateCommand(sql);
        Scope(command, target);
        if (operation != "request") command.Parameters.AddWithValue("op", operation);
        if (id is not null) command.Parameters.AddWithValue("id", id.Value);
        if (operation is "register" or "disable" or "request") command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)body ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not int result || result < 0 || result > (operation is "apply" or "cancel" ? 4 : 3)) throw Invalid();
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<LocalReleaseTargetSnapshot?> ReadTargetAsync(ActorContext actor, ReleaseTargetReference target, CancellationToken token = default)
    {
        await using var tx = await BeginAsync(actor, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_local_target(@org,@project,@mapping,@environment,@version,@target)");
        Scope(command, target);
        LocalReleaseTargetSnapshot? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var bytes = rows.GetFieldValue<byte[]>(0);
                if (bytes.Length is < 1 or > 16384) throw Invalid();
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                var decoded = new LocalReleaseTarget(Target(root), JsonSerializer.Deserialize<BuildRuntimeProfile>(root.GetProperty("profile").GetRawText(), Json) ?? throw Invalid(),
                    root.GetProperty("runtimeImage").GetString()!);
                var active = rows.IsDBNull(2) ? (Guid?)null : rows.GetGuid(2);
                if (decoded.Reference != target || active == Guid.Empty || !decoded.CopyCanonicalBytes().AsSpan().SequenceEqual(bytes)) throw Invalid();
                result = new(decoded, rows.GetBoolean(1), active);
                if (await rows.ReadAsync(token)) throw Invalid();
            }
        }
        if (result is not null)
        {
            // Re-read the complete pointer after metadata decoding, not only row existence.
            command.CommandText = "SELECT enabled,active_release_id FROM braid.read_local_target(@org,@project,@mapping,@environment,@version,@target)";
            await using var rows = await command.ExecuteReaderAsync(token);
            if (!await rows.ReadAsync(token)) result = null;
            else result = result with { Enabled = rows.GetBoolean(0), ActiveReleaseId = rows.IsDBNull(1) ? null : rows.GetGuid(1) };
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<LocalReleaseSnapshot?> ReadAsync(ActorContext actor, ReleaseTargetReference target, Guid releaseId, CancellationToken token = default)
    {
        if (releaseId == Guid.Empty) throw new ArgumentException("A release identifier is required.", nameof(releaseId));
        await using var tx = await BeginAsync(actor, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_local_release(@org,@project,@mapping,@environment,@version,@target,@id)");
        Scope(command, target); command.Parameters.AddWithValue("id", releaseId);
        byte[]? body = null; string? phase = null; string? events = null;
        BuildQualification? qualified = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                body = rows.GetFieldValue<byte[]>(0); phase = rows.GetString(1); events = rows.GetString(2);
                if (body.Length is < 1 or > 16384 || events.Length > 8192) throw Invalid();
                using var metadata = JsonDocument.Parse(body);
                qualified = BuildQualificationRecord.Read(rows, 3, actor, target.Mapping, metadata.RootElement.GetProperty("qualificationId").GetGuid(), token);
                if (await rows.ReadAsync(token)) throw Invalid();
            }
        }
        LocalReleaseSnapshot? result = null;
        if (body is not null)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (qualified is null) throw Invalid();
            var intent = new ReleaseIntent(root.GetProperty("releaseId").GetGuid(), new(root.GetProperty("actorId").GetGuid()), Target(root), qualified,
                root.GetProperty("action").GetString() switch { "activate" => ReleaseAction.Activate, "rollback" => ReleaseAction.Rollback, _ => throw Invalid() },
                NullableId(root, "expectedActiveReleaseId"), NullableId(root, "rollbackOfReleaseId"));
            if (intent.ReleaseId != releaseId || intent.Actor != actor.PrincipalId || intent.Target != target || !intent.CopyCanonicalBytes().AsSpan().SequenceEqual(body)) throw Invalid();
            using var audit = JsonDocument.Parse(events!);
            var entries = audit.RootElement.EnumerateArray().Select(e => new ReleaseAuditEntry(e.GetProperty("sequence").GetInt32(),
                e.GetProperty("kind").GetString() switch
                {
                    "requested" => ReleaseAuditKind.Requested,
                    "activated" => ReleaseAuditKind.Activated,
                    "canceled" => ReleaseAuditKind.Canceled,
                    "rejected" => ReleaseAuditKind.Rejected,
                    _ => throw Invalid()
                }, e.GetProperty("recordedAt").GetDateTimeOffset(),
                e.GetProperty("rejection").GetString() switch
                {
                    "none" => ReleaseRejection.None,
                    "staleActiveRelease" => ReleaseRejection.StaleActiveRelease,
                    "targetUnavailable" => ReleaseRejection.TargetUnavailable,
                    "approvalRevoked" => ReleaseRejection.ApprovalRevoked,
                    _ => throw Invalid()
                })).ToArray();
            result = new(intent, phase switch
            {
                "requested" => ReleasePhase.Requested,
                "active" => ReleasePhase.Active,
                "canceled" => ReleasePhase.Canceled,
                "failed" => ReleasePhase.Failed,
                _ => throw Invalid()
            }, entries);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.read_local_release(@org,@project,@mapping,@environment,@version,@target,@id))";
            if (await command.ExecuteScalarAsync(token) is not true) result = null;
        }
        await tx.CommitAsync(token);
        return result;
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext actor, Guid org, bool write, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await LocalReleaseRoleGuard.ValidateAsync(connection, transaction, writer, token);
            await using (var identity = new NpgsqlCommand("SELECT set_config('braid.build_principal_id',@actor,true)", connection, transaction))
            {
                identity.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync(token);
            }
            var result = new PostgresTransaction(connection, transaction, org);
            await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }
    private static ReleaseTargetReference Target(JsonElement root)
    {
        var org = root.GetProperty("organizationId").GetGuid(); var project = root.GetProperty("projectId").GetGuid();
        return new(new(org, project, root.GetProperty("mappingId").GetGuid()),
            new(new(org, project), root.GetProperty("environmentId").GetGuid(), root.GetProperty("environmentVersion").GetInt64()), root.GetProperty("targetId").GetGuid());
    }
    private static Guid? NullableId(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetGuid();
    private static void Scope(NpgsqlCommand command, ReleaseTargetReference target)
    {
        command.Parameters.AddWithValue("org", target.Mapping.OrganizationId); command.Parameters.AddWithValue("project", target.Mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", target.Mapping.MappingId); command.Parameters.AddWithValue("environment", target.Environment.EnvironmentId);
        command.Parameters.AddWithValue("version", target.Environment.Version); command.Parameters.AddWithValue("target", target.TargetId);
    }
    private static InvalidOperationException Invalid() => new("Local release persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
