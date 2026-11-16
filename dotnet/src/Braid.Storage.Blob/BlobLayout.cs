using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.Blob;

/// <summary>Every blob name is generated from typed nonzero UUIDs; no caller text reaches a name.</summary>
internal static class BlobLayout
{
    internal const string MarkerName = ".braid-private-storage";
    internal const string MarkerPrefix = "braid-private-storage-blob-v1\n";
    internal const int MarkerLength = 30 + 33 + 33;

    internal static string ClassName(PrivateObjectClass objectClass) => objectClass switch
    {
        PrivateObjectClass.NativeArtifact => "native",
        PrivateObjectClass.DerivedArtifact => "derived",
        _ => throw new PrivateObjectStorageException(PrivateObjectStorageError.Unsupported)
    };

    internal static string ProjectPrefix(PrivateObjectClass objectClass, ProjectScope project) =>
        $"objects/{ClassName(objectClass)}/{project.OrganizationId:N}/{project.ProjectId:N}/";

    internal static string RevisionPrefix(PrivateObjectClass objectClass, RevisionScope scope) =>
        ProjectPrefix(objectClass, new(scope.OrganizationId, scope.ProjectId)) + $"{scope.MappingId:N}/{scope.RevisionId:N}/";

    internal static string FinalName(PrivateObjectId id) => RevisionPrefix(id.Class, id.Scope) + FinalLeaf(id.ObjectId);
    internal static string FinalLeaf(Guid objectId) => $"{objectId:N}.blob";
    internal static string AttemptName(PrivateObjectId id, Guid tempId) => RevisionPrefix(id.Class, id.Scope) + AttemptLeaf(tempId);
    internal static string AttemptLeaf(Guid tempId) => $".attempt-{tempId:N}.tmp";

    internal static string ObjectLeaf(Guid controlId, PrivateObjectId id) =>
        $"control/{controlId:N}/{ClassName(id.Class)}/{id.Scope.OrganizationId:N}/{id.Scope.ProjectId:N}/" +
        $"{id.Scope.MappingId:N}/{id.Scope.RevisionId:N}/{id.ObjectId:N}/";

    // The lease must protect the actual commit destination, not a different blob.
    internal static string ObjectGate(Guid controlId, PrivateObjectId id) => FinalName(id);
    internal static string TempLeaf(Guid controlId, PrivateObjectId id, Guid tempId) =>
        ObjectLeaf(controlId, id) + $"publications/{tempId:N}/";
    internal static string TempGate(Guid controlId, PrivateObjectId id, Guid tempId) => TempLeaf(controlId, id, tempId) + "temp.gate";
    internal static string Seal(Guid controlId, PrivateObjectId id, Guid tempId, BlobRecordKind kind) =>
        TempLeaf(controlId, id, tempId) + kind switch
        {
            BlobRecordKind.Absence => "absence",
            BlobRecordKind.Reservation => "reservation",
            BlobRecordKind.Bound => "bound",
            BlobRecordKind.Prepared => "prepared",
            _ => throw new PrivateObjectStorageException(PrivateObjectStorageError.Unsupported)
        };

    internal static string Probe(Guid controlId, Guid probeId) => $"control/{controlId:N}/probes/{probeId:N}";

    internal static byte[] Marker(Guid rootId, Guid controlId) =>
        Encoding.ASCII.GetBytes(MarkerPrefix + rootId.ToString("N") + "\n" + controlId.ToString("N") + "\n");

    internal static bool TryCanonicalId(ReadOnlySpan<char> text, out Guid id)
    {
        id = default;
        if (text.Length != 32) return false;
        foreach (var value in text)
            if (value is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return Guid.TryParseExact(text, "N", out id) && id != Guid.Empty;
    }
}

/// <summary>
/// The fixed mapping from blob facts onto the existing v2 publication identity record.
/// Blob storage has no boot, namespace, owner or inode: the profile identifier fills the
/// boot field, fixed values fill the namespace and owner fields, each data blob carries a
/// random nonzero incarnation as its file identity, and each virtual directory has a
/// deterministic identity derived from the store root and its exact prefix.
/// </summary>
internal static class BlobIdentity
{
    /// <summary>ASCII "BraidBlob" followed by profile version 1.</summary>
    internal static readonly Guid ProfileId = Guid.ParseExact("4272616964426c6f6200010000000001", "N");
    internal const ulong NamespaceDevice = 0;
    internal const ulong NamespaceInode = 1;
    internal const uint OwnerId = 0;
    internal const ushort DirectoryMode = 0x41c0;
    internal const ushort FinalMode = 0x8100;
    internal const ushort AttemptMode = 0x8180;

    internal static ulong MountId(Guid controlId)
    {
        Span<byte> bytes = stackalloc byte[16];
        controlId.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes) | 1;
    }

