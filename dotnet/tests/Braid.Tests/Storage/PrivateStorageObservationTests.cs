using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;
using Layout = Braid.Tests.PrivateStorageObservationTestSupport;

namespace Braid.Tests;

/// <summary>Actual metadata-only filesystem observations; authored files do not prove registered ownership.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageObservationTests
{
    [Fact]
    public async Task MissingProjectNamespacesAreObservedEmptyWithoutProvisioningOrRegistryResolution()
    {
        using var root = new PrivateStorageTestRoot();
        var calls = 0;
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () =>
        { calls++; throw new InvalidOperationException("The observer must not resolve a registry."); });
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var before = Layout.Snapshot(root.RootPath);
        var page = await store.ObserveAsync(project);
        Assert.Empty(page.Entries);
        Assert.Equal(0, page.ExaminedCount);
        Assert.True(page.NativeTraversalComplete);
        Assert.True(page.DerivedTraversalComplete);
        Assert.True(page.TraversalComplete);
        Assert.False(page.EntryLimitReached);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Layout.ProjectPath(root, project)));
        Assert.False(Directory.Exists(Layout.ProjectPath(root, project, PrivateObjectClass.DerivedArtifact)));
        Layout.SameSnapshot(before, Layout.Snapshot(root.RootPath));
    }

    [Theory]
    [InlineData(PrivateObjectClass.NativeArtifact)]
    [InlineData(PrivateObjectClass.DerivedArtifact)]
    public async Task FinalAndTemporarySlotsRetainIndependentlyMeasuredFullFileAndParentFacts(PrivateObjectClass objectClass)
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project, objectClass);
        var objectId = Guid.NewGuid();
        var tempId = Guid.NewGuid();
        var finalPath = Layout.File(revision.Path, $"{objectId:N}.blob");
        var tempPath = Layout.File(revision.Path, $".attempt-{tempId:N}.tmp", mode: PrivateStorageTestRoot.AttemptMode);
        var before = Layout.Snapshot(root.RootPath);
        var page = await store.ObserveAsync(project);
        Assert.True(page.TraversalComplete);
        Assert.Equal(4, page.ExaminedCount);
        Assert.Equal(new[] { 0, 1, 2, 3 }, page.Entries.Select(entry => entry.Ordinal));
        Assert.Equal(2, page.Entries.Count(entry => entry.Kind == PrivateStorageEntryKind.LayoutDirectory));
        var final = Assert.Single(page.Entries, entry => entry.Slot?.Kind == CleanupTargetKind.FinalObject);
        var temp = Assert.Single(page.Entries, entry => entry.Slot?.Kind == CleanupTargetKind.TrackedTemporary);
        foreach (var entry in new[] { final, temp })
        {
            Assert.Equal(objectClass, entry.ObjectClass);
            Assert.Equal(PrivateStorageEntryLevel.Payload, entry.Level);
            Assert.Equal(revision.Scope, entry.Slot!.Scope);
            Assert.Equal(PrivateStorageV2Identity.Read(revision.Path), entry.HeldParentIdentity);
            Assert.Equal(entry.HeldParentIdentity, entry.Slot.ActualIdentity.DirectoryIdentity);
            Assert.Equal(page.RootId, entry.Slot.ActualIdentity.RootId);
            Assert.Equal(page.ControlNamespaceId, entry.Slot.ActualIdentity.ControlNamespaceId);
            Assert.Equal(PrivateStorageLinux.EffectiveUid, entry.Facts!.OwnerId);
            Assert.Equal(1u, entry.Facts.LinkCount);
            Assert.Equal(3ul, entry.Facts.ByteCount);
        }
        Assert.Equal(PrivateStorageV2Identity.Read(finalPath), final.Facts!.EntryIdentity);
        Assert.Equal(PrivateStorageV2Identity.Read(tempPath), temp.Facts!.EntryIdentity);
        Assert.Equal((ushort)0x8100, final.Facts.Mode);
        Assert.Equal((ushort)0x8180, temp.Facts.Mode);
        Assert.Equal(objectId, final.Slot!.ObjectId!.ObjectId);
        Assert.Null(temp.Slot!.ObjectId);
        Assert.Equal(tempId, temp.Slot.TempId);
        Assert.False(Directory.Exists(Path.Combine(PrivateStorageV2Data.ControlRoot(root), "objects",
            objectClass == PrivateObjectClass.NativeArtifact ? "native" : "derived", project.OrganizationId.ToString("N"))));
        Layout.SameSnapshot(before, Layout.Snapshot(root.RootPath));
    }

    [Fact]
    public async Task RawInvalidUtf8AndHiddenNamesRemainDistinctAndAreNotImplicitlySerialized()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var parent = Layout.Directory(Layout.ProjectPath(root, project));
        using var first = new PrivateStorageObservationRawFile(parent, [0xff]);
        using var second = new PrivateStorageObservationRawFile(parent, [0xfe]);
        Layout.File(parent, ".hidden-private-name");
        var page = await store.ObserveAsync(project);
        Assert.Equal(3, page.ExaminedCount);
        Assert.All(page.Entries, entry => Assert.Equal(PrivateStorageEntryKind.Unproved, entry.Kind));
        var rawFirst = Assert.Single(page.Entries, entry => entry.CopyRawName().AsSpan().SequenceEqual(new byte[] { 0xff }));
        var rawSecond = Assert.Single(page.Entries, entry => entry.CopyRawName().AsSpan().SequenceEqual(new byte[] { 0xfe }));
        Assert.Equal(first.Identity, rawFirst.Facts!.EntryIdentity);
        Assert.Equal(second.Identity, rawSecond.Facts!.EntryIdentity);
        Assert.NotEqual(rawFirst.Facts.EntryIdentity, rawSecond.Facts.EntryIdentity);
        Assert.All(page.Entries, entry => Assert.Equal(PrivateStorageV2Identity.Read(parent), entry.HeldParentIdentity));
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain(".hidden-private-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(".hidden-private-name"u8), json, StringComparison.Ordinal);
        Assert.DoesNotContain("RawName", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PrivateStorageEntryObservation", rawFirst.ToString());
        var copied = rawFirst.CopyRawName();
        copied[0] = 1;
        Assert.Equal(new byte[] { 0xff }, rawFirst.CopyRawName());
    }

    [Theory]
    [InlineData(97, 99, false)]
    [InlineData(98, 100, true)]
    [InlineData(99, 100, true)]
    public async Task HierarchyEntriesShareTheHundredEntryBudgetAndTheBoundaryNeverLooksAhead(
        int payloads, int expectedEntries, bool limited)
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project);
        for (var index = 0; index < payloads; index++) Layout.File(revision.Path, $"{Guid.NewGuid():N}.blob");
        var consumed = 0;
        var hook = new PrivateStorageObservationHook((phase, _, _) =>
        {
            if (phase == PrivateStorageObservationPhase.EntryConsumed) consumed++;
            return ValueTask.FromResult(false);
        });
        var page = await store.ObserveAsync(project, hook);
        Assert.Equal(expectedEntries, consumed);
        Assert.Equal(expectedEntries, page.ExaminedCount);
        Assert.Equal(2, page.Entries.Count(entry => entry.Kind == PrivateStorageEntryKind.LayoutDirectory));
        Assert.Equal(limited, page.EntryLimitReached);
        Assert.Equal(!limited, page.TraversalComplete);
        if (limited) Assert.False(page.DerivedTraversalComplete);
    }

    [Fact]
    public async Task NativeNamespaceConsumesTheSharedLimitBeforeDerivedEnumeration()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var native = Layout.Directory(Layout.ProjectPath(root, project));
        var derived = Layout.Directory(Layout.ProjectPath(root, project, PrivateObjectClass.DerivedArtifact));
        for (var index = 0; index < 100; index++) Layout.File(native, $"unknown-{index}");
        Layout.File(derived, "derived-unknown");
        var page = await store.ObserveAsync(project);
        Assert.Equal(100, page.ExaminedCount);
        Assert.All(page.Entries, entry => Assert.Equal(PrivateObjectClass.NativeArtifact, entry.ObjectClass));
        Assert.False(page.NativeTraversalComplete);
        Assert.False(page.DerivedTraversalComplete);
        Assert.False(page.TraversalComplete);
    }

    [Fact]
    public async Task UnknownDirectoriesAndPayloadDirectoriesAreCountedButNeverDescended()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project);
        var unknown = Layout.Directory(Path.Combine(Layout.ProjectPath(root, project), ".unknown-tree"));
        Layout.File(unknown, "not-enumerated");
        var payloadDirectory = Layout.Directory(Path.Combine(revision.Path, $"{Guid.NewGuid():N}.blob"));
        Layout.File(payloadDirectory, "also-not-enumerated");
        var page = await store.ObserveAsync(project);
        Assert.Equal(4, page.ExaminedCount);
        Assert.Equal(2, page.UntraversedSubtreesObserved);
        Assert.False(page.TraversalComplete);
        Assert.DoesNotContain(page.Entries, entry => Encoding.ASCII.GetString(entry.CopyRawName()).Contains("enumerated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoHardlinkNamesRemainTwoUnsafeObservationsWithTheSameActualInode()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project);
        var first = Layout.File(revision.Path, $"{Guid.NewGuid():N}.blob");
        var second = Path.Combine(revision.Path, $"{Guid.NewGuid():N}.blob");
        PrivateStorageLinux.HardLink(first, second);
        var page = await store.ObserveAsync(project);
        Assert.Equal(4, page.ExaminedCount);
        var files = page.Entries.Where(entry => entry.Level == PrivateStorageEntryLevel.Payload).ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, entry =>
        {
            Assert.Equal(PrivateStorageEntryKind.Unproved, entry.Kind);
            Assert.Equal(PrivateStorageUnprovedReason.UnsafeFacts, entry.Reason);
            Assert.Equal(2u, entry.Facts!.LinkCount);
            Assert.Null(entry.Slot);
        });
        Assert.Equal(PrivateStorageV2Identity.Read(first), files[0].Facts!.EntryIdentity);
        Assert.Equal(files[0].Facts!.EntryIdentity, files[1].Facts!.EntryIdentity);
        Assert.False(files[0].CopyRawName().AsSpan().SequenceEqual(files[1].CopyRawName()));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Fact]
    public async Task SymlinksAndFifosAreObservedWithoutFollowingOrOpeningTheirPayloads()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project);
        var external = Path.Combine(root.NewDirectory("outside-project"), "secret");
        File.WriteAllBytes(external, [9, 8, 7]);
        var targetIdentity = PrivateStorageV2Identity.Read(external);
        File.CreateSymbolicLink(Path.Combine(revision.Path, $"{Guid.NewGuid():N}.blob"), external);
        PrivateStorageLinux.Fifo(Path.Combine(revision.Path, $".attempt-{Guid.NewGuid():N}.tmp"));
        var page = await store.ObserveAsync(project).WaitAsync(TimeSpan.FromSeconds(5));
        var files = page.Entries.Where(entry => entry.Level == PrivateStorageEntryLevel.Payload).ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, entry => { Assert.Equal(PrivateStorageEntryKind.Unproved, entry.Kind); Assert.Null(entry.Slot); });
        Assert.Contains(files, entry => (entry.Facts!.Mode & 0xf000) == 0xa000);
        Assert.Contains(files, entry => (entry.Facts!.Mode & 0xf000) == 0x1000);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(external));
        Assert.Equal(targetIdentity, PrivateStorageV2Identity.Read(external));
    }

    [Fact]
    public async Task ExactProjectSelectionNeverEnumeratesSiblingProjectsOrOrganizations()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var sibling = new ProjectScope(project.OrganizationId, Guid.NewGuid());
        var foreign = new ProjectScope(Guid.NewGuid(), project.ProjectId);
        Layout.File(Layout.Directory(Layout.ProjectPath(root, sibling)), "sibling-secret");
        Layout.File(Layout.Directory(Layout.ProjectPath(root, foreign)), "foreign-secret");
        var before = Layout.Snapshot(root.RootPath);
        var page = await store.ObserveAsync(project);
        Assert.Empty(page.Entries);
        Assert.True(page.TraversalComplete);
        Layout.SameSnapshot(before, Layout.Snapshot(root.RootPath));
    }

    [Fact]
    public async Task NamedFileReplacementBetweenActualStatAndOpenIsUnprovedChanged()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var revision = Layout.Revision(root, project);
        var name = $"{Guid.NewGuid():N}.blob";
        var original = Layout.File(revision.Path, name, [1, 2, 3]);
        var oldIdentity = PrivateStorageV2Identity.Read(original);
        var replaced = false;
        var hook = new PrivateStorageObservationHook((phase, ordinal, _) =>
        {
            if (phase == PrivateStorageObservationPhase.ChildInspected && ordinal == 2)
            {
                File.Move(original, root.NewPath("original-retained"));
                Layout.File(revision.Path, name, [4, 5, 6]);
                replaced = true;
            }
            return ValueTask.FromResult(false);
        });
        var page = await store.ObserveAsync(project, hook);
        Assert.True(replaced);
        var entry = page.Entries[2];
        Assert.Equal(PrivateStorageEntryKind.Unproved, entry.Kind);
        Assert.Equal(PrivateStorageUnprovedReason.Changed, entry.Reason);
        Assert.Equal(oldIdentity, entry.Facts!.EntryIdentity);
        Assert.Null(entry.Slot);
        Assert.NotEqual(oldIdentity, PrivateStorageV2Identity.Read(original));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(original));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(root.NewPath("original-retained")));
    }

    [Fact]
    public async Task ControlledRealGetdentsRewindRefusesARepeatedNameEvenWhenItsFirstFactsAreMissing()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var parent = Layout.Directory(Layout.ProjectPath(root, project));
        var path = Layout.File(parent, "same-raw-name");
        var consumed = 0;
        var observed = 0;
        var hook = new PrivateStorageObservationHook((phase, ordinal, _) =>
        {
            if (phase == PrivateStorageObservationPhase.EntryConsumed)
            {
                consumed++;
                Assert.Equal(0, ordinal);
                File.Delete(path);
            }
            if (phase == PrivateStorageObservationPhase.EntryObserved)
            {
                observed++;
                Assert.False(File.Exists(path));
                Layout.File(parent, "same-raw-name");
                return ValueTask.FromResult(true);
            }
            return ValueTask.FromResult(false);
        });
        var error = await Assert.ThrowsAsync<PrivateStorageObservationChangedException>(() => store.ObserveAsync(project, hook));
        Assert.Equal("The private storage observation changed.", error.Message);
        Assert.Equal(1, consumed); // The second real occurrence refuses before its hook/stat.
        Assert.Equal(1, observed);
        Assert.Single((await store.ObserveAsync(project)).Entries);
    }

    [Fact]
    public async Task ControlledRealGetdentsRepeatAtTheHundredthConsumedEntryRefusesInsteadOfReturningALimitPage()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var parent = Layout.Directory(Layout.ProjectPath(root, project));
        for (var index = 0; index < 99; index++) Layout.File(parent, $"unknown-{index}");
        var observed = 0;
        var hook = new PrivateStorageObservationHook((phase, ordinal, _) =>
        {
            if (phase != PrivateStorageObservationPhase.EntryObserved) return ValueTask.FromResult(false);
            observed++;
            return ValueTask.FromResult(ordinal == 98);
        });
        await Assert.ThrowsAsync<PrivateStorageObservationChangedException>(() => store.ObserveAsync(project, hook));
        Assert.Equal(99, observed);
        var recovery = await store.ObserveAsync(project);
        Assert.Equal(99, recovery.ExaminedCount);
        Assert.True(recovery.TraversalComplete);
    }

    [Fact]
    public async Task ASecondRequestedRewindRefusesAndReleasesItsOperationAndDirectoryDescriptors()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        Layout.File(Layout.Directory(Layout.ProjectPath(root, project)), "unknown");
        var hook = new PrivateStorageObservationHook((phase, _, _) =>
            ValueTask.FromResult(phase != PrivateStorageObservationPhase.ChildInspected));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported, () => store.ObserveAsync(project, hook));
        Assert.Single((await store.ObserveAsync(project)).Entries);
    }

    [Fact]
    public async Task CancellationSettlesTheHeldHookAndDisposalWaitsForTheActualOperation()
    {
        using var root = new PrivateStorageTestRoot();
        var store = FileSystemV2PrivateObjectStore.Initialize(new(root.RootPath, root.ApplicationRoot, maximumConcurrency: 1));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        Layout.File(Layout.Directory(Layout.ProjectPath(root, project)), "unknown");
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new PrivateStorageObservationHook(async (phase, _, token) =>
        {
            if (phase == PrivateStorageObservationPhase.EntryConsumed)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return false;
        });
        var pending = store.ObserveAsync(project, hook, canceled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = store.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ObserveAsync(project));
            using var reopened = FileSystemV2PrivateObjectStore.Open(root.Options);
            Assert.Single((await reopened.ObserveAsync(project)).Entries);
        }
        finally { canceled.Cancel(); await store.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedPinnedMarkerOrControlMetadataRefusesWithoutRestoringIt(bool control)
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var path = control ? Path.Combine(PrivateStorageV2Data.ControlRoot(root), "control.meta") : root.MarkerPath;
        var bytes = File.ReadAllBytes(path);
        var originalIdentity = PrivateStorageV2Identity.Read(path);
        File.Move(path, root.NewPath("retained-original"));
        File.WriteAllBytes(path, bytes);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
        var replacedIdentity = PrivateStorageV2Identity.Read(path);
        Assert.NotEqual(originalIdentity, replacedIdentity);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ObserveAsync(project));
        Assert.Equal(replacedIdentity, PrivateStorageV2Identity.Read(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
