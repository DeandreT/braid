using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Auth;
using Weft.Api.Catalog;
using Weft.Api.Specs;
using Weft.Application;
using Weft.Contracts.Specs;
using Weft.Domain;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>HTTP and real application-service orchestration over recording ports; not PostgreSQL/Blob qualification.</summary>
public sealed class SpecHttpTests
{
    [Fact]
    public async Task Generated_spec_routes_declare_security_strict_contracts_and_replay_headers()
    {
        var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi();
        await using var app = b.Build(); app.MapWeftSpecs(); app.MapOpenApi(); await app.StartAsync();
        using var client = app.GetTestClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var operations = paths.EnumerateObject().Where(path => path.Name.Contains("/spec-bundles/", StringComparison.Ordinal)).SelectMany(path => path.Value.EnumerateObject())
            .Where(property => property.Name is "get" or "post" or "put" or "delete").ToArray();
        Assert.Equal(7, operations.Length);
        foreach (var operation in operations)
        {
            Assert.Equal(2, operation.Value.GetProperty("security").GetArrayLength());
            Assert.True(operation.Value.GetProperty("responses").TryGetProperty("503", out _));
        }
        var upload = paths.EnumerateObject().Single(path => path.Name.EndsWith("/uploads", StringComparison.Ordinal)).Value.GetProperty("post");
        Assert.Contains(upload.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == SpecEndpoints.UploadHeader && p.GetProperty("required").GetBoolean());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(nameof(SpecUploadV1));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(8, schema.GetProperty("required").GetArrayLength());
        var extraction = paths.EnumerateObject().Single(path => path.Name.EndsWith("/extractions/{objectId}", StringComparison.Ordinal)).Value.GetProperty("get");
        var evidenceSchema = extraction.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", evidenceSchema.GetProperty("type").GetString());
        Assert.Equal(2, evidenceSchema.GetProperty("minItems").GetInt32());
        Assert.True(evidenceSchema.TryGetProperty("items", out _));
        var inventory = operations.Single(operation => operation.Value.GetProperty("operationId").GetString() == "GetSpecBundleInventory").Value;
        var headers = inventory.GetProperty("responses").GetProperty("200").GetProperty("headers");
        Assert.True(headers.TryGetProperty("X-Weft-Spec-Version", out _));
        Assert.False(headers.TryGetProperty("ETag", out _));
    }

