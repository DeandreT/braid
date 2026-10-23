using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Weft.MappingBackends.Workers;

/// <summary>
/// The shared libraries that a set of x86-64 ELF objects can load, found by reading their dynamic sections rather
/// than running the loader (the service image has no shell or <c>ldd</c>). Every <c>DT_NEEDED</c> name and every
/// name the .NET runtime opens itself is looked up, transitively, where glibc's loader searches for it, and in every
/// directory the loader may search beneath the granted system library directories (each directory, its multiarch
/// directory and their <c>glibc-hwcaps</c> subdirectories). The sandboxed child cannot read the loader cache, so the
/// loader searches exactly these directories after the run paths. Each match counts, so the closure covers the copy
/// the loader picks whatever its order. Run paths follow glibc: an object with <c>DT_RUNPATH</c> ignores every
/// <c>DT_RPATH</c> and searches its run path for its own direct dependencies only; any other object searches its own
/// <c>DT_RPATH</c>, then the <c>DT_RPATH</c> of each object above it in the chain that loaded it and of the executable.
/// </summary>
internal static class ElfLibraryClosure
{
    /// <summary>
    /// Libraries the .NET host and runtime open by name rather than through <c>DT_NEEDED</c>: every OpenSSL soname the
    /// cryptography shim tries, the LTTng tracepoint library and the unwinder glibc opens for thread cancellation.
    /// ICU is not opened because the children run in invariant globalization mode.
    /// </summary>
    internal static readonly IReadOnlyList<string> RuntimeOpenedLibraries =
    [
        "libssl.so.3", "libssl.so.1.1", "libssl.so.1.0.2", "libssl.so.1.0.0", "libssl.so.10",
        "liblttng-ust-tracepoint.so.0", "libgcc_s.so.1"
    ];

    private const string Multiarch = "x86_64-linux-gnu";
    private const int MaximumObjects = 4096;
    private const int MaximumVisits = 65536;
    private const long MaximumDynamicBytes = 1L << 20;
    private const long MaximumStringBytes = 16L << 20;

    /// <summary>
    /// The normalized absolute paths, in ordinal order, of every library that <paramref name="objects"/> or the named
    /// libraries can load. <paramref name="objects"/> are the executables and the libraries loaded by path; each may
    /// be the executable or the caller that opens a named library, so every <c>DT_RPATH</c> among them applies to
    /// every chain, and their <c>DT_RUNPATH</c> entries to the named libraries. Files that are not x86-64 ELF objects
    /// are included but not walked, as the loader skips them; a malformed ELF object fails the measurement.
    /// </summary>
    internal static IReadOnlyList<string> Resolve(IEnumerable<string> objects, IEnumerable<string> names, IReadOnlyList<string> libraryDirectories)
    {
        var system = SearchDirectories(libraryDirectories);
        var parsed = new Dictionary<string, Dependencies?>(StringComparer.Ordinal);
        Dependencies? Parse(string path)
        {
            if (!parsed.TryGetValue(path, out var dependencies)) parsed[path] = dependencies = Read(path);
            return dependencies;
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        // Each object with the inherited DT_RPATH directories it has been walked with; a new chain that adds any
        // directory walks it again with the union.
        var walked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pending = new Queue<(string Path, string[] Inherited)>();
        var visits = 0;
        void Load(string name, IEnumerable<string> paths, string[] inherited)
        {
            if (name.Contains('/'))
            {
                if (!Path.IsPathRooted(name)) throw new InvalidDataException("A library dependency is a relative path.");
                var path = Path.GetFullPath(name);
                if (File.Exists(path))
                {
                    found.Add(path);
                    pending.Enqueue((path, inherited));
                }

                return;
            }

            foreach (var candidate in paths.Concat(system).Select(directory => Path.GetFullPath(Path.Combine(directory, name))))
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                    pending.Enqueue((candidate, inherited));
                }
        }

        var roots = objects.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        string[] executable = [.. roots.SelectMany(root => Parse(root)?.RPath ?? []).Distinct(StringComparer.Ordinal)];
        foreach (var root in roots) pending.Enqueue((root, executable));
        string[] callers = [.. executable, .. roots.SelectMany(root => Parse(root)?.RunPath ?? []).Distinct(StringComparer.Ordinal)];
        foreach (var name in names) Load(name, callers, executable);
        while (pending.TryDequeue(out var next))
        {
            if (++visits > MaximumVisits) throw new InvalidDataException("The library closure is too large.");
            if (!walked.TryGetValue(next.Path, out var inherited))
            {
                if (walked.Count == MaximumObjects) throw new InvalidDataException("The library closure is too large.");
                walked[next.Path] = inherited = new HashSet<string>(StringComparer.Ordinal);
            }
            else if (inherited.IsSupersetOf(next.Inherited))
            {
                continue;
            }

            inherited.UnionWith(next.Inherited);
            if (Parse(next.Path) is not { } dependencies) continue;
            string[] chain = [.. inherited.Order(StringComparer.Ordinal)];
            // With DT_RUNPATH, neither its own nor any inherited DT_RPATH applies, and its run path is not passed on.
            var paths = dependencies.RunPath.Length > 0 ? dependencies.RunPath : [.. dependencies.RPath, .. chain];
            string[] passed = [.. chain, .. dependencies.RPath];
            foreach (var name in dependencies.Needed) Load(name, paths, passed);
        }

        return [.. found];
    }

