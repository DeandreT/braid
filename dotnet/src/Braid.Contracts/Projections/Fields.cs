namespace Braid.Contracts.Projections;

public enum BoundaryRole
{
    PrimaryInput,
    NamedInput,
    PrimaryOutput,
    NamedOutput
}

public enum BoundaryShape
{
    Unknown,
    Scalar,
    Object,
    Rows,
    DocumentSet
}

public sealed record ProjectionBoundary(
    string Id,
    string Name,
    BoundaryRole Role,
    string Format,
    BoundaryShape Shape,
    string? RootFieldId,
    NativeLocator NativeLocator);

public enum FieldDataTypeKind
{
    Unknown,
    Scalar,
    ScalarUnion,
    Object,
    Array
}

public enum ScalarDataType
{
    String,
    Integer,
    Decimal,
    BinaryFloat,
    Boolean
}

public sealed record FieldDataType(
    FieldDataTypeKind Kind,
    IReadOnlyList<ScalarDataType> ScalarTypes,
    string? NativeName = null);

public enum CardinalityKind
{
    Unknown,
    ExactlyOne,
    ZeroOrOne,
    ZeroOrMore,
    OneOrMore,
    Bounded
}

/// <summary>Bounded cardinalities retain exact decimal integer text for their limits.</summary>
public sealed record FieldCardinality(
    CardinalityKind Kind,
    string? Minimum = null,
    string? Maximum = null);

public enum FieldRequirement
{
    Unknown,
    Optional,
    Required
}

public enum FieldNullability
{
    Unknown,
    NonNullable,
    Nullable
}

public sealed record ProjectionField(
    string Id,
    string BoundaryId,
    IReadOnlyList<PathSegment> Path,
    string Name,
    FieldDataType DataType,
    FieldCardinality Cardinality,
    FieldRequirement Requirement,
    FieldNullability Nullability,
    NativeLocator NativeLocator);

public sealed record ProjectionParameter(
    string Id,
    string Name,
    FieldDataType DataType,
    FieldRequirement Requirement,
    LiteralValue? DefaultValue,
    NativeLocator NativeLocator);
