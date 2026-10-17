using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weft.Application;

namespace Weft.Identity;

/// <summary>Trusted composition only; no provider verification, HTTP routes or business authority.</summary>
public sealed partial class ServerSessionBrowser : IDisposable
{
    private readonly ServerSessionBrowserOptions options;
    private readonly DurableSessionKeyProfile borrowedProfile;
    private readonly Guid organization;
    private readonly IServerSessionReader reader;
    private readonly ServerSessionTicketCodec codec;
    private readonly ServerSessionLifecycle lifecycle;
    private readonly TimeProvider schedulingClock;
    private readonly ServiceProvider antiforgeryServices;
    private readonly IAntiforgery antiforgery;
    private int disposed;

    internal ServerSessionBrowser(ServerSessionBrowserOptions options, DurableSessionKeyProfile profile,
        IServerSessionReader reader, IServerSessionWriter writer, TimeProvider schedulingClock)
    {
        if (options is null || profile is null || reader is null || writer is null || schedulingClock is null)
            throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
        this.options = options;
        borrowedProfile = profile;
        organization = profile.OrganizationId;
        this.reader = reader;
        this.schedulingClock = schedulingClock;
        codec = new(profile);
        lifecycle = new(codec, reader, writer);
        TicketStore = new(profile, reader, writer);
        CarrierFormat = new(profile.CreateCookieCarrierProtector());
        MainCookieManager = new(this, issue: false);
        IssueCookieManager = new(this, issue: true);
        CookieEvents = new();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAntiforgery(antiforgeryOptions =>
        {
            antiforgeryOptions.HeaderName = ServerSessionBrowserCookies.RequestTokenHeader;
            antiforgeryOptions.SuppressReadingTokenFromFormBody = true;
            antiforgeryOptions.Cookie.Name = ServerSessionBrowserCookies.AntiforgeryName;
            antiforgeryOptions.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgeryOptions.Cookie.HttpOnly = true;
            antiforgeryOptions.Cookie.SameSite = SameSiteMode.Strict;
            antiforgeryOptions.Cookie.Path = "/";
            antiforgeryOptions.Cookie.Domain = null;
            antiforgeryOptions.Cookie.Expiration = null;
            antiforgeryOptions.Cookie.MaxAge = null;
            antiforgeryOptions.Cookie.IsEssential = true;
        });
        // Last explicit registrations select the borrowed admitted profile and no-output logger.
        services.AddSingleton(profile.CreateAntiforgeryProvider());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IAntiforgeryAdditionalDataProvider>(new ServerSessionEpochAdditionalDataProvider(this));
        var composition = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try { antiforgery = composition.GetRequiredService<IAntiforgery>(); }
        catch (Exception error) when (Nonfatal(error))
        {
            composition.Dispose();
            throw Failure(ServerSessionIdentityFailureKind.ConfigurationFailure);
        }
        antiforgeryServices = composition;
    }

    internal ServerSessionTicketStore TicketStore { get; }
    internal ServerSessionCarrierFormat CarrierFormat { get; }
    internal ServerSessionCookieManager MainCookieManager { get; }
    internal ServerSessionCookieManager IssueCookieManager { get; }
    internal ServerSessionFixedCookieEvents CookieEvents { get; }

