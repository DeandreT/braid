using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Braid.Application;

namespace Braid.Storage.FileSystem;

/// <summary>Only the admitted Linux x86-64 descriptor-relative filesystem primitives.</summary>
internal static partial class LinuxFileSystem
{
    private const string Libc = "libc.so.6";
    private const long OpenAt2SystemCall = 437;
    private const int AtCurrentDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtRemoveDirectory = 0x200;
    private const ulong NoSymlinks = 0x04;
    private const ulong Beneath = 0x08;
    private const ulong NoMountCrossing = 0x01;
    private const ulong CloseOnExec = 0x80000;
    private const ulong DirectoryFlag = 0x10000;
    private const ulong NoFollow = 0x20000;
    private const ulong PathFlag = 0x200000;
    private const ulong NonBlocking = 0x800;
    private const ulong NoControllingTerminal = 0x100;
    private const ulong Create = 0x40;
    private const ulong Exclusive = 0x80;
    private const ulong WriteOnly = 0x01;
    private const uint BasicStatsAndMount = 0x17ff;
    private const uint RequiredStatMask = 0x130f;
    internal const uint PrivateDirectoryMode = 0x1c0; // 0700
    internal const uint TemporaryFileMode = 0x180; // 0600
    internal const uint FinalFileMode = 0x100; // 0400
    internal const int AlreadyExists = 17;
    internal const int NotFound = 2;

