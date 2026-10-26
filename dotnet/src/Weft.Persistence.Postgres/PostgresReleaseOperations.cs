using System.Text.Json;
using NpgsqlTypes;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresLocalReleaseStore
{
    public async Task<ReleaseTransitionOutcome?> RecoverAsync(ActorContext actor, ReleaseTargetReference target,
        ReleaseOperation operation, CancellationToken token = default)
    {
        if (!writer) throw Invalid();
        await using var tx = await BeginAsync(actor, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT weft.read_release_operation(@org,@project,@mapping,@environment,@version,@target,@operation,@action,@expected,@qualification,@source)");
        Scope(command, target);
        command.Parameters.AddWithValue("operation", operation.OperationId);
        command.Parameters.AddWithValue("action", operation.Action);
        command.Parameters.AddWithValue("expected", operation.ExpectedVersion);
        command.Parameters.AddWithValue("qualification", NpgsqlDbType.Uuid, (object?)operation.QualificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Uuid, (object?)operation.SourceReleaseId ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token);
        await tx.CommitAsync(token);
        if (result is null or DBNull) return null;
        if (result is not int number || number is < 0 or > 5) throw Invalid();
        return (ReleaseTransitionOutcome)number;
    }
    public async Task<JsonElement?> ReadAsync(ActorContext actor, MappingScope mapping, Guid? target,
        ReleaseOperationsQuery query, CancellationToken token = default)
    {
        if (query.Section is not ("targets" or "releases" or "history" or "actions" or "callers" or "invocations" or "summary") ||
            query.Offset is < 0 or > 1000000 || query.Limit is < 1 or > 100 || target == Guid.Empty ||
            query.Section != "targets" && target is null)
            throw new ArgumentException("Invalid operations query.");
        var until = query.Until ?? DateTimeOffset.UtcNow;
        var since = query.Since ?? until.AddDays(-1);
        if (since >= until) throw new ArgumentException("Invalid operations window.");
        await using var tx = await BeginAsync(actor, mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT weft.read_release_operations(@org,@project,@mapping,@target,@section,@offset,@limit,@since,@until,@caller,@outcome)");
        command.Parameters.AddWithValue("org", mapping.OrganizationId);
        command.Parameters.AddWithValue("project", mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", mapping.MappingId);
        command.Parameters.AddWithValue("target", NpgsqlDbType.Uuid, (object?)target ?? DBNull.Value);
        command.Parameters.AddWithValue("section", query.Section);
        command.Parameters.AddWithValue("offset", query.Offset);
        command.Parameters.AddWithValue("limit", query.Limit);
        command.Parameters.AddWithValue("since", since.ToUniversalTime());
        command.Parameters.AddWithValue("until", until.ToUniversalTime());
        command.Parameters.AddWithValue("caller", NpgsqlDbType.Uuid, (object?)query.Caller ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome", NpgsqlDbType.Text, (object?)query.Outcome ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token);
        await tx.CommitAsync(token);
        if (result is null or DBNull) return null;
        if (result is not string json || json.Length > 1024 * 1024) throw Invalid();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public async Task<ReleaseTransitionOutcome> OperateAsync(ActorContext actor, ReleaseTargetReference target,
        ReleaseOperation operation, ReleaseIntent? intent, CancellationToken token = default)
    {
        if (!writer || intent is not null && (intent.Actor != actor.PrincipalId || intent.Target != target || intent.ReleaseId != operation.OperationId)) throw Invalid();
        await using var tx = await BeginAsync(actor, target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT weft.operate_release_target(@org,@project,@mapping,@environment,@version,@target,@operation,@action,@expected,@qualification,@source,@intent)");
        Scope(command, target);
        command.Parameters.AddWithValue("operation", operation.OperationId);
        command.Parameters.AddWithValue("action", operation.Action);
        command.Parameters.AddWithValue("expected", operation.ExpectedVersion);
        command.Parameters.AddWithValue("qualification", NpgsqlDbType.Uuid, (object?)operation.QualificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("source", NpgsqlDbType.Uuid, (object?)operation.SourceReleaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("intent", NpgsqlDbType.Bytea, (object?)intent?.CopyCanonicalBytes() ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not int result || result is < 0 or > 5) throw Invalid();
        await tx.CommitAsync(token);
        return (ReleaseTransitionOutcome)result;
    }
}
