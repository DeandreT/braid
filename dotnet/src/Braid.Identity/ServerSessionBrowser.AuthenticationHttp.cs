using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Braid.Identity;

public sealed partial class ServerSessionBrowser
{
    // Read the binding from fresh protected server facts, never from a friendly name or a new ticket claim.
    internal async Task<Guid> ReadDisplayBindingAsync(HttpContext context, ServerSessionRequestIdentity identity,
        CancellationToken ct)
    {
        CheckOperation(context, ct);
        var state = GetState(context);
        var observation = await ObserveCoreAsync(state, ct);
        if (observation.Kind != ServerSessionRequestKind.Authenticated || !SameIdentity(observation.Identity, identity))
            throw Failure(ServerSessionIdentityFailureKind.Denied);
        var handle = ParseObservedHandle(observation);
        var current = await reader.ObserveAsync(new(organization, handle.Digest), ct)
            ?? throw Failure(ServerSessionIdentityFailureKind.Denied);
        var envelope = codec.Unprotect(handle, current, ct);
        ServerSessionCanonicalTicket.ValidateRenew(observation.CopyTicket(), current, envelope);
        CheckOperation(context, ct);
        RequireCurrent(state);
        return current.Facts.BindingId;
    }

    internal bool UsesAuthenticationProfile(DurableSessionKeyProfile profile, Guid expectedOrganization,
        string expectedOrigin) => ReferenceEquals(profile, borrowedProfile) && organization == expectedOrganization &&
        string.Equals(options.ExpectedOrigin, expectedOrigin, StringComparison.Ordinal);

    // Join actual stored owners even when a public WaitAsync already canceled. Do not clear/reset state.
    internal async Task DrainAuthenticationRequestAsync(HttpContext context)
    {
        RequestState? state;
        lock (context.Features)
        {
            state = context.Features.Get<RequestState>();
            if (state is null) return;
            if (!ReferenceEquals(state.Owner, this) || !ReferenceEquals(state.Context, context) ||
                !ReferenceEquals(state.Features, context.Features))
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
        }
        var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        ExceptionDispatchInfo? first = null;
        while (true)
        {
            Task[] tasks;
            lock (state.Features)
            {
                tasks = new Task?[] { state.Handler, state.Observation, state.Admission }
                    .OfType<Task>().Where(joined.Add).ToArray();
            }
            if (tasks.Length == 0) break;
            foreach (var task in tasks)
                try { await task; }
                catch (Exception error) when (Nonfatal(error)) { first ??= ExceptionDispatchInfo.Capture(error); }
        }
        first?.Throw();
    }

    internal async Task ForgetDeniedLocalAsync(HttpContext context, BrowserAuthActionAdmission proof,
        CancellationToken ct)
    {
        CheckOperation(context, ct);
        proof.Require(this, context, "forget-local");
        await DrainAuthenticationRequestAsync(context);
        CheckOperation(context, ct);
        var state = GetState(context);
        lock (state.Features)
        {
            RequireReadingStateLocked(state);
            if (state.TokenIssuing || state.ObservationRefusal is not null ||
                state.AdmissionFailureKind is not null || state.AdmissionKind is not null &&
                    state.AdmissionKind != ServerSessionRequestKind.Authenticated)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Observation is { IsCompletedSuccessfully: true } prior &&
                prior.Result.FailureKind is not null)
                throw Failure(prior.Result.FailureKind.Value);
        }

        // Only this exact carrier classifier's Denied, before reservation/writer work, is eligible.
        var carrierHeader = context.Request.Headers.Cookie.ToArray();
        var denied = false;
        try { _ = ReadTransitionKey(state); }
        catch (ServerSessionIdentityException error) when (error.Kind == ServerSessionIdentityFailureKind.Denied)
        { denied = true; }
        CheckOperation(context, ct);
        if (!carrierHeader.SequenceEqual(context.Request.Headers.Cookie.ToArray()))
            throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
        if (!denied)
            throw new BrowserAuthenticationException(BrowserAuthenticationFailureKind.ForgetNotAvailable);
        proof.Require(this, context, "forget-local");
        if (!ReferenceEquals(ReserveTransition(context), state))
            throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
        using var cancellation = new BrowserCancellation(context, ct);
        try
        {
            CheckTransition(state, cancellation);
            // ReadSessionCookie enforces the identical captured carrier/header selection again.
            _ = ReadSessionCookie(state);
            if (!carrierHeader.SequenceEqual(context.Request.Headers.Cookie.ToArray()))
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            context.Response.Cookies.Delete(ServerSessionBrowserCookies.SessionName,
                ServerSessionBrowserCookies.SessionOptions());
            CheckTransition(state, cancellation);
            context.Response.Cookies.Delete(ServerSessionBrowserCookies.AntiforgeryName,
                ServerSessionBrowserCookies.AntiforgeryOptions());
            CheckTransition(state, cancellation);
            lock (state.Features)
            {
                RequireCurrentLocked(state);
                state.Transition = Transition.Completed;
            }
        }
        finally { FinishTransition(state); }
    }
}
