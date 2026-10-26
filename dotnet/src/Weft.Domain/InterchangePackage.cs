using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace Weft.Domain;

/// <summary>A closed, byte-only package. Names are logical POSIX paths, never filesystem paths.</summary>
public sealed class InterchangePackage
{
    public const int MaximumFiles = 128;
    public const int MaximumFileBytes = 4 * 1024 * 1024;
    public const int MaximumTotalBytes = 16 * 1024 * 1024;
    public const int MaximumPathBytes = 256;
    public const int MaximumEncodedBytes = MaximumTotalBytes + (MaximumFiles + 1) * (MaximumPathBytes + 8) + 128;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ReadOnlyCollection<InterchangeFile> _files;

    public InterchangePackage(string designPath, IEnumerable<InterchangeFile> files)
    {
        DesignPath = ValidatePath(designPath);
        ArgumentNullException.ThrowIfNull(files);
        var owned = new List<InterchangeFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (owned.Count == MaximumFiles || !names.Add(file.Path))
                throw new ArgumentException("Package file count or duplicate path is invalid.", nameof(files));
            total += file.ByteCount;
            if (total > MaximumTotalBytes)
                throw new ArgumentException("Package aggregate byte limit exceeded.", nameof(files));
            owned.Add(file);
        }
        if (!names.Contains(designPath))
            throw new ArgumentException("The design must be an exact package entry.", nameof(designPath));
        // UTF-8 byte order is shared with Rust String ordering, unlike UTF-16
        // ordinal order for supplementary-plane characters.
        owned.Sort((left, right) => Utf8.GetBytes(left.Path).AsSpan().SequenceCompareTo(Utf8.GetBytes(right.Path)));
        _files = owned.AsReadOnly();
        TotalBytes = (int)total;
        Digest = Sha256Digest.FromBytes(Encode());
    }

    public string DesignPath { get; }
    public IReadOnlyList<InterchangeFile> Files => _files;
    public int TotalBytes { get; }
    public Sha256Digest Digest { get; }

    /// <summary>Versioned canonical framing; sorted UTF-8 byte names and big-endian byte lengths.</summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        Write(stream, "weft.interchange-package.v1"u8);
        Write(stream, Utf8.GetBytes(DesignPath));
        WriteNumber(stream, _files.Count);
        foreach (var file in _files)
        {
            Write(stream, Utf8.GetBytes(file.Path));
            Write(stream, file.Bytes);
        }
        return stream.ToArray();
    }

    public static InterchangePackage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > MaximumEncodedBytes)
            throw new ArgumentException("Encoded package exceeds its bound.", nameof(encoded));
        var reader = new PackageReader(encoded);
        if (!reader.Read(64).SequenceEqual("weft.interchange-package.v1"u8))
            throw new ArgumentException("Unknown package framing.", nameof(encoded));
        var design = Utf8.GetString(reader.Read(MaximumPathBytes));
        var count = reader.ReadNumber();
        if (count is < 1 or > MaximumFiles)
            throw new ArgumentException("Invalid package count.", nameof(encoded));
        var files = new List<InterchangeFile>(count);
        var total = 0;
        for (var index = 0; index < count; index++)
        {
            var path = Utf8.GetString(reader.Read(MaximumPathBytes));
            var bytes = reader.Read(MaximumFileBytes);
            total = checked(total + bytes.Length);
            if (total > MaximumTotalBytes)
                throw new ArgumentException("Package aggregate byte limit exceeded.", nameof(encoded));
            files.Add(new InterchangeFile(path, bytes));
        }
        if (!reader.IsEmpty)
            throw new ArgumentException("Trailing package bytes.", nameof(encoded));
        var package = new InterchangePackage(design, files);
        if (!encoded.SequenceEqual(package.Encode()))
            throw new ArgumentException("Package framing must be canonical.", nameof(encoded));
        return package;
    }

    internal static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Utf8.GetByteCount(path) > MaximumPathBytes || path.Any(character =>
                char.IsControl(character) || character is '\\' or ':') ||
            path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Package paths must be safe relative UTF-8 paths.", nameof(path));
        return path;
    }

    private static void Write(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteNumber(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteNumber(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private ref struct PackageReader(ReadOnlySpan<byte> remaining)
    {
        private ReadOnlySpan<byte> _remaining = remaining;
        public bool IsEmpty => _remaining.IsEmpty;
        public int ReadNumber()
        {
            if (_remaining.Length < 4)
                throw new ArgumentException("Truncated package framing.");
            var value = BinaryPrimitives.ReadInt32BigEndian(_remaining);
            _remaining = _remaining[4..];
            return value;
        }
        public ReadOnlySpan<byte> Read(int maximum)
        {
            var length = ReadNumber();
            if (length < 0 || length > maximum || length > _remaining.Length)
                throw new ArgumentException("Invalid package field length.");
            var result = _remaining[..length];
            _remaining = _remaining[length..];
            return result;
        }
    }
}

public sealed class InterchangeFile
{
    private readonly byte[] _bytes;
    public InterchangeFile(string path, ReadOnlySpan<byte> bytes)
    {
        Path = InterchangePackage.ValidatePath(path);
        if (bytes.Length > InterchangePackage.MaximumFileBytes)
            throw new ArgumentException("Package file byte limit exceeded.", nameof(bytes));
        _bytes = bytes.ToArray();
        Digest = Sha256Digest.FromBytes(_bytes);
    }
    public string Path { get; }
    public int ByteCount => _bytes.Length;
    public Sha256Digest Digest { get; }
    // Span cannot expose the backing array through MemoryMarshal.TryGetArray.
    public ReadOnlySpan<byte> Bytes => _bytes;
}
