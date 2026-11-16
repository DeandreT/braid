using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

internal static partial class LinuxFileSystem
{
    private const int Interrupted = 4;
    private const int WouldBlock = 11;
    private const ulong ReadWrite = 2;
    internal readonly record struct Incarnation(Guid BootId, ulong NamespaceDevice, ulong NamespaceInode);

    internal static bool TryInspectChild(SafeFileHandle parent, string name, out Stat stat)
    {
        if (StatAt(Fd(parent), name, AtSymlinkNoFollow, BasicStatsAndMount, out stat) == 0)
        {
            RequireStatMask(stat);
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == NotFound) return false;
        ThrowErrno(error);
        return false;
    }

    internal static SafeFileHandle OpenRegularWrite(SafeFileHandle parent, string name, uint mode, uint userId)
    {
        using var probe = Open(Fd(parent), name, PathFlag | NoFollow | CloseOnExec, 0,
            NoSymlinks | Beneath | NoMountCrossing);
        var before = Inspect(probe);
        RequireFile(before, mode, userId);
        var handle = Open(Fd(parent), name, WriteOnly | NoFollow | CloseOnExec | NonBlocking | NoControllingTerminal,
            0, NoSymlinks | Beneath | NoMountCrossing);
        try
        {
            var opened = Inspect(handle);
            RequireFile(opened, mode, userId);
            if (before.Identity != opened.Identity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static Gate? TryLockGate(SafeFileHandle directory, string name, bool exclusive, uint userId)
    {
        using var probe = OpenRegularRead(directory, name, TemporaryFileMode, userId);
        var before = Inspect(probe);
        if (before.Size != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        // This open is deliberately fresh, never dup/cached/FileShare-based.
        var handle = Open(Fd(directory), name, ReadWrite | NoFollow | CloseOnExec | NonBlocking | NoControllingTerminal,
            0, NoSymlinks | Beneath | NoMountCrossing);
        try
        {
            var opened = Inspect(handle);
            RequireFile(opened, TemporaryFileMode, userId);
            if (opened.Identity != before.Identity || opened.Size != 0)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            if (Flock(Fd(handle), (exclusive ? 2 : 1) | 4) != 0) // LOCK_EX/SH | LOCK_NB
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == WouldBlock) { handle.Dispose(); return null; }
                // EINTR is a real failed acquisition, not an unbounded retry.
                if (error == Interrupted) throw Failure(PrivateObjectStorageError.Io);
                ThrowErrno(error);
            }
            var result = new Gate(directory, name, handle, opened.Identity, userId);
            result.Validate();
            return result;
        }
        catch { handle.Dispose(); throw; }
    }

    internal sealed class Gate(SafeFileHandle directory, string name, SafeFileHandle handle,
        Identity identity, uint userId) : IDisposable
    {
        internal Identity Identity => identity;
        internal void Validate()
        {
            var held = Inspect(handle);
            RequireFile(held, TemporaryFileMode, userId);
            if (!TryInspectChild(directory, name, out var named))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            RequireFile(named, TemporaryFileMode, userId);
            if (held.Identity != identity || named.Identity != identity || held.Size != 0 || named.Size != 0)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        public void Dispose() => handle.Dispose(); // last owned open description releases flock
    }

    internal static void SyncFile(SafeFileHandle handle)
    {
        if (Sync(Fd(handle)) != 0) ThrowErrno(Marshal.GetLastPInvokeError());
    }

    internal static void SyncDirectory(SafeFileHandle held)
    {
        using var ordinary = OpenSyncDirectory(held);
        SyncDirectory(held, ordinary);
    }

    internal static SafeFileHandle OpenSyncDirectory(SafeFileHandle held)
    {
        var before = Inspect(held);
        RequireDirectory(before, EffectiveUserId);
        var ordinary = Open(Fd(held), ".", DirectoryFlag | NoFollow | CloseOnExec, 0,
            NoSymlinks | Beneath | NoMountCrossing);
        try
        {
            var opened = Inspect(ordinary);
            RequireDirectory(opened, EffectiveUserId);
            if (before.Identity != opened.Identity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            return ordinary;
        }
        catch { ordinary.Dispose(); throw; }
    }

    internal static void SyncDirectory(SafeFileHandle held, SafeFileHandle ordinary)
    {
        var before = Inspect(held);
        RequireDirectory(before, EffectiveUserId);
        var opened = Inspect(ordinary);
        RequireDirectory(opened, EffectiveUserId);
        if (before.Identity != opened.Identity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        SyncFile(ordinary);
        if (Inspect(ordinary).Identity != before.Identity || Inspect(held).Identity != before.Identity)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    // Unlike the historical void helper, this reports actual unlink versus ENOENT.
    internal static bool UnlinkExact(SafeFileHandle parent, string name, Identity identity, uint userId,
        uint? expectedMode = null, ulong? expectedSize = null)
    {
        if (!TryInspectChild(parent, name, out var current)) return false;
        if (current.Identity != identity || current.UserId != userId ||
            (current.Mode & 0xf000) != 0x8000 || current.LinkCount != 1 ||
            (uint)(current.Mode & 0xfff) is not (TemporaryFileMode or FinalFileMode) ||
            (expectedMode is { } mode && (current.Mode & 0xfff) != mode) ||
            (expectedSize is { } size && current.Size != size))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (Unlink(Fd(parent), name, 0) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == NotFound) return false;
        ThrowErrno(error);
        return false;
    }

    internal static Incarnation ReadIncarnation()
    {
        // The only intentional magic-link follow is this fixed trusted namespace.
        var raw = OpenNamespace("/proc/self/ns/mnt", checked((int)CloseOnExec));
        if (raw < 0) ThrowErrno(Marshal.GetLastPInvokeError());
        if (raw < 3)
        {
            var duplicated = DuplicateAboveStdio(raw, 1030, 3);
            var error = Marshal.GetLastPInvokeError();
            Close(raw);
            if (duplicated < 0) ThrowErrno(error);
            raw = duplicated;
        }
        using var handle = new SafeFileHandle((IntPtr)raw, ownsHandle: true);
        var stat = Inspect(handle);
        if (stat.Inode == 0) throw Failure(PrivateObjectStorageError.Unsupported);
        var device = MakeDevice(stat.DeviceMajor, stat.DeviceMinor);
        using var input = new FileStream("/proc/sys/kernel/random/boot_id", FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[37];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || bytes[36] != (byte)'\n' ||
            !Guid.TryParseExact(System.Text.Encoding.ASCII.GetString(bytes[..36]), "D", out var boot) || boot == Guid.Empty)
            throw Failure(PrivateObjectStorageError.Unsupported);
        return new(boot, device, stat.Inode);
    }

    internal static PrivatePublicationInodeIdentity PublicationIdentity(Identity identity) =>
        new(identity.Inode, identity.DeviceMajor, identity.DeviceMinor, identity.MountId);

    internal static Identity FileIdentity(PrivatePublicationInodeIdentity identity) =>
        new(identity.Inode, identity.DeviceMajor, identity.DeviceMinor, identity.MountId);

    [DllImport(Libc, EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Flock(int fd, int operation);

    [DllImport(Libc, EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Sync(int fd);

    [DllImport(Libc, EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenNamespace([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(Libc, EntryPoint = "gnu_dev_makedev")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern ulong MakeDevice(uint major, uint minor);
}
