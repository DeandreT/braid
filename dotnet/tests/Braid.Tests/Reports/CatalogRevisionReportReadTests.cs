using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.Persistence.Postgres;
using H = Braid.Tests.CatalogRevisionReportHttpTestSupport;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CatalogRevisionReportReadTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task AllSixRealRoutesPreserveExactValuesAndCurrentRetainedAndCachedEnsureDoNoAdditionalWork()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        var source = await test.SourceSnapshotAsync();
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            using var absent = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, operation));
            var body = await H.ReportAsync(absent, Name(operation), "notDerived");
            Assert.Equal(JsonValueKind.Null, body.GetProperty("report").ValueKind);
        }
        Assert.Equal(0, test.BackendPort.Calls);
        Assert.Equal(0, test.MetadataPort.ClaimCalls);
        var ids = new Dictionary<RevisionReportOperation, Guid>();
        foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            using var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, operation), token);
            var body = await H.ReportAsync(response, Name(operation));
            Assert.Equal("observed", body.GetProperty("readOutcome").GetString());
            Assert.Equal("succeeded", body.GetProperty("status").GetString());
            Assert.Equal("1", body.GetProperty("result").GetProperty("generation").GetString());
            ids.Add(operation, body.GetProperty("result").GetProperty("derivationId").GetGuid());
            AssertPublic(body);
            if (operation == RevisionReportOperation.Projection)
            {
                var literals = body.GetProperty("report").GetProperty("document").GetProperty("rules").EnumerateArray()
                    .SelectMany(rule => rule.GetProperty("dependencies").EnumerateArray())
                    .Where(dependency => dependency.GetProperty("kind").GetString() == "constant")
                    .Select(dependency => dependency.GetProperty("value")).ToArray();
                Assert.Contains(literals, value => value.GetProperty("kind").GetString() == "exactNumber" && value.GetProperty("value").GetString() == "9007199254740993");
                Assert.Contains(literals, value => value.GetProperty("kind").GetString() == "exactNumber" && value.GetProperty("value").GetString() == "8000000000000000");
            }
            else Assert.True(body.GetProperty("report").GetProperty("isValid").GetBoolean());
        }
        Assert.Equal(2, test.BackendPort.Calls);
        var counts = await test.CountsAsync();
        var claims = test.MetadataPort.ClaimCalls;
        var publications = test.StoragePort.PublishCalls;
        var nativeReads = test.StoragePort.NativeReads;
        var nativePath = test.PhysicalRoot.FinalPath(test.Source.NativeObject);
        var held = test.PhysicalRoot.NewPath("http-owned-native-held.blob");
        File.Move(nativePath, held);
        try
        {
            foreach (var operation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
            {
                foreach (var (method, path) in new[]
                {
                    (HttpMethod.Get, H.CurrentPath(test.Revision, operation)),
                    (HttpMethod.Get, H.RetainedPath(test.Revision, operation, ids[operation])),
                    (HttpMethod.Post, H.EnsurePath(test.Revision, operation))
                })
                {
                    using var response = await api.SendAsync(method, path, token);
                    var body = await H.ReportAsync(response, Name(operation));
                    Assert.Equal(ids[operation], body.GetProperty("result").GetProperty("derivationId").GetGuid());
                    AssertPublic(body);
                }
            }
        }
        finally { File.Move(held, nativePath); }
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(nativeReads, test.StoragePort.NativeReads);
        Assert.Equal(publications, test.StoragePort.PublishCalls);
        Assert.Equal(claims, test.MetadataPort.ClaimCalls);
        Assert.Equal(counts, await test.CountsAsync());
        Assert.Equal(source, await test.SourceSnapshotAsync());
        Assert.Equal(0, api.BodyReads);
    }

    [RevisionIngestionFact]
    public async Task RealPartialAndFalseValidationResponsesLinkOnlyTheExactVerifiedProjectionAndKeepStoredBytesUnlinked()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "validation-cycle");
        await using var api = await H.StartAsync(test);
        var token = await api.TokenAsync();
        Guid projection;
        using (var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token))
        {
            var body = await H.ReportAsync(response, "projection");
            projection = body.GetProperty("result").GetProperty("derivationId").GetGuid();
            Assert.Equal("partial", body.GetProperty("report").GetProperty("document").GetProperty("coverage").GetProperty("status").GetString());
        }
        Guid validation;
        using (var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Validation), token))
        {
            var body = await H.ReportAsync(response, "validation");
            validation = body.GetProperty("result").GetProperty("derivationId").GetGuid();
            Assert.Equal("invalidMapping", body.GetProperty("status").GetString());
            Assert.False(body.GetProperty("report").GetProperty("isValid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("projectionDerivationId").ValueKind);
            Assert.All(body.GetProperty("report").GetProperty("diagnostics").EnumerateArray(),
                diagnostic => Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("projectionLocator").ValueKind));
        }
        var retained = (await test.Reader.GetRetainedAsync(test.Actor, test.Revision, RevisionReportOperation.Validation, validation))!.Result!;
        var bytes = await test.PhysicalStore.ReadVerifiedAsync(test.Revision, retained.DerivedObject);
        var row = await test.AttemptSnapshotAsync(validation);
        using (var response = await api.SendAsync(HttpMethod.Get, H.RetainedPath(test.Revision, RevisionReportOperation.Validation, validation) + $"?projectionDerivationId={projection:D}"))
        {
            var body = await H.ReportAsync(response, "validation");
            Assert.Equal(projection, body.GetProperty("projectionDerivationId").GetGuid());
            Assert.Contains(body.GetProperty("report").GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("projectionLocator").ValueKind == JsonValueKind.Object &&
                    diagnostic.GetProperty("projectionLocator").GetProperty("kind").GetString() == "rule");
        }
        using (var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Validation) + $"?projectionDerivationId={Guid.NewGuid():D}"))
            await H.ErrorAsync(response, HttpStatusCode.NotFound, "notFound");
        Assert.Equal(2, test.BackendPort.Calls);
        Assert.Equal(row, await test.AttemptSnapshotAsync(validation));
        Assert.Equal(bytes, await test.PhysicalStore.ReadVerifiedAsync(test.Revision, retained.DerivedObject));
        using var stored = JsonDocument.Parse(bytes);
        Assert.All(stored.RootElement.GetProperty("report").GetProperty("diagnostics").EnumerateArray(),
            diagnostic => Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("projectionLocator").ValueKind));
    }

    [RevisionIngestionFact]
    public async Task AllFourReadMetadataRolesExposeReportActionsAndMayDeriveThroughTheRealHttpRoute()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var index = 0;
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4 AND role<>$1",
                role, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            var service = test.NewService(test.NewRegistry(new(itemLimit: 2048 - index++)));
            await using var api = await H.StartAsync(test, reader: service, deriver: service);
            var token = await api.TokenAsync();
            using (var project = await api.Client.GetAsync(CatalogHttpTestSupport.ProjectPath(test.Project)))
            {
                var actions = (await CatalogHttpTestSupport.JsonAsync(project)).GetProperty("access").GetProperty("actions");
                Assert.True(actions.GetProperty("readReports").GetBoolean());
                Assert.True(actions.GetProperty("deriveReports").GetBoolean());
            }
            using var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token);
            await H.ReportAsync(response, "projection");
        }
        Assert.Equal(4, test.BackendPort.Calls);
        Assert.Equal(new long[] { 4, 4, 4, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task ReadOnlyAndCachedEnsureNeedNoWriterOrWorkerAndUnavailableReportsLeaveMetadataReadable()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        var selected = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        await using var reads = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
        var writer = new ForbiddenWriter();
        var registry = FerruleRevisionReportBackendFactory.CreateRegistry(test.Profile, new(),
            () => throw new InvalidOperationException("A cached HTTP read resolved a worker."));
        var readonlyService = test.NewService(registry, reader: new PostgresRevisionReportMetadataReader(reads), metadata: writer);
        await using (var api = await H.StartAsync(test, derivationEnabled: false, reader: readonlyService, registerDeriver: false))
        {
            var token = await api.TokenAsync();
            using (var response = await api.SendAsync(HttpMethod.Get, H.RetainedPath(test.Revision, RevisionReportOperation.Projection, selected.Metadata.DerivationId)))
                await H.ReportAsync(response, "projection");
            using (var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), token))
                await H.ErrorAsync(response, HttpStatusCode.NotFound, "unavailable");
            Assert.Equal(0, api.DeriverResolutions);
        }
        await using (var api = await H.StartAsync(test, reader: readonlyService, deriver: readonlyService))
        {
            using var response = await api.SendAsync(HttpMethod.Post, H.EnsurePath(test.Revision, RevisionReportOperation.Projection), await api.TokenAsync());
            await H.ReportAsync(response, "projection");
        }
        await using (var unavailable = await H.StartAsync(test, registerReader: false, registerDeriver: false))
        {
            using (var response = await unavailable.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection)))
                await H.ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "revisionReportUnavailable");
            using var metadata = await unavailable.Client.GetAsync(H.RevisionPath(test.Revision));
            await CatalogHttpTestSupport.JsonAsync(metadata);
        }
        await using (var unsafeStore = await H.StartAsync(test))
        {
            File.SetUnixFileMode(test.PhysicalRoot.MarkerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                using (var report = await unsafeStore.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection)))
                    await H.ErrorAsync(report, HttpStatusCode.ServiceUnavailable, "revisionReportUnavailable");
                using var metadata = await unsafeStore.Client.GetAsync(H.RevisionPath(test.Revision));
                await CatalogHttpTestSupport.JsonAsync(metadata);
            }
            finally { File.SetUnixFileMode(test.PhysicalRoot.MarkerPath, PrivateStorageTestRoot.FinalMode); }
        }
        Assert.Equal(0, writer.Calls);
        Assert.Equal(1, test.BackendPort.Calls);
        Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
    }

    [RevisionIngestionFact]
    public async Task CompleteRealWrapperByteValueAndDepthCapsAdmitExactlyAndRefuseOneBelowBeforePartialOutput()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture, "valid-basic");
        RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection));
        byte[] baseline;
        await using (var api = await H.StartAsync(test))
        {
            using var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            baseline = await response.Content.ReadAsByteArrayAsync();
        }
        using var document = JsonDocument.Parse(baseline);
        var values = CountValues(document.RootElement);
        var depth = ContainerDepth(document.RootElement, 0);
        foreach (var (exact, below) in new[]
        {
            (new RevisionReportPolicy(responseByteLimit: baseline.Length), new RevisionReportPolicy(responseByteLimit: baseline.Length - 1)),
            (new RevisionReportPolicy(responseItemLimit: values), new RevisionReportPolicy(responseItemLimit: values - 1)),
            (new RevisionReportPolicy(jsonDepthLimit: depth), new RevisionReportPolicy(jsonDepthLimit: depth - 1))
        })
        {
            await using (var api = await H.StartAsync(test, activeReadCaps: exact))
            {
                using var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
                var body = await H.ReportAsync(response, "projection");
                Assert.Equal("observed", body.GetProperty("readOutcome").GetString());
                Assert.Equal(baseline, await response.Content.ReadAsByteArrayAsync());
            }
            await using (var api = await H.StartAsync(test, activeReadCaps: below))
            {
                using var response = await api.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
                var body = await H.ReportAsync(response, "projection");
                Assert.Equal("resourceLimit", body.GetProperty("readOutcome").GetString());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("report").ValueKind);
                Assert.Equal("succeeded", body.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Object, body.GetProperty("result").ValueKind);
            }
        }
        await using (var tiny = await H.StartAsync(test, activeReadCaps: new(responseByteLimit: 1)))
        {
            using var response = await tiny.SendAsync(HttpMethod.Get, H.CurrentPath(test.Revision, RevisionReportOperation.Projection));
            await H.ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "revisionReportResourceLimit");
            Assert.InRange((await response.Content.ReadAsByteArrayAsync()).Length, 1, 1024);
        }
        Assert.Equal(1, test.BackendPort.Calls);

        static int CountValues(JsonElement value) => 1 + (value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Sum(item => CountValues(item.Value)),
            JsonValueKind.Array => value.EnumerateArray().Sum(CountValues),
            _ => 0
        });
        static int ContainerDepth(JsonElement value, int containing) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Select(item => ContainerDepth(item.Value, containing + 1)).Prepend(containing + 1).Max(),
            JsonValueKind.Array => value.EnumerateArray().Select(item => ContainerDepth(item, containing + 1)).Prepend(containing + 1).Max(),
            _ => containing
        };
    }

    private static string Name(RevisionReportOperation operation) => operation == RevisionReportOperation.Projection ? "projection" : "validation";

    private static void AssertPublic(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "candidateObjectId", "cacheEntryId", "leaseToken", "leaseExpiresAtUtc",
                    "serverInstanceId", "derivedObject", "nativeObjectId", "objectId", "dependencyIdentity", "physicalPath", "policy", "key" });
                AssertPublic(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertPublic(item);
    }

    private sealed class ForbiddenWriter : IRevisionReportMetadataStore
    {
        internal int Calls { get; private set; }
        private Task<RevisionReportStoreResult> Refuse() { Calls++; throw new InvalidOperationException("A cached HTTP request attempted a metadata write."); }
        public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key, Guid serverInstanceId, CancellationToken cancellationToken = default) => Refuse();
        public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim, RevisionReportCompletion completion, CancellationToken cancellationToken = default) => Refuse();
        public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target, Guid serverInstanceId, CancellationToken cancellationToken = default) => Refuse();
    }
}
