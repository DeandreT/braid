using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Braid.Identity;

internal sealed class PrivateEntraHandler : OpenIdConnectHandler
{
    private readonly IOptionsMonitor<OpenIdConnectOptions> registered;
    internal EntraBrowserClient Owner { get; }

    public PrivateEntraHandler(EntraBrowserClient owner, IOptionsMonitor<OpenIdConnectOptions> registered,
        HtmlEncoder htmlEncoder, UrlEncoder encoder)
        : base(owner.FixedMonitor, EntraSilentLogger.Instance, htmlEncoder, encoder)
    { Owner = owner; this.registered = registered; }

    public override Task<bool> HandleRequestAsync()
    {
        if (!Owner.IsCallbackOperation(Context)) return Task.FromResult(false);
        Validate();
        return base.HandleRequestAsync();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Owner.RequireOperation().Callback)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        Validate();
        return base.HandleChallengeAsync(properties);
    }

    public override Task SignOutAsync(AuthenticationProperties? properties) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    private void Validate()
    {
        Owner.RequireOperation().CheckActive();
        Owner.ValidateOptions(Options);
        Owner.ValidateOptions(registered.Get(EntraBrowserClient.Scheme));
    }
}

internal sealed class EntraRefusingSignInHandler : AuthenticationHandler<AuthenticationSchemeOptions>,
    IAuthenticationSignInHandler, IAuthenticationSignOutHandler
{
    public EntraRefusingSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, UrlEncoder encoder)
        : base(options, EntraSilentLogger.Instance, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    public Task SignInAsync(System.Security.Claims.ClaimsPrincipal user, AuthenticationProperties? properties) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
    public Task SignOutAsync(AuthenticationProperties? properties) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
}

internal sealed class EntraSilentLogger : ILoggerFactory, ILogger
{
    internal static readonly EntraSilentLogger Instance = new();
    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    { }
    public void Dispose() { }
}
