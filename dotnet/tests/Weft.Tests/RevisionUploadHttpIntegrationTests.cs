using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Real HTTP upload, restricted PostgreSQL, physical storage and the configured Ferrule worker.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionUploadHttpIntegrationTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task KnownAndChunkedCreateAppendPreserveExactNativeBytesAndImmutableMetadata()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var legacy = Encoding.UTF8.GetString(RevisionIngestionTestSupport.Fixture("valid-basic"));
        var end = legacy.LastIndexOf('}');
        var bytes = Encoding.UTF8.GetBytes(" \r\n" + legacy[..end] + ",\"weft_unknown_metadata\":{\"integer\":9007199254740993,\"spelling\":\"a/b~c\"}}\r\n ");
        foreach (var chunked in new[] { false, true })
        {
            var query = RevisionUploadHttpTestSupport.CreateQuery("  HTTP exact bytes  ", "Preserve + % and emoji 😀", filename: "preserved.native.json");
            using var createResponse = await api.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + query,
                token, Guid.NewGuid(), bytes, chunked);
            var created = await RevisionUploadHttpTestSupport.ResultAsync(createResponse, HttpStatusCode.OK, "committedValid");
            var scope = RevisionUploadHttpTestSupport.Mapping(created);
            Assert.Equal("1", created.GetProperty("receipt").GetProperty("committedSequence").GetString());
            Assert.Equal("valid", created.GetProperty("receipt").GetProperty("committedAdmission").GetString());
            Assert.Equal("preserved.native.json", created.GetProperty("receipt").GetProperty("originalFilename").GetString());
            var first = (await test.PrivateReceiptAsync(RevisionUploadHttpTestSupport.Operation(created)))!;
            Assert.Equal(bytes, await test.PhysicalStore.ReadVerifiedAsync(first.Facts.FutureRevision, first.Facts.NativeObject));
            Assert.Equal(bytes, test.Runner.NativeBodies[^1]);
            Assert.Equal(Sha256Digest.FromBytes(bytes), first.Facts.NativeObject.Digest);
            Assert.Equal(bytes.LongLength, first.Facts.NativeObject.ByteCount);
            using var revisionResponse = await api.Client.GetAsync(created.GetProperty("committedRevisionUrl").GetString());
            var revision = await CatalogHttpTestSupport.JsonAsync(revisionResponse);
            Assert.Equal("entry.native.json", revision.GetProperty("logicalDocument").GetString());
            Assert.Equal("1", revision.GetProperty("sequence").GetString());
            Assert.Equal(bytes.Length, revision.GetProperty("nativeByteCount").GetInt32());
            var mappingPath = RevisionUploadHttpTestSupport.AppendPath(scope)[..^"/revisions".Length];
            using var beforeResponse = await api.Client.GetAsync(mappingPath);
            var before = await CatalogHttpTestSupport.JsonAsync(beforeResponse);
            Assert.Equal("HTTP exact bytes", before.GetProperty("name").GetString());
            Assert.Equal("Preserve + % and emoji 😀", before.GetProperty("description").GetString());
            var appendedBytes = RevisionIngestionTestSupport.Fixture("versioned-exact");
            using var appendResponse = await api.PostAsync(RevisionUploadHttpTestSupport.AppendPath(scope) + "?head=any&originalFilename=versioned.native.json",
                token, Guid.NewGuid(), appendedBytes, !chunked);
            var appended = await RevisionUploadHttpTestSupport.ResultAsync(appendResponse, HttpStatusCode.OK, "committedValid");
            Assert.Equal(scope, RevisionUploadHttpTestSupport.Mapping(appended));
            Assert.Equal("2", appended.GetProperty("receipt").GetProperty("committedSequence").GetString());
            Assert.NotEqual(RevisionUploadHttpTestSupport.Revision(created), RevisionUploadHttpTestSupport.Revision(appended));
            var second = (await test.PrivateReceiptAsync(RevisionUploadHttpTestSupport.Operation(appended)))!;
            Assert.Equal(appendedBytes, await test.PhysicalStore.ReadVerifiedAsync(second.Facts.FutureRevision, second.Facts.NativeObject));
            Assert.Equal(appendedBytes, test.Runner.NativeBodies[^1]);
            using var afterResponse = await api.Client.GetAsync(mappingPath);
            var after = await CatalogHttpTestSupport.JsonAsync(afterResponse);
            foreach (var field in new[] { "name", "description", "backendId", "creatorPrincipalId", "ownerPrincipalId", "version" })
                Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
            Assert.Equal("2", after.GetProperty("head").GetProperty("sequence").GetString());
        }
        Assert.Equal(4, test.BackendPort.Calls);
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Empty(test.PhysicalRoot.AttemptFiles());
    }

    [RevisionIngestionFact]
    public async Task MaximumSupplementaryScalarMetadataTraversesActualHttpAndNativeWorkerWithoutQueryTruncation()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var name = string.Concat(Enumerable.Repeat("😀", 200));
        var description = string.Concat(Enumerable.Repeat("𐐀", 4000));
        var filename = new string('a', 255);
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery(name, description, filename: filename);
        Assert.InRange(Encoding.ASCII.GetByteCount("POST " + path + " HTTP/1.1\r\n"), 48_000, RevisionUploadHttpTestSupport.MaximumRequestLineBytes);
        using var response = await api.PostAsync(path, await api.TokenAsync(), Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("valid-basic"));
        var result = await RevisionUploadHttpTestSupport.ResultAsync(response, HttpStatusCode.OK, "committedValid");
        var mapping = RevisionUploadHttpTestSupport.Mapping(result);
        Assert.Equal(name, await test.Case.ScalarAsync<string>("SELECT name FROM weft.mappings WHERE id=$1", mapping.MappingId));
        Assert.Equal(description, await test.Case.ScalarAsync<string>("SELECT description FROM weft.mappings WHERE id=$1", mapping.MappingId));
        Assert.Equal(filename, result.GetProperty("receipt").GetProperty("originalFilename").GetString());
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(RevisionIngestionTestSupport.Fixture("valid-basic"), test.Runner.NativeBodies.Single());
    }

    [RevisionIngestionFact]
    public async Task DecodedInvalidAndClosedNativeRejectionsHaveDistinctSafeWireOutcomes()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        using var invalidResponse = await api.PostAsync(path, token, Guid.NewGuid(), RevisionIngestionTestSupport.Fixture("invalid-function"));
        var invalid = await RevisionUploadHttpTestSupport.ResultAsync(invalidResponse, HttpStatusCode.OK, "committedDecodedInvalid");
        Assert.Equal("decodedInvalid", invalid.GetProperty("receipt").GetProperty("committedAdmission").GetString());
        Assert.Equal("1", invalid.GetProperty("receipt").GetProperty("committedSequence").GetString());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("rejection").ValueKind);
        var unsupported = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(RevisionIngestionTestSupport.Fixture("versioned-exact"))
            .Replace("\"version\": 2", "\"version\": 999", StringComparison.Ordinal));
        var graph = JsonNode.Parse(RevisionIngestionTestSupport.Fixture("valid-basic"))!;
        var nodes = new JsonObject();
        for (var index = 1; index <= 9000; index++)
            nodes[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["kind"] = "const", ["value"] = 0 };
        graph["graph"]!["nodes"] = nodes;
        foreach (var (bytes, rejection) in new[]
        {
            ("{not native JSON"u8.ToArray(), "nativeDecode"),
            (unsupported, "unsupportedArtifact"),
            (JsonSerializer.SerializeToUtf8Bytes(graph), "resourceLimit")
        })
        {
            using var rejectedResponse = await api.PostAsync(path, token, Guid.NewGuid(), bytes);
            var rejected = await RevisionUploadHttpTestSupport.ResultAsync(rejectedResponse, HttpStatusCode.UnprocessableEntity, "rejected");
            Assert.Equal(rejection, rejected.GetProperty("rejection").GetString());
            Assert.Equal("rejected", rejected.GetProperty("receipt").GetProperty("phase").GetString());
            Assert.Equal(JsonValueKind.Null, rejected.GetProperty("committedRevisionUrl").ValueKind);
            Assert.Equal(JsonValueKind.Null, rejected.GetProperty("receipt").GetProperty("committedSequence").ValueKind);
            using var receiptResponse = await api.Client.GetAsync(RevisionUploadHttpTestSupport.ReceiptPath(test.Project, RevisionUploadHttpTestSupport.Operation(rejected)));
            var receipt = await CatalogHttpTestSupport.JsonAsync(receiptResponse);
            RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
            Assert.False(receipt.TryGetProperty("rejectionReason", out _));
        }
        using var emptyResponse = await api.PostAsync(path, token, Guid.NewGuid(), []);
        var empty = await RevisionUploadHttpTestSupport.ResultAsync(emptyResponse, HttpStatusCode.BadRequest, "rejected");
        Assert.Equal("emptyNative", empty.GetProperty("rejection").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("receipt").ValueKind);
        Assert.Null(emptyResponse.Headers.Location);
        api.ResetObservations();
        using var unavailableResponse = await api.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) +
            RevisionUploadHttpTestSupport.CreateQuery(backendId: "unregistered-backend"), token, Guid.NewGuid(), [255]);
        var unavailable = await RevisionUploadHttpTestSupport.ResultAsync(unavailableResponse, HttpStatusCode.UnprocessableEntity, "rejected");
        Assert.Equal("unregisteredBackend", unavailable.GetProperty("rejection").GetString());
        Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("receipt").ValueKind);
        Assert.Equal(0, api.BodyReads);
        Assert.Equal(4, test.BackendPort.Calls);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task FixedFourMiBBoundAndReducedNewPolicyKeepExactBudgetPlusOneReadsAndNoPhantomReservation()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var fixtureBytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var exact = new byte[RevisionUploadHttpTestSupport.MaximumNativeBytes];
        Array.Fill(exact, (byte)' ');
        fixtureBytes.CopyTo(exact, 0);
        await using (var api = await RevisionUploadHttpTestSupport.StartAsync(test))
        {
            var token = await api.TokenAsync();
            var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
            using var overflow = await api.PostAsync(path, token, Guid.NewGuid(), exact.Concat(new byte[4096]).ToArray(), chunked: true);
            var rejected = await RevisionUploadHttpTestSupport.ResultAsync(overflow, HttpStatusCode.RequestEntityTooLarge, "rejected");
            Assert.Equal("nativeLimit", rejected.GetProperty("rejection").GetString());
            Assert.Equal(JsonValueKind.Null, rejected.GetProperty("receipt").ValueKind);
            Assert.Equal(RevisionUploadHttpTestSupport.MaximumNativeBytes + 1L, api.BodyBytes);
            Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
            api.ResetObservations();
            using var accepted = await api.PostAsync(path, token, Guid.NewGuid(), exact, chunked: true);
            var committed = await RevisionUploadHttpTestSupport.ResultAsync(accepted, HttpStatusCode.OK, "committedValid");
            var receipt = (await test.PrivateReceiptAsync(RevisionUploadHttpTestSupport.Operation(committed)))!;
            Assert.Equal(exact, await test.PhysicalStore.ReadVerifiedAsync(receipt.Facts.FutureRevision, receipt.Facts.NativeObject));
            Assert.Equal(exact, test.Runner.NativeBodies.Single());
            Assert.Equal(exact.LongLength, api.BodyBytes);
        }
        await using var reducedApi = await RevisionUploadHttpTestSupport.StartAsync(test, test.NewService(options: new(1), storeLimit: 1));
        using var reduced = await reducedApi.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery(),
            await reducedApi.TokenAsync(), Guid.NewGuid(), fixtureBytes);
        var result = await RevisionUploadHttpTestSupport.ResultAsync(reduced, HttpStatusCode.RequestEntityTooLarge, "rejected");
        Assert.Equal("nativeLimit", result.GetProperty("rejection").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("receipt").ValueKind);
        Assert.Equal(2L, reducedApi.BodyBytes);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(1, test.BackendPort.Calls);
    }

    [RevisionIngestionFact]
    public async Task RestartedTerminalReplayComparesFullBytesWithRemovedBackendReducedCapsAndUnavailablePhysicalRoot()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var bytes = RevisionIngestionTestSupport.Fixture("valid-basic");
        var key = Guid.NewGuid();
        var path = RevisionUploadHttpTestSupport.CreatePath(test.Project) + RevisionUploadHttpTestSupport.CreateQuery();
        JsonElement original;
        await using (var firstApi = await RevisionUploadHttpTestSupport.StartAsync(test))
        {
            using var response = await firstApi.PostAsync(path, await firstApi.TokenAsync(), key, bytes);
            original = await RevisionUploadHttpTestSupport.ResultAsync(response, HttpStatusCode.OK, "committedValid");
        }
        Directory.Move(test.PhysicalRoot.RootPath, Path.Combine(test.PhysicalRoot.BasePath, "owned-unavailable-store"));
        await using var lazy = new RevisionUploadHttpTestSupport.LazyPhysicalStore(test.PhysicalRoot);
        var restartedService = test.NewService(new IngestionBackendRegistry([]), new(1), lazy, 1);
        await using var restarted = await RevisionUploadHttpTestSupport.StartAsync(test, restartedService);
        var token = await restarted.TokenAsync();
        var receiptPath = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, RevisionUploadHttpTestSupport.Operation(original));
        using var receiptResponse = await restarted.Client.GetAsync(receiptPath);
        var receipt = await CatalogHttpTestSupport.JsonAsync(receiptResponse);
        RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
        Assert.Equal(original.GetProperty("receipt").GetRawText(), receipt.GetRawText());
        Assert.True(receiptResponse.Headers.CacheControl?.NoStore);
        using var replayResponse = await restarted.PostAsync(path, token, key, bytes, chunked: true);
        var replay = await RevisionUploadHttpTestSupport.ResultAsync(replayResponse, HttpStatusCode.OK, "committedValid");
        Assert.Equal(original.GetRawText(), replay.GetRawText());
        Assert.Equal(bytes.LongLength, restarted.BodyBytes);
        foreach (var (query, source) in new[]
        {
            (RevisionUploadHttpTestSupport.CreateQuery(name: "changed"), bytes),
            (RevisionUploadHttpTestSupport.CreateQuery(description: "changed"), bytes),
            (RevisionUploadHttpTestSupport.CreateQuery(filename: "changed.native.json"), bytes),
            (RevisionUploadHttpTestSupport.CreateQuery(backendId: "removed-and-different"), bytes),
            (RevisionUploadHttpTestSupport.CreateQuery(), bytes.Concat(new byte[] { (byte)' ' }).ToArray())
        })
        {
            restarted.ResetObservations();
            using var conflictResponse = await restarted.PostAsync(RevisionUploadHttpTestSupport.CreatePath(test.Project) + query, token, key, source);
            var conflict = await RevisionUploadHttpTestSupport.ResultAsync(conflictResponse, HttpStatusCode.Conflict, "idempotencyConflict");
            Assert.Equal(source.LongLength, restarted.BodyBytes);
            Assert.Equal(JsonValueKind.Null, conflict.GetProperty("committedRevisionUrl").ValueKind);
        }
        using var resumeResponse = await restarted.PostAsync(receiptPath + "/resume", token, null, []);
        var resumed = await RevisionUploadHttpTestSupport.ResultAsync(resumeResponse, HttpStatusCode.OK, "committedValid");
        Assert.Equal(original.GetRawText(), resumed.GetRawText());
        Assert.Equal(0, lazy.Opens);
        Assert.Equal(0, lazy.Publications);
        Assert.Equal(0, lazy.Reads);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [RevisionIngestionFact]
    public async Task OwnReceiptAndResumeRequireOriginalActorAndFreshPublisherOrAdministratorAuthority()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var created = await test.CreateAsync(RevisionIngestionTestSupport.Fixture("valid-basic"));
        var id = created.Receipt!.OperationId;
        var path = RevisionUploadHttpTestSupport.ReceiptPath(test.Project, id);
        await using var api = await RevisionUploadHttpTestSupport.StartAsync(test);
        var token = await api.TokenAsync();
        await using var other = await RevisionUploadHttpTestSupport.StartAsync(test, principal: test.Seed.Admitted);
        var otherToken = await other.TokenAsync();
        using (var hiddenReceipt = await other.Client.GetAsync(path))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenReceipt, HttpStatusCode.NotFound, "notFound");
        using (var hiddenResume = await other.PostAsync(path + "/resume", otherToken, null, []))
            await RevisionUploadHttpTestSupport.AssertErrorAsync(hiddenResume, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(0, other.ServiceCalls);
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await RoleAsync(test, role);
            api.ResetObservations();
            using var receiptResponse = await api.Client.GetAsync(path);
            if (role is "viewer" or "runner")
            {
                await RevisionUploadHttpTestSupport.AssertErrorAsync(receiptResponse, HttpStatusCode.NotFound, "notFound");
                using var refused = await api.PostAsync(path + "/resume", token, null, []);
                await RevisionUploadHttpTestSupport.AssertErrorAsync(refused, HttpStatusCode.NotFound, "notFound");
                Assert.Equal(0, api.BodyReads);
                Assert.Equal(0, api.ServiceCalls);
            }
            else
            {
                var receipt = await CatalogHttpTestSupport.JsonAsync(receiptResponse);
                RevisionUploadHttpTestSupport.AssertSafeReceipt(receipt);
                Assert.Equal(id.ToString("D"), receipt.GetProperty("operationId").GetString());
                using var resumed = await api.PostAsync(path + "/resume", token, null, []);
                await RevisionUploadHttpTestSupport.ResultAsync(resumed, HttpStatusCode.OK, "committedValid");
                Assert.Equal(1, api.ServiceCalls);
            }
        }
        var wrongProject = new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA);
        using var wrongAncestor = await api.Client.GetAsync(RevisionUploadHttpTestSupport.ReceiptPath(wrongProject, id));
        await RevisionUploadHttpTestSupport.AssertErrorAsync(wrongAncestor, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(1, test.StorePort.PublishCalls);
    }

    [RevisionIngestionFact]
    public async Task CatalogActionsFollowFreshRolesAndIndependentUploadConfiguration()
    {
        await using var test = await RevisionIngestionTestSupport.CreateAsync(fixture);
        var created = await test.CreateAsync(RevisionIngestionTestSupport.Fixture("valid-basic"));
        var mappingPath = RevisionUploadHttpTestSupport.AppendPath(created.Receipt!.FutureMapping)[..^"/revisions".Length];
        foreach (var enabled in new[] { false, true })
        {
            await using var api = await RevisionUploadHttpTestSupport.StartAsync(test, enabled: enabled);
            using var contextResponse = await api.Client.GetAsync("/api/v1/catalog/context");
            var context = await CatalogHttpTestSupport.JsonAsync(contextResponse);
            Assert.Equal(enabled, context.GetProperty("features").GetProperty("createMappings").GetBoolean());
            Assert.Equal(enabled, context.GetProperty("features").GetProperty("uploadRevisions").GetBoolean());
            Assert.False(context.GetProperty("features").GetProperty("editMetadata").GetBoolean());
            foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
            {
                await RoleAsync(test, role);
                using var projectResponse = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project));
                using var mappingResponse = await api.Client.GetAsync(mappingPath);
                var project = await CatalogHttpTestSupport.JsonAsync(projectResponse);
                var mapping = await CatalogHttpTestSupport.JsonAsync(mappingResponse);
                var permitted = enabled && (role is "publisher" or "administrator");
                Assert.Equal(permitted, project.GetProperty("access").GetProperty("actions").GetProperty("createMappings").GetBoolean());
                Assert.False(project.GetProperty("access").GetProperty("actions").GetProperty("editMetadata").GetBoolean());
                Assert.Equal(permitted, mapping.GetProperty("access").GetProperty("actions").GetProperty("uploadRevisions").GetBoolean());
                Assert.False(mapping.GetProperty("access").GetProperty("actions").GetProperty("editMetadata").GetBoolean());
            }
        }
    }

    internal static Task RoleAsync(RevisionIngestionTestSupport test, string role) => test.Case.ExecuteAsync(
        "UPDATE weft.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
        role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
}
