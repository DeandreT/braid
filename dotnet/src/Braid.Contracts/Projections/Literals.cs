using System.Text.Json.Serialization;

namespace Braid.Contracts.Projections;

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
/// IEEE-754 binary64 bit pattern as exactly 16 lowercase hexadecimal characters,
/// retaining every bit, including negative zero, infinities and NaN payloads.
/// </summary>
public sealed record ExactNumberLiteralValue(ExactNumberFlavor Flavor, string Value) : LiteralValue;

public enum NullLiteralKind
{
    Absent,
    ExplicitNull,
    XmlNil
}

public sealed record NullLiteralValue(NullLiteralKind NullKind) : LiteralValue;
