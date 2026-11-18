using System.Text.Json;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Lifecycle;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore : IResourceDeletionStore
{
    public async Task<ResourceDeletionResult> DeleteAsync(ActorContext actor, ResourceItemDeletion deletion, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(deletion);
        await using var tx = await BeginAsync(deletion.OrganizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.delete_resource_item(@org,@actor,@operation,@kind,@project,@mapping,@resource,@expected)::text");
        Scope(command, deletion.OrganizationId, actor.PrincipalId.Value, deletion.ProjectId);
        command.Parameters.AddWithValue("operation", deletion.OperationId);
        command.Parameters.AddWithValue("kind", KindText(deletion.Kind));
        command.Parameters.AddWithValue("mapping", NpgsqlDbType.Uuid, (object?)deletion.MappingId ?? DBNull.Value);
        command.Parameters.AddWithValue("resource", deletion.ResourceId);
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, (object?)deletion.ExpectedVersion ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not string text) throw InvalidDeletion();
        using var json = ParseLifecycle(text);
        var root = json.RootElement;
        var outcome = LifecycleText(root, "outcome") switch
        {
            "deleted" => ResourceDeletionOutcome.Deleted,
            "replayed" => ResourceDeletionOutcome.Replayed,
            "refused" => ResourceDeletionOutcome.NotAuthorizedOrNotFound,
            "preconditionFailed" => ResourceDeletionOutcome.PreconditionFailed,
            "activeWork" => ResourceDeletionOutcome.ActiveWork,
            "referencedResource" => ResourceDeletionOutcome.ReferencedResource,
            "conflict" => ResourceDeletionOutcome.Conflict,
            "invalidRequest" => ResourceDeletionOutcome.InvalidRequest,
            _ => throw InvalidDeletion()
        };
        if (outcome == ResourceDeletionOutcome.ReferencedResource)
        {
            var references = root.GetProperty("references");
            return new(outcome, References: new(Count(references, "regressionRuns"), Count(references, "automationEvaluations"),
                Count(references, "buildApprovals"), Count(references, "releaseTargets")));
        }
        if (outcome is not (ResourceDeletionOutcome.Deleted or ResourceDeletionOutcome.Replayed))
            return new(outcome); // A refusal never commits partially prepared facts.
        var receipt = DeletionReceipt(root, outcome);
        if (receipt.OperationId != deletion.OperationId || receipt.Kind != deletion.Kind || receipt.ResourceId != deletion.ResourceId ||
            deletion.ProjectId is { } project && (receipt.ProjectId != project || receipt.MappingId != deletion.MappingId)) throw InvalidDeletion();
        await tx.CommitAsync(token);
        return new(outcome, receipt);
    }

    async Task<ResourceDeletionReceipt?> IResourceDeletionStore.ReadOperationAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid deletion operation.", nameof(operationId));
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_resource_deletion(@org,@actor,@operation)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("operation", operationId);
        ResourceDeletionReceipt? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            if (LifecycleText(json.RootElement, "outcome") != "recorded") throw InvalidDeletion();
            result = DeletionReceipt(json.RootElement, ResourceDeletionOutcome.Replayed);
            if (result.OperationId != operationId) throw InvalidDeletion();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static ResourceDeletionReceipt DeletionReceipt(JsonElement root, ResourceDeletionOutcome outcome)
    {
        var kind = LifecycleText(root, "resourceKind") switch
        {
            "preview" => ResourceItemKind.Preview,
            "regressionCase" => ResourceItemKind.RegressionCase,
            "buildEnvironment" => ResourceItemKind.BuildEnvironment,
            "conversation" => ResourceItemKind.Conversation,
            _ => throw InvalidDeletion()
        };
        var project = OptionalLifecycleId(root, "projectId");
        var mapping = OptionalLifecycleId(root, "mappingId");
        if (mapping is not null && project is null || kind != ResourceItemKind.Conversation && project is null) throw InvalidDeletion();
        return new(outcome, LifecycleId(root, "operationId"), kind, LifecycleId(root, "resourceId"), project, mapping,
            root.GetProperty("recordedAtUtc").GetDateTimeOffset());
    }

    private static long? Count(JsonElement references, string name) =>
        references.TryGetProperty(name, out var value) ? LifecycleCount(LifecycleText(references, name)) : null;
    private static long LifecycleCount(string text) => text == "0" ? 0 : LifecycleDecimal(text);
    internal static string KindText(ResourceItemKind kind) => kind switch
    {
        ResourceItemKind.Preview => "preview",
        ResourceItemKind.RegressionCase => "regressionCase",
        ResourceItemKind.BuildEnvironment => "buildEnvironment",
        ResourceItemKind.Conversation => "conversation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static InvalidOperationException InvalidDeletion() => new("Resource deletion metadata is unavailable.");
}
