using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Weft.Identity;
using Weft.Tests;

namespace Weft.HostPrivacyProbe;

/// <summary>Component integration only: actual116 owned transport meets malformed bytes from a test TLS peer.</summary>
internal static class HostMalformedProviderScenario
{
    internal static async Task<int> RunAsync()
    {
        int total = 0;
        foreach (bool malformedStatus in new[] { true, false })
        {
            var crypto = new EntraProtocolCrypto();
            var files = new SessionTicketQualificationRoot();
            Weft.Identity.DurableSessionKeyProfile? profile = null; ServiceProvider? services = null;
            EntraBrowserClient? client = null; Task? clientDispose = null, providerDispose = null;
            var peer = new MalformedProviderPeer(crypto, malformedStatus); HostProbeFailure? primary = null;
            string category = malformedStatus ? "malformed-provider-status" : "malformed-provider-header";
            bool settled = false;
            try
            {
                using (DurableSessionKeyProfile.BootstrapEmpty(files.Options(EntraProtocolFixturePolicy.Organization), files.Certificate, [],
                    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
                profile = DurableSessionKeyProfile.Open(files.Options(EntraProtocolFixturePolicy.Organization), files.Certificate, []);
                var descriptors = new ServiceCollection(); descriptors.AddLogging();
                EntraClientRegistration.AddForHttpsFixture(descriptors,
                    new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, "https://127.0.0.1:59431"), profile,
                    crypto.ClientCertificate, new EntraHttpsFixtureTransport(peer.Port, crypto.Root));
                services = descriptors.BuildServiceProvider(); client = services.GetRequiredService<EntraBrowserClient>();
                using var scope = services.CreateScope(); using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = budget.Token };
                context.Request.Method = "POST"; context.Request.Scheme = "https"; context.Request.Host = new("127.0.0.1", 59431);
                context.Request.Path = "/api/auth/login"; context.Response.Body = new MemoryStream();
                bool failed = false;
                try { _ = await client.BeginLoginAsync(context, budget.Token); }
                catch (ServerSessionIdentityException) { failed = true; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { failed = true; }
                await peer.FirstReplyWritten.Task.WaitAsync(budget.Token);
                if (!failed || peer.Written == 0 || peer.Unexpected != 0) throw new HostProbeFailure(category);
                total += peer.Written;
                context.Response.Body.Dispose();
            }
            catch (Exception error) { primary = error as HostProbeFailure ?? new HostProbeFailure(category); }
            finally
            {
                bool clientSettled = client is null, providerSettled = services is null;
                if (client is not null)
                {
                    clientDispose ??= client.DisposeAsync().AsTask();
                    try { await clientDispose.WaitAsync(TimeSpan.FromSeconds(10)); clientSettled = true; } catch (Exception error) { primary ??= error as HostProbeFailure ?? new HostProbeFailure("malformed-provider-cleanup"); }
                }
                if (clientSettled && services is not null)
                {
                    providerDispose ??= services.DisposeAsync().AsTask();
                    try { await providerDispose.WaitAsync(TimeSpan.FromSeconds(10)); providerSettled = true; } catch (Exception error) { primary ??= error as HostProbeFailure ?? new HostProbeFailure("malformed-provider-cleanup"); }
                }
                try { await peer.DisposeAsync(); } catch (Exception error) { primary ??= error as HostProbeFailure ?? new HostProbeFailure("malformed-provider-cleanup"); }
                settled = clientSettled && providerSettled && peer.Settled;
                if (settled) { profile?.Dispose(); files.Dispose(); crypto.Dispose(); }
                // Borrowed root/certificates are retained on unproved transport/module settlement.
            }
            if (!settled || primary is not null) throw primary ?? new HostProbeFailure("malformed-provider-cleanup");
        }
        return total;
    }
}

internal sealed class MalformedProviderPeer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
    private readonly EntraProtocolCrypto crypto;
    private readonly bool malformedStatus;
    private readonly List<Task> connections = [];
    private readonly object sync = new();
    private readonly Task accepting;
    private Task? disposal;
    internal int Port { get; }
    internal int Written, Unexpected;
    internal TaskCompletionSource FirstReplyWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Settled { get; private set; }

    internal MalformedProviderPeer(EntraProtocolCrypto crypto, bool malformedStatus)
    {
        this.crypto = crypto; this.malformedStatus = malformedStatus;
        listener.Start(8); Port = ((IPEndPoint)listener.LocalEndpoint).Port; accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var caller = await listener.AcceptTcpClientAsync(lifetime.Token);
                lock (sync)
                {
                    if (connections.Count >= 32) { caller.Dispose(); Unexpected++; throw new HostProbeFailure("malformed-provider-bound"); }
                    connections.Add(ServeAsync(caller));
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient caller)
    {
        bool written = false;
        using (caller)
        await using (var tls = new SslStream(caller.GetStream(), leaveInnerStreamOpen: false))
        {
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = crypto.IssuerTls }, lifetime.Token);
                using var request = new MemoryStream(); var buffer = new byte[1024];
                while (true)
                {
                    int count = await tls.ReadAsync(buffer, lifetime.Token); if (count == 0) throw new HostProbeFailure("malformed-provider-eof");
                    if (request.Length + count > 16_384) throw new HostProbeFailure("malformed-provider-bound"); request.Write(buffer, 0, count);
                    string value = Encoding.ASCII.GetString(request.ToArray());
                    if (value.Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (!value.StartsWith("GET " + EntraProtocolFixturePolicy.Discovery + " HTTP/1.1\r\n", StringComparison.Ordinal))
                        { Interlocked.Increment(ref Unexpected); throw new HostProbeFailure("malformed-provider-request"); }
                        break;
                    }
                }
                string reply = malformedStatus ? "HTTP/1.1 XYZ " + Program.Marker + "\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\n" + Program.Marker + "\r\nConnection: close\r\n\r\n";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(reply), lifetime.Token); await tls.FlushAsync(lifetime.Token);
                written = true; Interlocked.Increment(ref Written); FirstReplyWritten.TrySetResult();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (written) { }
            catch (Exception error) { Interlocked.Increment(ref Unexpected); throw error as HostProbeFailure ?? new HostProbeFailure("malformed-provider-peer"); }
        }
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel(); listener.Stop();
        await accepting.WaitAsync(TimeSpan.FromSeconds(10));
        Task[] pending; lock (sync) pending = connections.ToArray();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        Settled = accepting.IsCompleted && pending.All(task => task.IsCompleted); lifetime.Dispose();
    }
}
