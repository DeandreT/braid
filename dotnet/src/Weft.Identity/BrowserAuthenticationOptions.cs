using System.Text.Json.Serialization;

namespace Weft.Identity;

public sealed class BrowserAuthenticationOptions
{
    public BrowserAuthenticationOptions(Guid organizationId, string expectedOrigin)
    {
        if (organizationId == Guid.Empty)
            throw new BrowserAuthenticationException(BrowserAuthenticationFailureKind.Unavailable);
        Transport = new(expectedOrigin);
        OrganizationId = organizationId;
    }

    [JsonIgnore] public Guid OrganizationId { get; }
    [JsonIgnore] public string ExpectedOrigin => Transport.ExpectedOrigin;
    internal ServerSessionBrowserOptions Transport { get; }
    public override string ToString() => nameof(BrowserAuthenticationOptions);
}

public enum BrowserAuthenticationState { Anonymous = 1, AuthenticationRequired = 2, Authenticated = 3 }
public enum BrowserForgetLocalOutcome { RevocationUnconfirmed = 1 }
public enum BrowserAuthenticationFailureKind
{
    InvalidRequest = 1, RequestRejected = 2, AuthenticationRequired = 3,
    ForgetNotAvailable = 4, Unavailable = 5
}

public sealed class BrowserAuthenticationException : Exception
{
    internal BrowserAuthenticationException(BrowserAuthenticationFailureKind kind)
        : base("The browser authentication operation failed.") => Kind = kind;
    [JsonIgnore] public BrowserAuthenticationFailureKind Kind { get; }
    public override string ToString() => nameof(BrowserAuthenticationException);
}

public sealed class BrowserAuthenticationSession
{
    internal BrowserAuthenticationSession(BrowserAuthenticationState state, string authActionRequestToken,
        string? requestToken, string? identityContext)
    {
        if (!Enum.IsDefined(state) || !ServerSessionCarrierFormat.IsCanonicalToken(authActionRequestToken, 4096) ||
            (state == BrowserAuthenticationState.Authenticated
                ? !ServerSessionCarrierFormat.IsCanonicalToken(requestToken, 4096) ||
                    !ServerSessionIdentityContext.IsCanonical(identityContext)
                : requestToken is not null || identityContext is not null))
            throw new BrowserAuthenticationException(BrowserAuthenticationFailureKind.Unavailable);
        State = state;
        AuthActionRequestToken = authActionRequestToken;
        RequestToken = requestToken;
        IdentityContext = identityContext;
    }

    [JsonIgnore] public BrowserAuthenticationState State { get; }
    [JsonIgnore] public string AuthActionRequestToken { get; }
    [JsonIgnore] public string? RequestToken { get; }
    [JsonIgnore] public string? IdentityContext { get; }
    public override string ToString() => nameof(BrowserAuthenticationSession);
}
