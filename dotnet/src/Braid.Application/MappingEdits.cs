using Braid.Contracts.Editing;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Application;

public static class MappingEditPolicy
{
    public const int MaximumOperations = 2000;
    public const int MaximumOperationsBytes = 1024 * 1024;
    public const int MaximumNativeBytes = 4 * 1024 * 1024;
    public const int MaximumReportBytes = 16 * 1024 * 1024;
    public const int MaximumRequestBytes = MaximumNativeBytes + MaximumOperationsBytes + 16396;
    public const int MaximumResponseBytes = MaximumNativeBytes + MaximumReportBytes + 8;
    public static TimeSpan Expiry { get; } = TimeSpan.FromDays(30);
}

/// <summary>Only the trusted worker can produce native checkpoint bytes.</summary>
public interface IMappingEditBackend
{
    Task<MappingEditResult> ReplayAsync(NativeArtifact immutableBase, MappingEditScript operations,
        CancellationToken cancellationToken = default);
}

public sealed record MappingEditCheckpoint(NativeArtifact Artifact, IngestionEvidenceDocument Evidence,
    ValidationReportV1 Validation, ProjectionReportV1 Projection);

public sealed record MappingEditResult(MappingEditCheckpoint? Checkpoint, MappingEditFailure? Failure);
public sealed record MappingEditFailure(string Code, string Message, bool MessageTruncated, string? OperationIndex,
    IngestionBackendStatus Status);
