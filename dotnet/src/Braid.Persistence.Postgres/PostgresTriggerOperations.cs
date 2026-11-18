using System.Text.Json;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresLocalReleaseStore : ITriggerOperationsStore
{
    public async Task<JsonElement?> ReadTriggerOperationsAsync(ActorContext actor, MappingScope mapping, Guid target, string section, int offset, int limit,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (section is not ("triggers" or "runs" or "deliveries" or "quarantine") || target == Guid.Empty || offset is < 0 or > 1000000 || limit is < 1 or > 101)
            throw new ArgumentException("Invalid trigger operations query.");
        await using var tx = await BeginAsync(actor, mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT braid.read_trigger_operations(@org,@project,@mapping,@target,@section,@offset,@limit)");
        command.Parameters.AddWithValue("org", mapping.OrganizationId);
        command.Parameters.AddWithValue("project", mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", mapping.MappingId);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("section", section);
        command.Parameters.AddWithValue("offset", offset);
        command.Parameters.AddWithValue("limit", limit);
        var result = await command.ExecuteScalarAsync(token);
        await tx.CommitAsync(token);
        if (result is null or DBNull) return null;
        if (result is not string json || json.Length > 1024 * 1024) throw Invalid();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.Clone() : throw Invalid();
    }

    public async Task<TriggerResolutionOutcome> ResolveDeliveryAsync(ActorContext administrator, ReleaseTargetReference target, Guid deliveryId, Guid operationId,
        int expectedSends, TriggerDeliveryResolution resolution, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(administrator); ArgumentNullException.ThrowIfNull(target);
        if (!writer) throw Invalid();
        if (deliveryId == Guid.Empty || operationId == Guid.Empty || expectedSends is < 0 or > DeliveryRetryPolicy.MaximumTotalSends || !Enum.IsDefined(resolution))
            throw new ArgumentException("Invalid delivery resolution.");
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT braid.resolve_trigger_delivery(@org,@project,@mapping,@environment,@version,@target,@delivery,@operation,@expected,@resolution)");
        Scope(command, target);
        command.Parameters.AddWithValue("delivery", deliveryId);
        command.Parameters.AddWithValue("operation", operationId);
        command.Parameters.AddWithValue("expected", expectedSends);
        command.Parameters.AddWithValue("resolution", resolution.ToString().ToLowerInvariant());
        var result = await command.ExecuteScalarAsync(token) switch
        {
            0 => TriggerResolutionOutcome.Applied,
            2 => TriggerResolutionOutcome.Stale,
            4 => TriggerResolutionOutcome.Refused,
            5 => TriggerResolutionOutcome.IdempotencyConflict,
            _ => throw Invalid()
        };
        await tx.CommitAsync(token);
        return result;
    }
}
