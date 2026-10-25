using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weft.Application;
using Weft.Contracts.Auth;
using Weft.Domain;

namespace Weft.Identity;

public sealed class EntraBrowserAuthentication : IAsyncDisposable
{
    private readonly BrowserAuthenticationOptions options;
    private readonly IExternalIdentityReader identities;
    private readonly IAccountDisplayMetadata? displayMetadata;
    private readonly ServerSessionBrowser browser;
    private readonly EntraBrowserClient entra;
    private readonly IOrganizationInvitationRedeemer? invitations;
    private readonly BrowserAuthActionAntiforgery antiforgery;
    private readonly EntraOwnedWork methods = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalSync = new();
    private Task? disposal;

    internal EntraBrowserAuthentication(BrowserAuthenticationOptions options, DurableSessionKeyProfile profile,
        IExternalIdentityReader identities, ServerSessionBrowser browser, EntraBrowserClient entra,
        IAccountDisplayMetadata? displayMetadata = null, IOrganizationInvitationRedeemer? invitations = null)
    {
        if (!browser.UsesAuthenticationProfile(profile, options.OrganizationId, options.ExpectedOrigin) ||
            !entra.UsesAuthenticationProfile(profile, options.OrganizationId, options.ExpectedOrigin))
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
        this.options = options;
        this.identities = identities;
        this.displayMetadata = displayMetadata;
        this.browser = browser;
        this.entra = entra;
        this.invitations = invitations;
        antiforgery = new(options.OrganizationId, profile, browser);
    }

