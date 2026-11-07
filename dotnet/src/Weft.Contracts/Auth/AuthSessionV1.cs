using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Weft.Contracts.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthSessionV1
{
    [JsonConstructor]
    public AuthSessionV1(int version, string mode, string state, string authActionRequestToken,
        string? requestToken, string? identityContext, string? displayName = null)
    {
        if (version != 1 || mode != "session" || state is not ("anonymous" or "authenticationRequired" or "authenticated") ||
            !AuthValueV1.IsToken(authActionRequestToken, 4096) ||
            (state == "authenticated" ? !AuthValueV1.IsToken(requestToken, 4096) ||
                !AuthValueV1.IsIdentityContext(identityContext) : requestToken is not null || identityContext is not null || displayName is not null) ||
            displayName is not null && !AccountDisplayName.IsValid(displayName))
            throw new ArgumentException("The authentication status is invalid.");
        State = state;
        AuthActionRequestToken = authActionRequestToken;
        RequestToken = requestToken;
        IdentityContext = identityContext;
        DisplayName = displayName;
    }
    public int Version => 1;
    public string Mode => "session";
    public string State { get; }
    public string AuthActionRequestToken { get; }
    public string? RequestToken { get; }
    public string? IdentityContext { get; }
    public string? DisplayName { get; }
    public override string ToString() => nameof(AuthSessionV1);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthModeStatusV1
{
    [JsonConstructor]
    public AuthModeStatusV1(int version, string mode)
    {
        if (version != 1 || mode is not ("development" or "disabled"))
            throw new ArgumentException("The authentication mode is invalid.");
        Mode = mode;
    }
    public int Version => 1;
    public string Mode { get; }
    public override string ToString() => nameof(AuthModeStatusV1);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthLoginV1
{
    [JsonConstructor]
    public AuthLoginV1(int version, string authorizationUrl)
    {
        if (version != 1 || authorizationUrl is null || authorizationUrl.Length > 16384 ||
            !Uri.TryCreate(authorizationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The authorization destination is invalid.");
        AuthorizationUrl = authorizationUrl;
    }
    public int Version => 1;
    public string AuthorizationUrl { get; }
    public override string ToString() => nameof(AuthLoginV1);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthForgetLocalV1
{
    [JsonConstructor]
    public AuthForgetLocalV1(int version, string outcome)
    {
        if (version != 1 || outcome != "revocationUnconfirmed")
            throw new ArgumentException("The local forgetting outcome is invalid.");
    }
    public int Version => 1;
    public string Outcome => "revocationUnconfirmed";
    public override string ToString() => nameof(AuthForgetLocalV1);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AuthErrorV1
{
    [JsonConstructor]
    public AuthErrorV1(int version, string code)
    {
        if (version != 1 || code is not ("invalidRequest" or "authenticationRequired" or
            "requestRejected" or "forgetNotAvailable" or "unavailable"))
            throw new ArgumentException("The authentication error category is invalid.");
        Code = code;
    }
    public int Version => 1;
    public string Code { get; }
    public override string ToString() => nameof(AuthErrorV1);
}

internal static class AuthValueV1
{
    internal static bool IsIdentityContext(string? value) => value is { Length: 46 } &&
        value.StartsWith("v1.", StringComparison.Ordinal) && IsToken(value[3..], 43);

    internal static bool IsToken(string? value, int maximum)
    {
        if (value is not { Length: > 0 } || value.Length > maximum || value.Length % 4 == 1 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return false;
        byte[]? bytes = null;
        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - value.Length % 4) % 4));
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
        }
        catch (FormatException) { return false; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }
}
