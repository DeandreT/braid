using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

internal readonly record struct CatalogRevisionReportReadSelector(Guid? ProjectionDerivationId);
internal readonly record struct CatalogRevisionReportEnsureSelector(Guid? RepairDerivationId);
internal readonly record struct CatalogRevisionComparisonSideSelector(Guid Revision, Guid Projection, Guid? Validation);
internal readonly record struct CatalogRevisionComparisonSelector(
    CatalogRevisionComparisonSideSelector Left, CatalogRevisionComparisonSideSelector Right);

/// <summary>Strict selectors and proven empty framing; this parser never consumes an entity.</summary>
internal static class CatalogRevisionReportRequestParser
{
    private const int MaximumQueryCharacters = 256;
    private const int MaximumComparisonQueryCharacters = 1024;
    private static readonly string[] ComparisonNames =
        ["leftRevision", "leftProjection", "leftValidation", "rightRevision", "rightProjection", "rightValidation"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ForbiddenHeaders =
        ["If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since", "If-Range", "Idempotency-Key"];

    internal static RevisionScope Scope(string organizationId, string projectId, string mappingId, string revisionId) =>
        new(Id(organizationId), Id(projectId), Id(mappingId), Id(revisionId));

    internal static (Guid OrganizationId, Guid ProjectId, Guid MappingId) Mapping(string organizationId,
        string projectId, string mappingId) => (Id(organizationId), Id(projectId), Id(mappingId));

    internal static Guid RetainedId(string derivationId) => Id(derivationId);

    internal static CatalogRevisionReportReadSelector ParseRead(HttpRequest request, RevisionReportOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (operation is not (RevisionReportOperation.Projection or RevisionReportOperation.Validation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        RequireEmptyRequest(request);
        return new(Selector(request, operation == RevisionReportOperation.Validation ? "projectionDerivationId" : null));
    }

    internal static CatalogRevisionReportEnsureSelector ParseEnsure(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireEmptyRequest(request);
        return new(Selector(request, "repairDerivationId"));
    }

    internal static CatalogRevisionComparisonSelector ParseComparison(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireEmptyRequest(request);
        var query = request.QueryString.Value;
        if (string.IsNullOrEmpty(query) || query.Length > MaximumComparisonQueryCharacters || query[0] != '?')
            throw Invalid();
        var values = new Guid?[ComparisonNames.Length];
        foreach (var part in query[1..].Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || part.LastIndexOf('=') != equals || part.Length > MaximumQueryCharacters)
                throw Invalid();
            var index = Array.IndexOf(ComparisonNames, Decode(part.AsSpan(0, equals)));
            // Each name is accepted once, including names escaped differently.
            if (index < 0 || values[index] is not null)
                throw Invalid();
            values[index] = Id(Decode(part.AsSpan(equals + 1)));
        }
        if (values[0] is not { } leftRevision || values[1] is not { } leftProjection ||
            values[3] is not { } rightRevision || values[4] is not { } rightProjection)
            throw Invalid();
        return new(new(leftRevision, leftProjection, values[2]), new(rightRevision, rightProjection, values[5]));
    }

    private static void RequireEmptyRequest(HttpRequest request)
    {
        foreach (var name in ForbiddenHeaders)
            if (request.Headers.ContainsKey(name))
                throw Invalid();
        if (request.Headers.ContainsKey("Content-Type"))
            throw Unsupported();
        var codings = request.Headers.ContentEncoding;
        if (request.Headers.ContainsKey("Content-Encoding") && (codings.Count != 1 ||
            !string.Equals(codings[0]?.Trim(' ', '\t'), "identity", StringComparison.OrdinalIgnoreCase)))
            throw Unsupported();
        if (request.Headers.ContainsKey("Transfer-Encoding"))
            throw Invalid();
        if (request.Headers.TryGetValue("Content-Length", out var lengths))
        {
            if (lengths.Count != 1 || lengths[0] is not { } raw)
                throw Invalid();
            var text = raw.Trim(' ', '\t');
            if (text.Length is < 1 or > 20 || text.Any(character => character != '0'))
                throw Invalid();
            return;
        }
        // Unknown framing is not established empty by a user stream or a bounded
        // probe. Only the server's actual request-body feature supplies this proof.
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false)
            throw Invalid();
    }

    private static Guid? Selector(HttpRequest request, string? allowedName)
    {
        var query = request.QueryString.Value;
        if (string.IsNullOrEmpty(query) || query == "?")
            return null;
        if (allowedName is null || query.Length > MaximumQueryCharacters || query[0] != '?')
            throw Invalid();
        var raw = query.AsSpan(1);
        var equals = raw.IndexOf('=');
        if (equals <= 0 || raw.IndexOf('&') >= 0 || raw.LastIndexOf('=') != equals)
            throw Invalid();
        var name = Decode(raw[..equals]);
        if (!string.Equals(name, allowedName, StringComparison.Ordinal))
            throw Invalid();
        return Id(Decode(raw[(equals + 1)..]));
    }

    private static string Decode(ReadOnlySpan<char> raw)
    {
        Span<byte> bytes = stackalloc byte[MaximumQueryCharacters];
        var count = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character == '%')
            {
                if (index + 2 >= raw.Length || Hex(raw[index + 1]) is not { } high || Hex(raw[index + 2]) is not { } low)
                    throw Invalid();
                bytes[count++] = (byte)((high << 4) | low);
                index += 2;
            }
            else
            {
                // Every valid name and canonical UUID is ASCII. A plus sign is
                // preserved as a literal character, never form-decoded to space.
                if (character > 0x7f)
                    throw Invalid();
                bytes[count++] = (byte)character;
            }
        }
        try { return StrictUtf8.GetString(bytes[..count]); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }

    private static int? Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => null
    };

    private static Guid Id(string text)
    {
        if (text is null || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
            throw Invalid();
        return id;
    }

    private static CatalogRevisionReportRequestException Invalid() =>
        new(CatalogErrorCodeV1.InvalidRequest, StatusCodes.Status400BadRequest);
    private static CatalogRevisionReportRequestException Unsupported() =>
        new(CatalogErrorCodeV1.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType);
}
