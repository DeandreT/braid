using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Runs the restricted preview worker image; expectations are authored independently of the worker.</summary>
public sealed class FerrulePreviewWorkerIntegrationTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly byte[] Order =
        "{\"id\":\"SO-1\",\"lines\":[{\"sku\":\"A\",\"qty\":2},{\"sku\":\"B\",\"qty\":9007199254740993}]}"u8.ToArray();
    private static readonly byte[] Rates = "{\"currency\":\"EUR\"}"u8.ToArray();
    private const string Clock = "2026-10-20T01:02:03.5+02:00";

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualWorkerPreviewsJsonWithParametersNamedInputsAndTheHostClock()
    {
        var backend = new FerrulePreviewBackend(new DockerInspectionWorkerRunner(Options()));
        var request = Request();
        var native = request.Artifact.CopyBytes();
        var result = await backend.PreviewAsync(request);

        Assert.True(result.Status == MappingPreviewStatus.Succeeded, result.Failure?.Message);
        Assert.True(result.Provenance.Confirmed);
        Assert.Equal(Options().ImageId, result.Provenance.BackendBuild);
        Assert.Equal(FerruleInspectionBackend.EngineRevision, result.Provenance.EngineRevision);
        Assert.Equal(["primary-output", "named-output:0"], result.Artifacts.Select(artifact => artifact.BoundaryId));
        Assert.Equal(["Shipment", "summary"], result.Artifacts.Select(artifact => artifact.TargetName));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"orderId":"SO-1","region":"west","generatedAt":"2026-10-20T01:02:03.5+02:00","currency":"EUR","express":true,
             "items":[{"sku":"A","quantity":2},{"sku":"B","quantity":9007199254740993}]}
            """), JsonNode.Parse(result.Artifacts[0].Bytes)));
        Assert.Contains("9007199254740993", Encoding.UTF8.GetString(result.Artifacts[0].Bytes), StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"lineCount":2,"region":"west"}"""), JsonNode.Parse(result.Artifacts[1].Bytes)));
        Assert.Equal(native, request.Artifact.CopyBytes());

        var repeated = await backend.PreviewAsync(Request());
        Assert.Equal(result.Artifacts.Select(artifact => artifact.CopyBytes()), repeated.Artifacts.Select(artifact => artifact.CopyBytes()));
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualWorkerReturnsBoundedRefusalsWithoutPartialOutputs()
    {
        var backend = new FerrulePreviewBackend(new DockerInspectionWorkerRunner(Options()));
        var cases = new (MappingPreviewRequest Request, MappingPreviewStatus Status, string Code, string? Subject)[]
        {
            (Request() with { NamedInputs = [] }, MappingPreviewStatus.MissingInput, "ferrule.missingInput", "rates"),
            (Request() with { Primary = new(null, "{\"id\":"u8) }, MappingPreviewStatus.InvalidInput, "ferrule.invalidInput", "primary"),
            (Request() with { Parameters = [new("region", PreviewParameterKind.String, "west")] }, MappingPreviewStatus.InvalidParameter, "ferrule.missingParameter", "express"),
            (Request() with { Limits = PreviewLimits.Default with { MaximumOutputBytes = 32 } }, MappingPreviewStatus.ResourceLimit, "ferrule.resultLimitExceeded", null),
            (Request() with { CurrentDateTime = "2026-10-20T01:02:03" }, MappingPreviewStatus.InvalidRequest, "ferrule.invalidClock", null),
            (Request("preview-csv-source", Order) with { NamedInputs = [], Parameters = [] }, MappingPreviewStatus.UnsupportedCapability, "ferrule.unsupportedCapability", "primary"),
            (Request("invalid-graph", Order) with { NamedInputs = [], Parameters = [] }, MappingPreviewStatus.InvalidMapping, "ferrule.invalidMapping", null)
        };
        foreach (var (request, status, code, subject) in cases)
        {
            var result = await backend.PreviewAsync(request);
            Assert.Equal(status, result.Status);
            Assert.True(result.Provenance.Confirmed);
            Assert.Empty(result.Artifacts);
            Assert.Equal(code, result.Failure!.Code);
            Assert.Equal(subject, result.Failure.Subject);
            Assert.True(Encoding.UTF8.GetByteCount(result.Failure.Message) <= PreviewPolicy.MaximumTextBytes);
        }
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public Task CancellationRemovesARunningPreviewWorkerAndAdmitsTheNextRequest() => TerminationRemovesWorker(cancel: true);

    [FerruleWorkerIntegrationTests.WorkerFact]
    public Task DeadlineRemovesARunningPreviewWorkerAndAdmitsTheNextRequest() => TerminationRemovesWorker(cancel: false);

    private static async Task TerminationRemovesWorker(bool cancel)
    {
        var launcher = new HoldingInputLauncher();
        var backend = new FerrulePreviewBackend(new DockerInspectionWorkerRunner(Options(TimeSpan.FromSeconds(5)), launcher));
        using var cancellation = new CancellationTokenSource();
        var operation = backend.PreviewAsync(Request(), cancellation.Token);
        try
        {
            await launcher.InputWritten.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.NotNull(launcher.ContainerName);
            using var container = JsonDocument.Parse(await WaitForRunningContainer(launcher.ContainerName));
            var configuration = container.RootElement.GetProperty("HostConfig");
            Assert.Equal("none", configuration.GetProperty("NetworkMode").GetString());
            Assert.True(configuration.GetProperty("ReadonlyRootfs").GetBoolean());
            Assert.Equal(536870912, configuration.GetProperty("Memory").GetInt64());
            Assert.Equal(536870912, configuration.GetProperty("MemorySwap").GetInt64());
            Assert.Contains(configuration.GetProperty("Ulimits").EnumerateArray(), limit =>
                limit.GetProperty("Name").GetString() == "cpu" && limit.GetProperty("Hard").GetInt64() == 30);
            Assert.Empty(container.RootElement.GetProperty("Mounts").EnumerateArray());
            if (cancel)
                await cancellation.CancelAsync();
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(cancel ? MappingPreviewStatus.Canceled : MappingPreviewStatus.TimedOut, result.Status);
            Assert.False(result.Provenance.Confirmed);
            Assert.Empty(result.Artifacts);
            Assert.NotEqual(0, (await ContainerCommand("inspect", launcher.ContainerName)).ExitCode);
            var retry = await backend.PreviewAsync(Request());
            Assert.Equal(MappingPreviewStatus.Succeeded, retry.Status);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (launcher.ContainerName is { } name)
                await ContainerCommand("rm", "--force", name);
            await operation.WaitAsync(TimeSpan.FromSeconds(12));
        }
    }

    private static MappingPreviewRequest Request(string fixture = "preview-orders", byte[]? primary = null) => new(
        NativeArtifact.Admit(Scope, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", fixture + ".json")),
            InspectionPolicy.MaximumNativeBytes),
        PreviewTargetSelection.All,
        new PreviewInputDocument(null, primary ?? Order),
        [new PreviewInputDocument("rates", Rates)],
        [new("region", PreviewParameterKind.String, "west"), new("express", PreviewParameterKind.Boolean, "true")],
        Clock,
        PreviewLimits.Default);

    private static DockerInspectionWorkerOptions Options(TimeSpan? deadline = null) => new("/usr/bin/docker",
        Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!, deadline, profile: DockerWorkerProfile.Preview);

    private static async Task<string> WaitForRunningContainer(string name)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var state = await ContainerCommand("inspect", "--format", "{{json .}}", name);
            if (state.ExitCode == 0)
            {
                using var parsed = JsonDocument.Parse(state.Output);
                if (parsed.RootElement.GetProperty("State").GetProperty("Running").GetBoolean())
                    return state.Output;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The owned preview worker did not reach its running state.");
    }

    private static async Task<(int ExitCode, string Output)> ContainerCommand(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (var argument in new[] { "--host", "unix:///var/run/docker.sock", "container" }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await error;
            return (process.ExitCode, await output);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }

    // Withhold EOF once so the real worker stays blocked on the protocol's completion
    // check. Daemon, image, container, pipes and cleanup stay real.
    private sealed class HoldingInputLauncher : IWorkerProcessLauncher
    {
        private int held;
        public TaskCompletionSource InputWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ContainerName { get; private set; }

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var name = Array.IndexOf(arguments, "--name");
            if (name >= 0)
                ContainerName = arguments[name + 1];
            var hold = arguments.Contains("--attach", StringComparer.Ordinal) && Interlocked.Exchange(ref held, 1) == 0;
            var process = new Process { StartInfo = startInfo };
            process.Start();
            return new HeldProcess(process, hold ? new HeldInput(process.StandardInput.BaseStream, InputWritten) : process.StandardInput.BaseStream, hold);
        }
    }

    private sealed class HeldProcess(Process process, Stream input, bool holdInputOpen) : IWorkerProcess
    {
        public Stream StandardInput => input;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput() { if (!holdInputOpen) process.StandardInput.Dispose(); }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose() { process.StandardInput.Dispose(); process.Dispose(); }
    }

    private sealed class HeldInput(Stream inner, TaskCompletionSource written) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken) { await inner.FlushAsync(cancellationToken); written.TrySetResult(); }
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
