using System.Buffers;
using System.Text;

namespace Braid.Application;

/// <summary>Bounds for the opt-in XML contract. These constants do not enable any execution profile.</summary>
public static class XmlDocumentPolicy
{
    public const string MetadataSchema = "braid.xml-boundary.v1";
    public const string SafetyProfile = "braid.xml-safe.v1";
    public const string MediaType = "application/xml";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumDepth = 64;
    public const int MaximumNodes = 1_000_000;
    public const int MaximumAttributes = 256;
    public const int MaximumInputs = 16;
    public const int MaximumOutputs = 64;
    public const int MaximumMetadataBytes = 32 * 1024;
    public const int MaximumSchemaHintPairs = 32;
    public const int MaximumSchemaHintTokenBytes = 4096;
    public const int MaximumNativeBytes = 4 * 1024 * 1024;

    public static bool Admits(XmlDocumentMetadata? metadata, XmlDocumentRole role, string mediaType) =>
        mediaType == MediaType && Admits(metadata, role);

    public static bool Admits(XmlDocumentMetadata? metadata, XmlDocumentRole role)
    {
        if (role is not (XmlDocumentRole.Input or XmlDocumentRole.Output) || metadata is null ||
            metadata.Schema != MetadataSchema || metadata.SafetyProfile != SafetyProfile) return false;
        if (metadata.SchemaHints is not { } hints) return true;
        if (role != XmlDocumentRole.Output || hints.Locations.Count > MaximumSchemaHintPairs ||
            hints.NoNamespaceLocation is null && hints.Locations.Count == 0 ||
            hints.NoNamespaceLocation is { } noNamespace && !ValidHintToken(noNamespace)) return false;
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in hints.Locations)
            if (pair is null || !ValidHintToken(pair.Namespace) || !ValidHintToken(pair.Location) ||
                !namespaces.Add(pair.Namespace)) return false;
        return true;
    }

    private static bool ValidHintToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumSchemaHintTokenBytes) return false;
        var bytes = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value is not (>= 0x21 and <= 0xd7ff or >= 0xe000 and <= 0xfffd or >= 0x10000 and <= 0x10ffff))
                return false;
            bytes += rune.Utf8SequenceLength;
            if (bytes > MaximumSchemaHintTokenBytes) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}

public enum XmlDocumentRole { Input, Output }

/// <summary>Fixed public reasons only; parser messages, payloads and resource identifiers are never retained.</summary>
public enum XmlDocumentFailure
{
    InvalidUtf8,
    InvalidXml,
    InvalidDeclaration,
    DocumentRequired,
    UnsupportedName,
    ByteLimit,
    DepthLimit,
    NodeLimit,
    AttributeLimit,
    DocumentCountLimit,
    InvalidMetadata,
    MetadataTooLarge,
    InvalidNativeMetadata,
    NativeByteLimit
}

/// <summary>One-based parser position, not a byte offset or a mapping/schema location.</summary>
public sealed record XmlDocumentLocation(int Line, int Column);

public sealed record XmlDocumentStatistics(int OriginalBytes, int PhysicalNodes, int MaximumDepth, int MaximumAttributes);

public sealed class XmlDocumentException : Exception
{
    public XmlDocumentException(XmlDocumentFailure reason, XmlDocumentLocation? location = null,
        XmlDocumentStatistics? statistics = null, long? observed = null, long? limit = null)
        : base(MessageFor(reason))
    {
        Reason = reason;
        Location = location is { Line: > 0, Column: > 0 } ? location : null;
        Statistics = statistics;
        Observed = observed;
        Limit = limit;
    }

    public XmlDocumentFailure Reason { get; }
    public XmlDocumentLocation? Location { get; }
    public XmlDocumentStatistics? Statistics { get; }
    public long? Observed { get; }
    public long? Limit { get; }
    public bool IsResourceLimit => Reason is XmlDocumentFailure.ByteLimit or XmlDocumentFailure.DepthLimit or
        XmlDocumentFailure.NodeLimit or XmlDocumentFailure.AttributeLimit or XmlDocumentFailure.DocumentCountLimit or
        XmlDocumentFailure.MetadataTooLarge or XmlDocumentFailure.NativeByteLimit;

    private static string MessageFor(XmlDocumentFailure reason) => reason switch
    {
        XmlDocumentFailure.InvalidUtf8 => "XML must be strict UTF-8.",
        XmlDocumentFailure.InvalidXml => "XML does not meet the safe document syntax policy.",
        XmlDocumentFailure.InvalidDeclaration => "XML requires an XML 1.0 declaration with UTF-8 or no declared encoding.",
        XmlDocumentFailure.DocumentRequired => "XML requires one nonempty document with one root element.",
        XmlDocumentFailure.UnsupportedName => "XML physical names must use Basic Multilingual Plane characters.",
        XmlDocumentFailure.ByteLimit => "XML exceeds the original byte limit.",
        XmlDocumentFailure.DepthLimit => "XML exceeds the physical element depth limit.",
        XmlDocumentFailure.NodeLimit => "XML exceeds the physical node limit.",
        XmlDocumentFailure.AttributeLimit => "XML exceeds the physical attribute limit.",
        XmlDocumentFailure.DocumentCountLimit => "XML exceeds the document count limit.",
        XmlDocumentFailure.InvalidMetadata => "Invalid XML boundary metadata.",
        XmlDocumentFailure.MetadataTooLarge => "XML boundary metadata exceeds the encoded metadata limit.",
        XmlDocumentFailure.InvalidNativeMetadata => "The saved native artifact does not declare admitted XML boundary metadata.",
        XmlDocumentFailure.NativeByteLimit => "The saved native artifact exceeds the byte limit.",
        _ => "Invalid XML document operation."
    };
}
