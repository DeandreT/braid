using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogInterchangeHttpTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RealHttpReviewConfirmReplayAndExportDownloadKeepWarningsAndAuthorityBound()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await using var reads = new PostgresDatabase(test.Case.RuntimeConnectionString);
        var projects = new PostgresProjectReader(reads);
        var backend = new Backend(test, projects);
        var clock = new ReviewClock();
        var ingestion = new RevisionIngestionService(test.Store, test.Store, projects, new(backend, 4 * 1024 * 1024),
            new IngestionBackendRegistry([new(backend.Profile, 4 * 1024 * 1024, backend)]));
        await using var host = await CatalogHttpTestSupport.StartAsync(test.Case, test.Actor.PrincipalId.Value,
            test.Project.OrganizationId, configureServices: services =>
            {
                services.AddSingleton(new CatalogInterchangeOptions(true));
                services.AddSingleton<IInterchangeBackendRegistry>(backend);
                services.AddSingleton<IProjectReader>(projects);
                services.AddSingleton<IIngestionWorkflowReader>(test.Store);
                services.AddSingleton(new InterchangeImportService(projects, test.Store, backend, test.Store, ingestion, clock));
                services.AddSingleton(new InterchangeExportService(backend, backend, backend, test.Store, TimeProvider.System));
            }, maximumRequestBodyBytes: 65536);
        var context = await CatalogHttpTestSupport.JsonAsync(await host.Client.GetAsync("/api/v1/catalog/context"), HttpStatusCode.OK);
        var token = context.GetProperty("requestToken").GetString()!;
        var projectPath = CatalogHttpTestSupport.ProjectPath(test.Project);
        var package = new InterchangePackage("design.mfd", [new("design.mfd", "<self-authored/>"u8),
            new("large.schema", new byte[4 * 1024 * 1024]), new("other.schema", new byte[1024 * 1024])]);
        var reviewed = await Post(host, projectPath + "/mfd-imports/review?name=Imported&backendId=ferrule", token,
            package.Encode(), CatalogInterchangeParser.MediaType);
        var review = await CatalogHttpTestSupport.JsonAsync(reviewed, HttpStatusCode.OK);
        var id = review.GetProperty("reviewId").GetString()!;
        var digest = review.GetProperty("reviewDigest").GetString()!;
        var restored = await CatalogHttpTestSupport.JsonAsync(await host.Client.GetAsync(projectPath + $"/mfd-imports/{id}"), HttpStatusCode.OK);
        Assert.Equal(digest, restored.GetProperty("reviewDigest").GetString());
        Assert.Equal("Imported", restored.GetProperty("mappingName").GetString());
        Assert.Equal(1, backend.Imports);
        Assert.Null(backend.Reference);
        var confirmPath = projectPath + $"/mfd-imports/{id}/confirm";
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(host, confirmPath, token, Confirmation(digest, false), "application/json", id)).StatusCode);
        backend.ValidationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstConfirmation = Post(host, confirmPath, token, Confirmation(digest, true), "application/json", id);
        try
        {
            await backend.ValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Accepted,
                (await Post(host, confirmPath, token, Confirmation(digest, true), "application/json", id)).StatusCode);
        }
        finally { backend.ValidationGate.SetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await firstConfirmation).StatusCode);
        // An already-reserved operation is recoverable after its review expires.
        clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(HttpStatusCode.OK, (await Post(host, confirmPath, token, Confirmation(digest, true), "application/json", id)).StatusCode);
        Assert.Equal(1, backend.Imports);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        var scope = backend.Reference!.Id.Scope;
        // ReadMetadata viewers can export; uploads still require Publisher.
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Seed.Alice);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(projectPath + $"/mfd-imports/{id}")).StatusCode);
        var exportPath = CatalogHttpTestSupport.RevisionPath(scope) + "/mfd-exports";
        var export = await CatalogHttpTestSupport.JsonAsync(await Post(host, exportPath + "/review", token, [], null), HttpStatusCode.OK);
        var exportId = export.GetProperty("reviewId").GetString()!;
        var exportDigest = export.GetProperty("reviewDigest").GetString()!;
        var downloadPath = exportPath + $"/{exportId}/download";
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Post(host, downloadPath, token, Confirmation(exportDigest, false), "application/json")).StatusCode);
        var download = await Post(host, downloadPath, token, Confirmation(exportDigest, true), "application/json");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.True(download.Headers.Contains("X-Content-Sha256"));
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Seed.Alice);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(host, downloadPath, token, Confirmation(exportDigest, true), "application/json")).StatusCode);
    }

    [Fact]
    public async Task DefaultHostDoesNotEnableInterchangeOrConsumeAnUpload()
    {
        await using var host = await CatalogHttpTestSupport.StartAsync();
        var response = await host.Client.PostAsync("/api/v1/organizations/11111111-1111-1111-1111-111111111111/projects/22222222-2222-2222-2222-222222222222/mfd-imports/review", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task UnadmittedRequestDoesNotConstructLazyWorkerOrStorageServices()
    {
        var resolutions = 0;
        await using var host = await CatalogHttpTestSupport.StartAsync(configureServices: services =>
        {
            services.AddSingleton(new CatalogInterchangeOptions(true));
            services.AddSingleton<IInterchangeBackendRegistry>(_ => { resolutions++; throw new InvalidOperationException("Unexpected worker resolution"); });
            services.AddSingleton<InterchangeImportService>(_ => { resolutions++; throw new InvalidOperationException("Unexpected import resolution"); });
            services.AddSingleton<InterchangeExportService>(_ => { resolutions++; throw new InvalidOperationException("Unexpected storage resolution"); });
        });
        var response = await host.Client.PostAsync("/api/v1/organizations/11111111-1111-1111-1111-111111111111/projects/22222222-2222-2222-2222-222222222222/mfd-imports/review", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, resolutions);
    }

    private static byte[] Confirmation(string digest, bool ack) => JsonSerializer.SerializeToUtf8Bytes(new { reviewDigest = digest, acknowledgeWarnings = ack });
    private static Task<HttpResponseMessage> Post(CatalogHttpTestSupport host, string path, string token,
        byte[] bytes, string? media, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        if (media is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        request.Headers.Add("Origin", host.Origin.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-XSRF-TOKEN", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return host.Client.SendAsync(request);
    }

    private sealed class ReviewClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Backend(PostgresIngestionTestSupport test, IProjectReader projects) : IInterchangeBackend,
        IInterchangeBackendRegistry, IIngestionAdmissionBackend, IPrivateObjectStore, IRevisionReportMetadataReader
    {
        public IngestionBackendProfile Profile => PostgresIngestionTestSupport.Profile;
        internal int Imports;
        internal PrivateObjectReference? Reference;
        internal TaskCompletionSource? ValidationGate;
        internal readonly TaskCompletionSource ValidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] native = [];
        public IInterchangeBackend? GetCurrent(string backendId) => backendId == Profile.BackendId ? this : null;
        public Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken)
        {
            Imports++;
            return Task.FromResult(new InterchangeImportOutput(Profile, package.Digest, "native-synthetic"u8, new("Synthetic mapping", [new("unsupported", "Review warning")])));
        }
        public Task<InterchangeExportOutput> ExportAsync(NativeArtifact artifact, string designPath, CancellationToken cancellationToken) =>
            Task.FromResult(new InterchangeExportOutput(Profile, artifact.Digest,
                new(designPath, [new(designPath, "<self-authored/>"u8), new("schema.json", "{}"u8)]), new("Synthetic export", [new("lossy", "Review warning")])));
        public async Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            ValidationStarted.TrySetResult();
            if (ValidationGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            using var diagnostics = JsonDocument.Parse("[]");
            return IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(
                new(IngestionBackendStatus.Succeeded, new(artifact.Scope, artifact.Digest, Profile, true), true, 0, 0, false), diagnostics.RootElement));
        }
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes, cancellationToken);
            Reference = reference;
            native = bytes.ToArray();
            return PrivateObjectPublishResult.Created;
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) => Task.FromResult((byte[])native.Clone());
        public async Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
        {
            var access = await projects.GetAccessAsync(actor, test.Project, cancellationToken);
            return Reference?.Id.Scope == scope && access?.Can(ProjectAction.ReadMetadata) == true
                ? new(new(scope, Reference, IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, Profile.BackendId), actor.PrincipalId, DateTimeOffset.UtcNow) : null;
        }
        public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
