using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>HTTP-created projects, current membership authority and actual private-storage/Ferrule uploads.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ProjectCreationUploadHttpTests(PostgresFixture fixture)
{
    private const string ProjectCreateMediaType = "application/vnd.braid.project-create.v1+json";
    private const string ProjectName = "HTTP-only project";
    private const string ProjectDescription = "Initial Administrator metadata 😀 + %";

    [RevisionIngestionFact]
    public async Task HttpCreatesOnlyTargetAndItsFirstAdministratorThenUploadsExactNativeBytesAndReadsHistory()
    {
        // The original fixture contains unrelated projects only. This actor has no
        // project membership, and the target ID does not exist until the real POST.
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        var actor = new ActorContext(new PrincipalId(test.Seed.Admitted));
        await test.SetCapabilityAsync(true, actor.PrincipalId.Value);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>(
            "SELECT count(*) FROM braid.project_memberships WHERE principal_id=$1", actor.PrincipalId.Value));
        var before = await test.CountsAsync();
        await using var native = NativeUploadComposition.Create(test.Case);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test,
            principal: actor.PrincipalId.Value, ingestionService: native.Service,
            ingestionProjectReader: native.ProjectReader);
        var token = await api.TokenAsync();
        var projectKey = Guid.NewGuid();
        using var projectResponse = await PostProjectAsync(api, test.OrganizationId, token, projectKey);
        var created = await ProjectResultAsync(projectResponse, HttpStatusCode.Created, "created", actor, test.OrganizationId);
        var project = Project(created);
        Assert.DoesNotContain(project.ProjectId, new[] { test.Seed.ProjectA, test.Seed.HiddenProjectA, test.Seed.ProjectB });
        Assert.Equal(new[] { before[0] + 1, before[1] + 1, before[2] + 1 }, await test.CountsAsync());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM braid.project_memberships
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
              AND role='administrator' AND active AND version=1
            """, project.OrganizationId, project.ProjectId, actor.PrincipalId.Value));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>(
            "SELECT count(*) FROM braid.project_memberships WHERE principal_id=$1", actor.PrincipalId.Value));
        Assert.Equal(actor.PrincipalId.Value, await test.Case.ScalarAsync<Guid>(
            "SELECT creator_principal_id FROM braid.projects WHERE organization_id=$1 AND id=$2",
            project.OrganizationId, project.ProjectId));
        await AssertProjectReadAsync(api, created.GetProperty("projectUrl").GetString()!, project, "administrator", createMappings: true);
        using (var projectsResponse = await api.Client.GetAsync($"/api/v1/organizations/{project.OrganizationId:D}/projects"))
        {
            var page = await CatalogHttpTestSupport.JsonAsync(projectsResponse);
            Assert.Equal(project.ProjectId, Assert.Single(page.GetProperty("projects").EnumerateArray())
                .GetProperty("scope").GetProperty("projectId").GetGuid());
        }
        using (var hiddenResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(new(test.OrganizationId, test.Seed.HiddenProjectA))))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenResponse, HttpStatusCode.NotFound, "notFound");

        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        using var uploadResponse = await PostNativeAsync(api,
            RevisionUploadHttpTestSupport.CreatePath(project) + RevisionUploadHttpTestSupport.CreateQuery(
                "Native in HTTP-created project", "Created through the first membership", filename: "first.native.json"),
            token, Guid.NewGuid(), bytes);
        var uploaded = await RevisionUploadHttpTestSupport.ResultAsync(uploadResponse, HttpStatusCode.OK, "committedValid");
        var first = await AssertActualPrivateRevisionAsync(native, actor, project, uploaded, bytes);
        Assert.Equal("1", uploaded.GetProperty("receipt").GetProperty("committedSequence").GetString());
        Assert.Equal("first.native.json", uploaded.GetProperty("receipt").GetProperty("originalFilename").GetString());
        using (var receiptResponse = await api.Client.GetAsync(
            RevisionUploadHttpTestSupport.ReceiptPath(project, first.Facts.OperationId)))
        {
            var receipt = await CatalogHttpTestSupport.JsonAsync(receiptResponse);
            Assert.Equal(uploaded.GetProperty("receipt").GetRawText(), receipt.GetRawText());
            RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
        }

        var mapping = RevisionUploadHttpTestSupport.Mapping(uploaded);
        Assert.Equal(project.OrganizationId, mapping.OrganizationId);
        Assert.Equal(project.ProjectId, mapping.ProjectId);
        var appendedBytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
        using var appendResponse = await PostNativeAsync(api,
            RevisionUploadHttpTestSupport.AppendPath(mapping) + "?head=any&originalFilename=second.native.json",
            token, Guid.NewGuid(), appendedBytes);
        var appended = await RevisionUploadHttpTestSupport.ResultAsync(appendResponse, HttpStatusCode.OK, "committedValid");
        var second = await AssertActualPrivateRevisionAsync(native, actor, project, appended, appendedBytes);
        Assert.Equal(mapping, RevisionUploadHttpTestSupport.Mapping(appended));
        Assert.Equal("2", appended.GetProperty("receipt").GetProperty("committedSequence").GetString());
        var mappingPath = CatalogHttpTestSupport.MappingPath(first.Facts.FutureRevision);
        using (var mappingResponse = await api.Client.GetAsync(mappingPath))
        {
            var current = await CatalogHttpTestSupport.JsonAsync(mappingResponse);
            Assert.Equal("Native in HTTP-created project", current.GetProperty("name").GetString());
            Assert.Equal("administrator", current.GetProperty("access").GetProperty("role").GetString());
            Assert.True(current.GetProperty("access").GetProperty("actions").GetProperty("uploadRevisions").GetBoolean());
            Assert.False(current.GetProperty("access").GetProperty("actions").GetProperty("createProjects").GetBoolean());
            Assert.Equal(second.Facts.FutureRevision.RevisionId,
                current.GetProperty("head").GetProperty("scope").GetProperty("revisionId").GetGuid());
        }
        using (var historyResponse = await api.Client.GetAsync(mappingPath + "/revisions"))
        {
            var history = await CatalogHttpTestSupport.JsonAsync(historyResponse);
            Assert.Equal(new[] { "2", "1" }, history.GetProperty("revisions").EnumerateArray()
                .Select(revision => revision.GetProperty("sequence").GetString()));
            Assert.Equal(new[] { second.Facts.FutureRevision.RevisionId, first.Facts.FutureRevision.RevisionId },
                history.GetProperty("revisions").EnumerateArray().Select(revision =>
                    revision.GetProperty("scope").GetProperty("revisionId").GetGuid()));
        }
        foreach (var (result, expectedBytes) in new[] { (uploaded, bytes), (appended, appendedBytes) })
        {
            using var revisionResponse = await api.Client.GetAsync(result.GetProperty("committedRevisionUrl").GetString());
            var revision = await CatalogHttpTestSupport.JsonAsync(revisionResponse);
            Assert.Equal(project.ProjectId, revision.GetProperty("scope").GetProperty("projectId").GetGuid());
            Assert.Equal(actor.PrincipalId.Value, revision.GetProperty("creatorPrincipalId").GetGuid());
            Assert.Equal(expectedBytes.LongLength, revision.GetProperty("nativeByteCount").GetInt64());
            Assert.Equal(Sha256Digest.FromBytes(expectedBytes).Value,
                revision.GetProperty("nativeDigest").GetProperty("value").GetString());
        }
        using var replayResponse = await PostProjectAsync(api, test.OrganizationId, token, projectKey);
        var replayed = await ProjectResultAsync(replayResponse, HttpStatusCode.OK, "replayed", actor, test.OrganizationId);
        Assert.Equal(created.GetProperty("receipt").GetRawText(), replayed.GetProperty("receipt").GetRawText());
        Assert.Equal(new[] { before[0] + 1, before[1] + 1, before[2] + 1 }, await test.CountsAsync());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(2, native.BackendPort.Calls);
        Assert.Equal(bytes, native.Runner.NativeBodies[0]);
        Assert.Equal(appendedBytes, native.Runner.NativeBodies[1]);
        Assert.Empty(native.PhysicalRoot.AttemptFiles());
    }

    [PostgresFact]
    public async Task CreationWithUnregisteredWorkerAndAbsentPhysicalRootDoesNotResolveNativeDependencies()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        var actor = new ActorContext(new PrincipalId(test.Seed.Admitted));
        await test.SetCapabilityAsync(true, actor.PrincipalId.Value);
        var before = await test.CountsAsync();
        using var root = new PrivateStorageTestRoot();
        await using var unavailableStore = new RevisionUploadHttpTestSupport.LazyPhysicalStore(root);
        await using var ingestionDatabase = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
        await using var readerDatabase = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var metadata = new PostgresRevisionIngestionStore(ingestionDatabase);
        var projectReader = new PostgresCatalogReader(readerDatabase);
        var service = new RevisionIngestionService(metadata, metadata, projectReader,
            new(unavailableStore, PrivateStorageTestRoot.NativeLimit), new IngestionBackendRegistry([]));
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test,
            principal: actor.PrincipalId.Value, ingestionService: service, ingestionProjectReader: projectReader);
        var token = await api.TokenAsync();
        using var createdResponse = await PostProjectAsync(api, test.OrganizationId, token, Guid.NewGuid());
        var created = await ProjectResultAsync(createdResponse, HttpStatusCode.Created, "created", actor, test.OrganizationId);
        var project = Project(created);
        await AssertProjectReadAsync(api, created.GetProperty("projectUrl").GetString()!, project, "administrator", createMappings: true);
        Assert.False(Directory.Exists(root.RootPath));
        Assert.Equal(0, unavailableStore.Opens);
        Assert.Equal(0, unavailableStore.Publications);
        Assert.Equal(0, unavailableStore.Reads);

        // The separate upload uses the real application service and restricted
        // pool, but deliberately no registered backend and no physical store.
        using var unavailableResponse = await PostNativeAsync(api,
            RevisionUploadHttpTestSupport.CreatePath(project) + RevisionUploadHttpTestSupport.CreateQuery(),
            token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
        var unavailable = await RevisionUploadHttpTestSupport.ResultAsync(unavailableResponse,
            HttpStatusCode.UnprocessableEntity, "rejected");
        Assert.Equal("unregisteredBackend", unavailable.GetProperty("rejection").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("receipt").ValueKind);
        Assert.Equal(new[] { before[0] + 1, before[1] + 1, before[2] + 1 }, await test.CountsAsync());
        foreach (var table in new[] { "revision_ingestions", "mappings", "mapping_revisions" })
            Assert.Equal(0L, await test.Case.ScalarAsync<long>($"SELECT count(*) FROM braid.{table}"));
        Assert.False(Directory.Exists(root.RootPath));
        Assert.Equal(0, unavailableStore.Opens);
        Assert.Equal(0, unavailableStore.Publications);
        Assert.Equal(0, unavailableStore.Reads);
    }

    [RevisionIngestionFact]
    public async Task HistoricalCreationReplayNeverRestoresDemotedInactiveOrDeletedNativeUploadAuthority()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        var actor = new ActorContext(new PrincipalId(test.Seed.Admitted));
        await test.SetCapabilityAsync(true, actor.PrincipalId.Value);
        await using var native = NativeUploadComposition.Create(test.Case);
        await using var api = await ProjectCreationHttpTestSupport.StartAsync(test,
            principal: actor.PrincipalId.Value, ingestionService: native.Service,
            ingestionProjectReader: native.ProjectReader);
        var token = await api.TokenAsync();
        var key = Guid.NewGuid();
        using var createdResponse = await PostProjectAsync(api, test.OrganizationId, token, key);
        var created = await ProjectResultAsync(createdResponse, HttpStatusCode.Created, "created", actor, test.OrganizationId);
        var project = Project(created);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        using var uploadResponse = await PostNativeAsync(api,
            RevisionUploadHttpTestSupport.CreatePath(project) + RevisionUploadHttpTestSupport.CreateQuery(),
            token, Guid.NewGuid(), bytes);
        var uploaded = await RevisionUploadHttpTestSupport.ResultAsync(uploadResponse, HttpStatusCode.OK, "committedValid");
        var original = await AssertActualPrivateRevisionAsync(native, actor, project, uploaded, bytes);
        var counts = await test.CountsAsync();
        var creationSnapshot = await test.CreationSnapshotAsync(key);
        foreach (var state in new[] { "viewer", "inactive" })
        {
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',active=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                state == "viewer", project.OrganizationId, project.ProjectId, actor.PrincipalId.Value);
            using var replayResponse = await PostProjectAsync(api, test.OrganizationId, token, key);
            var replayed = await ProjectResultAsync(replayResponse, HttpStatusCode.OK, "replayed", actor, test.OrganizationId);
            Assert.Equal(created.GetProperty("receipt").GetRawText(), replayed.GetProperty("receipt").GetRawText());
            Assert.Equal(creationSnapshot, await test.CreationSnapshotAsync(key));
            var membershipCount = await test.Case.ScalarAsync<long>("""
                SELECT count(*) FROM braid.project_memberships
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
                """, project.OrganizationId, project.ProjectId, actor.PrincipalId.Value);
            Assert.Equal(1L, membershipCount);
            if (state == "viewer")
            {
                await AssertProjectReadAsync(api, created.GetProperty("projectUrl").GetString()!, project, "viewer", createMappings: false);
                using var historyResponse = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(original.Facts.FutureRevision) + "/revisions");
                var history = await CatalogHttpTestSupport.JsonAsync(historyResponse);
                Assert.Equal(original.Facts.FutureRevision.RevisionId, Assert.Single(history.GetProperty("revisions").EnumerateArray())
                    .GetProperty("scope").GetProperty("revisionId").GetGuid());
            }
            else
            {
                using var hiddenResponse = await api.Client.GetAsync(created.GetProperty("projectUrl").GetString());
                await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenResponse, HttpStatusCode.NotFound, "notFound");
                using var hiddenHistoryResponse = await api.Client.GetAsync(CatalogHttpTestSupport.MappingPath(original.Facts.FutureRevision) + "/revisions");
                await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenHistoryResponse, HttpStatusCode.NotFound, "notFound");
            }
            api.ResetObservations();
            foreach (var path in new[]
            {
                RevisionUploadHttpTestSupport.CreatePath(project) + RevisionUploadHttpTestSupport.CreateQuery(),
                RevisionUploadHttpTestSupport.AppendPath(RevisionUploadHttpTestSupport.Mapping(uploaded)) + "?head=any"
            })
            {
                using var deniedResponse = await PostNativeAsync(api, path, token, Guid.NewGuid(), bytes);
                await RevisionUploadHttpTestSupport.AssertErrorAsync(deniedResponse, HttpStatusCode.NotFound, "notFound");
            }
            Assert.Equal(0, api.BodyReads);
            Assert.Equal(1, native.BackendPort.Calls);
            Assert.Equal(1, native.StorePort.PublishCalls);
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
            var currentCounts = await test.CountsAsync();
            Assert.Equal(counts[0], currentCounts[0]);
            Assert.Equal(counts[1], currentCounts[1]);
            Assert.Equal(counts[2], currentCounts[2]);
        }
        Assert.Equal(bytes, await native.PhysicalStore.ReadVerifiedAsync(original.Facts.FutureRevision, original.Facts.NativeObject));
        Assert.Single(native.Runner.NativeBodies);

        // Existing mappings retain their creator membership through the original
        // source FK. Deletion replay applies to a separately HTTP-created empty
        // project whose first membership has no mapping reference.
        var referenced = await Assert.ThrowsAsync<PostgresException>(() => test.Case.ExecuteAsync(
            "DELETE FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            project.OrganizationId, project.ProjectId, actor.PrincipalId.Value));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, referenced.SqlState);
        Assert.Equal("mappings_creator_fk", referenced.ConstraintName);
        Assert.Equal(counts, await test.CountsAsync());
        Assert.Equal(creationSnapshot, await test.CreationSnapshotAsync(key));

        var emptyKey = Guid.NewGuid();
        using var emptyCreatedResponse = await PostProjectAsync(api, test.OrganizationId, token, emptyKey);
        var emptyCreated = await ProjectResultAsync(emptyCreatedResponse, HttpStatusCode.Created, "created", actor, test.OrganizationId);
        var emptyProject = Project(emptyCreated);
        Assert.NotEqual(project, emptyProject);
        await AssertProjectReadAsync(api, emptyCreated.GetProperty("projectUrl").GetString()!, emptyProject, "administrator", createMappings: true);
        Assert.Equal(new[] { counts[0] + 1, counts[1] + 1, counts[2] + 1 }, await test.CountsAsync());
        Assert.Equal(0L, await test.Case.ScalarAsync<long>(
            "SELECT count(*) FROM braid.mappings WHERE organization_id=$1 AND project_id=$2",
            emptyProject.OrganizationId, emptyProject.ProjectId));
        var emptyCreationSnapshot = await test.CreationSnapshotAsync(emptyKey);
        await test.Case.ExecuteAsync("DELETE FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            emptyProject.OrganizationId, emptyProject.ProjectId, actor.PrincipalId.Value);
        using var emptyReplayResponse = await PostProjectAsync(api, test.OrganizationId, token, emptyKey);
        var emptyReplayed = await ProjectResultAsync(emptyReplayResponse, HttpStatusCode.OK, "replayed", actor, test.OrganizationId);
        Assert.Equal(emptyCreated.GetProperty("receipt").GetRawText(), emptyReplayed.GetProperty("receipt").GetRawText());
        Assert.Equal(emptyCreationSnapshot, await test.CreationSnapshotAsync(emptyKey));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM braid.project_memberships
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
            """, emptyProject.OrganizationId, emptyProject.ProjectId, actor.PrincipalId.Value));
        using (var hiddenResponse = await api.Client.GetAsync(emptyCreated.GetProperty("projectUrl").GetString()))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenResponse, HttpStatusCode.NotFound, "notFound");
        api.ResetObservations();
        using (var deniedResponse = await PostNativeAsync(api,
            RevisionUploadHttpTestSupport.CreatePath(emptyProject) + RevisionUploadHttpTestSupport.CreateQuery(),
            token, Guid.NewGuid(), bytes))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(deniedResponse, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(1, native.BackendPort.Calls);
        Assert.Equal(1, native.StorePort.PublishCalls);
        Assert.Equal(new[] { counts[0] + 1, counts[1], counts[2] + 1 }, await test.CountsAsync());
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        Assert.Equal(creationSnapshot, await test.CreationSnapshotAsync(key));
        Assert.Equal(bytes, await native.PhysicalStore.ReadVerifiedAsync(original.Facts.FutureRevision, original.Facts.NativeObject));
        Assert.Single(native.Runner.NativeBodies);
    }

    private static Task<HttpResponseMessage> PostProjectAsync(ProjectCreationHttpTestSupport api, Guid organization,
        string token, Guid key) => PostAsync(api, $"/api/v1/organizations/{organization:D}/projects", token, key,
        JsonSerializer.SerializeToUtf8Bytes(new { name = "  " + ProjectName + "  ", description = ProjectDescription }), ProjectCreateMediaType);

    private static Task<HttpResponseMessage> PostNativeAsync(ProjectCreationHttpTestSupport api, string path,
        string token, Guid key, byte[] bytes) => PostAsync(api, path, token, key, bytes, RevisionUploadHttpTestSupport.MediaType);

    private static async Task<HttpResponseMessage> PostAsync(ProjectCreationHttpTestSupport api, string path,
        string token, Guid key, byte[] bytes, string mediaType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(bytes) };
        Assert.True(request.Content.Headers.TryAddWithoutValidation("Content-Type", mediaType));
        Assert.True(request.Headers.TryAddWithoutValidation("Origin", api.Origin.GetLeftPart(UriPartial.Authority)));
        Assert.True(request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token));
        Assert.True(request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D")));
        return await api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> ProjectResultAsync(HttpResponseMessage response, HttpStatusCode status,
        string outcome, ActorContext actor, Guid organization)
    {
        var result = await CatalogHttpTestSupport.JsonAsync(response, status);
        Assert.Equal(outcome, result.GetProperty("outcome").GetString());
        Assert.Equal(new[] { "outcome", "projectUrl", "receipt" },
            result.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var receipt = result.GetProperty("receipt");
        Assert.Equal(new[] { "createdAtUtc", "creatorPrincipalId", "description", "initialMembershipActive", "initialMembershipRole", "initialMembershipVersion", "initialProjectVersion", "name", "project" },
            receipt.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(actor.PrincipalId.Value, receipt.GetProperty("creatorPrincipalId").GetGuid());
        Assert.Equal(ProjectName, receipt.GetProperty("name").GetString());
        Assert.Equal(ProjectDescription, receipt.GetProperty("description").GetString());
        Assert.Equal(TimeSpan.Zero, receipt.GetProperty("createdAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal("administrator", receipt.GetProperty("initialMembershipRole").GetString());
        Assert.True(receipt.GetProperty("initialMembershipActive").GetBoolean());
        Assert.Equal("1", receipt.GetProperty("initialMembershipVersion").GetString());
        Assert.Equal("1", receipt.GetProperty("initialProjectVersion").GetString());
        var project = Project(result);
        Assert.Equal(organization, project.OrganizationId);
        Assert.NotEqual(Guid.Empty, project.ProjectId);
        Assert.Equal(CatalogHttpTestSupport.ProjectPath(project), result.GetProperty("projectUrl").GetString());
        Assert.Equal(result.GetProperty("projectUrl").GetString(), response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        return result;
    }

    private static ProjectScope Project(JsonElement result)
    {
        var project = result.GetProperty("receipt").GetProperty("project");
        return new(project.GetProperty("organizationId").GetGuid(), project.GetProperty("projectId").GetGuid());
    }

    private static async Task AssertProjectReadAsync(ProjectCreationHttpTestSupport api, string path,
        ProjectScope expected, string role, bool createMappings)
    {
        using var response = await api.Client.GetAsync(path);
        var project = await CatalogHttpTestSupport.JsonAsync(response);
        Assert.Equal(expected.OrganizationId, project.GetProperty("scope").GetProperty("organizationId").GetGuid());
        Assert.Equal(expected.ProjectId, project.GetProperty("scope").GetProperty("projectId").GetGuid());
        Assert.Equal(ProjectName, project.GetProperty("name").GetString());
        Assert.Equal(ProjectDescription, project.GetProperty("description").GetString());
        Assert.Equal(role, project.GetProperty("access").GetProperty("role").GetString());
        Assert.Equal(createMappings, project.GetProperty("access").GetProperty("actions").GetProperty("createMappings").GetBoolean());
        Assert.False(project.GetProperty("access").GetProperty("actions").GetProperty("createProjects").GetBoolean());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task<IngestionReceipt> AssertActualPrivateRevisionAsync(NativeUploadComposition native,
        ActorContext actor, ProjectScope project, JsonElement result, byte[] expectedBytes)
    {
        var receipt = Assert.IsType<IngestionReceipt>(await native.Metadata.GetReceiptAsync(actor, project,
            RevisionUploadHttpTestSupport.Operation(result)));
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        Assert.Equal(project, receipt.Facts.Project);
        Assert.Equal(actor.PrincipalId, receipt.Facts.OriginalActor);
        Assert.Equal(RevisionUploadHttpTestSupport.Revision(result), receipt.Facts.FutureRevision.RevisionId);
        Assert.Equal(expectedBytes, await native.PhysicalStore.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
        Assert.Equal(Sha256Digest.FromBytes(expectedBytes), receipt.Facts.NativeObject.Digest);
        Assert.Equal(expectedBytes.LongLength, receipt.Facts.NativeObject.ByteCount);
        var completion = Assert.Single(receipt.Attempts).Completion;
        Assert.NotNull(completion);
        Assert.Equal(IngestionAttemptCompletionKind.Validation, completion.Kind);
        var evidence = Assert.IsType<IngestionEvidenceDocument>(completion.Evidence);
        Assert.Equal(IngestionBackendStatus.Succeeded, evidence.Facts.Status);
        Assert.True(evidence.Facts.IsValid);
        Assert.True(evidence.Facts.Provenance.Confirmed);
        Assert.Equal(receipt.Facts.FutureRevision, evidence.Facts.Provenance.Scope);
        Assert.Equal(receipt.Facts.NativeObject.Digest, evidence.Facts.Provenance.NativeDigest);
        Assert.Equal(native.Registration.Profile, evidence.Facts.Provenance.BackendProfile);
        Assert.Equal(native.Registration.Profile, receipt.Facts.BackendProfile);
        return receipt;
    }

    /// <summary>Qualified real components attached to a caller-owned case; no project/member setup.</summary>
    private sealed class NativeUploadComposition : IAsyncDisposable
    {
        internal PrivateStorageTestRoot PhysicalRoot { get; }
        internal FileSystemPrivateObjectStore PhysicalStore { get; }
        internal PostgresRevisionIngestionStore Metadata { get; }
        internal PostgresCatalogReader ProjectReader { get; }
        internal RevisionIngestionStorePort StorePort { get; }
        internal RevisionIngestionWorkerRunner Runner { get; }
        internal RevisionIngestionBackendPort BackendPort { get; }
        internal IngestionBackendRegistration Registration { get; }
        internal RevisionIngestionService Service { get; }
        private readonly PostgresIngestionDatabase database;
        private readonly PostgresCatalogDatabase readerDatabase;
        private readonly RevisionIngestionWorkerLauncher launcher;

        private NativeUploadComposition(PostgresCase test, PrivateStorageTestRoot root, FileSystemPrivateObjectStore store)
        {
            PhysicalRoot = root;
            PhysicalStore = store;
            database = new(test.IngestionConnectionString);
            readerDatabase = new(test.CatalogConnectionString);
            Metadata = new(database);
            ProjectReader = new(readerDatabase);
            StorePort = new(PhysicalStore);
            launcher = new();
            var docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "/usr/bin/docker";
            Runner = new(new DockerInspectionWorkerRunner(new(docker,
                Environment.GetEnvironmentVariable("BRAID_FERRULE_WORKER_IMAGE")!), launcher));
            var actual = FerruleIngestionAdmissionBackend.CreateRegistration(Runner);
            BackendPort = new(actual.Backend);
            Registration = new(actual.Profile, actual.NativeByteLimit, BackendPort);
            Service = new(Metadata, Metadata, ProjectReader,
                new(StorePort, PrivateStorageTestRoot.NativeLimit), new IngestionBackendRegistry([Registration]));
        }

        internal static NativeUploadComposition Create(PostgresCase test)
        {
            var root = new PrivateStorageTestRoot();
            FileSystemPrivateObjectStore? store = null;
            try
            {
                store = root.Initialize();
                return new(test, root, store);
            }
            catch
            {
                store?.Dispose();
                root.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            launcher.ReleaseAll();
            try { await database.DisposeAsync(); }
            finally
            {
                try { await readerDatabase.DisposeAsync(); }
                finally
                {
                    try { await PhysicalStore.DisposeAsync(); }
                    finally { PhysicalRoot.Dispose(); }
                }
            }
        }
    }
}
