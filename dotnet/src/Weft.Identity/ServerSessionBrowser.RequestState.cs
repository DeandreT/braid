using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Weft.Identity;

public sealed partial class ServerSessionBrowser
{
    private RequestState GetState(HttpContext context)
    {
        var features = context.Features;
        RequestState state;
        var created = false;
        lock (features)
        {
            state = features.Get<RequestState>()!;
            if (state is null)
            {
                state = new(this, context, features);
                features.Set(state);
                created = true;
            }
            RequireCurrentLocked(state);
        }
        if (created)
        {
            try { context.Response.OnCompleted(() => CleanupRequestAsync(state)); }
            catch (Exception error) when (Nonfatal(error))
            {
                lock (features) { state.Cleared = true; }
                throw Failure(ServerSessionIdentityFailureKind.Unavailable);
            }
        }
        return state;
    }

    private static async Task CleanupRequestAsync(RequestState state)
    {
        Task? handler;
        Task? observation;
        Task? admission;
        lock (state.Features)
        {
            // Handler settles initialization and the actual framework authentication continuation.
            handler = state.Handler;
            observation = state.Observation;
            admission = state.Admission;
        }
        foreach (var task in new[] { handler, observation, admission })
        {
            if (task is null)
                continue;
            try { await task; }
            catch (Exception error) when (Nonfatal(error)) { }
        }
        lock (state.Features)
        {
            state.Cleared = true;
            state.Handler = null;
            state.Observation = null;
            state.Admission = null;
            state.CookieValue = null;
            state.TransitionCancellation = null;
        }
    }

    private void RequireCurrent(RequestState state)
    {
        lock (state.Features) { RequireCurrentLocked(state); }
    }

    private void RequireCurrentLocked(RequestState state)
    {
        if (!CurrentLocked(state))
            throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
    }

    private bool CurrentLocked(RequestState state) => ReferenceEquals(state.Owner, this) && !state.Cleared &&
        ReferenceEquals(state.Context.Features, state.Features) &&
        ReferenceEquals(state.Features.Get<RequestState>(), state);

