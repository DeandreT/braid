using Braid.Domain;

namespace Braid.Application.Regression;

/// <summary>The original immutable case is evidence; the evaluation target is a separate identity.</summary>
public sealed record RegressionEvaluationTarget(RevisionScope Revision, Sha256Digest NativeDigest);

public interface IRegressionEvaluationExecutor
{
    Task<RegressionCaseExecution> EvaluateAsync(ActorContext actor, RegressionCaseReference original,
        RegressionEvaluationTarget target, CancellationToken cancellationToken = default);
}
