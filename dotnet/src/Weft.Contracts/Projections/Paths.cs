using System.Text.Json.Serialization;

namespace Weft.Contracts.Projections;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FieldPathSegment), "field")]
[JsonDerivedType(typeof(AttributePathSegment), "attribute")]
[JsonDerivedType(typeof(IndexPathSegment), "index")]
[JsonDerivedType(typeof(AnyItemPathSegment), "anyItem")]
public abstract record PathSegment;

public sealed record FieldPathSegment(string Name, string? NamespaceUri = null) : PathSegment;

public sealed record AttributePathSegment(string Name, string? NamespaceUri = null) : PathSegment;

public sealed record IndexPathSegment(string Index) : PathSegment;

public sealed record AnyItemPathSegment : PathSegment;
