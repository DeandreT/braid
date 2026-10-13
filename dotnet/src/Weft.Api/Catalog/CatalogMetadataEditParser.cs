using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Contracts.Catalog;

namespace Weft.Api.Catalog;

internal static class CatalogMetadataEditParser
{
    internal const string MediaType = "application/vnd.weft.mapping-metadata-edit.v1+json";
    internal const int MaximumBodyBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireContent(HttpRequest request)
    {
        var types = request.Headers.ContentType;
        if (types.Count != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var type) ||
            !string.Equals(type.MediaType.Value, MediaType, StringComparison.OrdinalIgnoreCase) || type.Parameters.Count > 1)
            throw Unsupported();
        foreach (var parameter in type.Parameters)
            if (!string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HeaderUtilities.RemoveQuotes(parameter.Value).Value, "utf-8", StringComparison.OrdinalIgnoreCase))
                throw Unsupported();
        var encodings = request.Headers.ContentEncoding;
        if (encodings.Count > 1 || (encodings.Count == 1 &&
            !string.Equals(encodings[0]?.Trim(' ', '\t'), "identity", StringComparison.OrdinalIgnoreCase)))
            throw Unsupported();
        if (request.ContentLength > MaximumBodyBytes)
            throw BodyLimit();
    }

    internal static async Task<MappingMetadataEdit> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // One overflow byte proves the limit without draining an unbounded body.
        var bytes = new byte[MaximumBodyBytes + 1];
        var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await request.Body.ReadAsync(bytes.AsMemory(count, bytes.Length - count), cancellationToken);
                if (read == 0)
                    break;
                count += read;
                if (count > MaximumBodyBytes)
                    throw BodyLimit();
            }
        }
        catch (BadHttpRequestException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
                throw BodyLimit();
            throw CatalogMetadataRequestException.Invalid();
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CatalogMetadataRequestException.Invalid();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(bytes.AsMemory(0, count));
    }

    private static MappingMetadataEdit Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                throw CatalogMetadataRequestException.Invalid();
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw CatalogMetadataRequestException.Invalid();
            string? name = null;
            string? description = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw CatalogMetadataRequestException.Invalid();
                switch (property.Name)
                {
                    case "name" when name is null:
                        name = property.Value.GetString();
                        break;
                    case "description" when description is null:
                        description = property.Value.GetString();
                        break;
                    default:
                        throw CatalogMetadataRequestException.Invalid();
                }
            }
            if (name is null || description is null)
                throw CatalogMetadataRequestException.Invalid();
            return new MappingMetadataEdit(name, description);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            // Invalid escaped surrogates can fail while unescaping a JSON string.
            throw CatalogMetadataRequestException.Invalid();
        }
    }

    private static CatalogMetadataRequestException Unsupported() =>
        new(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
    private static CatalogMetadataRequestException BodyLimit() =>
        new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}
