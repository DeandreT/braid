namespace Weft.Identity;

/// <summary>
/// The one rule for where browser sign-in may return: a same-origin path of an application section, never
/// another origin, an API route or the sign-in page itself. The Angular sign-in page applies the same rule
/// (apps/web/src/auth/return-path.ts); both test suites read the shared vectors in
/// contracts/browser-return-paths.json.
/// </summary>
public static class BrowserReturnPath
{
    /// <summary>Where sign-in returns when it names no path: the application root, which opens the catalog.</summary>
    public const string Root = "/";

    /// <summary>The catalog, where the application root and an invalid client-side request lead.</summary>
    public const string Default = "/catalog";

    public const int MaximumLength = 1024;

    // Application sections a return path may open. Sign-in, API and static file paths are never sections.
    private static readonly string[] Sections = ["catalog", "help"];

    public static bool IsValid(string? value)
    {
        if (value is null || value.Length is 0 or > MaximumLength || value[0] != '/') return false;
        var fragment = value.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0 && (!value.StartsWith("/help/", StringComparison.Ordinal) ||
            fragment == value.Length - 1 || !value[(fragment + 1)..].All(AnchorCharacter)))
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index == fragment) continue;
            if (character == '%')
            {
                if (index + 2 >= value.Length || !Hex(value[index + 1], out var high) ||
                    !Hex(value[index + 2], out var low))
                    return false;
                // An encoded octet must not hide a separator (/ \), a dot segment (.), another escape (%) or
                // a control or non-ASCII byte.
                var octet = high * 16 + low;
                if (octet is < 0x20 or >= 0x7F or 0x2F or 0x5C or 0x2E or 0x25) return false;
                index += 2;
            }
            else if (!Allowed(character)) return false;
        }
        var query = value.IndexOf('?', StringComparison.Ordinal);
        var end = Math.Min(query < 0 ? value.Length : query, fragment < 0 ? value.Length : fragment);
        var path = value[..end];
        var segments = path[1..].Split('/');
        return Array.IndexOf(Sections, segments[0]) >= 0 &&
            segments.All(segment => segment.Length != 0 && segment is not ("." or ".."));
    }

    private static bool AnchorCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    private static bool Allowed(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ||
        "-._~!$&'()*+,;=:@/?".Contains(character, StringComparison.Ordinal);

    private static bool Hex(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
        return value >= 0;
    }
}
