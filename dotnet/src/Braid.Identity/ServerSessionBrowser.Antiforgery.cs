using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Braid.Identity;

public sealed partial class ServerSessionBrowser
{
    public async Task<string> IssueRequestTokenAsync(HttpContext context, CancellationToken ct)
    {
        CheckOperation(context, ct);
        var state = GetState(context);
        using var cancellation = new BrowserCancellation(context, ct);
        try
        {
            lock (state.Features)
            {
                RequireCurrentLocked(state);
                if (state.Transition != Transition.None)
                    throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            }
            CheckTransition(state, cancellation);
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
                !HttpMethods.IsOptions(context.Request.Method))
                throw Failure(ServerSessionIdentityFailureKind.Denied);
            var admission = await ObserveAsync(context, cancellation.Token);
            cancellation.Check();
            if (admission.Kind != ServerSessionRequestKind.Authenticated)
                throw Failure(ObservationFailureKind(state) ?? (admission.Kind == ServerSessionRequestKind.Unavailable
                    ? ServerSessionIdentityFailureKind.Unavailable : ServerSessionIdentityFailureKind.Denied));
            var observation = RequirePositiveObservation(state);
            var cookie = ServerSessionBrowserCookies.Read(context, ServerSessionBrowserCookies.AntiforgeryName,
                ServerSessionBrowserCookies.MaximumRequestTokenCharacters);
            if (cookie.IsMalformed)
                throw Failure(ServerSessionIdentityFailureKind.Denied);
            SetUser(state, observation, ServerSessionRequestKind.Authenticated);
            CheckTransition(state, cancellation);
            lock (state.Features)
            {
                RequireReadingStateLocked(state);
                if (state.TokenIssuing)
                    throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
                state.TokenIssuing = true;
            }
            try
            {
                var token = antiforgery.GetAndStoreTokens(context).RequestToken;
                CheckTransition(state, cancellation);
                lock (state.Features) { RequireReadingStateLocked(state); }
                if (!ServerSessionCarrierFormat.IsCanonicalToken(token, ServerSessionBrowserCookies.MaximumRequestTokenCharacters))
                    throw Failure(ServerSessionIdentityFailureKind.Unavailable);
                return token!;
            }
            finally { lock (state.Features) { state.TokenIssuing = false; } }
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
        catch (ServerSessionIdentityException) { cancellation.Check(); throw; }
        catch (Exception error) when (Nonfatal(error))
        {
            cancellation.Check();
            if (WrappedCancellation(error) is { } canceled)
                throw canceled;
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    private async Task<bool> ValidateUnsafeAsync(RequestState state, BrowserCancellation cancellation)
    {
        cancellation.Check();
        RequireCurrent(state);
        var context = state.Context;
        var origins = context.Request.Headers.Origin;
        var tokens = context.Request.Headers[ServerSessionBrowserCookies.RequestTokenHeader];
        if (origins.Count != 1 || !string.Equals(origins[0], options.ExpectedOrigin, StringComparison.Ordinal) ||
            tokens.Count != 1 || !ServerSessionCarrierFormat.IsCanonicalToken(tokens[0],
                ServerSessionBrowserCookies.MaximumRequestTokenCharacters))
            return false;
        var cookie = ServerSessionBrowserCookies.Read(context, ServerSessionBrowserCookies.AntiforgeryName,
            ServerSessionBrowserCookies.MaximumRequestTokenCharacters);
        if (cookie.IsMalformed || cookie.Value is null)
            return false;
        var observation = RequirePositiveObservation(state);
        SetUser(state, observation, ServerSessionRequestKind.Authenticated);
        try
        {
            cancellation.Check();
            await antiforgery.ValidateRequestAsync(context);
            cancellation.Check();
            lock (state.Features) { RequireReadingStateLocked(state); }
            return true;
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
        catch (AntiforgeryValidationException error)
        {
            cancellation.Check();
            if (WrappedCancellation(error) is { } canceled)
                throw canceled;
            if (WrappedUnavailable(error))
                throw Failure(ServerSessionIdentityFailureKind.Unavailable);
            return false;
        }
    }

    private BrowserObservation RequirePositiveObservation(RequestState state)
    {
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Handler is { IsCanceled: true } or { IsFaulted: true })
                state.Handler.GetAwaiter().GetResult();
            if (state.Admission is { IsCanceled: true } or { IsFaulted: true })
                state.Admission.GetAwaiter().GetResult();
            if (state.ObservationRefusal is { } refusal)
                throw Failure(refusal);
            if (state.AdmissionKind is { } admission && admission != ServerSessionRequestKind.Authenticated)
                throw Failure(state.AdmissionFailureKind ?? (admission == ServerSessionRequestKind.Unavailable
                    ? ServerSessionIdentityFailureKind.Unavailable : ServerSessionIdentityFailureKind.Denied));
            if (state.Observation is null || !state.Observation.IsCompleted)
                throw Failure(ServerSessionIdentityFailureKind.Denied);
            // Completed cancellation/failure is preserved, never replaced with a new observation.
            var observation = state.Observation.GetAwaiter().GetResult();
            if (observation.Kind != ServerSessionRequestKind.Authenticated)
                throw Failure(observation.FailureKind ?? (observation.Kind == ServerSessionRequestKind.Unavailable
                    ? ServerSessionIdentityFailureKind.Unavailable : ServerSessionIdentityFailureKind.Denied));
            return observation;
        }
    }

    private ServerSessionIdentityFailureKind? ObservationFailureKind(RequestState state)
    {
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None)
                return ServerSessionIdentityFailureKind.UntrustedIssuance;
            if (state.AdmissionFailureKind is { } admissionFailure)
                return admissionFailure;
            if (state.ObservationRefusal is { } refusal)
                return refusal;
            return state.Observation is { IsCompletedSuccessfully: true }
                ? state.Observation.GetAwaiter().GetResult().FailureKind : null;
        }
    }

    private ServerSessionRequestIdentity RequireEpochIdentity(HttpContext context)
    {
        CheckOperation(context, context.RequestAborted);
        var state = GetState(context);
        lock (state.Features)
        {
            var observation = RequirePositiveObservation(state);
            var ticket = observation.CopyTicket();
            var userTicket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(context.User,
                ticket.Properties, ServerSessionSchemes.Session);
            var user = BrowserObservation.ReadIdentity(userTicket, organization);
            if (!SameIdentity(user, observation.Identity))
                throw Failure(ServerSessionIdentityFailureKind.Denied);
            return user;
        }
    }

    private static OperationCanceledException? WrappedCancellation(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            if (current is OperationCanceledException canceled)
                return canceled;
        return null;
    }

    private static bool WrappedUnavailable(Exception error)
    {
        Exception? current = error;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            if (current is ServerSessionIdentityException closed && closed.Kind is
                ServerSessionIdentityFailureKind.Unavailable or ServerSessionIdentityFailureKind.ConfigurationFailure)
                return true;
        return false;
    }

    private static string Expected(HttpContext context, ServerSessionRequestIdentity identity) =>
        context.Features.Get<ServerSessionBusinessScope>() is { } scope &&
            scope.PrincipalId == identity.PrincipalId.Value && scope.LoginEpoch == identity.LoginEpoch
            ? "braid:business-csrf:2:" + identity.OrganizationId.ToString("D") + ":" + identity.LoginEpoch.ToString("D") +
                ":" + scope.OrganizationId.ToString("D") + ":" + scope.AdmissionVersion.ToString("D")
            : "braid:csrf:1:" + identity.OrganizationId.ToString("D") + ":" + identity.LoginEpoch.ToString("D");

    private sealed class ServerSessionEpochAdditionalDataProvider(ServerSessionBrowser browser)
        : IAntiforgeryAdditionalDataProvider
    {
        public string GetAdditionalData(HttpContext context)
        {
            var identity = browser.RequireEpochIdentity(context);
            return Expected(context, identity);
        }

        public bool ValidateAdditionalData(HttpContext context, string additionalData)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            try
            {
                var identity = browser.RequireEpochIdentity(context);
                var expected = Expected(context, identity);
                return additionalData is { Length: >= 30 and <= 160 } && string.Equals(additionalData, expected, StringComparison.Ordinal);
            }
            catch (ServerSessionIdentityException error) when (error.Kind is
                ServerSessionIdentityFailureKind.Denied or ServerSessionIdentityFailureKind.UntrustedIssuance or
                ServerSessionIdentityFailureKind.IntegrityFailure or ServerSessionIdentityFailureKind.UnsupportedFormat)
            {
                return false;
            }
        }
    }
}
