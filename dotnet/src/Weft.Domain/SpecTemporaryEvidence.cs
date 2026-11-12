using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Weft.Domain;

public enum SpecTemporaryPhase { Reserved = 1, Bound = 2, Prepared = 3 }

/// <summary>
/// Bounded physical evidence for a bundle-owned temporary. The registry binds these bytes to the exact logical claim.
/// This record is not cleanup authority and does not enable filesystem storage. All inode identities are observed, not paths.
/// </summary>
public sealed class SpecTemporaryEvidence
{
    public const int EncodedLength = 196;
    public SpecTemporaryEvidence(Guid bootId, Guid controlId, ulong namespaceDevice, ulong namespaceInode, uint userId,
        SpecInode root, SpecInode control, SpecInode parent, SpecInode gate, SpecInode? file)
    {
        if (bootId == Guid.Empty || controlId == Guid.Empty || namespaceInode == 0) throw new ArgumentException("Physical incarnation is required.");
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(gate);
        if (new[] { control, parent, gate }.Any(i => i.Mount != root.Mount || i.Device != root.Device) ||
            file is { } f && (f.Mount != parent.Mount || f.Device != parent.Device || f.Inode == parent.Inode))
            throw new ArgumentException("Evidence must remain within its admitted mount.");
        BootId = bootId; ControlId = controlId; NamespaceDevice = namespaceDevice; NamespaceInode = namespaceInode;
        UserId = userId; Root = root; Control = control; Parent = parent; Gate = gate; File = file;
    }
    public Guid BootId { get; }
    public Guid ControlId { get; }
    public ulong NamespaceDevice { get; }
    public ulong NamespaceInode { get; }
    public uint UserId { get; }
    public SpecInode Root { get; }
    public SpecInode Control { get; }
    public SpecInode Parent { get; }
    public SpecInode Gate { get; }
    public SpecInode? File { get; }

    public byte[] Encode()
    {
        var bytes = new byte[EncodedLength];
        "WST1"u8.CopyTo(bytes);
        BootId.TryWriteBytes(bytes.AsSpan(4, 16), true, out _);
        ControlId.TryWriteBytes(bytes.AsSpan(20, 16), true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(36), NamespaceDevice);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(44), NamespaceInode);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(52), UserId);
        var offset = 56;
        foreach (var inode in new[] { Root, Control, Parent, Gate, File })
        {
            if (inode is not null)
            {
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset), inode.Device);
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset + 8), inode.Mount);
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset + 16), inode.Inode);
            }
            offset += 28; // four reserved zero bytes per identity permit only this canonical v1 encoding
        }
        return bytes;
    }

    public static SpecTemporaryEvidence Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedLength || !bytes[..4].SequenceEqual("WST1"u8)) throw new ArgumentException("Invalid temporary evidence.");
        var ids = new SpecInode?[5];
        for (var i = 0; i < ids.Length; i++)
        {
            var part = bytes.Slice(56 + i * 28, 28);
            if (BinaryPrimitives.ReadUInt32BigEndian(part[24..]) != 0) throw new ArgumentException("Noncanonical temporary evidence.");
            var device = BinaryPrimitives.ReadUInt64BigEndian(part);
            var mount = BinaryPrimitives.ReadUInt64BigEndian(part[8..]);
            var inode = BinaryPrimitives.ReadUInt64BigEndian(part[16..]);
            if (i == 4 && device == 0 && mount == 0 && inode == 0) continue;
            ids[i] = new(device, mount, inode);
        }
        return new(new Guid(bytes.Slice(4, 16), true), new Guid(bytes.Slice(20, 16), true),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[36..]), BinaryPrimitives.ReadUInt64BigEndian(bytes[44..]),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[52..]), ids[0]!, ids[1]!, ids[2]!, ids[3]!, ids[4]);
    }
    public override string ToString() => nameof(SpecTemporaryEvidence);
}

public sealed record SpecInode
{
    public SpecInode(ulong device, ulong mount, ulong inode)
    {
        if (mount == 0 || inode == 0) throw new ArgumentException("An observed inode and mount are required.");
        Device = device; Mount = mount; Inode = inode;
    }
    public ulong Device { get; }
    public ulong Mount { get; }
    public ulong Inode { get; }
}

public static class SpecTemporarySeal
{
    public static Sha256Digest Digest(SpecPublicationClaim publication, Guid temporaryId, SpecTemporaryPhase phase,
        Sha256Digest? previous, SpecTemporaryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(publication); ArgumentNullException.ThrowIfNull(evidence);
        if (temporaryId == Guid.Empty || !Enum.IsDefined(phase) || (phase == SpecTemporaryPhase.Reserved) != (previous is null)
            || (phase == SpecTemporaryPhase.Reserved) != (evidence.File is null)) throw new ArgumentException("Invalid seal phase.");
        var reference = publication.Reference;
        var source = reference.Id.Source;
        var project = source.Bundle.Project;
        var text = string.Join('|', "weft.spec-seal.v1", publication.StoreId.ToString("D"), project.OrganizationId.ToString("D"),
            project.ProjectId.ToString("D"), source.Bundle.BundleId.ToString("D"), source.SourceId.ToString("D"),
            source.Version.ToString(CultureInfo.InvariantCulture), reference.Id.ObjectId.ToString("D"), ((int)reference.Id.Kind).ToString(CultureInfo.InvariantCulture),
            reference.Digest.Value, reference.ByteCount.ToString(CultureInfo.InvariantCulture), publication.AttemptId.ToString("D"), temporaryId.ToString("D"),
            ((int)phase).ToString(CultureInfo.InvariantCulture), previous?.Value ?? "");
        return Sha256Digest.FromBytes([.. Encoding.UTF8.GetBytes(text), .. evidence.Encode()]);
    }
}