    internal readonly record struct Identity(ulong Inode, uint DeviceMajor, uint DeviceMinor, ulong MountId);

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }

    // Linux UAPI statx is 256 bytes, independently of the libc struct stat ABI.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct Stat
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
        internal readonly Identity Identity => new(Inode, DeviceMajor, DeviceMinor, MountId);
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystemStat
    {
        [FieldOffset(0)] internal long Type;
    }

    internal static void RequireProfile()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            IntPtr.Size != 8 || Marshal.SizeOf<OpenHow>() != 24 || Marshal.SizeOf<Stat>() != 256 ||
            Marshal.SizeOf<FileSystemStat>() != 120)
            throw Failure(PrivateObjectStorageError.Unsupported);
    }

    internal static uint EffectiveUserId => GetEffectiveUserId();

    internal static SafeFileHandle OpenAbsoluteDirectory(string path) =>
        Open(AtCurrentDirectory, path, PathFlag | DirectoryFlag | NoFollow | CloseOnExec, 0, NoSymlinks);

    internal static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name) =>
        Open(Fd(parent), name, PathFlag | DirectoryFlag | NoFollow | CloseOnExec, 0,
            NoSymlinks | Beneath | NoMountCrossing);

    // Mount traversal is admitted up to the configured root, never below its held descriptor.
    internal static SafeFileHandle OpenConfiguredRoot(SafeFileHandle parent, string name) =>
        Open(Fd(parent), name, PathFlag | DirectoryFlag | NoFollow | CloseOnExec, 0, NoSymlinks | Beneath);

    internal static SafeFileHandle CreateTemporary(SafeFileHandle parent, string name) =>
        Open(Fd(parent), name, WriteOnly | Create | Exclusive | NoFollow | CloseOnExec | NoControllingTerminal,
            TemporaryFileMode, NoSymlinks | Beneath | NoMountCrossing);

    internal static SafeFileHandle OpenRegularRead(SafeFileHandle parent, string name, uint mode, uint userId)
    {
        // O_PATH avoids opening a FIFO/device for data before checking its actual type.
        using var probe = Open(Fd(parent), name, PathFlag | NoFollow | CloseOnExec, 0,
            NoSymlinks | Beneath | NoMountCrossing);
        var probed = Inspect(probe);
        RequireFile(probed, mode, userId);
        var file = Open(Fd(parent), name, NoFollow | CloseOnExec | NonBlocking | NoControllingTerminal, 0,
            NoSymlinks | Beneath | NoMountCrossing);
        try
        {
            var opened = Inspect(file);
            RequireFile(opened, mode, userId);
            if (opened.Identity != probed.Identity)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    internal static Stat Inspect(SafeFileHandle handle)
    {
        if (StatAt(Fd(handle), "", AtEmptyPath, BasicStatsAndMount, out var stat) != 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
        RequireStatMask(stat);
        return stat;
    }

    internal static bool ContainsGitMarker(SafeFileHandle directory)
    {
        if (StatAt(Fd(directory), ".git", AtSymlinkNoFollow, BasicStatsAndMount, out _) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == NotFound)
            return false;
        ThrowErrno(error);
        return false;
    }

    internal static void RequireDirectory(Stat stat, uint userId)
    {
        if ((stat.Mode & 0xf000) != 0x4000 || (stat.Mode & 0xfff) != PrivateDirectoryMode ||
            stat.UserId != userId || stat.LinkCount == 0)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    internal static void RequireSafeParent(Stat stat, uint userId)
    {
        var rootOwnedStickyTemporary = stat.UserId == 0 && (stat.Mode & 0xfff) == 0x3ff; // 01777
        var trustedNonWritable = (stat.UserId == userId || stat.UserId == 0) && (stat.Mode & 0x12) == 0; // 0022
        if ((stat.Mode & 0xf000) != 0x4000 || stat.LinkCount == 0 ||
            (!rootOwnedStickyTemporary && !trustedNonWritable))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    internal static void RequireFile(Stat stat, uint mode, uint userId)
    {
        if ((stat.Mode & 0xf000) != 0x8000 || (stat.Mode & 0xfff) != mode ||
            stat.UserId != userId || stat.LinkCount != 1)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    internal static void RequireLocalFileSystem(SafeFileHandle handle)
    {
        if (FileSystemInfo(Fd(handle), out var stat) != 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
        if (stat.Type is not (0xef53 or 0x9123683e or 0x58465342 or 0x01021994 or 0x794c7630))
            throw Failure(PrivateObjectStorageError.Unsupported);
    }

    internal static bool CreateDirectory(SafeFileHandle parent, string name)
    {
        if (MakeDirectory(Fd(parent), name, PrivateDirectoryMode) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == AlreadyExists)
            return false;
        ThrowErrno(error);
        return false;
    }

    internal static void MakeReadOnly(SafeFileHandle file)
    {
        if (ChangeMode(Fd(file), FinalFileMode) != 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
    }

    internal static bool PublishNoReplace(SafeFileHandle directory, string temporary, string final)
    {
        var fd = Fd(directory);
        if (RenameNoReplace(fd, temporary, fd, final, 1) == 0)
            return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == AlreadyExists)
            return false;
        ThrowErrno(error);
        return false;
    }

    internal static void UnlinkOwned(SafeFileHandle parent, string name, Identity identity, bool directory = false)
    {
        if (StatAt(Fd(parent), name, AtSymlinkNoFollow, BasicStatsAndMount, out var stat) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NotFound)
                return;
            ThrowErrno(error);
        }
        RequireStatMask(stat);
        if (stat.Identity != identity || stat.UserId != EffectiveUserId ||
            (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (!directory && stat.LinkCount != 1))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (Unlink(Fd(parent), name, directory ? AtRemoveDirectory : 0) != 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
    }

    internal static PrivateObjectStorageException Failure(PrivateObjectStorageError error) => new(error);

    private static SafeFileHandle Open(int parent, string path, ulong flags, ulong mode, ulong resolve)
    {
        var how = new OpenHow { Flags = flags, Mode = mode, Resolve = resolve };
        var result = OpenAt2(OpenAt2SystemCall, parent, path, ref how, 24);
        if (result < 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
        var fd = checked((int)result);
        if (fd < 3)
        {
            var duplicated = DuplicateAboveStdio(fd, 1030, 3); // F_DUPFD_CLOEXEC
            var error = Marshal.GetLastPInvokeError();
            Close(fd);
            if (duplicated < 0)
                ThrowErrno(error);
            fd = duplicated;
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static void RequireStatMask(Stat stat)
    {
        if ((stat.Mask & RequiredStatMask) != RequiredStatMask)
            throw Failure(PrivateObjectStorageError.Unsupported);
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    private static void ThrowErrno(int error) => throw Failure(error switch
    {
        NotFound => PrivateObjectStorageError.Missing,
        13 or 18 or 20 or 21 or 40 => PrivateObjectStorageError.UnsafeStorage,
        7 or 22 or 38 or 95 => PrivateObjectStorageError.Unsupported,
        _ => PrivateObjectStorageError.Io
    });

    [DllImport(Libc, EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long OpenAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref OpenHow how, nuint size);

    [DllImport(Libc, EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Stat stat);

    [DllImport(Libc, EntryPoint = "fstatfs", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int FileSystemInfo(int fd, out FileSystemStat stat);

    [DllImport(Libc, EntryPoint = "mkdirat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int MakeDirectory(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport(Libc, EntryPoint = "fchmod", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ChangeMode(int fd, uint mode);

    [DllImport(Libc, EntryPoint = "renameat2", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameNoReplace(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    [DllImport(Libc, EntryPoint = "unlinkat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Unlink(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(Libc, EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    [DllImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int DuplicateAboveStdio(int fd, int command, int minimum);

    [DllImport(Libc, EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int fd);
}
