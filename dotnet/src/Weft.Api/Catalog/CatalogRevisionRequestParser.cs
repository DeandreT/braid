using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal static class CatalogRevisionRequestParser
{
    internal const string MediaType = "application/octet-stream";
    internal const int MaximumNativeBytes = 4_194_304;
    private const int MaximumQueryCharacters = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static CreateRevisionRequest Create(HttpRequest request)
    {
        var query = Query(request, "name", "backendId", "description", "originalFilename");
        if (!query.TryGetValue("name", out var name) || !query.TryGetValue("backendId", out var backend))
            throw CatalogRevisionRequestException.Invalid();
        try
        {
            return new(new IngestionCreateMetadata(name, query.GetValueOrDefault("description") ?? ""),
                backend, query.GetValueOrDefault("originalFilename"));
        }
        catch (ArgumentException) { throw CatalogRevisionRequestException.Invalid(); }
    }

    internal static AppendRevisionRequest Append(HttpRequest request)
    {
        var query = Query(request, "head", "expectedHeadRevisionId", "originalFilename");
        var head = query.GetValueOrDefault("head") ?? "any";
        HeadCondition condition;
        if (head == "any" && !query.ContainsKey("expectedHeadRevisionId"))
            condition = new AnyHead();
        else if (head == "equal" && query.TryGetValue("expectedHeadRevisionId", out var expected))
            condition = new EqualHead(CanonicalId(expected));
        else
            throw CatalogRevisionRequestException.Invalid();
        try { return new(condition, query.GetValueOrDefault("originalFilename")); }
        catch (ArgumentException) { throw CatalogRevisionRequestException.Invalid(); }
    }

    internal static void NoQuery(HttpRequest request) => _ = Query(request);

    internal static Guid Key(HttpRequest request)
    {
        RejectConditions(request);
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || values[0] is not { } text)
            throw CatalogRevisionRequestException.Invalid();
        return CanonicalId(text.Trim(' ', '\t'));
    }

    internal static void ResumeHeaders(HttpRequest request)
    {
        RejectConditions(request);
        if (request.Headers.ContainsKey("Idempotency-Key"))
            throw CatalogRevisionRequestException.Invalid();
    }

    internal static void RequireContent(HttpRequest request)
    {
        var types = request.Headers.ContentType;
        if (types.Count != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var type) ||
            !string.Equals(type.MediaType.Value, MediaType, StringComparison.OrdinalIgnoreCase) || type.Parameters.Count != 0)
            throw new CatalogRevisionRequestException(CatalogErrorCodeV1.UnsupportedMediaType,
                StatusCodes.Status415UnsupportedMediaType);
        var encodings = request.Headers.ContentEncoding;
        if (encodings.Count > 1 || (encodings.Count == 1 &&
            !string.Equals(encodings[0]?.Trim(' ', '\t'), "identity", StringComparison.OrdinalIgnoreCase)))
            throw new CatalogRevisionRequestException(CatalogErrorCodeV1.UnsupportedMediaType,
                StatusCodes.Status415UnsupportedMediaType);
        // Existing-key comparisons must reach C2 even after current configured caps are reduced.
        if (request.ContentLength > MaximumNativeBytes)
            throw CatalogRevisionRequestException.BodyLimit();
    }

    internal static async Task RequireEmptyBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0)
            throw CatalogRevisionRequestException.Invalid();
        if (request.ContentLength == 0)
            return;
        var source = new CatalogRevisionRequestStream(request.Body, cancellationToken);
        var probe = new byte[1];
        if (await source.ReadAsync(probe.AsMemory(), cancellationToken) != 0)
            throw CatalogRevisionRequestException.Invalid();
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static Dictionary<string, string> Query(HttpRequest request, params string[] allowed)
    {
        var query = request.QueryString.Value ?? "";
        if (query.Length > MaximumQueryCharacters || query.Any(character => character > 0x7f))
            throw CatalogRevisionRequestException.Invalid();
        if (query.StartsWith('?'))
            query = query[1..];
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length == 0)
            return result;
        foreach (var pair in query.Split('&'))
        {
            var equal = pair.IndexOf('=');
            var key = Decode(equal < 0 ? pair : pair[..equal]);
            var value = Decode(equal < 0 ? "" : pair[(equal + 1)..]);
            if (!allowed.Contains(key, StringComparer.Ordinal) || !result.TryAdd(key, value))
                throw CatalogRevisionRequestException.Invalid();
        }
        return result;
    }

    private static string Decode(string value)
    {
        var bytes = new byte[value.Length];
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length || Hex(value[index + 1]) is not { } high || Hex(value[index + 2]) is not { } low)
                    throw CatalogRevisionRequestException.Invalid();
                bytes[count++] = (byte)((high << 4) | low);
                index += 2;
            }
            else
                bytes[count++] = character == '+' ? (byte)' ' : checked((byte)character);
        }
        try { return StrictUtf8.GetString(bytes, 0, count); }
        catch (DecoderFallbackException) { throw CatalogRevisionRequestException.Invalid(); }
    }

    private static int? Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => null
    };

    internal static Guid CanonicalId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
            throw CatalogRevisionRequestException.Invalid();
        return id;
    }

    private static void RejectConditions(HttpRequest request)
    {
        if (request.Headers.ContainsKey("If-Match") || request.Headers.ContainsKey("If-None-Match"))
            throw CatalogRevisionRequestException.Invalid();
    }
}

internal sealed class CatalogRevisionRequestException(CatalogErrorCodeV1 code, int statusCode) : Exception
{
    internal CatalogErrorCodeV1 Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
    internal static CatalogRevisionRequestException Invalid() =>
        new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
    internal static CatalogRevisionRequestException BodyLimit() =>
        new(CatalogErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}
