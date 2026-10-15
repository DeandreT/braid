using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.MaintenanceObservationContractFacts;

namespace Weft.Tests;

/// <summary>Pure authored stat/name fixtures; these tests do not assert real filesystem ownership.</summary>
public sealed class PrivateStorageObservationContractTests
{
    [Fact]
    public void RawNameIsOwnedMethodOnlyAndAbsentFromOrdinaryJsonAndText()
    {
        var raw = Encoding.ASCII.GetBytes("secret-private-name");
        var original = raw.ToArray();
        var entry = Unknown(name: raw);
        raw[0] = (byte)'X';
        var returned = entry.CopyRawName();
        Assert.Equal(original, returned);
        returned[0] = (byte)'Y';
        Assert.Equal(original, entry.CopyRawName());
        Assert.Equal("PrivateStorageEntryObservation", entry.ToString());
        var json = JsonSerializer.Serialize(StoragePage([entry]));
        Assert.DoesNotContain("secret-private-name", json);
        Assert.DoesNotContain(Convert.ToBase64String(original), json);
        Assert.DoesNotContain("RawName", json);
        Assert.DoesNotContain(typeof(PrivateStorageEntryObservation).GetProperties(), property =>
            property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(ReadOnlyMemory<byte>));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => entry.CopyRawName(canceled.Token));
    }

    [Fact]
    public void ArbitraryInvalidUtf8AndHiddenNamesRemainDistinctWithoutLossyConversion()
    {
        byte[][] names = [[0xff], [0xfe], Encoding.ASCII.GetBytes(".hidden"), Encoding.ASCII.GetBytes(".attempt-unknown.tmp")];
        var entries = names.Select((name, index) => Unknown(index, name)).ToArray();
        var page = StoragePage(entries);
        Assert.Equal(4, page.ExaminedCount);
        for (var index = 0; index < names.Length; index++) Assert.Equal(names[index], page.Entries[index].CopyRawName());
        byte[][] invalid = [[], [0], [(byte)'/'], Encoding.ASCII.GetBytes("."), Encoding.ASCII.GetBytes(".."), new byte[256]];
        Assert.All(invalid, name => Assert.Throws<ArgumentException>(() => Unknown(name: name)));
        var longest = Enumerable.Repeat((byte)0xff, 255).ToArray();
        Assert.Equal(longest, Unknown(name: longest).CopyRawName());
    }

    [Fact]
    public void RepeatedParentAndRawNameRefusesEvenWithoutChildFactsOrAcrossClasses()
    {
        var name = "same-name"u8.ToArray();
        var first = Unknown(name: name);
        Assert.Null(first.Facts);
        var repeated = Unknown(1, name, objectClass: PrivateObjectClass.DerivedArtifact);
        var error = Assert.Throws<PrivateStorageObservationChangedException>(() => StoragePage([first, repeated]));
        Assert.Equal("The private storage observation changed.", error.Message);
        Assert.DoesNotContain("same-name", error.ToString());
        var anotherParent = new PrivatePublicationInodeIdentity(99, 7, 8, 9);
        Assert.Equal(2, StoragePage([first, Unknown(1, name, anotherParent)]).ExaminedCount);
    }

    [Fact]
    public void HundredthRepeatedOccurrenceRefusesInsteadOfReturningLimitPage()
    {
        var entries = Enumerable.Range(0, 99).Select(index => Unknown(index)).ToList();
        entries.Add(Unknown(99, entries[0].CopyRawName()));
        Assert.Throws<PrivateStorageObservationChangedException>(() => StoragePage(entries, limit: true));
    }

    [Fact]
    public void DifferentHardlinkNamesCountTwoAndPreserveHonestUnsafeFacts()
    {
        var facts = new PrivateStorageEntryFacts(Child, uint.MaxValue, ushort.MaxValue, 2, ulong.MaxValue);
        var page = StoragePage([Unknown(name: "link-a"u8.ToArray(), facts: facts), Unknown(1, "link-b"u8.ToArray(), facts: facts)]);
        Assert.Equal(2, page.ExaminedCount);
        Assert.Equal(page.Entries[0].Facts!.EntryIdentity, page.Entries[1].Facts!.EntryIdentity);
        Assert.Equal(ushort.MaxValue, page.Entries[0].Facts!.Mode);
        Assert.Equal(ulong.MaxValue, page.Entries[0].Facts!.ByteCount);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(99, false, true)]
    [InlineData(100, true, false)]
    public void StorageBoundaryIsIndependentOfCandidateSelectionAndNeverLooksAhead(int count, bool limit, bool complete)
    {
        var input = Enumerable.Range(0, count).Select(index => Unknown(index)).ToList();
        var page = StoragePage(input, limit: limit);
        input.Clear();
        Assert.Equal(count, page.ExaminedCount);
        Assert.Equal(complete, page.TraversalComplete);
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateStorageEntryObservation>)page.Entries).Clear());
        Assert.Throws<ArgumentException>(() => StoragePage(page.Entries, limit: !limit));
    }

    [Fact]
    public void IncompleteNativeTraversalCanCoexistWithDerivedEofAndSkippedSubtreePreventsComplete()
    {
        var page = StoragePage([Unknown()], native: false, derived: true, subtrees: 1);
        Assert.False(page.NativeTraversalComplete);
        Assert.True(page.DerivedTraversalComplete);
        Assert.False(page.TraversalComplete);
        Assert.False(StoragePage([Unknown()], subtrees: 1).TraversalComplete);
        Assert.Throws<ArgumentOutOfRangeException>(() => StoragePage([], subtrees: 1));
        Assert.Throws<ArgumentException>(() => StoragePage([Unknown(1)]));
    }

    [Theory]
    [InlineData(0x0100)]
    [InlineData(0x8180)]
    [InlineData(0xa100)]
    [InlineData(0x8104)]
    public void FinalPositiveSlotRequiresFullRegularModeAndExactReadonlyPermissions(int mode)
    {
        Assert.Throws<ArgumentException>(() => Candidate(facts: new(Child, 1000, (ushort)mode, 1, 3)));
    }

    [Fact]
    public void TemporaryPositiveSlotAcceptsBothActualPublicationModesWithoutInventingObjectId()
    {
        var slot = PrivateStorageSlotObservation.Temporary(0, Scope, PrivateObjectClass.NativeArtifact, Id(60), Identity());
        Assert.Null(slot.ObjectId);
        Assert.Equal(Id(60), slot.TempId);
        Assert.Equal(CleanupTargetKind.TrackedTemporary, slot.Kind);
        Assert.Same(slot, Candidate(slot).Slot);
        Assert.Same(slot, Candidate(slot, facts: new(Child, 1000, 0x8180, 1, 0)).Slot);
    }

    [Fact]
    public void PositiveCandidateBindsNameScopeFileParentUserAndLinkFacts()
    {
        Assert.Equal(Child, Candidate().Facts!.EntryIdentity);
        Assert.Throws<ArgumentException>(() => Candidate(name: Encoding.ASCII.GetBytes(Id(99).ToString("N") + ".blob")));
        Assert.Throws<ArgumentException>(() => Candidate(name: Encoding.ASCII.GetBytes(Object().ObjectId.ToString("N") + ".BLOB")));
        Assert.Throws<ArgumentException>(() => Candidate(facts: new(Child, 1001, 0x8100, 1, 3)));
        Assert.Throws<ArgumentException>(() => Candidate(facts: new(Child, 1000, 0x8100, 2, 3)));
        Assert.Throws<ArgumentException>(() => Candidate(facts: new(new(99, 7, 8, 9), 1000, 0x8100, 1, 3)));
        Assert.Throws<ArgumentException>(() => Candidate(heldParent: new(99, 7, 8, 9)));
        var slot = Final();
        Assert.Throws<ArgumentException>(() => new PrivateStorageEntryObservation(0, slot.ObjectClass,
            PrivateStorageEntryLevel.Payload, Id(99), Scope.RevisionId, Parent, Candidate().CopyRawName(),
            PrivateStorageEntryKind.CandidateSlot, new(Child, 1000, 0x8100, 1, 3), slot, null));
        Assert.Throws<ArgumentException>(() => StoragePage([Candidate(Final(identity: Identity(root: Id(99))))]));
        Assert.Throws<ArgumentException>(() => StoragePage([Candidate(Final(identity: Identity(control: Id(99))))]));
        var foreign = new RevisionScope(Scope.OrganizationId, Id(99), Scope.MappingId, Scope.RevisionId);
        Assert.Throws<ArgumentException>(() => StoragePage([Candidate(Final(objectId: Object(scope: foreign)))]));
    }

    [Fact]
    public void LayoutDirectoriesAreStructuralAndClosedArmsRetainTheirLevel()
    {
        var directory = new PrivateStorageEntryObservation(0, PrivateObjectClass.NativeArtifact,
            PrivateStorageEntryLevel.Mapping, null, null, Parent, Encoding.ASCII.GetBytes(Id(3).ToString("N")),
            PrivateStorageEntryKind.LayoutDirectory, new(Child, 1000, 0x41c0, 2, 4096), null, null);
        Assert.Equal(PrivateStorageEntryKind.LayoutDirectory, Assert.Single(StoragePage([directory]).Entries).Kind);
        Assert.Throws<ArgumentException>(() => new PrivateStorageEntryObservation(0, PrivateObjectClass.NativeArtifact,
            PrivateStorageEntryLevel.Mapping, Id(3), null, Parent, "unknown"u8.ToArray(),
            PrivateStorageEntryKind.Unproved, null, null, PrivateStorageUnprovedReason.UnknownName));
        Assert.Throws<ArgumentException>(() => new PrivateStorageEntryObservation(0, PrivateObjectClass.NativeArtifact,
            PrivateStorageEntryLevel.Payload, Id(3), Id(4), Parent, "unknown"u8.ToArray(),
            PrivateStorageEntryKind.Unproved, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivateStorageSlotObservation.Temporary(0, Scope, (PrivateObjectClass)99, Id(60), Identity()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Unknown(100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void ObservationCategoriesFailClosedWithoutEchoingPrivateNames(int unknown)
    {
        PrivateStorageEntryObservation Make(PrivateObjectClass objectClass = PrivateObjectClass.NativeArtifact,
            PrivateStorageEntryLevel level = PrivateStorageEntryLevel.Payload,
            PrivateStorageEntryKind kind = PrivateStorageEntryKind.Unproved,
            PrivateStorageUnprovedReason reason = PrivateStorageUnprovedReason.UnknownName) =>
            new(0, objectClass, level, Id(3), Id(4), Parent, "private-name"u8.ToArray(), kind, null, null, reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(objectClass: (PrivateObjectClass)unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(level: (PrivateStorageEntryLevel)unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(kind: (PrivateStorageEntryKind)unknown));
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Make(reason: (PrivateStorageUnprovedReason)unknown));
        Assert.DoesNotContain("private-name", error.Message);
        Assert.Throws<ArgumentException>(() => PrivateStorageSlotObservation.Temporary(0, Scope, PrivateObjectClass.NativeArtifact, Guid.Empty, Identity()));
    }

    [Fact]
    public void StorageConstructorAndTraversalObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PrivateStorageEntryObservation(0, PrivateObjectClass.NativeArtifact,
            PrivateStorageEntryLevel.Payload, Id(3), Id(4), Parent, "name"u8.ToArray(), PrivateStorageEntryKind.Unproved,
            null, null, PrivateStorageUnprovedReason.UnknownName, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new PrivateStorageObservationPage(Project, Id(10), Id(11), [], true, true, false, 0, cancellation.Token));
    }
}
