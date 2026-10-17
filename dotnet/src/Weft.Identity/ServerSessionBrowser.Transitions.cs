using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Weft.Domain;

namespace Weft.Identity;

public sealed partial class ServerSessionBrowser
{
    public async Task IssueFreshAsync(HttpContext context, Guid bindingId, long bindingVersion,
        PrincipalId verifiedOwner, DateTimeOffset verifiedDeadlineUtc, CancellationToken ct)
    {
        CheckOperation(context, ct);
        if (bindingId == Guid.Empty || bindingVersion < 1 || verifiedOwner is null || verifiedOwner.Value == Guid.Empty ||
            !ServerSessionIdentityValidation.IsDatabaseInstant(verifiedDeadlineUtc))
            throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
        var owner = new PrincipalId(verifiedOwner.Value);
        var state = ReserveTransition(context);
        using var cancellation = new BrowserCancellation(context, ct);
        try
        {
            CheckTransition(state, cancellation);
            await SettlePriorObservationAsync(state, cancellation);
            CheckTransition(state, cancellation);
            var key = ReadTransitionKey(state);
            if (key is not null)
            {
                CheckTransition(state, cancellation);
                await TicketStore.RemoveAsync(key, context, cancellation.Token);
                CheckTransition(state, cancellation);
            }
            // No new handle/epoch/scope exists before the previous revoke has been acknowledged.
            using var issuance = TicketStore.BeginTrustedIssuance(context, bindingId, bindingVersion,
                owner, verifiedDeadlineUtc, cancellation.Token);
            var ticket = issuance.CopySignInTicket();
            CheckTransition(state, cancellation);
            lock (state.Features)
            {
                RequireCurrentLocked(state);
                if (state.Transition != Transition.Reserved)
                    throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
                state.Transition = Transition.IssueReady;
                state.TransitionCancellation = cancellation;
            }
            await context.SignInAsync(ServerSessionSchemes.Issue, ticket.Principal, ticket.Properties);
            CheckTransition(state, cancellation);
            lock (state.Features)
            {
                if (state.Transition != Transition.IssueRunning)
                    throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
                state.Transition = Transition.Completed;
            }
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
        catch (ServerSessionIdentityException) { cancellation.Check(); throw; }
        catch (Exception error) when (Nonfatal(error))
        {
            cancellation.Check();
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        }
        finally { FinishTransition(state); }
    }

    public async Task RevokeLocalAsync(HttpContext context, CancellationToken ct)
    {
        CheckOperation(context, ct);
        var state = ReserveTransition(context);
        using var cancellation = new BrowserCancellation(context, ct);
        try
        {
            CheckTransition(state, cancellation);
            await SettlePriorObservationAsync(state, cancellation);
            CheckTransition(state, cancellation);
            var key = ReadTransitionKey(state);
            if (key is not null)
            {
                CheckTransition(state, cancellation);
                await TicketStore.RemoveAsync(key, context, cancellation.Token);
            }
            CheckTransition(state, cancellation);
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
        catch (OperationCanceledException) { cancellation.Check(); throw; }
        catch (ServerSessionIdentityException) { cancellation.Check(); throw; }
        catch (Exception error) when (Nonfatal(error))
        {
            cancellation.Check();
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        }
        finally { FinishTransition(state); }
    }

    private RequestState ReserveTransition(HttpContext context)
    {
        var state = GetState(context);
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None || state.TokenIssuing || (state.Admission is not null &&
                (!state.Admission.IsCompletedSuccessfully || state.AdmissionKind != ServerSessionRequestKind.Authenticated)))
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Observation is { IsCanceled: true } or { IsFaulted: true })
                state.Observation.GetAwaiter().GetResult();
            state.Transition = Transition.Reserved;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        return state;
    }

    private async Task SettlePriorObservationAsync(RequestState state, BrowserCancellation cancellation)
    {
        Task<IAuthenticationHandler>? handler;
        Task<BrowserObservation>? observation;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            handler = state.Handler;
            observation = state.Observation;
        }
        if (handler is not null)
            await handler.WaitAsync(cancellation.Token);
        if (observation is not null)
        {
            var previous = await observation.WaitAsync(cancellation.Token);
            if (previous.Kind == ServerSessionRequestKind.Unavailable)
                throw Failure(previous.FailureKind ?? ServerSessionIdentityFailureKind.Unavailable);
        }
        cancellation.Check();
        RequireCurrent(state);
    }

    private string? ReadTransitionKey(RequestState state)
    {
        var cookie = ReadSessionCookie(state);
        if (cookie.IsMalformed)
            throw Failure(ServerSessionIdentityFailureKind.Denied);
        if (cookie.Value is null)
            return null;
        var carrier = CarrierFormat.Unprotect(cookie.Value)
            ?? throw Failure(ServerSessionIdentityFailureKind.Denied);
        return ServerSessionCarrierFormat.RequireCarrier(carrier);
    }

    private void CheckTransition(RequestState state, BrowserCancellation cancellation)
    {
        cancellation.Check();
        CheckOperation(state.Context, cancellation.Token);
        RequireCurrent(state);
        if (state.Context.Response.HasStarted)
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
    }

    internal void ConsumeIssueTransition(HttpContext context)
    {
        var state = GetState(context);
        BrowserCancellation cancellation;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.IssueReady || state.TransitionCancellation is null)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            cancellation = state.TransitionCancellation;
            // This latch remains consumed even if cancellation/headers prevent the first write.
            state.Transition = Transition.IssueRunning;
        }
        CheckTransition(state, cancellation);
    }

    internal void RequireIssueAppend(HttpContext context)
    {
        var state = GetState(context);
        BrowserCancellation cancellation;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.IssueRunning || state.TransitionCancellation is null)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            cancellation = state.TransitionCancellation;
        }
        CheckTransition(state, cancellation);
    }

    private static void FinishTransition(RequestState state)
    {
        lock (state.Features)
        {
            if (state.Transition != Transition.Completed)
                state.Transition = Transition.Failed;
            state.TransitionCancellation = null;
        }
    }
}
