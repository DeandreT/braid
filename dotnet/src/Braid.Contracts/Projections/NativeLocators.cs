using System.Text.Json.Serialization;

namespace Braid.Contracts.Projections;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DocumentNativeLocator), "document")]
[JsonDerivedType(typeof(JsonPointerNativeLocator), "jsonPointer")]
[JsonDerivedType(typeof(GraphNodeNativeLocator), "graphNode")]
[JsonDerivedType(typeof(TextSpanNativeLocator), "textSpan")]
[JsonDerivedType(typeof(OpaqueNativeLocator), "opaque")]
public abstract record NativeLocator(string DocumentId);

public sealed record DocumentNativeLocator(string DocumentId) : NativeLocator(DocumentId);

public sealed record JsonPointerNativeLocator(string DocumentId, string Pointer) : NativeLocator(DocumentId);

/// <summary>A function-local node retains its separate function identity.</summary>
public sealed record GraphNodeNativeLocator(
    string DocumentId,
    string NodeId,
    string? FunctionId = null) : NativeLocator(DocumentId);

/// <summary>Offsets and lengths are exact integers; the unit is stated by the locator.</summary>
public sealed record TextSpanNativeLocator(
    string DocumentId,
    string StartOffset,
    string Length,
    TextSpanUnit Unit = TextSpanUnit.Utf8Bytes) : NativeLocator(DocumentId);

public enum TextSpanUnit
{
    Utf8Bytes,
    Utf16CodeUnits,
    UnicodeScalars
}

/// <summary>A display-only backend locator. Its value does not grant resource access.</summary>
public sealed record OpaqueNativeLocator(string DocumentId, string Scheme, string Value) : NativeLocator(DocumentId);
