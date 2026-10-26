using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Weft.Contracts.Chat;
using Weft.Domain;

namespace Weft.Api.Chat;

internal sealed record ChatTurnRequest(Guid TurnId, string Text, string ProfileId, int ProfileVersion,
    IReadOnlyList<ChatCitationTarget> Selectors)
{
    public override string ToString() => nameof(ChatTurnRequest);
}

internal static class ChatRequestParser
{
    internal const int ConversationBodyBytes = 1024;
    internal const int TurnBodyBytes = 262_144;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Guid Id(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw ChatRequestException.Invalid();
        return id;
    }

    internal static void RequireQuery(HttpRequest request, params string[] allowed)
    {
        foreach (var query in request.Query)
            if (!allowed.Contains(query.Key, StringComparer.Ordinal) || query.Value.Count != 1)
                throw ChatRequestException.Invalid();
    }

    /// <summary>Canonical decimal in [minimum, maximum], or the fallback when absent.</summary>
    internal static int Number(HttpRequest request, string name, int minimum, int maximum, int fallback)
    {
        if (!request.Query.TryGetValue(name, out var values)) return fallback;
        var text = values[0];
        if (text is null || text.Length is < 1 or > 4 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum || text != value.ToString(CultureInfo.InvariantCulture))
            throw ChatRequestException.Invalid();
        return value;
    }

    /// <summary>A canonical identity query value, or null when absent.</summary>
    internal static Guid? OptionalId(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) ? Id(values[0] ?? throw ChatRequestException.Invalid()) : null;

    internal static void RequireNoBody(HttpRequest request)
    {
        if (request.ContentLength is > 0 || request.Headers.ContentType.Count != 0 ||
            request.ContentLength is null && request.Headers.TransferEncoding.Count != 0)
            throw ChatRequestException.Invalid();
    }

