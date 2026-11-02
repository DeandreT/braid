using System.Diagnostics;
using System.Text;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class WorkerDiagnosticTests
{
    [Fact]
    public void Process_start_is_recorded_before_launch_and_failure_text_is_not_copied()
    {
        RuntimeDiagnosticLauncher? diagnostics = null;
        var failure = new IOException("private-start-detail");
        diagnostics = new(new Launcher(_ =>
        {
            Assert.Contains("create:starting", diagnostics!.Summary, StringComparison.Ordinal);
            throw failure;
        }));
        var info = new ProcessStartInfo();
        info.ArgumentList.Add("create");
        info.ArgumentList.Add("private-argument");
        Assert.Same(failure, Assert.Throws<IOException>(() => diagnostics.Start(info)));
        Assert.Contains("create:startFailed:", diagnostics.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private", diagnostics.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_keep_only_stage_and_fixed_error_classifications()
    {
        var fake = new Process();
        var diagnostics = new RuntimeDiagnosticLauncher(new Launcher(_ => fake));
        var info = new ProcessStartInfo();
        info.ArgumentList.Add("--attach");
        using var process = diagnostics.Start(info);
        await process.StandardError.CopyToAsync(Stream.Null);
        process.CloseStandardInput();
        process.Kill();
        await process.WaitForExitAsync(default);
        Assert.Contains("execute:starting", diagnostics.Summary, StringComparison.Ordinal);
        Assert.Contains("execute:started:", diagnostics.Summary, StringComparison.Ordinal);
        Assert.Contains("inputClosed", diagnostics.Summary, StringComparison.Ordinal);
        Assert.Contains("killRequested", diagnostics.Summary, StringComparison.Ordinal);
        Assert.Contains("Out of memory", diagnostics.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private", diagnostics.Summary, StringComparison.Ordinal);
        Assert.True(fake.InputClosed);
        Assert.True(fake.Killed);
    }

    [Fact]
    public async Task Primary_and_all_cleanup_failures_are_preserved_and_the_directory_is_retained()
    {
        var root = Path.Combine(Path.GetTempPath(), "weft-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var order = new List<int>();
        var primary = new IOException("primary");
        var first = new IOException("first cleanup");
        var second = new IOException("second cleanup");
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => FerruleServiceProcessRunnerTests.CompleteCleanupAsync(
                [new Resource(1, first, order), new Resource(2, second, order)], root, primary));
            Assert.Equal(new Exception[] { primary, second, first }, error.InnerExceptions);
            Assert.Equal(new[] { 2, 1 }, order);
            Assert.True(Directory.Exists(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task Successful_cleanup_rethrows_the_original_failure_without_an_aggregate()
    {
        var root = Path.Combine(Path.GetTempPath(), "weft-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var primary = new IOException("primary");
        var order = new List<int>();
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => FerruleServiceProcessRunnerTests.CompleteCleanupAsync(
                [new Resource(1, null, order)], root, primary));
            Assert.Same(primary, error);
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root); }
    }

    private sealed class Launcher(Func<ProcessStartInfo, IWorkerProcess> start) : IWorkerProcessLauncher
    {
        public IWorkerProcess Start(ProcessStartInfo info) => start(info);
    }

    private sealed class Process : IWorkerProcess
    {
        public Stream StandardInput => Stream.Null;
        public Stream StandardOutput => Stream.Null;
        public Stream StandardError { get; } = new MemoryStream(Encoding.UTF8.GetBytes("private worker value: Out of memory"));
        public int ExitCode => 137;
        internal bool InputClosed, Killed;
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void CloseStandardInput() => InputClosed = true;
        public void Kill() => Killed = true;
        public void Dispose() => StandardError.Dispose();
    }

    private sealed class Resource(int id, Exception? failure, List<int> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            order.Add(id);
            if (failure is not null) throw failure;
            return ValueTask.CompletedTask;
        }
    }
}
