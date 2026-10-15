using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Actual local filesystem behavior with explicitly synthetic publication/cleanup metadata.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageV2ComponentTests
{
    [Fact]
    public void InitializationBindsAnImmutableControlNamespaceAndOpenDoesNotProbeOrResolveTheRegistry()
    {
        using var root = new PrivateStorageTestRoot();
        var resolutions = 0;
        IPrivateObjectPublicationRegistry Factory() { resolutions++; throw new InvalidOperationException(); }
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options, Factory)) { }
        Assert.Equal(0, resolutions);
        var marker = PrivateStorageV2Data.Marker(root);
        Assert.NotEqual(Guid.Empty, marker.Root);
        Assert.NotEqual(marker.Root, marker.Control);
        var meta = Path.Combine(PrivateStorageV2Data.ControlRoot(root), "control.meta");
        Assert.Equal(68, new FileInfo(meta).Length);
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(root.MarkerPath));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(meta));
        var before = PrivateStorageV2Data.Files(root.RootPath);
        using (FileSystemV2PrivateObjectStore.Open(root.Options, Factory)) { }
        var after = PrivateStorageV2Data.Files(root.RootPath);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in before.Keys) Assert.Equal(before[path], after[path]);
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task PlainPublicationAndMissingRegistryRefuseBeforeAnySourceRead()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var bytes = "opaque payload"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, source));
        Assert.Equal(0, source.ReadCalls);
        Assert.False(File.Exists(root.FinalPath(reference)));
    }

    [Theory]
    [InlineData(PrivateObjectClass.NativeArtifact)]
    [InlineData(PrivateObjectClass.DerivedArtifact)]
    public async Task ActualTempIsBoundWhileEmptyBeforePayloadReadsAndClosedReadOnlyBeforePublication(PrivateObjectClass kind)
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(65539);
        var reference = PrivateStorageData.Reference(bytes, kind);
        using var input = new PrivateStorageInput(bytes, chunkSize: 997);
        var registry = new PrivateStorageV2PhysicalRegistry
        {
            BeforeReserve = () => Assert.Equal(0, input.ReadCalls),
            BeforeBind = () => Assert.Equal(0, input.ReadCalls)
        };
        var boundObserved = false;
        var preparedObserved = false;
        var observer = new PrivateStorageV2Observer((observation, _) =>
        {
            if (observation.Phase == V2StoragePhase.BindingAcknowledged)
            {
                boundObserved = true;
                Assert.Equal(0, input.ReadCalls);
                var row = registry.Single;
                Assert.NotNull(row.Binding);
                var temp = PrivateStorageV2Data.TempPath(root, row.Reservation.Key);
                Assert.Equal(0, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(temp));
            }
            if (observation.Phase == V2StoragePhase.PayloadPrepared)
            {
                preparedObserved = true;
                var row = registry.Single;
                var temp = PrivateStorageV2Data.TempPath(root, row.Reservation.Key);
                Assert.Equal(bytes.LongLength, new FileInfo(temp).Length);
                Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(temp));
                Assert.Equal(bytes, File.ReadAllBytes(temp));
                var prepared = Assert.IsType<V2PreparedRecord>(V2PublicationRecordCodec.Decode(File.ReadAllBytes(
                    Path.Combine(PrivateStorageV2Data.TempLeaf(root, row.Reservation.Key), "prepared"))));
                Assert.Equal(reference, prepared.Context.Reference);
                Assert.Equal(row.Binding!.ActualIdentity.FileIdentity, prepared.DataFileIdentity);
                Assert.False(File.Exists(root.FinalPath(reference)));
            }
            return ValueTask.CompletedTask;
        });
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry, observer);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, input));
        Assert.True(boundObserved);
        Assert.True(preparedObserved);
        Assert.Equal(1, registry.ReserveCalls);
        Assert.Equal(1, registry.BindCalls);
        Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, registry.Single.Reservation.Key)));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.True(input.CanRead); // The caller still owns the stream.
    }

    [Fact]
    public async Task IndependentAdaptersVerifyAlreadyPresentAndNeverReplaceDifferentContent()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var bytes = "original exact bytes"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using (var first = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry))
            Assert.Equal(PrivateObjectPublishResult.Created, await first.PublishCreateOnlyAsync(
                PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes)));
        using var second = FileSystemV2PrivateObjectStore.Open(root.Options, () => registry);
        Assert.Equal(PrivateObjectPublishResult.AlreadyPresent, await second.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes)));
        var replacement = "other bytes"u8.ToArray();
        var conflict = new PrivateObjectReference(reference.Id, Sha256Digest.FromBytes(replacement), replacement.LongLength);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => second.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(conflict), conflict.Id.Scope, conflict, new MemoryStream(replacement)));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        Assert.Equal(bytes, await second.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Fact]
    public async Task SyntheticLostReserveAndBindAcknowledgementsResolveOnlyTheOriginalNaturalKeyBeforeAnyRead()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(71);
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        var registry = new PrivateStorageV2PhysicalRegistry
        {
            ThrowAfterReserve = true,
            ThrowAfterBind = true,
            BeforeReserve = () => Assert.Equal(0, input.ReadCalls),
            BeforeBind = () => Assert.Equal(0, input.ReadCalls)
        };
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry);
        var owner = PrivateStorageV2Data.Owner(reference);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input));
        Assert.Equal(2, registry.GetCalls);
        Assert.Equal(owner, registry.Single.Reservation.Owner);
        Assert.NotNull(registry.Single.Binding);
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Fact]
    public async Task OwnerRefusalLeavesAbsenceEvidenceButCannotCreateOrReadPayload()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry { RefuseOwner = true };
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry);
        var bytes = "refused"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.PublishCreateOnlyAsync(
            PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, input));
        Assert.Equal(0, input.ReadCalls);
        Assert.Equal(0, registry.BindCalls);
        Assert.Empty(Directory.GetFiles(root.RootPath, "*.tmp", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(root.RootPath, "absence", SearchOption.AllDirectories));
        Assert.False(File.Exists(root.FinalPath(reference)));
    }

    [Fact]
    public async Task PermanentFinalFencePrecedesDeletionAndBlocksLaterReadAndPublicationWithoutRegistryCalls()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var bytes = "retired object"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry);
        await store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes));
        registry.RefuseEveryCall = true;
        var claim = PrivateStorageV2Data.FinalClaim(reference, owner);
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await store.RetireAsync(claim));
        Assert.False(File.Exists(root.FinalPath(reference)));
        var fence = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.retired");
        Assert.Equal(0, new FileInfo(fence).Length);
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(claim));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using var source = new PrivateStorageInput(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.PublishCreateOnlyAsync(owner,
            reference.Id.Scope, reference, source));
        Assert.Equal(0, source.ReadCalls);
        // An external same-owner recreation cannot make an irreversibly retired object readable.
        File.WriteAllBytes(root.FinalPath(reference), bytes);
        File.SetUnixFileMode(root.FinalPath(reference), PrivateStorageTestRoot.FinalMode);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        Assert.Equal(1, registry.ReserveCalls);
        Assert.Equal(1, registry.BindCalls);
        Assert.Equal(0, registry.GetCalls);
    }

    [Fact]
    public async Task NeverWrittenObjectCanBeFencedMissingAndAMissingExistingGateIsNeverRecreated()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var reference = PrivateStorageData.Reference("missing"u8.ToArray());
        var owner = PrivateStorageV2Data.Owner(reference);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        var leaf = PrivateStorageV2Data.ObjectLeaf(root, reference.Id);
        Assert.True(File.Exists(Path.Combine(leaf, "object.gate")));
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        File.Delete(Path.Combine(leaf, "object.gate"));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        Assert.False(File.Exists(Path.Combine(leaf, "object.gate")));
    }

    [Fact]
    public async Task IndependentlyHeldObjectGateReturnsBusyWithoutInstallingAFenceAndRecoversAfterRelease()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry);
        var bytes = "gated"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        var leaf = PrivateStorageV2Data.ObjectLeaf(root, reference.Id);
        using (new PrivateStorageV2IndependentGate(Path.Combine(leaf, "object.gate")))
        {
            Assert.Equal(PrivateObjectRetirementOutcome.Busy, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            using var input = new PrivateStorageInput(bytes);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, input));
            Assert.Equal(0, input.ReadCalls);
            Assert.Equal(0, registry.ReserveCalls);
            Assert.False(File.Exists(Path.Combine(leaf, "object.retired")));
        }
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
    }

    [Fact]
    public async Task ReplacementOfANamedGateWhileItsOldDescriptorIsHeldCannotAuthorizeARead()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(97);
        var reference = PrivateStorageData.Reference(bytes);
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            await writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var boundary = new PrivateStorageV2Boundary(V2StoragePhase.ReadGateAcquired);
        using var reader = FileSystemV2PrivateObjectStore.Open(root.Options, null, boundary);
        var read = reader.ReadVerifiedAsync(reference.Id.Scope, reference);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var gate = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.gate");
            File.Move(gate, gate + ".old");
            File.WriteAllBytes(gate, []);
            File.SetUnixFileMode(gate, PrivateStorageTestRoot.AttemptMode);
        }
        finally { boundary.Release(); }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, async () => { _ = await read; });
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Fact]
    public async Task PreparedTempPathReplacementCannotBePublishedOrUnlinkedAsTheOriginalInode()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(113);
        var reference = PrivateStorageData.Reference(bytes);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new PrivateStorageV2Boundary(V2StoragePhase.PayloadPrepared);
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry, boundary);
        var write = store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        string? temp = null;
        var replacement = Enumerable.Repeat((byte)0xee, bytes.Length).ToArray();
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            temp = PrivateStorageV2Data.TempPath(root, registry.Single.Reservation.Key);
            File.Move(temp, temp + ".original");
            File.WriteAllBytes(temp, replacement);
            File.SetUnixFileMode(temp, PrivateStorageTestRoot.FinalMode);
        }
        finally { boundary.Release(); }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, async () => { _ = await write; });
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.Equal(replacement, File.ReadAllBytes(temp!));
        Assert.Equal(bytes, File.ReadAllBytes(temp + ".original"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CapturedReservationWithoutLocalAcknowledgementCanFenceOnlyAnActuallyAbsentTemp(int presentBytes)
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var observer = new PrivateStorageV2Observer((observation, _) => observation.Phase == V2StoragePhase.ReservationAcknowledged
            ? ValueTask.FromException(new IOException("Synthetic stop after real in-memory registration.")) : ValueTask.CompletedTask);
        var reference = PrivateStorageData.Reference("absent before CreateNew"u8.ToArray());
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry, observer))
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => writer.PublishCreateOnlyAsync(
                PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream("absent before CreateNew"u8.ToArray())));
        var row = registry.Single;
        var leaf = PrivateStorageV2Data.TempLeaf(root, row.Reservation.Key);
        Assert.True(File.Exists(Path.Combine(leaf, "absence")));
        Assert.False(File.Exists(Path.Combine(leaf, "reservation")));
        var temp = PrivateStorageV2Data.TempPath(root, row.Reservation.Key);
        Assert.False(File.Exists(temp));
        if (presentBytes >= 0)
        {
            File.WriteAllBytes(temp, new byte[presentBytes]);
            File.SetUnixFileMode(temp, PrivateStorageTestRoot.AttemptMode);
        }
        registry.RefuseEveryCall = true;
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options, () => registry);
        Assert.Equal(presentBytes < 0 ? PrivateObjectRetirementOutcome.Missing : PrivateObjectRetirementOutcome.UnsafeStorage,
            await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row)));
        Assert.Equal(presentBytes < 0, File.Exists(Path.Combine(leaf, "temp.retired")));
        if (presentBytes >= 0) Assert.Equal(presentBytes, new FileInfo(temp).Length);
        Assert.Equal(0, registry.BindCalls);
        Assert.Equal(0, registry.GetCalls);
    }

    [Fact]
    public async Task CancellationAtCommittedBindingSettlesWithoutReadingAndLeavesSealsForMissingRecovery()
    {
        using var root = new PrivateStorageTestRoot();
        var registry = new PrivateStorageV2PhysicalRegistry();
        var boundary = new PrivateStorageV2Boundary(V2StoragePhase.BindingAcknowledged);
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => registry, boundary);
        var bytes = "canceled after binding"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using var input = new PrivateStorageInput(bytes);
        using var cancellation = new CancellationTokenSource();
        var write = store.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, input, cancellation.Token);
        try
        {
            await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally { boundary.Release(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await write; });
        Assert.Equal(0, input.ReadCalls);
        var row = registry.Single;
        Assert.NotNull(row.Binding);
        Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, row.Reservation.Key)));
        Assert.True(File.Exists(Path.Combine(PrivateStorageV2Data.TempLeaf(root, row.Reservation.Key), "bound")));
        registry.RefuseEveryCall = true;
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await store.RetireAsync(PrivateStorageV2Data.TemporaryClaim(row)));
    }
}
