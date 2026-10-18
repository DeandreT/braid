using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostTransportTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task DirectTlsRejectsHeaderAuthorityAndForwardingBeforeAWithheldBody() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var host = test.Start(); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            foreach (var headers in new KeyValuePair<string, string>[][] {
                [new("X-Forwarded-Proto", "https")], [new("Forwarded", "proto=https")], [new("X-Original-Host", "unadmitted.invalid")] })
            {
                var reply = await ServerHostRaw.SendAsync(test.Origin, test.Files.TlsCertificate, test.Origin.Authority, headers, withheldBody: true);
                ServerHostCheck.That(reply.Status == 403 && reply.Bytes.Length == 0,
                    "Direct TLS forwarding refusal consumed the withheld body or returned a nonfixed response.");
                ServerHostHttp.PrivateHeaders(reply);
            }
            var wrong = await ServerHostRaw.SendAsync(test.Origin, test.Files.TlsCertificate, "unadmitted.invalid", [], withheldBody: true);
            ServerHostCheck.That(wrong.Status == 403 && wrong.Bytes.Length == 0,
                "Direct TLS accepted an unadmitted Host authority.");
            test.PositiveStatus(await test.StatusAsync(client));
            ServerHostCheck.That(host.OwnedIpv4Listeners().Count == 1, "Session acquired a second application listener.");
            await host.StopAsync();
        });

    [PostgresFact]
    public Task OwnedHttpsFrontProxyStripsClientFieldsAndBackendRequiresExactActualPeerAndOneHop() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
            int backendPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var environment = test.Environment(proxy: true, backendPort); reservation.Stop();
            var host = test.Start(environment);
            Uri backend = await host.WaitForLoopbackListenerAsync();
            ServerHostCheck.That(backend.Port == backendPort, "The proxy backend changed its fixed configured endpoint.");
            await using var edge = await ServerHostHttpsEdge.StartAsync(test.Origin, backendPort, test.Files.TlsCertificate);
            test.RequireExternalSettlement(() => edge.Settled);
            using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            test.PositiveContext(await test.ContextAsync(client));
            var stripped = await ServerHostHttp.SendAsync(client, "/api/auth/session", headers: new Dictionary<string, string>
            {
                ["X-Weft-Session"] = "1",
                ["Cookie"] = test.Cookies.Header,
                ["Forwarded"] = "for=unadmitted.invalid",
                ["X-Forwarded-Host"] = "unadmitted.invalid",
                ["X-Forwarded-Proto"] = "http",
                ["X-Original-Host"] = "unadmitted.invalid"
            });
            test.PositiveStatus(stripped);
            KeyValuePair<string, string>[] good = [new("X-Forwarded-For", "127.0.0.1"), new("X-Forwarded-Proto", "https"),
                new("X-Forwarded-Host", test.Origin.Authority), new("X-Weft-Session", "1")];
            var peer = await ServerHostRaw.SendAsync(backend, null, test.Origin.Authority, good, IPAddress.Parse("127.0.0.2"), withheldBody: true);
            ServerHostCheck.That(peer.Status == 403 && peer.Bytes.Length == 0,
                "Backend admitted an actual untrusted source peer based on forwarding fields.");
            foreach (var fields in new KeyValuePair<string, string>[][] {
                [], [.. good, new("X-Forwarded-For", "127.0.0.2")],
                [new("X-Forwarded-For", "127.0.0.1, 127.0.0.2"), new("X-Forwarded-Proto", "https"), new("X-Forwarded-Host", test.Origin.Authority)],
                [new("X-Forwarded-For", "unadmitted.invalid"), new("X-Forwarded-Proto", "https"), new("X-Forwarded-Host", test.Origin.Authority)],
                [new("X-Forwarded-For", "127.0.0.1"), new("X-Forwarded-Proto", "http"), new("X-Forwarded-Host", test.Origin.Authority)],
                [.. good, new("Forwarded", "proto=https")], [.. good, new("X-Forwarded-Prefix", "/unadmitted")] })
            {
                var denied = await ServerHostRaw.SendAsync(backend, null, test.Origin.Authority, fields, IPAddress.Loopback, withheldBody: true);
                ServerHostCheck.That(denied.Status == 403 && denied.Bytes.Length == 0,
                    "Backend accepted a missing, malformed, chained or original forwarding shape.");
                ServerHostHttp.PrivateHeaders(denied);
            }
            test.PositiveContext(await test.ContextAsync(client));
            await edge.DisposeAsync(); ServerHostCheck.That(edge.Settled, "The owned HTTPS edge did not settle.");
            await host.StopAsync();
        });
}
