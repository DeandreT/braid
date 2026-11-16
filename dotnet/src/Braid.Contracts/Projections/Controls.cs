using System.Text.Json.Serialization;

namespace Braid.Contracts.Projections;

public enum ControlKind
{
    Unknown,
    Iteration,
    Filter,
    Join,
    Grouping,
    Sorting,
    Window,
    Failure,
    Ordering,
    DynamicBoundary,
    Construction
}

public sealed record ProjectionControl(
    string Id,
    ControlKind Kind,
    string Summary,
    IReadOnlyList<ProjectionNodeReference> AppliesTo,
    IReadOnlyList<RuleDependency> Dependencies,
    IReadOnlyList<DetailNode> Details,
    NativeLocator NativeLocator);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GroupDetail), "group")]
[JsonDerivedType(typeof(ExpressionDetail), "expression")]
[JsonDerivedType(typeof(LiteralDetail), "literal")]
[JsonDerivedType(typeof(ReferenceDetail), "reference")]
[JsonDerivedType(typeof(UnsupportedDetail), "unsupported")]
public abstract record DetailNode;

public sealed record GroupDetail(string Label, IReadOnlyList<DetailNode> Children) : DetailNode;

public sealed record ExpressionDetail(string Operation, IReadOnlyList<DetailNode> Arguments) : DetailNode;

public sealed record LiteralDetail(LiteralValue Value) : DetailNode;

public sealed record ReferenceDetail(ProjectionNodeReference Reference) : DetailNode;

public sealed record UnsupportedDetail(
    string Construct,
    string Description,
    NativeLocator NativeLocator) : DetailNode;
