using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Weft.Server;

/// <summary>Bounded private-file admission; no creation, adoption, chmod, or descriptor-relative guarantee.</summary>
internal static class HostPrivateFiles
{
    internal static string ReadText(string path, int maximum, bool removeTerminalLf = false)
    {
        byte[]? bytes = null;
        try
        {
            if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(path) ||
                path != Path.GetFullPath(path) || Path.EndsInDirectorySeparator(path)) throw Failure();
            var owner = EffectiveUserId();
            var parents = new List<(string Path, Stat Stamp)>();
            var parent = "/";
            parents.Add((parent, Inspect(parent)));
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
            {
                parent = Path.Combine(parent, part);
                parents.Add((parent, Inspect(parent)));
            }
            foreach (var item in parents) RequireParent(item.Stamp, owner);
            var first = Inspect(path);
            RequireFile(first, owner, maximum);
            bytes = new byte[maximum + 1];
            var count = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes.AsSpan(count));
                    if (read == 0) break;
                    count += read;
                }
            if (count == 0 || count > maximum || (ulong)count != first.Size) throw Failure();
            var after = Inspect(path);
            RequireFile(after, owner, maximum);
            if (!Same(first, after) || first.Size != after.Size ||
                first.ModifiedSeconds != after.ModifiedSeconds || first.ModifiedNanoseconds != after.ModifiedNanoseconds ||
                first.ChangedSeconds != after.ChangedSeconds || first.ChangedNanoseconds != after.ChangedNanoseconds)
                throw Failure();
            foreach (var item in parents)
            {
                var current = Inspect(item.Path);
                RequireParent(current, owner);
                if (!Same(item.Stamp, current)) throw Failure();
            }
            if (bytes.AsSpan(0, count).IndexOf((byte)0) >= 0 ||
                count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) throw Failure();
            var value = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (removeTerminalLf && value.EndsWith('\n')) value = value[..^1];
            if (value.Length == 0) throw Failure();
            return value;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { throw Failure(); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireParent(Stat value, uint owner)
    {
        if ((value.Mode & 0xf000) != 0x4000 || value.LinkCount == 0) throw Failure();
        var rootStickyTemporary = value.UserId == 0 && (value.Mode & 0xfff) == 0x3ff;
        var protectedParent = (value.UserId == 0 || value.UserId == owner) && (value.Mode & 0x12) == 0;
        if (!rootStickyTemporary && !protectedParent) throw Failure();
    }

    private static void RequireFile(Stat value, uint owner, int maximum)
    {
        if ((value.Mode & 0xf000) != 0x8000 || value.UserId != owner || value.LinkCount != 1 ||
            (value.Mode & 0xfff) != 0x180 || value.Size is 0 || value.Size > (ulong)maximum) throw Failure();
    }

    private static bool Same(Stat first, Stat second) => first.Inode == second.Inode &&
        first.DeviceMajor == second.DeviceMajor && first.DeviceMinor == second.DeviceMinor;
    private static Stat Inspect(string path)
    {
        if (StatAt(-100, path, 0x100, 0x7ff, out var value) != 0 || (value.Mask & 0x3cf) != 0x3cf)
            throw Failure();
        return value;
    }
    private static InvalidOperationException Failure() => new("The private host file is invalid.");
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Stat
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(96)] internal long ChangedSeconds;
        [FieldOffset(104)] internal uint ChangedNanoseconds;
        [FieldOffset(112)] internal long ModifiedSeconds;
        [FieldOffset(120)] internal uint ModifiedNanoseconds;
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
