using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Cli;
using Weft.Contracts.Catalog;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using static Weft.Tests.FerruleServiceTestSupport;

namespace Weft.Tests.Cli;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CliEdiSessionHostTests(PostgresFixture postgres)
{
    [DocumentPreviewFact]
    public async Task SignedInCliExecutesAndDownloadsBothEdiFamiliesThroughNativeWorkers()
    {
        var root = TemporaryRoot();
        try
        {
            await using var inspection = new ProcessWorkerRunner(Options(root));
            await using var preview = new ProcessWorkerRunner(Options(root, DockerWorkerProfile.Preview));
            await ExerciseAsync(inspection, preview);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [DocumentPreviewFact]
    public async Task SignedInCliExecutesAndDownloadsBothEdiFamiliesThroughThePinnedService()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = await FerruleServiceEndToEndTests.ServiceHost.StartAsync(root);
            var connector = new PinnedServiceConnector(new(service.Endpoint, service.ServerPin, service.ClientCertificate));
            await using var inspection = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.InspectionProfile, service.Identity));
            await using var preview = new FerruleServiceRunner(new(connector, FerruleServiceProtocol.PreviewProfile, service.Identity));
            await ExerciseAsync(inspection, preview);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task ExerciseAsync(IInspectionWorkerRunner inspection, IInspectionWorkerRunner preview)
    {
        using var root = new PrivateStorageTestRoot();
        await using var objects = root.Initialize();
        await AuthHttpFixture.RunAsync(postgres, async test =>
        {
            var ports = test.First.Services.GetRequiredService<PreviewPorts>();
            var runs = test.First.Services.GetRequiredService<PreviewRunService>();
            var ingestion = new RevisionIngestionService(test.Data.Ingestion, test.Data.Ingestion,
                new PostgresProjectReader(ports.Runtime), new(objects, PreviewPolicy.MaximumNativeBytes),
                new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(inspection)]));
            var executor = new PreviewRunExecutor(ports.Store, objects, new FerrulePreviewBackend(preview), TimeProvider.System);
            var records = new ConcurrentQueue<AuthHttpRecord>();
            using var cli = CliSessionHostTests.Harness(test, records);
            await cli.ConfigureAsync();
            await cli.LoginAsync();
            string? lastRun = null;
            foreach (var family in new[] { "x12", "edifact", "mixed-x12" })
            {
                var mixed = family == "mixed-x12";
                var source = mixed ? JsonNode.Parse(DocumentPreviewTestData.InterchangeNative())! : null;
                if (source is JsonObject objectSource) objectSource.Remove("extra_targets");
                var nativeBytes = mixed ? Encoding.UTF8.GetBytes(source!.ToJsonString()) : DocumentPreviewTestData.Native(family);
                var inputBytes = mixed ? Encoding.UTF8.GetBytes(DocumentPreviewTestData.MixedX12()) : DocumentPreviewTestData.Input(family);
                using var native = new MemoryStream(nativeBytes, writable: false);
                var created = await ingestion.CreateAsync(test.Data.Actor, test.Data.Project, Guid.NewGuid(),
                    new(new("CLI " + family, "Self-authored EDI round trip"), "ferrule", "mapping.native.json"), native);
                Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
                var revision = created.Receipt!.FutureRevision;
                var key = Guid.NewGuid().ToString("D");
                var command = new[] { "previews", "create", "--project", revision.ProjectId.ToString("D"),
                    "--mapping", revision.MappingId.ToString("D"), "--revision", revision.RevisionId.ToString("D"),
                    "--input", "-", "--request-id", key, "--document", "--json" };
                var before = await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs");
                var submitted = Read<CatalogPreviewSubmitResultV1>(await cli.RunWithInputAsync(inputBytes, command));
                var runId = Guid.Parse(submitted.Run.RunId);
                Assert.Equal("created", submitted.Outcome);
                Assert.Equal(submitted.Run.RunId, Read<CatalogPreviewSubmitResultV1>(
                    await cli.RunWithInputAsync(inputBytes, command)).Run.RunId);
                Assert.Equal(before + 1, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));
                Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(revision.OrganizationId, runId, Guid.NewGuid()));
                var completed = Read<CatalogPreviewRunV1>(await cli.RunAsync("previews", "get", submitted.Run.RunId, "--json"));
                Assert.Equal("succeeded", completed.Outcome);
                Assert.True(completed.Provenance!.Confirmed);
                Assert.Equal(DocumentPreviewPolicy.Profile, completed.Provenance.Profile);
                var media = mixed ? "application/json" : family == "x12" ? "application/edi-x12" : "application/edifact";
                Assert.Equal(media, Assert.Single(completed.Outputs).MediaType);
                var stored = (await runs.ReadOutputAsync(test.Data.Actor, revision.OrganizationId, runId, 0))!;
                var downloaded = await cli.RunAsync("previews", "download", submitted.Run.RunId, "--output", "-");
                Assert.Equal(CliExitCode.Success, downloaded.Code);
                Assert.Equal(stored.Content, downloaded.Binary);
                if (mixed) MixedX12ConvergenceTests.AssertRecords(downloaded.Binary);
                else Assert.True(EdiEnvelopeValidation.IsValid(downloaded.Binary, media));
                Assert.Contains("WEFT-330", Encoding.UTF8.GetString(downloaded.Binary), StringComparison.Ordinal);
                var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(inputBytes).Replace("WEFT-330", "WEFT-CHANGED", StringComparison.Ordinal));
                Assert.Equal(CliExitCode.Conflict, (await cli.RunWithInputAsync(changed, command)).Code);
                Assert.Equal(before + 1, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.preview_runs"));

                // Omitting --document must not grant the legacy JSON profile the new codecs.
                var legacy = command.Where(value => value != "--document").ToArray();
                legacy[Array.IndexOf(legacy, key)] = Guid.NewGuid().ToString("D");
                var refused = Read<CatalogPreviewSubmitResultV1>(await cli.RunWithInputAsync(inputBytes, legacy));
                Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(revision.OrganizationId, Guid.Parse(refused.Run.RunId), Guid.NewGuid()));
                var legacyResult = Read<CatalogPreviewRunV1>(await cli.RunAsync("previews", "get", refused.Run.RunId, "--json"));
                Assert.Equal("unsupportedCapability", legacyResult.Outcome);
                Assert.Empty(legacyResult.Outputs);
                if (mixed)
                {
                    var oversized = Encoding.UTF8.GetBytes(DocumentPreviewTestData.MixedX12().Replace("SECOND", new string('X', 20_000), StringComparison.Ordinal));
                    var bounded = command.ToArray(); bounded[Array.IndexOf(bounded, key)] = Guid.NewGuid().ToString("D");
                    var accepted = Read<CatalogPreviewSubmitResultV1>(await cli.RunWithInputAsync(oversized, bounded));
                    Assert.Equal(PreviewExecutionOutcome.Completed, await executor.ExecuteAsync(revision.OrganizationId, Guid.Parse(accepted.Run.RunId), Guid.NewGuid()));
                    var limit = Read<CatalogPreviewRunV1>(await cli.RunAsync("previews", "get", accepted.Run.RunId, "--json"));
                    Assert.Equal("resourceLimit", limit.Outcome);
                    Assert.Empty(limit.Outputs);
                }
                lastRun = submitted.Run.RunId;
            }
            await test.Data.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", test.Data.Actor.PrincipalId.Value);
            var denied = await cli.RunAsync("previews", "download", lastRun!, "--output", "-");
            Assert.Equal(CliExitCode.NotFound, denied.Code);
            Assert.Empty(denied.Binary);
            Assert.All(records, record => Assert.False(record.Unexpected));
            Assert.True(records.Sum(record => record.Audits) >= 4);
            Assert.Equal(1, test.Issuer.PublicDeviceGrants);
        }, bearerBurst: 100, configureCatalogServices: (test, services) =>
        {
            services.AddWeftPreviews(new(true));
            services.AddSingleton(_ => new PreviewPorts(test.Data.Case));
            services.AddSingleton(provider =>
            {
                var ports = provider.GetRequiredService<PreviewPorts>();
                return new PreviewRunService(ports.Store, ports.Reader, TimeProvider.System);
            });
        });
    }

    private static T Read<T>(CliResult result)
    {
        AuthHttpFixture.Check(result.Exit == 0, $"The EDI CLI expected {typeof(T).Name} but returned {result.Code}.");
        return JsonSerializer.Deserialize<T>(result.Out, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class PreviewPorts : IAsyncDisposable
    {
        private readonly PostgresPreviewDatabase writer;
        private readonly PostgresPreviewDatabase reader;
        internal PostgresDatabase Runtime { get; }
        internal PostgresPreviewRunStore Store { get; }
        internal PostgresPreviewRunReader Reader { get; }
        internal PreviewPorts(PostgresCase test)
        {
            Runtime = new(test.RuntimeConnectionString);
            writer = new(test.PreviewWriterConnectionString, PreviewDatabaseProfile.Writer);
            reader = new(test.PreviewReaderConnectionString, PreviewDatabaseProfile.Reader);
            Store = new(writer);
            Reader = new(reader);
        }
        public async ValueTask DisposeAsync()
        {
            await reader.DisposeAsync();
            await writer.DisposeAsync();
            await Runtime.DisposeAsync();
        }
    }
}
