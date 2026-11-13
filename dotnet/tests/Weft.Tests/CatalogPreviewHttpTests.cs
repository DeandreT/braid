using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weft.Api;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual Kestrel preview routes over the real catalog and preview profiles.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogPreviewHttpTests(PostgresFixture fixture)
{
    private static readonly byte[] Output = "{\"orders\":[{\"id\":\"SO-1\",\"qty\":9007199254740993}]}"u8.ToArray();

    [PostgresFact]
    public async Task Xml_http_requires_explicit_enablement_and_preserves_exact_bytes_and_metadata()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, XmlPreviewWorkflowTests.Native);
        var body = new CatalogPreviewSubmissionV1(new("all"), [], [new(Convert.ToBase64String(XmlPreviewWorkflowTests.Input))], XmlPreviewAdmission.Profile);
        byte[] Encode(CatalogPreviewSubmissionV1 value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (var disabled = await PreviewHost.StartAsync(test))
        {
            using var refused = await disabled.SubmitAsync(await disabled.TokenAsync(), Guid.NewGuid(), Encode(body));
            await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
        }
        await using var host = await PreviewHost.StartAsync(test, xmlEnabled: true);
        var token = await host.TokenAsync(); var key = Guid.NewGuid();
        using var created = await host.SubmitAsync(token, key, Encode(body));
        var value = await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
        var run = Guid.Parse(value.GetProperty("run").GetProperty("runId").GetString()!);
        Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(new XmlPreviewPersistenceTests.Backend(), new() { XmlEnabled = true })
            .ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid()));
        using var read = await host.Client.GetAsync(host.RunPath(run));
        var result = await CatalogHttpTestSupport.JsonAsync(read);
        var metadata = result.GetProperty("outputs")[0].GetProperty("xmlMetadata");
        Assert.Equal("weft.xml-boundary.v1", metadata.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("schemaHints").ValueKind);
        using var download = await host.Client.GetAsync(host.RunPath(run) + "/outputs/0");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/xml", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(XmlPreviewWorkflowTests.Input, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        using var replay = await host.SubmitAsync(token, key, Encode(body));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var injected = Encoding.UTF8.GetString(Encode(body)).Replace("\"content\":", "\"xmlMetadata\":null,\"content\":", StringComparison.Ordinal);
        using var refusal = await host.SubmitAsync(token, Guid.NewGuid(), Encoding.UTF8.GetBytes(injected));
        await ErrorAsync(refusal, HttpStatusCode.BadRequest, "invalidRequest");
    }

    [PostgresFact]
    public async Task Csv_profile_is_not_admitted_when_only_legacy_previews_are_enabled()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, CsvPreviewTestData.Native);
        await using var host = await PreviewHost.StartAsync(test);
        var body = new CatalogPreviewSubmissionV1(new("all"), [], [new("")], CsvDocumentPolicy.Profile);
        using var response = await host.SubmitAsync(await host.TokenAsync(), Guid.NewGuid(),
            JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidRequest");
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
    }

    [PostgresFact]
    public async Task Csv_http_profile_admits_explicit_empty_content_but_not_absence_or_caller_dialects()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, CsvPreviewTestData.Native);
        await using var host = await PreviewHost.StartAsync(test, csvEnabled: true);
        var token = await host.TokenAsync();
        var body = new CatalogPreviewSubmissionV1(new("all"), [], [new("")], CsvDocumentPolicy.Profile);
        byte[] Encode(CatalogPreviewSubmissionV1 value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var created = await host.SubmitAsync(token, Guid.NewGuid(), Encode(body));
        var json = await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
        var run = Guid.Parse(json.GetProperty("run").GetProperty("runId").GetString()!);
        await test.Executor(new CsvPreviewTestData.Backend()).ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid());
        using var download = await host.Client.GetAsync(host.RunPath(run) + "/outputs/0");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/tab-separated-values", download.Content.Headers.ContentType!.MediaType);
        Assert.Empty(await download.Content.ReadAsByteArrayAsync());
        foreach (var invalid in new[] { body with { Inputs = [] }, body with { Profile = null }, body with { Profile = DocumentPreviewPolicy.Profile },
            body with { Target = new("named", "value") } })
        {
            using var refused = await host.SubmitAsync(token, Guid.NewGuid(), Encode(invalid));
            await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
        }
        var injected = Encoding.UTF8.GetString(Encode(body)).Replace("\"content\":\"\"", "\"content\":\"\",\"csvDialect\":{}", StringComparison.Ordinal);
        using var reconfigured = await host.SubmitAsync(token, Guid.NewGuid(), Encoding.UTF8.GetBytes(injected));
        await ErrorAsync(reconfigured, HttpStatusCode.BadRequest, "invalidRequest");
    }

    [DocumentPreviewFact]
    public async Task DocumentPreviewsPersistExplicitAdmissionExecuteRealEnvelopesAndStreamTheirMedia()
    {
        foreach (var family in new[] { "x12", "edifact" })
        {
            await using var test = await PreviewRunTestSupport.CreateAsync(fixture, DocumentPreviewTestData.Native(family));
            await using var host = await PreviewHost.StartAsync(test);
            var token = await host.TokenAsync();
            var key = Guid.NewGuid();
            var body = new CatalogPreviewSubmissionV1(new("all"), [],
                [new(Convert.ToBase64String(DocumentPreviewTestData.Input(family)))], DocumentPreviewPolicy.Profile);
            using var created = await host.SubmitAsync(token, key, JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var value = await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
            var run = Guid.Parse(value.GetProperty("run").GetProperty("runId").GetString()!);
            using var conflicting = await host.SubmitAsync(token, key, JsonSerializer.SerializeToUtf8Bytes(body with { Profile = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await ErrorAsync(conflicting, HttpStatusCode.Conflict, "idempotencyConflict");
            var root = FerruleServiceTestSupport.TemporaryRoot();
            try
            {
                await using var child = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(root, DockerWorkerProfile.Preview));
                Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(new FerrulePreviewBackend(child))
                    .ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid()));
                var saved = (await test.Service().ReadAsync(test.Alice, test.Revision.OrganizationId, run))!;
                Assert.True(saved.Outcome == PreviewRunOutcome.Succeeded, saved.Failure?.Message);
                Assert.Equal(DocumentPreviewPolicy.Profile, saved.Provenance!.Profile);
                var media = family == "x12" ? "application/edi-x12" : "application/edifact";
                Assert.Equal(media, Assert.Single(saved.Outputs).MediaType);
                using var response = await host.Client.GetAsync(host.RunPath(run) + "/outputs/0");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(media, response.Content.Headers.ContentType?.MediaType);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(saved.Outputs[0].Digest, Sha256Digest.FromBytes(bytes));
                Assert.Equal(Encoding.UTF8.GetString(DocumentPreviewTestData.Input(family)),
                    Encoding.UTF8.GetString(bytes).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal));
                Assert.Equal(PreviewExecutionOutcome.Finished, await test.Executor(new FerrulePreviewBackend(child))
                    .ExecuteAsync(test.Revision.OrganizationId, run, Guid.NewGuid()));
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [PostgresFact]
    public async Task SubmissionReplaysOneKeyReadsTheRunAndServesExactOutputBytes()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await using var host = await PreviewHost.StartAsync(test);
        var token = await host.TokenAsync();
        var key = Guid.NewGuid();
        var body = Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "west");

        using var created = await host.SubmitAsync(token, key, body);
        var first = await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created);
        Assert.Equal("created", first.GetProperty("outcome").GetString());
        var run = first.GetProperty("run");
        var runId = Guid.Parse(run.GetProperty("runId").GetString()!);
        Assert.Equal($"/api/v1/organizations/{test.Revision.OrganizationId:D}/previews/{runId:D}", created.Headers.Location?.OriginalString);
        Assert.True(created.Headers.CacheControl?.NoStore);
        Assert.Equal("queued", run.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("outcome").ValueKind);
        Assert.Equal(0, run.GetProperty("outputs").GetArrayLength());
        Assert.Equal(test.Revision.RevisionId.ToString("D"), run.GetProperty("revision").GetProperty("revisionId").GetString());

        using var replayed = await host.SubmitAsync(token, key, body);
        var second = await CatalogHttpTestSupport.JsonAsync(replayed);
        Assert.Equal("replayed", second.GetProperty("outcome").GetString());
        Assert.Equal(runId.ToString("D"), second.GetProperty("run").GetProperty("runId").GetString());
        using var conflict = await host.SubmitAsync(token, key, Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "east"));
        await ErrorAsync(conflict, HttpStatusCode.Conflict, "idempotencyConflict");

        var backend = new SucceedingBackend();
        Assert.Equal(PreviewExecutionOutcome.Completed,
            await test.Executor(backend).ExecuteAsync(test.Revision.OrganizationId, runId, Guid.NewGuid()));
        Assert.Equal(PreviewRunTestSupport.Order, backend.Primary);

        using var read = await host.Client.GetAsync(host.RunPath(runId));
        var completed = await CatalogHttpTestSupport.JsonAsync(read);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.Equal("succeeded", completed.GetProperty("outcome").GetString());
        Assert.Equal("2", completed.GetProperty("recordsWritten").GetString());
        var output = Assert.Single(completed.GetProperty("outputs").EnumerateArray());
        Assert.Equal(0, output.GetProperty("ordinal").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Output)), output.GetProperty("sha256").GetString());
        Assert.Equal(Output.Length, output.GetProperty("byteCount").GetInt32());

        using var bytes = await host.Client.GetAsync(host.RunPath(runId) + "/outputs/0");
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("application/json", bytes.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Output, await bytes.Content.ReadAsByteArrayAsync());
        Assert.Equal("sha-256=:" + Convert.ToBase64String(SHA256.HashData(Output)) + ":",
            Assert.Single(bytes.Headers.GetValues("Content-Digest")));
        Assert.Equal("nosniff", Assert.Single(bytes.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(bytes.Headers.CacheControl?.NoStore);

        using var missingOutput = await host.Client.GetAsync(host.RunPath(runId) + "/outputs/1");
        await ErrorAsync(missingOutput, HttpStatusCode.NotFound, "notFound");
        foreach (var ordinal in new[] { "01", "-1", "64", "x" })
        {
            using var invalid = await host.Client.GetAsync(host.RunPath(runId) + "/outputs/" + ordinal);
            await ErrorAsync(invalid, HttpStatusCode.BadRequest, "invalidRequest");
        }

        using var finished = await host.CancelAsync(token, runId);
        var finishedValue = await CatalogHttpTestSupport.JsonAsync(finished);
        Assert.Equal("finished", finishedValue.GetProperty("outcome").GetString());
        Assert.Equal("completed", finishedValue.GetProperty("run").GetProperty("state").GetString());
    }

    [PostgresFact]
    public async Task QueuedRunsCancelAndForeignUnknownOrUnauthorizedRunsAreNotFound()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await using var host = await PreviewHost.StartAsync(test);
        var token = await host.TokenAsync();
        using var created = await host.SubmitAsync(token, Guid.NewGuid(), Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "west"));
        var runId = Guid.Parse((await CatalogHttpTestSupport.JsonAsync(created, HttpStatusCode.Created))
            .GetProperty("run").GetProperty("runId").GetString()!);

        using var unknown = await host.Client.GetAsync(host.RunPath(Guid.NewGuid()));
        await ErrorAsync(unknown, HttpStatusCode.NotFound, "notFound");
        using var foreign = await host.Client.GetAsync($"/api/v1/organizations/{Guid.NewGuid():D}/previews/{runId:D}");
        await ErrorAsync(foreign, HttpStatusCode.NotFound, "notFound");
        using var noToken = await host.SendAsync(HttpMethod.Post, host.RunPath(runId) + "/cancel", null);
        Assert.NotEqual(HttpStatusCode.OK, noToken.StatusCode);

        using var canceled = await host.CancelAsync(token, runId);
        var value = await CatalogHttpTestSupport.JsonAsync(canceled);
        Assert.Equal("canceled", value.GetProperty("outcome").GetString());
        Assert.Equal("canceled", value.GetProperty("run").GetProperty("state").GetString());
        Assert.Equal("canceled", value.GetProperty("run").GetProperty("outcome").GetString());

        Assert.True(await host.PreviewActionAsync());
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1",
            test.Catalog.Seed.Alice);
        Assert.False(await host.PreviewActionAsync());
        using var refused = await host.SubmitAsync(token, Guid.NewGuid(), Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "west"));
        await ErrorAsync(refused, HttpStatusCode.NotFound, "notFound");
        using var hidden = await host.Client.GetAsync(host.RunPath(runId));
        await ErrorAsync(hidden, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
    }

    [PostgresFact]
    public async Task MalformedUnsupportedAndOversizedBodiesAreRefusedWithoutARun()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await using var host = await PreviewHost.StartAsync(test, bodyLimit: 4096);
        var token = await host.TokenAsync();
        var valid = Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "west");

        using var media = await host.SubmitAsync(token, Guid.NewGuid(), valid, "text/plain");
        await ErrorAsync(media, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
        using var charset = await host.SubmitAsync(token, Guid.NewGuid(), valid, "application/json; charset=utf-16");
        await ErrorAsync(charset, HttpStatusCode.UnsupportedMediaType, "unsupportedMediaType");
        using var noKey = await host.SendAsync(HttpMethod.Post, host.SubmitPath, token, valid, "application/json");
        await ErrorAsync(noKey, HttpStatusCode.BadRequest, "invalidRequest");
        foreach (var invalid in new[]
        {
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[{\"content\":\"e30=\"}],\"profile\":\"guess-from-filename\"}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[{\"content\":\"e30=\"}],\"extra\":1}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[{\"content\":\"e30=\"}],\"inputs\":[]}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[]}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[{\"content\":\"not base64!\"}]}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[],\"inputs\":[{\"name\":\"first\",\"content\":\"e30=\"}]}",
            "{\"target\":{\"kind\":\"named\"},\"parameters\":[],\"inputs\":[{\"content\":\"e30=\"}]}",
            "{\"target\":{\"kind\":\"all\"},\"parameters\":[{\"name\":\"p\",\"kind\":\"decimal\",\"value\":\"1\"}],\"inputs\":[{\"content\":\"e30=\"}]}",
            "[]"
        })
        {
            using var refused = await host.SubmitAsync(token, Guid.NewGuid(), Encoding.UTF8.GetBytes(invalid));
            await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidRequest");
        }
        using var oversized = await host.SubmitAsync(token, Guid.NewGuid(), Body(new byte[4096], PreviewRunTestSupport.Rates, "west"));
        await ErrorAsync(oversized, HttpStatusCode.RequestEntityTooLarge, "bodyLimit");
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));

        using var minimal = await host.SubmitAsync(token, Guid.NewGuid(),
            "{\"target\":{\"kind\":\"primary\"},\"parameters\":[],\"inputs\":[{\"content\":\"e30=\"}]}"u8.ToArray(), "application/json; charset=utf-8");
        Assert.Equal("queued", (await CatalogHttpTestSupport.JsonAsync(minimal, HttpStatusCode.Created)).GetProperty("run").GetProperty("state").GetString());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
    }

    [PostgresFact]
    public async Task DisabledPreviewsAreUnavailableAndAdvertiseNoFeature()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture);
        await using (var disabled = await PreviewHost.StartAsync(test, enabled: false))
        {
            using var context = await disabled.Client.GetAsync("/api/v1/catalog/context");
            Assert.False((await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("features").GetProperty("preview").GetBoolean());
            Assert.False(await disabled.PreviewActionAsync());
            var token = await disabled.TokenAsync();
            using var submit = await disabled.SubmitAsync(token, Guid.NewGuid(), Body(PreviewRunTestSupport.Order, PreviewRunTestSupport.Rates, "west"));
            await ErrorAsync(submit, HttpStatusCode.NotFound, "unavailable");
            using var read = await disabled.Client.GetAsync(disabled.RunPath(Guid.NewGuid()));
            await ErrorAsync(read, HttpStatusCode.NotFound, "unavailable");
        }
        await using var enabled = await PreviewHost.StartAsync(test);
        using var enabledContext = await enabled.Client.GetAsync("/api/v1/catalog/context");
        Assert.True((await CatalogHttpTestSupport.JsonAsync(enabledContext)).GetProperty("features").GetProperty("preview").GetBoolean());
        Assert.True(await enabled.PreviewActionAsync());
    }

    private static byte[] Body(byte[] primary, byte[] rates, string region) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        target = new { kind = "all", name = (string?)null },
        parameters = new[] { new { name = "region", kind = "string", value = region } },
        inputs = new[] { new { name = (string?)null, content = Convert.ToBase64String(primary) }, new { name = (string?)"rates", content = Convert.ToBase64String(rates) } }
    });

    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var value = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal(code, value.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private sealed class SucceedingBackend : IMappingPreviewBackend
    {
        public byte[]? Primary { get; private set; }

        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default)
        {
            Primary = request.Primary.CopyBytes();
            var provenance = new PreviewProvenance(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "engine", "image", "adapter", true);
            return Task.FromResult(new MappingPreviewResult(MappingPreviewStatus.Succeeded, provenance, PreviewPolicy.Profile, 2,
                [new PreviewOutputArtifact("primary-output", "", false, PreviewPolicy.MediaType, 2, Output)], null));
        }
    }

    private sealed class PreviewHost : IAsyncDisposable
    {
        private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();
        private readonly WebApplication app;
        private readonly PostgresCatalogDatabase database;
        private readonly RevisionScope revision;

        private PreviewHost(WebApplication app, PostgresCatalogDatabase database, Uri origin, RevisionScope revision)
        {
            this.app = app;
            this.database = database;
            this.revision = revision;
            Origin = origin;
            Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = true, CookieContainer = new() })
            {
                BaseAddress = origin,
                Timeout = TimeSpan.FromSeconds(40),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
        }

        internal Uri Origin { get; }
        internal HttpClient Client { get; }
        internal string SubmitPath => CatalogRevisionReportHttpTestSupport.RevisionPath(revision) + "/previews";
        internal string RunPath(Guid run) => $"/api/v1/organizations/{revision.OrganizationId:D}/previews/{run:D}";

        internal static async Task<PreviewHost> StartAsync(PreviewRunTestSupport test, bool enabled = true,
            int bodyLimit = CatalogPreviewOptions.MaximumBodyBytes, bool csvEnabled = false, bool xmlEnabled = false)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
            WebApplication? app = null;
            PostgresCatalogDatabase? database = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(server =>
                    server.Listen(IPAddress.Loopback, origin.Port, listen => listen.Protocols = HttpProtocols.Http1));
                builder.Services.AddWeftApi();
                builder.Services.AddSingleton(Keys);
                builder.Services.AddWeftCatalog(new(true, true, test.Alice.PrincipalId.Value, test.Revision.OrganizationId,
                    origin.GetLeftPart(UriPartial.Authority)), builder.Environment, new CatalogMetadataEditOptions(),
                    new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(), new CatalogRevisionReportOptions());
                database = new(test.Case.CatalogConnectionString);
                builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(database));
                builder.Services.AddSingleton(test.Service(new() { XmlEnabled = xmlEnabled }));
                builder.Services.AddSingleton<IPrivateObjectStore>(test.Objects);
                builder.Services.AddSingleton(_ => new PostgresRevisionReportDatabase(test.Case.ReportConnectionString));
                builder.Services.AddSingleton<IRevisionReportMetadataReader, PostgresRevisionReportMetadataReader>();
                builder.Services.AddWeftPreviews(new CatalogPreviewOptions(enabled, bodyLimit, csvEnabled, xmlEnabled: xmlEnabled));
                app = builder.Build();
                app.MapWeftCatalog();
                reservation.Stop();
                await app.StartAsync();
                return new(app, database, origin, test.Revision);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                if (database is not null) await database.DisposeAsync();
                throw;
            }
            finally { reservation.Stop(); }
        }

        internal async Task<bool> PreviewActionAsync()
        {
            using var response = await Client.GetAsync(
                $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}");
            return (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("access").GetProperty("actions").GetProperty("preview").GetBoolean();
        }

        internal async Task<string> TokenAsync()
        {
            using var response = await Client.GetAsync("/api/v1/catalog/context");
            return (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("requestToken").GetString()!;
        }

        internal Task<HttpResponseMessage> SubmitAsync(string token, Guid key, byte[] body, string contentType = "application/json") =>
            SendAsync(HttpMethod.Post, SubmitPath, token, body, contentType, key);

        internal Task<HttpResponseMessage> CancelAsync(string token, Guid run) =>
            SendAsync(HttpMethod.Post, RunPath(run) + "/cancel", token);

        internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token,
            byte[]? body = null, string? contentType = null, Guid? key = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = new ByteArrayContent(body ?? []) };
            if (contentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (token is not null) request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token);
            if (key is { } value) request.Headers.TryAddWithoutValidation("Idempotency-Key", value.ToString("D"));
            request.Headers.TryAddWithoutValidation("Origin", Origin.GetLeftPart(UriPartial.Authority));
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
