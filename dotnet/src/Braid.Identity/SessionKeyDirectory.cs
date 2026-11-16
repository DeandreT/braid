using System.Runtime.InteropServices;

namespace Braid.Identity;

/// <summary>
/// Path admission and revalidation for the standard Data Protection filesystem repository.
/// Data Protection still opens paths itself; this is not descriptor-relative storage.
/// </summary>
internal sealed class SessionKeyDirectory
{
    private readonly string directory;
    private readonly uint owner;
    private readonly DirectoryStamp[] ancestors;

    private SessionKeyDirectory(string directory, uint owner, DirectoryStamp[] ancestors)
    {
        this.directory = directory;
        this.owner = owner;
        this.ancestors = ancestors;
    }

    internal static SessionKeyDirectory Admit(string directory)
    {
        if (!OperatingSystem.IsLinux())
            throw ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        var owner = EffectiveUserId();
        var paths = new List<string> { "/" };
        var current = "/";
        foreach (var component in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            paths.Add(current);
        }
        var stamps = paths.Select(path => new DirectoryStamp(path, Inspect(path))).ToArray();
        var admitted = new SessionKeyDirectory(directory, owner, stamps);
        admitted.Revalidate();
        return admitted;
    }

    internal void Revalidate()
    {
        foreach (var ancestor in ancestors)
        {
            var observed = Inspect(ancestor.Path);
            if (!SameIdentity(ancestor.Stat, observed) || (observed.Mode & 0xf000) != 0x4000 ||
                observed.LinkCount == 0)
                throw Failure();
            if (string.Equals(ancestor.Path, directory, StringComparison.Ordinal))
            {
                if (observed.UserId != owner || (observed.Mode & 0xfff) != 0x1c0) // 0700
                    throw Failure();
            }
            else
            {
                var rootStickyTemporary = observed.UserId == 0 && (observed.Mode & 0xfff) == 0x3ff; // 01777
                var trustedParent = (observed.UserId == 0 || observed.UserId == owner) &&
                    (observed.Mode & 0x12) == 0; // no group/other write
                if (!rootStickyTemporary && !trustedParent)
                    throw Failure();
            }
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var file = Inspect(path);
            if ((file.Mode & 0xf000) != 0x8000 || file.UserId != owner || file.LinkCount != 1 ||
                (file.Mode & 0xfff) != 0x180) // regular, private 0600; no repair
                throw Failure();
        }
        if (!SameIdentity(ancestors[^1].Stat, Inspect(directory)))
            throw Failure();
    }

    internal bool IsEmpty()
    {
        Revalidate();
        var empty = !Directory.EnumerateFileSystemEntries(directory).Any();
        Revalidate();
        return empty;
    }

    private static Stat Inspect(string path)
    {
        // AT_SYMLINK_NOFOLLOW rejects each admitted component itself, including children.
        if (StatAt(-100, path, 0x100, 0x7ff, out var value) != 0 || (value.Mask & 0x10f) != 0x10f)
            throw Failure();
        return value;
    }

    private static bool SameIdentity(Stat first, Stat second) => first.Inode == second.Inode &&
        first.DeviceMajor == second.DeviceMajor && first.DeviceMinor == second.DeviceMinor;

    private static ServerSessionIdentityException Failure() =>
        ServerSessionIdentityErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

    private readonly record struct DirectoryStamp(string Path, Stat Stat);

    // The fixed Linux statx ABI avoids depending on libc's architecture-specific struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Stat
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    [DllImport("libc.so.6", EntryPoint = "statx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Stat stat);

    [DllImport("libc.so.6", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint EffectiveUserId();
}
