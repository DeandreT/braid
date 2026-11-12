using System.Globalization;
using System.Text.Json.Serialization;

namespace Weft.Contracts.Catalog;

/// <summary>A preflight refusal, never a stored run. Only closed categories and nonnegative numeric facts cross the boundary.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogXmlPreviewProblemV1(string Code, string Kind, string Reason,
    CatalogXmlLocationV1? Location, CatalogXmlStatisticsV1? Statistics, string? Observed, string? Limit)
{
    public static readonly string[] Reasons = ["invalidUtf8", "invalidXml", "invalidDeclaration", "documentRequired", "unsupportedName",
        "byteLimit", "depthLimit", "nodeLimit", "attributeLimit", "documentCountLimit", "invalidMetadata", "metadataTooLarge", "invalidNativeMetadata", "nativeByteLimit"];

    public static string KindFor(string reason) => reason switch
    {
        "byteLimit" or "depthLimit" or "nodeLimit" or "attributeLimit" or "documentCountLimit" or "metadataTooLarge" or "nativeByteLimit" => "resourceLimit",
        "invalidNativeMetadata" => "unsupportedCapability",
        _ => "invalidInput"
    };

    public bool IsSafe() => Code == "xmlPreflight" && Reasons.Contains(Reason, StringComparer.Ordinal) && Kind == KindFor(Reason)
        && (Location is null || Location is { Line: > 0, Column: > 0 })
        && (Statistics is null || Statistics is { OriginalBytes: >= 0, PhysicalNodes: >= 0, MaximumDepth: >= 0, MaximumAttributes: >= 0 })
        && Decimal(Observed) && Decimal(Limit);

    private static bool Decimal(string? value) => value is null || value.Length is > 0 and <= 19
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
        && value == parsed.ToString(CultureInfo.InvariantCulture);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogXmlLocationV1(int Line, int Column);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogXmlStatisticsV1(int OriginalBytes, int PhysicalNodes, int MaximumDepth, int MaximumAttributes);
