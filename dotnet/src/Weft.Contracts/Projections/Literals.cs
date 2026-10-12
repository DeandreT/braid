using System.Text.Json.Serialization;

namespace Weft.Contracts.Projections;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StringLiteralValue), "string")]
[JsonDerivedType(typeof(BooleanLiteralValue), "boolean")]
[JsonDerivedType(typeof(ExactNumberLiteralValue), "exactNumber")]
[JsonDerivedType(typeof(NullLiteralValue), "null")]
public abstract record LiteralValue;

public sealed record StringLiteralValue(string Value) : LiteralValue;

public sealed record BooleanLiteralValue(bool Value) : LiteralValue;

public enum ExactNumberFlavor
{
    Integer,
    Decimal,
    BinaryFloat
}

/// <summary>
/// Integer and decimal values use exact lexical text. Binary floats use their
/// hexadecimal IEEE-754 bit pattern, retaining negative zero and payload bits.
/// </summary>
public sealed record ExactNumberLiteralValue(ExactNumberFlavor Flavor, string Value) : LiteralValue;

public enum NullLiteralKind
{
    Absent,
    ExplicitNull,
    XmlNil
}

public sealed record NullLiteralValue(NullLiteralKind NullKind) : LiteralValue;
