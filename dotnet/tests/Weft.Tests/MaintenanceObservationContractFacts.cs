using System.Text;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Authored pure-contract facts, never actual inode, ownership, database clock or cleanup evidence.</summary>
internal static class MaintenanceObservationContractFacts
{
    internal static Guid Id(int seed) => Guid.Parse($"00000000-0000-4000-8000-{seed:x12}");
    internal static DateTimeOffset At => new(2026, 10, 15, 1, 2, 3, TimeSpan.Zero);
    internal static ProjectScope Project => new(Id(1), Id(2));
    internal static RevisionScope Scope => new(Id(1), Id(2), Id(3), Id(4));
    internal static PrivateObjectId Object(int seed = 5, RevisionScope? scope = null,
        PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact) =>
        PrivateObjectId.Restore(scope ?? Scope, Id(seed), objectClass);
    internal static PrivatePublicationInodeIdentity Parent => new(41, 7, 8, 9);
    internal static PrivatePublicationInodeIdentity Child => new(42, 7, 8, 9);

    internal static PrivatePublicationFileIdentity Identity(Guid? root = null, Guid? control = null, Guid? boot = null,
        ulong namespaceDevice = 10, ulong namespaceInode = 11, uint user = 1000,
        PrivatePublicationInodeIdentity? file = null, PrivatePublicationInodeIdentity? parent = null) =>
        new(root ?? Id(10), control ?? Id(11), boot ?? Id(12), namespaceDevice, namespaceInode, user,
            file ?? Child, parent ?? Parent);

    internal static CleanupAttemptStatusHeader Attempt(long generation = 1, DateTimeOffset? finished = null,
        ObjectCleanupDisposition? disposition = null, Guid? id = null, DateTimeOffset? start = null) =>
        new(id ?? Id(30), generation, start ?? At, (start ?? At).AddSeconds(60), finished, disposition);

    internal static ObjectCleanupStatusHeader Status(Guid? id = null, CleanupAttemptStatusHeader? attempt = null,
        DateTimeOffset? observed = null, Guid? completed = null, RevisionScope? scope = null) =>
        new(id ?? Id(20), new ObjectCleanupTarget(Object(scope: scope), CleanupTargetKind.FinalObject), At.AddSeconds(-1),
            attempt?.Generation ?? 0, attempt, completed, observed ?? At);

    internal static PrivateStorageSlotObservation Final(int ordinal = 0, PrivateObjectId? objectId = null,
        PrivatePublicationFileIdentity? identity = null) =>
        PrivateStorageSlotObservation.Final(ordinal, objectId ?? Object(), identity ?? Identity());

    internal static PrivateStorageEntryObservation Candidate(PrivateStorageSlotObservation? slot = null,
        byte[]? name = null, PrivateStorageEntryFacts? facts = null,
        PrivatePublicationInodeIdentity? heldParent = null)
    {
        var selected = slot ?? Final();
        var spelling = selected.Kind == CleanupTargetKind.FinalObject ? selected.ObjectId!.ObjectId.ToString("N") + ".blob" :
            ".attempt-" + selected.TempId!.Value.ToString("N") + ".tmp";
        return new(selected.Ordinal, selected.ObjectClass, PrivateStorageEntryLevel.Payload,
            selected.Scope.MappingId, selected.Scope.RevisionId, heldParent ?? selected.ActualIdentity.DirectoryIdentity,
            name ?? Encoding.ASCII.GetBytes(spelling), PrivateStorageEntryKind.CandidateSlot,
            facts ?? new(selected.ActualIdentity.FileIdentity, selected.ActualIdentity.EffectiveUserId, 0x8100, 1, 3), selected, null);
    }

    internal static PrivateStorageEntryObservation Unknown(int ordinal = 0, byte[]? name = null,
        PrivatePublicationInodeIdentity? parent = null, PrivateStorageEntryFacts? facts = null,
        PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact) =>
        new(ordinal, objectClass, PrivateStorageEntryLevel.Payload, Scope.MappingId, Scope.RevisionId,
            parent ?? Parent, name ?? Encoding.ASCII.GetBytes($"unknown-{ordinal}"), PrivateStorageEntryKind.Unproved,
            facts, null, PrivateStorageUnprovedReason.UnknownName);

    internal static PrivateStorageObservationPage StoragePage(IEnumerable<PrivateStorageEntryObservation> entries,
        bool native = true, bool derived = true, bool limit = false, int subtrees = 0) =>
        new(Project, Id(10), Id(11), entries, native, derived, limit, subtrees);

    internal static ObservedPublicationWitness Witness(int publication = 50, int temp = 60,
        PrivateObjectId? objectId = null, PrivatePublicationFileIdentity? identity = null)
    {
        var target = objectId ?? Object();
        var actual = identity ?? Identity();
        var origin = target.Class == PrivateObjectClass.NativeArtifact
            ? ObjectCleanupOrigin.ForIngestion(target.Scope, Id(70))
            : ObjectCleanupOrigin.ForReport(target.Scope, Id(71), Id(72), 1);
        return new(Id(publication), new(actual.RootId, target, Id(temp)), actual, origin);
    }
}
