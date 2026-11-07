using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal static class CatalogInterchangeParser
{
    internal const string MediaType = "application/vnd.weft.mfd-package.v1";
    internal static ImportDestination Create(HttpRequest request, ProjectScope project)
    {
        var query = CatalogRevisionRequestParser.Query(request, "name", "description", "backendId");
        if (!query.TryGetValue("name", out var name) || !query.TryGetValue("backendId", out var backend))
            throw CatalogRevisionRequestException.Invalid();
        try { return ImportDestination.Create(project, backend, new(name, query.GetValueOrDefault("description") ?? "")); }
        catch (ArgumentException) { throw CatalogRevisionRequestException.Invalid(); }
    }
    internal static EqualHead AppendHead(HttpRequest request)
    {
        var query = CatalogRevisionRequestParser.Query(request, "expectedHeadRevisionId");
        return query.TryGetValue("expectedHeadRevisionId", out var id)
            ? new(CatalogRevisionRequestParser.CanonicalId(id)) : throw CatalogRevisionRequestException.Invalid();
    }
    internal static async Task<InterchangePackage> PackageAsync(HttpRequest request, CancellationToken token)
    {
        CatalogRevisionRequestParser.ResumeHeaders(request);
        Content(request, MediaType);
        var bytes = await ReadAsync(request, InterchangePackage.MaximumEncodedBytes, token);
        try { return InterchangePackage.Decode(bytes); }
        catch (ArgumentException) { throw CatalogRevisionRequestException.Invalid(); }
    }
    internal static async Task<CatalogInterchangeConfirmationV1> ConfirmationAsync(HttpRequest request, CancellationToken token)
    {
        CatalogRevisionRequestParser.NoQuery(request);
        Content(request, "application/json");
        var bytes = await ReadAsync(request, 1024, token);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw CatalogRevisionRequestException.Invalid();
            string? digest = null;
            bool? acknowledge = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name == "reviewDigest" && digest is null && property.Value.ValueKind == JsonValueKind.String)
                    digest = property.Value.GetString();
                else if (property.Name == "acknowledgeWarnings" && acknowledge is null && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    acknowledge = property.Value.GetBoolean();
                else throw CatalogRevisionRequestException.Invalid();
            }
            if (digest is null || acknowledge is null || Sha256Digest.Parse(digest).Value != digest)
                throw CatalogRevisionRequestException.Invalid();
            return new(digest, acknowledge.Value);
        }
        catch (JsonException) { throw CatalogRevisionRequestException.Invalid(); }
        catch (ArgumentException) { throw CatalogRevisionRequestException.Invalid(); }
    }
    private static void Content(HttpRequest request, string expected)
    {
        var values = request.Headers.ContentType;
        if (values.Count != 1 || !MediaTypeHeaderValue.TryParse(values[0], out var type) ||
            !string.Equals(type.MediaType.Value, expected, StringComparison.OrdinalIgnoreCase) || type.Parameters.Count != 0 ||
            request.Headers.ContentEncoding.Count > 1 || request.Headers.ContentEncoding.Count == 1 &&
            !string.Equals(request.Headers.ContentEncoding[0], "identity", StringComparison.OrdinalIgnoreCase))
            throw new CatalogRevisionRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
    }
    private static async Task<byte[]> ReadAsync(HttpRequest request, int limit, CancellationToken token)
    {
        if (request.ContentLength > limit) throw CatalogRevisionRequestException.BodyLimit();
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = limit - checked((int)destination.Length);
            int count;
            try { count = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), token); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413) { throw CatalogRevisionRequestException.BodyLimit(); }
            catch (BadHttpRequestException) { throw CatalogRevisionRequestException.Invalid(); }
            catch (IOException) when (!token.IsCancellationRequested) { throw CatalogRevisionRequestException.Invalid(); }
            if (count == 0) break;
            if (count > remaining) throw CatalogRevisionRequestException.BodyLimit();
            destination.Write(buffer, 0, count);
        }
        return destination.ToArray();
    }
}
