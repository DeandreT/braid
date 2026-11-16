using System.Buffers.Binary;
using System.Security.Cryptography;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.Blob;

internal enum BlobRecordKind : ushort { Absence = 1, Reservation = 2, Bound = 3, Prepared = 4 }

/// <summary>Facts every seal repeats: store, exact reference, temporary ID and the original owner's digest.</summary>
internal sealed record BlobPublicationContext
{
    internal BlobPublicationContext(Guid rootId, Guid controlId, PrivateObjectReference reference, Guid tempId,
        PrivateObjectOwnerKind ownerKind, Sha256Digest ownerDigest)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(ownerDigest);
        if (rootId == Guid.Empty || controlId == Guid.Empty || tempId == Guid.Empty || rootId == controlId)
            throw Unsafe();
        if (!Enum.IsDefined(ownerKind) ||
            (ownerKind == PrivateObjectOwnerKind.NativeIngestion) != (reference.Id.Class == PrivateObjectClass.NativeArtifact))
            throw Unsafe();
        var maximum = reference.Id.Class == PrivateObjectClass.NativeArtifact
            ? BlobStorageOptions.NativeHardMaximum : BlobStorageOptions.DerivedHardMaximum;
        if (reference.ByteCount > maximum || (reference.Id.Class == PrivateObjectClass.NativeArtifact && reference.ByteCount == 0))
            throw Unsafe();
        RootId = rootId;
        ControlId = controlId;
        Reference = reference;
        TempId = tempId;
        OwnerKind = ownerKind;
        OwnerDigest = ownerDigest;
    }

    internal Guid RootId { get; }
    internal Guid ControlId { get; }
    internal PrivateObjectReference Reference { get; }
    internal Guid TempId { get; }
    internal PrivateObjectOwnerKind OwnerKind { get; }
    internal Sha256Digest OwnerDigest { get; }

    internal static BlobPublicationContext For(Guid rootId, Guid controlId, PrivateObjectReference reference, Guid tempId,
        PrivatePublicationOwner owner) => new(rootId, controlId, reference, tempId, owner.Kind, BlobRecordCodec.OwnerDigest(owner));

    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);
}

/// <summary>
/// One immutable seal. Absence has no suffix; Reservation retains the database reservation;
/// Bound retains the attempt marker's incarnation and entity tag; Prepared retains the verified
/// payload facts staged on the final name.
/// </summary>
internal sealed record BlobPublicationRecord(BlobRecordKind Kind, BlobPublicationContext Context,
    Sha256Digest? PreviousDigest = null, Guid? PublicationId = null, DateTimeOffset? TimeUtc = null,
    ulong? Incarnation = null, string? AttemptETag = null, Sha256Digest? VerifiedDigest = null,
    long? VerifiedByteCount = null, int? BlockCount = null);

/// <summary>Canonical big-endian seal encoding. Decoding rejects every extra, missing or noncanonical byte.</summary>
internal static class BlobRecordCodec
{
    internal const int MaximumRecordBytes = 1024;
    private const int HeaderBytes = 17;
    private const int CommonBytes = 16 + 16 + 1 + (6 * 16) + 32 + 8 + 1 + 32;
    private const int MaximumETagBytes = 128;
    private static ReadOnlySpan<byte> Magic => "BRAIDB1R\n"u8;
    private static ReadOnlySpan<byte> OwnerMagic => "BRAIDB1O\n"u8;

    internal static byte[] Encode(BlobPublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var etag = record.AttemptETag is { } text ? System.Text.Encoding.ASCII.GetBytes(text) : [];
        var suffix = record.Kind switch
        {
            BlobRecordKind.Absence => 0,
            BlobRecordKind.Reservation => 32 + 16 + 8,
            BlobRecordKind.Bound => 32 + 16 + 8 + 2 + etag.Length,
            BlobRecordKind.Prepared => 32 + 16 + 8 + 8 + 32 + 8 + 4,
            _ => throw Unsafe()
        };
        Validate(record);
        var bytes = new byte[HeaderBytes + CommonBytes + suffix];
        var writer = new Writer(bytes);
        writer.Bytes(Magic);
        writer.UInt16((ushort)record.Kind);
        writer.UInt16(1);
        writer.UInt32((uint)bytes.Length);
        var context = record.Context;
        writer.Id(context.RootId);
        writer.Id(context.ControlId);
        writer.Byte(context.Reference.Id.Class == PrivateObjectClass.NativeArtifact ? (byte)1 : (byte)2);
        var scope = context.Reference.Id.Scope;
        writer.Id(scope.OrganizationId);
        writer.Id(scope.ProjectId);
        writer.Id(scope.MappingId);
        writer.Id(scope.RevisionId);
        writer.Id(context.Reference.Id.ObjectId);
        writer.Id(context.TempId);
        writer.Digest(context.Reference.Digest);
        writer.Int64(context.Reference.ByteCount);
        writer.Byte((byte)context.OwnerKind);
        writer.Digest(context.OwnerDigest);
        switch (record.Kind)
        {
            case BlobRecordKind.Reservation:
                writer.Digest(record.PreviousDigest!);
                writer.Id(record.PublicationId!.Value);
                writer.Int64(record.TimeUtc!.Value.UtcTicks);
                break;
            case BlobRecordKind.Bound:
                writer.Digest(record.PreviousDigest!);
                writer.Id(record.PublicationId!.Value);
                writer.UInt64(record.Incarnation!.Value);
                writer.UInt16((ushort)etag.Length);
                writer.Bytes(etag);
                break;
            case BlobRecordKind.Prepared:
                writer.Digest(record.PreviousDigest!);
                writer.Id(record.PublicationId!.Value);
                writer.Int64(record.TimeUtc!.Value.UtcTicks);
                writer.UInt64(record.Incarnation!.Value);
                writer.Digest(record.VerifiedDigest!);
                writer.Int64(record.VerifiedByteCount!.Value);
                writer.UInt32((uint)record.BlockCount!.Value);
                break;
        }
        writer.End();
        return bytes;
    }

