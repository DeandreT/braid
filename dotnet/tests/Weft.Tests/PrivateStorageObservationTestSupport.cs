using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Authored physical layouts only; no publication metadata or deletion authority is fabricated.</summary>
[SupportedOSPlatform("linux")]
internal static class PrivateStorageObservationTestSupport
{
    internal static string ProjectPath(PrivateStorageTestRoot root, ProjectScope project,
        PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact) => Path.Combine(root.RootPath,
            "objects", objectClass == PrivateObjectClass.NativeArtifact ? "native" : "derived",
            project.OrganizationId.ToString("N"), project.ProjectId.ToString("N"));

    internal static string Directory(string path)
    {
        if (System.IO.Directory.Exists(path)) return path;
        // A deep CreateDirectory(mode) applies mode only to its leaf. Establish
        // each missing fixture component without changing existing ancestors.
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("An owned fixture directory requires a parent.");
        if (!System.IO.Directory.Exists(parent)) Directory(parent);
        System.IO.Directory.CreateDirectory(path, PrivateStorageTestRoot.DirectoryMode);
        Assert.Equal(PrivateStorageTestRoot.DirectoryMode, System.IO.File.GetUnixFileMode(path));
        return path;
    }

    internal static (RevisionScope Scope, string Path) Revision(PrivateStorageTestRoot root, ProjectScope project,
        PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact)
    {
        var scope = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
        return (scope, Directory(Path.Combine(ProjectPath(root, project, objectClass),
            scope.MappingId.ToString("N"), scope.RevisionId.ToString("N"))));
    }

    internal static string File(string directory, string name, byte[]? bytes = null,
        UnixFileMode mode = PrivateStorageTestRoot.FinalMode)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.WriteAllBytes(path, bytes ?? [0x5a, 0, 0xff]);
        System.IO.File.SetUnixFileMode(path, mode);
        return path;
    }

    internal static Dictionary<string, (PrivatePublicationInodeIdentity Identity, ushort Mode, byte[] Bytes)> Snapshot(string root)
    {
        var result = new Dictionary<string, (PrivatePublicationInodeIdentity, ushort, byte[])>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var metadata = PrivateStorageLinux.Metadata(path);
            result.Add(Path.GetRelativePath(root, path), (PrivateStorageV2Identity.Read(path), metadata.Mode,
                (metadata.Mode & 0xf000) == 0x8000 ? System.IO.File.ReadAllBytes(path) : []));
        }
        return result;
    }

    internal static void SameSnapshot(Dictionary<string, (PrivatePublicationInodeIdentity Identity, ushort Mode, byte[] Bytes)> before,
        Dictionary<string, (PrivatePublicationInodeIdentity Identity, ushort Mode, byte[] Bytes)> after)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, facts) in before)
        {
            Assert.Equal(facts.Identity, after[name].Identity);
            Assert.Equal(facts.Mode, after[name].Mode);
            Assert.Equal(facts.Bytes, after[name].Bytes);
        }
    }
}

internal sealed class PrivateStorageObservationHook(
    Func<PrivateStorageObservationPhase, int, CancellationToken, ValueTask<bool>> callback) : IPrivateStorageObservationHook
{
    public ValueTask<bool> AfterEntryAsync(PrivateStorageObservationPhase phase, int ordinal,
        CancellationToken cancellationToken) => callback(phase, ordinal, cancellationToken);
}

/// <summary>Independent raw-name fixture creation/inspection; cleanup retains the original native bytes.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageObservationRawFile : IDisposable
{
    private readonly SafeFileHandle _parent;
    private readonly byte[] _name;
    internal PrivatePublicationInodeIdentity Identity { get; }

    internal PrivateStorageObservationRawFile(string parent, byte[] rawName)
    {
        var descriptor = OpenDirectory(parent, 0x10000 | 0x20000 | 0x80000);
        if (descriptor < 0) throw new IOException("The owned raw-name parent could not be opened.");
        _parent = new((IntPtr)descriptor, true);
        _name = new byte[rawName.Length + 1];
        rawName.CopyTo(_name, 0);
        try
        {
            var file = OpenFile(descriptor, _name, 1 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180);
            if (file < 0) throw new IOException("The owned raw-name file could not be created.");
            using (var handle = new SafeFileHandle((IntPtr)file, true))
            using (var output = new FileStream(handle, FileAccess.Write)) output.WriteByte(0x7d);
            if (Inspect(descriptor, _name, 0x100, 0x17ff, out var stat) != 0)
                throw new IOException("The owned raw-name identity could not be inspected.");
            Identity = new(stat.Inode, stat.DeviceMajor, stat.DeviceMinor, stat.MountId);
        }
        catch { _ = Remove(descriptor, _name, 0); _parent.Dispose(); throw; }
    }

    public void Dispose()
    {
        var result = Remove(checked((int)_parent.DangerousGetHandle()), _name, 0);
        _parent.Dispose();
        if (result != 0) throw new IOException("The owned raw-name file could not be removed.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct RawStat
    {
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenFile(int directory, [In] byte[] path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Inspect(int directory, [In] byte[] path, int flags, uint mask, out RawStat stat);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int Remove(int directory, [In] byte[] path, int flags);
}
