using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Weft.Identity;

internal sealed class BrowserAuthActionAntiforgery : IDisposable
{
    internal const string CookieName = "__Host-Weft.AuthAction";
    internal const string HeaderName = "X-Weft-Auth-Token";
    private readonly Guid organization;
    private readonly ServerSessionBrowser browser;
    private readonly ServiceProvider services;
    private readonly IAntiforgery antiforgery;
    private readonly AsyncLocal<Operation?> current = new();

    internal BrowserAuthActionAntiforgery(Guid organization, DurableSessionKeyProfile profile,
        ServerSessionBrowser browser)
    {
        this.organization = organization;
        this.browser = browser;
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAntiforgery(value =>
        {
            value.HeaderName = HeaderName;
            value.SuppressReadingTokenFromFormBody = true;
            value.Cookie.Name = CookieName;
            value.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            value.Cookie.HttpOnly = true;
            value.Cookie.SameSite = SameSiteMode.Strict;
            value.Cookie.Path = "/";
            value.Cookie.Domain = null;
            value.Cookie.Expiration = null;
            value.Cookie.MaxAge = null;
            value.Cookie.IsEssential = true;
        });
        registrations.AddSingleton<IDataProtectionProvider>(new Protection(this,
            profile.CreateAuthActionAntiforgeryProvider()));
        registrations.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        registrations.AddSingleton<IAntiforgeryAdditionalDataProvider>(new AdditionalData(this));
        services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try { antiforgery = services.GetRequiredService<IAntiforgery>(); }
        catch { services.Dispose(); throw; }
    }

    internal string Issue(HttpContext actual, CancellationToken token)
    {
        using var operation = Begin(actual, "bootstrap", token);
        var cookie = ServerSessionBrowserCookies.Read(actual, CookieName, 4096);
        if (cookie.IsMalformed) throw Refused();
        try
        {
            var value = antiforgery.GetAndStoreTokens(operation.View).RequestToken;
            operation.Check();
            if (!ServerSessionCarrierFormat.IsCanonicalToken(value, 4096)) throw Unavailable();
            return value!;
        }
        catch { operation.Check(); throw; }
    }

    internal async Task<BrowserAuthActionAdmission> ValidateAsync(HttpContext actual, string action,
        CancellationToken token)
    {
        using var operation = Begin(actual, action, token);
        var cookie = ServerSessionBrowserCookies.Read(actual, CookieName, 4096);
        var header = actual.Request.Headers[HeaderName];
        if (cookie.IsMalformed || cookie.Value is null || header.Count != 1 ||
            !ServerSessionCarrierFormat.IsCanonicalToken(header[0], 4096)) throw Refused();
        try
        {
            await antiforgery.ValidateRequestAsync(operation.View);
            operation.Check();
            return new(this, browser, actual, actual.Features, action);
        }
        catch (AntiforgeryValidationException)
        {
            operation.Check();
            throw Refused();
        }
        catch { operation.Check(); throw; }
    }

    internal void Delete(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, ServerSessionBrowserCookies.AntiforgeryOptions());

    public void Dispose() => services.Dispose();

    private Operation Begin(HttpContext actual, string action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        actual.RequestAborted.ThrowIfCancellationRequested();
        if (current.Value is not null || actual.Response.HasStarted) throw Unavailable();
        var operation = new Operation(this, actual, action, token);
        current.Value = operation;
        return operation;
    }

    private Operation Require(HttpContext? view = null)
    {
        var operation = current.Value;
        if (operation is null || !ReferenceEquals(operation.Owner, this) ||
            view is not null && !ReferenceEquals(view, operation.View)) throw Unavailable();
        operation.Check();
        return operation;
    }

