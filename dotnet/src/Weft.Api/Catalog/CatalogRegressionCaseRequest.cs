using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal sealed class CatalogRegressionCaseRequestException(CatalogErrorCodeV1 code, int status) : Exception("Regression request refused.")
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed record CatalogRegressionCaseDefinition(string Name, Guid RevisionId, RegressionRuntimeIdentity Runtime,
    RegressionReplay Replay, string Policy, RegressionOutputSet Expected);

internal static class CatalogRegressionCaseRequest
{
    internal const string MediaType = "application/vnd.weft.regression-case.v1+json";
    internal const int MaximumBodyBytes = 24 * 1024 * 1024;

    internal static long Version(string text)
    {
        if (text.Length is < 1 or > 19 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < 1 || result.ToString(CultureInfo.InvariantCulture) != text) throw Invalid();
        return result;
    }

    internal static Guid Id(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var result) || result == Guid.Empty || text != result.ToString("D")) throw Invalid();
        return result;
    }

    internal static async Task<CatalogRegressionCaseDefinition> ReadAsync(HttpRequest request, CancellationToken token)
    {
        var contentTypes = request.Headers.ContentType;
        if (contentTypes.Count != 1 || !MediaTypeHeaderValue.TryParse(contentTypes[0], out var media) ||
            !string.Equals(media.MediaType.Value, MediaType, StringComparison.OrdinalIgnoreCase) ||
            media.Parameters.Count > 1 || media.Parameters.Any(p => !string.Equals(p.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)) ||
            media.Charset.HasValue && !string.Equals(media.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase) ||
            request.Headers.ContainsKey("Content-Encoding"))
            throw new CatalogRegressionCaseRequestException(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength is > MaximumBodyBytes) throw TooLarge();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (count > MaximumBodyBytes - buffer.Length) throw TooLarge();
                buffer.Write(chunk, 0, count);
            }
            if (request.ContentLength is { } declared && buffer.Length != declared) throw Invalid();
            var bytes = buffer.ToArray();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            return Parse(document.RootElement, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CatalogRegressionCaseRequestException(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status408RequestTimeout);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException or RegressionComparisonException)
        {
            throw Invalid();
        }
        catch (RegressionCaseException error)
        {
            throw error.Failure == RegressionCaseFailure.ResourceLimit ? TooLarge() : Invalid();
        }
    }

    private static CatalogRegressionCaseDefinition Parse(JsonElement root, CancellationToken token)
    {
        Shape(root, "name", "revisionId", "runtime", "currentDateTime", "target", "limits", "primaryBase64Url", "namedInputs", "parameters", "comparisonPolicy", "expected");
        var runtime = root.GetProperty("runtime");
        Shape(runtime, "backendId", "engineRevision", "backendBuild", "adapterBuild", "profile");
        var identity = new RegressionRuntimeIdentity(Text(runtime, "backendId"), Text(runtime, "engineRevision"), Text(runtime, "backendBuild"),
            Text(runtime, "adapterBuild"), Text(runtime, "profile"));
        var targetValue = root.GetProperty("target");
        Shape(targetValue, "kind", "name");
        var targetName = NullableText(targetValue, "name");
        var target = Text(targetValue, "kind") switch
        {
            "all" when targetName is null => PreviewTargetSelection.All,
            "primary" when targetName is null => PreviewTargetSelection.Primary,
            "named" when targetName is not null => PreviewTargetSelection.Named(targetName),
            _ => throw Invalid()
        };
        var limitsValue = root.GetProperty("limits");
        Shape(limitsValue, "maximumInputBytes", "maximumOutputBytes", "maximumArtifacts", "maximumTextBytes");
        var limits = new PreviewLimits(Integer(limitsValue, "maximumInputBytes"), Integer(limitsValue, "maximumOutputBytes"),
            Integer(limitsValue, "maximumArtifacts"), Integer(limitsValue, "maximumTextBytes"));
        var primary = new PreviewInputDocument(null, Bytes(Text(root, "primaryBase64Url"), PreviewPolicy.MaximumInputBytes));
        var named = Array(root, "namedInputs", PreviewPolicy.MaximumInputDocuments - 1);
        var inputs = new List<PreviewInputDocument>();
        var inputRemaining = PreviewPolicy.MaximumInputBytes - primary.ByteCount;
        foreach (var input in named.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Shape(input, "name", "bytesBase64Url");
            var bytes = Bytes(Text(input, "bytesBase64Url"), inputRemaining);
            inputRemaining -= bytes.Length;
            inputs.Add(new(Text(input, "name"), bytes));
        }
        var parameters = Array(root, "parameters", PreviewPolicy.MaximumParameters);
        var values = new List<PreviewParameter>();
        foreach (var parameter in parameters.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Shape(parameter, "name", "kind", "value");
            var kind = Text(parameter, "kind") switch
            {
                "string" => PreviewParameterKind.String,
                "integer" => PreviewParameterKind.Integer,
                "float" => PreviewParameterKind.Float,
                "boolean" => PreviewParameterKind.Boolean,
                _ => throw Invalid()
            };
            values.Add(new(Text(parameter, "name"), kind, Text(parameter, "value")));
        }
        var expected = Array(root, "expected", PreviewPolicy.MaximumArtifacts);
        var outputs = new List<RegressionOutput>();
        var outputRemaining = PreviewPolicy.MaximumOutputBytes;
        foreach (var output in expected.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Shape(output, "name", "bytesBase64Url");
            var name = NullableText(output, "name");
            var bytes = Bytes(Text(output, "bytesBase64Url"), outputRemaining);
            outputRemaining -= bytes.Length;
            outputs.Add(RegressionOutput.Capture(name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(name), bytes, token));
        }
        return new(Text(root, "name"), Id(Text(root, "revisionId")), identity,
            new(target, Text(root, "currentDateTime"), limits, primary, inputs, values, token),
            Text(root, "comparisonPolicy"), RegressionOutputSet.Capture(outputs, token));
    }

    private static byte[] Bytes(string text, int remaining)
    {
        if (text.Length > (remaining + 2L) / 3 * 4) throw TooLarge();
        if (text.Any(c => c is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_')) throw Invalid();
        var decoded = WebEncoders.Base64UrlDecode(text);
        if (decoded.Length > remaining) throw TooLarge();
        if (WebEncoders.Base64UrlEncode(decoded) != text) throw Invalid();
        return decoded;
    }

    private static void Shape(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var required = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!required.Remove(property.Name)) throw Invalid();
        if (required.Count != 0) throw Invalid();
    }
    private static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String
        ? root.GetProperty(name).GetString()! : throw Invalid();
    private static string? NullableText(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(root, name);
    private static int Integer(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number.ToString(CultureInfo.InvariantCulture) != value.GetRawText()) throw Invalid();
        return number;
    }
    private static JsonElement Array(JsonElement root, string name, int maximum)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw Invalid();
        if (value.GetArrayLength() > maximum) throw TooLarge();
        return value;
    }
    private static CatalogRegressionCaseRequestException Invalid() => new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
    private static CatalogRegressionCaseRequestException TooLarge() => new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}
