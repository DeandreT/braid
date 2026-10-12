namespace Weft.Contracts.Projections;

public sealed record ProjectionRule(
    string Id,
    string OutputFieldId,
    string Summary,
    IReadOnlyList<RuleDependency> Dependencies,
    IReadOnlyList<DetailNode> Details,
    NativeLocator NativeLocator);
