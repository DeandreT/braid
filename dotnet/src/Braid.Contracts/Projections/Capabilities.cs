namespace Braid.Contracts.Projections;

public enum MappingOperation
{
    Inspect,
    Project,
    Validate,
    Preview,
    Build
}

public enum CapabilityStatus
{
    Supported,
    Unsupported,
    Conditional,
    NotQualified
}

public sealed record CapabilityCondition(string Code, string Description);

/// <summary>
/// Describes one operation/profile at this revision. It does not authorize a
/// request or replace execution admission and host resource policy.
/// </summary>
public sealed record OperationCapability(
    MappingOperation Operation,
    CapabilityStatus Status,
    string Profile,
    IReadOnlyList<string> BoundaryIds,
    IReadOnlyList<CapabilityCondition> Conditions);