    internal static ulong DirectoryInode(Guid rootId, string prefix)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes("braid-blob-directory\n" + rootId.ToString("N") + "\n" + prefix));
        return BinaryPrimitives.ReadUInt64BigEndian(digest) | 1;
    }

    internal static PrivatePublicationInodeIdentity Directory(Guid rootId, Guid controlId, string prefix) =>
        new(DirectoryInode(rootId, prefix), 0, 0, MountId(controlId));

    internal static PrivatePublicationInodeIdentity File(Guid controlId, ulong incarnation) =>
        new(incarnation, 0, 0, MountId(controlId));

    internal static PrivatePublicationFileIdentity Publication(Guid rootId, Guid controlId, PrivateObjectClass objectClass,
        RevisionScope scope, ulong incarnation) => new(rootId, controlId, ProfileId, NamespaceDevice, NamespaceInode, OwnerId,
            File(controlId, incarnation), Directory(rootId, controlId, BlobLayout.RevisionPrefix(objectClass, scope)));

    internal static ulong NewIncarnation(Guid rootId, PrivateObjectClass objectClass, RevisionScope scope)
    {
        var directory = DirectoryInode(rootId, BlobLayout.RevisionPrefix(objectClass, scope));
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            if (value != 0 && value != directory) return value;
        }
    }

    internal static string Format(ulong incarnation) => incarnation.ToString("x16", CultureInfo.InvariantCulture);

    internal static bool TryParse(string? text, out ulong incarnation)
    {
        incarnation = 0;
        return text is { Length: 16 } && text.All(value => value is (>= '0' and <= '9') or (>= 'a' and <= 'f')) &&
            ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out incarnation) && incarnation != 0;
    }
}

internal enum BlobForm { Unknown = 0, Marker, Gate, Seal, Attempt, Object, Retired, Probe }

/// <summary>Exact closed metadata forms. Extra, missing or noncanonical values are an unknown form.</summary>
internal readonly record struct BlobFacts(BlobForm Form, ETag ETag, long Length, ulong Incarnation, string? Sha256, long DeclaredLength)
{
    internal const string KindKey = "braidkind";
    internal const string IncarnationKey = "braidincarnation";
    internal const string DigestKey = "braidsha256";
    internal const string LengthKey = "braidlength";

    internal static Dictionary<string, string> Metadata(BlobForm form) => new(StringComparer.Ordinal) { [KindKey] = Name(form) };

    internal static Dictionary<string, string> AttemptMetadata(ulong incarnation) => new(StringComparer.Ordinal)
    {
        [KindKey] = Name(BlobForm.Attempt),
        [IncarnationKey] = BlobIdentity.Format(incarnation)
    };

    internal static Dictionary<string, string> ObjectMetadata(ulong incarnation, PrivateObjectReference reference) => new(StringComparer.Ordinal)
    {
        [KindKey] = Name(BlobForm.Object),
        [IncarnationKey] = BlobIdentity.Format(incarnation),
        [DigestKey] = reference.Digest.Value,
        [LengthKey] = reference.ByteCount.ToString(CultureInfo.InvariantCulture)
    };

    internal static BlobFacts Classify(ETag etag, long length, IDictionary<string, string>? metadata)
    {
        var unknown = new BlobFacts(BlobForm.Unknown, etag, length, 0, null, -1);
        if (metadata is null) return unknown;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            var key = pair.Key.ToLowerInvariant();
            if (!values.TryAdd(key, pair.Value)) return unknown;
        }
        if (!values.TryGetValue(KindKey, out var kind)) return unknown;
        var form = kind switch
        {
            "marker" => BlobForm.Marker,
            "gate" => BlobForm.Gate,
            "seal" => BlobForm.Seal,
            "attempt" => BlobForm.Attempt,
            "object" => BlobForm.Object,
            "retired" => BlobForm.Retired,
            "probe" => BlobForm.Probe,
            _ => BlobForm.Unknown
        };
        switch (form)
        {
            case BlobForm.Unknown:
                return unknown;
            case BlobForm.Attempt:
                return values.Count == 2 && length == 0 &&
                    BlobIdentity.TryParse(values.GetValueOrDefault(IncarnationKey), out var attempt)
                    ? new(form, etag, length, attempt, null, 0) : unknown;
            case BlobForm.Object:
                if (values.Count != 4 || !BlobIdentity.TryParse(values.GetValueOrDefault(IncarnationKey), out var incarnation) ||
                    values.GetValueOrDefault(DigestKey) is not { Length: 64 } digest ||
                    digest.Any(value => value is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) ||
                    values.GetValueOrDefault(LengthKey) is not { Length: >= 1 and <= 8 } declared ||
                    (declared.Length > 1 && declared[0] == '0') ||
                    !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    count > BlobStorageOptions.DerivedHardMaximum)
                    return unknown;
                return new(form, etag, length, incarnation, digest, count);
            default:
                // Fences, gates and probes carry no content; seals and the marker carry bounded records.
                if (values.Count != 1) return unknown;
                if (form is BlobForm.Gate or BlobForm.Retired or BlobForm.Probe && length != 0) return unknown;
                if (form is BlobForm.Seal or BlobForm.Marker && length is < 1 or > BlobRecordCodec.MaximumRecordBytes) return unknown;
                return new(form, etag, length, 0, null, 0);
        }
    }

    private static string Name(BlobForm form) => form switch
    {
        BlobForm.Marker => "marker",
        BlobForm.Gate => "gate",
        BlobForm.Seal => "seal",
        BlobForm.Attempt => "attempt",
        BlobForm.Object => "object",
        BlobForm.Retired => "retired",
        BlobForm.Probe => "probe",
        _ => throw new ArgumentOutOfRangeException(nameof(form))
    };
}
