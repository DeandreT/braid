using System.Text.Json;
using Braid.Application;
using Braid.Application.Lifecycle;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresAdministrationStore : ITargetRetirementStore
{
    public async Task<(TargetRetirementOutcome Outcome, TargetRetirementReceipt? Receipt)> RetireAsync(ActorContext actor,
        TargetRetirement retirement, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(retirement);
        await using var tx = await BeginAsync(retirement.OrganizationId, token, exclusiveLifecycle: true);
        await using var command = tx.CreateCommand("SELECT braid.retire_release_target(@org,@actor,@operation,@project,@target,@expected,@release)::text");
        Scope(command, retirement.OrganizationId, actor.PrincipalId.Value, retirement.ProjectId);
        command.Parameters.AddWithValue("operation", retirement.OperationId);
        command.Parameters.AddWithValue("target", retirement.TargetId);
        command.Parameters.AddWithValue("expected", retirement.ExpectedVersion);
        command.Parameters.AddWithValue("release", retirement.ActiveReleaseId);
        if (await command.ExecuteScalarAsync(token) is not string text) throw InvalidRetirement();
        using var json = ParseLifecycle(text);
        var outcome = LifecycleText(json.RootElement, "outcome") switch
        {
            "retired" => TargetRetirementOutcome.Retired,
            "replayed" => TargetRetirementOutcome.Replayed,
            "refused" => TargetRetirementOutcome.NotAuthorizedOrNotFound,
            "preconditionFailed" => TargetRetirementOutcome.PreconditionFailed,
            "conflict" => TargetRetirementOutcome.Conflict,
            "invalidRequest" => TargetRetirementOutcome.InvalidRequest,
            _ => throw InvalidRetirement()
        };
        if (outcome is not (TargetRetirementOutcome.Retired or TargetRetirementOutcome.Replayed)) return (outcome, null); // A refusal commits nothing.
        var receipt = Retirement(json.RootElement, outcome);
        if (receipt.OperationId != retirement.OperationId || receipt.TargetId != retirement.TargetId || receipt.ProjectId != retirement.ProjectId) throw InvalidRetirement();
        await tx.CommitAsync(token);
        return (outcome, receipt);
    }

    public async Task<TargetRetirementReceipt?> ReadRetirementAsync(ActorContext actor, Guid organizationId, Guid operationId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid retirement operation.", nameof(operationId));
        await using var tx = await BeginAsync(organizationId, token, readOnly: true);
        await using var command = tx.CreateCommand("SELECT braid.read_target_retirement(@org,@actor,@operation)::text");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("actor", actor.PrincipalId.Value);
        command.Parameters.AddWithValue("operation", operationId);
        TargetRetirementReceipt? result = null;
        if (await command.ExecuteScalarAsync(token) is string text)
        {
            using var json = ParseLifecycle(text);
            if (LifecycleText(json.RootElement, "outcome") != "recorded") throw InvalidRetirement();
            result = Retirement(json.RootElement, TargetRetirementOutcome.Replayed);
            if (result.OperationId != operationId) throw InvalidRetirement();
        }
        await tx.CommitAsync(token);
        return result;
    }

    private static TargetRetirementReceipt Retirement(JsonElement root, TargetRetirementOutcome outcome) => new(outcome,
        LifecycleId(root, "operationId"), LifecycleId(root, "projectId"), LifecycleId(root, "mappingId"), LifecycleId(root, "targetId"),
        LifecycleId(root, "retiredReleaseId"), LifecycleDecimal(LifecycleText(root, "targetVersion")), root.GetProperty("recordedAtUtc").GetDateTimeOffset());

    private static InvalidOperationException InvalidRetirement() => new("Release target retirement metadata is unavailable.");
}
