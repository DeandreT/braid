using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Weft.Identity;

namespace Weft.Server;

/// <summary>Retains original shutdown tasks. A timed-out wait never proves the work stopped.</summary>
internal sealed class SessionHostLifetime(IHost host, SessionRequestGate requests, SessionHostResources resources)
{
    private static readonly TimeSpan SettlementLimit = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private ServerSessionBrowser? browser;
    private EntraBrowserClient? entra;
    private EntraBrowserAuthentication? authentication;
    private Task? stopping, requestDrain, authDisposal, entraDisposal, hostDisposal, resourceDisposal, closing;
    private bool browserDisposed;

    internal void ResolveBeforeStart()
    {
        browser = host.Services.GetRequiredService<ServerSessionBrowser>();
        entra = host.Services.GetRequiredService<EntraBrowserClient>();
        authentication = host.Services.GetRequiredService<EntraBrowserAuthentication>();
        var value = host.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        if (value.DefaultScheme is not null || value.DefaultAuthenticateScheme is not null ||
            value.DefaultChallengeScheme is not null || value.DefaultForbidScheme is not null ||
            value.DefaultSignInScheme is not null || value.DefaultSignOutScheme is not null)
            throw ServerHostOptions.Failure();
    }

    internal Task CloseAsync()
    {
        lock (sync) return closing ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        var failed = false;
        var requestsFinished = requestDrain ??= requests.CloseAndDrain();
        // Start each independently and retain the exact first task, including synchronous failure.
        var stopFinished = stopping ??= Begin(() => host.StopAsync(CancellationToken.None));
        var authOwner = authentication;
        var entraOwner = entra;
        var authFinished = authDisposal ??= authOwner is null ? Task.CompletedTask : Begin(() => authOwner.DisposeAsync().AsTask());
        var entraFinished = entraDisposal ??= entraOwner is null ? Task.CompletedTask : Begin(() => entraOwner.DisposeAsync().AsTask());
        foreach (var task in new[] { stopFinished, requestsFinished, authFinished, entraFinished })
            if (!await SettledSuccessfully(task)) failed = true;
        if (!stopFinished.IsCompleted || !requestsFinished.IsCompletedSuccessfully ||
            !authFinished.IsCompletedSuccessfully || !entraFinished.IsCompletedSuccessfully)
        {
            // A settled Stop failure remains a failure, but is not an unfinished request/module task.
            // Faulted module drains do not certify their dependent work, even when the task is complete.
            RetainedUnsettledHosts.Keep(this);
            throw new InvalidOperationException("The host shutdown could not be confirmed.");
        }
        if (browser is not null)
        {
            try { browser.Dispose(); browserDisposed = true; }
            catch (Exception error) when (SessionHostResources.Nonfatal(error)) { failed = true; }
        }
        // Request/module settlement makes the first provider disposal safe to attempt even if
        // StopAsync or the browser's synchronous cleanup failed. Never substitute another task.
        var providerFinished = hostDisposal ??= Begin(async () =>
        {
            if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            else host.Dispose();
        });
        if (!await SettledSuccessfully(providerFinished)) failed = true;
        if (!providerFinished.IsCompletedSuccessfully || browser is not null && !browserDisposed)
        {
            RetainedUnsettledHosts.Keep(this);
            throw new InvalidOperationException("The host shutdown could not be confirmed.");
        }
        var resourcesFinished = resourceDisposal ??= Begin(resources.DisposeAfterSettlementAsync);
        if (!await SettledSuccessfully(resourcesFinished))
        {
            RetainedUnsettledHosts.Keep(this);
            throw new InvalidOperationException("The host resources did not close cleanly.");
        }
        if (failed) throw new InvalidOperationException("The host shutdown could not be confirmed.");
    }

    private static Task Begin(Func<Task> action)
    {
        Task task;
        try { task = action(); }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { task = Task.FromException(error); }
        _ = task.ContinueWith(value => { _ = value.Exception; }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private static async Task<bool> SettledSuccessfully(Task original)
    {
        try { await original.WaitAsync(SettlementLimit); return true; }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return false; }
    }

    // Retention is deliberate: no finalizer or second disposal can certify the first unfinished operation.
    private static class RetainedUnsettledHosts
    {
        private static readonly List<SessionHostLifetime> Owners = [];
        internal static void Keep(SessionHostLifetime owner) { lock (Owners) Owners.Add(owner); }
    }
}
