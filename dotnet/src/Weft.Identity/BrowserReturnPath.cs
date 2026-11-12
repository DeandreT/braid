using System.Text;

namespace Weft.Identity;

/// <summary>
/// Same-origin application navigation, never a general URI. Path syntax, query data and
/// retained-resource fragments have separate rules; validation never rewrites the wire value.
/// Angular mirrors this contract in auth/return-path.ts and shares its test vectors.
/// </summary>
public static class BrowserReturnPath
{
    public const string Root = "/";
    public const string Default = "/catalog";
    public const int MaximumLength = 7 * 1024;
    public const int MaximumPathLength = 1024;
    public const int MaximumSearchQueryBytes = 256;
    public const int MaximumSearchCursorLength = 4096;
    public const int MaximumResourceBytes = 2048;
    // The protected state adds flow/nonce/correlation/PKCE metadata and encryption/base64
    // overhead. It remains inside the unchanged 16 KiB authorization/callback budget.
    public const int MaximumProtectedStateLength = 12 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Sections = ["catalog", "help", "administration", "chat", "notifications"];

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length is 0 or > MaximumLength || value[0] != '/' || !value.All(WireCharacter))
            return false;
        var fragmentAt = value.IndexOf('#', StringComparison.Ordinal);
        var queryAt = value.IndexOf('?', StringComparison.Ordinal);
        if (queryAt >= 0 && fragmentAt >= 0 && queryAt > fragmentAt) queryAt = -1;
        var pathEnd = Math.Min(queryAt < 0 ? value.Length : queryAt, fragmentAt < 0 ? value.Length : fragmentAt);
        var path = value[..pathEnd];
        if (path.Length > MaximumPathLength) return false;
        var raw = path[1..].Split('/');
        if (!Sections.Contains(raw[0], StringComparer.Ordinal)) return false;
        var segments = new string[raw.Length];
        for (var index = 0; index < raw.Length; index++)
            if (!PathSegment(raw[index], out segments[index])) return false;
        var queryEnd = fragmentAt < 0 ? value.Length : fragmentAt;
        if (!Query(queryAt < 0 ? "" : value[(queryAt + 1)..queryEnd], out var values) || !AllowedQuery(segments, values))
            return false;
        if (fragmentAt < 0) return true;
        if (!Decode(value[(fragmentAt + 1)..], false, out var fragment)) return false;
        if (segments[0] == "help" && segments.Length >= 2)
            return Token(fragment, 128);
        if (!Revision(segments) || !values.ContainsKey("projectionDerivationId")) return false;
        var prefix = fragment.StartsWith("field:", StringComparison.Ordinal) ? 6 :
            fragment.StartsWith("rule:", StringComparison.Ordinal) ? 5 : 0;
        return prefix != 0 && TextBytes(fragment[prefix..], MaximumResourceBytes, false);
    }

    private static bool AllowedQuery(string[] path, Dictionary<string, string> values)
    {
        if (path is ["chat"])
        {
            if (!Keys(values, "conversationId", "projectId", "mappingId", "revisionId") || !values.Values.All(Id)) return false;
            if (values.Count == 0) return true;
            var scoped = new[] { "projectId", "mappingId", "revisionId" }.Count(values.ContainsKey);
            return values.ContainsKey("conversationId") && scoped is 0 or 3;
        }
        if (path is ["chat", "resources"])
        {
            if (!Keys(values, "kind", "organizationId", "projectId", "mappingId", "revisionId", "subjectId", "version") ||
                !new[] { "organizationId", "projectId", "subjectId" }.All(key => values.TryGetValue(key, out var value) && Id(value))) return false;
            values.TryGetValue("kind", out var kind); values.TryGetValue("mappingId", out var mapping);
            values.TryGetValue("revisionId", out var revision); values.TryGetValue("version", out var version);
            var numbered = version is { Length: > 0 and <= 20 } && version[0] is >= '1' and <= '9' && version.All(char.IsAsciiDigit);
            if (kind == "buildEnvironment") return mapping is null && revision is null && numbered;
            if (mapping is null || !Id(mapping)) return false;
            if (kind == "previewRun") return revision is not null && Id(revision) && version is null;
            if (kind == "regressionCase") return revision is null && numbered;
            return kind is "regressionRun" or "buildRun" or "buildApproval" && revision is null && version is null;
        }
        if (path[0] == "chat") return false;
        if (path[0] == "notifications")
            return path.Length == 1 && Keys(values, "after") && values.Values.All(Id);
        if (path is ["catalog", "search"])
        {
            if (!Keys(values, "q", "cursor")) return false;
            if (values.TryGetValue("q", out var query) && !TextBytes(query.Trim(), MaximumSearchQueryBytes, true)) return false;
            return !values.TryGetValue("cursor", out var cursor) ||
                query is not null && query.Trim().Length > 0 && Cursor(cursor, MaximumSearchCursorLength, false);
        }
        if (path is ["catalog", "organizations", var creationOrganization, "projects", var creationProject, "creation", var proposal] &&
            Id(creationOrganization) && Id(creationProject) && Id(proposal))
            return values.Count == 1 && values.TryGetValue("fingerprint", out var fingerprint) && fingerprint.Length == 64 &&
                fingerprint.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
        if (Revision(path))
            return Keys(values, "projectionDerivationId", "validationDerivationId") && values.Values.All(Id);
        if (Mapping(path, "comparisons"))
        {
            if (!Keys(values, "left", "right", "leftProjection", "leftValidation", "rightProjection", "rightValidation", "cursor")) return false;
            foreach (var (key, value) in values)
                if (key == "cursor" ? !Cursor(value, 683, true) : !Id(value)) return false;
            return (!values.ContainsKey("leftProjection") && !values.ContainsKey("leftValidation") || values.ContainsKey("left")) &&
                (!values.ContainsKey("rightProjection") && !values.ContainsKey("rightValidation") || values.ContainsKey("right"));
        }
        if (PagedCatalog(path))
            return Keys(values, "cursor") && (!values.TryGetValue("cursor", out var cursor) || Cursor(cursor, 683, true));
        if (path[0] == "help")
            return Keys(values, "view", "section") && values.Values.All(value => Token(value, 64));
        if (path is ["administration"])
            return Keys(values, "view") && values.Values.All(value => Token(value, 64));
        return values.Count == 0;
    }

    private static bool Revision(string[] path) => path is
        ["catalog", "organizations", var organization, "projects", var project, "mappings", var mapping, "revisions", var revision] &&
        Id(organization) && Id(project) && Id(mapping) && Id(revision);

    private static bool Mapping(string[] path, string leaf) => path.Length == 8 && path[0] == "catalog" &&
        path[1] == "organizations" && Id(path[2]) && path[3] == "projects" && Id(path[4]) &&
        path[5] == "mappings" && Id(path[6]) && path[7] == leaf;

    private static bool PagedCatalog(string[] path) =>
        path is ["catalog", "organizations", var organization, "projects"] && Id(organization) ||
        path is ["catalog", "organizations", var org, "projects", var project] && Id(org) && Id(project) ||
        path is ["catalog", "organizations", var o, "projects", var p, "mappings", var mapping] && Id(o) && Id(p) && Id(mapping);

    private static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && value == id.ToString("D");
    private static bool Keys(Dictionary<string, string> values, params string[] names) => values.Keys.All(key => names.Contains(key, StringComparer.Ordinal));
    private static bool Token(string value, int maximum) => value.Length > 0 && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool Cursor(string value, int maximum, bool canonical)
    {
        if (!Token(value, maximum) || canonical && value.Length < 2) return false;
        if (!canonical) return true;
        return (value.Length % 4) switch
        {
            0 => true,
            2 => "AQgw".Contains(value[^1], StringComparison.Ordinal),
            3 => "AEIMQUYcgkosw048".Contains(value[^1], StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool PathSegment(string raw, out string decoded)
    {
        var result = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character == '%')
            {
                if (index + 2 >= raw.Length || !Hex(raw[index + 1], out var high) || !Hex(raw[index + 2], out var low))
                { decoded = ""; return false; }
                character = (char)(high * 16 + low);
                if (character is '.' or '%' or '/' or '\\') { decoded = ""; return false; }
                index += 2;
            }
            if (!PathCharacter(character)) { decoded = ""; return false; }
            result.Append(character);
        }
        decoded = result.ToString();
        return decoded.Length > 0 && decoded is not ("." or "..");
    }

    private static bool Query(string raw, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if (raw.Length == 0) return true;
        foreach (var pair in raw.Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals < 1 || values.Count >= 7) return false;
            var key = pair[..equals];
            if (!key.All(char.IsAsciiLetterOrDigit) || !Decode(pair[(equals + 1)..], true, out var value) || !values.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static bool Decode(string raw, bool form, out string value)
    {
        var bytes = new byte[raw.Length];
        var length = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character == '%')
            {
                if (index + 2 >= raw.Length || !Hex(raw[index + 1], out var high) || !Hex(raw[index + 2], out var low))
                { value = ""; return false; }
                bytes[length++] = (byte)(high * 16 + low);
                index += 2;
            }
            else bytes[length++] = (byte)(form && character == '+' ? ' ' : character);
        }
        try { value = Utf8.GetString(bytes, 0, length); return !value.Any(char.IsControl); }
        catch (DecoderFallbackException) { value = ""; return false; }
    }

    private static bool TextBytes(string value, int maximum, bool empty) =>
        (empty || value.Length > 0) && Utf8.GetByteCount(value) <= maximum;
    private static bool WireCharacter(char value) => char.IsAsciiLetterOrDigit(value) || "-._~!$&'()*+,;=:@/?%#".Contains(value, StringComparison.Ordinal);
    private static bool PathCharacter(char value) => char.IsAsciiLetterOrDigit(value) || "-._~!$&'()*+,;=:@".Contains(value, StringComparison.Ordinal);
    private static bool Hex(char character, out int value)
    {
        value = character switch { >= '0' and <= '9' => character - '0', >= 'a' and <= 'f' => character - 'a' + 10, >= 'A' and <= 'F' => character - 'A' + 10, _ => -1 };
        return value >= 0;
    }
}
