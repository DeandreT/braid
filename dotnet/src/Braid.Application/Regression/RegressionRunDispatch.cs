namespace Braid.Application.Regression;

public sealed record RegressionRunReady(Guid OrganizationId, Guid RunId);

/// <summary>Machine-only discovery of committed eligible IDs. This is not an execution or sample-data grant.</summary>
public interface IRegressionRunQueue
{
    Task<IReadOnlyList<RegressionRunReady>> ListReadyAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IRegressionRunDispatcher
{
    Task<RegressionRunDispatchOutcome> ExecuteAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default);
}
