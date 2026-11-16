using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

internal enum V2RecordKind : ushort { Absence = 1, Reservation = 2, Bound = 3, Prepared = 4 }

// These are private retained facts, never a grant or a replacement for registry admission.
internal sealed record V2PublicationContext
{
    internal V2PublicationContext(Guid rootId, Guid controlNamespaceId, PrivateObjectReference reference,
        Guid tempId, PrivatePublicationOwner owner, Guid bootId, ulong mountNamespaceDevice,
        ulong mountNamespaceInode, uint effectiveUserId, PrivatePublicationInodeIdentity rootIdentity,
        PrivatePublicationInodeIdentity controlIdentity, PrivatePublicationInodeIdentity objectDirectoryIdentity,
        PrivatePublicationInodeIdentity tempDirectoryIdentity, PrivatePublicationInodeIdentity dataParentIdentity,
        PrivatePublicationInodeIdentity tempGateIdentity)
    {
        RootId = V2RecordValue.Id(rootId);
        ControlNamespaceId = V2RecordValue.Id(controlNamespaceId);
        TempId = V2RecordValue.Id(tempId);
        BootId = V2RecordValue.Id(bootId);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        ArgumentNullException.ThrowIfNull(controlIdentity);
        ArgumentNullException.ThrowIfNull(objectDirectoryIdentity);
        ArgumentNullException.ThrowIfNull(tempDirectoryIdentity);
        ArgumentNullException.ThrowIfNull(dataParentIdentity);
        ArgumentNullException.ThrowIfNull(tempGateIdentity);
        if (mountNamespaceInode == 0)
            throw new ArgumentException("A publication context requires an observed namespace identity.");
        var maximum = reference.Id.Class == PrivateObjectClass.NativeArtifact ? 4194304L : 16777216L;
        if (reference.ByteCount > maximum ||
            (reference.Id.Class == PrivateObjectClass.NativeArtifact && reference.ByteCount == 0))
            throw new ArgumentException("A publication reference must fit its object class.");
        _ = owner.OriginFor(reference.Id);
        V2RecordValue.Utc(owner.LeaseExpiresAtUtc);
        Reference = reference;
        Owner = owner;
        MountNamespaceDevice = mountNamespaceDevice;
        MountNamespaceInode = mountNamespaceInode;
        EffectiveUserId = effectiveUserId;
        RootIdentity = rootIdentity;
        ControlIdentity = controlIdentity;
        ObjectDirectoryIdentity = objectDirectoryIdentity;
        TempDirectoryIdentity = tempDirectoryIdentity;
        DataParentIdentity = dataParentIdentity;
        TempGateIdentity = tempGateIdentity;
        PrivatePublicationInodeIdentity[] identities = [rootIdentity, controlIdentity, objectDirectoryIdentity,
            tempDirectoryIdentity, dataParentIdentity, tempGateIdentity];
        for (var index = 0; index < identities.Length; index++)
        {
            V2RecordValue.SameMount(rootIdentity, identities[index]);
            for (var previous = 0; previous < index; previous++)
                if (identities[previous].Inode == identities[index].Inode)
                    throw new ArgumentException("Publication locations must have distinct observed identities.");
        }
    }

    internal Guid RootId { get; }
    internal Guid ControlNamespaceId { get; }
    internal PrivateObjectReference Reference { get; }
    internal Guid TempId { get; }
    internal PrivatePublicationOwner Owner { get; }
    internal Guid BootId { get; }
    internal ulong MountNamespaceDevice { get; }
    internal ulong MountNamespaceInode { get; }
    internal uint EffectiveUserId { get; }
    internal PrivatePublicationInodeIdentity RootIdentity { get; }
    internal PrivatePublicationInodeIdentity ControlIdentity { get; }
    internal PrivatePublicationInodeIdentity ObjectDirectoryIdentity { get; }
    internal PrivatePublicationInodeIdentity TempDirectoryIdentity { get; }
    internal PrivatePublicationInodeIdentity DataParentIdentity { get; }
    internal PrivatePublicationInodeIdentity TempGateIdentity { get; }

    internal PrivatePublicationFileIdentity CreateFileIdentity(PrivatePublicationInodeIdentity fileIdentity)
    {
        RequireSeparateFile(fileIdentity);
        return new(RootId, ControlNamespaceId, BootId, MountNamespaceDevice, MountNamespaceInode,
            EffectiveUserId, fileIdentity, DataParentIdentity);
    }

    internal void RequireSeparateFile(PrivatePublicationInodeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        V2RecordValue.SameMount(RootIdentity, identity);
        if (identity.Inode == RootIdentity.Inode || identity.Inode == ControlIdentity.Inode ||
            identity.Inode == ObjectDirectoryIdentity.Inode || identity.Inode == TempDirectoryIdentity.Inode ||
            identity.Inode == DataParentIdentity.Inode || identity.Inode == TempGateIdentity.Inode)
            throw new ArgumentException("A publication file must retain its own observed identity.");
    }

    internal void RequireWithinLease(DateTimeOffset value)
    {
        V2RecordValue.Utc(value);
        var start = Owner.ReportClaim?.StartedAtUtc ??
            Owner.LeaseExpiresAtUtc.AddSeconds(-IngestionWorkflowLimits.ClaimLeaseSeconds);
        if (value < start || value >= Owner.LeaseExpiresAtUtc)
            throw new ArgumentException("Publication time must be within its original owner's lease.");
    }
}

