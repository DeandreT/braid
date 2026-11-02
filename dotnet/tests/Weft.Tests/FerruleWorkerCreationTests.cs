using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Actual Docker publication/cleanup and worker retry; the proxy gates one control request only.</summary>
[SupportedOSPlatform("linux")]
public sealed class FerruleWorkerCreationTests
{
    private static readonly RevisionScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task CallerCancellationDuringGatedActualCreateSettlesPublicationRemovesExactContainerAndAdmitsRealRetry()
    {
        await using var proxy = new DockerCreationGateProxy();
        var launcher = new ObservedRealLauncher();
        var options = new DockerInspectionWorkerOptions("/usr/bin/docker",
            Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!, dockerSocketPath: proxy.SocketPath);
        var runner = new DockerInspectionWorkerRunner(options, launcher);
        var backend = new FerruleInspectionBackend(runner);
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var operation = backend.InspectAsync(Artifact(), cancellation.Token);
        Task<MappingInspectionResult>? retry = null;
        DockerCreationGateProxy.PublishedCreation? publication = null;
        var failures = new List<Exception>();
        try
        {
            var name = await SignalAsync(proxy.CapturedCreate.Task, proxy, TimeSpan.FromSeconds(8));
            Assert.Equal(launcher.OwnerFor(name), await SignalAsync(proxy.CapturedOwner.Task, proxy, TimeSpan.FromSeconds(1)));
            await launcher.CreateWaitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(launcher.Commands, command => command.Operation is "container-inspect" or "start" or "rm");
            Assert.Equal(0, launcher.InputByteCount);
            Assert.False(proxy.PublishedCreate.Task.IsCompleted);
            cancellation.Cancel();
            // The observed real CLI wait token must remain live. The old linked create
            // token is synchronously canceled here, so this is a deterministic regression.
            Assert.False(launcher.CreateWaitCanceled.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
            using var queuedCancellation = new CancellationTokenSource();
            var commandCount = launcher.Commands.Count;
            var queued = backend.InspectAsync(Artifact(), queuedCancellation.Token);
            queuedCancellation.Cancel();
            Assert.Equal(MappingOperationStatus.Canceled, (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Status);
            Assert.Equal(commandCount, launcher.Commands.Count);
            Assert.DoesNotContain(launcher.Commands, command => command.Operation is "container-inspect" or "start" or "rm");
            Assert.Equal(0, launcher.InputByteCount);
            Assert.True(elapsed.Elapsed < options.WallTimeout, "Creation must be released inside the original wall budget.");
            proxy.ReleaseCreate();
            publication = await SignalAsync(proxy.PublishedCreate.Task, proxy, TimeSpan.FromSeconds(5));
            Assert.Equal(name, publication.Name);
            var canceled = await operation.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(MappingOperationStatus.Canceled, canceled.Status);
            Assert.False(canceled.Provenance.Confirmed);
            // Cancellation still forbids policy inspection/start/native input. The new
            // ownership-only read is necessary to authorize exact-ID cleanup.
            Assert.DoesNotContain(launcher.Commands, command => command.Operation is "container-inspect" or "start");
            Assert.Equal(0, launcher.InputByteCount);
            Assert.Equal(publication.ContainerId, Assert.Single(launcher.Commands, command => command.Operation == "ownership-inspect").Target);
            Assert.Equal(publication.ContainerId, Assert.Single(launcher.Commands, command => command.Operation == "rm").Target);
            await AssertAbsentAsync(proxy.DirectoryPath, publication.Name);
            await AssertAbsentAsync(proxy.DirectoryPath, publication.ContainerId);

            retry = backend.InspectAsync(Artifact());
            var succeeded = await retry.WaitAsync(TimeSpan.FromSeconds(14));
            Assert.Equal(MappingOperationStatus.Succeeded, succeeded.Status);
            Assert.True(succeeded.Provenance.Confirmed);
            Assert.Equal(Scope, succeeded.Provenance.Scope);
            Assert.Contains(launcher.Commands, command => command.Operation == "container-inspect");
            Assert.Contains(launcher.Commands, command => command.Operation == "start");
            Assert.True(launcher.InputByteCount > 0);
            foreach (var ownedName in launcher.CreatedNames)
                await AssertAbsentAsync(proxy.DirectoryPath, ownedName);
            Assert.False(proxy.Failure.Task.IsCompleted,
                proxy.Failure.Task.IsCompletedSuccessfully ? proxy.Failure.Task.Result.ToString() : null);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            cancellation.Cancel();
            proxy.ReleaseCreate();
            try
            {
                // Even when a regression kills the original CLI, settle the accepted
                // publication first. Its failure is retained while later cleanup proceeds.
                if (proxy.CapturedCreate.Task.IsCompletedSuccessfully)
                    await CollectAsync(async () => { publication ??= await SignalAsync(proxy.PublishedCreate.Task, proxy, TimeSpan.FromSeconds(5)); });
                await CollectAsync(async () => { await operation.WaitAsync(TimeSpan.FromSeconds(14)); });
                if (retry is not null)
                    await CollectAsync(async () => { await retry.WaitAsync(TimeSpan.FromSeconds(14)); });
            }
            finally
            {
                if (proxy.CapturedCreate.Task.IsCompletedSuccessfully)
                    await CollectAsync(async () =>
                    {
                        try { await proxy.CreateForwardCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (Exception settlement)
                        {
                            // A hung test forwarding task is aborted, then reaped before
                            // best-effort removal. Uncertain publication stays a failed gate.
                            proxy.AbortGatedForward();
                            try { await proxy.CreateForwardCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
                            catch (Exception aborted) { throw new AggregateException(settlement, aborted); }
                            throw;
                        }
                    });
                var names = launcher.CreatedNames.ToHashSet(StringComparer.Ordinal);
                if (proxy.CapturedCreate.Task.IsCompletedSuccessfully)
                    names.Add(proxy.CapturedCreate.Task.Result);
                foreach (var ownedName in names)
                    await CollectAsync(() => RemoveOwnedIfPresentAsync(proxy.DirectoryPath, ownedName, launcher.OwnerFor(ownedName),
                        publication is { } created && created.Name == ownedName ? created.ContainerId : null));
                if (publication is not null)
                    await CollectAsync(() => AssertAbsentAsync(proxy.DirectoryPath, publication.ContainerId));
                await CollectAsync(async () => await proxy.DisposeAsync());
            }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Actual creation qualification or owned cleanup failed.", failures);

        async Task CollectAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    [FerruleWorkerIntegrationTests.WorkerFact]
    public async Task ActualForcedRemovalOfAnAlreadyRemovedVerifiedIdCanReturnQuietSuccessWithoutAcknowledgement()
    {
        var directory = Directory.CreateTempSubdirectory("weft-missing-rm-").FullName;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var launcher = new ObservedRealLauncher();
        var runner = new DockerInspectionWorkerRunner(new("/usr/bin/docker",
            Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!), launcher);
        try
        {
            var completed = await new FerruleInspectionBackend(runner).InspectAsync(Artifact());
            Assert.Equal(MappingOperationStatus.Succeeded, completed.Status);
            // Successful runner completion required label/name/ID verification before its
            // first removal. Repeat ONLY that immutable ID, never an absent random name.
            var id = Assert.Single(launcher.Commands, command => command.Operation == "rm").Target!;
            Assert.Matches("^[a-f0-9]{64}$", id);
            await AssertAbsentAsync(directory, id);
            var removal = await DockerAsync(directory, "rm", "--force", "--volumes", id);
            Assert.Equal(0, removal.ExitCode);
            Assert.Equal("", removal.Output);
            // Docker versions differ: an already absent exact ID can return success
            // with either no diagnostic or the daemon's exact missing-ID diagnostic.
            Assert.True(removal.Error.Length == 0 || IsMissing(removal.Error, id), "Repeated removal returned an unexpected diagnostic.");
            await AssertAbsentAsync(directory, id);
        }
        finally
        {
            try
            {
                foreach (var name in launcher.CreatedNames)
                    await RemoveOwnedIfPresentAsync(directory, name, launcher.OwnerFor(name), null);
            }
            finally { Directory.Delete(directory); }
        }
    }

    private static NativeArtifact Artifact() => NativeArtifact.Admit(Scope,
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", "valid-basic.json")), InspectionPolicy.MaximumNativeBytes);

    private static async Task<T> SignalAsync<T>(Task<T> signal, DockerCreationGateProxy proxy, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(signal, proxy.Failure.Task).WaitAsync(timeout);
        if (completed == proxy.Failure.Task)
            throw new InvalidOperationException("The owned Docker control proxy failed.", await proxy.Failure.Task);
        return await signal;
    }

    private static async Task AssertAbsentAsync(string directory, string nameOrId)
    {
        var state = await DockerAsync(directory, "inspect", "--format", "{{.Id}}", nameOrId);
        Assert.NotEqual(0, state.ExitCode);
        Assert.Equal("", state.Output.Trim());
        Assert.True(IsMissing(state.Error, nameOrId), "Exact-name inspection did not establish absence.");
    }

    private static bool IsMissing(string error, string nameOrId) => error.Trim() is var text &&
        (text == $"Error: No such object: {nameOrId}" || text == $"Error: No such container: {nameOrId}" ||
            text == $"Error response from daemon: No such container: {nameOrId}");

    private static async Task RemoveOwnedIfPresentAsync(string directory, string name, string owner, string? expectedId)
    {
        var target = expectedId ?? name;
        var state = await DockerAsync(directory, "inspect", "--format", DockerWorkerOwnershipTestSupport.InspectFormat, target);
        if (state.ExitCode != 0)
        {
            Assert.Equal("", state.Output.Trim());
            Assert.True(IsMissing(state.Error, target), "Owned cleanup could not establish exact-target absence.");
            return;
        }
        var id = DockerWorkerOwnershipTestSupport.VerifiedId(state.Output, name, owner, expectedId);
        var removal = await DockerAsync(directory, "rm", "--force", "--volumes", id);
        Assert.Equal(0, removal.ExitCode);
        await AssertAbsentAsync(directory, id);
    }

    private static async Task<DockerObservation> DockerAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.Environment.Clear();
        start.Environment["HOME"] = directory;
        start.Environment["DOCKER_CONFIG"] = directory;
        start.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in new[] { "--config", directory, "--host", "unix:///var/run/docker.sock", "container" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned Docker observation did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = CaptureAsync(process.StandardOutput, deadline.Token);
        var error = CaptureAsync(process.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (text.Length + count > 64 * 1024)
                throw new InvalidOperationException("The exact-container observation exceeded its output bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private sealed record DockerObservation(int ExitCode, string Output, string Error);
    private sealed record ObservedCommand(string Operation, string? ContainerName, string? OperationOwner, string? Target);

    /// <summary>Observes real process waits and byte counts; it changes no process/stream/cancellation outcome.</summary>
    private sealed class ObservedRealLauncher : IWorkerProcessLauncher
    {
        private readonly ConcurrentQueue<ObservedCommand> commands = new();
        private long inputByteCount;
        private int firstCreate;
        internal IReadOnlyList<ObservedCommand> Commands => commands.ToArray();
        internal IEnumerable<string> CreatedNames => commands.Where(command => command.Operation == "create").Select(command => command.ContainerName!).Distinct();
        internal string OwnerFor(string name) => commands.Single(command => command.Operation == "create" && command.ContainerName == name).OperationOwner!;
        internal long InputByteCount => Interlocked.Read(ref inputByteCount);
        internal TaskCompletionSource CreateWaitRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CreateWaitCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.Skip(4).ToArray();
            var operation = arguments switch
            {
                ["info", ..] => "info",
                ["image", "inspect", ..] => "image-inspect",
                ["container", "create", ..] => "create",
                ["container", "inspect", "--format", "{{json .}}", ..] => "container-inspect",
                ["container", "inspect", ..] => "ownership-inspect",
                ["container", "start", ..] => "start",
                ["container", "rm", ..] => "rm",
                _ => throw new InvalidOperationException("The observed runner launched an unexpected command.")
            };
            var index = Array.IndexOf(arguments, "--name");
            var label = operation == "create"
                ? arguments.Single(argument => argument.StartsWith(DockerWorkerOwnershipTestSupport.OperationLabel + "=", StringComparison.Ordinal))
                : null;
            commands.Enqueue(new(operation, index < 0 ? null : arguments[index + 1],
                label?[(DockerWorkerOwnershipTestSupport.OperationLabel.Length + 1)..],
                operation is "container-inspect" or "ownership-inspect" or "start" or "rm" ? arguments[^1] : null));
            var observeCreate = operation == "create" && Interlocked.Exchange(ref firstCreate, 1) == 0;
            var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                return new ObservedRealProcess(process,
                    count => Interlocked.Add(ref inputByteCount, count), observeCreate ? this : null);
            }
            catch { process.Dispose(); throw; }
        }
    }

    private sealed class ObservedRealProcess : IWorkerProcess
    {
        private readonly Process process;
        private readonly ObservedRealLauncher? creation;
        private readonly Stream input;
        private int inputClosed;
        internal ObservedRealProcess(Process process, Action<int> written, ObservedRealLauncher? creation)
        {
            this.process = process;
            this.creation = creation;
            input = new CountedInput(process.StandardInput.BaseStream, written);
        }
        public Stream StandardInput => input;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => creation?.CreateWaitCanceled.TrySetResult());
            creation?.CreateWaitRegistered.TrySetResult();
            await process.WaitForExitAsync(cancellationToken);
        }
        public void CloseStandardInput()
        {
            if (Interlocked.Exchange(ref inputClosed, 1) == 0) process.StandardInput.Dispose();
        }
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose()
        {
            try { CloseStandardInput(); }
            finally { process.StandardOutput.Dispose(); process.StandardError.Dispose(); process.Dispose(); }
        }
    }

    private sealed class CountedInput(Stream inner, Action<int> written) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); written(count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            written(buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
