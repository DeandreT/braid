using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;
using Xunit.Abstractions;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HostedBuildRunnerLiveTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [ServiceCompileFact]
    public async Task The_ordinary_worker_completes_approved_builds_through_the_pinned_service_after_restart()
    {
        Assert.True(PostgresFixture.IsConfigured);
        await using var test = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        var root = FerruleServiceTestSupport.TemporaryRoot();
        using var server = InspectionTransportTests.TransportCase.Certificate("CN=Hosted compiler");
        using var client = InspectionTransportTests.TransportCase.Certificate("CN=Hosted compiler caller");
        try
        {
            await using var direct = new ProcessCompileRunner(FerruleServiceTestSupport.CompileOptions(root));
            var native = direct.BuildIdentity;
            var pin = direct.ToolchainIdentity;
            var identity = direct.CompilationBuildIdentity;
            await using var service = await PackagedFerruleService.StartAsync(PackagedFerruleService.Bundle(server),
                [InspectionTransportTests.TransportCase.Pin(client)], root, extra: PackagedFerruleService.Compilation);
            var endpoint = new Uri("https://127.0.0.1:" + service.Port);
            PinnedServiceConnector Connector() => new(new(endpoint, InspectionTransportTests.TransportCase.Pin(server), client));
            await using var inspect = new FerruleServiceRunner(new(Connector(), FerruleServiceProtocol.InspectionProfile, native, pin));
            await using var preview = new FerruleServiceRunner(new(Connector(), FerruleServiceProtocol.PreviewProfile, native, pin));

            var mapping = JsonNode.Parse(RevisionIngestionTestSupport.Fixture("valid-basic"))!;
            mapping["source_options"] = new JsonObject { ["json_document"] = true };
            mapping["target_options"] = new JsonObject { ["json_document"] = true };
            var ingestion = new RevisionIngestionService(test.Metadata, test.Native.Metadata,
                new PostgresProjectReader(test.Native.ReaderDatabase), new(test.Storage, FileSystemStorageOptions.NativeHardMaximum),
                new IngestionBackendRegistry([FerruleIngestionAdmissionBackend.CreateRegistration(inspect)]));
            var created = await ingestion.CreateAsync(test.Actor, test.Project, Guid.NewGuid(), RevisionIngestionTestSupport.Request(),
                new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(mapping)), default);
            Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
            var receipt = await test.ReceiptAsync(created);
            var revision = receipt.Facts.FutureRevision;
            Assert.Equal(identity, receipt.Facts.BackendProfile.BackendBuild);
            var registry = FerruleRevisionReportBackendFactory.CreateRegistry(FerruleRevisionReportBackendFactory.CreateProfile(identity),
                new(), () => inspect);
            var report = await test.NewReports(registry: registry).EnsureAsync(test.Actor, revision, RevisionReportOperation.Projection);
            _ = RevisionReportTestSupport.Available(report);

            var runtime = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, identity,
                FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
            var input = "{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}"u8.ToArray();
            var expected = "{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n"u8.ToArray();
            var value = new RegressionCaseVersion(Guid.NewGuid(), 1, test.Actor.PrincipalId, "Service build evidence", revision,
                receipt.Facts.NativeObject.Digest, runtime, new(PreviewTargetSelection.All, "2026-10-22T12:00:00Z", PreviewLimits.Default,
                    new(null, input), [], []), RegressionComparisonService.ExactBytesPolicy,
                RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, expected)]));
            await using var cases = new PostgresRegressionCaseStore(test.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
            await using var caseReader = new PostgresRegressionCaseStore(test.Case.RegressionConnectionString("weft_case_reader"), RegressionCaseDatabaseProfile.Reader);
            await cases.SaveAsync(test.Actor, value);
            await using var runs = new PostgresRegressionRunStore(test.Case.RegressionConnectionString("weft_run_writer"), true);
            var run = Guid.NewGuid();
            Assert.Equal(RegressionRunSubmitOutcome.Created, await runs.SubmitAsync(new(test.Actor, run, new RegressionCaseEnvelopeCodec().Encode(value).Reference)));
            var executor = new RegressionCaseExecutionService(caseReader, new PostgresProjectReader(test.Native.ReaderDatabase),
                test.ReportReader, test.Physical, new FerrulePreviewBackend(preview), runtime);
            Assert.Equal(RegressionRunDispatchOutcome.Completed, await new RegressionRunExecutor(runs, executor).ExecuteAsync(revision.OrganizationId, run, Guid.NewGuid()));

            await using var controls = new PostgresBuildStore(test.Case.RegressionConnectionString("weft_build_writer"), true);
            var environment = new BuildEnvironmentReference(new(revision.OrganizationId, revision.ProjectId), Guid.NewGuid(), 1);
            Assert.Equal(BuildControlOutcome.Created, await controls.SaveEnvironmentAsync(test.Actor, new(environment, FerruleBuildCompiler.Profile, true)));
            var cert = test.Root.NewDirectory("service-client");
            var certFile = Path.Combine(cert, "client.pem"); var keyFile = Path.Combine(cert, "client.key");
            File.WriteAllText(certFile, client.ExportCertificatePem());
            using (var key = client.GetRSAPrivateKey()!) File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(certFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var selected = new Dictionary<string, string>
            {
                [FerruleRunnerSelection.Setting] = "service",
                [FerruleRunnerSelection.CompileToolchainSetting] = pin,
                [FerruleServiceEndpoint.Url] = endpoint.ToString().TrimEnd('/'),
                [FerruleServiceEndpoint.CertificatePin] = InspectionTransportTests.TransportCase.Pin(server),
                [FerruleServiceEndpoint.ClientCertificateFile] = certFile,
                [FerruleServiceEndpoint.ClientPrivateKeyFile] = keyFile,
                // This worker cannot fall back to the local Docker compiler.
                ["WEFT_DOCKER_EXECUTABLE"] = "/not-a-docker-executable"
            };
            await using var api = await BuildApiLiveHost.StartAsync(test, native, runnerSettings: selected);
            await using var history = new PostgresBuildRunStore(test.Case.RegressionConnectionString("weft_build_run_reader"), BuildRunDatabaseProfile.Reader);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var intent = new BuildIntent(Guid.NewGuid(), revision, value.NativeDigest,
                    new("ferrule", FerruleInspectionBackend.EngineRevision, identity, FerruleBuildCompiler.Adapter), FerruleBuildCompiler.Profile,
                    [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")], environment, Guid.NewGuid(), run);
                await api.ApproveAsync(intent);
                await api.SubmitAsync(intent);
                await using var worker = BuildDispatchLiveTests.Child.Start(test, native, selected);
                var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
                BuildRunSnapshot? completed;
                while ((completed = await history.ReadAsync(test.Actor, revision.OrganizationId, intent.BuildId))?.State is BuildRunState.Queued or BuildRunState.Running)
                {
                    Assert.False(worker.HasExited);
                    Assert.True(DateTimeOffset.UtcNow < deadline, "The service-backed build did not complete.");
                    await Task.Delay(100);
                }
                Assert.NotNull(completed);
                Assert.Equal(BuildRunState.Succeeded, completed.State);
                Assert.Equal(identity, completed.Artifact!.Intent.Backend.EngineBuild);
                Assert.True(completed.Artifact.ByteCount > 0);
                Assert.Equal(1, completed.Attempt);
                output.WriteLine("Service-backed build completed after worker start " + iteration);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
