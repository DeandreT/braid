using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Owned v1 fixtures and independent physical observations, never an offline-process oracle.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageActivationFixture : IDisposable
{
    internal PrivateStorageTestRoot Root { get; } = new();
    internal List<(PrivateObjectReference Reference, byte[] Bytes, PrivatePublicationInodeIdentity Identity)> Finals { get; } = [];
    internal byte[] MarkerBytes { get; private set; } = [];
    internal PrivatePublicationInodeIdentity MarkerIdentity { get; private set; } = default!;
    internal Guid MarkerRootId { get; private set; }
    internal string? LegacyAttempt { get; private set; }
    private byte[]? legacyBytes;
    private PrivatePublicationInodeIdentity? legacyIdentity;

    private PrivateStorageActivationFixture() { }

    internal static async Task<PrivateStorageActivationFixture> CreateAsync(bool legacySentinel = true)
    {
        var fixture = new PrivateStorageActivationFixture();
        try
        {
            using (var store = fixture.Root.Initialize())
            {
                foreach (var kind in new[] { PrivateObjectClass.NativeArtifact, PrivateObjectClass.DerivedArtifact })
                {
                    var bytes = new byte[] { 0, 255, 128, 13, 10 }.Concat(PrivateStorageData.Pattern(
                        kind == PrivateObjectClass.NativeArtifact ? 4099 : 65539)).ToArray();
                    var reference = PrivateStorageData.Reference(bytes, kind);
                    Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(
                        reference.Id.Scope, reference, new MemoryStream(bytes)));
                    fixture.Finals.Add((reference, bytes, PrivateStorageV2Identity.Read(fixture.Root.FinalPath(reference))));
                }
            }
            // These are authored unknown legacy bytes, not a claim that a real
            // historical writer is stopped. Process tests also retain a real killed attempt.
            if (legacySentinel)
            {
                fixture.LegacyAttempt = Path.Combine(Path.GetDirectoryName(fixture.Root.FinalPath(fixture.Finals[0].Reference))!,
                    $".attempt-{Guid.NewGuid():N}.tmp");
                fixture.legacyBytes = "unknown legacy attempt\0\xff"u8.ToArray();
                File.WriteAllBytes(fixture.LegacyAttempt, fixture.legacyBytes);
                File.SetUnixFileMode(fixture.LegacyAttempt, PrivateStorageTestRoot.AttemptMode);
                fixture.legacyIdentity = PrivateStorageV2Identity.Read(fixture.LegacyAttempt);
            }
            var original = File.ReadAllBytes(fixture.Root.MarkerPath);
            var lines = Encoding.ASCII.GetString(original).Split('\n');
            Assert.Equal(58, original.Length);
            Assert.Equal("braid-private-storage-v1", lines[0]);
            fixture.MarkerBytes = original;
            fixture.MarkerIdentity = PrivateStorageV2Identity.Read(fixture.Root.MarkerPath);
            fixture.MarkerRootId = Guid.ParseExact(lines[1], "N");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    internal void AssertPreserved()
    {
        foreach (var final in Finals)
        {
            var path = Root.FinalPath(final.Reference);
            Assert.Equal(final.Bytes, File.ReadAllBytes(path));
            Assert.Equal(final.Identity, PrivateStorageV2Identity.Read(path));
            Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(path));
            Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(path).Uid);
            Assert.Equal(1u, PrivateStorageLinux.Metadata(path).Links);
        }
        if (LegacyAttempt is { } attempt)
        {
            Assert.Equal(legacyBytes, File.ReadAllBytes(attempt));
            Assert.Equal(legacyIdentity, PrivateStorageV2Identity.Read(attempt));
            Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(attempt));
        }
    }

    internal async Task AssertReadableAsync()
    {
        using var store = FileSystemV2PrivateObjectStore.Open(Root.Options,
            () => throw new InvalidOperationException("Reads and activation do not resolve publication metadata."));
        foreach (var final in Finals)
            Assert.Equal(final.Bytes, await store.ReadVerifiedAsync(final.Reference.Id.Scope, final.Reference));
    }

    public void Dispose() => Root.Dispose();
}

