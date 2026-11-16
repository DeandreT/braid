using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

internal static partial class V2PublicationRecordCodec
{
    internal const int MaximumRecordBytes = 65536;
    internal const int MaximumKeyBytes = 32768;

    internal static byte[] Encode(V2PublicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var writer = new RecordWriter();
        writer.Bytes("BRAIDV2R\n"u8);
        writer.UInt16((ushort)record.Kind);
        writer.UInt16(1);
        writer.UInt32(0); // Total length is filled after the complete bounded record is written.
        WriteContext(ref writer, record.Context);
        switch (record)
        {
            case V2AbsenceRecord:
                break;
            case V2ReservationRecord reservation:
                writer.Digest(reservation.AbsenceRecordDigest);
                writer.Identity(reservation.AbsenceRecordIdentity);
                writer.Id(reservation.PublicationId);
                writer.Time(reservation.RegisteredAtUtc);
                break;
            case V2BoundRecord bound:
                writer.Digest(bound.ReservationRecordDigest);
                writer.Identity(bound.ReservationRecordIdentity);
                writer.Id(bound.PublicationId);
                writer.Identity(bound.DataFileIdentity);
                break;
            case V2PreparedRecord prepared:
                writer.Digest(prepared.BoundRecordDigest);
                writer.Identity(prepared.BoundRecordIdentity);
                writer.Id(prepared.PublicationId);
                writer.Time(prepared.BoundAtUtc);
                writer.Identity(prepared.DataFileIdentity);
                writer.Digest(prepared.VerifiedDigest);
                writer.Int64(prepared.VerifiedByteCount);
                break;
            default:
                throw Unsafe();
        }
        return writer.Complete();
    }

