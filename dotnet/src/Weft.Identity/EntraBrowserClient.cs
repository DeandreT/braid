using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Identity;

public sealed partial class EntraBrowserClient : IAsyncDisposable
{
    internal const string Scheme = "Weft.Entra";
    internal const string Sink = "Weft.Entra.RefuseSignIn";
    internal const string CallbackPath = "/api/auth/callback/entra";
    internal const string NoncePrefix = "__Host-Weft.Entra.Nonce.";
    internal const string CorrelationPrefix = "__Host-Weft.Entra.Correlation.";
    internal static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    internal static readonly string[] Scopes = ["openid", "profile"];
    private readonly EntraClientOptions options;
    private readonly DurableSessionKeyProfile borrowedProfile;
    private readonly Guid organization;
    private readonly X509Certificate2 certificate;
    private readonly EntraEndpointPolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly EntraOwnedWork requests = new();
    private readonly EntraBackchannel backchannel;
    private readonly EntraMetadata metadata;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> manager;
    private readonly EntraStrictTokenHandler tokenHandler;
    private readonly IDataProtectionProvider flowProtection;
    private readonly OpenIdConnectEvents events;
    private readonly OpenIdConnectProtocolValidator protocol;
    private readonly OpenIdConnectOptions fixedOptions;
    private readonly ConditionalWeakTable<OpenIdConnectOptions, ExpectedFormats> formats = new();
    private readonly Dictionary<string, string> protocolHashes;
    private readonly AsyncLocal<Operation?> current = new();
    private readonly object disposeSync = new();
    private Task? disposal;

