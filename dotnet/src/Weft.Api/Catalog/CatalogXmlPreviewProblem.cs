using System.Globalization;
using Weft.Application;
using Weft.Contracts.Catalog;

namespace Weft.Api.Catalog;

internal static class CatalogXmlPreviewProblem
{
    internal static CatalogXmlPreviewProblemV1 From(XmlDocumentException error)
    {
        // Do not serialize an exception, Message, parser text, names or native metadata.
        // An unknown future reason has a deterministic invalid-input fallback.
        var reason = error.Reason switch
        {
            XmlDocumentFailure.InvalidUtf8 => "invalidUtf8",
            XmlDocumentFailure.InvalidXml => "invalidXml",
            XmlDocumentFailure.InvalidDeclaration => "invalidDeclaration",
            XmlDocumentFailure.DocumentRequired => "documentRequired",
            XmlDocumentFailure.UnsupportedName => "unsupportedName",
            XmlDocumentFailure.ByteLimit => "byteLimit",
            XmlDocumentFailure.DepthLimit => "depthLimit",
            XmlDocumentFailure.NodeLimit => "nodeLimit",
            XmlDocumentFailure.AttributeLimit => "attributeLimit",
            XmlDocumentFailure.DocumentCountLimit => "documentCountLimit",
            XmlDocumentFailure.InvalidMetadata => "invalidMetadata",
            XmlDocumentFailure.MetadataTooLarge => "metadataTooLarge",
            XmlDocumentFailure.InvalidNativeMetadata => "invalidNativeMetadata",
            XmlDocumentFailure.NativeByteLimit => "nativeByteLimit",
            _ => "invalidXml"
        };
        return new("xmlPreflight", CatalogXmlPreviewProblemV1.KindFor(reason), reason,
            error.Location is { Line: > 0, Column: > 0 } location ? new(location.Line, location.Column) : null,
            error.Statistics is { OriginalBytes: >= 0, PhysicalNodes: >= 0, MaximumDepth: >= 0, MaximumAttributes: >= 0 } stats
                ? new(stats.OriginalBytes, stats.PhysicalNodes, stats.MaximumDepth, stats.MaximumAttributes) : null,
            Number(error.Observed), Number(error.Limit));
    }

    private static string? Number(long? value) => value is >= 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;
}