    internal static V2PublicationRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 17 or > MaximumRecordBytes)
            throw Unsafe();
        try
        {
            var reader = new RecordReader(bytes);
            if (!reader.Bytes(9).SequenceEqual("BRAIDV2R\n"u8))
                throw Unsafe();
            var kind = (V2RecordKind)reader.UInt16();
            if (reader.UInt16() != 1 || reader.UInt32() != (uint)bytes.Length)
                throw Unsafe();
            var context = ReadContext(ref reader);
            V2PublicationRecord record = kind switch
            {
                V2RecordKind.Absence => new V2AbsenceRecord(context),
                V2RecordKind.Reservation => new V2ReservationRecord(context, reader.Digest(), reader.Identity(),
                    reader.Id(), reader.Time()),
                V2RecordKind.Bound => new V2BoundRecord(context, reader.Digest(), reader.Identity(),
                    reader.Id(), reader.Identity()),
                V2RecordKind.Prepared => new V2PreparedRecord(context, reader.Digest(), reader.Identity(),
                    reader.Id(), reader.Time(), reader.Identity(), reader.Digest(), reader.Int64()),
                _ => throw Unsafe()
            };
            if (!reader.IsAtEnd)
                throw Unsafe();
            return record;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            FormatException or OverflowException)
        {
            throw Unsafe();
        }
    }

    private static void WriteContext(ref RecordWriter writer, V2PublicationContext context)
    {
        writer.Id(context.RootId);
        writer.Id(context.ControlNamespaceId);
        var reference = context.Reference;
        writer.Byte((byte)reference.Id.Class);
        writer.Id(reference.Id.Scope.OrganizationId);
        writer.Id(reference.Id.Scope.ProjectId);
        writer.Id(reference.Id.Scope.MappingId);
        writer.Id(reference.Id.Scope.RevisionId);
        writer.Id(reference.Id.ObjectId);
        writer.Id(context.TempId);
        writer.Digest(reference.Digest);
        writer.Int64(reference.ByteCount);
        var owner = context.Owner;
        writer.Byte((byte)owner.Kind);
        writer.Id(owner.OriginalActor.Value);
        writer.Id(owner.ServerInstanceId);
        writer.Id(owner.LeaseToken);
        writer.Int64(owner.Generation);
        writer.Time(owner.LeaseExpiresAtUtc);
        if (owner.IngestionClaim is { } native)
            writer.Id(native.OperationId);
        else if (owner.ReportClaim is { } report)
        {
            writer.Id(report.CacheEntryId);
            writer.Id(report.DerivationId);
            writer.Time(report.StartedAtUtc);
            writer.Byte(report.RepairTargetDerivationId.HasValue ? (byte)1 : (byte)0);
            if (report.RepairTargetDerivationId is { } repair)
                writer.Id(repair);
            var keyBytes = V2ReportKeyCodec.Encode(report.Key);
            writer.UInt32((uint)keyBytes.Length);
            writer.Bytes(keyBytes);
            writer.Digest(Sha256Digest.FromBytes(keyBytes));
        }
        else
            throw Unsafe();
        writer.Id(context.BootId);
        writer.UInt64(context.MountNamespaceDevice);
        writer.UInt64(context.MountNamespaceInode);
        writer.UInt32(context.EffectiveUserId);
        writer.Identity(context.RootIdentity);
        writer.Identity(context.ControlIdentity);
        writer.Identity(context.ObjectDirectoryIdentity);
        writer.Identity(context.TempDirectoryIdentity);
        writer.Identity(context.DataParentIdentity);
        writer.Identity(context.TempGateIdentity);
    }

    private static V2PublicationContext ReadContext(ref RecordReader reader)
    {
        var rootId = reader.Id();
        var controlId = reader.Id();
        var objectClass = (PrivateObjectClass)reader.Byte();
        if (objectClass is not (PrivateObjectClass.NativeArtifact or PrivateObjectClass.DerivedArtifact))
            throw Unsafe();
        var scope = new RevisionScope(reader.Id(), reader.Id(), reader.Id(), reader.Id());
        var objectId = PrivateObjectId.Restore(scope, reader.Id(), objectClass);
        var tempId = reader.Id();
        var reference = new PrivateObjectReference(objectId, reader.Digest(), reader.Int64());
        var ownerKind = (PrivateObjectOwnerKind)reader.Byte();
        var principal = new PrincipalId(reader.Id());
        var actor = new ActorContext(principal);
        var instance = reader.Id();
        var token = reader.Id();
        var generation = reader.Int64();
        var expiry = reader.Time();
        PrivatePublicationOwner owner;
        if (ownerKind == PrivateObjectOwnerKind.NativeIngestion)
            owner = PrivatePublicationOwner.ForIngestion(actor, new IngestionClaim(
                new ProjectScope(scope.OrganizationId, scope.ProjectId), reader.Id(), principal,
                instance, token, generation, expiry));
        else if (ownerKind == PrivateObjectOwnerKind.ReportAttempt)
        {
            var cacheId = reader.Id();
            var derivationId = reader.Id();
            var started = reader.Time();
            var repairFlag = reader.Byte();
            if (repairFlag > 1)
                throw Unsafe();
            Guid? repair = repairFlag == 1 ? reader.Id() : null;
            var length = reader.UInt32();
            if (length is < 1 or > MaximumKeyBytes)
                throw Unsafe();
            var keyBytes = reader.Bytes((int)length);
            var key = V2ReportKeyCodec.Decode(keyBytes);
            if (reader.Digest() != Sha256Digest.FromBytes(keyBytes))
                throw Unsafe();
            owner = PrivatePublicationOwner.ForReport(actor, new RevisionReportClaim(cacheId, derivationId,
                key, principal, instance, token, generation, started, expiry, objectId, repair));
        }
        else
            throw Unsafe();
        return new(rootId, controlId, reference, tempId, owner, reader.Id(), reader.UInt64(), reader.UInt64(),
            reader.UInt32(), reader.Identity(), reader.Identity(), reader.Identity(), reader.Identity(),
            reader.Identity(), reader.Identity());
    }

    private static PrivateObjectStorageException Unsafe() => new(PrivateObjectStorageError.UnsafeStorage);
}