    private sealed class Operation : IDisposable
    {
        private readonly IFeatureCollection actualFeatures;
        private readonly CancellationToken token;
        private ServerSessionIdentityFailureKind? failure;
        private bool active = true;
        internal Operation(BrowserAuthActionAntiforgery owner, HttpContext actual, string action,
            CancellationToken token)
        {
            Owner = owner;
            Actual = actual;
            Action = action;
            actualFeatures = actual.Features;
            this.token = token;
            View = new AnonymousContext(actual, owner.services);
        }
        internal BrowserAuthActionAntiforgery Owner { get; }
        internal HttpContext Actual { get; }
        internal string Action { get; }
        internal HttpContext View { get; }
        internal void Check()
        {
            token.ThrowIfCancellationRequested();
            Actual.RequestAborted.ThrowIfCancellationRequested();
            if (!active || !ReferenceEquals(Owner.current.Value, this) ||
                !ReferenceEquals(Actual.Features, actualFeatures) || Actual.Response.HasStarted) throw Unavailable();
            if (failure is { } kind) throw ServerSessionIdentityErrors.Create(kind);
        }
        internal void Retain(ServerSessionIdentityFailureKind kind)
        {
            if (kind is not (ServerSessionIdentityFailureKind.Denied or ServerSessionIdentityFailureKind.UnsupportedFormat))
                failure ??= kind;
        }
        public void Dispose()
        {
            active = false;
            if (ReferenceEquals(Owner.current.Value, this)) Owner.current.Value = null;
        }
    }

    // Only standard AF sees this view. Actual features/User are never copied or mutated.
    private sealed class AnonymousContext(HttpContext actual, IServiceProvider services) : HttpContext
    {
        private readonly ClaimsPrincipal anonymous = new(new ClaimsIdentity());
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override HttpRequest Request => actual.Request;
        public override HttpResponse Response => actual.Response;
        public override ConnectionInfo Connection => actual.Connection;
        public override WebSocketManager WebSockets => actual.WebSockets;
        public override ClaimsPrincipal User { get => anonymous; set => throw Unavailable(); }
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
        public override IServiceProvider RequestServices { get => services; set => throw Unavailable(); }
        public override CancellationToken RequestAborted { get => actual.RequestAborted; set => throw Unavailable(); }
        public override string TraceIdentifier { get => "Weft.AuthAction"; set => throw Unavailable(); }
        public override ISession Session { get => throw Unavailable(); set => throw Unavailable(); }
        public override void Abort() => actual.Abort();
    }

    private sealed class AdditionalData(BrowserAuthActionAntiforgery owner) : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context)
        {
            owner.Require(context);
            return "weft:auth-action:1:" + owner.organization.ToString("D");
        }
        public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
            string.Equals(GetAdditionalData(context), additionalData, StringComparison.Ordinal);
    }

    private sealed class Protection(BrowserAuthActionAntiforgery owner, IDataProtectionProvider underlying)
        : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new Protector(owner, underlying.CreateProtector(purpose));
    }

    private sealed class Protector(BrowserAuthActionAntiforgery owner, IDataProtector underlying) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new Protector(owner, underlying.CreateProtector(purpose));
        public byte[] Protect(byte[] plaintext) => Transform(plaintext, false);
        public byte[] Unprotect(byte[] protectedData) => Transform(protectedData, true);
        private byte[] Transform(byte[] value, bool unprotect)
        {
            var operation = owner.Require();
            byte[]? result = null;
            try
            {
                result = unprotect ? underlying.Unprotect(value) : underlying.Protect(value);
                operation.Check();
                var output = result;
                result = null;
                return output;
            }
            catch (ServerSessionIdentityException error)
            {
                // A submitted token from another audience (or a tampered token) is
                // ordinary AF rejection. Keep actual profile/operational failures
                // latched even if standard antiforgery wraps or recovers them.
                if (!unprotect || error.Kind != ServerSessionIdentityFailureKind.IntegrityFailure)
                    operation.Retain(error.Kind);
                throw;
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
            {
                operation.Retain(ServerSessionIdentityFailureKind.Unavailable);
                throw Unavailable();
            }
            finally { if (result is not null) CryptographicOperations.ZeroMemory(result); }
        }
    }

    private static BrowserAuthenticationException Refused() => new(BrowserAuthenticationFailureKind.RequestRejected);
    private static BrowserAuthenticationException Unavailable() => new(BrowserAuthenticationFailureKind.Unavailable);
}

internal sealed class BrowserAuthActionAdmission(BrowserAuthActionAntiforgery owner, ServerSessionBrowser browser,
    HttpContext context, IFeatureCollection features, string action)
{
    internal void Require(ServerSessionBrowser actualBrowser, HttpContext actual, string actualAction)
    {
        if (owner is null || !ReferenceEquals(browser, actualBrowser) || !ReferenceEquals(context, actual) ||
            !ReferenceEquals(features, actual.Features) || !string.Equals(action, actualAction, StringComparison.Ordinal))
            throw new BrowserAuthenticationException(BrowserAuthenticationFailureKind.RequestRejected);
    }
    public override string ToString() => nameof(BrowserAuthActionAdmission);
}