    internal EntraBrowserClient(EntraClientOptions options, DurableSessionKeyProfile profile,
        X509Certificate2 certificate, EntraHttpsFixtureTransport? fixture)
    {
        this.options = options;
        borrowedProfile = profile;
        organization = profile.OrganizationId;
        this.certificate = certificate;
        RequireCertificate();
        policy = new(options);
        backchannel = new(policy, lifetime.Token, fixture);
        try
        {
            metadata = new(policy, lifetime.Token);
            manager = new(policy.Discovery, metadata, backchannel, metadata);
            tokenHandler = new(policy, manager, metadata, () => RequireOperation().EffectiveCancellation);
            flowProtection = new EntraFlowProtection(profile.CreateEntraFlowProvider(options.TenantId, options.ClientId),
                RequireOperation);
            protocol = new OpenIdConnectProtocolValidator
            {
                RequireNonce = true,
                RequireStateValidation = false,
                RequireState = true,
                RequireSub = true,
                RequireTimeStampInNonce = true,
                NonceLifetime = FlowLifetime
            };
            protocolHashes = new(protocol.HashAlgorithmMap, StringComparer.Ordinal);
            events = CreateEvents();
            fixedOptions = new();
            ConfigureNamedOptions(fixedOptions);
            new OpenIdConnectPostConfigureOptions(flowProtection).PostConfigure(Scheme, fixedOptions);
            RememberFormats(fixedOptions);
            fixedOptions.Validate(Scheme);
            FixedMonitor = new FixedOptions(fixedOptions);
        }
        catch
        {
            backchannel.Client.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    internal IOptionsMonitor<OpenIdConnectOptions> FixedMonitor { get; }

    public Task<EntraLoginChallenge> BeginLoginAsync(HttpContext context, CancellationToken cancellationToken) =>
        BeginLoginAsync(context, BrowserReturnPath.Root, cancellationToken);

    public Task<EntraLoginChallenge> BeginLoginAsync(HttpContext context, string returnPath,
        CancellationToken cancellationToken) => BeginLoginAsync(context, returnPath, null, cancellationToken);

    internal async Task<EntraLoginChallenge> BeginLoginAsync(HttpContext context, string returnPath, string? invitation,
        CancellationToken cancellationToken)
    {
        // The return path travels in the protected state and is checked again on the callback.
        if (returnPath != BrowserReturnPath.Root && !BrowserReturnPath.IsValid(returnPath))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        var operation = await RunAsync(context, callback: false, cancellationToken, returnPath, invitation);
        return operation.Challenge ?? throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
    }

    public async Task<EntraVerifiedLogin> VerifyCallbackAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var operation = await RunAsync(context, callback: true, cancellationToken);
        if (operation.Verified is not { } result || !result.HasProvenance(this, context, operation))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        return result;
    }

    private async Task<Operation> RunAsync(HttpContext context, bool callback, CancellationToken caller,
        string returnPath = BrowserReturnPath.Root, string? invitation = null)
    {
        if (context is null) throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        var originalAbort = context.RequestAborted;
        originalAbort.ThrowIfCancellationRequested();
        caller.ThrowIfCancellationRequested();
        using var lease = requests.Enter();
        RequireRequest(context, callback);
        RequestMarker marker;
        lock (context)
        {
            if (context.Features.Get<RequestMarker>() is not null)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
            marker = new(this);
            context.Features.Set(marker);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalAbort, caller, lifetime.Token);
        var operation = new Operation(this, context, callback, originalAbort, caller, cancellation.Token);
        marker.Active = operation; // Publish ownership before the first framework await.
        var originalFeature = context.Features.Get<IResponseCookiesFeature>();
        var originalRequestCookies = context.Features.Get<IRequestCookiesFeature>();
        EntraResponseCookies? guardedCookies = null;
        context.RequestAborted = cancellation.Token;
        current.Value = operation;
        var previousSignal = EntraFailureScope.Current.Value;
        EntraFailureScope.Current.Value = operation.Signal;
        try
        {
            var cookies = context.Response.Cookies;
            originalFeature = context.Features.Get<IResponseCookiesFeature>();
            guardedCookies = new EntraResponseCookies(operation, cookies);
            context.Features.Set<IResponseCookiesFeature>(guardedCookies);
            var requestCookies = context.Request.Cookies;
            originalRequestCookies = context.Features.Get<IRequestCookiesFeature>();
            context.Features.Set<IRequestCookiesFeature>(new EntraRequestCookies(operation, requestCookies));
            AuthenticationOptions registration;
            try { registration = context.RequestServices.GetRequiredService<IOptions<AuthenticationOptions>>().Value; }
            catch (Exception error) when (EntraErrors.Recoverable(error))
            { throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure); }
            RequireAuthenticationRegistration(registration);
            var handler = await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>()
                .GetHandlerAsync(context, Scheme);
            operation.CheckActive();
            if (handler is not PrivateEntraHandler actual || !ReferenceEquals(actual.Owner, this))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            if (callback)
            {
                if (!await actual.HandleRequestAsync())
                    operation.Refuse(ServerSessionIdentityFailureKind.Denied);
            }
            else
            {
                var now = WholeSecondNow();
                var properties = new AuthenticationProperties
                {
                    RedirectUri = returnPath,
                    IsPersistent = false,
                    IssuedUtc = now,
                    ExpiresUtc = now.Add(FlowLifetime)
                };
                properties.Items[".weft.flow"] = "1";
                if (invitation is not null)
                {
                    if (!Weft.Application.OrganizationInvitationSecret.IsValid(invitation))
                        throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
                    // Standard protected state binds this credential to the exact correlation,
                    // nonce, tenant and client. A separate mutable cookie cannot substitute it.
                    properties.Items[".weft.invitation"] = invitation;
                }
                await actual.ChallengeAsync(properties);
                operation.CheckActive();
                var location = context.Response.Headers.Location.ToString();
                if (context.Response.StatusCode != StatusCodes.Status302Found ||
                    Encoding.UTF8.GetByteCount(location) > EntraEndpointPolicy.MaximumProtocolBytes ||
                    !Uri.TryCreate(location, UriKind.Absolute, out var destination) ||
                    destination.GetLeftPart(UriPartial.Path) != policy.Authorize || destination.Fragment.Length != 0)
                    throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
                context.Response.Headers.Remove("Location");
                context.Response.StatusCode = StatusCodes.Status200OK;
                operation.Challenge = new(location);
            }
            operation.CheckActive();
            if (callback && operation.Verified is null)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            return operation;
        }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        {
            if (context.Response.HasStarted) operation.Refuse(ServerSessionIdentityFailureKind.Unavailable);
            guardedCookies?.Cleanup();
            if (!context.Response.HasStarted) context.Response.Headers.Remove("Location");
            operation.ThrowOriginalCancellation();
            if (operation.Failure is { } failure) throw EntraErrors.Create(failure);
            if (error is ServerSessionIdentityException closed) throw EntraErrors.Create(closed.Kind);
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
        finally
        {
            // The actual handler continuation has settled before releasing request state.
            operation.ClearCandidates();
            operation.Active = false;
            marker.Active = null; // Retain used marker to reject same-request reuse.
            current.Value = null;
            EntraFailureScope.Current.Value = previousSignal;
            context.RequestAborted = originalAbort;
            context.Features.Set(originalFeature);
            context.Features.Set(originalRequestCookies);
        }
    }

    private void RequireRequest(HttpContext context, bool callback)
    {
        if (context.Response.HasStarted)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        var request = context.Request;
        if (!request.IsHttps || request.PathBase.HasValue ||
            "https://" + request.Host.ToUriComponent() != options.PublicOrigin ||
            request.Path != (callback ? CallbackPath : "/api/auth/login") ||
            (callback ? !HttpMethods.IsGet(request.Method) : !HttpMethods.IsPost(request.Method)) ||
            request.ContentLength is > 0 || request.Headers.TransferEncoding.Count != 0 ||
            Encoding.UTF8.GetByteCount(request.Headers.Cookie.ToString()) > EntraEndpointPolicy.MaximumProtocolBytes)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        var nonces = request.Cookies.Keys.Count(name => name.StartsWith(NoncePrefix, StringComparison.Ordinal));
        var correlations = request.Cookies.Keys.Count(name => name.StartsWith(CorrelationPrefix, StringComparison.Ordinal));
        if (nonces > 4 || correlations > 4 || !callback && (nonces >= 4 || correlations >= 4))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        if (callback)
        {
            if (Encoding.UTF8.GetByteCount(request.QueryString.Value ?? "") > EntraEndpointPolicy.MaximumProtocolBytes)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            foreach (var field in request.Query)
                if (field.Value.Count != 1 || field.Key is not
                    ("code" or "state" or "session_state" or "client_info" or "iss" or
                     "error" or "error_description" or "error_uri"))
                    throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            if (request.Query["state"].Count != 1 ||
                !(request.Query["code"].Count == 1 ^ request.Query["error"].Count == 1))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        }
        else if (request.QueryString.HasValue)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
    }

    internal Operation RequireOperation()
    {
        var operation = current.Value;
        if (operation is null || !ReferenceEquals(operation.Owner, this) ||
            !ReferenceEquals(operation.Context.Features.Get<RequestMarker>()?.Active, operation))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        operation.CheckActive();
        return operation;
    }

    internal bool IsCallbackOperation(HttpContext context) =>
        context.Features.Get<RequestMarker>() is { Active: { Callback: true } operation } marker &&
        ReferenceEquals(marker.Owner, this) && ReferenceEquals(operation, current.Value);

    private void RequireCertificate()
    {
        try
        {
            var now = DateTime.UtcNow;
            if (certificate is null || !certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now ||
                certificate.NotAfter.ToUniversalTime() <= now)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is null || rsa.KeySize < 2048)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        { throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure); }
    }