    private ServerSessionBrowserCookies.CookieRead ReadSessionCookie(RequestState state)
    {
        RequireCurrent(state);
        var cookie = ServerSessionBrowserCookies.Read(state.Context, ServerSessionBrowserCookies.SessionName,
            ServerSessionCarrierFormat.MaximumCharacters);
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.CookieCaptured && (state.CookieMalformed != cookie.IsMalformed ||
                !string.Equals(state.CookieValue, cookie.Value, StringComparison.Ordinal)))
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            state.CookieCaptured = true;
            state.CookieMalformed = cookie.IsMalformed;
            state.CookieValue = cookie.Value;
        }
        return cookie;
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, HttpContext context, CancellationToken caller)
    {
        using var cancellation = new BrowserCancellation(context, caller);
        try
        {
            cancellation.Check();
            var result = await task.WaitAsync(cancellation.Token);
            cancellation.Check();
            return result;
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
    }

    private async Task<IAuthenticationHandler> GetMainHandlerAsync(RequestState state, CancellationToken caller)
    {
        Task<IAuthenticationHandler> task;
        TaskCompletionSource<IAuthenticationHandler>? start = null;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Handler is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                // Publish ownership before initialization or framework authentication can await.
                state.Handler = start.Task;
            }
            task = state.Handler;
        }
        if (start is not null)
            _ = CompleteHandlerAsync(state, caller, start);
        return await WaitAsync(task, state.Context, caller);
    }

    private async Task CompleteHandlerAsync(RequestState state, CancellationToken caller,
        TaskCompletionSource<IAuthenticationHandler> completion)
    {
        using var cancellation = new BrowserCancellation(state.Context, caller);
        var authenticationStarted = false;
        try
        {
            cancellation.Check();
            RequireCurrent(state);
            var provider = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<IAuthenticationHandlerProvider>(state.Context.RequestServices);
            var handler = await provider.GetHandlerAsync(state.Context, ServerSessionSchemes.Session);
            cancellation.Check();
            RequireCurrent(state);
            if (handler is not ServerSessionCookieHandler)
                throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
            authenticationStarted = true;
            // Waiters cancel only their wait. This owner drains the actual framework task.
            await handler.AuthenticateAsync();
            cancellation.Check();
            RequireCurrent(state);
            completion.TrySetResult(handler);
        }
        catch (OperationCanceledException error) { CompleteCanceled(completion, cancellation, error); }
        catch (ServerSessionIdentityException error) when (authenticationStarted)
        {
            if (!CompleteCancellationIfRequested(completion, cancellation))
                completion.TrySetException(Failure(error.Kind));
        }
        catch (Exception error) when (Nonfatal(error))
        {
            if (!CompleteCancellationIfRequested(completion, cancellation))
                completion.TrySetException(Failure(authenticationStarted ? ServerSessionIdentityFailureKind.Unavailable
                    : ServerSessionIdentityFailureKind.ConfigurationFailure));
        }
    }

    private static void CompleteCanceled<T>(TaskCompletionSource<T> completion, BrowserCancellation cancellation,
        OperationCanceledException error) => completion.TrySetCanceled(cancellation.Cause(error.CancellationToken));

    private static bool CompleteCancellationIfRequested<T>(TaskCompletionSource<T> completion,
        BrowserCancellation cancellation)
    {
        if (!cancellation.Token.IsCancellationRequested)
            return false;
        completion.TrySetCanceled(cancellation.Cause(cancellation.Token));
        return true;
    }

    private enum Transition { None, Reserved, IssueReady, IssueRunning, Completed, Failed }

    private sealed class RequestState(ServerSessionBrowser owner, HttpContext context, IFeatureCollection features)
    {
        internal ServerSessionBrowser Owner { get; } = owner;
        internal HttpContext Context { get; } = context;
        internal IFeatureCollection Features { get; } = features;
        internal Task<IAuthenticationHandler>? Handler;
        internal Task<BrowserObservation>? Observation;
        internal Task<ServerSessionRequestAdmission>? Admission;
        internal ServerSessionRequestKind? AdmissionKind;
        internal ServerSessionIdentityFailureKind? AdmissionFailureKind;
        internal ServerSessionIdentityFailureKind? ObservationRefusal;
        internal Transition Transition;
        internal BrowserCancellation? TransitionCancellation;
        internal bool CookieCaptured;
        internal bool TokenIssuing;
        internal bool CookieMalformed;
        internal string? CookieValue;
        internal bool Cleared;
        public override string ToString() => nameof(RequestState);
    }

    private sealed class BrowserCancellation : IDisposable
    {
        private readonly CancellationToken caller;
        private readonly CancellationToken request;
        private readonly CancellationTokenSource linked;

        internal BrowserCancellation(HttpContext context, CancellationToken caller)
        {
            this.caller = caller;
            request = context.RequestAborted;
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, request);
        }

        internal CancellationToken Token => linked.Token;
        internal void Check()
        {
            caller.ThrowIfCancellationRequested();
            request.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
        }

        internal CancellationToken Cause(CancellationToken fallback) => caller.IsCancellationRequested ? caller
            : request.IsCancellationRequested ? request : linked.IsCancellationRequested ? linked.Token : fallback;

        public void Dispose() => linked.Dispose();
    }

    private sealed class BrowserObservation
    {
        private readonly AuthenticationTicket? ticket;

        internal BrowserObservation(ServerSessionRequestKind kind, string? key = null,
            AuthenticationTicket? ticket = null, Guid organization = default,
            ServerSessionIdentityFailureKind? failureKind = null)
        {
            Kind = kind;
            FailureKind = failureKind;
            if (kind == ServerSessionRequestKind.Authenticated)
            {
                if (!ServerSessionHandle.TryParse(key, out _) || ticket is null)
                    throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
                Identity = ReadIdentity(ticket, organization);
                this.ticket = ticket.Clone();
                Key = key;
            }
        }

        internal ServerSessionRequestKind Kind { get; }
        internal ServerSessionIdentityFailureKind? FailureKind { get; }
        internal ServerSessionRequestIdentity? Identity { get; }
        internal string? Key { get; }
        internal AuthenticationTicket CopyTicket() => ticket?.Clone()
            ?? throw Failure(ServerSessionIdentityFailureKind.Denied);
        public override string ToString() => nameof(BrowserObservation);

        internal static ServerSessionRequestIdentity ReadIdentity(AuthenticationTicket value, Guid organization)
        {
            if (value.AuthenticationScheme != ServerSessionSchemes.Session || value.Principal.GetType() != typeof(ClaimsPrincipal))
                throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            var identities = value.Principal.Identities.Take(2).ToArray();
            if (identities.Length != 1 || identities[0].GetType() != typeof(ClaimsIdentity))
                throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            var identity = identities[0];
            var claims = identity.Claims.Take(4).ToArray();
            if (identity.AuthenticationType != ServerSessionSchemes.Session ||
                identity.NameClaimType != ClaimTypes.NameIdentifier || identity.RoleClaimType != ClaimTypes.Role ||
                identity.Actor is not null || identity.BootstrapContext is not null || identity.Label is not null ||
                claims.Length != 3)
                throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            Guid principal = default, realm = default, epoch = default;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var claim in claims)
            {
                if (claim.GetType() != typeof(Claim) || claim.ValueType != ClaimValueTypes.String ||
                    claim.Issuer != ServerSessionSchemes.Session || claim.OriginalIssuer != ServerSessionSchemes.Session ||
                    claim.Properties.Count != 0 || !ReferenceEquals(claim.Subject, identity) || !seen.Add(claim.Type) ||
                    !Guid.TryParseExact(claim.Value, "D", out var id) || id == Guid.Empty ||
                    !string.Equals(claim.Value, id.ToString("D"), StringComparison.Ordinal))
                    throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
                switch (claim.Type)
                {
                    case ClaimTypes.NameIdentifier: principal = id; break;
                    case "weft:organization_id": realm = id; break;
                    case "weft:login_epoch": epoch = id; break;
                    default: throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
                }
            }
            if (principal == Guid.Empty || realm != organization || epoch == Guid.Empty ||
                value.Properties.Parameters.Count != 0 || value.Properties.IsPersistent ||
                value.Properties.AllowRefresh != false || value.Properties.IssuedUtc is null ||
                value.Properties.ExpiresUtc is null)
                throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            var expected = new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                IssuedUtc = value.Properties.IssuedUtc,
                ExpiresUtc = value.Properties.ExpiresUtc
            };
            if (value.Properties.Items.Count != expected.Items.Count || value.Properties.Items.Any(pair =>
                !expected.Items.Any(other => string.Equals(pair.Key, other.Key, StringComparison.Ordinal) &&
                    string.Equals(pair.Value, other.Value, StringComparison.Ordinal))))
                throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            return new(new Weft.Domain.PrincipalId(principal), realm, epoch);
        }
    }
}