    internal static async Task<Guid> ReadConversationAsync(HttpRequest request, CancellationToken ct)
    {
        using var document = await ReadJsonAsync(request, ConversationBodyBytes, ct);
        Guid? id = null;
        try
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "conversationId" && id is null && property.Value.ValueKind == JsonValueKind.String)
                    id = Id(property.Value.GetString()!);
                else throw ChatRequestException.Invalid();
            }
        }
        catch (InvalidOperationException) { throw ChatRequestException.Invalid(); }
        return id ?? throw ChatRequestException.Invalid();
    }

    /// <summary>A workspace conversation's identity and optional focus: a mapping, or one exact revision of it.</summary>
    internal static async Task<(Guid ConversationId, (Guid Project, Guid Mapping, Guid? Revision)? Focus)> ReadWorkspaceConversationAsync(
        HttpRequest request, CancellationToken ct)
    {
        using var document = await ReadJsonAsync(request, ConversationBodyBytes, ct);
        Guid? id = null;
        (Guid, Guid, Guid?)? focus = null;
        var focusSeen = false;
        try
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "conversationId" && id is null && property.Value.ValueKind == JsonValueKind.String)
                    id = Id(property.Value.GetString()!);
                else if (property.Name == "focus" && !focusSeen)
                {
                    focusSeen = true;
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    if (property.Value.ValueKind != JsonValueKind.Object) throw ChatRequestException.Invalid();
                    Guid? project = null, mapping = null, revision = null;
                    var revisionSeen = false;
                    foreach (var item in property.Value.EnumerateObject())
                    {
                        if (item.Name == "projectId" && project is null && item.Value.ValueKind == JsonValueKind.String) project = Id(item.Value.GetString()!);
                        else if (item.Name == "mappingId" && mapping is null && item.Value.ValueKind == JsonValueKind.String) mapping = Id(item.Value.GetString()!);
                        else if (item.Name == "revisionId" && !revisionSeen && item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                        {
                            revisionSeen = true;
                            revision = item.Value.ValueKind == JsonValueKind.Null ? null : Id(item.Value.GetString()!);
                        }
                        else throw ChatRequestException.Invalid();
                    }
                    focus = (project ?? throw ChatRequestException.Invalid(), mapping ?? throw ChatRequestException.Invalid(), revision);
                }
                else throw ChatRequestException.Invalid();
            }
        }
        catch (InvalidOperationException) { throw ChatRequestException.Invalid(); }
        return (id ?? throw ChatRequestException.Invalid(), focus);
    }

    internal static async Task<ChatTurnRequest> ReadTurnAsync(HttpRequest request, CancellationToken ct)
    {
        using var document = await ReadJsonAsync(request, TurnBodyBytes, ct);
        // Invalid escaped surrogates fail while unescaping; domain shape checks throw ArgumentException.
        try { return Turn(document.RootElement); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        { throw ChatRequestException.Invalid(); }
    }

    private static ChatTurnRequest Turn(JsonElement root)
    {
        Guid? turn = null;
        string? text = null, profile = null;
        int? version = null;
        List<ChatCitationTarget>? selectors = null;
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "turnId" when turn is null && value.ValueKind == JsonValueKind.String:
                    turn = Id(value.GetString()!);
                    break;
                case "text" when text is null && value.ValueKind == JsonValueKind.String:
                    text = value.GetString()!;
                    break;
                case "profileId" when profile is null && value.ValueKind == JsonValueKind.String:
                    profile = value.GetString()!;
                    break;
                case "profileVersion" when version is null && value.ValueKind == JsonValueKind.Number:
                    version = value.TryGetInt32(out var number) && number >= 1 && value.GetRawText() == number.ToString(CultureInfo.InvariantCulture)
                        ? number : throw ChatRequestException.Invalid();
                    break;
                case "selectors" when selectors is null && value.ValueKind == JsonValueKind.Array:
                    selectors = Selectors(value);
                    break;
                default:
                    throw ChatRequestException.Invalid();
            }
        }
        if (turn is null || text is null || profile is null || version is null || selectors is null ||
            string.IsNullOrWhiteSpace(text) || text.Contains('\0') || StrictUtf8.GetByteCount(text) > ChatApiOptions.MaximumTextBytes ||
            profile.Length is < 1 or > 64)
            throw ChatRequestException.Invalid();
        return new(turn.Value, text, profile, version.Value, selectors.AsReadOnly());
    }

    private static List<ChatCitationTarget> Selectors(JsonElement array)
    {
        var result = new List<ChatCitationTarget>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || result.Count == ChatApiOptions.MaximumSelectors)
                throw ChatRequestException.Invalid();
            ChatSelectorKindV1? kind = null;
            string? resource = null;
            int? ordinal = null;
            bool hasResource = false, hasOrdinal = false;
            foreach (var property in item.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "kind" when kind is null && value.ValueKind == JsonValueKind.String:
                        kind = Kind(value.GetString()!);
                        break;
                    case "resourceId" when !hasResource && value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        hasResource = true;
                        resource = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
                        break;
                    case "diagnosticOrdinal" when !hasOrdinal && value.ValueKind is JsonValueKind.Number or JsonValueKind.Null:
                        hasOrdinal = true;
                        if (value.ValueKind == JsonValueKind.Number)
                            ordinal = value.TryGetInt32(out var number) && value.GetRawText() == number.ToString(CultureInfo.InvariantCulture)
                                ? number : throw ChatRequestException.Invalid();
                        break;
                    default:
                        throw ChatRequestException.Invalid();
                }
            }
            if (kind is null || !hasResource || !hasOrdinal) throw ChatRequestException.Invalid();
            var target = new ChatCitationTarget((ChatCitationKind)(int)kind.Value, resource, ordinal);
            if (result.Contains(target)) throw ChatRequestException.Invalid();
            result.Add(target);
        }
        return result;
    }

    private static ChatSelectorKindV1 Kind(string text) => text switch
    {
        "boundary" => ChatSelectorKindV1.Boundary,
        "field" => ChatSelectorKindV1.Field,
        "parameter" => ChatSelectorKindV1.Parameter,
        "rule" => ChatSelectorKindV1.Rule,
        "control" => ChatSelectorKindV1.Control,
        "projectionDiagnostic" => ChatSelectorKindV1.ProjectionDiagnostic,
        "validationDiagnostic" => ChatSelectorKindV1.ValidationDiagnostic,
        "projectionReport" => ChatSelectorKindV1.ProjectionReport,
        "validationReport" => ChatSelectorKindV1.ValidationReport,
        _ => throw ChatRequestException.Invalid()
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        var types = request.Headers.ContentType;
        if (types.Count != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var type) ||
            !string.Equals(type.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) || type.Parameters.Count > 1 ||
            type.Parameters.Any(parameter => !string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(HeaderUtilities.RemoveQuotes(parameter.Value).Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ChatRequestException(ChatErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        var encodings = request.Headers.ContentEncoding;
        if (encodings.Count > 1 || encodings.Count == 1 && !string.Equals(encodings[0]?.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
            throw new ChatRequestException(ChatErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
        if (request.ContentLength > limit) throw BodyLimit();
        var bytes = new byte[limit + 1];
        var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await request.Body.ReadAsync(bytes.AsMemory(count, bytes.Length - count), ct);
                if (read == 0) break;
                count += read;
                if (count > limit) throw BodyLimit();
            }
        }
        catch (BadHttpRequestException error)
        {
            ct.ThrowIfCancellationRequested();
            throw error.StatusCode == StatusCodes.Status413PayloadTooLarge ? BodyLimit() : ChatRequestException.Invalid();
        }
        catch (Exception error) when (error is IOException or ConnectionResetException)
        {
            ct.ThrowIfCancellationRequested();
            throw ChatRequestException.Invalid();
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            var body = bytes.AsMemory(0, count);
            if (body.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw ChatRequestException.Invalid();
            _ = StrictUtf8.GetCharCount(body.Span);
            var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw ChatRequestException.Invalid();
            }
            return document;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or DecoderFallbackException)
        {
            throw ChatRequestException.Invalid();
        }
    }

    private static ChatRequestException BodyLimit() => new(ChatErrorCodeV1.BodyLimit, StatusCodes.Status413PayloadTooLarge);
}

internal sealed class ChatRequestException(ChatErrorCodeV1 code, int statusCode) : Exception("The chat request was refused.")
{
    internal ChatErrorCodeV1 Code { get; } = code;
    internal int StatusCode { get; } = statusCode;
    internal static ChatRequestException Invalid() => new(ChatErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
}
