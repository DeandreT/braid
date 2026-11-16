using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Braid.Application;

namespace Braid.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    private const string MarkerName = ".braid-private-storage";
    private const string MarkerPrefix = "braid-private-storage-v2\n";
    private const string ControlName = ".braid-private-control";
    private sealed record Marker(Guid RootId, Guid ControlId, LinuxFileSystem.Identity Identity);

    public static FileSystemV2PrivateObjectStore Initialize(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null) => Initialize(options, registryFactory, null);

    public static FileSystemV2PrivateObjectStore Open(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null) => Open(options, registryFactory, null);

    internal static FileSystemV2PrivateObjectStore Initialize(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory, IV2StorageObserver? observer) =>
        OpenConfigured(options, registryFactory, observer, initialize: true);

    internal static FileSystemV2PrivateObjectStore Open(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory, IV2StorageObserver? observer) =>
        OpenConfigured(options, registryFactory, observer, initialize: false);

    private static FileSystemV2PrivateObjectStore OpenConfigured(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? factory, IV2StorageObserver? observer, bool initialize)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            LinuxFileSystem.RequireProfile();
            ValidateEnvironment(options, !initialize);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(options.RootPath)!);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(parent), LinuxFileSystem.EffectiveUserId);
            LinuxFileSystem.RequireLocalFileSystem(parent);
            var leaf = Path.GetFileName(options.RootPath);
            if (initialize && !LinuxFileSystem.CreateDirectory(parent, leaf))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var root = LinuxFileSystem.OpenConfiguredRoot(parent, leaf);
            SafeFileHandle? control = null;
            try
            {
                LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(root), LinuxFileSystem.EffectiveUserId);
                LinuxFileSystem.RequireLocalFileSystem(root);
                if (initialize)
                {
                    var rootId = Guid.NewGuid();
                    var controlId = Guid.NewGuid();
                    ValidateBaseNamespace(root, true);
                    using var controlParent = OpenPrivateDirectory(root, ControlName, true);
                    using var freshControl = OpenPrivateDirectory(controlParent, controlId.ToString("N"), true);
                    ValidateBaseNamespace(freshControl, true);
                    WriteImmutable(freshControl, "control.meta", ControlBytes(rootId, controlId));
                    ProbeCapabilities(freshControl);
                    WriteImmutable(root, MarkerName, Encoding.ASCII.GetBytes(
                        MarkerPrefix + rootId.ToString("N") + "\n" + controlId.ToString("N") + "\n"));
                    LinuxFileSystem.SyncDirectory(root);
                }
                var marker = ReadMarker(root);
                ValidateBaseNamespace(root, false);
                using (var controlParent = OpenPrivateDirectory(root, ControlName, false))
                    control = OpenPrivateDirectory(controlParent, marker.ControlId.ToString("N"), false);
                ValidateBaseNamespace(control, false);
                var meta = ReadSafeBytes(control, "control.meta", 69);
                if (!meta.Bytes.AsSpan().SequenceEqual(ControlBytes(marker.RootId, marker.ControlId)))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var result = new FileSystemV2PrivateObjectStore(options, root, control, marker, meta.Identity, factory, observer);
                result.ValidateRoot();
                return result;
            }
            catch { control?.Dispose(); root.Dispose(); throw; }
        }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
        catch (PrivateObjectStorageException) { throw; }
        catch (ArgumentException) { throw; }
        catch (Exception error) { throw NormalizeUnexpected(error); }
    }

    private static void ValidateEnvironment(FileSystemStorageOptions options, bool rootExists)
    {
        using var application = LinuxFileSystem.OpenAbsoluteDirectory(options.ApplicationContentRoot);
        if (options.RepositoryRoot is { } repository)
        {
            using var repositoryDirectory = LinuxFileSystem.OpenAbsoluteDirectory(repository);
        }
        var ancestor = rootExists ? options.RootPath : Path.GetDirectoryName(options.RootPath)!;
        while (true)
        {
            using var directory = LinuxFileSystem.OpenAbsoluteDirectory(ancestor);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(directory), LinuxFileSystem.EffectiveUserId);
            if (LinuxFileSystem.ContainsGitMarker(directory))
                throw new ArgumentException("Private storage must be outside detected Git repository trees.", nameof(options));
            if (ancestor == "/") return;
            ancestor = Path.GetDirectoryName(ancestor)!;
        }
    }

    private void ValidateRoot()
    {
        try
        {
            ValidateEnvironment(_options, true);
            var held = LinuxFileSystem.Inspect(_root);
            LinuxFileSystem.RequireDirectory(held, _userId);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(_options.RootPath)!);
            using var named = LinuxFileSystem.OpenConfiguredRoot(parent, Path.GetFileName(_options.RootPath));
            LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(named), _userId);
            if (held.Identity != _rootIdentity || LinuxFileSystem.Inspect(named).Identity != _rootIdentity ||
                ReadMarker(_root) != _marker)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            using var controlParent = OpenPrivateDirectory(_root, ControlName, false);
            using var control = OpenPrivateDirectory(controlParent, _marker.ControlId.ToString("N"), false);
            LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(_control), _userId);
            var meta = ReadSafeBytes(_control, "control.meta", 69);
            if (LinuxFileSystem.Inspect(control).Identity != _controlIdentity ||
                LinuxFileSystem.Inspect(_control).Identity != _controlIdentity || meta.Identity != _metaIdentity ||
                !meta.Bytes.AsSpan().SequenceEqual(ControlBytes(_marker.RootId, _marker.ControlId)))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            ValidateBaseNamespace(_root, false);
            ValidateBaseNamespace(_control, false);
        }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
        catch (ArgumentException) { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
    }

    private static Marker ReadMarker(SafeFileHandle root)
    {
        var read = ReadSafeBytes(root, MarkerName, 91);
        var text = Encoding.ASCII.GetString(read.Bytes);
        if (text.Length != 91 || !text.StartsWith(MarkerPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(text.AsSpan(MarkerPrefix.Length, 32), "N", out var rootId) || rootId == Guid.Empty ||
            !Guid.TryParseExact(text.AsSpan(MarkerPrefix.Length + 33, 32), "N", out var controlId) || controlId == Guid.Empty ||
            text != MarkerPrefix + rootId.ToString("N") + "\n" + controlId.ToString("N") + "\n")
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return new(rootId, controlId, read.Identity);
    }

    private static byte[] ControlBytes(Guid rootId, Guid controlId)
    {
        var bytes = new byte[69];
        "BRAIDV2C\n"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9), 1);
        rootId.TryWriteBytes(bytes.AsSpan(11, 16), bigEndian: true, out _);
        controlId.TryWriteBytes(bytes.AsSpan(27, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(43), 1);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(45), FileSystemStorageOptions.NativeHardMaximum);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(53), FileSystemStorageOptions.DerivedHardMaximum);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(61), 65536);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(65), 32768);
        return bytes;
    }

    private static (byte[] Bytes, LinuxFileSystem.Identity Identity) ReadSafeBytes(SafeFileHandle parent,
        string name, int maximum)
    {
        using var file = LinuxFileSystem.OpenRegularRead(parent, name, LinuxFileSystem.FinalFileMode,
            LinuxFileSystem.EffectiveUserId);
        var initial = LinuxFileSystem.Inspect(file);
        if (initial.Size > (ulong)maximum) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var input = new FileStream(file, FileAccess.Read, 4096, isAsync: false);
        var bytes = new byte[checked((int)initial.Size)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var complete = LinuxFileSystem.Inspect(file);
        LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, LinuxFileSystem.EffectiveUserId);
        if (!LinuxFileSystem.TryInspectChild(parent, name, out var named) ||
            named.Identity != initial.Identity || complete.Identity != initial.Identity || complete.Size != initial.Size)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireFile(named, LinuxFileSystem.FinalFileMode, LinuxFileSystem.EffectiveUserId);
        return (bytes, initial.Identity);
    }

    private static void WriteImmutable(SafeFileHandle parent, string name, byte[] bytes)
    {
        using (var file = LinuxFileSystem.CreateTemporary(parent, name))
        {
            LinuxFileSystem.RequireFile(LinuxFileSystem.Inspect(file), LinuxFileSystem.TemporaryFileMode,
                LinuxFileSystem.EffectiveUserId);
            using var output = new FileStream(file, FileAccess.Write, 4096, isAsync: false);
            output.Write(bytes);
            output.Flush();
            LinuxFileSystem.MakeReadOnly(file);
            LinuxFileSystem.SyncFile(file);
        }
        LinuxFileSystem.SyncDirectory(parent);
    }

    private static void ProbeCapabilities(SafeFileHandle parent)
    {
        _ = LinuxFileSystem.ReadIncarnation();
        // This mutating test exists only in explicit Initialize, never Open.
        var name = ".probe-" + Guid.NewGuid().ToString("N");
        if (!LinuxFileSystem.CreateDirectory(parent, name)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var directory = OpenPrivateDirectory(parent, name, false);
        var identity = LinuxFileSystem.Inspect(directory).Identity;
        try
        {
            CreateEmptyGate(directory, "probe.gate");
            using (var first = LinuxFileSystem.TryLockGate(directory, "probe.gate", true, LinuxFileSystem.EffectiveUserId) ??
                throw Failure(PrivateObjectStorageError.Unsupported))
            {
                using var denied = LinuxFileSystem.TryLockGate(directory, "probe.gate", false, LinuxFileSystem.EffectiveUserId);
                if (denied is not null) throw Failure(PrivateObjectStorageError.Unsupported);
            }
            using (var shared = LinuxFileSystem.TryLockGate(directory, "probe.gate", false, LinuxFileSystem.EffectiveUserId) ??
                throw Failure(PrivateObjectStorageError.Unsupported))
            using (var second = LinuxFileSystem.TryLockGate(directory, "probe.gate", false, LinuxFileSystem.EffectiveUserId) ??
                throw Failure(PrivateObjectStorageError.Unsupported)) { shared.Validate(); second.Validate(); }
            WriteImmutable(directory, "first", [0xa5]);
            WriteImmutable(directory, "second", [0x5a]);
            if (!LinuxFileSystem.PublishNoReplace(directory, "first", "final") ||
                LinuxFileSystem.PublishNoReplace(directory, "second", "final") ||
                !ReadSafeBytes(directory, "final", 1).Bytes.AsSpan().SequenceEqual(new byte[] { 0xa5 }))
                throw Failure(PrivateObjectStorageError.Unsupported);
            LinuxFileSystem.SyncDirectory(directory);
        }
        finally
        {
            // Only initializer-created, never admitted protocol probe names are disposable.
            foreach (var child in new[] { "probe.gate", "first", "second", "final" })
                if (LinuxFileSystem.TryInspectChild(directory, child, out var stat))
                    LinuxFileSystem.UnlinkOwned(directory, child, stat.Identity);
            LinuxFileSystem.UnlinkOwned(parent, name, identity, directory: true);
        }
    }
}
