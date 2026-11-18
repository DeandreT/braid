using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class NativePreviewReplayHttpTests(PostgresFixture fixture)
{
    [DocumentPreviewFact]
    public Task FlexText_exact_bytes_survive_http_native_execution_reopen_and_idempotent_replay() =>
        Verify(NativeDocumentFormat.FlexText, "flextext", "Résumé 🧵"u8.ToArray(), null);

    [DocumentPreviewFact]
    public Task Protobuf_replays_completed_and_truncated_messages() =>
        Verify(NativeDocumentFormat.Protobuf, "protobuf", [10, 3, 65, 0, 66], [10, 3, 65]);

    [DocumentPreviewFact]
    public Task Xbrl_replays_completed_and_invalid_numeric_instances() =>
        Verify(NativeDocumentFormat.Xbrl, "xbrl-instance", DocumentPreviewTestData.XbrlInstance("5"), DocumentPreviewTestData.XbrlInstance("not-a-number"));

    [DocumentPreviewFact]
    public Task Valid_workbook_output_is_refused_before_worker_dispatch_or_persistence() =>
        Verify(NativeDocumentFormat.Xlsx, "xlsx-flat", Workbook("Orders"), null);

    private async Task Verify(NativeDocumentFormat format, string fixtureName, byte[] valid, byte[]? corrupt)
    {
        var native = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixtureName + ".native.json"));
        await using var test = await PreviewRunTestSupport.CreateAsync(fixture, native, migrationCount: MigrationRunner.EmbeddedMigrations.Count);
        var revision = test.Revision;
        Assert.NotNull(await test.Catalog.Reader.GetOrganizationAdmissionAsync(test.Alice, revision.OrganizationId));
        var origin = new Uri("http://127.0.0.1:53781"); // In-process HTTP, no listener.
        var options = new PreviewRunOptions { NativeFormats = [format] };
        await using var reports = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidCatalog(new(true, true, test.Alice.PrincipalId.Value, revision.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
            new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(), new CatalogRevisionReportOptions());
        builder.Services.AddSingleton<ICatalogReader>(test.Catalog.Reader);
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(new PostgresRevisionReportMetadataReader(reports));
        builder.Services.AddSingleton(test.Objects);
        builder.Services.AddSingleton(test.Service(options));
        builder.Services.AddBraidPreviews(new(enabled: true, nativeFormats: [format]));
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.Connection.RemoteIpAddress = IPAddress.Loopback; await next(http); });
        app.MapBraidCatalog(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = origin;
        using var contextResponse = await client.GetAsync("/api/v1/catalog/context");
        var context = await CatalogHttpTestSupport.JsonAsync(contextResponse);
        var token = context.GetProperty("requestToken").GetString()!;
        var cookie = string.Join("; ", contextResponse.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]));
        var root = Directory.CreateTempSubdirectory("braid-native-replay-").FullName;
        try
        {
            await using var runner = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(root, DockerWorkerProfile.Preview));
            var backend = new FerrulePreviewBackend(runner, PreviewExecutionProfile.LegacyJson, nativeFormats: [format]);
            byte[]? previous = null;
            foreach (var bytes in corrupt is null ? new[] { valid, Array.Empty<byte>() } : new[] { valid, corrupt, valid })
            {
                var key = Guid.NewGuid();
                async Task<HttpResponseMessage> Submit(byte[] input)
                {
                    var body = new CatalogPreviewSubmissionV1(new("primary"), [], [new(Convert.ToBase64String(input))], format.Profile);
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}/revisions/{revision.RevisionId:D}/previews") { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority)); request.Headers.Add("Cookie", cookie);
                    request.Headers.Add("X-XSRF-TOKEN", token); request.Headers.Add("Idempotency-Key", key.ToString("D"));
                    return await client.SendAsync(request);
                }
                using var submit = await Submit(bytes);
                if (format == NativeDocumentFormat.Xlsx)
                {
                    Assert.False(context.GetProperty("features").TryGetProperty("nativePreviewProfiles", out _));
                    Assert.Equal(HttpStatusCode.NotImplemented, submit.StatusCode);
                    using var problem = JsonDocument.Parse(await submit.Content.ReadAsByteArrayAsync());
                    Assert.Equal(NativeDocumentFormat.XlsxOutputRefusal, problem.RootElement.GetProperty("detail").GetString());
                    Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs"));
                    var artifact = Braid.Domain.NativeArtifact.Admit(revision, native, PreviewPolicy.MaximumNativeBytes);
                    var request = new MappingPreviewRequest(artifact, PreviewTargetSelection.Primary, new(null, valid, documentSettings: NativeDocumentNativeSettings.Read(artifact, format).Input), [], [], "2026-11-07T12:00:00Z", PreviewLimits.Default) { Profile = format.Execution };
                    var refusal = await backend.PreviewAsync(request);
                    Assert.Equal(MappingPreviewStatus.UnsupportedCapability, refusal.Status);
                    Assert.Equal("preview.xlsxOutputUnavailable", refusal.Failure!.Code);
                    Assert.False(refusal.Provenance.Confirmed);
                    return;
                }
                var created = await CatalogHttpTestSupport.JsonAsync(submit, HttpStatusCode.Created);
                var run = Guid.Parse(created.GetProperty("run").GetProperty("runId").GetString()!);
                Assert.Equal(PreviewExecutionOutcome.Completed, await test.Executor(backend, options).ExecuteAsync(revision.OrganizationId, run, Guid.NewGuid()));
                var path = $"/api/v1/organizations/{revision.OrganizationId:D}/previews/{run:D}";
                using var reopened = await client.GetAsync(path);
                var result = await CatalogHttpTestSupport.JsonAsync(reopened);
                var invalid = ReferenceEquals(bytes, corrupt);
                Assert.Equal(invalid ? "invalidInput" : "succeeded", result.GetProperty("outcome").GetString());
                Assert.Equal(format.Profile, result.GetProperty("provenance").GetProperty("profile").GetString());
                Assert.Equal(format.SettingsSchema, result.GetProperty("primaryDocumentSettings").GetProperty("schema").GetString());
                using var output = await client.GetAsync(path + "/outputs/0");
                if (invalid)
                {
                    Assert.Empty(result.GetProperty("outputs").EnumerateArray());
                    Assert.Equal(HttpStatusCode.NotFound, output.StatusCode);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, output.StatusCode);
                    Assert.Equal(format.MediaType, output.Content.Headers.ContentType!.MediaType);
                    var actual = await output.Content.ReadAsByteArrayAsync();
                    if (format == NativeDocumentFormat.Xbrl)
                        Assert.Equal("5", XDocument.Parse(Encoding.UTF8.GetString(actual)).Descendants(XName.Get("Amount", "urn:a")).Single().Value);
                    else Assert.Equal(bytes, actual);
                    if (previous is not null && corrupt is not null) Assert.Equal(previous, actual);
                    previous = actual;
                }
                Assert.Equal(bytes, (await test.Reader.ReadInputAsync(test.Alice, revision.OrganizationId, run, 0))!.Content);
                using var replay = await Submit(bytes);
                var duplicate = await CatalogHttpTestSupport.JsonAsync(replay);
                Assert.Equal("replayed", duplicate.GetProperty("outcome").GetString());
                Assert.Equal(run, duplicate.GetProperty("run").GetProperty("runId").GetGuid());
                Assert.Equal(result.GetProperty("outcome").GetString(), duplicate.GetProperty("run").GetProperty("outcome").GetString());
                Assert.Equal(result.GetProperty("requestFingerprint").GetString(), duplicate.GetProperty("run").GetProperty("requestFingerprint").GetString());
                using var conflict = await Submit(invalid ? valid : corrupt ?? "changed"u8.ToArray());
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static byte[] Workbook(string sheetName, bool malformed = false)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
            void Part(string path, string xml)
            {
                using var part = zip.CreateEntry(path).Open();
                // Keep the ZIP directory valid so rejection occurs at the native worksheet reader.
                part.Write(Encoding.UTF8.GetBytes(malformed && path == "xl/worksheets/sheet1.xml" ? "<worksheet><c></worksheet>" : xml));
            }
            Part("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Part("_rels/.rels", $"""<Relationships xmlns="{package}"><Relationship Id="rId1" Type="{rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part("xl/workbook.xml", $"""<workbook xmlns="{Spreadsheet}" xmlns:r="{rel}"><sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Part("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="{package}"><Relationship Id="rId1" Type="{rel}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Part("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="{Spreadsheet}"><sheetData><row r="2"><c r="A2" t="inlineStr"><is><t>Code</t></is></c><c r="B2" t="inlineStr"><is><t>Quantity</t></is></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>Q</t></is></c><c r="B3"><v>7</v></c></row></sheetData></worksheet>""");
        }
        return stream.ToArray();
    }
}