    public Task<BrowserAuthenticationSession> ReadSessionAsync(HttpContext context, CancellationToken ct) =>
        Observe(RunAsync(context, "bootstrap", ct, async token =>
        {
            var observed = await ObserveSessionAsync(context, token);
            string? requestToken = null, identityContext = null, displayName = null;
            var state = observed.Kind switch
            {
                ServerSessionRequestKind.Anonymous => BrowserAuthenticationState.Anonymous,
                ServerSessionRequestKind.AuthenticationRequired => BrowserAuthenticationState.AuthenticationRequired,
                ServerSessionRequestKind.Authenticated => BrowserAuthenticationState.Authenticated,
                _ => throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable)
            };
            if (observed.Identity is { } identity)
            {
                requestToken = await browser.IssueRequestTokenAsync(context, token);
                identityContext = ServerSessionIdentityContext.Derive(identity);
                if (displayMetadata is not null)
                {
                    var binding = await browser.ReadDisplayBindingAsync(context, identity, token);
                    displayName = AccountDisplayName.Optional(await displayMetadata.ReadAsync(
                        identity.OrganizationId, binding, token));
                }
            }
            var authActionToken = antiforgery.Issue(context, token);
            return new BrowserAuthenticationSession(state, authActionToken, requestToken, identityContext, displayName);
        }));

    public Task<EntraLoginChallenge> BeginLoginAsync(HttpContext context, CancellationToken ct) =>
        Observe(RunAsync(context, "login", ct, async token =>
        {
            _ = await antiforgery.ValidateAsync(context, "login", token);
            await AdmitPositiveSessionAsync(context, token);
            var headers = context.Request.Headers["X-Weft-Invitation"];
            if (headers.Count > 1 || headers.Count == 1 && (invitations is null || !OrganizationInvitationSecret.IsValid(headers[0])))
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.InvalidRequest);
            return await entra.BeginLoginAsync(context, BrowserAuthenticationRequest.ReturnPath(context),
                headers.Count == 1 ? headers[0] : null, token);
        }));

    /// <summary>Completes sign-in and returns the validated application path it returns to.</summary>
    public Task<string> CompleteLoginAsync(HttpContext context, CancellationToken ct) =>
        Observe(RunAsync(context, "callback", ct, async token =>
        {
            var verified = await entra.VerifyCallbackAsync(context, token);
            token.ThrowIfCancellationRequested();
            if (verified.OrganizationId != options.OrganizationId)
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
            if (verified.InvitationSecret is { } invitation && (invitations is null ||
                !await invitations.RedeemAsync(options.OrganizationId, invitation, verified.ExternalIdentity, token)))
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.AuthenticationRequired);
            var resolution = await identities.ResolveAsync(verified.ExternalIdentity, token);
            token.ThrowIfCancellationRequested();
            if (resolution.Outcome != ExternalIdentityResolutionOutcome.Resolved)
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.AuthenticationRequired);
            var binding = resolution.Binding;
            if (binding is null || !binding.Enabled || binding.Key != verified.ExternalIdentity ||
                binding.BindingId == Guid.Empty || binding.Version < 1 || binding.PrincipalId.Value == Guid.Empty)
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
            if (displayMetadata is not null)
                await displayMetadata.ObserveAsync(options.OrganizationId, binding.BindingId,
                    verified.DisplayName, verified.ObservedAtUtc, token);
            await browser.IssueFreshAsync(context, binding.BindingId, binding.Version,
                new PrincipalId(binding.PrincipalId.Value), verified.VerifiedDeadlineUtc, token);
            return verified.ReturnPath;
        }));

    public Task LogoutAsync(HttpContext context, CancellationToken ct) =>
        Observe(RunAsync(context, "logout", ct, async token =>
        {
            _ = await antiforgery.ValidateAsync(context, "logout", token);
            await AdmitPositiveSessionAsync(context, token);
            await browser.RevokeLocalAsync(context, token);
            token.ThrowIfCancellationRequested();
            antiforgery.Delete(context);
            return true;
        }));

    public Task<BrowserForgetLocalOutcome> ForgetLocalAsync(HttpContext context, CancellationToken ct) =>
        Observe(RunAsync(context, "forget-local", ct, async token =>
        {
            var proof = await antiforgery.ValidateAsync(context, "forget-local", token);
            await browser.ForgetDeniedLocalAsync(context, proof, token);
            token.ThrowIfCancellationRequested();
            antiforgery.Delete(context);
            return BrowserForgetLocalOutcome.RevocationUnconfirmed;
        }));

    private async Task<ServerSessionRequestAdmission> ObserveSessionAsync(HttpContext context, CancellationToken ct)
    {
        var observed = await browser.ObserveAsync(context, ct);
        ct.ThrowIfCancellationRequested();
        if (observed.Kind == ServerSessionRequestKind.Unavailable ||
            observed.Kind == ServerSessionRequestKind.Authenticated && (observed.Identity is not { } identity ||
                identity.OrganizationId != options.OrganizationId || identity.PrincipalId.Value == Guid.Empty ||
                identity.LoginEpoch == Guid.Empty))
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
        return observed;
    }

    private async Task AdmitPositiveSessionAsync(HttpContext context, CancellationToken ct)
    {
        var observed = await ObserveSessionAsync(context, ct);
        if (observed.Kind != ServerSessionRequestKind.Authenticated) return;
        var admitted = await browser.AdmitRequestAsync(context, ct);
        ct.ThrowIfCancellationRequested();
        if (admitted.Kind == ServerSessionRequestKind.CsrfRejected)
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.RequestRejected);
        if (admitted.Kind is ServerSessionRequestKind.Anonymous or ServerSessionRequestKind.AuthenticationRequired)
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.AuthenticationRequired);
        if (admitted.Kind != ServerSessionRequestKind.Authenticated || admitted.Identity is not { } actual ||
            actual.OrganizationId != observed.Identity!.OrganizationId || actual.PrincipalId != observed.Identity.PrincipalId ||
            actual.LoginEpoch != observed.Identity.LoginEpoch)
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
    }

    private async Task<T> RunAsync<T>(HttpContext context, string action, CancellationToken caller,
        Func<CancellationToken, Task<T>> body)
    {
        if (context is null) throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.InvalidRequest);
        caller.ThrowIfCancellationRequested();
        var requestAbort = context.RequestAborted;
        requestAbort.ThrowIfCancellationRequested();
        using var lease = methods.Enter();
        var lifetimeToken = lifetime.Token;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, requestAbort, lifetimeToken);
        ExceptionDispatchInfo? primary = null;
        var features = context.Features;
        T result;
        try
        {
            BrowserAuthenticationRequest.Require(context, options, action);
            lock (features)
            {
                if (features.Get<RequestUse>() is not null)
                    throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.RequestRejected);
                features.Set(new RequestUse(this, context, features));
            }
            result = await body(cancellation.Token);
            caller.ThrowIfCancellationRequested();
            requestAbort.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(features, context.Features) || context.Response.HasStarted)
                throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
        }
        catch (Exception error) when (Recoverable(error))
        {
            primary = ExceptionDispatchInfo.Capture(error);
            caller.ThrowIfCancellationRequested();
            requestAbort.ThrowIfCancellationRequested();
            throw Map(error);
        }
        finally
        {
            try { await browser.DrainAuthenticationRequestAsync(context); }
            catch (Exception error) when (Recoverable(error))
            {
                if (primary is null)
                {
                    caller.ThrowIfCancellationRequested();
                    requestAbort.ThrowIfCancellationRequested();
                    throw Map(error);
                }
            }
        }
        // Draining can await work started on this request with an independent
        // token. Cancellation during that join must not publish the saved result.
        caller.ThrowIfCancellationRequested();
        requestAbort.ThrowIfCancellationRequested();
        lifetimeToken.ThrowIfCancellationRequested();
        cancellation.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(features, context.Features) || context.Response.HasStarted)
            throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
        return result;
    }

    private static Task<T> Observe<T>(Task<T> work)
    {
        _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        var drain = methods.CloseAndDrain();
        ExceptionDispatchInfo? failure = null;
        try { await lifetime.CancelAsync(); }
        catch (Exception error) when (Recoverable(error)) { failure = ExceptionDispatchInfo.Capture(error); }
        await drain;
        try { antiforgery.Dispose(); }
        catch (Exception error) when (Recoverable(error)) { failure ??= ExceptionDispatchInfo.Capture(error); }
        lifetime.Dispose();
        if (failure is not null) throw BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable);
    }

    private static Exception Map(Exception error) => error switch
    {
        OperationCanceledException => error,
        BrowserAuthenticationException => error,
        ServerSessionIdentityException { Kind: ServerSessionIdentityFailureKind.Denied } =>
            BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.AuthenticationRequired),
        _ => BrowserAuthenticationRequest.Error(BrowserAuthenticationFailureKind.Unavailable)
    };
    private static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException);
    public override string ToString() => nameof(EntraBrowserAuthentication);
    private sealed class RequestUse(EntraBrowserAuthentication owner, HttpContext context, IFeatureCollection features)
    {
        internal EntraBrowserAuthentication Owner { get; } = owner;
        internal HttpContext Context { get; } = context;
        internal IFeatureCollection Features { get; } = features;
    }
}
