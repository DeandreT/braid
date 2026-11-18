using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Braid.Api;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Original_approval_recovers_after_runtime_change_and_revocation_without_new_intent_resolution()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        Assert.True(await data.Store.RevokeAsync(data.Actor, data.Intent.Revision.OrganizationId, data.Intent.Revision.ProjectId, data.Intent.ApprovalId));
        await using var host = await Host.StartAsync(data, changedRuntime: true);
        var token = await host.TokenAsync();
        using var response = await host.PutAsync(host.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D"),
            ApprovalBody(data.Intent), CatalogBuildRequest.ApprovalMediaType, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("replayed", body.GetProperty("outcome").GetString());
        var approval = body.GetProperty("approval");
        Assert.True(approval.GetProperty("revoked").GetBoolean());
        Assert.Equal(data.Intent.Backend.EngineBuild, approval.GetProperty("backend").GetProperty("engineBuild").GetString());
        Assert.Equal(0, host.IntentResolutions); Assert.Equal(0, host.ActiveApprovalWrites);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [PostgresFact]
    public async Task Queue_replay_survives_revocation_without_requeueing_and_history_never_contains_private_bytes()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using var host = await Host.StartAsync(data);
        var token = await host.TokenAsync();
        var path = host.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D");
        var request = JsonSerializer.Serialize(new { approvalId = data.Intent.ApprovalId.ToString("D") });
        using var created = await host.PutAsync(path, request, CatalogBuildRequest.RunMediaType, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(await data.Store.RevokeAsync(data.Actor, data.Intent.Revision.OrganizationId, data.Intent.Revision.ProjectId, data.Intent.ApprovalId));
        using var replayed = await host.PutAsync(path, request, CatalogBuildRequest.RunMediaType, token);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal("replayed", (await Json(replayed)).GetProperty("outcome").GetString());
        using var history = await host.Client.GetAsync(host.MappingPath + "/build-runs?limit=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var text = await history.Content.ReadAsStringAsync();
        Assert.DoesNotContain("claimToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("intentBody", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artifactBody", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_runs"));
        Assert.Equal(0, await data.Runs.Cases.Case.ScalarAsync<int>("SELECT attempt FROM braid.build_runs"));
    }

    [PostgresFact]
    public async Task Missing_csrf_and_viewer_authority_fail_before_body_reads_or_build_service_resolution()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.StartAsync(data);
        var path = host.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D");
        using (var denied = await host.PutAsync(path, "{", CatalogBuildRequest.ApprovalMediaType, null))
            Assert.NotEqual(HttpStatusCode.Created, denied.StatusCode);
        Assert.Equal(0, host.BodyReads); Assert.Equal(0, host.MetadataResolutions);
        var token = await host.TokenAsync();
        await data.Runs.Cases.RoleAsync("viewer");
        using var viewer = await host.PutAsync(path, "{", CatalogBuildRequest.ApprovalMediaType, token);
        Assert.Equal(HttpStatusCode.NotFound, viewer.StatusCode);
        Assert.Equal(0, host.BodyReads); Assert.Equal(0, host.MetadataResolutions);
    }

    [PostgresFact]
    public async Task Disabled_build_routes_read_neither_body_nor_build_dependencies()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var host = await Host.StartAsync(data, enabled: false);
        var token = await host.TokenAsync();
        using var response = await host.PutAsync(host.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D"), "{", CatalogBuildRequest.ApprovalMediaType, token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.BodyReads); Assert.Equal(0, host.MetadataResolutions);
    }

    [PostgresFact]
    public async Task Changed_approval_selectors_conflict_and_foreign_mapping_reads_are_opaque()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using var host = await Host.StartAsync(data);
        var token = await host.TokenAsync();
        var changed = ApprovalBody(data.Intent).Replace(data.Intent.RegressionEvidenceId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        using var conflict = await host.PutAsync(host.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D"), changed, CatalogBuildRequest.ApprovalMediaType, token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(0, host.IntentResolutions); Assert.Equal(0, host.ActiveApprovalWrites);
        var foreign = host.MappingPath.Replace(data.Intent.Revision.MappingId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        using var hidden = await host.Client.GetAsync(foreign + "/build-approvals/" + data.Intent.ApprovalId.ToString("D"));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var cursor = await host.Client.GetAsync(host.MappingPath + "/build-approvals?afterApprovalId=" + Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.NotFound, cursor.StatusCode);
    }

    [PostgresFact]
    public async Task Cancellation_returns_the_actual_retained_terminal_state_and_does_not_create_artifacts()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using var host = await Host.StartAsync(data);
        var token = await host.TokenAsync();
        var path = host.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D");
        using (var queued = await host.PutAsync(path, JsonSerializer.Serialize(new { approvalId = data.Intent.ApprovalId.ToString("D") }), CatalogBuildRequest.RunMediaType, token))
            Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path + "/cancel");
            request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = await Json(response);
            Assert.Equal("canceled", row.GetProperty("state").GetString());
            Assert.Equal(0, row.GetProperty("attempt").GetInt32());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("artifact").ValueKind);
        }
        Assert.Equal(0L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_publications"));
    }

    [PostgresFact]
    public async Task Lost_submission_reply_recovers_after_host_restart_runtime_change_and_approval_revocation()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        var request = JsonSerializer.Serialize(new { approvalId = data.Intent.ApprovalId.ToString("D") });
        await using (var original = await Host.StartAsync(data))
        {
            var token = await original.TokenAsync();
            original.DropNextReply = 1;
            await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            {
                using var lost = await original.PutAsync(original.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D"),
                    request, CatalogBuildRequest.RunMediaType, token);
            });
        }
        Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_runs"));
        Assert.True(await data.Store.RevokeAsync(data.Actor, data.Intent.Revision.OrganizationId, data.Intent.Revision.ProjectId, data.Intent.ApprovalId));
        await using var restarted = await Host.StartAsync(data, changedRuntime: true);
        var renewed = await restarted.TokenAsync();
        using var recovered = await restarted.PutAsync(restarted.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D"),
            request, CatalogBuildRequest.RunMediaType, renewed);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("replayed", (await Json(recovered)).GetProperty("outcome").GetString());
        using var approval = await restarted.PutAsync(restarted.MappingPath + "/build-approvals/" + data.Intent.ApprovalId.ToString("D"),
            ApprovalBody(data.Intent), CatalogBuildRequest.ApprovalMediaType, renewed);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.Equal(0, restarted.IntentResolutions); Assert.Equal(0, restarted.ActiveApprovalWrites);
        Assert.Equal(1L, await data.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_runs"));
        Assert.Equal(0, await data.Runs.Cases.Case.ScalarAsync<int>("SELECT attempt FROM braid.build_runs"));
    }

    [PostgresFact]
    public async Task An_accessible_other_mapping_cannot_read_cancel_replay_or_use_the_run_as_a_cursor()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        await using var ingestionDatabase = new PostgresIngestionDatabase(data.Runs.Cases.Case.IngestionConnectionString);
        var ingestion = new PostgresRevisionIngestionStore(ingestionDatabase);
        var reserved = await ingestion.ReserveAsync(data.Actor, data.Intent.Environment.Project, Guid.NewGuid(),
            PostgresIngestionTestSupport.CreateIntent(), PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        var facts = reserved.Receipt!.Facts;
        var claim = (await ingestion.ClaimAsync(data.Actor, facts.Project, facts.OperationId, Guid.NewGuid())).Claim!;
        await ingestion.MarkStagedAsync(data.Actor, claim, facts.NativeObject);
        await ingestion.CompleteAttemptAsync(data.Actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts));
        Assert.Equal(IngestionPhase.Committed, (await ingestion.FinalizeAsync(data.Actor, claim)).Receipt!.Phase);
        await using var host = await Host.StartAsync(data);
        var token = await host.TokenAsync();
        var body = JsonSerializer.Serialize(new { approvalId = data.Intent.ApprovalId.ToString("D") });
        using (var own = await host.PutAsync(host.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D"), body, CatalogBuildRequest.RunMediaType, token))
            Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var other = host.MappingPath.Replace(data.Intent.Revision.MappingId.ToString("D"), facts.FutureRevision.MappingId.ToString("D"), StringComparison.Ordinal);
        using (var capability = await host.Client.GetAsync(other + "/build-capabilities")) Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
        using (var hidden = await host.Client.GetAsync(other + "/build-runs/" + data.Intent.BuildId.ToString("D"))) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var hidden = await host.PutAsync(other + "/build-runs/" + data.Intent.BuildId.ToString("D"), body, CatalogBuildRequest.RunMediaType, token)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var hidden = await host.Client.GetAsync(other + "/build-runs?afterBuildId=" + data.Intent.BuildId.ToString("D"))) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var cancel = new HttpRequestMessage(HttpMethod.Post, other + "/build-runs/" + data.Intent.BuildId.ToString("D") + "/cancel");
        cancel.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        cancel.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
        using (var hidden = await host.Client.SendAsync(cancel)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("queued", await data.Runs.Cases.Case.ScalarAsync<string>("SELECT state FROM braid.build_runs"));
    }

    [PostgresFact]
    public async Task Package_manifest_is_canonical_owned_metadata_not_an_execution_or_publication_action()
    {
        await using var data = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await data.Approve());
        var test = data.Runs.Cases.Case;
        await using var writer = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer);
        await using var dispatcher = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_dispatcher"), BuildRunDatabaseProfile.Dispatcher);
        Assert.Equal(BuildRunSubmitOutcome.Created, await writer.SubmitAsync(data.Actor, data.Intent));
        await using var host = await Host.StartAsync(data);
        await host.TokenAsync();
        var path = host.MappingPath + "/build-runs/" + data.Intent.BuildId.ToString("D") + "/package-manifest";
        using (var queued = await host.Client.GetAsync(path)) Assert.Equal(HttpStatusCode.NotFound, queued.StatusCode);

        var claim = (await dispatcher.ClaimAsync(data.Intent.Revision.OrganizationId, data.Intent.BuildId, Guid.NewGuid()))!;
        Assert.NotNull(claim);
        await using var publisher = new PostgresBuildStore(data.Connection, true, claim);
        var bytes = new BuildBytes([7, 8, 9], data.Intent.Profile.MaximumArtifactBytes);
        var artifact = BuildArtifactReference.Restore(data.Intent, bytes.Digest, bytes.Length);
        var publication = new BuildPublication(data.Actor.PrincipalId, artifact,
            new(PrivateObjectId.Restore(data.Intent.Revision, data.Intent.BuildId, PrivateObjectClass.DerivedArtifact), bytes.Digest, bytes.Length));
        Assert.NotNull(await publisher.StageAsync(data.Actor, new(publication, bytes)));
        var objects = new PostgresBuildObjectStore(publisher, new NoPackageNative(), data.Actor);
        using (var source = new MemoryStream(bytes.Copy(), false))
            Assert.Equal(PrivateObjectPublishResult.Created, await objects.PublishCreateOnlyAsync(data.Intent.Revision, publication.Object, source));
        Assert.Equal(BuildPublicationOutcome.Created, await publisher.PublishAsync(data.Actor, publication));
        Assert.True(await dispatcher.CompleteAsync(claim, new(BuildExecutionOutcome.Created, publication)));

        using (var response = await host.Client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            Assert.Null(response.Headers.ETag);
            var encoded = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(publication.Manifest.CopyCanonicalBytes(), encoded);
            Assert.True(MappingPackageManifest.Decode(encoded).Matches(artifact, bytes));
        }
        using (var query = await host.Client.GetAsync(path + "?anything=true")) Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        var foreign = path.Replace(data.Intent.Revision.MappingId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        using (var hidden = await host.Client.GetAsync(foreign)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var otherPrincipal = data.Runs.Cases.Seed.Admitted;
        await data.Runs.Cases.Seed.AddMembershipAsync(test, data.Intent.Revision.OrganizationId,
            data.Intent.Revision.ProjectId, otherPrincipal, "publisher");
        await using (var otherHost = await Host.StartAsync(data, principalOverride: otherPrincipal))
        {
            await otherHost.TokenAsync();
            using (var admitted = await otherHost.Client.GetAsync(otherHost.MappingPath + "/build-capabilities"))
                Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            using var hidden = await otherHost.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        await data.Runs.Cases.RoleAsync("viewer");
        using (var denied = await host.Client.GetAsync(path)) Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, host.IntentResolutions);
        Assert.Equal(0, host.ActiveApprovalWrites);
        Assert.Equal(0, host.BodyReads);
    }

    private sealed class NoPackageNative : IPrivateObjectStore
    {
        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken token = default) =>
            throw new InvalidOperationException("Package metadata must not read native source.");
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream bytes,
            CancellationToken token = default) => throw new InvalidOperationException("Package metadata must not publish native source.");
    }

    private static string ApprovalBody(BuildIntent intent) => JsonSerializer.Serialize(new
    {
        buildId = intent.BuildId.ToString("D"),
        revisionId = intent.Revision.RevisionId.ToString("D"),
        environmentId = intent.Environment.EnvironmentId.ToString("D"),
        environmentVersion = intent.Environment.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        regressionEvidenceId = intent.RegressionEvidenceId.ToString("D")
    });
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()); return document.RootElement.Clone(); }

    internal sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly PostgresCatalogDatabase catalog;
        private readonly PostgresBuildMetadataReader metadata;
        private readonly PostgresBuildRunStore reader, writer;
        private readonly PostgresRevisionReportDatabase reports;
        internal HttpClient Client { get; }
        internal string MappingPath { get; }
        internal int BodyReads, MetadataResolutions, IntentResolutions, ActiveApprovalWrites, DropNextReply, AdministrationCommands;
        internal Func<Task>? AfterBody;
        private Host(WebApplication app, PostgresCatalogDatabase catalog, PostgresBuildMetadataReader metadata, PostgresBuildRunStore reader,
            PostgresBuildRunStore writer, PostgresRevisionReportDatabase reports, Uri origin, BuildIntent intent)
        {
            this.app = app; this.catalog = catalog; this.metadata = metadata; this.reader = reader; this.writer = writer; this.reports = reports;
            Client = new(new HttpClientHandler { UseCookies = true, CookieContainer = new(), UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(45) };
            MappingPath = $"/api/v1/organizations/{intent.Revision.OrganizationId:D}/projects/{intent.Revision.ProjectId:D}/mappings/{intent.Revision.MappingId:D}";
        }
        internal static async Task<Host> StartAsync(BuildPersistenceTests.Context data, bool enabled = true, bool changedRuntime = false,
            BuildRuntimeProfile? selectedProfile = null, Guid? principalOverride = null)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            var origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, origin.Port));
            builder.Services.AddBraidApi(); builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddBraidCatalog(new(true, true, principalOverride ?? data.Actor.PrincipalId.Value, data.Intent.Revision.OrganizationId,
                origin.GetLeftPart(UriPartial.Authority)), builder.Environment);
            var backend = changedRuntime ? new BuildBackendIdentity(data.Intent.Backend.BackendId, data.Intent.Backend.EngineRevision, "different-current-image", data.Intent.Backend.AdapterVersion) : data.Intent.Backend;
            builder.Services.AddBraidBuildApi(new(enabled, backend, selectedProfile ?? data.Intent.Profile));
            var test = data.Runs.Cases.Case;
            var catalog = new PostgresCatalogDatabase(test.CatalogConnectionString);
            var catalogReader = new PostgresCatalogReader(catalog);
            var metadata = new PostgresBuildMetadataReader(test.RegressionConnectionString("braid_build_metadata_reader"));
            var reader = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
            var writer = new PostgresBuildRunStore(test.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer);
            var reports = new PostgresRevisionReportDatabase(test.ReportConnectionString);
            var controls = new BuildControlService(data.Store, catalogReader, new PostgresRevisionReportMetadataReader(reports), new RegressionRunReadService(data.Runs.Reader, data.Runs.Cases.Reader));
            Host? host = null;
            builder.Services.AddSingleton<ICatalogReader>(catalogReader);
            builder.Services.AddSingleton<IBuildMetadataReader>(_ => { host!.MetadataResolutions++; return metadata; });
            builder.Services.AddSingleton<IBuildRunReader>(reader); builder.Services.AddSingleton<IBuildRunCommands>(writer);
            builder.Services.AddSingleton<IBuildIntentFactory>(_ => new NeverIntent(host!));
            builder.Services.AddSingleton<IBuildApprovalWriter>(_ => new NeverApproval(host!));
            builder.Services.AddSingleton(provider => new BuildApprovalRecoveryService(provider.GetRequiredService<IBuildMetadataReader>(), provider.GetRequiredService<IBuildIntentFactory>(),
                provider.GetRequiredService<IBuildApprovalWriter>(), catalogReader));
            builder.Services.AddSingleton(provider => new BuildSubmissionService(provider.GetRequiredService<IBuildMetadataReader>(), reader, writer, controls, catalogReader, backend, data.Intent.Profile));
            builder.Services.AddSingleton(provider =>
            {
                var commands = new CountedAdministration(data.Store, host!);
                return new BuildAdministrationService(provider.GetRequiredService<IBuildMetadataReader>(),
                    commands, commands, catalogReader, selectedProfile ?? data.Intent.Profile);
            });
            var app = builder.Build();
            host = new(app, catalog, metadata, reader, writer, reports, origin, data.Intent);
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPost(context.Request.Method))
                {
                    context.Request.Body = new Counted(context.Request.Body, host);
                    context.Response.OnStarting(() =>
                    {
                        if (Interlocked.Exchange(ref host.DropNextReply, 0) == 1) context.Abort();
                        return Task.CompletedTask;
                    });
                }
                await next(context);
            });
            app.MapBraidCatalog(); app.MapBraidBuilds(); reservation.Stop();
            try { await app.StartAsync(); return host; }
            catch { await host.DisposeAsync(); throw; }
        }
        internal async Task<string> TokenAsync()
        {
            using var response = await Client.GetAsync("/api/v1/catalog/context");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).GetProperty("requestToken").GetString()!;
        }
        internal async Task<HttpResponseMessage> PutAsync(string path, string body, string media, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = new StringContent(body, Encoding.UTF8) };
            request.Content.Headers.ContentType = new(media); request.Headers.TryAddWithoutValidation("Origin", Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            if (token is not null) request.Headers.TryAddWithoutValidation(CatalogRequestBoundary.RequestTokenHeader, token);
            return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await app.StopAsync(); await app.DisposeAsync();
            await metadata.DisposeAsync(); await reader.DisposeAsync(); await writer.DisposeAsync(); await reports.DisposeAsync(); await catalog.DisposeAsync();
        }
        private sealed class NeverIntent(Host host) : IBuildIntentFactory
        {
            public Task<BuildIntent?> CreateAsync(ActorContext actor, MappingScope mapping, Guid approval, BuildApprovalSelection selection, CancellationToken token = default)
            { host.IntentResolutions++; throw new InvalidOperationException("Historical recovery must not resolve current intent facts."); }
        }
        private sealed class NeverApproval(Host host) : IBuildApprovalWriter
        {
            public Task<BuildControlOutcome> ApproveAsync(ActorContext actor, BuildIntent intent, CancellationToken token = default)
            { host.ActiveApprovalWrites++; throw new InvalidOperationException("Historical recovery must not renew approval."); }
        }
        private sealed class CountedAdministration(IBuildControlStore inner, Host host) : IBuildEnvironmentCommands, IBuildApprovalRevocation
        {
            public Task<BuildControlOutcome> SaveEnvironmentAsync(ActorContext actor, BuildEnvironment environment, CancellationToken token = default)
            { Interlocked.Increment(ref host.AdministrationCommands); return inner.SaveEnvironmentAsync(actor, environment, token); }
            public Task<bool> RevokeAsync(ActorContext actor, Guid organizationId, Guid projectId, Guid approvalId, CancellationToken token = default)
            { Interlocked.Increment(ref host.AdministrationCommands); return inner.RevokeAsync(actor, organizationId, projectId, approvalId, token); }
        }
        private sealed class Counted(Stream inner, Host host) : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) { Interlocked.Increment(ref host.BodyReads); return inner.Read(buffer, offset, count); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                Interlocked.Increment(ref host.BodyReads);
                var count = await inner.ReadAsync(buffer, token);
                if (count == 0 && host.AfterBody is { } after)
                {
                    host.AfterBody = null;
                    await after();
                }
                return count;
            }
            public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