    public async Task<ServerSessionRequestAdmission> ObserveAsync(HttpContext context, CancellationToken ct)
    {
        RequestState? state = null;
        try
        {
            CheckOperation(context, ct);
            state = GetState(context);
            // The owned handler task includes framework authentication and awaits a separate core task.
            var work = ObserveCoreAsync(state, ct);
            try
            {
                await GetMainHandlerAsync(state, ct);
            }
            catch (Exception error) when (Nonfatal(error)) { await work; throw; }
            var observation = await work;
            CheckOperation(context, ct);
            RequireCurrent(state);
            var result = EffectiveAdmission(state, observation);
            SetUser(state, observation, result.Kind);
            return result;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            throw;
        }
        catch (ServerSessionIdentityException error)
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            RecordObservationRefusal(state, error.Kind);
            return ClosedAdmission(error.Kind);
        }
        catch (Exception error) when (Nonfatal(error))
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            RecordObservationRefusal(state, ServerSessionIdentityFailureKind.Unavailable);
            return new(ServerSessionRequestKind.Unavailable);
        }
    }

    public async Task<ServerSessionRequestAdmission> AdmitRequestAsync(HttpContext context, CancellationToken ct)
    {
        try { return await AdmitRequestCoreAsync(context, ct); }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            throw;
        }
        catch (ServerSessionIdentityException error)
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            return ClosedAdmission(error.Kind);
        }
        catch (Exception error) when (Nonfatal(error))
        {
            ct.ThrowIfCancellationRequested();
            context?.RequestAborted.ThrowIfCancellationRequested();
            return new(ServerSessionRequestKind.Unavailable);
        }
    }

    private async Task<ServerSessionRequestAdmission> AdmitRequestCoreAsync(HttpContext context, CancellationToken ct)
    {
        CheckOperation(context, ct);
        if (context.Response.HasStarted)
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        var state = GetState(context);
        Task<ServerSessionRequestAdmission> task;
        TaskCompletionSource<ServerSessionRequestAdmission>? start = null;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Admission is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Admission = start.Task;
            }
            task = state.Admission;
        }
        if (start is not null)
            _ = CompleteAdmissionAsync(state, ct, start);
        var result = await WaitAsync(task, context, ct);
        CheckOperation(context, ct);
        RequireCurrent(state);
        lock (state.Features)
        {
            if (state.Transition != Transition.None)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
        }
        return result;
    }

    internal async Task<AuthenticateResult> AuthenticateForHandlerAsync(HttpContext context)
    {
        CheckOperation(context, context.RequestAborted);
        var state = GetState(context);
        var observation = await ObserveCoreAsync(state, context.RequestAborted);
        RequireCurrent(state);
        var admission = EffectiveAdmission(state, observation);
        return admission.Kind switch
        {
            ServerSessionRequestKind.Authenticated => AuthenticateResult.Success(observation.CopyTicket()),
            ServerSessionRequestKind.Anonymous => AuthenticateResult.NoResult(),
            ServerSessionRequestKind.AuthenticationRequired => AuthenticateResult.Fail(Failure(ServerSessionIdentityFailureKind.Denied)),
            _ => AuthenticateResult.Fail(Failure(ServerSessionIdentityFailureKind.Unavailable))
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try { antiforgeryServices.Dispose(); }
            catch (Exception error) when (Nonfatal(error))
            {
                throw Failure(ServerSessionIdentityFailureKind.Unavailable);
            }
        }
    }

    public override string ToString() => nameof(ServerSessionBrowser);

    private async Task<BrowserObservation> ObserveCoreAsync(RequestState state, CancellationToken caller)
    {
        CheckOperation(state.Context, caller);
        Task<BrowserObservation> task;
        TaskCompletionSource<BrowserObservation>? start = null;
        lock (state.Features)
        {
            RequireCurrentLocked(state);
            if (state.Transition != Transition.None)
                throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
            if (state.Observation is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Observation = start.Task;
            }
            task = state.Observation;
        }
        if (start is not null)
            _ = CompleteObservationAsync(state, caller, start);
        return await WaitAsync(task, state.Context, caller);
    }

    private async Task CompleteObservationAsync(RequestState state, CancellationToken caller,
        TaskCompletionSource<BrowserObservation> completion)
    {
        using var cancellation = new BrowserCancellation(state.Context, caller);
        try
        {
            cancellation.Check();
            RequireCurrent(state);
            var cookie = ReadSessionCookie(state);
            BrowserObservation result;
            if (cookie.IsMalformed)
                result = new(ServerSessionRequestKind.AuthenticationRequired);
            else if (cookie.Value is null)
                result = new(ServerSessionRequestKind.Anonymous);
            else
            {
                var carrier = CarrierFormat.Unprotect(cookie.Value);
                if (carrier is null)
                    result = new(ServerSessionRequestKind.AuthenticationRequired);
                else
                {
                    var key = ServerSessionCarrierFormat.RequireCarrier(carrier);
                    cancellation.Check();
                    var ticket = await TicketStore.RetrieveAsync(key, state.Context, cancellation.Token);
                    cancellation.Check();
                    result = ticket is null ? new(ServerSessionRequestKind.AuthenticationRequired)
                        : new(ServerSessionRequestKind.Authenticated, key, ticket, organization);
                }
            }
            RequireCurrent(state);
            cancellation.Check();
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException error) { CompleteCanceled(completion, cancellation, error); }
        catch (ServerSessionIdentityException error)
        {
            if (CompleteCancellationIfRequested(completion, cancellation))
                return;
            completion.TrySetResult(new(error.Kind is ServerSessionIdentityFailureKind.Denied or
                ServerSessionIdentityFailureKind.UnsupportedFormat ? ServerSessionRequestKind.AuthenticationRequired
                : ServerSessionRequestKind.Unavailable, failureKind: error.Kind));
        }
        catch (Exception error) when (Nonfatal(error))
        {
            if (!CompleteCancellationIfRequested(completion, cancellation))
                completion.TrySetResult(new(ServerSessionRequestKind.Unavailable,
                    failureKind: ServerSessionIdentityFailureKind.Unavailable));
        }
    }

    private async Task CompleteAdmissionAsync(RequestState state, CancellationToken caller,
        TaskCompletionSource<ServerSessionRequestAdmission> completion)
    {
        using var cancellation = new BrowserCancellation(state.Context, caller);
        try
        {
            cancellation.Check();
            RequireCurrent(state);
            var method = state.Context.Request.Method;
            var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
            var unsafeMethod = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
                HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
            if (!safe && !unsafeMethod)
            {
                FinishAdmission(state, new(ServerSessionRequestKind.CsrfRejected), completion);
                return;
            }
            var observedAdmission = await ObserveAsync(state.Context, cancellation.Token);
            cancellation.Check();
            if (observedAdmission.Kind != ServerSessionRequestKind.Authenticated)
            {
                FinishAdmission(state, observedAdmission, completion);
                return;
            }
            var observation = await ObserveCoreAsync(state, cancellation.Token);
            var admission = EffectiveAdmission(state, observation);
            if (admission.Kind != ServerSessionRequestKind.Authenticated)
            {
                FinishAdmission(state, admission, completion);
                return;
            }
            SetUser(state, observation, admission.Kind);
            if (!safe && !await ValidateUnsafeAsync(state, cancellation))
            {
                FinishAdmission(state, new(ServerSessionRequestKind.CsrfRejected), completion);
                return;
            }
            var handle = ParseObservedHandle(observation);
            var current = await reader.ObserveAsync(new(organization, handle.Digest), cancellation.Token);
            cancellation.Check();
            RequireCurrent(state);
            if (current is null)
            {
                FinishAdmission(state, new(ServerSessionRequestKind.AuthenticationRequired), completion);
                return;
            }
            var envelope = codec.Unprotect(handle, current, cancellation.Token);
            ServerSessionCanonicalTicket.ValidateRenew(observation.CopyTicket(), current, envelope);
            var lifetime = current.Facts.Lifetime;
            var threshold = Math.Max(0, lifetime.IdleExpiresAtUtc.Ticks - TimeSpan.FromMinutes(10).Ticks);
            if (lifetime.IdleExpiresAtUtc < lifetime.AbsoluteExpiresAtUtc &&
                schedulingClock.GetUtcNow().UtcTicks >= threshold)
            {
                CheckTransition(state, cancellation);
                var renewal = await lifecycle.RenewAsync(handle,
                    ServerSessionCanonicalTicket.CreateCurrent(current, envelope), cancellation.Token);
                cancellation.Check();
                var renewed = new BrowserObservation(ServerSessionRequestKind.Authenticated,
                    observation.Key, renewal.CopyTicket(), organization);
                if (!SameIdentity(observation.Identity, renewed.Identity))
                    throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
            }
            cancellation.Check();
            FinishAdmission(state, new(ServerSessionRequestKind.Authenticated, observation.Identity), completion);
        }
        catch (OperationCanceledException error) { CompleteCanceled(completion, cancellation, error); }
        catch (ServerSessionIdentityException error)
        {
            if (CompleteCancellationIfRequested(completion, cancellation))
                return;
            lock (state.Features) { state.AdmissionFailureKind = error.Kind; }
            FinishAdmission(state, new(error.Kind == ServerSessionIdentityFailureKind.Denied
                ? ServerSessionRequestKind.AuthenticationRequired : ServerSessionRequestKind.Unavailable), completion);
        }
        catch (Exception error) when (Nonfatal(error))
        {
            if (!CompleteCancellationIfRequested(completion, cancellation))
                FinishAdmission(state, new(ServerSessionRequestKind.Unavailable), completion);
        }
    }

    private void FinishAdmission(RequestState state, ServerSessionRequestAdmission admission,
        TaskCompletionSource<ServerSessionRequestAdmission> completion)
    {
        lock (state.Features)
        {
            if (!CurrentLocked(state) || state.Transition != Transition.None)
            {
                completion.TrySetException(Failure(ServerSessionIdentityFailureKind.UntrustedIssuance));
                return;
            }
            if (state.Transition == Transition.None)
            {
                state.AdmissionKind = admission.Kind;
                if (admission.Kind != ServerSessionRequestKind.Authenticated)
                    state.Context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }
        completion.TrySetResult(admission);
    }

    private ServerSessionRequestAdmission EffectiveAdmission(RequestState state, BrowserObservation observation)
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
                return ClosedAdmission(refusal);
            if (state.AdmissionKind is { } kind && kind != ServerSessionRequestKind.Authenticated)
                return new(kind);
            return new(observation.Kind, observation.Identity);
        }
    }

    private void CheckOperation(HttpContext context, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (context is null)
            throw Failure(ServerSessionIdentityFailureKind.ContextRequired);
        context.RequestAborted.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0)
            throw Failure(ServerSessionIdentityFailureKind.Unavailable);
        if (!context.Request.IsHttps || !string.Equals(context.Request.Scheme, "https", StringComparison.Ordinal) ||
            !string.Equals(context.Request.Host.Value, options.Authority, StringComparison.Ordinal))
            throw Failure(ServerSessionIdentityFailureKind.Denied);
        if (context.Features.Get<ITlsTokenBindingFeature>()?.GetProvidedTokenBindingId() is not null)
            throw Failure(ServerSessionIdentityFailureKind.UnsupportedFormat);
    }

    private void RecordObservationRefusal(RequestState? state, ServerSessionIdentityFailureKind kind)
    {
        if (state is null)
            return;
        lock (state.Features)
        {
            if (!CurrentLocked(state) || state.Transition != Transition.None)
                return;
            state.ObservationRefusal ??= kind;
            state.Context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    private void SetUser(RequestState state, BrowserObservation observation, ServerSessionRequestKind kind)
    {
        lock (state.Features)
        {
            RequireReadingStateLocked(state);
            state.Context.User = kind == ServerSessionRequestKind.Authenticated ? observation.CopyTicket().Principal
                : new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    private void RequireReadingStateLocked(RequestState state)
    {
        RequireCurrentLocked(state);
        if (state.Transition != Transition.None)
            throw Failure(ServerSessionIdentityFailureKind.UntrustedIssuance);
    }

    private static bool SameIdentity(ServerSessionRequestIdentity? first, ServerSessionRequestIdentity? second) =>
        first is not null && second is not null && first.PrincipalId == second.PrincipalId &&
        first.OrganizationId == second.OrganizationId && first.LoginEpoch == second.LoginEpoch;

    private static ServerSessionHandle ParseObservedHandle(BrowserObservation observation)
    {
        if (!ServerSessionHandle.TryParse(observation.Key, out var handle))
            throw Failure(ServerSessionIdentityFailureKind.IntegrityFailure);
        return handle;
    }

    private static bool Nonfatal(Exception error) => error is not (OutOfMemoryException or StackOverflowException);
    private static ServerSessionRequestAdmission ClosedAdmission(ServerSessionIdentityFailureKind kind) => new(
        kind is ServerSessionIdentityFailureKind.Denied or ServerSessionIdentityFailureKind.UnsupportedFormat
            ? ServerSessionRequestKind.AuthenticationRequired
            : ServerSessionRequestKind.Unavailable);
    private static ServerSessionIdentityException Failure(ServerSessionIdentityFailureKind kind) =>
        ServerSessionIdentityErrors.Create(kind);
}
