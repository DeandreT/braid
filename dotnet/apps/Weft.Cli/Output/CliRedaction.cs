using System.Text.RegularExpressions;

namespace Weft.Cli.Output;

/// <summary>
/// The last guard on diagnostic text: anything shaped like a JSON Web Token, a bearer credential or an OAuth
/// token parameter is replaced before it is written. CLI messages are fixed text, so this should never match.
/// </summary>
internal static partial class CliRedaction
{
    internal const string Replacement = "[redacted]";

    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var redacted = JwtPattern().Replace(text, Replacement);
        redacted = BearerPattern().Replace(redacted, "$1 " + Replacement);
        return TokenParameterPattern().Replace(redacted, "$1=" + Replacement);
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b(Bearer|bearer|BEARER)\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(access_token|refresh_token|id_token|device_code|code|client_secret|client_assertion)=[^&\s""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenParameterPattern();
}