[SupportedOSPlatform("linux")]
internal static class PrivateStorageActivationData
{
    internal const int ReceiptSize = 632;
    internal static string DirectoryPath(PrivateStorageTestRoot root) => Path.Combine(root.RootPath, ".braid-private-storage.activation");
    internal static string ReceiptPath(PrivateStorageTestRoot root) => Path.Combine(DirectoryPath(root), "receipt");
    internal static string CounterpartPath(PrivateStorageTestRoot root) => Path.Combine(DirectoryPath(root), "marker");
    internal static string GatePath(PrivateStorageTestRoot root) => Path.Combine(DirectoryPath(root), "gate");
    internal static string StagedControlPath(PrivateStorageTestRoot root) => Path.Combine(DirectoryPath(root), "control");
    internal static string InstalledControlPath(PrivateStorageTestRoot root) => Path.Combine(root.RootPath, ".braid-private-control");

    internal static Guid ReadId(byte[] receipt, int offset) => new(receipt.AsSpan(offset, 16), bigEndian: true);

    internal static void AssertSealed(PrivateStorageActivationFixture fixture, bool installed, bool active = false)
    {
        var root = fixture.Root;
        var receipt = File.ReadAllBytes(ReceiptPath(root));
        Assert.Equal(ReceiptSize, receipt.Length);
        Assert.Equal("BRAIDV2A\n"u8.ToArray(), receipt[..9]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(receipt.AsSpan(9)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(receipt.AsSpan(11)));
        Assert.Equal((uint)ReceiptSize, BinaryPrimitives.ReadUInt32BigEndian(receipt.AsSpan(13)));
        Assert.NotEqual(Guid.Empty, ReadId(receipt, 17));
        Assert.Equal(fixture.MarkerRootId, ReadId(receipt, 33));
        var controlId = ReadId(receipt, 49);
        Assert.NotEqual(Guid.Empty, controlId);
        Assert.Equal((byte)1, receipt[101]);
        Assert.Equal(fixture.MarkerBytes, receipt[318..376]);
        Assert.Equal(Sha256Digest.FromBytes(fixture.MarkerBytes).Value,
            Convert.ToHexStringLower(receipt[376..408]));
        var activeMarker = Encoding.ASCII.GetBytes($"braid-private-storage-v2\n{fixture.MarkerRootId:N}\n{controlId:N}\n");
        Assert.Equal(activeMarker, receipt[408..499]);
        Assert.Equal(Sha256Digest.FromBytes(activeMarker).Value, Convert.ToHexStringLower(receipt[499..531]));
        var control = installed ? InstalledControlPath(root) : StagedControlPath(root);
        Assert.True(Directory.Exists(control));
        Assert.False(Directory.Exists(installed ? StagedControlPath(root) : InstalledControlPath(root)));
        var controlNamespace = Path.Combine(control, controlId.ToString("N"));
        Assert.Equal(new[] { controlNamespace }, Directory.GetDirectories(control));
        var meta = Path.Combine(controlNamespace, "control.meta");
        Assert.Equal(receipt[531..600], File.ReadAllBytes(meta));
        Assert.Equal("BRAIDV2C\n"u8.ToArray(), receipt[531..540]);
        Assert.Equal(Sha256Digest.FromBytes(receipt[531..600]).Value, Convert.ToHexStringLower(receipt[600..632]));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(meta));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(ReceiptPath(root)));
        Assert.False(File.Exists(Path.Combine(DirectoryPath(root), "receipt.stage")));
        Assert.Equal(0, new FileInfo(GatePath(root)).Length);
        Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(GatePath(root)));
        var oldMarker = active ? CounterpartPath(root) : root.MarkerPath;
        var newMarker = active ? root.MarkerPath : CounterpartPath(root);
        Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(oldMarker));
        Assert.Equal(fixture.MarkerIdentity, PrivateStorageV2Identity.Read(oldMarker));
        Assert.Equal(activeMarker, File.ReadAllBytes(newMarker));
        var paths = new[] { root.RootPath, DirectoryPath(root), GatePath(root), control, controlNamespace,
            meta, oldMarker, newMarker, ReceiptPath(root) };
        for (var index = 0; index < paths.Length; index++)
        {
            var offset = 102 + index * 24;
            var expected = new PrivatePublicationInodeIdentity(
                BinaryPrimitives.ReadUInt64BigEndian(receipt.AsSpan(offset)),
                BinaryPrimitives.ReadUInt32BigEndian(receipt.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt32BigEndian(receipt.AsSpan(offset + 12)),
                BinaryPrimitives.ReadUInt64BigEndian(receipt.AsSpan(offset + 16)));
            Assert.Equal(expected, PrivateStorageV2Identity.Read(paths[index]));
        }
        fixture.AssertPreserved();
    }

    internal static void Tamper(PrivateStorageTestRoot root, string target)
    {
        var control = Directory.Exists(InstalledControlPath(root)) ? InstalledControlPath(root) : StagedControlPath(root);
        var controlNamespace = Directory.Exists(control) ? Assert.Single(Directory.GetDirectories(control)) : "";
        switch (target)
        {
            case "receipt": ReplaceFile(root, ReceiptPath(root)); break;
            case "marker": ReplaceFile(root, CounterpartPath(root)); break;
            case "rootMarker": ReplaceFile(root, root.MarkerPath); break;
            case "gate": ReplaceFile(root, GatePath(root)); break;
            case "meta": ReplaceFile(root, Path.Combine(controlNamespace, "control.meta")); break;
            case "control": ReplaceDirectory(root, control); break;
            case "controlNamespace": ReplaceDirectory(root, controlNamespace); break;
            case "activation": ReplaceDirectory(root, DirectoryPath(root)); break;
            case "root": ReplaceDirectory(root, root.RootPath); break;
            case "missingReceipt": File.Delete(ReceiptPath(root)); break;
            case "missingGate": File.Delete(GatePath(root)); break;
            case "missingMarker": File.Delete(CounterpartPath(root)); break;
            case "missingControl":
                Directory.Move(control, root.NewPath("displaced-control-" + Guid.NewGuid().ToString("N")));
                break;
            case "markerContent": RewriteFile(CounterpartPath(root)); break;
            case "rootMarkerContent": RewriteFile(root.MarkerPath); break;
            case "metaContent": RewriteFile(Path.Combine(controlNamespace, "control.meta")); break;
            case "gateContent": RewriteFile(GatePath(root)); break;
            case "controlUnknown":
                File.WriteAllBytes(Path.Combine(control, "unknown-control-evidence"), [0x7f]);
                break;
            case "unknown":
                File.WriteAllBytes(Path.Combine(DirectoryPath(root), "unknown-evidence"), [0x7f]);
                break;
            case "bothControls":
                Directory.CreateDirectory(StagedControlPath(root));
                File.SetUnixFileMode(StagedControlPath(root), PrivateStorageTestRoot.DirectoryMode);
                break;
            default:
                var receipt = File.ReadAllBytes(ReceiptPath(root));
                switch (target)
                {
                    case "magic": receipt[0] ^= 1; break;
                    case "version": receipt[10] ^= 2; break;
                    case "boot": receipt[65] ^= 1; break;
                    case "namespaceDevice": receipt[81] ^= 1; break;
                    case "namespaceInode": receipt[89] ^= 1; break;
                    case "uid": receipt[97] ^= 1; break;
                    case "ack": receipt[101] = 0; break;
                    case "rootIdentity": receipt[102] ^= 1; break;
                    case "receiptIdentity": receipt[294] ^= 1; break;
                    case "truncated": receipt = receipt[..^1]; break;
                    case "trailing": receipt = [.. receipt, 0x7f]; break;
                    default: throw new InvalidOperationException("Unknown owned evidence mutation.");
                }
                File.SetUnixFileMode(ReceiptPath(root), PrivateStorageTestRoot.AttemptMode);
                File.WriteAllBytes(ReceiptPath(root), receipt);
                File.SetUnixFileMode(ReceiptPath(root), PrivateStorageTestRoot.FinalMode);
                break;
        }
    }

    private static void ReplaceFile(PrivateStorageTestRoot root, string path)
    {
        var length = new FileInfo(path).Length;
        // Empty gates can be exclusively locked by the activation being observed.
        byte[] bytes = length == 0 ? [] : File.ReadAllBytes(path);
        Assert.Equal(length, bytes.LongLength);
        var mode = File.GetUnixFileMode(path);
        File.Move(path, root.NewPath("displaced-file-" + Guid.NewGuid().ToString("N")));
        File.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, mode);
    }

    private static void RewriteFile(string path)
    {
        var mode = File.GetUnixFileMode(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) bytes = [0x7f];
        else bytes[0] ^= 1;
        File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode);
        File.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, mode);
    }

    private static void ReplaceDirectory(PrivateStorageTestRoot root, string path)
    {
        var displaced = root.NewPath("displaced-directory-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, displaced);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.DirectoryMode);
        foreach (var entry in Directory.EnumerateFileSystemEntries(displaced))
        {
            var destination = Path.Combine(path, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageActivationSnapshot
{
    private sealed record Entry(string Path, bool Directory, PrivatePublicationInodeIdentity Identity,
        UnixFileMode Mode, uint Uid, uint Links, long Length, byte[] Bytes);
    private readonly Entry[] entries;
    private PrivateStorageActivationSnapshot(Entry[] entries) => this.entries = entries;

    internal static PrivateStorageActivationSnapshot Capture(string root)
    {
        var paths = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root)
            .Order(StringComparer.Ordinal);
        return new(paths.Select(path =>
        {
            var metadata = PrivateStorageLinux.Metadata(path);
            var directory = Directory.Exists(path);
            var length = directory ? 0 : new FileInfo(path).Length;
            // Empty gates may carry an actual exclusive flock in a live child.
            // Stat proves their empty content without a conflicting .NET read-open.
            byte[] bytes = length == 0 ? [] : File.ReadAllBytes(path);
            Assert.Equal(length, bytes.LongLength);
            if (!directory) Assert.Equal(length, new FileInfo(path).Length);
            return new Entry(Path.GetRelativePath(root, path), directory, PrivateStorageV2Identity.Read(path),
                File.GetUnixFileMode(path), metadata.Uid, metadata.Links, length, bytes);
        }).ToArray());
    }

    internal void AssertUnchanged(string root)
    {
        var actual = Capture(root).entries;
        Assert.Equal(entries.Select(entry => entry.Path), actual.Select(entry => entry.Path));
        for (var index = 0; index < entries.Length; index++)
        {
            Assert.Equal(entries[index].Directory, actual[index].Directory);
            Assert.Equal(entries[index].Identity, actual[index].Identity);
            Assert.Equal(entries[index].Mode, actual[index].Mode);
            Assert.Equal(entries[index].Uid, actual[index].Uid);
            Assert.Equal(entries[index].Links, actual[index].Links);
            Assert.Equal(entries[index].Length, actual[index].Length);
            Assert.Equal(entries[index].Bytes, actual[index].Bytes);
        }
    }
}

internal sealed class PrivateStorageActivationObserver(Func<V2ActivationObservation, CancellationToken, ValueTask> observe) : IV2ActivationObserver
{
    public ValueTask ObserveAsync(V2ActivationObservation observation, CancellationToken token) => observe(observation, token);
}

/// <summary>Owned independent v2 protocol child; all private inputs use bounded stdin only.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageActivationProbe : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(180));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string privateRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private PrivateStorageActivationProbe(Process process, string privateRoot)
    {
        this.process = process;
        this.privateRoot = privateRoot;
        output = CaptureAsync(process.StandardOutput, 4096, deadline.Token);
        error = CaptureAsync(process.StandardError, 4096, deadline.Token);
    }

    internal static string Command(PrivateStorageTestRoot root, string? gateDirectory = null,
        string? gateStage = null, bool offlineAcknowledged = true) => JsonSerializer.Serialize(new
        {
            Operation = "activate",
            root.RootPath,
            ApplicationContentRoot = root.ApplicationRoot,
            RegistryMode = "syntheticPhysical",
            OfflineAcknowledged = offlineAcknowledged,
            GateDirectory = gateDirectory,
            GateStage = gateStage
        }, JsonOptions);

    internal static Task<PrivateStorageActivationProbe> StartAsync(PrivateStorageTestRoot root,
        string? gateDirectory = null, string? gateStage = null, bool offlineAcknowledged = true) =>
        StartRawAsync(root, Command(root, gateDirectory, gateStage, offlineAcknowledged));

    internal static async Task<PrivateStorageActivationProbe> StartRawAsync(PrivateStorageTestRoot root,
        string command, string? extraArgument = null)
    {
        var dll = Environment.GetEnvironmentVariable("BRAID_STORAGE_PROTOCOL_PROBE_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "StorageProtocolProbe", "Braid.StorageProtocolProbe.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The protocol probe must be copied into the test output.");
        var start = new ProcessStartInfo(Host())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        if (extraArgument is not null) start.ArgumentList.Add(extraArgument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned activation probe did not start.");
        var probe = new PrivateStorageActivationProbe(process, root.RootPath);
        try
        {
            if (extraArgument is null)
                await process.StandardInput.WriteAsync(command.AsMemory(), probe.deadline.Token);
            process.StandardInput.Close();
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    internal async Task WaitReadyAsync(string directory)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = Path.Combine(directory, "ready");
        while (!File.Exists(ready))
        {
            if (process.HasExited)
            {
                var result = await CompleteAsync();
                throw new InvalidOperationException($"The owned activation child exited before readiness ({result.Status}/{result.Error}).");
            }
            await Task.Delay(20, wait.Token);
        }
        Assert.Equal("ready", await File.ReadAllTextAsync(ready, wait.Token));
        Assert.False(process.HasExited);
    }

    internal static Task ReleaseAsync(string directory) => PrivateStorageV2Probe.ReleaseAsync(directory);

    internal async Task<PrivateStorageActivationProbeResult> CompleteAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
        var stdout = await output;
        Assert.DoesNotContain(privateRoot, stdout, StringComparison.Ordinal);
        Assert.Empty(await error);
        using var document = JsonDocument.Parse(stdout);
        var value = document.RootElement;
        Assert.Equal(0, value.GetProperty("sourceReadCalls").GetInt64());
        Assert.Equal(0, value.GetProperty("sourceBytes").GetInt64());
        Assert.Equal(0, value.GetProperty("registryFactoryResolutions").GetInt32());
        Assert.Equal(0, value.GetProperty("reserveCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("bindCalls").GetInt32());
        Assert.Equal(0, value.GetProperty("getCalls").GetInt32());
        Assert.False(value.GetProperty("kernelFlockDenied").GetBoolean());
        return new(process.ExitCode, value.GetProperty("status").GetString()!,
            value.TryGetProperty("error", out var failure) ? failure.GetString() : null);
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

    internal static string Host()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(host) ? host :
            Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
    }

    internal static async Task<string> CaptureAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > maximum)
                throw new InvalidOperationException("The owned activation command exceeded categorical output bounds.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

internal sealed record PrivateStorageActivationProbeResult(int ExitCode, string Status, string? Error);
