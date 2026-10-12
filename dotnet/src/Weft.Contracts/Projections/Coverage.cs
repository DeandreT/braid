namespace Weft.Contracts.Projections;

public enum CoverageStatus
{
    Complete,
    Partial,
    Unavailable
}

public sealed record CoverageGap(
    string Code,
    string Message,
    NativeLocator NativeLocator,
    ProjectionNodeReference? ProjectionLocator = null);

public sealed record ProjectionCoverage(CoverageStatus Status, IReadOnlyList<CoverageGap> Gaps);