    private static DateTimeOffset WholeSecondNow() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    internal static bool IsBoundNonceCookieName(string name) => name.Length is > 0 and <= 4096 &&
        name.StartsWith(NoncePrefix, StringComparison.Ordinal) && name.Length > NoncePrefix.Length &&
        name.Skip(NoncePrefix.Length).All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public ValueTask DisposeAsync()
    {
        lock (disposeSync) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var requestDrain = requests.CloseAndDrain();
        var retrievalDrain = metadata.CloseAndDrainAsync();
        var sendDrain = backchannel.CloseAndDrainAsync();
        var cancellationFailed = false;
        try { await lifetime.CancelAsync(); }
        catch (Exception error) when (EntraErrors.Recoverable(error)) { cancellationFailed = true; }
        await Task.WhenAll(requestDrain, retrievalDrain, sendDrain);
        backchannel.Client.Dispose();
        lifetime.Dispose();
        if (cancellationFailed) throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        // Standard manager's hidden memory-only validate/update continuation is not
        // joinable. Closed gates exclude HTTP; validator uses managed policy/config only.
    }

    public override string ToString() => nameof(EntraBrowserClient);

    private sealed class FixedOptions(OpenIdConnectOptions value) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => value;
        public OpenIdConnectOptions Get(string? name) => name == Scheme ? value :
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }

    private sealed record ExpectedFormats(ISecureDataFormat<AuthenticationProperties> State,
        ISecureDataFormat<string> Nonce);

    private void RememberFormats(OpenIdConnectOptions value) =>
        formats.Add(value, new(value.StateDataFormat, value.StringDataFormat));

    private sealed class RequestMarker(EntraBrowserClient owner)
    { internal EntraBrowserClient Owner { get; } = owner; internal Operation? Active; }

    internal sealed class Operation(EntraBrowserClient owner, HttpContext context, bool callback,
        CancellationToken request, CancellationToken caller, CancellationToken effective)
    {
        internal EntraBrowserClient Owner { get; } = owner;
        internal HttpContext Context { get; } = context;
        internal bool Callback { get; } = callback;
        internal CancellationToken EffectiveCancellation { get; } = effective;
        internal bool Active = true;
        internal readonly EntraFailureSignal Signal = new();
        internal ServerSessionIdentityFailureKind? Failure => Signal.Kind;
        internal EntraLoginChallenge? Challenge;
        internal EntraVerifiedLogin? Verified;
        internal Candidate? Candidate;
        internal string? CorrelationName;
        internal string? NonceName;

        internal void ThrowOriginalCancellation()
        { request.ThrowIfCancellationRequested(); caller.ThrowIfCancellationRequested(); }
        internal void CheckActive()
        {
            ThrowOriginalCancellation();
            if (!Active || EffectiveCancellation.IsCancellationRequested || Context.Response.HasStarted)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            if (Failure is { } failure) throw EntraErrors.Create(failure);
        }
        internal void Refuse(ServerSessionIdentityFailureKind kind) => Signal.Refuse(kind);
        internal void ClearCandidates() => Candidate = null;
    }
}
