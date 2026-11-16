using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Braid.Application;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ServerHostPublishedTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PublishedDefaultAndExplicitDisabledKeepPortZeroAndClosedModeStatus()
    {
        foreach (string? mode in new string?[] { null, "disabled" })
        {
            var environment = new Dictionary<string, string?>
            {
                ["BRAID_DEV_LISTEN_ADDR"] = "127.0.0.1:0",
                ["BRAID_CATALOG_AUTH_MODE"] = mode,
                ["BRAID_SESSION_KEY_DIRECTORY"] = "/nonexistent-owned-disabled-sentinel",
                ["BRAID_ENTRA_CLIENT_PRIVATE_KEY_FILE"] = "/nonexistent-owned-disabled-key"
            };
            if (mode == "disabled") { environment["DOTNET_ENVIRONMENT"] = "Development"; environment["ASPNETCORE_ENVIRONMENT"] = "Development"; }
            await using var host = ServerHostProcess.Start(environment);
            Uri origin = await host.WaitForLoopbackListenerAsync(); using var client = ServerHostHttp.Client(origin);
            ServerHostHttp.Mode(await ServerHostHttp.StartupStatusAsync(client), "disabled");
            // Disabled mode admits the actual listener origin after the dynamic socket binds.
            await ServerHostBrowserSecurity.CheckAsync(client);
            ServerHostHttp.Mode(await ServerHostHttp.SendAsync(client, "/api/auth/session", headers: new Dictionary<string, string> { ["X-Braid-Session"] = "1" }), "disabled");
            var health = await ServerHostHttp.SendAsync(client, "/api/v1/health");
            using var healthy = health.Json();
            ServerHostCheck.That(health.Status == 200 && healthy.RootElement.GetProperty("status").GetString() == "ok",
                "The published disabled shell health did not complete.");
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/v1/unmapped-owned-host")).Status == 404,
                "An unknown API route became an SPA response.");
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/auth/login", HttpMethod.Post, body: [])).Status == 404,
                "Disabled mode mapped a session authentication operation.");
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/v1/catalog/context")).Status == 404,
                "Disabled mode admitted the catalog.");
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/v1/examples/mappings")).Status == 404,
                "The disabled host exposed the removed examples API.");
            ServerHostCheck.That(host.OwnedIpv4Listeners().Count == 1, "The disabled host acquired an additional listener.");
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("SESSION", "true")]
    [InlineData("session ", "true")]
    [InlineData("disabled", "true")]
    [InlineData("development", "false")]
    [InlineData("session", "false")]
    [InlineData("owned-private-mode-marker", "false")]
    public async Task PublishedModeContradictionsFailBeforeListening(string mode, string catalog)
    {
        await using var host = ServerHostProcess.Start(new Dictionary<string, string?>
        { ["BRAID_CATALOG_AUTH_MODE"] = mode, ["BRAID_CATALOG_ENABLED"] = catalog, ["BRAID_DEV_LISTEN_ADDR"] = "127.0.0.1:0" });
        ServerHostCheck.That(await host.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
            "An invalid mode configuration reported successful startup.");
        await host.AssertPrivateOutputAsync(["owned-private-mode-marker"]);
    }

    [PostgresFact]
    public Task AbsentModeRetainsActualLegacyDevelopmentContextAndNoSessionFingerprint() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(1);
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            var origin = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            var environment = new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["BRAID_DEV_LISTEN_ADDR"] = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
                ["BRAID_CATALOG_ENABLED"] = "true",
                ["BRAID_DEV_IDENTITY_ENABLED"] = "true",
                ["BRAID_DEV_PRINCIPAL_ID"] = test.Data.Actor.PrincipalId.Value.ToString("D"),
                ["BRAID_DEV_ORGANIZATION_ID"] = test.Data.Project.OrganizationId.ToString("D"),
                ["BRAID_DEV_BROWSER_ORIGIN"] = origin.GetLeftPart(UriPartial.Authority),
                ["BRAID_CATALOG_CONNECTION_STRING"] = test.Data.Case.CatalogConnectionString,
                ["BRAID_SESSION_KEY_DIRECTORY"] = "/nonexistent-owned-development-sentinel"
            };
            reservation.Stop(); var host = test.Start(environment); using var client = ServerHostHttp.Client(origin);
            ServerHostHttp.Mode(await ServerHostHttp.StartupStatusAsync(client), "development");
            await ServerHostBrowserSecurity.CheckAsync(client);
            var context = await ServerHostHttp.SendAsync(client, "/api/v1/catalog/context");
            using var body = context.Json(); var value = body.RootElement;
            ServerHostCheck.That(context.Status == 200 && value.GetProperty("mode").GetString() == "development"
                && value.GetProperty("actorPrincipalId").GetString() == test.Data.Actor.PrincipalId.Value.ToString("D")
                && value.GetProperty("allowedOrganizationId").GetString() == test.Data.Project.OrganizationId.ToString("D")
                && !value.TryGetProperty("identityContext", out _) && value.GetProperty("requestToken").GetString() is { Length: > 0 },
                "Legacy Development catalog JSON or token behavior changed.");
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/v1/examples/mappings")).Status == 404,
                "The Development host exposed the removed examples API.");
            await host.StopAsync();
        });

    [PostgresFact]
    public Task SessionStartupAndAnonymousStatusDoNotOpenDeliveredRestrictedPoolsButRealCarrierUsesItsReader() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var reader = test.OwnNetwork(new ServerHostConnectionWitness()); var writer = test.OwnNetwork(new ServerHostConnectionWitness());
            var identity = test.OwnNetwork(new ServerHostConnectionWitness()); var catalog = test.OwnNetwork(new ServerHostConnectionWitness());
            var environment = test.Environment();
            environment["BRAID_SESSION_READER_CONNECTION_STRING_FILE"] = test.Files.Text("watched-reader.txt", reader.Route(test.Data.Case.SessionReaderConnectionString));
            environment["BRAID_SESSION_WRITER_CONNECTION_STRING_FILE"] = test.Files.Text("watched-writer.txt", writer.Route(test.Data.Case.SessionWriterConnectionString));
            environment["BRAID_IDENTITY_READER_CONNECTION_STRING_FILE"] = test.Files.Text("watched-identity.txt", identity.Route(test.Data.Case.IdentityConnectionString));
            environment["BRAID_CATALOG_CONNECTION_STRING"] = catalog.Route(test.Data.Case.CatalogConnectionString);
            var host = test.Start(environment); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            var anonymous = await ServerHostHttp.StartupStatusAsync(client); using var body = anonymous.Json();
            ServerHostCheck.That(body.RootElement.GetProperty("state").GetString() == "anonymous"
                && reader.Count == 0 && writer.Count == 0 && identity.Count == 0 && catalog.Count == 0,
                "Startup or absent-carrier status opened a delivered restricted database connection.");
            var unavailable = await test.StatusAsync(client);
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ServerHostCheck.That(unavailable.Status == 503 && reader.Count > 0 && writer.Count == 0 && identity.Count == 0 && catalog.Count == 0,
                "A real carrier skipped its delivered reader or fell back after its unavailable connection.");
            foreach (var witness in new[] { writer, identity, catalog })
            {
                using var sensor = new TcpClient(); await sensor.ConnectAsync(IPAddress.Loopback, witness.Port);
                await witness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                ServerHostCheck.That(witness.Count > 0, "An unused restricted-port witness lacked its independent positive connection control.");
            }
            await host.StopAsync();
        });

    [PostgresFact]
    public Task PublishedDirectSessionConsumesTrusted112CookieAndFreshlyDeniesRevokedMembership() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            string ring = test.Files.RingSnapshot(); var environment = test.Environment();
            environment["DOTNET_ENVIRONMENT"] = "Development"; environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            var host = test.Start(environment);
            using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            test.PositiveStatus(await test.StatusAsync(client)); test.PositiveContext(await test.ContextAsync(client));
            await ServerHostBrowserSecurity.CheckAsync(client);
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, "/api/v1/examples/mappings")).Status == 404
                && (await ServerHostHttp.SendAsync(client, "/openapi/v1.json")).Status == 404,
                "Session inherited Development prototype endpoints.");
            string project = CatalogHttpTestSupport.ProjectPath(test.Data.Project);
            var headers = new Dictionary<string, string> { ["Cookie"] = test.Cookies.Header };
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, project + "/mappings?limit=10", headers: headers)).Status == 200,
                "The published Session host did not use its restricted catalog reader.");
            string mapping = CatalogHttpTestSupport.MappingPath(test.Data.Original.Facts.FutureRevision);
            ServerHostCheck.That((await ServerHostHttp.SendAsync(client, mapping, headers: headers)).Status == 200,
                "The published Session host did not retain actual mapping ancestry.");
            await test.Data.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                test.Data.Project.OrganizationId, test.Data.Actor.PrincipalId.Value);
            test.PositiveStatus(await test.StatusAsync(client));
            var rejected = await test.ContextAsync(client);
            using var refusal = rejected.Json();
            // Resource authority uses the catalog's established non-disclosing notFound contract.
            ServerHostCheck.That(rejected.Status == 404 && refusal.RootElement.EnumerateObject().Count() == 1
                && refusal.RootElement.GetProperty("code").GetString() == "notFound"
                && !rejected.Headers.ContainsKey("Location") && !rejected.Headers.ContainsKey("Set-Cookie")
                && !rejected.Headers.ContainsKey("ETag"),
                "A prior cookie cached revoked organization membership or changed the closed catalog refusal.");
            await test.Data.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
                test.Data.Project.OrganizationId, test.Data.Actor.PrincipalId.Value);
            test.PositiveContext(await test.ContextAsync(client));
            ServerHostCheck.That(string.Equals(ring, test.Files.RingSnapshot(), StringComparison.Ordinal),
                "Ordinary session consumption changed the admitted durable ring.");
            await host.StopAsync();
        });

    [PostgresFact]
    public Task FreshPublishedProcessAndSamePostgresRestartRetainDurableSessionWithoutProvisioning() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var phase = DurableRestartPhase.SnapshotRing;
            try
            {
                string ring = test.Files.RingSnapshot();
                phase = DurableRestartPhase.FirstStart;
                var first = test.Start();
                using (var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate))
                {
                    phase = DurableRestartPhase.FirstStatus;
                    test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
                    phase = DurableRestartPhase.FirstContext;
                    test.PositiveContext(await test.ContextAsync(client));
                }
                phase = DurableRestartPhase.FirstStop;
                await first.StopAsync();
                phase = DurableRestartPhase.InitialSessionCountQuery;
                long initialSessions = await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.server_sessions");
                phase = DurableRestartPhase.InitialSessionCountCheck;
                ServerHostCheck.That(initialSessions == 1, "The restart fixture did not begin with exactly one durable session.");
                phase = DurableRestartPhase.OwnerClose;
                await test.Data.Case.CloseOwnerPoolAsync();
                phase = DurableRestartPhase.PostgresRestart;
                await postgres.RestartAsync();
                phase = DurableRestartPhase.SecondStart;
                var second = test.Start();
                using (var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate))
                {
                    phase = DurableRestartPhase.SecondStatus;
                    test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
                    phase = DurableRestartPhase.SecondContext;
                    test.PositiveContext(await test.ContextAsync(client));
                }
                phase = DurableRestartPhase.RingAndProcessCheck;
                ServerHostCheck.That(!ReferenceEquals(first, second) && first.Settled && string.Equals(ring, test.Files.RingSnapshot(), StringComparison.Ordinal),
                    "Fresh-process durable recovery did not preserve its exact owned ring and distinct process.");
                phase = DurableRestartPhase.FreshVerificationOpen;
                var verification = new Npgsql.NpgsqlConnectionStringBuilder(test.Data.Case.MigrationConnectionString) { Pooling = false };
                await using (var connection = new Npgsql.NpgsqlConnection(verification.ConnectionString))
                {
                    await connection.OpenAsync();
                    phase = DurableRestartPhase.SessionCountQuery;
                    await using var command = new Npgsql.NpgsqlCommand("SELECT count(*) FROM braid.server_sessions", connection);
                    long sessions = (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected owned session count."));
                    phase = DurableRestartPhase.SessionCountCheck;
                    ServerHostCheck.That(sessions == 1, "Host restart provisioned or adopted another session.");
                    phase = DurableRestartPhase.FreshVerificationClose;
                }
                phase = DurableRestartPhase.SecondStop;
                await second.StopAsync();
            }
            catch (Exception error)
            {
                // Capture only closed categories before the fixture sanitizes non-owned exceptions.
                var category = error switch
                {
                    ServerHostCaseFailure => DurableRestartFailure.ClosedCheckOrHelper,
                    Npgsql.PostgresException { SqlState: "57P01" } => DurableRestartFailure.PostgresAdminShutdown,
                    Npgsql.PostgresException => DurableRestartFailure.PostgresServer,
                    Npgsql.NpgsqlException { InnerException: EndOfStreamException } => DurableRestartFailure.PostgresEndOfStream,
                    Npgsql.NpgsqlException => DurableRestartFailure.PostgresClient,
                    TimeoutException => DurableRestartFailure.Timeout,
                    OperationCanceledException => DurableRestartFailure.Cancellation,
                    JsonException => DurableRestartFailure.Json,
                    KeyNotFoundException => DurableRestartFailure.MissingJsonMember,
                    IOException => DurableRestartFailure.Io,
                    UnauthorizedAccessException => DurableRestartFailure.Unauthorized,
                    InvalidOperationException => DurableRestartFailure.InvalidOperation,
                    _ => DurableRestartFailure.Unexpected
                };
                throw new ServerHostCaseFailure($"The owned durable restart failed: phase={phase}; category={category}.");
            }
        });

    private enum DurableRestartPhase
    {
        SnapshotRing, FirstStart, FirstStatus, FirstContext, FirstStop, InitialSessionCountQuery, InitialSessionCountCheck,
        OwnerClose, PostgresRestart, SecondStart, SecondStatus, SecondContext, RingAndProcessCheck,
        FreshVerificationOpen, SessionCountQuery, SessionCountCheck, FreshVerificationClose, SecondStop
    }

    private enum DurableRestartFailure
    {
        ClosedCheckOrHelper, PostgresAdminShutdown, PostgresServer, PostgresEndOfStream, PostgresClient, Timeout, Cancellation,
        Json, MissingJsonMember, Io, Unauthorized, InvalidOperation, Unexpected
    }

    [PostgresFact]
    public Task PublishedSessionFreshlyRejectsDisabledPrincipalInsteadOfDevelopmentOrBearerFallback() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var host = test.Start(); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            await test.Data.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Data.Actor.PrincipalId.Value);
            var reply = await test.StatusAsync(client); using var json = reply.Json(); var value = json.RootElement;
            ServerHostCheck.That(reply.Status == 200 && value.GetProperty("mode").GetString() == "session"
                && value.GetProperty("state").GetString() == "authenticationRequired"
                && value.GetProperty("requestToken").ValueKind == JsonValueKind.Null
                && value.GetProperty("identityContext").ValueKind == JsonValueKind.Null,
                "A disabled principal retained a positive session or fell back to another mode.");
            var forged = await ServerHostHttp.SendAsync(client, "/api/v1/catalog/context", headers: new Dictionary<string, string>
            { ["Authorization"] = "Bearer owned-host-forgery", ["X-Braid-Principal-Id"] = test.Data.Actor.PrincipalId.Value.ToString("D") });
            ServerHostCheck.That(forged.Status == 401 && !forged.Headers.ContainsKey("Location"),
                "A forged identity bypassed the session boundary or triggered a default challenge redirect.");
            await host.StopAsync();
        });

    [PostgresFact]
    public Task PublishedSessionFromARetiredSubjectBindingLastsUntilTheOwnerDisablesThatBinding() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            // The upgraded host never constructs the retired key, but an existing session names its binding by ID.
            var host = test.Start(); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            var disabled = await test.Identities.DisableAsync(test.Binding.Key, test.Data.Actor.PrincipalId, test.Binding.Version);
            ServerHostCheck.That(disabled.Outcome == ExternalIdentityBindingOwnerOutcome.Disabled, "The retired binding was not disabled.");
            var reply = await test.StatusAsync(client); using var json = reply.Json(); var value = json.RootElement;
            ServerHostCheck.That(reply.Status == 200 && value.GetProperty("state").GetString() == "authenticationRequired"
                && value.GetProperty("requestToken").ValueKind == JsonValueKind.Null,
                "A session from a disabled retired binding stayed authenticated.");
            await host.StopAsync();
        }, retiredBinding: true);

    [PostgresFact]
    public Task InadmissibleDeliveredKeyOrCredentialProfileFailsClosedWithoutBootstrap() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            string before = test.Files.RingSnapshot();
            foreach (string kind in new[] { "missing-ring", "mismatched-pem", "insecure-secret" })
            {
                var environment = test.Environment();
                if (kind == "missing-ring") environment["BRAID_SESSION_KEY_DIRECTORY"] = Path.Combine(test.Files.Root, "missing-ring");
                if (kind == "mismatched-pem") environment["BRAID_SESSION_KEY_PRIVATE_KEY_FILE"] = test.Files.ClientKey;
                if (kind == "insecure-secret")
                {
                    string insecure = test.Files.Text("insecure-reader.txt", test.Data.Case.SessionReaderConnectionString);
                    File.SetUnixFileMode(insecure, ServerHostFiles.PrivateFile | UnixFileMode.GroupRead);
                    environment["BRAID_SESSION_READER_CONNECTION_STRING_FILE"] = insecure;
                }
                var host = test.Start(environment);
                ServerHostCheck.That(await host.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                    "An inadmissible delivered host profile reported startup success.");
                ServerHostCheck.That(string.Equals(before, test.Files.RingSnapshot(), StringComparison.Ordinal)
                    && !Directory.Exists(Path.Combine(test.Files.Root, "missing-ring")),
                    "A refused host repaired or bootstrapped durable storage.");
                await host.AssertPrivateOutputAsync([test.Files.Root, test.Data.Case.SessionReaderConnectionString]);
            }
        });

    [PostgresFact]
    public Task DistinctApplicationCanOpenTheRingButCannotConsumeAnotherApplicationsProtectedCookie() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            string before = test.Files.RingSnapshot(); string originalCookie = test.Cookies.Header;
            var original = test.Start();
            using (var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate))
                test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, originalCookie));
            await original.StopAsync();
            var environment = test.Environment(); environment["BRAID_SESSION_APPLICATION_NAME"] = "Distinct owned application";
            var distinct = test.Start(environment);
            using (var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate))
            {
                var refused = await ServerHostHttp.StartupStatusAsync(client, originalCookie);
                using var body = refused.Json(); var value = body.RootElement;
                ServerHostCheck.That(refused.Status == 200 && value.EnumerateObject().Count() == 7
                    && value.GetProperty("version").GetInt32() == 1 && value.GetProperty("mode").GetString() == "session"
                    && value.GetProperty("state").GetString() == "authenticationRequired"
                    && value.GetProperty("requestToken").ValueKind == JsonValueKind.Null
                    && value.GetProperty("identityContext").ValueKind == JsonValueKind.Null
                    && value.GetProperty("displayName").ValueKind == JsonValueKind.Null,
                    "A different application consumed the original protected session cookie.");
                ServerHostHttp.PrivateHeaders(refused);
                var context = await ServerHostHttp.SendAsync(client, "/api/v1/catalog/context", headers: new Dictionary<string, string>
                { ["Cookie"] = originalCookie });
                ServerHostCheck.That(context.Status == 401 && !context.Headers.ContainsKey("Location"),
                    "A cross-application cookie selected catalog authority or a default challenge redirect.");
            }
            await distinct.StopAsync();
            var restored = test.Start();
            using (var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate))
                test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, originalCookie));
            ServerHostCheck.That(string.Equals(before, test.Files.RingSnapshot(), StringComparison.Ordinal)
                && await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.server_sessions") == 1,
                "Application isolation repaired the ring or replaced the original durable session.");
            await restored.StopAsync();
        });

    [PostgresFact]
    public Task DeliveredDecryptOverlapHasExactClosedEightPairBoundAndSupportsHistoricalExpiry() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            var pairs = Enumerable.Range(0, 9).Select(index => test.Files.HistoricalPair("historical-" + index, expired: index == 0))
                .Select(pair => new { certificateFile = pair.CertificateFile, privateKeyFile = pair.PrivateKeyFile }).ToArray();
            string exact = test.Files.Text("overlap-eight.json", JsonSerializer.Serialize(pairs.Take(8)));
            string tooMany = test.Files.Text("overlap-nine.json", JsonSerializer.Serialize(pairs));
            string extra = test.Files.Text("overlap-extra.json", JsonSerializer.Serialize(new[] { new {
                pairs[0].certificateFile, pairs[0].privateKeyFile, unadmitted = true } }));
            string malformed = test.Files.Text("overlap-malformed.json", "{not overlap JSON");
            string ring = test.Files.RingSnapshot();
            foreach (string path in new[] { tooMany, extra, malformed })
            {
                var rejected = test.Environment(); rejected["BRAID_SESSION_DECRYPT_CERTIFICATES_FILE"] = path;
                var failed = test.Start(rejected);
                ServerHostCheck.That(await failed.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                    "Host admitted an overbound or nonclosed decrypt delivery.");
            }
            var accepted = test.Environment(); accepted["BRAID_SESSION_DECRYPT_CERTIFICATES_FILE"] = exact;
            var host = test.Start(accepted); using var client = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
            test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(client, test.Cookies.Header));
            ServerHostCheck.That(string.Equals(ring, test.Files.RingSnapshot(), StringComparison.Ordinal),
                "Decrypt overlap delivery changed the existing private ring.");
            await host.StopAsync();
        });

    [PostgresFact]
    public Task SessionRejectsLegacyIdentityAndExtraListenersBeforePublishingAnyMode() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            foreach (var injection in new[] {
                new KeyValuePair<string,string?>("BRAID_DEV_IDENTITY_ENABLED", "true"),
                new KeyValuePair<string,string?>("BRAID_DEV_PRINCIPAL_ID", test.Data.Actor.PrincipalId.Value.ToString("D")),
                new KeyValuePair<string,string?>("BRAID_DEV_LISTEN_ADDR", "127.0.0.1:0"),
                new KeyValuePair<string,string?>("ASPNETCORE_URLS", "http://127.0.0.1:0"),
                new KeyValuePair<string,string?>("Kestrel__Endpoints__Unadmitted__Url", "http://127.0.0.1:0"),
                new KeyValuePair<string,string?>("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true") })
            {
                var environment = test.Environment(); environment[injection.Key] = injection.Value;
                var host = test.Start(environment);
                ServerHostCheck.That(await host.WaitForExitAsync(TimeSpan.FromSeconds(35)) != 0,
                    "Session accepted a legacy identity or conflicting listener/forwarding configuration.");
                await host.AssertPrivateOutputAsync([test.Data.Actor.PrincipalId.Value.ToString("D"), test.Files.Root, test.Data.Case.CatalogConnectionString]);
            }
        });
}