    internal static BlobPublicationRecord Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (bytes.Length is < HeaderBytes + CommonBytes or > MaximumRecordBytes || !bytes[..Magic.Length].SequenceEqual(Magic))
                throw Unsafe();
            var reader = new Reader(bytes);
            reader.Skip(Magic.Length);
            var kind = (BlobRecordKind)reader.UInt16();
            if (!Enum.IsDefined(kind) || reader.UInt16() != 1 || reader.UInt32() != bytes.Length) throw Unsafe();
            var rootId = reader.Id();
            var controlId = reader.Id();
            var objectClass = reader.Byte() switch
            {
                1 => PrivateObjectClass.NativeArtifact,
                2 => PrivateObjectClass.DerivedArtifact,
                _ => throw Unsafe()
            };
            var scope = new RevisionScope(reader.Id(), reader.Id(), reader.Id(), reader.Id());
            var objectId = reader.Id();
            var tempId = reader.Id();
            var digest = reader.Digest();
            var count = reader.Int64();
            var ownerKind = (PrivateObjectOwnerKind)reader.Byte();
            var ownerDigest = reader.Digest();
            if (count < 0) throw Unsafe();
            var context = new BlobPublicationContext(rootId, controlId,
                new(PrivateObjectId.Restore(scope, objectId, objectClass), digest, count), tempId, ownerKind, ownerDigest);
            var record = kind switch
            {
                BlobRecordKind.Absence => new BlobPublicationRecord(kind, context),
                BlobRecordKind.Reservation => new BlobPublicationRecord(kind, context, reader.Digest(), reader.Id(), reader.Time()),
                BlobRecordKind.Bound => Bound(context, ref reader),
                BlobRecordKind.Prepared => new BlobPublicationRecord(kind, context, reader.Digest(), reader.Id(), reader.Time(),
                    reader.UInt64(), null, reader.Digest(), reader.Int64(), checked((int)reader.UInt32())),
                _ => throw Unsafe()
            };
            reader.End();
            Validate(record);
            // A second canonical encoding rejects any alternate representation.
            if (!bytes.SequenceEqual(Encode(record))) throw Unsafe();
            return record;
        }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
        { throw Unsafe(); }
    }

    internal static Sha256Digest Digest(ReadOnlySpan<byte> bytes) => Sha256Digest.FromBytes(bytes);

    /// <summary>A canonical digest of every fact that defines the original owner, including the full report key.</summary>
    internal static Sha256Digest OwnerDigest(PrivatePublicationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var value = new byte[16];
        hash.AppendData(OwnerMagic);
        hash.AppendData([(byte)owner.Kind]);
        Id(owner.OriginalActor.Value);
        Id(owner.ServerInstanceId);
        Id(owner.LeaseToken);
        Int64(owner.Generation);
        Int64(owner.LeaseExpiresAtUtc.UtcTicks);
        if (owner.IngestionClaim is { } ingestion)
        {
            Id(ingestion.Project.OrganizationId);
            Id(ingestion.Project.ProjectId);
            Id(ingestion.OperationId);
        }
        else
        {
            var claim = owner.ReportClaim!;
            Id(claim.CacheEntryId);
            Id(claim.DerivationId);
            Int64(claim.StartedAtUtc.UtcTicks);
            hash.AppendData([claim.RepairTargetDerivationId is null ? (byte)0 : (byte)1]);
            if (claim.RepairTargetDerivationId is { } repair) Id(repair);
            Id(claim.CandidateObjectId.ObjectId);
            var key = claim.Key.CopyCanonicalUtf8();
            BinaryPrimitives.WriteUInt32BigEndian(value, (uint)key.Length);
            hash.AppendData(value, 0, 4);
            hash.AppendData(key);
        }
        return Sha256Digest.Parse(Convert.ToHexStringLower(hash.GetHashAndReset()));

        void Id(Guid id)
        {
            id.TryWriteBytes(value, bigEndian: true, out _);
            hash.AppendData(value);
        }
        void Int64(long number)
        {
            BinaryPrimitives.WriteInt64BigEndian(value, number);
            hash.AppendData(value, 0, 8);
        }
    }

    private static BlobPublicationRecord Bound(BlobPublicationContext context, ref Reader reader)
    {
        var previous = reader.Digest();
        var publicationId = reader.Id();
        var incarnation = reader.UInt64();
        var length = reader.UInt16();
        var etag = reader.Ascii(length);
        return new(BlobRecordKind.Bound, context, previous, publicationId, null, incarnation, etag);
    }

    private static void Validate(BlobPublicationRecord record)
    {
        (bool Previous, bool Publication, bool Time, bool Incarnation, bool ETag, bool Verified) expected = record.Kind switch
        {
            BlobRecordKind.Absence => (false, false, false, false, false, false),
            BlobRecordKind.Reservation => (true, true, true, false, false, false),
            BlobRecordKind.Bound => (true, true, false, true, true, false),
            BlobRecordKind.Prepared => (true, true, true, true, false, true),
            _ => throw Unsafe()
        };
        if ((record.PreviousDigest is not null) != expected.Previous || (record.PublicationId is not null) != expected.Publication ||
            (record.TimeUtc is not null) != expected.Time || (record.Incarnation is not null) != expected.Incarnation ||
            (record.AttemptETag is not null) != expected.ETag || (record.VerifiedDigest is not null) != expected.Verified ||
            (record.VerifiedByteCount is not null) != expected.Verified || (record.BlockCount is not null) != expected.Verified)
            throw Unsafe();
        if (record.PublicationId == Guid.Empty || record.Incarnation == 0 ||
            (record.TimeUtc is { } time && (time.Offset != TimeSpan.Zero || time.UtcTicks <= 0)) ||
            (record.AttemptETag is { } etag && (etag.Length is < 1 or > MaximumETagBytes ||
                etag.Any(value => value is < (char)0x21 or > (char)0x7e))) ||
            record.VerifiedByteCount < 0 || record.BlockCount < 0 ||
            (record.VerifiedByteCount is not null && record.VerifiedByteCount != record.Context.Reference.ByteCount) ||
            (record.VerifiedDigest is { } digest && digest != record.Context.Reference.Digest))
            throw Unsafe();
    }

    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);

    private ref struct Writer(Span<byte> bytes)
    {
        private readonly Span<byte> _bytes = bytes;
        private int _offset;
        internal void Bytes(ReadOnlySpan<byte> value) { value.CopyTo(_bytes[_offset..]); _offset += value.Length; }
        internal void Byte(byte value) => _bytes[_offset++] = value;
        internal void UInt16(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(_bytes[_offset..], value); _offset += 2; }
        internal void UInt32(uint value) { BinaryPrimitives.WriteUInt32BigEndian(_bytes[_offset..], value); _offset += 4; }
        internal void UInt64(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(_bytes[_offset..], value); _offset += 8; }
        internal void Int64(long value) { BinaryPrimitives.WriteInt64BigEndian(_bytes[_offset..], value); _offset += 8; }
        internal void Id(Guid value) { value.TryWriteBytes(_bytes[_offset..], bigEndian: true, out _); _offset += 16; }
        internal void Digest(Sha256Digest value) => Bytes(Convert.FromHexString(value.Value));
        internal readonly void End() { if (_offset != _bytes.Length) throw Unsafe(); }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _offset + count > _bytes.Length) throw Unsafe();
            var value = _bytes.Slice(_offset, count);
            _offset += count;
            return value;
        }
        internal void Skip(int count) => Take(count);
        internal byte Byte() => Take(1)[0];
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal long Int64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
        internal Guid Id()
        {
            var value = new Guid(Take(16), bigEndian: true);
            return value == Guid.Empty ? throw Unsafe() : value;
        }
        internal Sha256Digest Digest() => Sha256Digest.Parse(Convert.ToHexStringLower(Take(32)));
        internal DateTimeOffset Time() => new(Int64(), TimeSpan.Zero);
        internal string Ascii(int count) => System.Text.Encoding.ASCII.GetString(Take(count));
        internal readonly void End() { if (_offset != _bytes.Length) throw Unsafe(); }
    }
}
