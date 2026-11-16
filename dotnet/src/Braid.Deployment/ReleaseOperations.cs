using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Deployment;

public sealed record ReleaseOperationsQuery(string Section, int Offset = 0, int Limit = 50,
    DateTimeOffset? Since = null, DateTimeOffset? Until = null, Guid? Caller = null, string? Outcome = null);
public sealed record ReleaseOperation(Guid OperationId, string Action, long ExpectedVersion,
    Guid? QualificationId = null, Guid? SourceReleaseId = null);

/// <summary>Metadata only. Implementations must never return qualification results, sample envelopes or invocation payloads.</summary>
public interface IReleaseOperationsStore
{
    Task<JsonElement?> ReadAsync(ActorContext actor, MappingScope mapping, Guid? target, ReleaseOperationsQuery query, CancellationToken token = default);
    Task<ReleaseTransitionOutcome> OperateAsync(ActorContext actor, ReleaseTargetReference target, ReleaseOperation operation,
        ReleaseIntent? intent, CancellationToken token = default);
    Task<ReleaseTransitionOutcome?> RecoverAsync(ActorContext actor, ReleaseTargetReference target, ReleaseOperation operation,
        CancellationToken token = default);
}

public sealed class ReleaseOperationsService(IReleaseOperationsStore operations, ILocalReleaseStore releases, IBuildQualificationStore qualifications)
{
    private readonly LocalReleaseService admission = new(releases, qualifications);
    public async Task<ReleaseTransitionOutcome> OperateAsync(ActorContext actor, ReleaseTargetReference target,
        ReleaseOperation operation, CancellationToken token = default)
    {
        if (operation.OperationId == Guid.Empty || operation.ExpectedVersion < 1 || operation.QualificationId == Guid.Empty || operation.SourceReleaseId == Guid.Empty ||
            operation.Action is not ("activate" or "rollback" or "enable" or "disable") ||
            operation.Action is "enable" or "disable" && (operation.QualificationId is not null || operation.SourceReleaseId is not null) ||
            operation.Action == "activate" && (operation.QualificationId is null || operation.SourceReleaseId is not null) ||
            operation.Action == "rollback" && (operation.QualificationId is null || operation.SourceReleaseId is null))
            throw new ArgumentException("Invalid release operation.");
        if (await operations.RecoverAsync(actor, target, operation, token) is { } recovered) return recovered;
        ReleaseIntent? intent = null;
        if (operation.QualificationId is { } id)
        {
            var current = await releases.ReadTargetAsync(actor, target, token);
            if (current is not null && current.Target.Reference == target && current.ActiveReleaseId != operation.OperationId &&
                operation.SourceReleaseId != operation.OperationId &&
                (operation.Action != "rollback" || current.ActiveReleaseId is not null && current.ActiveReleaseId != operation.SourceReleaseId))
            {
                intent = await admission.PrepareIntentAsync(actor, target, new(operation.OperationId, id, current.ActiveReleaseId,
                    operation.Action == "rollback" ? ReleaseAction.Rollback : ReleaseAction.Activate, operation.SourceReleaseId), token);
            }
        }
        // Persistence checks replay before intent admission: approval revocation does not
        // prevent authorized recovery, and retrying an old success never moves today's pointer.
        return await operations.OperateAsync(actor, target, operation, intent, token);
    }
}
