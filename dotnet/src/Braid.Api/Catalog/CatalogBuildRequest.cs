using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Braid.Contracts.Catalog;
using Braid.Deployment;

namespace Braid.Api.Catalog;

internal sealed class CatalogBuildRequestException(CatalogErrorCodeV1 code, int status) : Exception("Build request refused.")
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int Status { get; } = status;
}

internal static class CatalogBuildRequest
{
    internal const string ApprovalMediaType = "application/vnd.braid.build-approval.v1+json";
    internal const string RunMediaType = "application/vnd.braid.build-run.v1+json";
    internal const string EnvironmentMediaType = "application/vnd.braid.build-environment.v1+json";
    internal const int MaximumBytes = 2048;
    internal static async Task<Braid.Contracts.Operations.ReleaseOperationV1> ReleaseOperationAsync(HttpRequest request, CancellationToken token)
    {
        var values = await ReadAsync(request, "application/json", ["action", "environmentId", "environmentVersion", "qualificationId", "sourceReleaseId"], token,
            ["qualificationId", "sourceReleaseId"]);
        return new(values["action"]!, values["environmentId"]!, values["environmentVersion"]!,
            values["qualificationId"], values["sourceReleaseId"]);
    }
    internal static async Task<Braid.Contracts.Operations.TriggerDeliveryResolutionV1> DeliveryResolutionAsync(HttpRequest request, CancellationToken token)
    {
        var values = await ReadAsync(request, "application/json", ["resolution", "environmentId", "environmentVersion", "acknowledgement"], token, ["acknowledgement"]);
        return new(values["resolution"]!, values["environmentId"]!, values["environmentVersion"]!, values["acknowledgement"]);
    }
    internal static Guid Id(string value) => Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty && value == result.ToString("D") ? result : throw Invalid();
    internal static long Version(string value) => value.Length is > 0 and <= 19 &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0 &&
        value == result.ToString(CultureInfo.InvariantCulture) ? result : throw Invalid();

    internal static async Task<BuildApprovalSelection> ApprovalAsync(HttpRequest request, CancellationToken token)
    {
        var values = await ReadAsync(request, ApprovalMediaType,
            ["buildId", "revisionId", "environmentId", "environmentVersion", "regressionEvidenceId"], token);
        return new(Id(values["buildId"]!), Id(values["revisionId"]!), Id(values["environmentId"]!),
            Version(values["environmentVersion"]!), Id(values["regressionEvidenceId"]!));
    }
    internal static async Task<Guid> RunAsync(HttpRequest request, CancellationToken token) =>
        Id((await ReadAsync(request, RunMediaType, ["approvalId"], token))["approvalId"]!);

    internal static async Task<BuildEnvironmentSelection> EnvironmentAsync(HttpRequest request, CancellationToken token)
    {
        var values = await ReadAsync(request, EnvironmentMediaType, ["profileId", "profileVersion"], token);
        var id = values["profileId"];
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl) || Encoding.UTF8.GetByteCount(id) > 128) throw Invalid();
        return new(id, Version(values["profileVersion"]!));
    }

    internal static async Task<Dictionary<string, string?>> ReadAsync(HttpRequest request, string mediaType, string[] fields, CancellationToken token, string[]? nullable = null, int maximumBytes = MaximumBytes, string[]? optional = null)
    {
        var values = request.Headers.ContentType;
        if (values.Count != 1 || !MediaTypeHeaderValue.TryParse(values[0], out var media) ||
            !string.Equals(media.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase) ||
            media.Parameters.Count > 1 || media.Parameters.Any(value => !string.Equals(value.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) ||
            media.Charset.HasValue && !string.Equals(media.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContainsKey("Content-Encoding"))
            throw new CatalogBuildRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength > maximumBytes) throw TooLarge();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var buffer = new MemoryStream();
        var block = new byte[maximumBytes + 1];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(block, timeout.Token)) != 0)
            {
                if (count > maximumBytes - buffer.Length) throw TooLarge();
                buffer.Write(block, 0, count);
            }
            if (request.ContentLength is { } declared && declared != buffer.Length) throw Invalid();
            var bytes = buffer.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var required = fields.ToHashSet(StringComparer.Ordinal);
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!required.Remove(property.Name)) throw Invalid();
                if (property.Value.ValueKind == JsonValueKind.Null && nullable?.Contains(property.Name) == true) result.Add(property.Name, null);
                else if (property.Value.ValueKind == JsonValueKind.String) result.Add(property.Name, property.Value.GetString()!);
                else throw Invalid();
            }
            foreach (var field in optional ?? []) if (required.Remove(field)) result.Add(field, null);
            if (required.Count != 0) throw Invalid();
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CatalogBuildRequestException(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status408RequestTimeout);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    private static CatalogBuildRequestException Invalid() => new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
    private static CatalogBuildRequestException TooLarge() => new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}
