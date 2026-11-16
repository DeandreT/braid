using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Braid.MappingBackends.Workers;
using Braid.Server;

namespace Braid.WorkerShutdownProbe;

/// <summary>
/// Credential-free process-lifetime qualification, not an application endpoint.
/// The unowned control deliberately reproduces the old ownership graph; it must
/// never be interpreted as a supported production composition or cleanup promise.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private static readonly object OutputGate = new();
    private static long sequence;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static async Task<int> Main(string[] arguments)
    {
        if (!OperatingSystem.IsLinux() || arguments.Length != 0) return 2;
        using var inputDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Input input;
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4097];
            var stdin = Console.OpenStandardInput();
            while (await stdin.ReadAsync(buffer, inputDeadline.Token) is var count && count != 0)
            {
                if (bytes.Length + count > 4096) return 2;
                bytes.Write(buffer, 0, count);
            }
            input = JsonSerializer.Deserialize<Input>(bytes.ToArray(), Json) ?? throw new InvalidOperationException();
            if (input.SchemaVersion != "braid.worker-shutdown-probe.v1" ||
                input.Mode is not ("unowned-control" or "owned-drain") ||
                !Path.IsPathFullyQualified(input.SocketPath) || input.SocketPath != Path.GetFullPath(input.SocketPath)) return 2;
        }
        catch (Exception) { return 2; }

        var options = new DockerInspectionWorkerOptions("/usr/bin/docker", input.ImageId, dockerSocketPath: input.SocketPath);
        var launcher = new ObservedLauncher();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Internal.ConsoleLifetime>();
        DockerInspectionWorkerRunner? unowned = null;
        if (input.Mode == "owned-drain")
            InspectionWorkerHostConfiguration.Configure(builder.Services, options, launcher);
        else
            unowned = new DockerInspectionWorkerRunner(options, launcher);
        // Last registered, so reverse-order StoppingAsync observes entry before the drain.
        builder.Services.AddSingleton<IHostedService, LifecycleWitness>();
        var host = builder.Build();
        var exitCode = 5;
        try
        {
            await host.StartAsync();
            Emit("host-ready", hostPid: Environment.ProcessId, processGroup: GetProcessGroup(0),
                startTicks: ReadStartTicks(Environment.ProcessId));
            var runner = unowned ?? host.Services.GetRequiredService<DockerInspectionWorkerRunner>();
            // Raw self-authored fixture bytes are sufficient here: the owned-create test
            // must never attach or pass any native bytes after shutdown. A start is failure.
            var operation = runner.RunAsync(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "valid-basic.json")), CancellationToken.None);
            await launcher.CreateWaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = runner.RunAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            _ = ObserveQueuedAsync(queued);
            Emit("queue-owned");
            await host.WaitForShutdownAsync();
            Emit("host-stop-returned", unsettled: !operation.IsCompleted);
            if (input.Mode == "unowned-control" && !operation.IsCompleted) exitCode = 3;
            else
            {
                var result = await operation;
                Emit("run-terminal", failure: result.Failure.ToString());
                exitCode = result.Failure is WorkerProcessFailure.Canceled ? 0 : 4;
            }
        }
        catch (Exception) { Emit("host-failed"); }
        finally
        {
            try
            {
                if (host is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
                else host.Dispose();
                Emit("provider-disposed");
            }
            catch (Exception) { Emit("provider-disposal-failed"); exitCode = 6; }
            // The unowned negative control intentionally does not drain its runner.
            // The parent owns the proxy, exact name/ID, process group and final cleanup.
        }
        return exitCode;
    }

    private static async Task ObserveQueuedAsync(Task<WorkerProcessResult> queued)
    {
        try { Emit("queued-terminal", failure: (await queued).Failure.ToString()); }
        catch (Exception) { Emit("queued-failed"); }
    }

    private static void Emit(string kind, string? operation = null, string? containerName = null,
        int? cliPid = null, int? processGroup = null, long? startTicks = null, int? hostPid = null,
        int? exitCode = null, bool? unsettled = null, string? failure = null, long? inputBytes = null)
    {
        lock (OutputGate)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "braid.worker-shutdown-trace.v1",
                sequence = ++sequence,
                kind,
                operation,
                containerName,
                cliPid,
                processGroup,
                startTicks,
                hostPid,
                exitCode,
                unsettled,
                failure,
                inputBytes
            }, Json));
    }

    private sealed class LifecycleWitness : IHostedLifecycleService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) { Emit("stopping-entered"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Emit("hosted-stop"); return Task.CompletedTask; }
        public Task StoppedAsync(CancellationToken cancellationToken) { Emit("hosted-stopped"); return Task.CompletedTask; }
    }

    private sealed class ObservedLauncher : IWorkerProcessLauncher
    {
        internal TaskCompletionSource CreateWaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.Skip(4).ToArray();
            var operation = arguments switch
            {
                ["info", ..] => "info",
                ["image", "inspect", ..] => "image",
                ["container", "create", ..] => "create",
                ["container", "inspect", ..] => "inspect",
                ["container", "start", ..] => "start",
                ["container", "rm", ..] => "rm",
                _ => throw new InvalidOperationException("Unexpected probe operation.")
            };
            var nameIndex = Array.IndexOf(arguments, "--name");
            var name = nameIndex < 0 ? (operation is "rm" or "inspect" or "start" ? arguments[^1] : null) : arguments[nameIndex + 1];
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Probe CLI failed to start.");
                long? startTicks = null;
                try { startTicks = ReadStartTicks(process.Id); }
                catch (IOException) when (operation != "create" && process.HasExited) { }
                Emit("command-start", operation, name, process.Id, GetProcessGroup(process.Id),
                    startTicks);
                return new ObservedProcess(process, operation, name, CreateWaitRegistered);
            }
            catch { process.Dispose(); throw; }
        }
    }

    private sealed class ObservedProcess(Process process, string operation, string? name, TaskCompletionSource createWaitRegistered) : IWorkerProcess
    {
        private readonly StreamWriter writer = process.StandardInput;
        private CountingInput? input;
        private int closed, observed;
        public Stream StandardInput => input ??= new(writer.BaseStream);
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (operation == "create") createWaitRegistered.TrySetResult();
            await process.WaitForExitAsync(cancellationToken);
            if (Interlocked.Exchange(ref observed, 1) == 0)
                Emit("command-exit", operation, name, process.Id, exitCode: process.ExitCode, inputBytes: input?.Bytes ?? 0);
        }
        public void CloseStandardInput() { if (Interlocked.Exchange(ref closed, 1) == 0) writer.Dispose(); }
        public void Kill() { if (!process.HasExited) { Emit("command-kill", operation, name, process.Id); process.Kill(); } }
        public void Dispose()
        {
            try { CloseStandardInput(); }
            finally { process.StandardOutput.Dispose(); process.StandardError.Dispose(); process.Dispose(); }
        }
    }

    private sealed class CountingInput(Stream inner) : Stream
    {
        public long Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Bytes += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken); Bytes += buffer.Length; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed record Input(string SchemaVersion, string Mode, string ImageId, string SocketPath);

    // The kernel's boot-relative start time (stat field 22) is identical for every
    // observer; Process.StartTime is derived from the wall clock per reading process.
    private static long ReadStartTicks(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], CultureInfo.InvariantCulture);
    }

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroup(int pid);
}
