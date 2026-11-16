using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Automation;
using Braid.Application.Regression;
using Braid.Contracts.Catalog;
using Braid.Contracts.Regression;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class RegressionCaseHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Csv_case_uses_real_native_ports_and_reopens_the_pinned_empty_input_dialect()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, nativeBytes: CsvPreviewTestData.Native);
        var csv = new RegressionRuntimeIdentity(data.Runtime.BackendId, data.Runtime.EngineRevision, data.Runtime.BackendBuild, "csv-adapter", CsvDocumentPolicy.Profile);
        await using var http = await CaseHttp.StartAsync(data, csvRuntime: csv);
        using (var capabilities = await http.Client.GetAsync(http.BasePath + "/capabilities"))
        {
            Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
            Assert.Equal(CsvDocumentPolicy.Profile, (await Json(capabilities)).GetProperty("csvRuntime").GetProperty("profile").GetString());
            Assert.False(http.NativeResolved);
        }
        using (var native = new MemoryStream(CsvPreviewTestData.Native))
            await http.NativeObjects.PublishCreateOnlyAsync(data.Native.Id.Scope, data.Native, native);
        var value = new RegressionCaseVersion(data.CaseId, 1, data.Actor.PrincipalId, "CSV", data.Native.Id.Scope, data.Native.Digest, csv,
            new(PreviewTargetSelection.All, "2026-10-31T00:00:00Z", PreviewLimits.Default, new(null, [], CsvPreviewTestData.Dialect), [], []),
            RegressionComparisonService.CsvRowsPolicy, RegressionOutputSet.Capture([
                RegressionOutput.Capture(RegressionOutputKey.Primary, [], mediaType: "text/tab-separated-values", csvDialect: CsvPreviewTestData.Dialect)]));
        using var saved = await http.PutAsync(Body(value), await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var reopened = (await data.Reader.ReadAsync(data.Actor, data.Mapping, data.CaseId, 1))!;
        Assert.Empty(reopened.Replay.Primary.CopyBytes());
        Assert.Equal(CsvPreviewTestData.Dialect, reopened.Replay.Primary.CsvDialect);
        using var response = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/tab-separated-values", (await Json(response)).GetProperty("definition").GetProperty("expected")[0].GetProperty("mediaType").GetString());
        var changed = Body(value);
        changed["expected"]![0]!["csvDialect"]!["hasHeaderRow"] = true;
        using var refused = await http.PutAsync(changed, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(1, http.Writes);
    }

    [PostgresFact]
    public async Task Csv_revocation_after_native_metadata_read_prevents_private_store_resolution()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture, nativeBytes: CsvPreviewTestData.Native);
        var csv = new RegressionRuntimeIdentity(data.Runtime.BackendId, data.Runtime.EngineRevision, data.Runtime.BackendBuild, "csv-adapter", CsvDocumentPolicy.Profile);
        await using var http = await CaseHttp.StartAsync(data, csvRuntime: csv);
        http.AfterNativeSource = () => data.RoleAsync("viewer");
        var body = Body(data.Version(1));
        body["runtime"]!["profile"] = csv.Profile;
        body["runtime"]!["adapterBuild"] = csv.AdapterBuild;
        body["primaryBase64Url"] = "";
        body["comparisonPolicy"] = RegressionComparisonService.CsvRowsPolicy;
        body["expected"]![0]!["mediaType"] = "text/tab-separated-values";
        body["expected"]![0]!["csvDialect"] = JsonNode.Parse(CsvDialectJson.Encode(CsvPreviewTestData.Dialect));
        using var response = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(http.NativeResolved);
        Assert.Equal(0, http.Writes);
    }
    [PostgresFact]
    public async Task Publish_reopen_append_and_retry_keep_exact_private_bytes_and_versions()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        using var first = await http.PutAsync(body, token: await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("created", (await Json(first)).GetProperty("outcome").GetString());
        using var read = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var reopened = await Json(read);
        Assert.Equal(body["primaryBase64Url"]!.GetValue<string>(), reopened.GetProperty("definition").GetProperty("primaryBase64Url").GetString());
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.Null(read.Headers.ETag);
        using var append = await http.PutAsync(Body(data.Version(2)), token: await http.TokenAsync(), version: 2);
        Assert.Equal(HttpStatusCode.Created, append.StatusCode);
        using var retry = await http.PutAsync(body, token: await http.TokenAsync());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("replayed", (await Json(retry)).GetProperty("outcome").GetString());
        using var list = await http.Client.GetAsync(http.BasePath);
        Assert.Equal("2", (await Json(list)).GetProperty("cases")[0].GetProperty("version").GetString());
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_case_versions"));
    }

    [PostgresFact]
    public async Task Missing_mutation_token_rejects_before_body_or_store_access()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        using var response = await http.PutAsync(Body(data.Version(1)), token: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, http.BodyReads);
        Assert.Equal(0, http.Writes);
    }

    [PostgresFact]
    public async Task Viewers_see_metadata_but_neither_private_samples_nor_publication()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await data.RoleAsync("viewer");
        await using var http = await CaseHttp.StartAsync(data);
        using var metadata = await http.Client.GetAsync(http.BasePath);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.DoesNotContain("primaryBase64Url", await metadata.Content.ReadAsStringAsync());
        using var capabilities = await http.Client.GetAsync(http.BasePath + "/capabilities");
        var flags = await Json(capabilities);
        Assert.False(flags.GetProperty("readSamples").GetBoolean());
        Assert.False(flags.GetProperty("publishCases").GetBoolean());
        using var sample = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.NotFound, sample.StatusCode);
        using var save = await http.PutAsync(Body(data.Version(2)), await http.TokenAsync(), 2);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(0, http.BodyReads);
        Assert.Equal(0, http.Writes);
    }

    [PostgresFact]
    public async Task Runners_can_read_samples_but_cannot_publish_versions()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await data.RoleAsync("runner");
        await using var http = await CaseHttp.StartAsync(data);
        using var sample = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.OK, sample.StatusCode);
        using var save = await http.PutAsync(Body(data.Version(2)), await http.TokenAsync(), 2);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(0, http.Writes);
    }

    [PostgresFact]
    public async Task Revocation_while_reading_the_body_prevents_publication()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var token = await http.TokenAsync();
        http.GateBody = true;
        var request = http.PutAsync(Body(data.Version(1)), token);
        try
        {
            await http.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await data.RoleAsync("viewer");
            http.ReleaseBody.TrySetResult();
            using var response = await request;
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, http.Writes);
            Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_case_versions"));
        }
        finally { http.ReleaseBody.TrySetResult(); }
    }

    [PostgresFact]
    public async Task Revocation_after_private_store_read_drops_the_prepared_response()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await data.Writer.SaveAsync(data.Actor, data.Version(1));
        await using var http = await CaseHttp.StartAsync(data);
        http.AfterRead = () => data.RoleAsync("viewer");
        using var response = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(WebEncoders.Base64UrlEncode("private-input"u8.ToArray()), await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task Lost_response_after_commit_requires_explicit_retry_and_returns_original_receipt()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        var token = await http.TokenAsync();
        http.AfterWrite = () => throw new InvalidOperationException("private-database-response-loss");
        using var lost = await http.PutAsync(body, token);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        Assert.DoesNotContain("private-database", await lost.Content.ReadAsStringAsync());
        Assert.Equal(1, http.Writes);
        http.AfterWrite = null;
        using var retry = await http.PutAsync(body, token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("replayed", (await Json(retry)).GetProperty("outcome").GetString());
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_case_versions"));
    }

    public static TheoryData<string> MalformedCases => ["unknown", "owner", "null", "base64-padding", "base64-whitespace", "bad-kind", "duplicate-input"];

    [RegressionPostgresTheory]
    [MemberData(nameof(MalformedCases))]
    public async Task Invalid_shapes_are_refused_without_publishing(string mutation)
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        switch (mutation)
        {
            case "unknown": body["unknown"] = 1; break;
            case "owner": body["owner"] = Guid.NewGuid().ToString(); break;
            case "null": body["target"] = null; break;
            case "base64-padding": body["primaryBase64Url"] = "e30="; break;
            case "base64-whitespace": body["primaryBase64Url"] = " e30"; break;
            case "bad-kind": body["parameters"] = new JsonArray(new JsonObject { ["name"] = "n", ["kind"] = "decimal", ["value"] = "1" }); break;
            case "duplicate-input":
                body["namedInputs"] = new JsonArray(
                new JsonObject { ["name"] = "same", ["bytesBase64Url"] = "e30" }, new JsonObject { ["name"] = "same", ["bytesBase64Url"] = "e30" }); break;
        }
        using var response = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, http.Writes);
    }

    [PostgresFact]
    public async Task Stale_runtime_and_changed_same_version_have_distinct_conflicts()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        body["runtime"]!["adapterBuild"] = "stale-build";
        using var stale = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(0, http.Writes);
        body = Body(data.Version(1));
        using var created = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        body["name"] = "different";
        using var conflict = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [PostgresFact]
    public async Task Maximum_binary_samples_and_expected_outputs_remain_bounded_over_HTTP()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        var input = Enumerable.Repeat((byte)251, PreviewPolicy.MaximumInputBytes).ToArray();
        var expected = Enumerable.Repeat((byte)255, PreviewPolicy.MaximumOutputBytes).ToArray();
        body["primaryBase64Url"] = WebEncoders.Base64UrlEncode(input);
        body["expected"] = new JsonArray(new JsonObject { ["name"] = null, ["bytesBase64Url"] = WebEncoders.Base64UrlEncode(expected) });
        using var save = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        using var read = await http.Client.GetAsync(http.VersionPath);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.InRange(read.Content.Headers.ContentLength!.Value, 1, CatalogRegressionCaseRequest.MaximumBodyBytes);
        var definition = (await Json(read)).GetProperty("definition");
        Assert.Equal(input, WebEncoders.Base64UrlDecode(definition.GetProperty("primaryBase64Url").GetString()!));
        Assert.Equal(expected, WebEncoders.Base64UrlDecode(definition.GetProperty("expected")[0].GetProperty("bytesBase64Url").GetString()!));
    }

    [PostgresFact]
    public async Task Typed_lexical_parameters_named_inputs_and_fractional_clock_are_preserved()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var body = Body(data.Version(1));
        body["currentDateTime"] = "2026-10-20T01:02:03.123456789-07:00";
        body["parameters"] = new JsonArray(
            new JsonObject { ["name"] = "integer", ["kind"] = "integer", ["value"] = "9007199254740993" },
            new JsonObject { ["name"] = "float", ["kind"] = "float", ["value"] = "-0.0" },
            new JsonObject { ["name"] = "string", ["kind"] = "string", ["value"] = "private\0value" });
        body["namedInputs"] = new JsonArray(new JsonObject { ["name"] = "Lookup", ["bytesBase64Url"] = "e30" },
            new JsonObject { ["name"] = "lookup", ["bytesBase64Url"] = "W10" });
        using var save = await http.PutAsync(body, await http.TokenAsync());
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        using var read = await http.Client.GetAsync(http.VersionPath);
        var definition = (await Json(read)).GetProperty("definition");
        Assert.Equal("2026-10-20T01:02:03.123456789-07:00", definition.GetProperty("currentDateTime").GetString());
        Assert.Contains(definition.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("value").GetString() == "9007199254740993");
        Assert.Contains(definition.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("value").GetString() == "-0.0");
        Assert.Contains(definition.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("value").GetString() == "private\0value");
        Assert.Equal(new[] { "Lookup", "lookup" }, definition.GetProperty("namedInputs").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }

    [PostgresFact]
    public async Task Client_cancellation_during_body_read_never_reaches_the_writer()
    {
        await using var data = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        await using var http = await CaseHttp.StartAsync(data);
        var token = await http.TokenAsync();
        http.GateBody = true;
        using var cancellation = new CancellationTokenSource();
        var request = http.PutAsync(Body(data.Version(1)), token, cancellationToken: cancellation.Token);
        try
        {
            await http.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await http.PutEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, http.Writes);
            Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.regression_case_versions"));
        }
        finally { http.ReleaseBody.TrySetResult(); }
    }

    [Fact]
    public async Task Disabled_routes_need_no_catalog_or_private_store()
    {
        await using var http = await CaseHttp.StartAsync(null);
        using var response = await http.Client.GetAsync(http.BasePath + "/capabilities");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Declared_oversized_body_is_refused_before_reading()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = CatalogRegressionCaseRequest.MediaType;
        context.Request.ContentLength = CatalogRegressionCaseRequest.MaximumBodyBytes + 1L;
        context.Request.Body = new UnreadableBody();
        var failure = await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => CatalogRegressionCaseRequest.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.Status);
    }

    [Fact]
    public async Task A_stream_without_content_length_stops_at_the_body_bound()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = CatalogRegressionCaseRequest.MediaType;
        using var body = new RepeatingBody(CatalogRegressionCaseRequest.MaximumBodyBytes + 1024 * 1024L);
        context.Request.Body = body;
        var failure = await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => CatalogRegressionCaseRequest.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.Status);
        Assert.InRange(body.ReturnedBytes, CatalogRegressionCaseRequest.MaximumBodyBytes + 1L, CatalogRegressionCaseRequest.MaximumBodyBytes + 64 * 1024L);
    }

    [Theory]
    [InlineData("{\"name\":\"a\",\"name\":\"b\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Duplicate_or_nonobject_JSON_is_not_an_admitted_body(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = CatalogRegressionCaseRequest.MediaType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var failure = await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => CatalogRegressionCaseRequest.ReadAsync(context.Request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, failure.Status);
    }

    [Fact]
    public async Task Duplicate_member_is_rejected_in_an_otherwise_complete_request()
    {
        var runtime = new RegressionRuntimeIdentity("fixture", "engine", "build", "adapter", PreviewPolicy.Profile);
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, new(Guid.NewGuid()), "case",
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Sha256Digest.FromBytes([1]), runtime,
            new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default, new(null, "{}"u8), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([]));
        var valid = Body(value).ToJsonString();
        static HttpRequest Request(string text)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = CatalogRegressionCaseRequest.MediaType;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return context.Request;
        }
        Assert.NotNull(await CatalogRegressionCaseRequest.ReadAsync(Request(valid), CancellationToken.None));
        var duplicate = "{\"name\":\"duplicate\"," + valid[1..];
        var error = await Assert.ThrowsAsync<CatalogRegressionCaseRequestException>(() => CatalogRegressionCaseRequest.ReadAsync(Request(duplicate), CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
    }

    private static JsonObject Body(RegressionCaseVersion value)
    {
        var dto = new RegressionCasePutV1(value.Name, value.Revision.RevisionId.ToString("D"),
            new(value.Runtime.BackendId, value.Runtime.EngineRevision, value.Runtime.BackendBuild, value.Runtime.AdapterBuild, value.Runtime.Profile),
            value.Replay.CurrentDateTime, new("all", null), new(value.Replay.Limits.MaximumInputBytes, value.Replay.Limits.MaximumOutputBytes,
                value.Replay.Limits.MaximumArtifacts, value.Replay.Limits.MaximumTextBytes), WebEncoders.Base64UrlEncode(value.Replay.Primary.CopyBytes()),
            [], [], value.ComparisonPolicy, value.Expected.Outputs.Select(output => new RegressionDocumentV1(output.Key.Name,
                WebEncoders.Base64UrlEncode(output.CopyBytes()), value.Runtime.Profile == PreviewPolicy.Profile ? null : output.MediaType,
                output.CsvDialect is { } dialect ? new CsvDocumentDialectV1(dialect.Schema, dialect.Delimiter, dialect.Quote,
                    dialect.QuoteDisabled, dialect.HasHeaderRow, dialect.Utf8Bom, dialect.PreserveEmptyStrings) : null)).ToArray());
        return JsonSerializer.SerializeToNode(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.Clone();
    }

    private sealed class CaseHttp : IAsyncDisposable
    {
        private static readonly IDataProtectionProvider Keys = new EphemeralDataProtectionProvider();
        private readonly WebApplication app;
        private readonly PostgresCatalogDatabase? catalog;
        private readonly RegressionCasePostgresTestSupport? data;
        private readonly Lazy<PostgresRegressionRunStore>? runReader;
        private readonly Lazy<PostgresRegressionRunStore>? runWriter;
        private int bodyReads;
        private int writes;
        private PrivateStorageTestRoot? nativeRoot;
        private FileSystemPrivateObjectStore? nativeStore;
        private readonly object nativeLock = new();
        internal HttpClient Client { get; }
        internal string BasePath { get; }
        internal string RunsPath => BasePath.Replace("regression-cases", "regression-runs", StringComparison.Ordinal);
        internal PostgresRegressionRunStore RunReader => runReader!.Value;
        internal PostgresRegressionRunStore RunWriter => runWriter!.Value;
        internal string VersionPath => BasePath + "/" + (data?.CaseId.ToString("D") ?? Guid.Empty.ToString("D")) + "/versions/1";
        internal bool GateBody { get; set; }
        internal TaskCompletionSource BodyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PutEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<Task>? AfterRead { get; set; }
        internal Func<Task>? AfterWrite { get; set; }
        internal Func<Task>? AfterNativeSource { get; set; }
        internal bool NativeResolved => nativeStore is not null;
        internal IPrivateObjectStore NativeObjects
        {
            get
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                lock (nativeLock)
                {
                    nativeRoot ??= new PrivateStorageTestRoot();
                    return nativeStore ??= nativeRoot.Initialize();
                }
            }
        }
        internal int BodyReads => Volatile.Read(ref bodyReads);
        internal int Writes => Volatile.Read(ref writes);

        private CaseHttp(WebApplication app, PostgresCatalogDatabase? catalog, RegressionCasePostgresTestSupport? data, Uri origin)
        {
            this.app = app;
            this.catalog = catalog;
            this.data = data;
            if (data is not null)
            {
                runReader = new(() => new(data.Case.RegressionConnectionString("braid_run_reader"), writer: false));
                runWriter = new(() => new(data.Case.RegressionConnectionString("braid_run_writer"), writer: true));
            }
            Client = new(new HttpClientHandler { UseCookies = true, CookieContainer = new(), UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            var mapping = data?.Mapping ?? new MappingScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            BasePath = $"/api/v1/organizations/{mapping.OrganizationId:D}/projects/{mapping.ProjectId:D}/mappings/{mapping.MappingId:D}/regression-cases";
        }

        internal static async Task<CaseHttp> StartAsync(RegressionCasePostgresTestSupport? data,
            RegressionRuntimeIdentity? documentRuntime = null, RegressionRuntimeIdentity? csvRuntime = null)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
            WebApplication? app = null;
            PostgresCatalogDatabase? catalog = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, origin.Port));
                builder.Services.AddBraidApi();
                builder.Services.AddSingleton(Keys);
                var options = data is null ? new CatalogApiOptions() : new CatalogApiOptions(true, true, data.Actor.PrincipalId.Value,
                    data.Mapping.OrganizationId, origin.GetLeftPart(UriPartial.Authority));
                builder.Services.AddBraidCatalog(options, builder.Environment);
                CaseHttp? support = null;
                if (data is not null)
                {
                    builder.Services.AddSingleton(new CatalogAutomationOptions(true));
                    builder.Services.AddSingleton(NotificationConfiguration.Disabled);
                    builder.Services.AddSingleton<IAutomationStore>(_ => new PostgresAutomationStore(data.Case.RegressionConnectionString("braid_run_writer"), true));
                    catalog = new(data.Case.CatalogConnectionString);
                    builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(catalog));
                    builder.Services.AddSingleton(_ => new PostgresRevisionReportDatabase(data.Case.ReportConnectionString));
                    builder.Services.AddSingleton(provider => new PostgresRevisionReportMetadataReader(provider.GetRequiredService<PostgresRevisionReportDatabase>()));
                    builder.Services.AddSingleton<IRevisionReportMetadataReader>(provider => new NativeMetadataPort(
                        provider.GetRequiredService<PostgresRevisionReportMetadataReader>(), support!));
                    builder.Services.AddSingleton<IPrivateObjectStore>(_ => new NativeObjectPort(support!));
                    builder.Services.AddSingleton<IRegressionCaseReader>(_ => new CasePort(support!));
                    builder.Services.AddSingleton<IRegressionCaseWriter>(_ => new CasePort(support!));
                    builder.Services.AddSingleton<IRegressionRunReader>(_ => new RunPort(support!, false));
                    builder.Services.AddSingleton<IRegressionRunStore>(_ => new RunPort(support!, true));
                    builder.Services.AddSingleton(provider => new RegressionRunReadService(provider.GetRequiredService<IRegressionRunReader>(), provider.GetRequiredService<IRegressionCaseReader>()));
                }
                builder.Services.AddBraidRegressionCases(data is null ? new() : new(true, data.Runtime, documentRuntime, csvRuntime));
                builder.Services.AddBraidRegressionRuns(new(data is not null));
                app = builder.Build();
                support = new(app, catalog, data, origin);
                app.Use(async (context, next) =>
                {
                    var put = HttpMethods.IsPut(context.Request.Method);
                    if (put) context.Request.Body = new CountedBody(context.Request.Body, support);
                    try { await next(context); }
                    finally { if (put) support.PutEnded.TrySetResult(); }
                });
                app.MapBraidCatalog();
                app.MapBraidRegressionCases();
                app.MapBraidRegressionRuns();
                app.MapBraidAutomation();
                reservation.Stop();
                await app.StartAsync();
                return support;
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                if (catalog is not null) await catalog.DisposeAsync();
                throw;
            }
            finally { reservation.Stop(); }
        }

        internal async Task<string> TokenAsync()
        {
            using var response = await Client.GetAsync("/api/v1/catalog/context");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).GetProperty("requestToken").GetString()!;
        }
        internal async Task<HttpResponseMessage> PutAsync(JsonObject body, string? token, long version = 1, CancellationToken cancellationToken = default)
        {
            var path = BasePath + "/" + data!.CaseId.ToString("D") + "/versions/" + version;
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8) };
            request.Content.Headers.ContentType = new(CatalogRegressionCaseRequest.MediaType);
            request.Headers.TryAddWithoutValidation("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token is not null) request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            return await Client.SendAsync(request, cancellationToken);
        }
        internal async Task<HttpResponseMessage> PutRunAsync(Guid id, JsonObject body, string? token, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, RunsPath + "/" + id.ToString("D"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8) };
            request.Content.Headers.ContentType = new(CatalogRegressionRunRequest.MediaType);
            request.Headers.TryAddWithoutValidation("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token is not null) request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            return await Client.SendAsync(request, cancellationToken);
        }
        public Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterCaseId = null, CancellationToken cancellationToken = default) =>
            data!.Reader.ListAsync(actor, mapping, limit, afterCaseId, cancellationToken);
        public async Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version, CancellationToken cancellationToken = default)
        {
            var result = await data!.Reader.ReadAsync(actor, mapping, caseId, version, cancellationToken);
            if (AfterRead is { } callback) await callback();
            return result;
        }
        public async Task<RegressionCaseSaveResult> SaveAsync(ActorContext actor, RegressionCaseVersion version, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writes);
            var result = await data!.Writer.SaveAsync(actor, version, cancellationToken);
            if (AfterWrite is { } callback) await callback();
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseBody.TrySetResult();
            Client.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
            if (runReader?.IsValueCreated == true) await runReader.Value.DisposeAsync();
            if (runWriter?.IsValueCreated == true) await runWriter.Value.DisposeAsync();
            if (catalog is not null) await catalog.DisposeAsync();
            if (OperatingSystem.IsLinux())
            {
                if (nativeStore is not null) await nativeStore.DisposeAsync();
                nativeRoot?.Dispose();
            }
        }
        private sealed class NativeMetadataPort(IRevisionReportMetadataReader reader, CaseHttp owner) : IRevisionReportMetadataReader
        {
            public async Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
            {
                var value = await reader.GetSourceAsync(actor, scope, cancellationToken);
                if (owner.AfterNativeSource is { } callback) await callback();
                return value;
            }
            public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default) =>
                reader.GetCurrentAsync(actor, key, cancellationToken);
            public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation,
                Guid derivationId, CancellationToken cancellationToken = default) => reader.GetRetainedAsync(actor, scope, operation, derivationId, cancellationToken);
        }
        private sealed class NativeObjectPort(CaseHttp owner) : IPrivateObjectStore
        {
            public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) =>
                owner.NativeObjects.ReadVerifiedAsync(expectedScope, reference, cancellationToken);
            public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference,
                Stream source, CancellationToken cancellationToken = default) => owner.NativeObjects.PublishCreateOnlyAsync(expectedScope, reference, source, cancellationToken);
        }
        private sealed class RunPort(CaseHttp owner, bool writer) : IRegressionRunStore
        {
            private PostgresRegressionRunStore Store => writer ? owner.RunWriter : owner.RunReader;
            public Task<RegressionRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default) => Store.ReadAsync(actor, organizationId, runId, token);
            public Task<RegressionRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null, CancellationToken token = default) => Store.ListAsync(actor, mapping, limit, afterRunId, token);
            public Task<RegressionRunSubmitOutcome> SubmitAsync(RegressionRunSubmission submission, CancellationToken token = default) => Store.SubmitAsync(submission, token);
            public Task<RegressionRunClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid worker, CancellationToken token = default) => Store.ClaimAsync(organizationId, runId, worker, token);
            public Task<bool> ObserveAsync(RegressionRunClaim claim, CancellationToken token = default) => Store.ObserveAsync(claim, token);
            public Task<bool> CompleteAsync(RegressionRunClaim claim, RegressionCaseExecution completion, CancellationToken token = default) => Store.CompleteAsync(claim, completion, token);
            public Task<bool> ReleaseAsync(RegressionRunClaim claim, CancellationToken token = default) => Store.ReleaseAsync(claim, token);
            public Task<bool> CancelAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken token = default) => Store.CancelAsync(actor, organizationId, runId, token);
        }
        private sealed class CasePort(CaseHttp owner) : IRegressionCaseReader, IRegressionCaseWriter
        {
            public Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit,
                Guid? afterCaseId = null, CancellationToken cancellationToken = default) => owner.ListAsync(actor, mapping, limit, afterCaseId, cancellationToken);
            public Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
                CancellationToken cancellationToken = default) => owner.ReadAsync(actor, mapping, caseId, version, cancellationToken);
            public Task<RegressionCaseSaveResult> SaveAsync(ActorContext actor, RegressionCaseVersion version,
                CancellationToken cancellationToken = default) => owner.SaveAsync(actor, version, cancellationToken);
        }
        private sealed class CountedBody(Stream inner, CaseHttp owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.bodyReads);
                var count = await inner.ReadAsync(buffer, cancellationToken);
                if (owner.GateBody && count > 0)
                {
                    owner.BodyEntered.TrySetResult();
                    await owner.ReleaseBody.Task.WaitAsync(cancellationToken);
                }
                return count;
            }
        }
    }

    private sealed class UnreadableBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The body must not be read.");
    }

    private sealed class RepeatingBody(long length) : Stream
    {
        internal long ReturnedBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => ReturnedBytes; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - ReturnedBytes);
            buffer.Span[..count].Fill((byte)' ');
            ReturnedBytes += count;
            return ValueTask.FromResult(count);
        }
    }
}