internal abstract record V2PublicationRecord
{
    private protected V2PublicationRecord(V2RecordKind kind, V2PublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Kind = kind;
        Context = context;
    }

    internal V2RecordKind Kind { get; }
    internal V2PublicationContext Context { get; }
}

internal sealed record V2AbsenceRecord : V2PublicationRecord
{
    internal V2AbsenceRecord(V2PublicationContext context) : base(V2RecordKind.Absence, context) { }
}

internal sealed record V2ReservationRecord : V2PublicationRecord
{
    internal V2ReservationRecord(V2PublicationContext context, Sha256Digest absenceRecordDigest,
        PrivatePublicationInodeIdentity absenceRecordIdentity, Guid publicationId, DateTimeOffset registeredAtUtc)
        : base(V2RecordKind.Reservation, context)
    {
        ArgumentNullException.ThrowIfNull(absenceRecordDigest);
        context.RequireSeparateFile(absenceRecordIdentity);
        context.RequireWithinLease(registeredAtUtc);
        AbsenceRecordDigest = absenceRecordDigest;
        AbsenceRecordIdentity = absenceRecordIdentity;
        PublicationId = V2RecordValue.Id(publicationId);
        RegisteredAtUtc = registeredAtUtc;
    }

    internal Sha256Digest AbsenceRecordDigest { get; }
    internal PrivatePublicationInodeIdentity AbsenceRecordIdentity { get; }
    internal Guid PublicationId { get; }
    internal DateTimeOffset RegisteredAtUtc { get; }
}

internal sealed record V2BoundRecord : V2PublicationRecord
{
    internal V2BoundRecord(V2PublicationContext context, Sha256Digest reservationRecordDigest,
        PrivatePublicationInodeIdentity reservationRecordIdentity, Guid publicationId,
        PrivatePublicationInodeIdentity dataFileIdentity) : base(V2RecordKind.Bound, context)
    {
        ArgumentNullException.ThrowIfNull(reservationRecordDigest);
        context.RequireSeparateFile(reservationRecordIdentity);
        _ = context.CreateFileIdentity(dataFileIdentity);
        if (reservationRecordIdentity.Inode == dataFileIdentity.Inode)
            throw new ArgumentException("A publication seal and data file must have distinct identities.");
        ReservationRecordDigest = reservationRecordDigest;
        ReservationRecordIdentity = reservationRecordIdentity;
        PublicationId = V2RecordValue.Id(publicationId);
        DataFileIdentity = dataFileIdentity;
    }

    internal Sha256Digest ReservationRecordDigest { get; }
    internal PrivatePublicationInodeIdentity ReservationRecordIdentity { get; }
    internal Guid PublicationId { get; }
    internal PrivatePublicationInodeIdentity DataFileIdentity { get; }
}

internal sealed record V2PreparedRecord : V2PublicationRecord
{
    internal V2PreparedRecord(V2PublicationContext context, Sha256Digest boundRecordDigest,
        PrivatePublicationInodeIdentity boundRecordIdentity, Guid publicationId, DateTimeOffset boundAtUtc,
        PrivatePublicationInodeIdentity dataFileIdentity, Sha256Digest verifiedDigest, long verifiedByteCount)
        : base(V2RecordKind.Prepared, context)
    {
        ArgumentNullException.ThrowIfNull(boundRecordDigest);
        ArgumentNullException.ThrowIfNull(verifiedDigest);
        context.RequireSeparateFile(boundRecordIdentity);
        context.RequireWithinLease(boundAtUtc);
        _ = context.CreateFileIdentity(dataFileIdentity);
        if (boundRecordIdentity.Inode == dataFileIdentity.Inode || verifiedDigest != context.Reference.Digest ||
            verifiedByteCount != context.Reference.ByteCount)
            throw new ArgumentException("A prepared publication must retain its exact verified reference and identity.");
        BoundRecordDigest = boundRecordDigest;
        BoundRecordIdentity = boundRecordIdentity;
        PublicationId = V2RecordValue.Id(publicationId);
        BoundAtUtc = boundAtUtc;
        DataFileIdentity = dataFileIdentity;
        VerifiedDigest = verifiedDigest;
        VerifiedByteCount = verifiedByteCount;
    }

    internal Sha256Digest BoundRecordDigest { get; }
    internal PrivatePublicationInodeIdentity BoundRecordIdentity { get; }
    internal Guid PublicationId { get; }
    internal DateTimeOffset BoundAtUtc { get; }
    internal PrivatePublicationInodeIdentity DataFileIdentity { get; }
    internal Sha256Digest VerifiedDigest { get; }
    internal long VerifiedByteCount { get; }
}

internal static class V2RecordValue
{
    internal static Guid Id(Guid value) => value != Guid.Empty ? value :
        throw new ArgumentException("A publication identifier must be nonempty.");

    internal static void Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Publication times must be actual UTC facts.");
    }

    internal static void SameMount(PrivatePublicationInodeIdentity left, PrivatePublicationInodeIdentity right)
    {
        if (left.DeviceMajor != right.DeviceMajor || left.DeviceMinor != right.DeviceMinor ||
            left.MountId != right.MountId)
            throw new ArgumentException("Publication identities must retain their exact containing mount.");
    }
}
