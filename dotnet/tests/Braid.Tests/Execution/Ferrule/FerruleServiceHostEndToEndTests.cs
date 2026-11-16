using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Braid.Application;
using Braid.Jobs;
using Braid.MappingBackends.Workers;
using Braid.Storage.FileSystem;
using Wolverine;
using Wolverine.Runtime;

namespace Braid.Tests;

/// <summary>Runs with PostgreSQL and a worker executable for the packaged Ferrule service.</summary>
public sealed class ServiceHostFactAttribute : FactAttribute
{
    public ServiceHostFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable))
            Skip = "Configure PostgreSQL and BRAID_FERRULE_HOST_EXECUTABLE to run Braid hosts through the packaged Ferrule service.";
    }
}

/// <summary>
/// The published Session host and the ordinary Braid.Worker build selecting the packaged Ferrule service with real
/// sandboxed children, restricted PostgreSQL roles and private storage. Neither host uses Docker.
/// </summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class FerruleServiceHostEndToEndTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Native = TimeSpan.FromSeconds(60);

    [ServiceHostFact]
    public Task PublishedSessionUploadsAndDerivesThroughTheFerruleService() =>
        ServerHostSessionCase.RunAsync(postgres, async test =>
        {
            using var storage = new PrivateStorageTestRoot();
            using (FileSystemPrivateObjectStore.InitializeV2(storage.Options)) { }
            var root = FerruleServiceTestSupport.TemporaryRoot();
            try
            {
                var serviceCertificate = test.Files.HistoricalPair("ferrule-service", expired: false);
                var client = test.Files.HistoricalPair("ferrule-service-client", expired: false);
                var identity = PackagedFerruleService.Identity;
                await using var service = await PackagedFerruleService.StartAsync(
                    File.ReadAllText(serviceCertificate.CertificateFile) + "\n" + File.ReadAllText(serviceCertificate.PrivateKeyFile),
                    [Pin(client.CertificateFile)], root);
                var environment = test.Environment();
                environment["BRAID_REVISION_INGESTION_ENABLED"] = "true";
                environment["BRAID_POSTGRES_INGESTION_CONNECTION_STRING"] = test.Data.Case.IngestionConnectionString;
                environment["BRAID_PRIVATE_STORAGE_ROOT"] = storage.RootPath;
                environment["BRAID_REVISION_REPORTS_ENABLED"] = "true";
                environment["BRAID_POSTGRES_REPORT_CONNECTION_STRING"] = test.Data.Case.ReportConnectionString;
                environment["BRAID_REVISION_REPORT_DERIVATION_ENABLED"] = "true";
                environment["BRAID_POSTGRES_DERIVATION_CONNECTION_STRING"] = test.Data.Case.DerivationConnectionString;
                environment["BRAID_FERRULE_RUNNER"] = "service";
                environment["BRAID_FERRULE_WORKER_IMAGE"] = identity;
                environment["BRAID_FERRULE_SERVICE_URL"] = "https://127.0.0.1:" + service.Port.ToString(CultureInfo.InvariantCulture);
                environment["BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256"] = Pin(serviceCertificate.CertificateFile);
                environment["BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE"] = client.CertificateFile;
                environment["BRAID_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE"] = client.PrivateKeyFile;
                var host = test.Start(environment);
                using var http = ServerHostHttp.Client(test.Origin, test.Files.TlsCertificate);
                http.Timeout = Native;
                test.PositiveStatus(await ServerHostHttp.StartupStatusAsync(http, test.Cookies.Header));
                var context = await test.ContextAsync(http); test.PositiveContext(context);
                string token;
                using (var body = context.Json()) token = body.RootElement.GetProperty("requestToken").GetString()!;
                var unsafeHeaders = new Dictionary<string, string>
                {
                    ["Cookie"] = test.Cookies.Header,
                    ["Origin"] = test.Origin.GetLeftPart(UriPartial.Authority),
                    ["X-XSRF-TOKEN"] = token
                };
                var upload = new Dictionary<string, string>(unsafeHeaders)
                {
                    ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                    ["Content-Type"] = "application/octet-stream"
                };
                string create = RevisionUploadHttpTestSupport.CreatePath(test.Data.Project) +
                    RevisionUploadHttpTestSupport.CreateQuery("Ferrule service upload", "Exact bytes through the Ferrule service");
                var created = await ServerHostHttp.SendAsync(http, create, HttpMethod.Post, upload, RevisionIngestionTestSupport.Fixture("valid-basic"), budget: Native);
                Guid operation, revisionId, mappingId;
                using (var body = created.Json())
                {
                    ServerHostCheck.That(created.Status == 200 && body.RootElement.GetProperty("outcome").GetString() == "committedValid",
                        "Session native admission did not commit through the Ferrule service: " + created.Status.ToString(CultureInfo.InvariantCulture) + ".");
                    var receipt = body.RootElement.GetProperty("receipt");
                    operation = receipt.GetProperty("operationId").GetGuid();
                    revisionId = receipt.GetProperty("futureRevision").GetProperty("revisionId").GetGuid();
                    mappingId = receipt.GetProperty("futureRevision").GetProperty("mappingId").GetGuid();
                }
                ServerHostCheck.That(await test.Data.Case.ScalarAsync<string>(
                    "SELECT backend_build FROM braid.revision_ingestions WHERE operation_id=$1", operation) == identity,
                    "The admitted revision did not record the Ferrule service's build identity.");

                var invalid = await ServerHostHttp.SendAsync(http, create, HttpMethod.Post,
                    new Dictionary<string, string>(upload) { ["Idempotency-Key"] = Guid.NewGuid().ToString("D") },
                    RevisionIngestionTestSupport.Fixture("invalid-function"), budget: Native);
                using (var body = invalid.Json())
                    ServerHostCheck.That(invalid.Status == 200 && body.RootElement.GetProperty("outcome").GetString() == "committedDecodedInvalid",
                        "Session native admission did not retain the service's validation findings.");

                string revision = CatalogHttpTestSupport.ProjectPath(test.Data.Project) + "/mappings/" + mappingId.ToString("D") +
                    "/revisions/" + revisionId.ToString("D");
                foreach (var name in new[] { "projection", "validation" })
                {
                    var ensured = await ServerHostHttp.SendAsync(http, revision + "/reports/" + name + "/ensure", HttpMethod.Post, unsafeHeaders, budget: Native);
                    using var body = ensured.Json();
                    ServerHostCheck.That(ensured.Status == 200 && body.RootElement.GetProperty("state").GetString() == "available",
                        "Session report derivation did not complete through the Ferrule service: " + name + ".");
                }
                ServerHostCheck.That(await test.Data.Case.ScalarAsync<long>(
                    "SELECT count(*) FROM braid.revision_derivation_attempts WHERE result_usable AND provenance_confirmed AND provenance_backend_build=$1",
                    identity) == 2, "The retained reports did not record the Ferrule service's confirmed build identity.");

                await host.StopAsync();
                ServerHostCheck.That(await service.StopAsync() == 0, "The Ferrule service did not stop cleanly.");
                ServerHostCheck.That(!Directory.EnumerateFileSystemEntries(root).Any(), "The Ferrule service left a private directory behind.");
                await host.AssertPrivateOutputAsync([test.Files.Root, storage.RootPath, File.ReadAllText(client.PrivateKeyFile).Trim(),
                    test.Data.Case.IngestionConnectionString, test.Data.Case.DerivationConnectionString]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

    [ServiceHostFact]
    public async Task TheWorkerProcessExecutesPreviewRunsThroughTheFerruleService()
    {
        await using var test = await PreviewRunTestSupport.CreateAsync(postgres);
        using var storage = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        using (var physical = FileSystemV2PrivateObjectStore.Initialize(storage.Options, () => registry))
        using (var input = new MemoryStream(PreviewRunTestSupport.NativeBytes))
            Assert.Equal(PrivateObjectPublishResult.Created,
                await physical.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(test.Native), test.Revision, test.Native, input));
        var files = new ServerHostFiles();
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            var serviceCertificate = files.HistoricalPair("ferrule-service", expired: false);
            var client = files.HistoricalPair("ferrule-service-client", expired: false);
            var identity = PackagedFerruleService.Identity;
            await using var service = await PackagedFerruleService.StartAsync(
                File.ReadAllText(serviceCertificate.CertificateFile) + "\n" + File.ReadAllText(serviceCertificate.PrivateKeyFile),
                [Pin(client.CertificateFile)], root);
            using var submitter = Host.CreateDefaultBuilder()
                .ConfigureServices(services => services.AddWolverine(options =>
                    PreviewJobs.Configure(options, test.Case.PreviewWriterConnectionString, PreviewJobRole.Submitter)))
                .Build();
            await submitter.StartAsync();
            var previews = test.Service(store: new OutboxPreviewRunStore(test.Store, submitter.Services.GetRequiredService<IWolverineRuntime>()));
            var worker = PreviewWorkerProcess.Start(test, storage, new Dictionary<string, string?>
            {
                ["BRAID_FERRULE_RUNNER"] = "service",
                ["BRAID_FERRULE_WORKER_IMAGE"] = identity,
                ["BRAID_FERRULE_SERVICE_URL"] = "https://127.0.0.1:" + service.Port.ToString(CultureInfo.InvariantCulture),
                ["BRAID_FERRULE_SERVICE_CERTIFICATE_SHA256"] = Pin(serviceCertificate.CertificateFile),
                ["BRAID_FERRULE_SERVICE_CLIENT_CERTIFICATE_FILE"] = client.CertificateFile,
                ["BRAID_FERRULE_SERVICE_CLIENT_PRIVATE_KEY_FILE"] = client.PrivateKeyFile,
                // No Docker is available to this worker.
                ["BRAID_DOCKER_EXECUTABLE"] = "/nonexistent/docker"
            });
            try
            {
                var run = (await previews.SubmitAsync(test.Alice, test.Submission())).RunId!.Value;
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
                PreviewRunSnapshot? snapshot;
                while (true)
                {
                    snapshot = await previews.ReadAsync(test.Alice, test.Revision.OrganizationId, run);
                    if (snapshot!.State is PreviewRunState.Completed or PreviewRunState.Canceled) break;
                    Assert.False(worker.HasExited, worker.Diagnostics());
                    Assert.True(DateTime.UtcNow < deadline, worker.Diagnostics());
                    await Task.Delay(50);
                }
                Assert.True(snapshot.Outcome == PreviewRunOutcome.Succeeded, snapshot.Failure?.Message);
                Assert.Equal(["primary-output", "named-output:0"], snapshot.Outputs.Select(output => output.BoundaryId));
                Assert.Equal(identity, snapshot.Provenance!.BackendBuild);
                Assert.True(snapshot.Provenance.Confirmed);
            }
            finally
            {
                worker.Kill();
                await submitter.StopAsync();
            }
            Assert.Equal(0, await service.StopAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            files.Dispose();
        }
    }

    private static string Pin(string certificateFile)
    {
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificateFile));
        return InspectionTransport.CertificateDigest(certificate);
    }
}
