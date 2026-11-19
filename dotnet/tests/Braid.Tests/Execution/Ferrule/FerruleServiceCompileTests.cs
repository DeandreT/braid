using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using static Braid.Tests.FerruleServiceTestSupport;

namespace Braid.Tests;

/// <summary>
/// The Ferrule service's compile runner against a real worker, the real compile-sandbox launcher and the pinned
/// SDK. The launcher's Rust qualification proves each control inside the compiler; these tests prove that the
/// runner verifies them before the compiler starts, produces the Docker compiler's exact replies, and settles every
/// child it starts at the deadline, on cancellation and on stop.
/// </summary>
[Collection(FerruleServiceProcessCollection.Name)]
public sealed class FerruleServiceCompileTests
{
    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Structured_xml_public_compiler_and_child_preserve_bytes_and_classify_input()
    {
        var root = TemporaryRoot();
        try
        {
            var mapping = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "xml-closed.native.json")))!;
            mapping["extra_targets"] = new JsonArray(new JsonObject { ["name"] = "copy", ["schema"] = mapping["target"]!.DeepClone(),
                ["root"] = mapping["root"]!.DeepClone(), ["options"] = mapping["target_options"]!.DeepClone() });
            var bytes = Encoding.UTF8.GetBytes(mapping.ToJsonString());
            await using var generator = new ProcessCompileRunner(CompileOptions(root));
            var actor = new ActorContext(new(Guid.NewGuid()));
            var reference = new PrivateObjectReference(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(bytes), bytes.Length);
            var source = new NativeSource(actor, reference, bytes);
            var compiler = new FerruleBuildCompiler(generator, new(actor, source, source), xmlEnabled: true);
            var metadata = XmlDocumentMetadata.WithoutHints;
            var intent = new BuildIntent(Guid.NewGuid(), Scope, reference.Digest, compiler.XmlBackend, FerruleBuildCompiler.XmlProfile,
                [new(BuildBoundaryKind.PrimaryInput, null, "xml", xmlMetadata: metadata), new(BuildBoundaryKind.PrimaryOutput, null, "xml", xmlMetadata: metadata),
                 new(BuildBoundaryKind.NamedOutput, "copy", "xml", xmlMetadata: metadata)], new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(BuildPreflightReason.UnsupportedXmlBuild, (await new FerruleBuildCompiler(generator, new(actor, source, source)).InspectAsync(intent)).RefusalReason);
            var capability = await compiler.InspectAsync(intent);
            Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
            var artifact = await compiler.BuildAsync(intent, new(bytes, bytes.Length));
            var published = BuildArtifactReference.Create(intent, capability, artifact.Digest, artifact.Length);
            await using var worker = new ProcessCompiledRuntimeRunner(FerruleCompiledServiceTests.Options(root));
            var runtime = new FerruleCompiledBuildRuntime(worker, compiler.XmlBackend);
            await using var interpreter = new ProcessWorkerRunner(new(Executable!, DockerWorkerProfile.Preview, root));
            const string clock = "2026-10-24T12:00:00Z";
            foreach (var target in new[] { PreviewTargetSelection.All, PreviewTargetSelection.Primary, PreviewTargetSelection.Named("copy") })
            {
                var replay = new RegressionReplay(target, clock, PreviewLimits.Default, new(null,"<Order><code>A</code><quantity>2</quantity></Order>"u8, xmlMetadata:metadata), [], []);
                var result = await runtime.ExecuteAsync(published, artifact, replay);
                var interpreted = await new FerrulePreviewBackend(interpreter, PreviewExecutionProfile.XmlDocument, xmlEnabled: true).PreviewAsync(new(NativeArtifact.Admit(Scope, bytes, PreviewPolicy.MaximumNativeBytes), target, replay.Primary, [], [], clock, replay.Limits) { Profile = PreviewExecutionProfile.XmlDocument });
                Assert.Equal(MappingPreviewStatus.Succeeded, interpreted.Status);
                Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
                Assert.Equal(interpreted.Artifacts.Count, result.Outputs!.Outputs.Count);
                for (int i = 0; i < interpreted.Artifacts.Count; i++) Assert.Equal(interpreted.Artifacts[i].Bytes, result.Outputs.Outputs[i].CopyBytes());
            }
            var invalid = await runtime.ExecuteAsync(published, artifact, new(PreviewTargetSelection.All, clock, PreviewLimits.Default,
                new(null,"<Order>"u8, xmlMetadata:metadata), [], []));
            Assert.Equal(CompiledBuildOutcome.InvalidInput, invalid.Outcome);
            Assert.Null(invalid.Outputs);
            var limited = await runtime.ExecuteAsync(published, artifact, new(PreviewTargetSelection.All, clock,
                PreviewLimits.Default with { MaximumOutputBytes = 16 },
                new(null, "<Order><code>A</code><quantity>2</quantity></Order>"u8, xmlMetadata: metadata), [], []));
            Assert.Equal(CompiledBuildOutcome.ResourceLimit, limited.Outcome);
            Assert.Null(limited.Outputs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_x12_compiler_and_restricted_runtime_match_the_interpreted_complete_envelope_bytes()
        => await DocumentCompilerParity(false, false);

    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_x12_output_matches_interpreted_complete_envelope_bytes()
        => await DocumentCompilerParity(true, false);

    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_named_x12_output_matches_interpreted_all_primary_and_named_selection()
        => await DocumentCompilerParity(false, true);

    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_edifact_compiler_and_restricted_runtime_match_the_interpreted_complete_envelope_bytes()
        => await DocumentCompilerParity(false, false, "edifact");

    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_edifact_output_matches_interpreted_complete_envelope_bytes()
        => await DocumentCompilerParity(true, false, "edifact");

    [ServiceCompileFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_named_edifact_output_matches_interpreted_all_primary_and_named_selection()
        => await DocumentCompilerParity(false, true, "edifact");

    [ServiceCompileTheory]
    [InlineData("x12", true)]
    [InlineData("x12", false)]
    [InlineData("edifact", true)]
    [InlineData("edifact", false)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Document_oversized_segments_refuse_all_affected_targets_without_partial_success(string family, bool primaryEdi)
        => await DocumentCompilerParity(primaryEdi, true, family, oversized: true);

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task DocumentCompilerParity(bool ediOutput, bool namedOutput, string family = "x12", bool oversized = false)
    {
        var root = TemporaryRoot();
        try
        {
            var native = JsonNode.Parse(DocumentPreviewTestData.Native(family))!;
            if (oversized) native["source_options"] = new JsonObject { ["json_document"] = true };
            if (!ediOutput) native["target_options"] = new JsonObject { ["json_document"] = true };
            foreach (var side in new[] { "source", "target" })
            {
                var segments = native[side]!["kind"]!["children"]!.AsArray();
                if (family == "x12")
                {
                    segments.Single(value => (string?)value!["name"] == "ISA")!["kind"]!["children"]![11]!["fixed"] = "00501";
                    segments.Single(value => (string?)value!["name"] == "GS")!["kind"]!["children"]![7]!["fixed"] = "005010";
                }
                else
                {
                    var syntax = segments.Single(value => (string?)value!["name"] == "UNB")!["kind"]!["children"]![0]!["kind"]!["children"]!;
                    syntax[0]!["fixed"] = "UNOC";
                    syntax[1]!["fixed"] = "3";
                    var message = segments.Single(value => (string?)value!["name"] == "UNH")!["kind"]!["children"]![1]!["kind"]!["children"]!;
                    foreach (var (index, value) in new[] { "ORDERS", "D", "96A", "UN" }.Index()) message[index]!["fixed"] = value;
                }
            }
            if (namedOutput) native["extra_targets"] = new JsonArray(new JsonObject
            {
                ["name"] = "edi-copy",
                ["path"] = "/private/design/not-a-codec.json",
                ["schema"] = native["target"]!.DeepClone(),
                ["root"] = native["root"]!.DeepClone(),
                ["options"] = new JsonObject { ["edi_kind"] = family }
            });
            var bytes = Encoding.UTF8.GetBytes(native.ToJsonString());
            await using var compileRunner = new ProcessCompileRunner(CompileOptions(root));
            var actor = new ActorContext(new(Guid.NewGuid()));
            var reference = new PrivateObjectReference(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(bytes), bytes.Length);
            var source = new NativeSource(actor, reference, bytes);
            var compiler = new FerruleBuildCompiler(compileRunner, new(actor, source, source));
            var boundaries = new List<BuildBoundary> { new(BuildBoundaryKind.PrimaryInput, null, oversized ? "json" : family), new(BuildBoundaryKind.PrimaryOutput, null, ediOutput ? family : "json") };
            if (namedOutput) boundaries.Add(new(BuildBoundaryKind.NamedOutput, "edi-copy", family));
            var intent = new BuildIntent(Guid.NewGuid(), Scope, reference.Digest, compiler.DocumentBackend, FerruleBuildCompiler.DocumentProfile,
                boundaries,
                new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            var capability = await compiler.InspectAsync(intent);
            Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
            var artifact = await compiler.BuildAsync(intent, new(bytes, bytes.Length));
            var artifactReference = BuildArtifactReference.Create(intent, capability, artifact.Digest, artifact.Length);
            await using var previewRunner = new ProcessWorkerRunner(new(Executable!, DockerWorkerProfile.Preview, root));
            await using var runtimeRunner = new ProcessCompiledRuntimeRunner(FerruleCompiledServiceTests.Options(root));
            var runtime = new FerruleCompiledBuildRuntime(runtimeRunner, compiler.Backend);
            var targets = new List<PreviewTargetSelection> { PreviewTargetSelection.All, PreviewTargetSelection.Primary };
            if (namedOutput) targets.Add(PreviewTargetSelection.Named("edi-copy"));
            foreach (var target in targets)
            {
                var replay = new RegressionReplay(target, "2026-10-24T12:00:00Z", PreviewLimits.Default,
                    new(null, oversized ? JsonInput(family, 20_000) : DocumentPreviewTestData.Input(family)), [], []);
                var interpreted = await new FerrulePreviewBackend(previewRunner).PreviewAsync(new(
                    NativeArtifact.Admit(Scope, bytes, PreviewPolicy.MaximumNativeBytes), target, replay.Primary, [], [], replay.CurrentDateTime, replay.Limits)
                { Profile = PreviewExecutionProfile.Document });
                var result = await runtime.ExecuteAsync(artifactReference, artifact, replay);
                if (oversized && (ediOutput || target.Kind != PreviewTargetKind.Primary))
                {
                    Assert.Equal(MappingPreviewStatus.ResourceLimit, interpreted.Status);
                    Assert.Empty(interpreted.Artifacts);
                    Assert.Equal(CompiledBuildOutcome.ResourceLimit, result.Outcome);
                    Assert.Null(result.Outputs);
                    continue;
                }
                Assert.Equal(MappingPreviewStatus.Succeeded, interpreted.Status);
                Assert.Equal(CompiledBuildOutcome.Completed, result.Outcome);
                Assert.Equal(interpreted.Artifacts.Count, result.Outputs!.Outputs.Count);
                for (var index = 0; index < interpreted.Artifacts.Count; index++)
                {
                    Assert.Equal(interpreted.Artifacts[index].Bytes, result.Outputs.Outputs[index].CopyBytes());
                    Assert.Equal(interpreted.Artifacts[index].MediaType, result.Outputs.Outputs[index].MediaType);
                }
            }
            if (oversized)
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                return;
            }
            var trailer = family == "x12" ? "SE*3*0330" : "UNT+3+330";
            foreach (var broken in family == "x12" ? new[] { "SE*9*0330", "SE*3*9999" } : ["UNT+9+330", "UNT+3+9999"])
            {
                var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-24T12:00:00Z", PreviewLimits.Default,
                    new(null, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DocumentPreviewTestData.Input(family)).Replace(trailer, broken, StringComparison.Ordinal))), [], []);
                var result = await runtime.ExecuteAsync(artifactReference, artifact, replay);
                Assert.Equal(CompiledBuildOutcome.InvalidInput, result.Outcome);
                Assert.Null(result.Outputs);
            }
            if (family == "x12")
            {
                var mixed = DocumentPreviewTestData.X12 + DocumentPreviewTestData.X12.Replace('*', '|').Replace('~', '!');
                var replay = new RegressionReplay(PreviewTargetSelection.All, "2026-10-24T12:00:00Z", PreviewLimits.Default,
                    new(null, Encoding.UTF8.GetBytes(mixed)), [], []);
                var result = await runtime.ExecuteAsync(artifactReference, artifact, replay);
                Assert.Equal(CompiledBuildOutcome.ExecutionFailed, result.Outcome);
                Assert.Null(result.Outputs);
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] JsonInput(string family, int businessLength)
    {
        var input = new JsonObject();
        foreach (var segment in (family == "x12" ? DocumentPreviewTestData.X12 : DocumentPreviewTestData.Edifact)
            .Split(family == "x12" ? '~' : '\'', StringSplitOptions.RemoveEmptyEntries))
        {
            var elements = segment.Split(family == "x12" ? '*' : '+');
            var fields = new JsonObject();
            for (var i = 1; i < elements.Length; i++)
            {
                if (family == "edifact" && elements[i].Contains(':'))
                {
                    var components = new JsonObject();
                    foreach (var (index, part) in elements[i].Split(':').Index()) components["c" + (index + 1)] = part;
                    fields["e" + i] = components;
                }
                else fields["e" + i] = elements[i] == "BRAID-330" ? new string('X', businessLength) : elements[i];
            }
            input[elements[0]] = fields;
        }
        return Encoding.UTF8.GetBytes(input.ToJsonString());
    }

    [ServiceCompileFact]
    public async Task CompileEmitsAVerifiedAssemblyUnderTheServiceIdentityAndLeavesNothing()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root));
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Executable!))), runner.BuildIdentity);
            Assert.Matches("^sha256:[0-9a-f]{64}$", runner.ToolchainIdentity);
            Assert.True(await runner.ProbeAsync(CancellationToken.None));
            var (compiler, intent, native) = Compiler(runner, "valid-basic");
            var capability = await compiler.InspectAsync(intent);
            Assert.Equal(BuildPreflightOutcome.Supported, new BuildPreflightService().Evaluate(intent, capability).Outcome);
            var artifact = (await compiler.BuildAsync(intent, new(native, native.Length))).Copy();
            Assert.True(artifact.Length > 1024);
            Assert.Equal("MZ", Encoding.ASCII.GetString(artifact, 0, 2));
            // The compiler is deterministic: a second operation in fresh children returns the same bytes.
            Assert.Equal(artifact, (await compiler.BuildAsync(intent, new(native, native.Length))).Copy());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(runner.Available);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task InspectionsAndRefusalsAnswerWithoutCompiling()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root));
            var inspected = await runner.RunAsync(BuildFrame("valid-basic", "inspect"), CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, inspected.Failure);
            var request = BuildWorkerProtocol.DecodeRequest(BuildFrame("valid-basic", "inspect")).Header;
            var reply = BuildWorkerProtocol.DecodeReply(inspected.Output, request);
            Assert.Equal("supported", reply.Header.Status);
            Assert.Empty(reply.Header.Files);
            var refused = await runner.RunAsync(BuildFrame("preview-csv-source", "compile"), CancellationToken.None);
            Assert.Equal(WorkerProcessFailure.None, refused.Failure);
            Assert.Equal("unsupported", BuildWorkerProtocol.DecodeReply(refused.Output,
                BuildWorkerProtocol.DecodeRequest(BuildFrame("preview-csv-source", "compile")).Header).Header.Status);
            // An inadmissible request is a failed compile, as in the Docker compiler; no child starts.
            Assert.Equal(WorkerProcessFailure.WorkerFailed, (await runner.RunAsync(new byte[] { 0, 0, 0, 1, 7 }, CancellationToken.None)).Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [CompileEquivalenceFact]
    public async Task CompileRepliesEqualTheDockerCompilerByteForByte()
    {
        var root = TemporaryRoot();
        try
        {
            await using var service = new ProcessCompileRunner(CompileOptions(root));
            var compiled = 0;
            foreach (var fixture in Fixtures())
            {
                foreach (var operation in new[] { "compile", "inspect" })
                {
                    var frame = BuildFrame(fixture, operation);
                    var expected = await DockerCompilerAsync(frame);
                    var actual = await service.RunAsync(frame, CancellationToken.None);
                    Assert.Equal(WorkerProcessFailure.None, expected.Failure);
                    Assert.Equal(expected.Failure, actual.Failure);
                    Assert.Equal(expected.Output, actual.Output);
                    var reply = BuildWorkerProtocol.DecodeReply(actual.Output, BuildWorkerProtocol.DecodeRequest(frame).Header);
                    if (reply.Header.Files is [{ Path: BuildCompilation.Output }]) compiled++;
                }
            }

            Assert.True(compiled >= 2, "At least two fixtures must compile to artifacts.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public Task WallDeadlineKillsTheCompilerAndAdmitsTheNextOperation() => TerminationKillsCompiler(cancel: false);

    [ServiceCompileFact]
    public Task CallerCancellationKillsTheCompilerAndAdmitsTheNextOperation() => TerminationKillsCompiler(cancel: true);

    /// <summary>The actual hardened compiler launcher waits for GO until it is killed.</summary>
    private static async Task TerminationKillsCompiler(bool cancel)
    {
        var root = TemporaryRoot();
        try
        {
            await using var held = new ProcessCompileRunner(CompileOptions(root, TimeSpan.FromSeconds(cancel ? 60 : 8), withholdCompiler: true));
            using var cancellation = new CancellationTokenSource();
            var running = held.RunAsync(BuildFrame("valid-basic", "compile"), cancellation.Token);
            var child = await WaitForHeldCompiler();
            if (cancel) await cancellation.CancelAsync();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(cancel ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
            Assert.Empty(result.Output);
            Assert.False(Directory.Exists($"/proc/{child}"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(held.Available);
            Assert.Equal(WorkerProcessFailure.None, (await held.RunAsync(BuildFrame("valid-basic", "inspect"), CancellationToken.None)).Failure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task CancellationDuringGenerationKillsTheWorkerBeforeAnyCompilation()
    {
        var root = TemporaryRoot();
        try
        {
            await using var runner = new ProcessCompileRunner(CompileOptions(root, withholdGeneration: true));
            using var cancellation = new CancellationTokenSource();
            var running = runner.RunAsync(BuildFrame("valid-basic", "compile"), cancellation.Token);
            var worker = Assert.Single(await WaitForWorkerChildren(1, sandboxReady: true));
            await cancellation.CancelAsync();
            Assert.Equal(WorkerProcessFailure.Canceled, (await running.WaitAsync(TimeSpan.FromSeconds(30))).Failure);
            Assert.False(Directory.Exists($"/proc/{worker}"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Empty(CompileChildren());
            Assert.True(runner.Available);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [ServiceCompileFact]
    public async Task StoppingTheRunnerCancelsAndJoinsALiveCompiler()
    {
        var root = TemporaryRoot();
        try
        {
            var runner = new ProcessCompileRunner(CompileOptions(root, withholdCompiler: true));
            var running = runner.RunAsync(BuildFrame("valid-basic", "compile"), CancellationToken.None);
            var child = await WaitForHeldCompiler();
            await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkerProcessFailure.Canceled, (await running).Failure);
            Assert.False(Directory.Exists($"/proc/{child}"));
            Assert.False(runner.Available);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable, (await runner.RunAsync(BuildFrame("valid-basic", "compile"), CancellationToken.None)).Failure);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            await runner.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_non_native_launcher_is_refused_before_any_execution()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TemporaryRoot();
        using var installation = new FakeInstallation();
        try
        {
            // Announces the marker without applying any control and records whatever it would be sent.
            var launcher = installation.Script("launcher", $"""
                #!/bin/sh
                env > '{installation.Directory}/environment'
                printf BFC2
                cat > '{installation.Directory}/go'
                """);
            Environment.SetEnvironmentVariable("BRAID_SERVICE_TEST_SECRET", "must-not-reach-a-child");
            // A shell wrapper is not the surviving measured native image, even if it knows the marker.
            var toolchain = installation.Toolchain;
            Assert.Throws<ArgumentException>(() => new ProcessCompileRunner(new ProcessCompileOptions(launcher, launcher, root, toolchain)));
            Assert.False(File.Exists(Path.Combine(installation.Directory, "environment")));
            Assert.DoesNotContain("BRAID_SERVICE_TEST_SECRET", new ProcessCompileOptions(launcher, launcher, root, installation.Toolchain).Environment(root).Keys);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAID_SERVICE_TEST_SECRET", null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OptionsPinTheLauncherLimitsGrantsAndCompilerEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;
        var previousWorker = Environment.GetEnvironmentVariable("BRAID_FERRULE_HOST_EXECUTABLE");
        Environment.SetEnvironmentVariable("BRAID_FERRULE_HOST_EXECUTABLE", null);
        try
        {
            // This ordinary contract test must remain usable without the optional live worker.
            Assert.Null(Environment.GetEnvironmentVariable("BRAID_FERRULE_HOST_EXECUTABLE"));
            using var installation = new FakeInstallation();
            var options = new ProcessCompileOptions("/ferrule-host", BuildCompilation.Host(installation.Toolchain), "/tmp", installation.Toolchain);
            Assert.Equal(DockerWorkerProfile.Build, options.Generation.Profile);
            Assert.Equal(TimeSpan.FromSeconds(120), options.WallTimeout);
            Assert.Equal(["--service-sandbox", "--cpu-seconds=120", "--memory-bytes=2147483648", "--open-files=256"], options.Generation.Arguments);
            var grants = ProcessCompileOptions.ToolchainGrants.Resolve(options);
            var toolchain = installation.Toolchain;
            var expectedReads = new List<string> { toolchain };
            expectedReads.AddRange(new[] { "/lib", "/lib64", "/usr/lib", "/usr/lib64" }.Where(Directory.Exists));
            if (File.Exists("/dev/urandom")) expectedReads.Add("/dev/urandom");
            Assert.Equal(expectedReads, grants.Read);
            var arguments = options.LauncherArguments("/tmp/braid-compile-1", grants, output: true);
            string[] expectedCompile = ["--cpu-seconds=120", "--memory-bytes=6442450944", "--open-files=256", "--file-bytes=67108864",
            "--output=Ferrule.Generated.dll", "--dotnet-root=" + toolchain, "--profile=compile", .. expectedReads.Select(path => "--read=" + path)];
            Assert.Equal(expectedCompile, arguments);
            Assert.Equal(expectedCompile.Where(argument => argument != "--output=Ferrule.Generated.dll"),
                options.LauncherArguments("/tmp/braid-compile-1", grants, output: false));
            Assert.Single(grants.Interpreters);
            Assert.Equal(toolchain + "/host/fxr/10.0.12/libhostfxr.so", BuildCompilation.HostFxr(toolchain));
            Assert.Equal(toolchain + "/sdk/10.0.112/Roslyn/bincore/csc.dll", BuildCompilation.Compiler(toolchain));
            Assert.Equal(toolchain + "/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0", BuildCompilation.References(toolchain));
            Assert.Equal(["exec", toolchain + "/sdk/10.0.112/Roslyn/bincore/csc.dll", "-noconfig", "@/tmp/braid-compile-1/compiler.rsp"],
                BuildCompilation.CompilerArguments(toolchain, "/tmp/braid-compile-1"));
            var runtime = new ProcessCompiledRuntimeOptions("sha256:" + new string('a', 64), "/runtime/braid-compiled-runtime.dll",
                options.LauncherExecutable, "/tmp", toolchain);
            Assert.Equal(["--cpu-seconds=30", "--memory-bytes=3221225472", "--open-files=128", "--file-bytes=1048576",
            "--dotnet-root=" + toolchain, "--profile=runtime", "--runtime-assembly=/runtime/braid-compiled-runtime.dll",
            .. expectedReads.Append("/runtime").Select(path => "--read=" + path)], runtime.Arguments(grants));
            var environment = options.Environment("/tmp/braid-compile-1");
            Assert.Equal("0x80000000", environment["DOTNET_GCHeapHardLimit"]);
            Assert.Equal("0x80000000", environment["DOTNET_GCRegionRange"]);
            Assert.Equal("0", environment["DOTNET_EnableDiagnostics"]);
            Assert.Equal("Disable", environment["DOTNET_ROLL_FORWARD"]);
            Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
            Assert.Equal("/tmp/braid-compile-1", environment["TMPDIR"]);
            Assert.Equal(installation.Toolchain, environment["DOTNET_ROOT"]);
            Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "braid-compile-sandbox", "/tmp"));
            Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "/braid-compile-sandbox", "/tmp", "/usr/share/../dotnet"));
            Assert.Throws<ArgumentException>(() => new ProcessCompileOptions("/ferrule-host", "/braid-compile-sandbox", "/tmp")
            {
                Generation = new ProcessWorkerOptions("/ferrule-host", DockerWorkerProfile.Preview, "/tmp")
            });
            Assert.Equal("BFC2", Encoding.ASCII.GetString(ProcessCompileOptions.ReadyMarker));
        }
        finally { Environment.SetEnvironmentVariable("BRAID_FERRULE_HOST_EXECUTABLE", previousWorker); }
    }

    /// <summary>
    /// The Docker compiler's reply to one frame from a fresh runner. Under heavy host load a container's removal can
    /// exceed the runner's fixed cleanup bound; that is the reference runner's own failure, not an answer, so the
    /// frame is repeated with another fresh runner, at most three times.
    /// </summary>
    internal static async Task<WorkerProcessResult> DockerCompilerAsync(byte[] frame)
    {
        for (var attempt = 1; ; attempt++)
        {
            var docker = new DockerInspectionWorkerRunner(new DockerInspectionWorkerOptions("/usr/bin/docker",
                Environment.GetEnvironmentVariable("BRAID_BUILD_WORKER_IMAGE")!, profile: DockerWorkerProfile.Build));
            var result = await docker.RunAsync(frame, CancellationToken.None);
            try { await docker.DisposeAsync(); }
            catch (InvalidOperationException) when (result.Failure == WorkerProcessFailure.CleanupFailed) { }
            if (result.Failure != WorkerProcessFailure.CleanupFailed || attempt == 3) return result;
        }
    }

    /// <summary>The actual compiler launcher held after its sandbox controls are installed.</summary>
    private static async Task<int> WaitForHeldCompiler()
    {
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            foreach (var pid in Children(Path.GetFileName(Launcher!)))
            {
                try
                {
                    var status = await File.ReadAllLinesAsync($"/proc/{pid}/status");
                    if (status.Contains("Seccomp:\t2", StringComparer.Ordinal) && status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal)) return pid;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The held compiler did not start.");
    }

    internal static (FerruleBuildCompiler Compiler, BuildIntent Intent, byte[] Native) Compiler(IInspectionWorkerRunner runner, string fixture)
    {
        var native = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json"));
        var actor = new ActorContext(new(Guid.NewGuid()));
        var reference = new PrivateObjectReference(PrivateObjectId.Create(Scope, PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes(native), native.Length);
        var source = new NativeSource(actor, reference, native);
        var compiler = new FerruleBuildCompiler(runner, new(actor, source, source));
        var intent = new BuildIntent(Guid.NewGuid(), Scope, reference.Digest, compiler.Backend, FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")],
            new(new(Scope.OrganizationId, Scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
        return (compiler, intent, native);
    }

    internal sealed class NativeSource(ActorContext actor, PrivateObjectReference reference, byte[] bytes) : IBuildAdmissionReader, IPrivateObjectStore
    {
        public Task<BuildAdmission?> ReadAsync(ActorContext expected, BuildIntent intent, CancellationToken token = default) =>
            Task.FromResult<BuildAdmission?>(new(actor.PrincipalId, intent.Fingerprint, reference));

        public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference value, CancellationToken token = default) => Task.FromResult(bytes.ToArray());

        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference value, Stream source, CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A disposable toolchain layout whose host is a real ELF executable; nothing in it compiles.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal sealed class FakeInstallation : IDisposable
    {
        internal FakeInstallation()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("braid-compile-installation-").FullName;
            Toolchain = Path.Combine(Directory, "dotnet-root");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(BuildCompilation.Compiler(Toolchain))!);
            System.IO.Directory.CreateDirectory(BuildCompilation.References(Toolchain));
            var host = Path.Combine(Toolchain, "host/fxr", BuildWorkerProtocol.RuntimeVersion);
            var runtime = Path.Combine(Toolchain, "shared/Microsoft.NETCore.App", BuildWorkerProtocol.RuntimeVersion);
            System.IO.Directory.CreateDirectory(host);
            System.IO.Directory.CreateDirectory(runtime);
            File.WriteAllBytes(Path.Combine(host, "libhostfxr.so"), [1]);
            File.WriteAllBytes(Path.Combine(runtime, "System.Private.CoreLib.dll"), [1]);
            File.WriteAllBytes(BuildCompilation.Compiler(Toolchain), [1]);
            File.Copy("/usr/bin/true", BuildCompilation.Host(Toolchain));
        }

        internal string Directory { get; }
        internal string Toolchain { get; }

        internal string Script(string name, string text)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
