using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageTestRoot : IDisposable
{
    internal const int NativeLimit = 4 * 1024 * 1024;
    internal const int DerivedLimit = 16 * 1024 * 1024;
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode FinalMode = UnixFileMode.UserRead;
    internal const UnixFileMode AttemptMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal string BasePath { get; } = Path.Combine(Path.GetTempPath(), $"weft-storage-tests-{Guid.NewGuid():N}");
    internal string RootPath => Path.Combine(BasePath, "store");
    internal string ApplicationRoot => Path.Combine(BasePath, "application");
    internal string MarkerPath => Path.Combine(RootPath, ".weft-private-storage");
    internal FileSystemStorageOptions Options => new(RootPath, ApplicationRoot);

    internal PrivateStorageTestRoot()
    {
        Directory.CreateDirectory(BasePath);
        File.SetUnixFileMode(BasePath, DirectoryMode);
        Directory.CreateDirectory(ApplicationRoot);
        File.SetUnixFileMode(ApplicationRoot, DirectoryMode);
    }

    internal FileSystemPrivateObjectStore Initialize() => FileSystemPrivateObjectStore.Initialize(Options);
    internal FileSystemPrivateObjectStore Open() => FileSystemPrivateObjectStore.Open(Options);
    internal string NewPath(string name) => Path.Combine(BasePath, name);
    internal string NewDirectory(string name)
    {
        var path = NewPath(name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, DirectoryMode);
        return path;
    }

    internal string FinalPath(PrivateObjectReference reference, string? storageRoot = null) => Path.Combine(storageRoot ?? RootPath, "objects",
        reference.Id.Class == PrivateObjectClass.NativeArtifact ? "native" : "derived",
        reference.Id.Scope.OrganizationId.ToString("N"), reference.Id.Scope.ProjectId.ToString("N"),
        reference.Id.Scope.MappingId.ToString("N"), reference.Id.Scope.RevisionId.ToString("N"),
        $"{reference.Id.ObjectId:N}.blob");

    internal string[] AttemptFiles() => Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
        .Where(path => path != MarkerPath && !path.EndsWith(".blob", StringComparison.Ordinal)).ToArray();

    public void Dispose() => Directory.Delete(BasePath, recursive: true);
}

internal static class PrivateStorageData
{
    internal static RevisionScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    internal static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = unchecked((byte)((index * 31) + 7));
        return bytes;
    }

    internal static PrivateObjectReference Reference(byte[] bytes, PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact,
        RevisionScope? scope = null, Guid? objectId = null) => new(
            objectId is Guid id ? PrivateObjectId.Restore(scope ?? Scope(), id, objectClass) : PrivateObjectId.Create(scope ?? Scope(), objectClass),
            Sha256Digest.FromBytes(bytes), bytes.LongLength);

    internal static async Task ErrorAsync(PrivateObjectStorageError expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<PrivateObjectStorageException>(action);
        Assert.Equal(expected, error.Error);
    }

    internal static void Error(PrivateObjectStorageError expected, Action action)
    {
        var error = Assert.Throws<PrivateObjectStorageException>(action);
        Assert.Equal(expected, error.Error);
    }
}

/// <summary>Cannot supply Length/Seek; records the adapter's real streaming reads.</summary>
internal sealed class PrivateStorageInput(byte[] bytes, int chunkSize = int.MaxValue,
    int? throwAfter = null, Action<int>? afterRead = null) : Stream
{
    private int offset;
    internal int ReadCalls { get; private set; }
    internal int MaximumRequested { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ReadCore(buffer.Span, cancellationToken));

    private int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
        if (throwAfter is int threshold && offset >= threshold)
            throw new IOException("Synthetic input failure.");
        var count = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - offset);
        bytes.AsSpan(offset, count).CopyTo(buffer);
        offset += count;
        afterRead?.Invoke(offset);
        return count;
    }
}

internal sealed class PrivateStorageGatedInput(byte[] bytes) : Stream
{
    private readonly PrivateStorageInput input = new(bytes);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ReadCalls => input.ReadCalls;
    internal void Release() => release.TrySetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return await input.ReadAsync(buffer, cancellationToken);
    }
}

[SupportedOSPlatform("linux")]
internal static class PrivateStorageLinux
{
    internal static uint EffectiveUid => GetEffectiveUid();
    internal static (uint Uid, ushort Mode, uint Links) Metadata(string path)
    {
        if (Statx(-100, path, 0x100, 0x7ff, out var metadata) != 0)
            throw new IOException("Could not inspect owned test filesystem metadata.");
        return (metadata.Uid, metadata.Mode, metadata.Links);
    }

