using System.Text;
using Weft.Domain;

namespace Weft.Application;

public enum PrivateStorageEntryLevel { Mapping = 1, Revision = 2, Payload = 3 }
public enum PrivateStorageEntryKind { LayoutDirectory = 1, CandidateSlot = 2, Unproved = 3 }
public enum PrivateStorageUnprovedReason
{
    UnknownName = 1, WrongType = 2, UnsafeFacts = 3, Changed = 4, Unavailable = 5, Unsupported = 6
}

/// <summary>No partial page or positive attribution survives a repeated physical entry key.</summary>
public sealed class PrivateStorageObservationChangedException : Exception
{
    public PrivateStorageObservationChangedException() : base("The private storage observation changed.") { }
}

/// <summary>Actual child statx facts; unsafe facts may be retained without claiming a usable slot.</summary>
public sealed record PrivateStorageEntryFacts
{
    public PrivateStorageEntryFacts(PrivatePublicationInodeIdentity entryIdentity, uint ownerId,
        ushort mode, uint linkCount, ulong byteCount)
    {
        ArgumentNullException.ThrowIfNull(entryIdentity);
        EntryIdentity = entryIdentity;
        OwnerId = ownerId;
        Mode = mode;
        LinkCount = linkCount;
        ByteCount = byteCount;
    }

    public PrivatePublicationInodeIdentity EntryIdentity { get; }
    public uint OwnerId { get; }
    /// <summary>The complete Linux statx mode including the file-type bits.</summary>
    public ushort Mode { get; }
    public uint LinkCount { get; }
    public ulong ByteCount { get; }
}

/// <summary>A full observed logical slot; a temporary name has no independently known object ID.</summary>
public sealed record PrivateStorageSlotObservation
{
    private PrivateStorageSlotObservation(int ordinal, CleanupTargetKind kind, RevisionScope scope,
        PrivateObjectClass objectClass, PrivatePublicationFileIdentity actualIdentity,
        PrivateObjectId? objectId, Guid? tempId)
    {
        MaintenanceObservationValue.Ordinal(ordinal);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actualIdentity);
        MaintenanceValue.Known(objectClass, nameof(objectClass));
        Ordinal = ordinal;
        Kind = kind;
        Scope = scope;
        ObjectClass = objectClass;
        ActualIdentity = actualIdentity;
        ObjectId = objectId;
        TempId = tempId;
    }

    public int Ordinal { get; }
    public CleanupTargetKind Kind { get; }
    public RevisionScope Scope { get; }
    public PrivateObjectClass ObjectClass { get; }
    public PrivatePublicationFileIdentity ActualIdentity { get; }
    public PrivateObjectId? ObjectId { get; }
    public Guid? TempId { get; }

    public static PrivateStorageSlotObservation Final(int ordinal, PrivateObjectId objectId,
        PrivatePublicationFileIdentity actualIdentity)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return new(ordinal, CleanupTargetKind.FinalObject, objectId.Scope, objectId.Class, actualIdentity, objectId, null);
    }

    public static PrivateStorageSlotObservation Temporary(int ordinal, RevisionScope scope,
        PrivateObjectClass objectClass, Guid tempId, PrivatePublicationFileIdentity actualIdentity)
    {
        MaintenanceValue.Id(tempId, nameof(tempId));
        return new(ordinal, CleanupTargetKind.TrackedTemporary, scope, objectClass, actualIdentity, null, tempId);
    }
}

public sealed class PrivateStorageEntryObservation
{
    private readonly byte[] _rawName;

