using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual HTTP middleware and restricted PostgreSQL with an authored object/backend port, not native or filesystem qualification.</summary>
[Collection(PostgresCollection.Name)]
public sealed class XmlPreviewManagedHttpTests(PostgresFixture fixture)
{
    [PostgresTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "malformed")]
    [InlineData(true, "dtd")]
    [InlineData(true, "depthLimit")]
    [InlineData(true, "nodeLimit")]
    [InlineData(true, "metadataTooLarge")]
    public async Task Xml_opt_in_precedes_private_reads_and_preserves_request_replay_metadata_and_download_bytes(bool enabled, string? refusal)
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var nativeBytes = XmlPreviewWorkflowTests.Native;
        if (refusal == "metadataTooLarge")
        {
            var native = System.Text.Json.Nodes.JsonNode.Parse(nativeBytes)!;
            var project = native["document"] ?? native;
            project["target_options"]!["xml_schema_hints"] = JsonSerializer.SerializeToNode(new
            {
                locations = Enumerable.Range(0, 8).Select(index => new { @namespace = "urn:private-sentinel:" + index + new string('n', 3500), location = "https://private-sentinel.invalid/" + new string('l', 3500) })
            });
            nativeBytes = JsonSerializer.SerializeToUtf8Bytes(native);
        }
        var receipt = await catalog.CommitCreateAsync(body: nativeBytes);
        var revision = receipt.Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var reader = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        await using var catalogDatabase = new PostgresCatalogDatabase(catalog.Case.CatalogConnectionString);
        await using var reports = new PostgresRevisionReportDatabase(catalog.Case.ReportConnectionString);
        var store = new PostgresPreviewRunStore(writer);
        var options = new PreviewRunOptions { XmlEnabled = enabled };
        var service = new PreviewRunService(store, new PostgresPreviewRunReader(reader), TimeProvider.System, options);
        var objects = new AuthoredNative(revision, receipt.Facts.NativeObject, nativeBytes);
        var origin = new Uri("http://127.0.0.1:53781"); // TestServer owns no socket.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidCatalog(new(true, true, catalog.Actor.PrincipalId.Value, revision.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
            new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(), new CatalogRevisionReportOptions());
        builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(catalogDatabase));
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(new PostgresRevisionReportMetadataReader(reports));
        builder.Services.AddSingleton<IPrivateObjectStore>(objects);
        builder.Services.AddSingleton(service);
        builder.Services.AddBraidPreviews(new(enabled: true, xmlEnabled: enabled));
        await using var app = builder.Build();
        // TestServer has no socket peer; model the configured loopback transport.
        app.Use(async (http, next) => { http.Connection.RemoteIpAddress = IPAddress.Loopback; await next(http); });
        app.MapBraidCatalog(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = origin;
        using var contextResponse = await client.GetAsync("/api/v1/catalog/context");
        var context = await CatalogHttpTestSupport.JsonAsync(contextResponse);
        Assert.Equal(enabled, context.GetProperty("features").GetProperty("xmlPreview").GetBoolean());
        var token = context.GetProperty("requestToken").GetString()!;
        var cookie = AntiforgeryCookie(contextResponse);
        var key = Guid.NewGuid();
        var inputBytes = refusal switch
        {
            "malformed" => "<private-sentinel><x></private-sentinel>"u8.ToArray(),
            "dtd" => "<!DOCTYPE private-sentinel SYSTEM 'https://private-sentinel.invalid/x'><private-sentinel/>"u8.ToArray(),
            "depthLimit" => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<private-sentinel>", 65)) + string.Concat(Enumerable.Repeat("</private-sentinel>", 65))),
            "nodeLimit" => Encoding.UTF8.GetBytes("<private-sentinel>" + string.Concat(Enumerable.Repeat("<x/>", 1_000_000)) + "</private-sentinel>"),
            _ => XmlPreviewWorkflowTests.Input
        };
        var submission = new CatalogPreviewSubmissionV1(new("all"), [], [new(Convert.ToBase64String(inputBytes))], XmlPreviewAdmission.Profile);
        var body = JsonSerializer.SerializeToUtf8Bytes(submission, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var submitted = await SubmitAsync(body, key);
        if (!enabled)
        {
            Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
            Assert.Equal(0, objects.Reads);
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs"));
            return;
        }
        if (refusal is not null)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, submitted.StatusCode);
            var text = await submitted.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-sentinel", text);
            Assert.DoesNotContain("https:", text);
            var problem = JsonSerializer.Deserialize<CatalogXmlPreviewProblemV1>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.True(problem.IsSafe());
            Assert.Equal(refusal is "malformed" or "dtd" ? "invalidXml" : refusal, problem.Reason);
            Assert.Equal(refusal is "malformed" or "dtd" ? "invalidInput" : "resourceLimit", problem.Kind);
            if (problem.Kind == "resourceLimit")
            {
                Assert.NotNull(problem.Observed);
                Assert.Equal(refusal switch { "depthLimit" => "64", "nodeLimit" => "1000000", _ => "32768" }, problem.Limit);
            }
            if (refusal is "malformed" or "depthLimit" or "nodeLimit")
            {
                Assert.NotNull(problem.Location);
                Assert.Equal(inputBytes.Length, problem.Statistics!.OriginalBytes);
            }
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs"));
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_documents"));
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.xml_preview_documents"));
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.lifecycle_work_admissions WHERE work_kind='preview'"));
            return;
        }
        var created = await CatalogHttpTestSupport.JsonAsync(submitted, HttpStatusCode.Created);
        var run = Guid.Parse(created.GetProperty("run").GetProperty("runId").GetString()!);
        Assert.Equal(1, objects.Reads);
        var executor = new PreviewRunExecutor(store, objects, new AuthoredBackend(), TimeProvider.System, options);
        Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(revision.OrganizationId, run, Guid.NewGuid()));
        using var read = await client.GetAsync($"/api/v1/organizations/{revision.OrganizationId:D}/previews/{run:D}");
        var snapshot = await CatalogHttpTestSupport.JsonAsync(read);
        Assert.Equal("succeeded", snapshot.GetProperty("outcome").GetString());
        Assert.Equal("braid.xml-boundary.v1", snapshot.GetProperty("outputs")[0].GetProperty("xmlMetadata").GetProperty("schema").GetString());
        using var output = await client.GetAsync($"/api/v1/organizations/{revision.OrganizationId:D}/previews/{run:D}/outputs/0");
        Assert.Equal(HttpStatusCode.OK, output.StatusCode);
        Assert.Equal("application/xml", output.Content.Headers.ContentType!.MediaType);
        Assert.Equal(XmlPreviewWorkflowTests.Input, await output.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", output.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", Assert.Single(output.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("sha-256=:" + Convert.ToBase64String(Convert.FromHexString(Sha256Digest.FromBytes(XmlPreviewWorkflowTests.Input).Value)) + ":", Assert.Single(output.Headers.GetValues("Content-Digest")));
        using var replay = await SubmitAsync(body, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var before = objects.Reads;
        using var injected = await SubmitAsync(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("\"content\":", "\"xmlMetadata\":null,\"content\":", StringComparison.Ordinal)), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.Equal(before, objects.Reads);

        async Task<HttpResponseMessage> SubmitAsync(byte[] content, Guid requestKey)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}/revisions/{revision.RevisionId:D}/previews") { Content = new ByteArrayContent(content) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-XSRF-TOKEN", token); request.Headers.Add("Idempotency-Key", requestKey.ToString("D"));
            request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request);
        }
    }

    // TestServer's client keeps no cookies; forward only the issued cookie name/value pairs.
    private static string AntiforgeryCookie(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));

    [PostgresFact]
    public async Task Durable_store_admission_forwards_the_same_schema_gate_without_resolving_or_publishing_queue_work()
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        var runtime = System.Reflection.DispatchProxy.Create<Wolverine.Runtime.IWolverineRuntime, XmlPreviewWorkflowTests.RefuseIo>();
        var store = new Braid.Jobs.OutboxPreviewRunStore(new PostgresPreviewRunStore(writer), runtime);
        await new PreviewRunService(store, System.Reflection.DispatchProxy.Create<IPreviewRunReader, XmlPreviewWorkflowTests.RefuseIo>(), TimeProvider.System)
            .RequireSubmissionAdmissionAsync();
        await catalog.Case.ExecuteAsync("ALTER TABLE braid.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequireSubmissionAdmissionAsync());
        Assert.Empty(((XmlPreviewWorkflowTests.RefuseIo)(object)runtime).Calls);
        Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs"));
        Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_documents"));
    }

    public static IEnumerable<object[]> PreflightSchemaCases()
    {
        foreach (var profile in new[] { XmlPreviewAdmission.Profile, CsvDocumentPolicy.Profile })
            foreach (var mutation in new[] { "healthy", "suffix", "trigger", "rls", "policy", "grant", "column", "function" })
                yield return [profile, mutation];
    }

    [PostgresTheory]
    [MemberData(nameof(PreflightSchemaCases))]
    public async Task Current_schema_admission_precedes_all_native_preflight_reads_and_submission_effects(string profile, string mutation)
    {
        await using var catalog = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var nativeBytes = profile switch
        {
            XmlPreviewAdmission.Profile => XmlPreviewWorkflowTests.Native,
            CsvDocumentPolicy.Profile => CsvPreviewTestData.Native,
            _ => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "flextext.native.json"))
        };
        var receipt = await catalog.CommitCreateAsync(body: nativeBytes);
        var revision = receipt.Facts.FutureRevision;
        await using var writer = new PostgresPreviewDatabase(catalog.Case.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
        await using var readerDatabase = new PostgresPreviewDatabase(catalog.Case.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
        await using var catalogDatabase = new PostgresCatalogDatabase(catalog.Case.CatalogConnectionString);
        await using var reports = new PostgresRevisionReportDatabase(catalog.Case.ReportConnectionString);
        var inner = new PostgresPreviewRunStore(writer);
        var reader = new PostgresPreviewRunReader(readerDatabase);
        var options = new PreviewRunOptions { XmlEnabled = true };
        // Existing unrelated state must not be changed by either admission or refusal.
        var oldSubmission = new PreviewRunSubmission(revision, Guid.NewGuid(), PreviewTargetSelection.All, new(null, "{}"u8), [], []);
        var oldRun = (await new PreviewRunService(inner, reader, TimeProvider.System).SubmitAsync(catalog.Actor, oldSubmission)).RunId!.Value;
        var store = new ObservedStore(inner);
        var service = new PreviewRunService(store, reader, TimeProvider.System, options);
        var objects = new AuthoredNative(revision, receipt.Facts.NativeObject, nativeBytes)
        {
            BeforeRead = () =>
            {
                Assert.Equal(1, store.Admissions);
                Assert.Equal(0, store.Submissions);
            }
        };
        var origin = new Uri("http://127.0.0.1:53781"); // In-process TestServer, no socket or browser.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        builder.Services.AddBraidApi();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddBraidCatalog(new(true, true, catalog.Actor.PrincipalId.Value, revision.OrganizationId, origin.GetLeftPart(UriPartial.Authority)), builder.Environment,
            new CatalogMetadataEditOptions(), new CatalogRevisionIngestionOptions(), new CatalogProjectCreationOptions(), new CatalogRevisionReportOptions());
        builder.Services.AddSingleton<ICatalogReader>(new PostgresCatalogReader(catalogDatabase));
        builder.Services.AddSingleton<IRevisionReportMetadataReader>(new PostgresRevisionReportMetadataReader(reports));
        builder.Services.AddSingleton<IPrivateObjectStore>(objects);
        builder.Services.AddSingleton(service);
        builder.Services.AddBraidPreviews(new(enabled: true, csvEnabled: true, xmlEnabled: true));
        await using var app = builder.Build();
        // TestServer has no socket peer; model the configured loopback transport.
        app.Use(async (http, next) => { http.Connection.RemoteIpAddress = IPAddress.Loopback; await next(http); });
        app.MapBraidCatalog(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = origin;
        using var context = await client.GetAsync("/api/v1/catalog/context");
        var token = (await CatalogHttpTestSupport.JsonAsync(context)).GetProperty("requestToken").GetString()!;
        var cookie = AntiforgeryCookie(context);
        if (mutation != "healthy")
        {
            await catalog.Case.ExecuteAsync(mutation switch
            {
                "suffix" => """
                    DROP TABLE braid.xml_preview_documents;
                    DO $drop$ DECLARE signature text; BEGIN
                        FOR signature IN SELECT p.oid::regprocedure::text FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                            WHERE n.nspname='braid' AND p.proname=ANY(ARRAY['xml_metadata_valid','guard_xml_preview_document',
                                'preview_submit_xml','preview_complete_xml','preview_xml_inputs','preview_xml_documents'])
                        LOOP EXECUTE 'DROP FUNCTION '||signature; END LOOP;
                    END; $drop$;
                    """,
                "trigger" => "ALTER TABLE braid.xml_preview_documents DISABLE TRIGGER guard_xml_preview_document",
                "rls" => "ALTER TABLE braid.xml_preview_documents DISABLE ROW LEVEL SECURITY",
                "policy" => "ALTER POLICY xml_preview_documents_organization_scope ON braid.xml_preview_documents USING (true)",
                "grant" => "GRANT SELECT ON braid.xml_preview_documents TO braid_preview_reader",
                "column" => "ALTER TABLE braid.xml_preview_documents ADD COLUMN unexpected integer",
                _ => "CREATE OR REPLACE FUNCTION braid.xml_metadata_valid(value jsonb,input_role boolean) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS 'BEGIN RETURN true; END;'"
            });
        }
        Assert.Equal(XmlPreviewSchemaAdmission.MigrationChecksum, await catalog.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=42"));
        if (mutation == "suffix")
        {
            Assert.False(await catalog.Case.ScalarAsync<bool>("SELECT to_regclass('braid.xml_preview_documents') IS NOT NULL"));
            Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM pg_proc WHERE pronamespace='braid'::regnamespace AND proname=ANY($1)", (object)XmlPreviewSchemaAdmission.Functions));
        }
        foreach (var role in new[] { "braid_preview_writer", "braid_preview_reader" })
            Assert.False(await catalog.Case.ScalarAsync<bool>("SELECT has_any_column_privilege($1,'braid.schema_migrations','SELECT')", role));
        var before = await SnapshotAsync();
        var input = profile == XmlPreviewAdmission.Profile ? XmlPreviewWorkflowTests.Input : "owned <private-sentinel>\r\n"u8.ToArray();
        var body = new CatalogPreviewSubmissionV1(new("all"), [], [new(Convert.ToBase64String(input))], profile);
        var key = Guid.NewGuid();
        using var response = await SubmitAsync();
        if (mutation != "healthy")
        {
            var problem = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.InternalServerError);
            Assert.Equal("persistenceFailure", problem.GetProperty("code").GetString());
            Assert.DoesNotContain("private-sentinel", problem.GetRawText());
            Assert.Equal(1, store.Admissions);
            Assert.Equal(0, store.Submissions);
            Assert.Equal(0, store.CreatedPublications);
            Assert.Equal(0, objects.Reads);
            Assert.Equal(0, objects.Publications);
            Assert.Equal(before, await SnapshotAsync());
            if (mutation != "suffix") Assert.Equal(0L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.xml_preview_documents"));
            return;
        }
        var created = await CatalogHttpTestSupport.JsonAsync(response, HttpStatusCode.Created);
        var run = Guid.Parse(created.GetProperty("run").GetProperty("runId").GetString()!);
        Assert.NotEqual(oldRun, run);
        Assert.Equal(1, store.Admissions); Assert.Equal(1, store.Submissions); Assert.Equal(1, store.CreatedPublications);
        Assert.Equal(1, objects.Reads); Assert.Equal(0, objects.Publications);
        Assert.Equal(input, (await reader.ReadInputAsync(catalog.Actor, revision.OrganizationId, run, 0))!.Content);
        objects.BeforeRead = null;
        using var replayResponse = await SubmitAsync();
        var replay = await CatalogHttpTestSupport.JsonAsync(replayResponse);
        Assert.Equal("replayed", replay.GetProperty("outcome").GetString());
        Assert.Equal(run.ToString("D"), replay.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal(2, store.Admissions); Assert.Equal(2, store.Submissions); Assert.Equal(1, store.CreatedPublications);
        Assert.Equal(2, objects.Reads); Assert.Equal(0, objects.Publications);
        Assert.Equal(2L, await catalog.Case.ScalarAsync<long>("SELECT count(*) FROM braid.preview_runs"));
        Assert.Equal(PreviewRunState.Queued, (await reader.ReadAsync(catalog.Actor, revision.OrganizationId, oldRun))!.State);

        Task<string> SnapshotAsync() => catalog.Case.ScalarAsync<string>("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM braid.preview_runs r),
                (SELECT jsonb_agg(to_jsonb(d) ORDER BY run_id,direction,ordinal) FROM braid.preview_documents d),
                (SELECT jsonb_agg(to_jsonb(i) ORDER BY to_jsonb(i)::text) FROM braid.revision_ingestions i),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text) FROM braid.mapping_revisions r),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY to_jsonb(l)::text) FROM braid.lifecycle_work_admissions l),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY to_jsonb(l)::text) FROM braid.lifecycle_work_scopes l),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY to_jsonb(l)::text) FROM braid.lifecycle_operations l),
                (SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM braid.schema_migrations m))::text
            """);

        async Task<HttpResponseMessage> SubmitAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}/revisions/{revision.RevisionId:D}/previews")
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("X-XSRF-TOKEN", token); request.Headers.Add("Idempotency-Key", key.ToString("D"));
            request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request);
        }
    }

    private sealed class ObservedStore(PostgresPreviewRunStore inner) : IPreviewRunStore
    {
        internal int Admissions, Submissions, CreatedPublications;
        public Task RequireSubmissionAdmissionAsync(CancellationToken token = default)
        {
            Admissions++;
            return inner.RequireSubmissionAdmissionAsync(token);
        }
        public Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken token = default)
        {
            Submissions++;
            return inner.SubmitAsync(actor, record, (_, result, _) =>
            {
                // This authored publication observer is not a real durable queue.
                if (result.Outcome == PreviewSubmitOutcome.Created) CreatedPublications++;
                return Task.CompletedTask;
            }, token);
        }
        public Task<PreviewClaimResult> ClaimAsync(Guid organization, Guid run, Guid owner, TimeSpan lease, CancellationToken token = default) => inner.ClaimAsync(organization, run, owner, lease, token);
        public Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken token = default) => inner.ReadInputsAsync(claim, token);
        public Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken token = default) => inner.ObserveAsync(claim, token);
        public Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken token = default) => inner.CompleteAsync(claim, completion, token);
        public Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan delay, CancellationToken token = default) => inner.ReleaseAsync(claim, delay, token);
        public Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organization, Guid run, CancellationToken token = default) => inner.CancelAsync(actor, organization, run, token);
    }

    private sealed class AuthoredNative(RevisionScope revision, PrivateObjectReference native, byte[] content) : IPrivateObjectStore
    {
        internal int Reads, Publications;
        internal Action? BeforeRead;
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference value, CancellationToken cancellationToken = default)
        {
            Reads++;
            BeforeRead?.Invoke();
            Assert.Equal(revision, scope); Assert.Equal(native, value);
            return Task.FromResult(content);
        }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference value, Stream source, CancellationToken cancellationToken = default)
        {
            Publications++;
            throw new NotSupportedException();
        }
    }

    private sealed class AuthoredBackend : IMappingPreviewBackend
    {
        public Task<MappingPreviewResult> PreviewAsync(MappingPreviewRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MappingPreviewResult(
            MappingPreviewStatus.Succeeded, new(request.Artifact.Scope, request.Artifact.Digest, "ferrule", "authored", "authored", "authored", true), XmlPreviewAdmission.Profile, 1,
            [new("primary-output", "Order", false, XmlDocumentPolicy.MediaType, 1, request.Primary.Bytes, xmlMetadata: XmlDocumentMetadata.WithoutHints)], null));
    }
}
