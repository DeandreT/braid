using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Braid.Contracts.Catalog;

namespace Braid.Api.Catalog;

internal static class CatalogRegressionRunRequest
{
    internal const string MediaType = "application/vnd.braid.regression-run.v1+json";
    internal const int MaximumBytes = 2048;
    internal static async Task<(Guid CaseId, long Version)> ReadAsync(HttpRequest request, CancellationToken token)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || !string.Equals(type.MediaType.Value, MediaType, StringComparison.OrdinalIgnoreCase) ||
            type.Parameters.Count > 1 || type.Parameters.Any(parameter => !string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) ||
            type.Charset.HasValue && !string.Equals(type.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase) || request.Headers.ContainsKey("Content-Encoding"))
            throw new CatalogRegressionCaseRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength is > MaximumBytes) throw TooLarge();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var output = new MemoryStream();
        var chunk = new byte[MaximumBytes + 1];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (count > MaximumBytes - output.Length) throw TooLarge();
                output.Write(chunk, 0, count);
            }
            if (request.ContentLength is { } declared && declared != output.Length) throw Invalid();
            var bytes = output.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("caseId" or "version") || property.Value.ValueKind != JsonValueKind.String || !names.Add(property.Name)) throw Invalid();
            if (names.Count != 2) throw Invalid();
            return (CatalogRegressionCaseRequest.Id(document.RootElement.GetProperty("caseId").GetString()!),
                CatalogRegressionCaseRequest.Version(document.RootElement.GetProperty("version").GetString()!));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CatalogRegressionCaseRequestException(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status408RequestTimeout);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { throw Invalid(); }
    }
    private static CatalogRegressionCaseRequestException TooLarge() => new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
    private static CatalogRegressionCaseRequestException Invalid() => new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
}
