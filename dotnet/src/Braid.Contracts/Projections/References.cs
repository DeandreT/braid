using System.Text.Json.Serialization;

namespace Braid.Contracts.Projections;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BoundaryNodeReference), "boundary")]
[JsonDerivedType(typeof(FieldNodeReference), "field")]
[JsonDerivedType(typeof(ParameterNodeReference), "parameter")]
[JsonDerivedType(typeof(RuleNodeReference), "rule")]
[JsonDerivedType(typeof(ControlNodeReference), "control")]
public abstract record ProjectionNodeReference;

public sealed record BoundaryNodeReference(string BoundaryId) : ProjectionNodeReference;

public sealed record FieldNodeReference(string FieldId) : ProjectionNodeReference;

public sealed record ParameterNodeReference(string ParameterId) : ProjectionNodeReference;

public sealed record RuleNodeReference(string RuleId) : ProjectionNodeReference;

public sealed record ControlNodeReference(string ControlId) : ProjectionNodeReference;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FieldDependency), "field")]
[JsonDerivedType(typeof(ParameterDependency), "parameter")]
[JsonDerivedType(typeof(ConstantDependency), "constant")]
[JsonDerivedType(typeof(NodeDependency), "node")]
public abstract record RuleDependency(DependencyRole Role);

public enum DependencyRole
{
    Value,
    Condition,
    Default
}

public sealed record FieldDependency(
    string FieldId,
    DependencyRole Role = DependencyRole.Value) : RuleDependency(Role);

public sealed record ParameterDependency(
    string ParameterId,
    DependencyRole Role = DependencyRole.Value) : RuleDependency(Role);

public sealed record ConstantDependency(
    LiteralValue Value,
    DependencyRole Role = DependencyRole.Value) : RuleDependency(Role);

public sealed record NodeDependency(
    ProjectionNodeReference Node,
    DependencyRole Role = DependencyRole.Value) : RuleDependency(Role);
