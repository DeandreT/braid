using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

/// <summary>Retains fixed diagnostic classifications only, never a worker log line or payload.</summary>
internal sealed class RuntimeDiagnosticLauncher(IWorkerProcessLauncher? launcher = null) : IWorkerProcessLauncher
{
    private readonly IWorkerProcessLauncher actual = launcher ?? new WorkerProcessLauncher();
    private readonly ConcurrentDictionary<string, byte> kinds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> stages = new();
    private int exitCode = int.MinValue;
    internal string Summary => $"worker exit={exitCode}; classes={string.Join(",", kinds.Keys.Order(StringComparer.Ordinal))}; stages={string.Join(",", stages)}";
    public IWorkerProcess Start(ProcessStartInfo info)
    {
        var elapsed = Stopwatch.StartNew();
        var stage = info.ArgumentList.Contains("--attach") ? "execute" : info.ArgumentList.Contains("create") ? "create" :
            info.ArgumentList.Contains("rm") ? "remove" : info.ArgumentList.Contains("info") ? "daemon" :
            info.ArgumentList.Contains("image") ? "image" : "inspect";
        Record($"{stage}:starting");
        try
        {
            var process = actual.Start(info);
            Record($"{stage}:started:{elapsed.ElapsedMilliseconds}ms");
            return new Observed(process, this, stage, elapsed);
        }
        catch
        {
            Record($"{stage}:startFailed:{elapsed.ElapsedMilliseconds}ms");
            throw;
        }
    }
    private void Record(string stage)
    {
        stages.Enqueue(stage);
        while (stages.Count > 64) stages.TryDequeue(out _);
    }
    private sealed class Observed(IWorkerProcess inner, RuntimeDiagnosticLauncher owner, string stage, Stopwatch elapsed) : IWorkerProcess
    {
        private readonly Stream error = new ClassifiedStream(inner.StandardError, owner);
        public Stream StandardInput => inner.StandardInput;
        public Stream StandardOutput => inner.StandardOutput;
        public Stream StandardError => error;
        public int ExitCode => inner.ExitCode;
        public async Task WaitForExitAsync(CancellationToken token)
        {
            try
            {
                await inner.WaitForExitAsync(token);
                if (stage == "execute") Interlocked.Exchange(ref owner.exitCode, inner.ExitCode);
                owner.Record($"{stage}:{elapsed.ElapsedMilliseconds}ms:exit{inner.ExitCode}");
            }
            catch (OperationCanceledException) { owner.Record($"{stage}:{elapsed.ElapsedMilliseconds}ms:canceled"); throw; }
        }
        public void CloseStandardInput()
        {
            inner.CloseStandardInput();
            owner.Record($"{stage}:{elapsed.ElapsedMilliseconds}ms:inputClosed");
        }
        public void Kill()
        {
            owner.Record($"{stage}:{elapsed.ElapsedMilliseconds}ms:killRequested");
            inner.Kill();
        }
        public void Dispose() => inner.Dispose();
    }
    private sealed class ClassifiedStream(Stream inner, RuntimeDiagnosticLauncher owner) : Stream
    {
        private string tail = "";
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer, token);
            var text = tail + Encoding.UTF8.GetString(buffer.Span[..count]);
            foreach (var kind in new[] { "WeftCompiledRuntimeFailed:request", "WeftCompiledRuntimeFailed:decode", "WeftCompiledRuntimeFailed:invocation", "WeftCompiledRuntimeFailed:response",
                "Out of memory", "Failed to create CoreCLR", "Permission denied", "Read-only file system", "Too many open files" })
                if (text.Contains(kind, StringComparison.OrdinalIgnoreCase)) owner.kinds.TryAdd(kind, 0);
            tail = text[^Math.Min(text.Length, 128)..];
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
