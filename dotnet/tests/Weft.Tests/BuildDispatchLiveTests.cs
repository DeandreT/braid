using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed class BuildDispatchLiveFactAttribute : FactAttribute
{
    public BuildDispatchLiveFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !PostgresFixture.IsConfigured || Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE") is null ||
            Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is null)
            Skip = "Configure actual Linux PostgreSQL and the same qualified combined native/compiler image.";
    }
}

/// <summary>Actual native admission, saved regression evidence, ordinary worker process and private compiled publication.</summary>
[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class BuildDispatchLiveTests(PostgresFixture fixture)
{
    [BuildDispatchLiveFact]
    public async Task Approved_builds_compile_through_the_ordinary_worker_before_and_after_restart()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_BUILD_WORKER_IMAGE")!;
        Assert.Equal(image, Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE"));
        await using var native = await PostgresClaimAwareStorageIntegrationSupport.CreateAsync(fixture);
        var created = await native.CreateNativeAsync(RevisionIngestionTestSupport.Fixture("valid-basic"));
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, created.Outcome);
        var receipt = await native.ReceiptAsync(created);
        var revision = receipt.Facts.FutureRevision;
        await native.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            revision.OrganizationId, revision.ProjectId, native.Actor.PrincipalId.Value);
        var runtime = new RegressionRuntimeIdentity("ferrule", FerruleInspectionBackend.EngineRevision, image, FerrulePreviewBackend.AdapterBuild, PreviewPolicy.Profile);
        var expected = Encoding.UTF8.GetBytes("{\n  \"code\": \"A1\",\n  \"dotted\": \"dot\",\n  \"nestedOutput\": {\n    \"b\": \"x\"\n  }\n}\n");
        var value = new RegressionCaseVersion(Guid.NewGuid(), 1, native.Actor.PrincipalId, "Actual compiler evidence", revision,
            receipt.Facts.NativeObject.Digest, runtime,
            new(PreviewTargetSelection.All, "2026-10-20T12:00:00Z", PreviewLimits.Default,
                new(null, Encoding.UTF8.GetBytes("{\"code\":\"A1\",\"a.b\":\"dot\",\"nested\":{\"b\":\"x\"}}")), [], []),
            RegressionComparisonService.ExactBytesPolicy, RegressionOutputSet.Capture([RegressionOutput.Capture(RegressionOutputKey.Primary, expected)]));
        await using var caseWriter = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_writer"), RegressionCaseDatabaseProfile.Writer);
        await using var caseReader = new PostgresRegressionCaseStore(native.Case.RegressionConnectionString("weft_case_reader"), RegressionCaseDatabaseProfile.Reader);
        await caseWriter.SaveAsync(native.Actor, value);
        await using var runWriter = new PostgresRegressionRunStore(native.Case.RegressionConnectionString("weft_run_writer"), true);
        await using var runReader = new PostgresRegressionRunStore(native.Case.RegressionConnectionString("weft_run_reader"), false);
        var reference = new RegressionCaseEnvelopeCodec().Encode(value).Reference;
        var runId = Guid.NewGuid();
        Assert.Equal(RegressionRunSubmitOutcome.Created, await runWriter.SubmitAsync(new(native.Actor, runId, reference)));
        await using var previewRunner = new DockerInspectionWorkerRunner(new("/usr/bin/docker", image, profile: DockerWorkerProfile.Preview));
        var preview = new FerrulePreviewBackend(previewRunner);
        var regression = new RegressionCaseExecutionService(caseReader, new PostgresProjectReader(native.Native.ReaderDatabase),
            native.ReportReader, native.Physical, preview, runtime);
        Assert.Equal(RegressionRunDispatchOutcome.Completed, await new RegressionRunExecutor(runWriter, regression)
            .ExecuteAsync(revision.OrganizationId, runId, Guid.NewGuid()));
        var evidence = new RegressionRunReadService(runReader, caseReader);
        Assert.Equal(RegressionComparisonOutcome.Passed, (await evidence.ReadAsync(native.Actor, revision.OrganizationId, runId))!.Result!.Comparison!.Outcome);

        await using var controls = new PostgresBuildStore(native.Case.RegressionConnectionString("weft_build_writer"), true);
        var environment = new BuildEnvironmentReference(new(revision.OrganizationId, revision.ProjectId), Guid.NewGuid(), 1);
        Assert.Equal(BuildControlOutcome.Created, await controls.SaveEnvironmentAsync(native.Actor, new(environment, FerruleBuildCompiler.Profile, true)));
        var admissions = new BuildControlService(controls, new PostgresProjectReader(native.Native.ReaderDatabase), native.ReportReader, evidence);
        BuildIntent Intent() => new(Guid.NewGuid(), revision, value.NativeDigest,
            new("ferrule", FerruleInspectionBackend.EngineRevision, image, FerrulePreviewBackend.AdapterBuild), FerruleBuildCompiler.Profile,
            [new(BuildBoundaryKind.PrimaryInput, null, "json"), new(BuildBoundaryKind.PrimaryOutput, null, "json")], environment, Guid.NewGuid(), runId);
        await using var submission = new PostgresBuildRunStore(native.Case.RegressionConnectionString("weft_build_run_writer"), BuildRunDatabaseProfile.Writer);
        await using var history = new PostgresBuildRunStore(native.Case.RegressionConnectionString("weft_build_run_reader"), BuildRunDatabaseProfile.Reader);
        var first = Intent();
        Assert.Equal(BuildControlOutcome.Created, await admissions.ApproveAsync(native.Actor, first));
        Assert.Equal(BuildRunSubmitOutcome.Created, await submission.SubmitAsync(native.Actor, first));
        BuildRunSnapshot retained;
        await using (var worker = Child.Start(native, image))
            retained = await UntilAsync(history, native.Actor, first, worker);
        Assert.Equal(1, retained.Attempt); Assert.NotNull(retained.Artifact);
        var objects = new PostgresBuildObjectStore(controls, native.Physical, native.Actor);
        var artifact = new PrivateObjectReference(PrivateObjectId.Restore(revision, first.BuildId, PrivateObjectClass.DerivedArtifact),
            retained.Artifact.ContentDigest, retained.Artifact.ByteCount);
        var compiled = await objects.ReadVerifiedAsync(revision, artifact);
        Assert.True(compiled.Length > 1024);
        using (var bytes = new MemoryStream(compiled, false))
        using (var pe = new System.Reflection.PortableExecutable.PEReader(bytes))
            Assert.True(pe.HasMetadata && pe.PEHeaders.CorHeader is not null);

        var second = Intent();
        Assert.Equal(BuildControlOutcome.Created, await admissions.ApproveAsync(native.Actor, second));
        Assert.Equal(BuildRunSubmitOutcome.Created, await submission.SubmitAsync(native.Actor, second));
        await using (var restarted = Child.Start(native, image))
            Assert.Equal(1, (await UntilAsync(history, native.Actor, second, restarted)).Attempt);
        var reopened = (await history.ReadAsync(native.Actor, revision.OrganizationId, first.BuildId))!;
        Assert.Equal(retained.State, reopened.State); Assert.Equal(retained.Attempt, reopened.Attempt);
        Assert.Equal(retained.Artifact.ContentDigest, reopened.Artifact!.ContentDigest);
        Assert.Equal(compiled, await objects.ReadVerifiedAsync(revision, artifact));
        Assert.Equal(BuildRunSubmitOutcome.Replayed, await submission.SubmitAsync(native.Actor, first));
    }

    private static async Task<BuildRunSnapshot> UntilAsync(PostgresBuildRunStore history, ActorContext actor, BuildIntent intent, Child worker)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (worker.HasExited) throw new InvalidOperationException("The ordinary build worker exited before completion.");
            var row = await history.ReadAsync(actor, intent.Revision.OrganizationId, intent.BuildId);
            if (row?.State == BuildRunState.Succeeded) return row;
            Assert.NotNull(row);
            Assert.True(row.State is BuildRunState.Queued or BuildRunState.Running, "The actual build reached a nonpassing terminal state.");
            if (deadline.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("The ordinary build worker did not complete its queued build.");
            await Task.Delay(150);
        }
    }

    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string startTicks;
        private readonly Task output, error;
        private long bytes;
        private Child(Process process)
        {
            this.process = process; startTicks = StartTicks(process.Id);
            output = Drain(process.StandardOutput); error = Drain(process.StandardError);
        }
        internal bool HasExited => process.HasExited;
        internal static Child Start(PostgresClaimAwareStorageIntegrationSupport test, string image)
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "PreviewWorker", "Weft.Worker.dll");
            if (!File.Exists(dll)) throw new InvalidOperationException("The ordinary worker publication is required.");
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = test.Root.ApplicationRoot
            };
            start.ArgumentList.Add(dll);
            var inherited = new[] { "PATH", "DOTNET_ROOT", "LANG", "LC_ALL", "HOME" }.Where(start.Environment.ContainsKey)
                .ToDictionary(key => key, key => start.Environment[key]);
            start.Environment.Clear();
            foreach (var pair in inherited) start.Environment[pair.Key] = pair.Value;
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
            start.Environment["DOTNET_PROCESSOR_COUNT"] = "2";
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["TMPDIR"] = test.Root.NewDirectory("worker-" + Guid.NewGuid().ToString("N"));
            start.Environment["DOTNET_CLI_HOME"] = start.Environment["TMPDIR"];
            start.Environment["WEFT_PREVIEW_WRITER_CONNECTION_STRING"] = test.Case.PreviewWriterConnectionString;
            start.Environment["WEFT_PRIVATE_STORAGE_ROOT"] = test.Root.RootPath;
            start.Environment["WEFT_FERRULE_WORKER_IMAGE"] = image;
            start.Environment["WEFT_DOCKER_EXECUTABLE"] = "/usr/bin/docker";
            start.Environment["WEFT_BUILD_WORKER_ENABLED"] = "true";
            start.Environment["WEFT_BUILD_DISPATCHER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_dispatcher");
            start.Environment["WEFT_BUILD_WRITER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_build_writer");
            start.Environment["WEFT_BUILD_ACCESS_CONNECTION_STRING"] = test.Case.RuntimeConnectionString;
            start.Environment["WEFT_BUILD_REPORT_READER_CONNECTION_STRING"] = test.Case.ReportConnectionString;
            start.Environment["WEFT_BUILD_REGRESSION_RUN_READER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_run_reader");
            start.Environment["WEFT_BUILD_CASE_READER_CONNECTION_STRING"] = test.Case.RegressionConnectionString("weft_case_reader");
            return new(Process.Start(start) ?? throw new InvalidOperationException("The build worker did not start."));
        }
        private async Task Drain(StreamReader stream)
        {
            var buffer = new char[4096]; int count;
            while ((count = await stream.ReadAsync(buffer)) != 0) Interlocked.Add(ref bytes, count);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    Assert.Equal(startTicks, StartTicks(process.Id));
                    Assert.Equal(0, Signal(process.Id, 15));
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4)); }
                    catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                }
                await Task.WhenAll(output, error);
                Assert.Equal(0, process.ExitCode);
                Assert.True(Interlocked.Read(ref bytes) <= 2 * 1024 * 1024, "Worker diagnostics exceeded their bound.");
            }
            finally { process.Dispose(); }
        }
        private static string StartTicks(int pid) => File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat")
            .Split(") ", 2, StringSplitOptions.None)[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int Signal(int pid, int signal);
    }
}
