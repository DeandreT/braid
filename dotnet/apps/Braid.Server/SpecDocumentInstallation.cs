using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Braid.Server;

/// <summary>Require OS-enforced read-only installation mounts, rather than trusting a configured immutability flag.</summary>
internal static class SpecDocumentInstallation
{
    internal sealed record Mount(string Path, bool ReadOnly, string Device, string Root);
    internal sealed class AdmissionRefusedException : InvalidOperationException
    {
        internal AdmissionRefusedException() : base("The document worker installation is not admitted.") { }
    }
    private static AdmissionRefusedException Refused() => new();

    [SupportedOSPlatform("linux")]
    internal static void Validate(SpecDocumentHostSettings settings, CancellationToken token)
    {
        var status = File.ReadAllLines("/proc/self/status");
        var uid = status.Single(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        if (uid.Length != 4 || uid.Any(value => value == "0") || uid.Distinct().Count() != 1 ||
            status.Single(line => line.StartsWith("CapEff:", StringComparison.Ordinal)).Split(':')[1].Trim().Trim('0').Length != 0)
            throw Refused();
        var mounts = ParseMounts(File.ReadAllLines("/proc/self/mountinfo"));
        // A read-only root also protects the dynamic loader configuration and its immutable symlink parents.
        if (!mounts.Single(m => m.Path == "/").ReadOnly) throw Refused();
        foreach (var path in new[] { "/usr", "/lib", "/lib64" })
            if (Directory.Exists(path))
            {
                RequireReadOnlyTree(path, mounts);
                var resolved = Directory.ResolveLinkTarget(path, true)?.FullName;
                if (resolved is not null) RequireReadOnlyTree(resolved, mounts);
            }
        if (!Effective("/etc", mounts).ReadOnly) throw Refused();
        foreach (var path in new[] { "/etc/ld.so.cache", "/etc/ld.so.preload", "/etc/ld.so.conf", "/etc/ld.so.conf.d" })
        {
            RequireReadOnlyTree(path, mounts);
            if (File.Exists(path) || Directory.Exists(path)) RequireNoLinks(path);
        }
        var directories = new[] { Path.GetDirectoryName(settings.OfficeAssembly)!, Path.GetDirectoryName(settings.OfficeLauncher)!,
            settings.DotnetRoot, Path.GetDirectoryName(settings.PdfExecutable)!,
            Path.GetDirectoryName(typeof(Braid.MappingBackends.Workers.QualifiedOfficeProcessWorker).Assembly.Location)! }.Distinct().ToArray();
        foreach (var directory in directories)
        {
            RequireReadOnlyTree(directory, mounts);
            RequireNoLinks(directory);
            var pending = new Stack<string>(); pending.Push(directory); var count = 0;
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++count > 100_000) throw Refused();
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw Refused();
                    var native = Stat(item);
                    if ((native.Mode & 0xf000) is not (0x8000 or 0x4000)) throw Refused();
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                }
            }
        }
        foreach (var executable in new[] { settings.OfficeAssembly, settings.OfficeLauncher, settings.PdfExecutable })
            if (!File.Exists(executable) || (Stat(executable).Mode & 0xf000) != 0x8000) throw Refused();
        if (settings.OfficeWorkRoot == settings.PdfWorkRoot || Inside(settings.OfficeWorkRoot, settings.PdfWorkRoot) || Inside(settings.PdfWorkRoot, settings.OfficeWorkRoot))
            throw Refused();
        foreach (var root in new[] { settings.OfficeWorkRoot, settings.PdfWorkRoot })
        {
            token.ThrowIfCancellationRequested(); RequireNoLinks(root);
            if (Effective(root, mounts).ReadOnly || directories.Any(d => Inside(root, d) || Inside(d, root)) ||
                !Effective(Path.GetDirectoryName(root)!, mounts).ReadOnly || Stat(root).User != uint.Parse(uid[0], System.Globalization.CultureInfo.InvariantCulture) ||
                File.GetUnixFileMode(root) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) throw Refused();
            // With no effective capabilities and mode 0700, successful creation confirms this process owns usable private storage.
            ProbeWorkRoot(root);
        }
    }

    internal static void ProbeWorkRoot(string root, Action<string>? delete = null) =>
        Braid.MappingBackends.Workers.WorkerDirectoryProbe.Run(root, "document-admission-", delete);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint User;
        [FieldOffset(28)] internal ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int ReadStat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx value);
    private static Statx Stat(string path)
    {
        // Require the kernel to have returned type, mode and UID, not merely a successful syscall.
        if (ReadStat(-100, path, 0x100, 0x7ff, out var value) != 0 || (value.Mask & 0xb) != 0xb) throw Refused();
        return value;
    }

    private static void RequireNoLinks(string path)
    {
        var current = path;
        while (current != "/")
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Refused();
            current = Path.GetDirectoryName(current) ?? throw Refused();
        }
    }
    internal static IReadOnlyList<Mount> ParseMounts(IEnumerable<string> lines)
    {
        var mounts = new List<Mount>();
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 10 || !fields.Contains("-")) throw Refused();
            static string Decode(string value) => value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
            var path = Decode(fields[4]); var root = Decode(fields[3]);
            if (!path.StartsWith('/') || mounts.Any(m => m.Path == path)) throw Refused();
            var options = fields[5].Split(',');
            var ro = options.Contains("ro", StringComparer.Ordinal); var rw = options.Contains("rw", StringComparer.Ordinal);
            if (ro == rw) throw Refused();
            if (!root.StartsWith('/')) throw Refused();
            mounts.Add(new(path, ro, fields[2], root));
        }
        if (!mounts.Any(m => m.Path == "/")) throw Refused();
        return mounts.AsReadOnly();
    }
    private static bool Inside(string path, string parent) => path == parent || parent == "/" || path.StartsWith(parent + "/", StringComparison.Ordinal);
    private static Mount Effective(string path, IReadOnlyList<Mount> mounts) => mounts.Where(m => Inside(path, m.Path)).OrderByDescending(m => m.Path.Length).First();
    internal static void RequireReadOnlyTree(string path, IReadOnlyList<Mount> mounts)
    {
        if (!Effective(path, mounts).ReadOnly || mounts.Any(m => Inside(m.Path, path) && !m.ReadOnly)) throw Refused();
        // A read-only bind is not immutable if this process can reach the same backing files through a writable alias.
        foreach (var target in mounts.Where(m => Inside(m.Path, path)).Select(m => m.Path).Prepend(path))
        {
            var mount = Effective(target, mounts);
            // Disjoint backing roots can still expose hardlinks to the same inode.
            if (mounts.Any(m => !m.ReadOnly && m.Device == mount.Device)) throw Refused();
        }
    }
}