    [Fact]
    public async Task Escaping_heavy_full_intake_persists_atomic_limit_evidence_and_replays()
    {
        var scope = new SpecBundleScope(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var ports = new Ports(scope); var service = new SpecBundleService(ports, ports);
        var source = new SpecSourceIdentity(scope, Guid.NewGuid(), 1);
        var original = Guid.NewGuid(); var uploadKey = Guid.NewGuid(); var extracted = Guid.NewGuid(); var extractKey = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(new string('<', 2_000_000));
        Assert.True(bytes.Length < new SpecExtractionLimits().MaximumEvidenceBytes);
        SpecIntakeResult? first = null; byte[]? firstEvidence = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var input = new MemoryStream(bytes, false);
            var result = await service.IntakeAsync(source, SpecSourceFormat.Text, original, uploadKey, extracted, extractKey, 1, 2, input);
            Assert.Equal(SpecExtractionOutcome.LimitExceeded, result.Outcome);
            var evidence = await service.ReadExtractionAsync(scope, extracted);
            Assert.True(evidence.Length <= SpecCapturedSource.MaximumBytes);
            using var document = JsonDocument.Parse(evidence);
            Assert.Equal(2, document.RootElement.GetArrayLength()); // no truncated or partial chunk
            Assert.Equal("serialized-evidence-bytes", document.RootElement[1].GetString());
            Assert.Equal(bytes, await service.ReadAsync(scope, original));
            var inventory = await service.InventoryAsync(scope);
            Assert.Equal(SpecExtractionOutcome.LimitExceeded, inventory.Objects.Single(item => item.Reference.Id.ObjectId == extracted).Outcome);
            Assert.Equal(Sha256Digest.FromBytes(evidence), inventory.Objects.Single(item => item.Reference.Id.ObjectId == extracted).Reference.Digest);
            if (first is not null) { Assert.Equal(first, result); Assert.Equal(firstEvidence, evidence); }
            first = result; firstEvidence = evidence;
        }
        Assert.Equal(2, ports.Reservations.Count);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Inventory_changes_after_publication_without_reusing_a_representation_validator()
    {
        await using var h = await Host.Start(); await h.Create();
        var service = new SpecBundleService(h.Ports, h.Ports);
        h.Ports.FailAfterWrite = true;
        var source = new SpecSourceIdentity(h.Scope, Guid.NewGuid(), 1);
        var id = Guid.NewGuid(); var key = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => service.UploadAsync(source, SpecSourceFormat.Text, id, key, 1, new MemoryStream([1])));
        var pending = await h.Client.GetAsync(h.Path);
        Assert.Null(pending.Headers.ETag);
        Assert.Equal("2", Assert.Single(pending.Headers.GetValues("X-Weft-Spec-Version")));
        Assert.Equal("pending", (await pending.Content.ReadFromJsonAsync<SpecInventoryV1>())!.Objects.Single().State);
        h.Ports.FailAfterWrite = false;
        await service.UploadAsync(source, SpecSourceFormat.Text, id, key, 1, new MemoryStream([1]));
        var published = await h.Client.GetAsync(h.Path);
        Assert.Null(published.Headers.ETag);
        Assert.Equal("2", Assert.Single(published.Headers.GetValues("X-Weft-Spec-Version")));
        Assert.Equal("published", (await published.Content.ReadFromJsonAsync<SpecInventoryV1>())!.Objects.Single().State);
        using var stale = h.Delete(1);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.SendAsync(stale)).StatusCode);
        using var current = h.Delete(2);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(current)).StatusCode);
    }

    [Fact]
    public void Host_capability_defaults_off_and_refuses_development_enablement()
    {
        var services = new ServiceCollection();
        Weft.Server.SpecHostConfiguration.Configure(services, _ => null, false);
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<SpecApiOptions>().Enabled);
        Assert.Null(provider.GetService<ISpecBundleServiceFactory>());
        Assert.ThrowsAny<Exception>(() => Weft.Server.SpecHostConfiguration.Configure(new ServiceCollection(),
            name => name == "WEFT_SPEC_INTAKE_ENABLED" ? "true" : null, false));
    }

    [Fact]
    public async Task Development_catalog_identity_never_opens_spec_business_services()
    {
        var scope = new SpecBundleScope(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var ports = new Ports(scope);
        var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders(); b.Services.AddWeftApi();
        b.Services.AddAntiforgery();
        b.Services.AddSingleton(new CatalogApiOptions(true, true, ports.Actor.Value, scope.Project.OrganizationId, "http://127.0.0.1"));
        b.Services.AddSingleton<CatalogRequestBoundary>();
        b.Services.AddSingleton(new SpecApiOptions(true)); b.Services.AddSingleton<ISpecBundleServiceFactory>(ports);
        await using var app = b.Build();
        app.Use((c, next) => { c.Connection.RemoteIpAddress = IPAddress.Loopback; return next(c); });
        app.MapWeftSpecs(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = new("http://127.0.0.1");
        var result = await client.GetAsync($"/api/v1/organizations/{scope.Project.OrganizationId}/projects/{scope.Project.ProjectId}/spec-bundles/{scope.BundleId}");
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(0, ports.Opens);
        Assert.Equal("no-store", result.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Real_service_recovers_interleaved_extraction_cas_without_replacing_the_original()
    {
        var scope = new SpecBundleScope(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var ports = new Ports(scope); var service = new SpecBundleService(ports, ports);
        var source = new SpecSourceIdentity(scope, Guid.NewGuid(), 1);
        var original = Guid.NewGuid(); var uploadKey = Guid.NewGuid(); var extracted = Guid.NewGuid(); var extractKey = Guid.NewGuid();
        await service.UploadAsync(source, SpecSourceFormat.Json, original, uploadKey, 1, new MemoryStream("{bad"u8.ToArray()));
        await service.UploadAsync(new(scope, Guid.NewGuid(), 1), SpecSourceFormat.Text, Guid.NewGuid(), Guid.NewGuid(), 2, new MemoryStream([1]));
        var stale = await Assert.ThrowsAsync<SpecStorageException>(() => service.IntakeAsync(source, SpecSourceFormat.Json, original, uploadKey,
            extracted, extractKey, 1, 2, new MemoryStream("{bad"u8.ToArray())));
        Assert.Equal(SpecStorageError.Conflict, stale.Error);
        var result = await service.IntakeAsync(source, SpecSourceFormat.Json, original, uploadKey, extracted, extractKey, 1, 3, new MemoryStream("{bad"u8.ToArray()));
        Assert.Equal(SpecExtractionOutcome.Malformed, result.Outcome);
        Assert.Equal(3, ports.Reservations.Count);
        Assert.Equal(result, await service.IntakeAsync(source, SpecSourceFormat.Json, original, uploadKey, extracted, extractKey, 1, 3, new MemoryStream("{bad"u8.ToArray())));
        ports.Denied = true;
        await Assert.ThrowsAsync<SpecStorageException>(() => service.ReadExtractionAsync(scope, extracted));
    }

    [Fact]
    public async Task Real_service_bounds_nonseekable_uploads_and_refuses_revoked_content_before_consumption()
    {
        var scope = new SpecBundleScope(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var ports = new Ports(scope); var service = new SpecBundleService(ports, ports);
        using var stream = new Nonseekable(new byte[SpecCapturedSource.MaximumBytes + 1]);
        ports.Denied = true;
        await Assert.ThrowsAsync<SpecStorageException>(() => service.UploadAsync(new(scope, Guid.NewGuid(), 1), SpecSourceFormat.Text,
            Guid.NewGuid(), Guid.NewGuid(), 1, stream));
        Assert.Equal(0, stream.BytesRead);
        ports.Denied = false;
        Assert.Equal(SpecStorageError.Limit, (await Assert.ThrowsAsync<SpecStorageException>(() => service.UploadAsync(new(scope, Guid.NewGuid(), 1),
            SpecSourceFormat.Text, Guid.NewGuid(), Guid.NewGuid(), 1, stream))).Error);
        Assert.Empty(ports.Reservations);
    }

    private sealed class Nonseekable(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await base.ReadAsync(buffer, token); BytesRead += count; return count; }
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Browser_mutations_require_the_catalog_epoch_token_and_exact_origin()
    {
        await using var h = await Host.Start();
        h.Client.DefaultRequestHeaders.Authorization = null;
        var issue = await h.Client.GetAsync("/__issue");
        Assert.Equal(HttpStatusCode.NoContent, issue.StatusCode);
        var cookies = issue.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToList();
        h.Client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        var tokenReply = await h.Client.GetAsync("/__token");
        Assert.Equal(HttpStatusCode.OK, tokenReply.StatusCode);
        cookies.AddRange(tokenReply.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        h.Client.DefaultRequestHeaders.Remove("Cookie"); h.Client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        var opens = h.Ports.Opens;
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Create()).StatusCode);
        Assert.Equal(opens, h.Ports.Opens);
        h.Client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", await tokenReply.Content.ReadAsStringAsync());
        h.Client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.OK, (await h.Create()).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Intake_inventory_extraction_and_cas_tombstone_are_executable_and_replay_stable()
    {
        await using var h = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await h.Create()).StatusCode);
        var upload = h.Upload();
        Assert.Equal(HttpStatusCode.OK, (await h.Send("/uploads", upload, "{\"url\":\"https://never-fetch.invalid/private\"}"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Send("/uploads", upload, "{\"url\":\"https://never-fetch.invalid/private\"}"u8.ToArray())).StatusCode);
        Assert.Equal(2, h.Ports.Reservations.Count);
        var inventory = await h.Client.GetFromJsonAsync<SpecInventoryV1>(h.Path);
        Assert.Equal(3, inventory!.Bundle.Version);
        Assert.Equal(2, inventory.Objects.Count);
        var extracted = await h.Client.GetAsync(h.Path + "/extractions/" + upload.ExtractionObjectId);
        Assert.Equal(HttpStatusCode.OK, extracted.StatusCode);
        Assert.Contains("never-fetch.invalid", await extracted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "/extractions/" + upload.OriginalObjectId)).StatusCode);
        using var stale = h.Delete(1);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.SendAsync(stale)).StatusCode);
        using var delete = h.Delete(3);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path + "/extractions/" + upload.ExtractionObjectId)).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Uncertain_publication_replays_same_claims_and_never_exposes_exception_details()
    {
        await using var h = await Host.Start(); await h.Create();
        var upload = h.Upload(); h.Ports.FailAfterWrite = true;
        var failed = await h.Send("/uploads", upload, "{}"u8.ToArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("private", await failed.Content.ReadAsStringAsync());
        Assert.Single(h.Ports.Reservations);
        h.Ports.FailAfterWrite = false;
        Assert.Equal(HttpStatusCode.OK, (await h.Send("/uploads", upload, "{}"u8.ToArray())).StatusCode);
        Assert.Equal(2, h.Ports.Reservations.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Send("/uploads", upload, "[]"u8.ToArray())).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Revocation_cross_scope_and_missing_credentials_close_before_business()
    {
        await using var h = await Host.Start(); await h.Create();
        h.Ports.Denied = true;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Send("/uploads", h.Upload(), "{}"u8.ToArray())).StatusCode);
        Assert.Empty(h.Ports.Reservations);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path)).StatusCode);
        h.Ports.Denied = false;
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path.Replace(h.Scope.Project.OrganizationId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(h.Path.Replace(h.Scope.Project.ProjectId.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        var opens = h.Ports.Opens;
        h.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync(h.Path)).StatusCode);
        Assert.Equal(opens, h.Ports.Opens);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Strict_bounded_json_and_streams_reject_bad_requests_and_preserve_malformed_extraction_outcomes()
    {
        await using var h = await Host.Start(); await h.Create();
        foreach (var json in new[] { "{\"creationKey\":null}", "{\"creationKey\":\"" + Guid.NewGuid() + "\",\"extra\":1}",
            "{\"creationKey\":\"" + Guid.NewGuid() + "\",\"creationKey\":\"" + Guid.NewGuid() + "\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsync(h.Path, new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        var large = await h.Send("/uploads", h.Upload(), new byte[SpecCapturedSource.MaximumBytes + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Empty(h.Ports.Reservations);
        var malformed = await h.Send("/uploads", h.Upload(), "{bad"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, malformed.StatusCode);
        Assert.Equal("Malformed", (await malformed.Content.ReadFromJsonAsync<SpecIntakeV1>())!.Outcome);
    }

    [SupportedOSPlatform("linux")]
    [SpecSessionFact]
    public async Task Pasted_text_uses_same_intake_and_mixed_cookie_bearer_is_refused()
    {
        await using var h = await Host.Start(); await h.Create();
        var metadata = h.Upload() with { Format = "text" };
        using var request = new HttpRequestMessage(HttpMethod.Post, h.Path + "/text") { Content = JsonContent.Create(new SpecTextV1(metadata, "hello\nhttps://inert.invalid")) };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(request)).StatusCode);
        h.Client.DefaultRequestHeaders.Add("Cookie", "unrelated=1");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync(h.Path)).StatusCode);
    }

    [SupportedOSPlatform("linux")]
    private sealed class Host : IAsyncDisposable
    {
        private readonly SessionTicketQualificationRoot files = new();
        private DurableSessionKeyProfile profile = null!;
        private WebApplication app = null!;
        internal HttpClient Client = null!;
        internal SpecBundleScope Scope = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        internal Ports Ports = null!;
        internal string Path => $"/api/v1/organizations/{Scope.Project.OrganizationId}/projects/{Scope.Project.ProjectId}/spec-bundles/{Scope.BundleId}";
        internal static async Task<Host> Start()
        {
            var h = new Host();
            using (DurableSessionKeyProfile.BootstrapEmpty(h.files.Options(h.Scope.Project.OrganizationId), h.files.Certificate, [],
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2))) { }
            h.profile = DurableSessionKeyProfile.Open(h.files.Options(h.Scope.Project.OrganizationId), h.files.Certificate, []);
            var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseTestServer(); b.Services.AddWeftApi();
            h.Ports = new(h.Scope);
            var sessions = new SessionStoreTestHarness.CountingSessionPorts();
            sessions.Create = (intent, _) =>
            {
                sessions.Row = new(intent.Key, intent.BindingId, intent.BindingVersion, intent.LoginEpoch,
                    ServerSessionPolicy.InitialLifetime(DateTimeOffset.UtcNow, intent.VerifiedLoginDeadlineUtc)!, 1, intent.CopyProtectedTicket());
                return Task.FromResult(ServerSessionWriteResult.ForAcknowledgedFacts(ServerSessionWriteOutcome.Created, sessions.Row));
            };
            sessions.Observe = (key, _) => Task.FromResult(ServerSessionPolicy.ObserveCurrent(key, DateTimeOffset.UtcNow, sessions.Row,
                sessions.Row is { } row ? new(row.BindingId, row.BindingVersion, h.Ports.Actor, true, true) : null));
            b.Services.AddWeftBrowserSessions(new("https://localhost"), h.profile, sessions, sessions, TimeProvider.System);
            b.Services.AddWeftSessionCatalog(new(h.Scope.Project.OrganizationId), new(), new(), new(), new());
            b.Services.AddWeftBearerAccess(new(h.Scope.Project.OrganizationId), h.Ports, h.Ports, h.Ports);
            b.Services.AddSingleton(new SpecApiOptions(true)); b.Services.AddSingleton<ISpecBundleServiceFactory>(h.Ports);
            h.app = b.Build(); h.app.MapWeftSpecs();
            h.app.MapGet("/__issue", async (Microsoft.AspNetCore.Http.HttpContext c, ServerSessionBrowser browser) =>
            { await browser.IssueFreshAsync(c, Guid.NewGuid(), 1, h.Ports.Actor, DateTimeOffset.UtcNow.AddMinutes(30), c.RequestAborted); return Microsoft.AspNetCore.Http.Results.NoContent(); });
            h.app.MapGet("/__token", async (Microsoft.AspNetCore.Http.HttpContext c, ServerSessionBrowser browser) =>
                Microsoft.AspNetCore.Http.Results.Text(await browser.IssueRequestTokenAsync(c, c.RequestAborted)));
            await h.app.StartAsync();
            h.Client = h.app.GetTestClient(); h.Client.BaseAddress = new("https://localhost");
            h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-token");
            return h;
        }
        internal Task<HttpResponseMessage> Create() => Client.PutAsJsonAsync(Path, new SpecCreateV1(Guid.NewGuid()));
        internal SpecUploadV1 Upload() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, "json");
        internal async Task<HttpResponseMessage> Send(string suffix, SpecUploadV1 upload, byte[] bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path + suffix) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Headers.TryAddWithoutValidation(SpecEndpoints.UploadHeader, JsonSerializer.Serialize(upload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            return await Client.SendAsync(request);
        }
        internal HttpRequestMessage Delete(long version)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, Path);
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\""); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); return request;
        }
        public async ValueTask DisposeAsync() { Client?.Dispose(); if (app is not null) await app.DisposeAsync(); profile?.Dispose(); files.Dispose(); }
    }

    private sealed class Ports(SpecBundleScope scope) : ISpecBundleServiceFactory, ISpecBundleLifecycle, ISpecBundleInventory,
        ISpecObjectStore, IUserTokenValidator, IExternalIdentityReader, IApiRequestAuditWriter
    {
        private readonly Guid store = Guid.NewGuid();
        private readonly ExternalIdentityKey identity = new("fixture", "https://issuer.invalid", "subject");
        private readonly PrincipalId actor = new(Guid.NewGuid());
        internal PrincipalId Actor => actor;
        private readonly Dictionary<Guid, byte[]> content = [];
        internal Dictionary<Guid, SpecObjectReservation> Reservations = [];
        private readonly Dictionary<Guid, SpecInventoryItem> items = [];
        private long version = 1; private bool tombstoned;
        internal bool Denied, FailAfterWrite;
        internal int Opens;
        private void Admit(SpecBundleScope requested) { if (Denied || tombstoned || requested != scope) throw new SpecStorageException(SpecStorageError.NotAdmitted); }
        public Task<SpecBundleService> OpenAsync(ActorContext current, bool write, CancellationToken token)
        { Assert.Equal(actor, current.PrincipalId); Opens++; return Task.FromResult(new SpecBundleService(this, this)); }
        public Task<SpecBundleStatus> CreateAsync(SpecBundleScope s, Guid key, CancellationToken t) { Admit(s); return Task.FromResult(new SpecBundleStatus(s, version, false)); }
        public Task<SpecBundleStatus> AuthorizeAsync(SpecBundleScope s, bool write, CancellationToken t) => CreateAsync(s, Guid.NewGuid(), t);
        public Task<SpecObjectReservation> ReserveAsync(SpecObjectReference r, SpecSourceFormat format, Guid key, long expected, SpecExtractionIdentity? extraction, CancellationToken t)
        {
            Admit(r.Id.Source.Bundle);
            if (Reservations.TryGetValue(key, out var prior))
            { if (prior.Claim.Reference != r) throw new SpecStorageException(SpecStorageError.Conflict); return Task.FromResult(prior); }
            if (expected != version) throw new SpecStorageException(SpecStorageError.Conflict);
            var reservation = new SpecObjectReservation(new(store, key, r), ++version); Reservations.Add(key, reservation);
            items.Add(r.Id.ObjectId, new(r, format, "pending", extraction?.Outcome)); return Task.FromResult(reservation);
        }
        public Task<SpecObjectReference> FindAsync(SpecBundleScope s, Guid id, CancellationToken t)
        { Admit(s); if (!items.TryGetValue(id, out var item) || item.State != "published") throw new SpecStorageException(SpecStorageError.NotAdmitted); return Task.FromResult(item.Reference); }
        public Task<SpecBundleStatus> TombstoneAsync(SpecBundleScope s, SpecSourceIdentity? source, long expected, Guid key, CancellationToken t)
        { Admit(s); if (expected != version) throw new SpecStorageException(SpecStorageError.Conflict); tombstoned = true; return Task.FromResult(new SpecBundleStatus(s, ++version, true)); }
        public Task<IReadOnlyList<SpecRetirementClaim>> RetirementsAsync(SpecBundleScope s, CancellationToken t) => throw new NotSupportedException();
        public Task<SpecBundleInventory> InventoryAsync(SpecBundleScope s, CancellationToken t)
        { Admit(s); return Task.FromResult(new SpecBundleInventory(new(s, version, false), items.Values.ToArray())); }
        public async Task<SpecPublishResult> PublishAsync(SpecBundleScope s, SpecPublicationClaim claim, Stream input, CancellationToken t)
        {
            Admit(s); using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, t); content[claim.Reference.Id.ObjectId] = bytes.ToArray();
            if (FailAfterWrite) throw new IOException("private backend path");
            items[claim.Reference.Id.ObjectId] = items[claim.Reference.Id.ObjectId] with { State = "published" }; return SpecPublishResult.Created;
        }
        public Task<byte[]> ReadAsync(SpecBundleScope s, SpecObjectReference reference, CancellationToken t) { Admit(s); return Task.FromResult(content[reference.Id.ObjectId]); }
        public Task<SpecRetireResult> RetireAsync(SpecBundleScope s, SpecRetirementClaim claim, CancellationToken t) => throw new NotSupportedException();
        public Task<UserCaller> ValidateAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(new UserCaller(identity, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExternalIdentityResolution.FromCurrent(new(Guid.NewGuid(), key, actor, 1, true, DateTimeOffset.UtcNow), true));
        public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiRequestAuditOutcome> RecordAsync(ApiRequestAudit audit, CancellationToken cancellationToken) => Task.FromResult(ApiRequestAuditOutcome.Recorded);
    }
}

public sealed class SpecSessionFactAttribute : FactAttribute
{
    public SpecSessionFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The real browser session key profile requires Linux; recording business ports do not bypass identity admission.";
    }
}
