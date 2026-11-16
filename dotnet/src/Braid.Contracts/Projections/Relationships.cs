namespace Braid.Contracts.Projections;

public enum RelationshipKind
{
    ReadsFrom,
    WritesTo,
    GovernedBy,
    DependsOn,
    Produces,
    BoundaryMember
}

public sealed record ProjectionRelationship(
    string Id,
    RelationshipKind Kind,
    ProjectionNodeReference From,
    ProjectionNodeReference To);
