using System.Globalization;
using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore : IResourceLifecycleStore
{
    public async Task<ResourceLifecycleState?> ReadAsync(ActorContext actor, ResourceKey resource, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(resource);
        await using var tx = await BeginAsync(resource.Project.OrganizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_resource_lifecycle(@org,@actor,@project,@mapping)::text");
        Scope(command, resource.Project.OrganizationId, actor.PrincipalId.Value, resource.Project.ProjectId);
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)resource.MappingId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(token);
        ResourceLifecycleState? result = null;
        if (value is string text)
        {
            using var json = ParseLifecycle(text);
            var root = json.RootElement;
            var state = LifecycleText(root, "state");
            if (LifecycleText(root, "resourceKind") != (resource.IsMapping ? "mapping" : "project") || LifecycleId(root, "projectId") != resource.Project.ProjectId ||
                OptionalLifecycleId(root, "mappingId") != resource.MappingId || state is not ("active" or "archived") ||
                LifecycleText(root, "effectiveState") is not ("active" or "archived") ||
                (state == "archived" && LifecycleText(root, "effectiveState") != "archived")) throw InvalidLifecycle();
            result = new(resource, state == "archived", LifecycleText(root, "effectiveState") == "archived", LifecycleVersion(root, resource),
                root.GetProperty("archivedAtUtc").ValueKind == JsonValueKind.Null ? null : root.GetProperty("archivedAtUtc").GetDateTimeOffset(),
                root.GetProperty("canArchive").GetBoolean(), root.GetProperty("canRestore").GetBoolean());
            if ((result.ArchivedAtUtc is not null) != result.Archived || result.CanArchive && result.CanRestore) throw InvalidLifecycle();
            // A deleting or deleted identity never changes state again.
            if (await ReadPurgeAsync(tx, actor, resource, token) is { } purge)
                result = result with { CanArchive = false, CanRestore = false, DeletionJobId = purge.JobId };
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<(ResourceLifecycleOutcome Outcome, ResourceLifecycleReceipt? Receipt)> ChangeAsync(ActorContext actor,
        ResourceLifecycleChange change, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(change);
        var resource = change.Resource;
        // The change waits for in-flight writers and holds them off until it commits.
        await using var tx = await BeginAsync(resource.Project.OrganizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.change_resource_lifecycle(@org,@actor,@operation,@project,@mapping,@action,@projectVersion,@mappingVersion)::text");
        Scope(command, resource.Project.OrganizationId, actor.PrincipalId.Value, resource.Project.ProjectId);
        command.Parameters.AddWithValue("operation", change.OperationId);
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)resource.MappingId ?? DBNull.Value);
        command.Parameters.AddWithValue("action", change.Action == ResourceLifecycleAction.Archive ? "archive" : "restore");
        command.Parameters.AddWithValue("projectVersion", change.Expected.Project);
        command.Parameters.AddWithValue("mappingVersion", NpgsqlDbType.Bigint, (object?)change.Expected.Mapping ?? DBNull.Value);
        object? value;
        try { value = await command.ExecuteScalarAsync(token); }
        catch (Exception error) when (ResourcePurgeFence.Refused(error)) { return (ResourceLifecycleOutcome.Conflict, null); }
        if (value is not string text) throw InvalidLifecycle();
        using var json = ParseLifecycle(text);
        var outcome = LifecycleText(json.RootElement, "outcome") switch
        {
            "archived" => ResourceLifecycleOutcome.Archived,
            "restored" => ResourceLifecycleOutcome.Restored,
            "replayed" => ResourceLifecycleOutcome.Replayed,
            "refused" => ResourceLifecycleOutcome.NotAuthorizedOrNotFound,
            "preconditionFailed" => ResourceLifecycleOutcome.PreconditionFailed,
            "conflict" => ResourceLifecycleOutcome.Conflict,
            "invalidRequest" => ResourceLifecycleOutcome.InvalidRequest,
            _ => throw InvalidLifecycle()
        };
        if (outcome is not (ResourceLifecycleOutcome.Archived or ResourceLifecycleOutcome.Restored or ResourceLifecycleOutcome.Replayed))
            return (outcome, null); // A refusal never commits partially prepared facts.
        var receipt = LifecycleReceipt(json.RootElement, outcome, resource.Project.OrganizationId);
        if (receipt.OperationId != change.OperationId || receipt.Resource != resource || receipt.Action != change.Action ||
            outcome == ResourceLifecycleOutcome.Archived && change.Action != ResourceLifecycleAction.Archive ||
            outcome == ResourceLifecycleOutcome.Restored && change.Action != ResourceLifecycleAction.Restore) throw InvalidLifecycle();
        await tx.CommitAsync(token);
        return (outcome, receipt);
    }

    public async Task<ResourceLifecycleReceipt?> ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid lifecycle operation.", nameof(operationId));
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_lifecycle_operation(@org,@actor,@operation)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("operation", operationId);
        ResourceLifecycleReceipt? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            if (LifecycleText(json.RootElement, "outcome") != "recorded") throw InvalidLifecycle();
            result = LifecycleReceipt(json.RootElement, ResourceLifecycleOutcome.Replayed, organizationId);
            if (result.OperationId != operationId) throw InvalidLifecycle();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static ResourceLifecycleReceipt LifecycleReceipt(JsonElement root, ResourceLifecycleOutcome outcome, Guid organizationId)
    {
        var resource = new ResourceKey(new(organizationId, LifecycleId(root, "projectId")), OptionalLifecycleId(root, "mappingId"));
        if (LifecycleText(root, "resourceKind") != (resource.IsMapping ? "mapping" : "project")) throw InvalidLifecycle();
        var action = LifecycleText(root, "action") switch
        {
            "archive" => ResourceLifecycleAction.Archive,
            "restore" => ResourceLifecycleAction.Restore,
            _ => throw InvalidLifecycle()
        };
        if (LifecycleText(root, "state") != (action == ResourceLifecycleAction.Archive ? "archived" : "active")) throw InvalidLifecycle();
        return new(outcome, LifecycleId(root, "operationId"), resource, action, LifecycleVersion(root, resource), root.GetProperty("recordedAtUtc").GetDateTimeOffset());
    }

    private static ResourceLifecycleVersion LifecycleVersion(JsonElement root, ResourceKey resource)
    {
        var mapping = root.GetProperty("mappingVersion");
        if ((mapping.ValueKind == JsonValueKind.Null) == resource.IsMapping) throw InvalidLifecycle();
        return new(LifecycleDecimal(LifecycleText(root, "projectVersion")), resource.IsMapping ? LifecycleDecimal(LifecycleText(root, "mappingVersion")) : null);
    }

    private static JsonDocument ParseLifecycle(string text) =>
        text.Length <= 4096 ? JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 }) : throw InvalidLifecycle();
    private static string LifecycleText(JsonElement root, string name) =>
        root.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw InvalidLifecycle();
    private static Guid LifecycleId(JsonElement root, string name) =>
        Guid.TryParseExact(LifecycleText(root, name), "D", out var id) && id != Guid.Empty ? id : throw InvalidLifecycle();
    private static Guid? OptionalLifecycleId(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : LifecycleId(root, name);
    private static long LifecycleDecimal(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 &&
        text == value.ToString(CultureInfo.InvariantCulture) ? value : throw InvalidLifecycle();
    private static InvalidOperationException InvalidLifecycle() => new("Resource lifecycle metadata is unavailable.");
}
