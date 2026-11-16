using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Braid.Application;

namespace Braid.Identity;

/// <summary>Dormant durable ticket adapter. Composition does not activate authentication.</summary>
public sealed partial class ServerSessionTicketStore : ITicketStore
{
    private readonly ServerSessionTicketCodec codec;
    private readonly ServerSessionLifecycle lifecycle;
    private readonly object origin = new();

    public ServerSessionTicketStore(DurableSessionKeyProfile profile, IServerSessionReader reader,
        IServerSessionWriter writer)
    {
        codec = new(profile);
        lifecycle = new(codec, reader, writer);
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket) => Refuse<string>();
    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        Refuse<string>(cancellationToken);
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => Refuse<AuthenticationTicket?>();
    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) =>
        Refuse<AuthenticationTicket?>(cancellationToken);
    public Task RenewAsync(string key, AuthenticationTicket ticket) => Refuse();
    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        Refuse(cancellationToken);
    public Task RemoveAsync(string key) => Refuse();
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => Refuse(cancellationToken);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (httpContext is null)
            RequireContext(httpContext, cancellationToken);
        (IssuanceEntry Entry, ServerSessionHandle Handle, ServerSessionEnvelope Envelope,
            CancellationToken Token) issuance;
        try { issuance = ConsumeIssuance(httpContext); }
        catch (ServerSessionIdentityException)
        {
            // Refusal has already latched this request; cancellation retains its original cause.
            RequireContext(httpContext, cancellationToken);
            throw;
        }
        using var cancellation = new OperationCancellation(httpContext, cancellationToken, issuance.Token);
        try
        {
            cancellation.Check();
            var facts = await lifecycle.CreateAsync(issuance.Handle, issuance.Envelope, ticket, cancellation.Token);
            ServerSessionCanonicalTicket.ValidateIssue(ticket, issuance.Envelope);
            ServerSessionCanonicalTicket.ApplyCreatedTimes(ticket, facts);
            cancellation.Check();
            return issuance.Handle.ExportKey();
        }
        catch (OperationCanceledException)
        {
            cancellation.Check();
            throw;
        }
        finally { ClearIssuance(issuance.Entry); }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        RequireContext(httpContext, cancellationToken);
        using var cancellation = new OperationCancellation(httpContext, cancellationToken);
        try
        {
            cancellation.Check();
            if (!ServerSessionHandle.TryParse(key, out var handle))
            {
                cancellation.Check();
                return null;
            }
            var ticket = await lifecycle.RetrieveAsync(handle!, cancellation.Token);
            cancellation.Check();
            return ticket;
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        RequireContext(httpContext, cancellationToken);
        using var cancellation = new OperationCancellation(httpContext, cancellationToken);
        try
        {
            cancellation.Check();
            if (!ServerSessionHandle.TryParse(key, out var handle))
            {
                cancellation.Check();
                throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.UnsupportedFormat);
            }
            await lifecycle.RenewAsync(handle!, ticket, cancellation.Token);
            cancellation.Check();
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
    }

    public async Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        RequireContext(httpContext, cancellationToken);
        using var cancellation = new OperationCancellation(httpContext, cancellationToken);
        try
        {
            cancellation.Check();
            if (!ServerSessionHandle.TryParse(key, out var handle))
            {
                cancellation.Check();
                return;
            }
            await lifecycle.RevokeAsync(handle!, cancellation.Token);
            cancellation.Check();
        }
        catch (OperationCanceledException) { cancellation.Check(); throw; }
    }

    private static void RequireContext([NotNull] HttpContext? context, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (context is null)
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ContextRequired);
        context.RequestAborted.ThrowIfCancellationRequested();
    }

    private static Task<T> Refuse<T>(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ContextRequired);
    }

    private static Task Refuse(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ContextRequired);
    }

    private sealed class OperationCancellation : IDisposable
    {
        private readonly CancellationToken caller;
        private readonly CancellationToken request;
        private readonly CancellationToken issuance;
        private readonly CancellationTokenSource linked;

        internal OperationCancellation(HttpContext context, CancellationToken caller,
            CancellationToken issuance = default)
        {
            this.caller = caller;
            request = context.RequestAborted;
            this.issuance = issuance;
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, request, issuance);
        }

        internal CancellationToken Token => linked.Token;
        internal void Check()
        {
            caller.ThrowIfCancellationRequested();
            request.ThrowIfCancellationRequested();
            issuance.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
        }

        public void Dispose() => linked.Dispose();
    }
}