    /// <summary>The <c>DT_NEEDED</c> names and the expanded run paths of one object.</summary>
    private sealed record Dependencies(string[] Needed, string[] RPath, string[] RunPath);

    private static string[] SearchDirectories(IReadOnlyList<string> libraryDirectories)
    {
        var directories = new List<string>();
        foreach (var granted in libraryDirectories)
        {
            var info = new DirectoryInfo(granted);
            // A granted directory such as /lib64 is often a link to another granted directory.
            var canonical = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
            foreach (var directory in new[] { canonical, Path.Combine(canonical, Multiarch) })
            {
                if (!Directory.Exists(directory)) continue;
                directories.Add(directory);
                var capabilities = Path.Combine(directory, "glibc-hwcaps");
                if (Directory.Exists(capabilities))
                    directories.AddRange(Directory.EnumerateDirectories(capabilities).Order(StringComparer.Ordinal));
            }
        }

        return [.. directories.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Only <c>$ORIGIN</c> is expanded; any other token or a relative entry (including an empty one, which the loader
    /// reads as the working directory) fails the measurement.
    /// </summary>
    private static string Expand(string entry, string origin)
    {
        var path = entry.Replace("${ORIGIN}", origin, StringComparison.Ordinal).Replace("$ORIGIN", origin, StringComparison.Ordinal);
        if (path.Contains('$') || !Path.IsPathRooted(path)) throw new InvalidDataException("A library run path is not admitted.");
        return path;
    }

    /// <summary>The <c>DT_NEEDED</c> names and expanded run paths of a 64-bit little-endian x86-64 ELF object, or null for any other file.</summary>
    private static Dependencies? Read(string path)
    {
        using var file = File.OpenHandle(path);
        var length = RandomAccess.GetLength(file);
        Span<byte> header = stackalloc byte[64];
        if (length < header.Length || RandomAccess.Read(file, header, 0) != header.Length || !header[..4].SequenceEqual("\u007fELF"u8) ||
            header[4] != 2 || header[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(header[0x12..]) != 62)
            return null;
        var table = BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]);
        var size = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        if (size < 56 || table > (ulong)length || (ulong)size * count > (ulong)length - table) throw Malformed();

        var loads = new List<(ulong Address, ulong Offset, ulong Size)>();
        (ulong Offset, ulong Size)? dynamic = null;
        Span<byte> entry = stackalloc byte[56];
        for (var index = 0; index < count; index++)
        {
            ReadExactly(file, entry, table + (ulong)index * size);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var segment = (Address: BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]), Offset: BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]),
                Size: BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]));
            if (type == 1) loads.Add(segment);
            else if (type == 2) dynamic = (segment.Offset, segment.Size);
        }

        // A static object loads nothing.
        if (dynamic is not { } section) return new([], [], []);
        if (section.Size > MaximumDynamicBytes || section.Offset > (ulong)length || section.Size > (ulong)length - section.Offset) throw Malformed();
        var entries = new byte[section.Size];
        ReadExactly(file, entries, section.Offset);
        var needed = new List<ulong>();
        var rPath = new List<ulong>();
        var runPath = new List<ulong>();
        ulong? stringAddress = null, stringSize = null;
        for (var at = 0; at + 16 <= entries.Length; at += 16)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(at));
            var value = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(at + 8));
            if (tag == 0) break;
            switch (tag)
            {
                case 1: needed.Add(value); break;
                case 5: stringAddress = value; break;
                case 10: stringSize = value; break;
                case 15: rPath.Add(value); break;
                case 29: runPath.Add(value); break;
            }
        }

        if (needed.Count == 0 && rPath.Count == 0 && runPath.Count == 0) return new([], [], []);
        if (stringAddress is not { } address || stringSize is not { } bytes || bytes is 0 or > MaximumStringBytes) throw Malformed();
        // The string table's address lies in a loaded segment; its file offset follows from that segment.
        var load = loads.FindIndex(load => address >= load.Address && address - load.Address < load.Size && bytes <= load.Size - (address - load.Address));
        if (load < 0) throw Malformed();
        var offset = loads[load].Offset + (address - loads[load].Address);
        if (offset > (ulong)length || bytes > (ulong)length - offset) throw Malformed();
        var stringTable = new byte[bytes];
        ReadExactly(file, stringTable, offset);
        string Text(ulong start)
        {
            var end = start < bytes ? Array.IndexOf(stringTable, (byte)0, (int)start) : -1;
            if (end < 0) throw Malformed();
            return Encoding.UTF8.GetString(stringTable, (int)start, end - (int)start);
        }

        var origin = Path.GetDirectoryName(path)!;
        string[] Paths(List<ulong> values) => [.. values.SelectMany(value => Text(value).Split(':')).Select(entry => Expand(entry, origin))];
        // Like glibc, an object with DT_RUNPATH ignores its DT_RPATH.
        return new([.. needed.Select(Text)], runPath.Count > 0 ? [] : Paths(rPath), Paths(runPath));
    }

    private static void ReadExactly(SafeFileHandle file, Span<byte> buffer, ulong offset)
    {
        if (offset > long.MaxValue) throw Malformed();
        var position = (long)offset;
        while (!buffer.IsEmpty)
        {
            var count = RandomAccess.Read(file, buffer, position);
            if (count == 0) throw Malformed();
            buffer = buffer[count..];
            position += count;
        }
    }

    private static InvalidDataException Malformed() => new("A library is not a well-formed ELF object.");
}
