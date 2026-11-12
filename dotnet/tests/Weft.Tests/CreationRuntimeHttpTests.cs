using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Creation;
using Weft.Application;
using Weft.Contracts.Creation;
using Weft.Domain;
using Weft.Identity;

namespace Weft.Tests;

public sealed class CreationRuntimeHttpTests
{
    [Fact]
    public async Task Contracts_expose_exact_authenticated_actions_and_closed_bodies()
    {
        var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi();
        await using var app = b.Build(); app.MapWeftCreation(); app.MapOpenApi(); await app.StartAsync(); using var client = app.GetTestClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operations = json.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.Contains("/creation-proposals/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.EnumerateObject()).Where(p => p.Name is "get" or "post").ToArray();
        Assert.Equal(4, operations.Length);
        foreach (var op in operations) { Assert.Equal(2, op.Value.GetProperty("security").GetArrayLength()); Assert.True(op.Value.GetProperty("responses").TryGetProperty("503", out _)); }
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.GetProperty(nameof(CreationApprovalV1)).GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(2, schemas.GetProperty(nameof(CreationApprovalV1)).GetProperty("required").GetArrayLength());
        Assert.Contains(operations.Single(p => p.Name == "get").Value.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "expectedFingerprint" && p.GetProperty("required").GetBoolean());
        var list = json.RootElement.GetProperty("paths").EnumerateObject().Single(p => p.Name.EndsWith("/creation-proposals", StringComparison.Ordinal)).Value.GetProperty("get");
        Assert.Equal("ListCreationProposals", list.GetProperty("operationId").GetString());
        Assert.Equal(2, list.GetProperty("security").GetArrayLength());
        Assert.Contains(list.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "afterProposal");
    }

    [Fact]
    public async Task Development_identity_cannot_enter_creation_business_ports()
    {
        var f = new CreationRuntimeFixture(); var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders();
        b.Services.AddWeftApi(); b.Services.AddAntiforgery(); f.Register(b.Services);
        b.Services.AddSingleton(new CatalogApiOptions(true, true, WorkspaceToolFixture.Actor.PrincipalId.Value, f.Bundle.Project.OrganizationId, "http://127.0.0.1"));
        b.Services.AddSingleton<CatalogRequestBoundary>();
        await using var app = b.Build(); app.Use((c, next) => { c.Connection.RemoteIpAddress = IPAddress.Loopback; return next(c); });
        app.MapWeftCreation(); await app.StartAsync(); using var client = app.GetTestClient(); client.BaseAddress = new("http://127.0.0.1");
        var result = await client.GetAsync($"/api/v1/organizations/{f.Bundle.Project.OrganizationId}/projects/{f.Bundle.Project.ProjectId}/creation-proposals/{Guid.NewGuid()}?expectedFingerprint={new string('a', 64)}");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode); Assert.Equal(0, f.Reads); Assert.Equal(0, f.Creates);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/organizations/{f.Bundle.Project.OrganizationId}/projects/{f.Bundle.Project.ProjectId}/creation-proposals")).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Authenticated_exact_approval_recovery_preview_and_revocation()
    {
        await using var h = await Host.Start(); var proposal = await h.Fixture.Generate();
        var listed = (await h.Client.GetFromJsonAsync<CreationProposalPageV1>(h.CollectionPath + "?limit=1"))!;
        Assert.Equal(proposal.Fingerprint.Value, Assert.Single(listed.Proposals).Fingerprint);
        Assert.Equal(proposal.Id.ToString("D"), listed.AfterProposal);
        Assert.Empty((await h.Client.GetFromJsonAsync<CreationProposalPageV1>(h.CollectionPath + "?afterProposal=" + listed.AfterProposal))!.Proposals);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync(h.CollectionPath + "?limit=21")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + proposal.Fingerprint.Value)).StatusCode);
        Assert.Equal(0, h.Fixture.Creates);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(proposal.Fingerprint.Value, []))).StatusCode);
        Assert.Equal(0, h.Fixture.Leases);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsJsonAsync(h.Path + "/recover", new CreationRecoveryV1(proposal.Fingerprint.Value))).StatusCode);
        Assert.Equal(0, h.Fixture.Creates); Assert.Equal(0, h.Fixture.Resumes);
        var sample = "{\"a\":\"x\"}"u8.ToArray();
        var preview = await h.Client.PostAsJsonAsync(h.Path + "/preview", new CreationPreviewRequestV1(proposal.Fingerprint.Value, Convert.ToBase64String(sample), Sha256Digest.FromBytes(sample).Value));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode); var output = (await preview.Content.ReadFromJsonAsync<CreationPreviewV1>())!;
        Assert.Equal("json", output.Format); Assert.Equal("application/json", output.MediaType);
        Assert.Equal(output.OutputDigest, Sha256Digest.FromBytes(Convert.FromBase64String(output.OutputBase64!)).Value); Assert.Equal(0, h.Fixture.Creates);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync(h.Path + "/preview", new CreationPreviewRequestV1(proposal.Fingerprint.Value,
            Convert.ToBase64String(sample), new string('a', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(proposal.Fingerprint.Value, ["a1"]))).StatusCode);
        Assert.Equal(1, h.Fixture.Creates);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsJsonAsync(h.Path + "/recover", new CreationRecoveryV1(proposal.Fingerprint.Value))).StatusCode);
        Assert.Equal(1, h.Fixture.Creates); Assert.Equal(1, h.Fixture.Resumes);
        h.Identity.Actor = new(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.CollectionPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + proposal.Fingerprint.Value)).StatusCode);
        h.Identity.Actor = WorkspaceToolFixture.Actor.PrincipalId; h.Fixture.Denied = true;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + proposal.Fingerprint.Value)).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Session_csrf_and_critical_questions_refuse_ingestion()
    {
        await using var h = await Host.Start(); var p = await h.Fixture.Generate(criticalQuestion: true);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(p.Fingerprint.Value, ["a1"]))).StatusCode);
        Assert.Equal(0, h.Fixture.Creates); Assert.Equal(0, h.Fixture.Leases);
        h.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(p.Fingerprint.Value, ["a1"]))).StatusCode);
        var issue = await h.Client.GetAsync("/__issue");
        var cookie = issue.Headers.GetValues("Set-Cookie").Single(s => s.StartsWith("__Host-Weft.Session=", StringComparison.Ordinal)).Split(';')[0];
        h.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(p.Fingerprint.Value, ["a1"]))).StatusCode);
        Assert.Equal(0, h.Fixture.Creates);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Stale_snapshot_preserves_authorized_terminal_receipt_but_content_revocation_hides_it()
    {
        await using var h = await Host.Start(); var p = await h.Fixture.Generate();
        await h.Client.PostAsJsonAsync(h.Path + "/approve", new CreationApprovalV1(p.Fingerprint.Value, ["a1"]));
        h.Fixture.MakeTerminal(); h.Fixture.Stale = true;
        Assert.Equal(p.Fingerprint.Value, Assert.Single((await h.Client.GetFromJsonAsync<CreationProposalPageV1>(h.CollectionPath))!.Proposals).Fingerprint);
        var read = await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + p.Fingerprint.Value);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var receipt = (await read.Content.ReadFromJsonAsync<CreationProposalV1>())!.Receipt!;
        Assert.Equal("Committed", receipt.Phase); Assert.NotNull(receipt.MappingId); Assert.NotNull(receipt.RevisionId);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PostAsJsonAsync(h.Path + "/recover", new CreationRecoveryV1(p.Fingerprint.Value))).StatusCode);
        Assert.Equal(0, h.Fixture.Resumes); Assert.Equal(1, h.Fixture.Creates);
        h.Fixture.Denied = true;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.CollectionPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "?expectedFingerprint=" + p.Fingerprint.Value)).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    private sealed class Host : IAsyncDisposable
    {
        private readonly SessionTicketQualificationRoot files = new();
        private DurableSessionKeyProfile profile = null!; private WebApplication app = null!;
        internal readonly CreationRuntimeFixture Fixture = new(); internal readonly IdentityPorts Identity = new();
        internal HttpClient Client = null!;
        internal string CollectionPath => $"/api/v1/organizations/{Fixture.Bundle.Project.OrganizationId}/projects/{Fixture.Bundle.Project.ProjectId}/creation-proposals";
        internal string Path => $"/api/v1/organizations/{Fixture.Bundle.Project.OrganizationId}/projects/{Fixture.Bundle.Project.ProjectId}/creation-proposals/{Fixture.State!.Proposal.Id}";
        internal static async Task<Host> Start()
        {
            var h = new Host(); var org = h.Fixture.Bundle.Project.OrganizationId;
            using (DurableSessionKeyProfile.BootstrapEmpty(h.files.Options(org), h.files.Certificate, [], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
            h.profile = DurableSessionKeyProfile.Open(h.files.Options(org), h.files.Certificate, []);
            var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi(); h.Fixture.Register(b.Services);
            var sessions = new SessionStoreTestHarness.CountingSessionPorts();
            sessions.Create = (intent, _) =>
            {
                sessions.Row = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                ServerSessionPolicy.InitialLifetime(DateTimeOffset.UtcNow, intent.VerifiedLoginDeadlineUtc)!, 1, intent.CopyProtectedTicket());
                return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, sessions.Row));
            };
            sessions.Observe = (key, _) => Task.FromResult(ServerSessionPolicy.ObserveCurrent(key, DateTimeOffset.UtcNow, sessions.Row,
                sessions.Row is { } row ? new(row.BindingId, row.BindingVersion, h.Identity.Actor, true, true) : null));
            b.Services.AddWeftBrowserSessions(new("https://localhost"), h.profile, sessions, sessions, TimeProvider.System);
            b.Services.AddWeftSessionCatalog(new(org), new(), new(), new(), new());
            b.Services.AddWeftBearerAccess(new(org), h.Identity, h.Identity, h.Identity);
            h.app = b.Build(); h.app.MapWeftCreation();
            h.app.MapGet("/__issue", async (HttpContext c, ServerSessionBrowser browser) =>
            { await browser.IssueFreshAsync(c, Guid.NewGuid(), 1, h.Identity.Actor, DateTimeOffset.UtcNow.AddMinutes(30), c.RequestAborted); return Results.NoContent(); });
            await h.app.StartAsync(); h.Client = h.app.GetTestClient(); h.Client.BaseAddress = new("https://localhost");
            h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token"); return h;
        }
        public async ValueTask DisposeAsync() { Client?.Dispose(); if (app is not null) await app.DisposeAsync(); profile?.Dispose(); files.Dispose(); }
    }
    private sealed class IdentityPorts : IUserTokenValidator, IExternalIdentityReader, IApiRequestAuditWriter
    {
        private readonly ExternalIdentityKey identity = new("fixture", "https://issuer.invalid", "subject");
        internal PrincipalId Actor = WorkspaceToolFixture.Actor.PrincipalId;
        public Task<UserCaller> ValidateAsync(string token, CancellationToken ct = default) => Task.FromResult(new UserCaller(identity, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key, CancellationToken ct = default) =>
            Task.FromResult(ExternalIdentityResolution.FromCurrent(new(Guid.NewGuid(), key, Actor, 1, true, DateTimeOffset.UtcNow), true));
        public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken ct) => Task.FromResult(ApiRequestAuditOutcome.Recorded);
    }
}
