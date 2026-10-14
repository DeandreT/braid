using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal static class CatalogProjectCreationParser
{
    internal const string MediaType = "application/vnd.weft.project-create.v1+json";
    internal const int MaximumBodyBytes = 65_536;
    private const int MaximumIdempotencyHeaderCharacters = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireNoQuery(HttpRequest request)
    {
        if (request.QueryString.HasValue && request.QueryString.Value != "?")
            throw CatalogProjectCreationRequestException.Invalid();
    }

    internal static Guid Key(HttpRequest request)
    {
        if (request.Headers.ContainsKey("If-Match") || request.Headers.ContainsKey("If-None-Match"))
            throw CatalogProjectCreationRequestException.Invalid();
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || values[0] is not { } value || value.Length > MaximumIdempotencyHeaderCharacters)
            throw CatalogProjectCreationRequestException.Invalid();
        var text = value.Trim(' ', '\t');
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw CatalogProjectCreationRequestException.Invalid();
        return id;
    }

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

    internal static async Task<ProjectCreationMetadata> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // The handler acquires its permit first. One overflow byte proves the
        // ceiling without allocating or draining an unbounded entity.
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
            throw CatalogProjectCreationRequestException.Invalid();
        }
        catch (ConnectionResetException exception)
        {
            // A transport reset can precede RequestAborted cancellation.
            throw new CatalogProjectCreationBodyTransportException(exception);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw CatalogProjectCreationRequestException.Invalid();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(bytes.AsMemory(0, count));
    }

    private static ProjectCreationMetadata Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                throw CatalogProjectCreationRequestException.Invalid();
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw CatalogProjectCreationRequestException.Invalid();
            string? name = null;
            string? description = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw CatalogProjectCreationRequestException.Invalid();
                switch (property.Name)
                {
                    case "name" when name is null:
                        name = property.Value.GetString();
                        break;
                    case "description" when description is null:
                        description = property.Value.GetString();
                        break;
                    default:
                        throw CatalogProjectCreationRequestException.Invalid();
                }
            }
            if (name is null || description is null)
                throw CatalogProjectCreationRequestException.Invalid();
            return new ProjectCreationMetadata(name, description);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            // Invalid escaped surrogates can fail while unescaping a JSON string.
            throw CatalogProjectCreationRequestException.Invalid();
        }
    }

    private static CatalogProjectCreationRequestException Unsupported() =>
        new(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
    private static CatalogProjectCreationRequestException BodyLimit() =>
        new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}

internal sealed class CatalogProjectCreationRequestException(CatalogErrorCodeV1 code, int statusCode) : Exception
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
    internal static CatalogProjectCreationRequestException Invalid() =>
        new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
}

internal sealed class CatalogProjectCreationBodyTransportException(ConnectionResetException reset)
    : Exception("The project creation request connection was reset.", reset)
{ }