    internal static void HardLink(string existing, string link)
    {
        if (Link(existing, link) != 0)
            throw new IOException("Could not create an owned test hard-link alias.");
    }

    internal static void Fifo(string path)
    {
        if (MkFifo(path, 0x180) != 0)
            throw new IOException("Could not create an owned test FIFO.");
    }

    internal static void SetOwner(string path, uint uid)
    {
        if (Chown(path, uid, uint.MaxValue) != 0)
            throw new IOException("Could not change the ownership of an owned test path.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxData metadata);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string link);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
}

[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageProbe : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string privateRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private PrivateStorageProbe(Process process, string privateRoot)
    {
        this.process = process;
        this.privateRoot = privateRoot;
        output = CaptureAsync(process.StandardOutput, 512 * 1024, deadline.Token);
        error = CaptureAsync(process.StandardError, 64 * 1024, deadline.Token);
    }

    internal static async Task<PrivateStorageProbe> StartAsync(PrivateStorageTestRoot root,
        PrivateObjectReference reference, string operation, byte[]? bytes = null, int? patternLength = null,
        string? gateDirectory = null, string? gateStage = null)
    {
        var dll = Environment.GetEnvironmentVariable("WEFT_STORAGE_PROBE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "StorageProbe", "Weft.StorageProbe.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException("The test-only storage probe and dependencies must be copied to the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
        {
            var current = Environment.ProcessPath;
            host = current is not null && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        }
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned storage probe did not start.");
        var probe = new PrivateStorageProbe(process, root.RootPath);
        try
        {
            var scope = reference.Id.Scope;
            var command = new
            {
                Operation = operation,
                RootPath = root.RootPath,
                ApplicationContentRoot = root.ApplicationRoot,
                OrganizationId = scope.OrganizationId,
                ProjectId = scope.ProjectId,
                MappingId = scope.MappingId,
                RevisionId = scope.RevisionId,
                ObjectId = reference.Id.ObjectId,
                ObjectClass = reference.Id.Class.ToString(),
                Sha256 = reference.Digest.Value,
                reference.ByteCount,
                Base64Content = bytes is null ? null : Convert.ToBase64String(bytes),
                PatternLength = patternLength,
                GateDirectory = gateDirectory,
                GateStage = gateStage
            };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), probe.deadline.Token);
            process.StandardInput.Close();
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async Task WaitReadyAsync(string gateDirectory)
    {
        using var gateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(Path.Combine(gateDirectory, "ready")))
        {
            if (process.HasExited)
            {
                var response = await CompleteAsync();
                throw new InvalidOperationException($"The owned probe exited before its gate ({response.Status}/{response.Error}).");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), gateDeadline.Token);
        }
        Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(gateDirectory, "ready"), gateDeadline.Token));
        Assert.False(process.HasExited);
    }

    internal static Task ReleaseAsync(string gateDirectory) => File.WriteAllTextAsync(Path.Combine(gateDirectory, "release"), "release");

    internal async Task<PrivateStorageProbeResult> CompleteAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
        var stdout = await output;
        Assert.DoesNotContain(privateRoot, stdout, StringComparison.Ordinal);
        Assert.Empty(await error);
        using var document = JsonDocument.Parse(stdout);
        var body = document.RootElement;
        return new(process.ExitCode, body.GetProperty("status").GetString()!,
            body.TryGetProperty("error", out var failure) ? failure.GetString() : null,
            body.TryGetProperty("byteCount", out var length) ? length.GetInt64() : null,
            body.TryGetProperty("sha256", out var digest) ? digest.GetString() : null,
            body.TryGetProperty("base64Content", out var bytes) ? bytes.GetString() : null);
    }

    internal async Task KillAsync()
    {
        Assert.False(process.HasExited);
        process.Kill(entireProcessTree: true);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(reap.Token);
        Assert.NotEqual(0, process.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { deadline.Dispose(); process.Dispose(); }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, int ceiling, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + read > ceiling)
                throw new InvalidOperationException("The owned storage probe exceeded its output bound.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}

internal sealed record PrivateStorageProbeResult(int ExitCode, string Status, string? Error,
    long? ByteCount, string? Sha256, string? Base64Content);