    public PrivateStorageEntryObservation(int ordinal, PrivateObjectClass objectClass, PrivateStorageEntryLevel level,
        Guid? parentMappingId, Guid? parentRevisionId, PrivatePublicationInodeIdentity heldParentIdentity,
        ReadOnlyMemory<byte> rawName, PrivateStorageEntryKind kind, PrivateStorageEntryFacts? facts,
        PrivateStorageSlotObservation? slot, PrivateStorageUnprovedReason? reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MaintenanceObservationValue.Ordinal(ordinal);
        MaintenanceValue.Known(objectClass, nameof(objectClass));
        MaintenanceValue.Known(level, nameof(level));
        MaintenanceValue.Known(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(heldParentIdentity);
        if (parentMappingId == Guid.Empty || parentRevisionId == Guid.Empty ||
            (level != PrivateStorageEntryLevel.Mapping) != parentMappingId.HasValue ||
            (level == PrivateStorageEntryLevel.Payload) != parentRevisionId.HasValue)
            throw new ArgumentException("An observed entry must retain the parent identities required by its level.");
        if (rawName.Length is < 1 or > 255)
            throw new ArgumentException("An observed entry name must fit its native bound.", nameof(rawName));
        _rawName = new byte[rawName.Length];
        for (var index = 0; index < _rawName.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = rawName.Span[index];
            if (value is 0 or (byte)'/')
                throw new ArgumentException("An observed entry name must be one native path component.", nameof(rawName));
            _rawName[index] = value;
        }
        if (_rawName.AsSpan().SequenceEqual("."u8) || _rawName.AsSpan().SequenceEqual(".."u8))
            throw new ArgumentException("Special directory entries are not observations.", nameof(rawName));

        switch (kind)
        {
            case PrivateStorageEntryKind.LayoutDirectory:
                if (level == PrivateStorageEntryLevel.Payload || facts is null || slot is not null || reason is not null ||
                    (facts.Mode & 0xf000) != 0x4000 || (facts.Mode & 0xfff) != 0x1c0 || facts.LinkCount == 0 ||
                    facts.EntryIdentity.Inode == heldParentIdentity.Inode || !SameMount(facts.EntryIdentity, heldParentIdentity) ||
                    !CanonicalDirectoryName(_rawName, cancellationToken))
                    throw new ArgumentException("A structural observation requires a canonical safe directory.");
                break;
            case PrivateStorageEntryKind.CandidateSlot:
                if (level != PrivateStorageEntryLevel.Payload || facts is null || slot is null || reason is not null ||
                    slot.Ordinal != ordinal || slot.ObjectClass != objectClass ||
                    slot.Scope.MappingId != parentMappingId || slot.Scope.RevisionId != parentRevisionId ||
                    slot.ActualIdentity.DirectoryIdentity != heldParentIdentity || slot.ActualIdentity.FileIdentity != facts.EntryIdentity ||
                    slot.ActualIdentity.EffectiveUserId != facts.OwnerId || facts.LinkCount != 1 ||
                    (facts.Mode & 0xf000) != 0x8000 ||
                    ((facts.Mode & 0xfff) != 0x100 &&
                        (slot.Kind != CleanupTargetKind.TrackedTemporary || (facts.Mode & 0xfff) != 0x180)))
                    throw new ArgumentException("A candidate observation must bind its exact regular-file facts and slot.");
                var expectedName = slot.Kind == CleanupTargetKind.FinalObject
                    ? slot.ObjectId!.ObjectId.ToString("N") + ".blob"
                    : ".attempt-" + slot.TempId!.Value.ToString("N") + ".tmp";
                if (!_rawName.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(expectedName)))
                    throw new ArgumentException("A candidate observation must bind its exact native spelling.");
                break;
            case PrivateStorageEntryKind.Unproved:
                if (slot is not null || reason is null)
                    throw new ArgumentException("An unproved entry requires its category without a positive slot.");
                MaintenanceValue.Known(reason.Value, nameof(reason));
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Ordinal = ordinal;
        ObjectClass = objectClass;
        Level = level;
        ParentMappingId = parentMappingId;
        ParentRevisionId = parentRevisionId;
        HeldParentIdentity = heldParentIdentity;
        Kind = kind;
        Facts = facts;
        Slot = slot;
        Reason = reason;
    }

    public int Ordinal { get; }
    public PrivateObjectClass ObjectClass { get; }
    public PrivateStorageEntryLevel Level { get; }
    public Guid? ParentMappingId { get; }
    public Guid? ParentRevisionId { get; }
    public PrivatePublicationInodeIdentity HeldParentIdentity { get; }
    public PrivateStorageEntryKind Kind { get; }
    public PrivateStorageEntryFacts? Facts { get; }
    public PrivateStorageSlotObservation? Slot { get; }
    public PrivateStorageUnprovedReason? Reason { get; }

    public byte[] CopyRawName(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = new byte[_rawName.Length];
        for (var index = 0; index < owned.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owned[index] = _rawName[index];
        }
        return owned;
    }

    public override string ToString() => "PrivateStorageEntryObservation";

    internal bool SameEntry(PrivateStorageEntryObservation other, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HeldParentIdentity != other.HeldParentIdentity || _rawName.Length != other._rawName.Length) return false;
        for (var index = 0; index < _rawName.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rawName[index] != other._rawName[index]) return false;
        }
        return true;
    }

    private static bool SameMount(PrivatePublicationInodeIdentity left, PrivatePublicationInodeIdentity right) =>
        left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.MountId == right.MountId;

    private static bool CanonicalDirectoryName(byte[] name, CancellationToken cancellationToken)
    {
        if (name.Length != 32) return false;
        foreach (var value in name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f')) return false;
        }
        return Guid.TryParseExact(Encoding.ASCII.GetString(name), "N", out var id) && id != Guid.Empty;
    }
}

