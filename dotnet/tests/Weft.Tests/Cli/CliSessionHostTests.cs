using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Weft.Application;
using Weft.Cli;

namespace Weft.Tests.Cli;

/// <summary>
/// The CLI against the in-process Session catalog with bearer admission on real PostgreSQL: MSAL signs in to the owned
/// HTTPS issuer through its public-client flows and the host validates the issued delegated tokens with the same keys.
/// The CLI reaches both only by name (login.microsoftonline.com and the HTTPS origin), trusting the run's test authority.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CliSessionHostTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task TheSignedInCliReadsAndEditsAsTheBoundUserWithAnAuditedBearerMutation() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync();
        var login = await cli.LoginAsync();
        Assert.Contains("enter the code", login.Error, StringComparison.Ordinal);
        Assert.Equal(1, test.Issuer.PublicDeviceGrants);

        var whoami = await cli.RunAsync("whoami", "--json");
        Assert.True(whoami.Exit == 0, whoami.ToString());
        var context = JsonNode.Parse(whoami.Out)!["context"]!;
        Assert.Equal("bearer", context["mode"]!.GetValue<string>());
        Assert.Equal(test.Data.Actor.PrincipalId.Value.ToString("D"), context["actorPrincipalId"]!.GetValue<string>());
        Assert.Null(context["requestToken"]);

        var scope = test.Data.Scope;
        var projects = await cli.RunAsync("projects", "list", "--json");
        Assert.True(projects.Exit == 0, projects.ToString());
        Assert.Contains(scope.ProjectId.ToString("D"), projects.Out, StringComparison.Ordinal);
        var mapping = await cli.RunAsync("mappings", "get", "--project", scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"));
        Assert.True(mapping.Exit == 0, mapping.ToString());

        var before = records.Sum(record => record.Audits);
        var edited = await cli.RunAsync("mappings", "metadata", "edit", "--project", scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"),
            "--description", "Edited by the weft CLI");
        Assert.True(edited.Exit == 0, edited.ToString());
        Assert.Equal(1, records.Sum(record => record.Audits) - before);
        Assert.Equal(1, records.Sum(record => record.MetadataWrites));
        Assert.Equal("Edited by the weft CLI", (await test.Data.HeaderAsync()).Description);
        Assert.All(records, record => Assert.False(record.Unexpected));

        Assert.Equal(0, (await cli.RunAsync("logout")).Exit);
        Assert.Equal(CliExitCode.SignInRequired, (await cli.RunAsync("projects", "list")).Code);
        Assert.Equal(0, test.Issuer.PublicRefusals);
    }, bearerBurst: 100);

    [PostgresFact]
    public Task AValidTokenWithoutAWeftBindingIsRefusedAsASignInFailure() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        using var cli = Harness(test, new ConcurrentQueue<AuthHttpRecord>());
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        var refused = await cli.RunAsync("whoami");
        Assert.Equal(CliExitCode.SignInFailed, refused.Code);
        Assert.Contains("identityRejected", refused.Error, StringComparison.Ordinal);
    }, bindIssuer: false, bearerBurst: 100);

    [PostgresFact]
    public Task CatalogPagingAndRevisionReadsUseTheServersRealCursorsAndHideOtherProjects() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var another = await test.Data.CommitCreateAsync();
        var appended = await test.Data.CommitAppendAsync();
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        var scope = test.Data.Scope;
        var project = scope.ProjectId.ToString("D");
        var mapping = scope.MappingId.ToString("D");

        var projects = await cli.RunAsync("projects", "list", "--all", "--limit", "1", "--json");
        Assert.True(projects.Exit == 0, projects.ToString());
        Assert.Single(JsonNode.Parse(projects.Out)!["projects"]!.AsArray());
        Assert.DoesNotContain(test.Data.Seed.HiddenProjectA.ToString("D"), projects.Out, StringComparison.Ordinal);
        var mappings = await cli.RunAsync("mappings", "list", "--project", project, "--all", "--limit", "1", "--json");
        Assert.True(mappings.Exit == 0, mappings.ToString());
        var mappingPage = JsonNode.Parse(mappings.Out)!;
        Assert.Equal(2, mappingPage["mappings"]!.AsArray().Count);
        Assert.Null(mappingPage["nextCursor"]);
        Assert.Contains(another.Facts.FutureRevision.MappingId.ToString("D"), mappings.Out, StringComparison.Ordinal);
        var revisions = await cli.RunAsync("revisions", "list", "--project", project, "--mapping", mapping,
            "--all", "--limit", "1", "--json");
        Assert.True(revisions.Exit == 0, revisions.ToString());
        Assert.Equal(2, JsonNode.Parse(revisions.Out)!["revisions"]!.AsArray().Count);
        var revision = await cli.RunAsync("revisions", "get", "--project", project, "--mapping", mapping,
            appended.Facts.FutureRevision.RevisionId.ToString("D"), "--json");
        Assert.True(revision.Exit == 0, revision.ToString());
        Assert.Equal("2", JsonNode.Parse(revision.Out)!["sequence"]!.GetValue<string>());
        var hidden = await cli.RunAsync("projects", "get", test.Data.Seed.HiddenProjectA.ToString("D"));
        Assert.Equal(CliExitCode.NotFound, hidden.Code);
        Assert.All(records, record => Assert.False(record.Unexpected));
        Assert.Equal(0, records.Sum(record => record.Audits));
    }, bearerBurst: 100);

    [PostgresFact]
    public Task MetadataPreconditionsAndCurrentProjectRolesAreEnforcedByTheSessionHost() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        var scope = test.Data.Scope;
        var project = scope.ProjectId.ToString("D");
        var mapping = scope.MappingId.ToString("D");
        var metadata = await cli.RunAsync("mappings", "metadata", "get", "--project", project, mapping, "--json");
        Assert.True(metadata.Exit == 0, metadata.ToString());
        var tag = JsonNode.Parse(metadata.Out)!["eTag"]!.GetValue<string>();
        var edited = await cli.RunAsync("mappings", "metadata", "edit", "--project", project, mapping,
            "--name", "A new name", "--if-match", tag);
        Assert.True(edited.Exit == 0, edited.ToString());
        var stale = await cli.RunAsync("mappings", "metadata", "edit", "--project", project, mapping,
            "--name", "Must not overwrite", "--if-match", tag);
        Assert.Equal(CliExitCode.PreconditionFailed, stale.Code);
        Assert.Equal("A new name", (await test.Data.HeaderAsync()).Name);

        // The same cached account and access token do not freeze the user's project role.
        await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1",
            test.Data.Actor.PrincipalId.Value);
        var denied = await cli.RunAsync("mappings", "metadata", "edit", "--project", project, mapping,
            "--description", "Must not be written");
        // Metadata edits deliberately hide resources that the current role may not edit.
        Assert.Equal(CliExitCode.NotFound, denied.Code);
        Assert.Equal("Original description", (await test.Data.HeaderAsync()).Description);
        Assert.Equal(1, records.Sum(record => record.MetadataWrites));
        Assert.All(records, record => Assert.False(record.Unexpected));
    }, bearerBurst: 100);

    [PostgresFact]
    public Task DisablingTheBindingRefusesTheCachedAccountBeforeAnyCatalogRead() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        Assert.Equal(0, (await cli.RunAsync("whoami")).Exit);
        var disabled = await test.Owner.DisableAsync(test.Binding.Key, test.Data.Actor.PrincipalId, test.Binding.Version);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, disabled.Outcome);
        records.Clear();
        var refused = await cli.RunAsync("projects", "list");
        Assert.Equal(CliExitCode.SignInFailed, refused.Code);
        Assert.Contains("identityRejected", refused.Error, StringComparison.Ordinal);
        Assert.All(records, record => Assert.Equal(0, record.CatalogReads));
        Assert.Equal(1, test.Issuer.PublicDeviceGrants);
    }, bearerBurst: 100);

    [PostgresFact]
    public Task ThePerPrincipalRateLimitIsRetriedAfterItsRetryAfterAndThenReported() => AuthHttpFixture.RunAsync(postgres, async test =>
    {
        var records = new ConcurrentQueue<AuthHttpRecord>();
        using var cli = Harness(test, records);
        await cli.ConfigureAsync();
        await cli.LoginAsync();
        // A burst of one with no replenishment in the run: the context read uses it, so the page is refused five times.
        var limited = await cli.RunAsync("projects", "list");
        Assert.Equal(CliExitCode.RateLimited, limited.Code);
        Assert.Contains("rateLimited", limited.Error, StringComparison.Ordinal);
        Assert.Equal(1 + 5, records.Count);
        Assert.All(cli.Time.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(60), delay));
        Assert.Equal(4, cli.Time.Delays.Count);
    }, bearerBurst: 1);

    private static CliHarness Harness(AuthHttpFixture test, ConcurrentQueue<AuthHttpRecord> records)
    {
        test.Issuer.PublicClients = [UserTokenValidationTests.Cli];
        test.Issuer.PublicScope = UserTokenValidationTests.Scope;
        test.Issuer.PublicAccessClaims = client => UserTokenValidationTests.Claims(application: client);
        var api = new FixtureRequests(test, Handler(test.Crypto.Root, "127.0.0.1", test.Origin.Port, test.First.PhysicalPort), records);
        return new CliHarness(new IdentityTransport(AuthHttpFixture.NewClient(test.Crypto.Root, "login.microsoftonline.com", 443, test.Issuer.Port)),
            api, test.Origin, () => test.Issuer.PublicSecrets, EntraProtocolFixturePolicy.Tenant, UserTokenValidationTests.Cli,
            $"api://{EntraProtocolFixturePolicy.Client:D}/{UserTokenValidationTests.Scope}");
    }

    /// <summary>The only reachable endpoint is the logical name on its fixture port, trusting only the run's authority.</summary>
    private static SocketsHttpHandler Handler(X509Certificate2 root, string logicalHost, int logicalPort, int actualPort)
    {
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(root);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chain },
            ConnectCallback = async (context, cancellationToken) =>
            {
                Assert.True(context.DnsEndPoint.Host == logicalHost && context.DnsEndPoint.Port == logicalPort, "The CLI reached an unexpected origin.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, actualPort, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private sealed class IdentityTransport(HttpClient client) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => client;
    }

    /// <summary>Tags each CLI request with its own fixture record, which the in-process host requires and observes.</summary>
    private sealed class FixtureRequests(AuthHttpFixture test, HttpMessageHandler inner, ConcurrentQueue<AuthHttpRecord> records)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var record = test.NewRecord();
            records.Enqueue(record);
            request.Headers.Add(AuthHttpFixture.FixtureHeader, record.Id.ToString(CultureInfo.InvariantCulture));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
