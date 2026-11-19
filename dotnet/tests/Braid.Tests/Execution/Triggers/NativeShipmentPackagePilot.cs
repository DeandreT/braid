using System.Text;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Xunit.Abstractions;

namespace Braid.Tests;

public sealed class NativeShipmentPilotFactAttribute : FactAttribute
{
    public NativeShipmentPilotFactAttribute(bool transport = false)
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || !FerruleServiceTestSupport.CompileConfigured || Environment.GetEnvironmentVariable("BRAID_NATIVE_SHIPMENT_PILOT") != "1")
            Skip = "Requires the isolated Ferrule compiler/runtime and disposable PostgreSQL; opt in with BRAID_NATIVE_SHIPMENT_PILOT=1.";
        else if (transport && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAID_SFTP_TEST_PYTHON")))
            Skip = "Requires the independent local SFTP/AS2 receiver.";
    }
}

[Collection(PostgresCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed partial class NativeShipmentPackagePilot(PostgresFixture fixture, ITestOutputHelper output)
{
    [NativeShipmentPilotFact]
    public Task Real_package_must_qualify_before_activation_and_rollback_loads_original_artifact() => Rehearse(false);

    [NativeShipmentPilotFact(transport: true)]
    public Task Sftp_orders_execute_the_pinned_DLL_and_a_lost_MDN_does_not_block_a_second_target_or_rollback() => Rehearse(true);

    [NativeShipmentPilotFact]
    public Task Timeout_and_child_memory_limit_leave_sibling_orders_within_local_budget() => Rehearse(false, boundedFaults: true);

    [NativeShipmentPilotFact]
    public Task Live_document_endpoint_returns_the_released_945_and_replays_its_original_bytes() => Rehearse(false, endpoint: true);

    private static byte[] XmlInput => BuildPersistenceTests.XmlInput;

    [NativeShipmentPilotFact]
    public Task Qualified_xml_release_returns_exact_bytes_over_authenticated_loopback() => Rehearse(false, endpoint: true, xml: true);

    [NativeShipmentPilotFact]
    public Task Qualified_contrl_release_returns_exact_bytes_over_authenticated_loopback() => Rehearse(false, endpoint: true, contrl: true);

    [NativeShipmentPilotFact]
    public Task Qualified_flextext_byte_release() => Rehearse(false, endpoint: true, byteProfile: BuildWorkerProtocol.FlexProfile);
    [NativeShipmentPilotFact]
    public Task Qualified_protobuf_byte_release() => Rehearse(false, endpoint: true, byteProfile: BuildWorkerProtocol.ProtoProfile);
    [NativeShipmentPilotFact]
    public Task Qualified_portable_workbook_release() => Rehearse(false, endpoint: true, byteProfile: BuildWorkerProtocol.WorkbookProfile);
    [NativeShipmentPilotFact]
    public Task Qualified_xbrl_byte_release() => Rehearse(false, endpoint: true, byteProfile: BuildWorkerProtocol.XbrlProfile);

    private async Task Rehearse(bool transport, bool boundedFaults = false, bool endpoint = false, bool xml = false, bool contrl = false, string? byteProfile = null)
    {
        var root = FerruleServiceTestSupport.TemporaryRoot();
        try
        {
            await using var generator = new ProcessCompileRunner(FerruleServiceTestSupport.CompileOptions(root));
            await using var child = new ProcessCompiledRuntimeRunner(FerruleCompiledServiceTests.Options(root));
            var probe = await child.ProbeResultAsync(default);
            Assert.True(probe.Failure == WorkerProcessFailure.None, $"Compiled-runtime isolation admission: {probe.Failure}/{probe.AdmissionFailure}");
            Assert.True(await generator.ProbeAsync(default), "Compiler isolation admission failed before any mapping input.");
            await using var interpreter = new ProcessWorkerRunner(FerruleServiceTestSupport.Options(root, DockerWorkerProfile.Preview));
            var family = byteProfile is null ? null : BuildWorkerProtocol.NativeFormat(byteProfile)!;
            BuildPersistenceTests.ByteCase? bytesCase = null;
            var native = family is not null ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", family.Key == "xlsx" ? "xlsx-portable.native.json" : family.Key == "xbrl" ? "xbrl-instance.native.json" : family.Key + ".native.json")) : xml ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "xml-closed.native.json")) : contrl ? DocumentPreviewTestData.Native("edifact", DocumentPreviewTestData.Contrl) : NativeMapping();
            byte[]? xmlExpected = null;
            var preview = family is not null ? new FerrulePreviewBackend(interpreter, family.Execution, nativeFormats: [family]) : xml ? new FerrulePreviewBackend(interpreter, PreviewExecutionProfile.XmlDocument, xmlEnabled: true) : new FerrulePreviewBackend(interpreter);
            if (family is not null)
            {
                var artifact = NativeArtifact.Admit(FerruleServiceTestSupport.Scope, native, PreviewPolicy.MaximumNativeBytes);
                var settings = NativeDocumentNativeSettings.Read(artifact, family);
                var input = new PreviewInputDocument(null, family.Key switch { "xlsx" => Convert.FromBase64String(System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"FerruleFixtures","xlsx-portable-bytes.json")))!), "flextext" => "hello"u8.ToArray(), "protobuf" => [10,5,104,101,108,108,111], _ => DocumentPreviewTestData.XbrlInstance("5") }, documentSettings: settings.Input);
                var sample = await preview.PreviewAsync(new(artifact, PreviewTargetSelection.All, input, [], [], "2026-10-24T12:00:00Z", PreviewLimits.Default) { Profile = family.Execution });
                Assert.Equal(MappingPreviewStatus.Succeeded, sample.Status);
                bytesCase = new(byteProfile!, family.Key == "flextext" ? "flexText" : family.Key, family, input, settings.Output, Assert.Single(sample.Artifacts).CopyBytes());
                if (family == NativeDocumentFormat.Xlsx)
                {
                    bytesCase = bytesCase with { ReplaySaved = async saved => {
                        var replay = saved.Replay;
                        var source = NativeArtifact.Admit(saved.Revision, native, PreviewPolicy.MaximumNativeBytes);
                        Assert.Equal(saved.NativeDigest, source.Digest);
                        var actual = await preview.PreviewAsync(new(source, replay.Target, replay.Primary, replay.NamedInputs, replay.Parameters, replay.CurrentDateTime, replay.Limits) { Profile = family.Execution });
                        Assert.Equal(MappingPreviewStatus.Succeeded, actual.Status);
                        var output = Assert.Single(actual.Artifacts);
                        return RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, output.CopyBytes(), mediaType: output.MediaType, documentSettings: output.DocumentSettings)]);
                    } };
                }
            }
            if (xml)
            {
                var sample = await preview.PreviewAsync(new(NativeArtifact.Admit(FerruleServiceTestSupport.Scope, native, PreviewPolicy.MaximumNativeBytes), PreviewTargetSelection.All,
                    new(null, XmlInput, xmlMetadata: XmlDocumentMetadata.WithoutHints), [], [], "2026-10-24T12:00:00Z", PreviewLimits.Default) { Profile = PreviewExecutionProfile.XmlDocument });
                Assert.Equal(MappingPreviewStatus.Succeeded, sample.Status);
                xmlExpected = Assert.Single(sample.Artifacts).CopyBytes();
            }
            var expected = bytesCase?.Expected ?? xmlExpected ?? (contrl ? DocumentPreviewTestData.ContrlExpected : Shipment945Fixture.NativeExpected);
            var backend = new BuildBackendIdentity("ferrule", BuildWorkerProtocol.Engine, generator.CompilationBuildIdentity!, family?.AdapterBuild ?? (xml ? FerrulePreviewBackend.XmlAdapterBuild : FerrulePreviewBackend.DocumentAdapterBuild));
            var runtime = new FerruleCompiledBuildRuntime(child, backend);
            await using var test = await LocalReleasePersistenceTests.Context.CreateAsync(fixture, shipment: family is null && !xml && !contrl, xmlExpected: xmlExpected, nativeBytes: native, actualBackend: backend, runtimeImage: runtime.RuntimeImage, contrl: contrl, bytesCase: bytesCase);
            Assert.True(await test.Case.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM braid.schema_migrations WHERE version=66)"));
            var actor = test.Actor; var target = test.Target.Reference; var original = test.Control.Intent; var replay = test.Control.Runs.Value.Replay;
            var interpreted = await preview.PreviewAsync(new(NativeArtifact.Admit(original.Revision, native, PreviewPolicy.MaximumNativeBytes), replay.Target,
                replay.Primary, [], [], replay.CurrentDateTime, replay.Limits) { Profile = family?.Execution ?? (xml ? PreviewExecutionProfile.XmlDocument : PreviewExecutionProfile.Document) });
            Assert.Equal(MappingPreviewStatus.Succeeded, interpreted.Status);
            Assert.Equal(expected, Assert.Single(interpreted.Artifacts).CopyBytes());
            var source = new FerruleServiceCompileTests.NativeSource(actor, test.Control.Native, native);
            var compiler = new FerruleBuildCompiler(generator, new(actor, source, source), xmlEnabled: xml, nativeFormats: family is null ? [] : [family]);
            await using var runs = new PostgresBuildRunStore(test.Case.RegressionConnectionString("braid_build_run_reader"), BuildRunDatabaseProfile.Reader);
            await using var builds = new PostgresBuildStore(test.Case.RegressionConnectionString("braid_build_reader"), false);
            await using var access = new PostgresDatabase(test.Case.RuntimeConnectionString);
            await using var reports = new PostgresRevisionReportDatabase(test.Case.ReportConnectionString);
            await using var evidence = new PostgresRegressionRunStore(test.Case.RegressionConnectionString("braid_run_reader"), false);
            await using var cases = new PostgresRegressionCaseStore(test.Case.RegressionConnectionString("braid_case_reader"), RegressionCaseDatabaseProfile.Reader);
            var projects = new PostgresProjectReader(access);
            var execution = new CompiledBuildExecution(runs, new BuildControlService(builds, projects, new PostgresRevisionReportMetadataReader(reports), new RegressionRunReadService(evidence, cases)),
                builds, new PostgresBuildObjectStore(builds, new LocalReleasePersistenceTests.NoNative(), actor), runtime);
            var qualifications = new BuildQualificationService(test.Qualifications, runs, evidence, cases, execution, projects);
            var host = new LocalReleaseInvocation(test.Store, execution);
            BuildIntent Intent() => new(Guid.NewGuid(), original.Revision, original.NativeDigest, backend, original.Profile, original.Boundaries, original.Environment, Guid.NewGuid(), original.RegressionEvidenceId);
            async Task<BuildQualificationRequestResult> Stage(BuildIntent intent, bool invalid)
            {
                var capability = await compiler.InspectAsync(intent);
                Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
                var bytes = (await compiler.BuildAsync(intent, new(native, native.Length))).Copy();
                if (invalid) bytes[0] = 0; // valid manifest over malformed assembly: isolated execution, not the package hash, must refuse it.
                var run = await test.PublishOnly(intent, bytes);
                if (!invalid)
                {
                    var direct = await runtime.ExecuteAsync(run.Artifact!, new(bytes, intent.Profile.MaximumArtifactBytes), replay);
                    Assert.Equal(CompiledBuildOutcome.Completed, direct.Outcome);
                    Assert.Equal(expected, Assert.Single(direct.Outputs!.Outputs).CopyBytes());
                    var observed = (await evidence.ReadAsync(actor, intent.Revision.OrganizationId, intent.RegressionEvidenceId))!;
                    _ = BuildQualification.Create(Guid.NewGuid(), actor.PrincipalId, run, observed, test.Control.Runs.Value, runtime.RuntimeImage, direct);
                }
                var saved = (await builds.ReadPublishedPackageAsync(actor, target.Mapping.OrganizationId, intent.BuildId))!;
                using var wire = new MemoryStream(); await new MappingPackage(saved.Publication.Manifest, saved.Bytes).WriteAsync(wire, default); wire.Position = 0;
                var decoded = await MappingPackage.ReadAsync(wire, PostgresBuildStore.MaximumArtifactBytes, default);
                var id = Guid.NewGuid();
                var result = await qualifications.PutAsync(actor, target.Mapping, id, intent.BuildId, package: decoded);
                if (invalid) Assert.Null(await test.Qualifications.ReadAsync(actor, target.Mapping, id));
                return result;
            }
            async Task Invoke(ReleaseIntent release)
            {
                Assert.Equal(release.ReleaseId, (await test.Store.ReadTargetAsync(actor, target))!.ActiveReleaseId);
                var result = await host.ExecuteAsync(actor, target, replay);
                Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
                Assert.Equal(expected, Assert.Single(result.Outputs!.Outputs).CopyBytes());
            }
            var qa = await Stage(original, false); Assert.Equal(BuildQualificationRequestOutcome.Created, qa.Outcome);
            var a = await test.Request(qa.Qualification!, null); Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(a)); await Invoke(a);
            if (endpoint)
            {
                var caller = new MachinePrincipal(MachinePrincipal.Entra, EntraProtocolFixturePolicy.Tenant, Guid.NewGuid());
                await using var grants = new PostgresInvocationGrantStore(test.Case.RegressionConnectionString("braid_invocation_grant_writer"));
                Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(actor, new(Guid.NewGuid(), target, caller)));
                await using var ledger = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
                var service = new MachineInvocationService(ledger, test.Store, principal => new(test.Store,
                    new(runs, new BuildControlService(builds, projects, new PostgresRevisionReportMetadataReader(reports), new RegressionRunReadService(evidence, cases)),
                        builds, new PostgresBuildObjectStore(builds, new LocalReleasePersistenceTests.NoNative(), principal), runtime)),
                    TimeProvider.System, family is not null || xml || contrl ? PreviewLimits.Default with { MaximumOutputBytes = expected.Length } : Braid.MachineEndpoint.MappingEndpointResources.Limits);
                await InvokeHttp(target, a.ReleaseId, caller, service, () => child.Available, xmlExpected, contrl, bytesCase);
                return;
            }
            var b = a;
            if (!boundedFaults)
            {
                Assert.Equal(BuildQualificationRequestOutcome.NotQualified, (await Stage(Intent(), true)).Outcome); await Invoke(a);
                var qb = await Stage(Intent(), false); Assert.Equal(BuildQualificationRequestOutcome.Created, qb.Outcome);
                b = await test.Request(qb.Qualification!, a.ReleaseId); Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(b)); await Invoke(b);
            }
            async Task<ReleaseIntent> Rollback()
            {
                var rollback = await test.Request(qa.Qualification!, b.ReleaseId, ReleaseAction.Rollback, a.ReleaseId);
                Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Apply(rollback)); await Invoke(rollback);
                return rollback;
            }
            if (!transport && !boundedFaults) await Rollback();
            else
            {
                var peer = new LocalReleaseTarget(new(target.Mapping, target.Environment, Guid.NewGuid()), original.Profile, runtime.RuntimeImage);
                Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RegisterAsync(actor, peer));
                var peerRelease = new ReleaseIntent(Guid.NewGuid(), actor.PrincipalId, peer.Reference, qa.Qualification!, ReleaseAction.Activate, null);
                Assert.Equal(ReleaseWriteOutcome.Created, await test.Store.RequestAsync(actor, peerRelease));
                Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Store.ApplyAsync(actor, peer.Reference, peerRelease.ReleaseId));
                var callerA = new MachinePrincipal(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid());
                var callerB = new MachinePrincipal(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid());
                var appA = Guid.NewGuid(); var appB = Guid.NewGuid();
                await using var grants = new PostgresInvocationGrantStore(test.Case.RegressionConnectionString("braid_invocation_grant_writer"));
                Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(actor, new(Guid.NewGuid(), target, callerA)));
                Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(actor, new(Guid.NewGuid(), peer.Reference, callerB)));
                if (boundedFaults)
                {
                    await using var ledger = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
                    MachineInvocationService Service(IInspectionWorkerRunner worker) => new(ledger, test.Store, principal => new(test.Store,
                        new(runs, new BuildControlService(builds, projects, new PostgresRevisionReportMetadataReader(reports), new RegressionRunReadService(evidence, cases)),
                            builds, new PostgresBuildObjectStore(builds, new LocalReleasePersistenceTests.NoNative(), principal), new FerruleCompiledBuildRuntime(worker, backend))),
                        TimeProvider.System, Braid.MachineEndpoint.MappingEndpointResources.Limits);
                    foreach (var memory in new[] { false, true })
                        await BoundedOrders(root, memory, Service, b, peerRelease, new(callerA, appA, DateTimeOffset.UtcNow.AddMinutes(5)), new(callerB, appB, DateTimeOffset.UtcNow.AddMinutes(5)));
                    return;
                }
                var loopback = new SftpAs2RouteTests(fixture, output);
                Assert.NotEqual(a.Qualification.Artifact.Intent.BuildId, b.Qualification.Artifact.Intent.BuildId);
                await loopback.RunNativeRoute(test, callerA, appA, b, runtime, async unknown =>
                {
                    await loopback.RunNativeRoute(test, callerB, appB, peerRelease, runtime);
                    var rollback = await Rollback();
                    await loopback.RunNativeRoute(test, callerA, appA, rollback, runtime);
                    Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE delivery_id=$1 AND target_id=$2 AND phase='unknown' AND sends=1", unknown, target.TargetId));
                });
                Assert.Equal(3L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE phase='accepted' AND sends=1"));
                Assert.Equal(3L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.as2_send_fences"));
            }
            output.WriteLine("Interpreted and sandboxed DLL outputs equal independent 945 baseline; malformed DLL refused; A/B/rollback verified.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] NativeMapping()
    {
        static JsonObject Scalar(string n, string type = "string") => new() { ["name"] = n, ["kind"] = new JsonObject { ["kind"] = "scalar", ["ty"] = type } };
        static JsonObject Group(string n, JsonArray fields) => new() { ["name"] = n, ["kind"] = new JsonObject { ["kind"] = "group", ["children"] = fields } };
        var nodes = new JsonObject(); var scopes = new JsonArray(); var segments = new JsonArray(); var next = 0;
        foreach (var pattern in new[] { "ISA*00*          *00*          *ZZ*BRAIDWAREHOUSE *ZZ*BRAIDCUSTOMER  *261118*1200*U*00401*000000458*0*T*:",
            "GS*SW*BRAIDWAREHOUSE*BRAIDCUSTOMER*20261118*1200*458*X*004010", "ST*945*0458", "W06*F*$order*$shipDate*$shipment", "LX*1",
            "W12*CC*$quantity*$quantity*0*EA**VN*$sku", "W03*$quantity", "SE*6*0458", "GE*1*458", "IEA*1*000000458" })
        {
            var parts = pattern.Split('*'); var fields = new JsonArray(); var bindings = new JsonArray();
            for (var i = 1; i < parts.Length; i++)
            {
                var name = "e" + i; var value = parts[i]; var field = Scalar(name, value == "$quantity" ? "int" : "string");
                if ((parts[0] == "ISA" && i == 12) || (parts[0] == "GS" && i == 8)) field["fixed"] = value;
                fields.Add(field);
                nodes[(++next).ToString(System.Globalization.CultureInfo.InvariantCulture)] = value.StartsWith('$') ? new JsonObject { ["kind"] = "source_field", ["path"] = new JsonArray(value[1..]) } : new JsonObject { ["kind"] = "const", ["value"] = value };
                bindings.Add(new JsonObject { ["target_field"] = name, ["node"] = next });
            }
            segments.Add(Group(parts[0], fields)); scopes.Add(new JsonObject { ["target_field"] = parts[0], ["bindings"] = bindings, ["children"] = new JsonArray() });
        }
        return Encoding.UTF8.GetBytes(new JsonObject { ["source"] = Group("Shipment", new JsonArray(Scalar("order"), Scalar("shipDate"), Scalar("shipment"), Scalar("quantity", "int"), Scalar("sku"))),
            ["target"] = Group("Advice", segments), ["source_options"] = new JsonObject { ["json_document"] = true }, ["target_options"] = new JsonObject { ["edi_kind"] = "x12" },
            ["graph"] = new JsonObject { ["nodes"] = nodes }, ["root"] = new JsonObject { ["target_field"] = "", ["bindings"] = new JsonArray(), ["children"] = scopes } }.ToJsonString());
    }
}