/// <summary>A bounded traversal only. No filesystem observation time is inferred from a later database read.</summary>
public sealed class PrivateStorageObservationPage
{
    public PrivateStorageObservationPage(ProjectScope project, Guid rootId, Guid controlNamespaceId,
        IEnumerable<PrivateStorageEntryObservation> entries, bool nativeTraversalComplete,
        bool derivedTraversalComplete, bool entryLimitReached, int untraversedSubtreesObserved,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        RootId = MaintenanceValue.Id(rootId, nameof(rootId));
        ControlNamespaceId = MaintenanceValue.Id(controlNamespaceId, nameof(controlNamespaceId));
        var owned = MaintenanceObservationValue.Snapshot(entries, MaintenanceObservationValue.MaximumEntries, cancellationToken);
        for (var index = 0; index < owned.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = owned[index];
            for (var earlier = 0; earlier < index; earlier++)
                if (entry.SameEntry(owned[earlier], cancellationToken)) throw new PrivateStorageObservationChangedException();
            if (entry.Ordinal != index || (entry.Slot is { } slot &&
                (!MaintenanceObservationValue.InProject(slot.Scope, project) || slot.ActualIdentity.RootId != rootId ||
                    slot.ActualIdentity.ControlNamespaceId != controlNamespaceId)))
                throw new ArgumentException("Observed entries must retain their page ordinal, project and storage incarnation.", nameof(entries));
        }
        if (entryLimitReached != (owned.Count == MaintenanceObservationValue.MaximumEntries))
            throw new ArgumentException("The observed entry limit must agree with the actual consumed entry count.", nameof(entryLimitReached));
        MaintenanceValue.Count(untraversedSubtreesObserved, owned.Count, nameof(untraversedSubtreesObserved));
        Project = project;
        Entries = owned;
        NativeTraversalComplete = nativeTraversalComplete;
        DerivedTraversalComplete = derivedTraversalComplete;
        EntryLimitReached = entryLimitReached;
        UntraversedSubtreesObserved = untraversedSubtreesObserved;
    }

    public ProjectScope Project { get; }
    public Guid RootId { get; }
    public Guid ControlNamespaceId { get; }
    public IReadOnlyList<PrivateStorageEntryObservation> Entries { get; }
    public int ExaminedCount => Entries.Count;
    public bool NativeTraversalComplete { get; }
    public bool DerivedTraversalComplete { get; }
    public bool EntryLimitReached { get; }
    public int UntraversedSubtreesObserved { get; }
    public bool TraversalComplete => NativeTraversalComplete && DerivedTraversalComplete &&
        !EntryLimitReached && UntraversedSubtreesObserved == 0;
}
