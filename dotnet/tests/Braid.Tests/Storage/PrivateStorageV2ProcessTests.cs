using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Genuine child death and independent descriptors. Registry metadata in these facts is explicitly synthetic.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageV2ProcessTests
{
    [Fact]
    public async Task IndependentInitializationAndOpenDoNotResolveAnyPublicationRegistry()
    {
        using var root = new PrivateStorageTestRoot();
        await using (var initialize = await PrivateStorageV2Probe.StartAsync(root, null, "initialize"))
        {
            var result = await initialize.CompleteAsync();
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Initialized", result.Status);
            Assert.Equal(0, result.RegistryFactoryResolutions);
        }
        var files = PrivateStorageV2Data.Files(root.RootPath);
        await using (var open = await PrivateStorageV2Probe.StartAsync(root, null, "open"))
        {
            var result = await open.CompleteAsync();
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Opened", result.Status);
            Assert.Equal(0, result.RegistryFactoryResolutions);
        }
        var after = PrivateStorageV2Data.Files(root.RootPath);
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var name in files.Keys) Assert.Equal(files[name], after[name]);
    }

    [Theory]
    [InlineData("TemporaryCreated", null, false, 0)]
    [InlineData("SealStageWritten", "Bound", false, 0)]
    [InlineData("BindingAcknowledged", null, true, 0)]
    [InlineData("SealStageWritten", "Prepared", true, 100003)]
    [InlineData("PayloadPrepared", null, true, 100003)]
    public async Task ActualDeathLeavesAnExactTrackedTempThatCanBeFencedAndDeletedWithoutAnyLiveWriterRegistry(
        string phase, string? kind, bool bound, int payloadLength)
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(100003);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("publication-phase");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, phase, kind))
        {
            await child.WaitReadyAsync(directory);
            Assert.False(File.Exists(root.FinalPath(reference)));
            Assert.Equal(payloadLength == 0, !File.Exists(Path.Combine(directory, "source-read")));
            if (payloadLength != 0) Assert.Equal("read", File.ReadAllText(Path.Combine(directory, "source-read")));
            await child.KillAsync();
        }
        var record = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound);
        var temp = PrivateStorageV2Data.TempPath(root, record.Reservation.Key);
        var leaf = PrivateStorageV2Data.TempLeaf(root, record.Reservation.Key);
        Assert.Equal(payloadLength, new FileInfo(temp).Length);
        Assert.Equal(payloadLength == 0 ? PrivateStorageTestRoot.AttemptMode : PrivateStorageTestRoot.FinalMode,
            File.GetUnixFileMode(temp));
        var controls = PrivateStorageV2Data.Files(leaf);
        using var reopened = FileSystemV2PrivateObjectStore.Open(root.Options, () => throw new InvalidOperationException("Cleanup cannot resolve a writer."));
        var claim = PrivateStorageV2Data.TemporaryClaim(record);
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await reopened.RetireAsync(claim));
        Assert.False(File.Exists(temp));
        Assert.Equal(0, new FileInfo(Path.Combine(leaf, "temp.retired")).Length);
        foreach (var control in controls) Assert.Equal(control.Value, File.ReadAllBytes(Path.Combine(leaf, control.Key)));
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await reopened.RetireAsync(claim));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Theory]
    [InlineData("Absence")]
    [InlineData("Reservation")]
    public async Task DeathWithAPartialUnacceptedSealRetainsItsExactBytesAndReopenNeverPromotesIt(string kind)
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(83);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("partial-seal");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "SealStageWritten", kind))
        {
            await child.WaitReadyAsync(directory);
            Assert.False(File.Exists(Path.Combine(directory, "source-read")));
            await child.KillAsync();
        }
        var stage = Assert.Single(Directory.GetFiles(PrivateStorageV2Data.ObjectLeaf(root, reference.Id),
            kind.ToLowerInvariant() + ".stage", SearchOption.AllDirectories));
        var partial = File.ReadAllBytes(stage);
        Assert.NotEmpty(partial);
        Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(stage));
        var refusal = Assert.Throws<PrivateObjectStorageException>(() => V2PublicationRecordCodec.Decode(partial));
        Assert.Equal(PrivateObjectStorageError.UnsafeStorage, refusal.Error);
        using (var reopened = FileSystemV2PrivateObjectStore.Open(root.Options))
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(partial, File.ReadAllBytes(stage));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(stage)!, kind.ToLowerInvariant())));
        await using (var retry = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", PrivateStorageV2Data.Owner(reference), bytes))
            Assert.Equal("Created", (await retry.CompleteAsync()).Status);
        Assert.Equal(partial, File.ReadAllBytes(stage));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Fact]
    public async Task ChildDeathAfterReturnedCreatedLeavesCompleteFinalBytesAndOnlyItsAbsentTempCanBeRetired()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(65543);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("after-created");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "afterRename"))
        {
            await child.WaitReadyAsync(directory);
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
            await child.KillAsync();
        }
        var record = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        Assert.False(File.Exists(PrivateStorageV2Data.TempPath(root, record.Reservation.Key)));
        using (var reopened = FileSystemV2PrivateObjectStore.Open(root.Options))
        {
            Assert.Equal(PrivateObjectRetirementOutcome.Missing, await reopened.RetireAsync(PrivateStorageV2Data.TemporaryClaim(record)));
            Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        }
        await using var read = await PrivateStorageV2Probe.StartAsync(root, reference, "read");
        var response = await read.CompleteAsync();
        Assert.Equal(0, response.ExitCode);
        Assert.Equal("Read", response.Status);
        Assert.Equal(0, response.RegistryFactoryResolutions);
        Assert.Equal(0, response.SourceReadCalls);
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Fact]
    public async Task LiveChildTempGatePreventsRetirementUntilActualProcessDeathReleasesItsDescriptor()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(257);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("temp-gate");
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "BindingAcknowledged");
        await child.WaitReadyAsync(directory);
        var record = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var claim = PrivateStorageV2Data.TemporaryClaim(record);
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(claim));
        Assert.True(File.Exists(PrivateStorageV2Data.TempPath(root, record.Reservation.Key)));
        Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.TempLeaf(root, record.Reservation.Key), "temp.retired")));
        Assert.False(File.Exists(Path.Combine(directory, "source-read")));
        await child.KillAsync();
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await cleanup.RetireAsync(claim));
    }

    [Fact]
    public async Task PendingChildReadProtectsFinalObjectAndActualReleaseAllowsRetirement()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(257);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            await writer.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes));
        var directory = root.NewDirectory("held-read");
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "read",
            gateDirectory: directory, gateStage: "ReadGateAcquired");
        await child.WaitReadyAsync(directory);
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options);
        var claim = PrivateStorageV2Data.FinalClaim(reference, owner);
        Assert.Equal(PrivateObjectRetirementOutcome.Busy, await cleanup.RetireAsync(claim));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        await PrivateStorageV2Probe.ReleaseAsync(directory);
        var result = await child.CompleteAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Read", result.Status);
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await cleanup.RetireAsync(claim));
    }

    [Fact]
    public async Task IndependentExclusiveObjectDescriptorRejectsChildPublicationBeforeRegistryOrSourceReads()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemV2PrivateObjectStore.Initialize(root.Options);
        var bytes = "independent descriptor"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        using (new PrivateStorageV2IndependentGate(Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.gate")))
        {
            await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", PrivateStorageV2Data.Owner(reference), bytes);
            var result = await child.CompleteAsync();
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("Io", result.Error);
            Assert.Equal(0, result.SourceReadCalls);
            Assert.Equal(0, result.RegistryFactoryResolutions);
            Assert.Equal(0, result.ReserveCalls);
            Assert.Equal(0, result.BindCalls);
        }
        Assert.False(File.Exists(root.FinalPath(reference)));
    }

    [Fact]
    public async Task ReplacingTheNamedObjectGateCannotLetAHeldChildReadReturnSuccess()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = "held read identity"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            await writer.PublishCreateOnlyAsync(PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes));
        var directory = root.NewDirectory("replaced-gate");
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "read",
            gateDirectory: directory, gateStage: "ReadGateAcquired");
        await child.WaitReadyAsync(directory);
        var gate = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.gate");
        File.Move(gate, gate + ".held");
        File.WriteAllBytes(gate, []);
        File.SetUnixFileMode(gate, PrivateStorageTestRoot.AttemptMode);
        await PrivateStorageV2Probe.ReleaseAsync(directory);
        var result = await child.CompleteAsync();
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("UnsafeStorage", result.Error);
        Assert.Equal(0, result.RegistryFactoryResolutions);
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptAbsenceChainIsUnsafeEvenWhenTheExactDataSlotIsAbsent(bool removeTemp)
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = "corrupt chain"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("corrupt-after-death");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "BindingAcknowledged"))
        {
            await child.WaitReadyAsync(directory);
            await child.KillAsync();
        }
        var record = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var temp = PrivateStorageV2Data.TempPath(root, record.Reservation.Key);
        if (removeTemp) File.Delete(temp);
        var leaf = PrivateStorageV2Data.TempLeaf(root, record.Reservation.Key);
        var absence = Path.Combine(leaf, "absence");
        var corrupted = File.ReadAllBytes(absence);
        corrupted[0] ^= 1;
        File.SetUnixFileMode(absence, PrivateStorageTestRoot.AttemptMode);
        File.WriteAllBytes(absence, corrupted);
        File.SetUnixFileMode(absence, PrivateStorageTestRoot.FinalMode);
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(record)));
        Assert.False(File.Exists(Path.Combine(leaf, "temp.retired")));
        Assert.Equal(!removeTemp, File.Exists(temp));
        Assert.Equal(corrupted, File.ReadAllBytes(absence));
    }

    [Theory]
    [InlineData("nonempty-unbound")]
    [InlineData("bound-replacement")]
    [InlineData("bound-hardlink")]
    [InlineData("bound-permissions")]
    public async Task UnsafeActualTempFactsCannotBeErasedThroughOtherwiseCompleteRetainedSeals(string mutation)
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = "immutable slot evidence"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        var bound = mutation != "nonempty-unbound";
        var directory = root.NewDirectory("unsafe-slot");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, bound ? "BindingAcknowledged" : "TemporaryCreated"))
        {
            await child.WaitReadyAsync(directory);
            await child.KillAsync();
        }
        var record = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound);
        var temp = PrivateStorageV2Data.TempPath(root, record.Reservation.Key);
        switch (mutation)
        {
            case "nonempty-unbound": File.WriteAllBytes(temp, [0x5a]); break;
            case "bound-replacement":
                File.Move(temp, temp + ".original");
                File.WriteAllBytes(temp, []);
                File.SetUnixFileMode(temp, PrivateStorageTestRoot.AttemptMode);
                break;
            case "bound-hardlink": PrivateStorageLinux.HardLink(temp, temp + ".alias"); break;
            case "bound-permissions":
                File.SetUnixFileMode(temp, PrivateStorageTestRoot.AttemptMode | UnixFileMode.GroupRead);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var before = File.ReadAllBytes(temp);
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage, await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(record)));
        Assert.True(File.Exists(temp));
        Assert.Equal(before, File.ReadAllBytes(temp));
        Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.TempLeaf(root, record.Reservation.Key), "temp.retired")));
    }

    [Fact]
    public async Task FinalRetirementWinningBeforeThePreparedChildTakesItsFinalGatePermanentlyPreventsPublication()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(65551);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var directory = root.NewDirectory("retirement-wins");
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", owner, bytes,
            directory, "PayloadPrepared");
        await child.WaitReadyAsync(directory);
        var publication = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var key = publication.Reservation.Key;
        var temp = PrivateStorageV2Data.TempPath(root, key);
        var prepared = Path.Combine(PrivateStorageV2Data.TempLeaf(root, key), "prepared");
        var preparedBytes = File.ReadAllBytes(prepared);
        var identity = PrivateStorageV2Identity.Read(temp);
        Assert.Equal(publication.Binding!.ActualIdentity.FileIdentity, identity);
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(temp));
        Assert.Equal(bytes, File.ReadAllBytes(temp));
        Assert.False(File.Exists(root.FinalPath(reference)));

        // This claim is explicitly authored physical-test metadata, not proof of
        // actual elapsed grace. The independently acquired ObjectGate is real.
        using var retirement = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(PrivateObjectRetirementOutcome.Missing,
            await retirement.RetireAsync(PrivateStorageV2Data.FinalClaim(reference, owner)));
        var fence = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.retired");
        var fenceIdentity = PrivateStorageV2Identity.Read(fence);
        Assert.Equal(0, new FileInfo(fence).Length);
        Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(fence));
        Assert.Equal(identity, PrivateStorageV2Identity.Read(temp));
        Assert.Equal(bytes, File.ReadAllBytes(temp));

        await PrivateStorageV2Probe.ReleaseAsync(directory);
        var result = await child.CompleteAsync();
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Missing", result.Error);
        Assert.True(result.SourceReadCalls > 0);
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.False(File.Exists(temp)); // Ordinary failure removes only its own admitted inode.
        Assert.Equal(fenceIdentity, PrivateStorageV2Identity.Read(fence));
        Assert.Equal(preparedBytes, File.ReadAllBytes(prepared));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing,
            () => retirement.ReadVerifiedAsync(reference.Id.Scope, reference));
        await using var retry = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", owner, bytes);
        var denied = await retry.CompleteAsync();
        Assert.Equal(2, denied.ExitCode);
        Assert.Equal("Missing", denied.Error);
        Assert.Equal(0, denied.SourceReadCalls);
        Assert.Equal(0, denied.RegistryFactoryResolutions);
        Assert.Equal(fenceIdentity, PrivateStorageV2Identity.Read(fence));
    }

    [Fact]
    public async Task ChildHoldingTheActualFinalGateMakesRetirementBusyThenPublishesTheExactPreparedInode()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(65557);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var directory = root.NewDirectory("publication-wins");
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", owner, bytes,
            directory, "FinalGateAcquired");
        await child.WaitReadyAsync(directory);
        var publication = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var temp = PrivateStorageV2Data.TempPath(root, publication.Reservation.Key);
        var identity = PrivateStorageV2Identity.Read(temp);
        Assert.Equal(publication.Binding!.ActualIdentity.FileIdentity, identity);
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(temp));
        Assert.Equal(bytes, File.ReadAllBytes(temp));
        using var retirement = FileSystemV2PrivateObjectStore.Open(root.Options);
        var claim = PrivateStorageV2Data.FinalClaim(reference, owner); // Synthetic physical-test eligibility only.
        var fence = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.retired");
        Assert.Equal(PrivateObjectRetirementOutcome.Busy, await retirement.RetireAsync(claim));
        Assert.False(File.Exists(fence));
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.Equal(identity, PrivateStorageV2Identity.Read(temp));
        await PrivateStorageV2Probe.ReleaseAsync(directory);
        var result = await child.CompleteAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Created", result.Status);
        Assert.Equal(identity, PrivateStorageV2Identity.Read(root.FinalPath(reference)));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(fence));
        Assert.Equal(bytes, await retirement.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await retirement.RetireAsync(claim));
        Assert.True(File.Exists(fence));
        Assert.False(File.Exists(root.FinalPath(reference)));
    }

    [Fact]
    public async Task IndependentSimultaneousPublishersUseDistinctTempInodesAndNeverReplaceTheWinningFinalObject()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(65563);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        var firstDirectory = root.NewDirectory("first-overlapping-writer");
        var secondDirectory = root.NewDirectory("second-overlapping-writer");
        await using var first = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", owner, bytes,
            firstDirectory, "PayloadPrepared");
        await first.WaitReadyAsync(firstDirectory);
        var firstRecord = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var firstKey = firstRecord.Reservation.Key;
        var firstTemp = PrivateStorageV2Data.TempPath(root, firstKey);
        var firstIdentity = PrivateStorageV2Identity.Read(firstTemp);
        await using var second = await PrivateStorageV2Probe.StartAsync(root, reference, "publish", owner, bytes,
            secondDirectory, "PayloadPrepared");
        await second.WaitReadyAsync(secondDirectory);

        // Both independent processes have completed payload verification and
        // still hold their separate TempGate descriptions at this point.
        var publications = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "publications");
        var leaves = Directory.GetDirectories(publications);
        Assert.Equal(2, leaves.Length);
        var secondLeaf = Assert.Single(leaves, path => Path.GetFileName(path) != firstKey.TempId.ToString("N"));
        var secondPreparedBytes = File.ReadAllBytes(Path.Combine(secondLeaf, "prepared"));
        var secondPrepared = Assert.IsType<V2PreparedRecord>(V2PublicationRecordCodec.Decode(secondPreparedBytes));
        var secondKey = new PrivatePublicationKey(secondPrepared.Context.RootId, reference.Id, secondPrepared.Context.TempId);
        var secondTemp = PrivateStorageV2Data.TempPath(root, secondKey);
        var secondIdentity = PrivateStorageV2Identity.Read(secondTemp);
        Assert.Equal(firstKey.RootId, secondKey.RootId);
        Assert.Equal(firstKey.Object, secondKey.Object);
        Assert.NotEqual(firstKey.TempId, secondKey.TempId);
        Assert.NotEqual(firstRecord.Reservation.PublicationId, secondPrepared.PublicationId);
        Assert.NotEqual(firstIdentity, secondIdentity);
        Assert.Equal(firstRecord.Binding!.ActualIdentity.FileIdentity, firstIdentity);
        Assert.Equal(secondPrepared.DataFileIdentity, secondIdentity);
        Assert.Equal(bytes, File.ReadAllBytes(firstTemp));
        Assert.Equal(bytes, File.ReadAllBytes(secondTemp));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(firstTemp));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(secondTemp));
        Assert.False(File.Exists(root.FinalPath(reference)));

        await PrivateStorageV2Probe.ReleaseAsync(firstDirectory);
        var winner = await first.CompleteAsync();
        Assert.Equal(0, winner.ExitCode);
        Assert.Equal("Created", winner.Status);
        Assert.Equal(firstIdentity, PrivateStorageV2Identity.Read(root.FinalPath(reference)));
        Assert.Equal(secondIdentity, PrivateStorageV2Identity.Read(secondTemp));
        await PrivateStorageV2Probe.ReleaseAsync(secondDirectory);
        var loser = await second.CompleteAsync();
        Assert.Equal(0, loser.ExitCode);
        Assert.Equal("AlreadyPresent", loser.Status);
        Assert.Equal(firstIdentity, PrivateStorageV2Identity.Read(root.FinalPath(reference)));
        Assert.False(File.Exists(firstTemp));
        Assert.False(File.Exists(secondTemp));
        Assert.Equal(secondPreparedBytes, File.ReadAllBytes(Path.Combine(secondLeaf, "prepared")));
        Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.retired")));
        using var reader = FileSystemV2PrivateObjectStore.Open(root.Options);
        Assert.Equal(bytes, await reader.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Fact]
    public async Task ActualDeathDuringABoundPartialPayloadRetiresOnlyTheIndependentlyObservedOriginalInode()
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(100003);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("partial-bound-payload");
        PrivatePublicationRecord publication;
        PrivatePublicationInodeIdentity originalIdentity;
        string temp;
        string leaf;
        Dictionary<string, byte[]> controls;
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "beforeSecondSourceRead"))
        {
            await child.WaitReadyAsync(directory);
            publication = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
            temp = PrivateStorageV2Data.TempPath(root, publication.Reservation.Key);
            leaf = PrivateStorageV2Data.TempLeaf(root, publication.Reservation.Key);
            originalIdentity = PrivateStorageV2Identity.Read(temp);
            Assert.Equal(publication.Binding!.ActualIdentity.FileIdentity, originalIdentity);
            Assert.Equal(65536, new FileInfo(temp).Length);
            Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(temp));
            Assert.Equal(bytes.AsSpan(0, 65536).ToArray(), File.ReadAllBytes(temp));
            Assert.Equal("read", File.ReadAllText(Path.Combine(directory, "source-read")));
            Assert.True(File.Exists(Path.Combine(leaf, "bound")));
            Assert.False(File.Exists(Path.Combine(leaf, "prepared")));
            Assert.False(File.Exists(Path.Combine(leaf, "prepared.stage")));
            Assert.False(File.Exists(root.FinalPath(reference)));
            // Readiness identifies entry to the second source read. These parent
            // observations, not the marker itself, prove the real partial file.
            await child.KillAsync();
            // The closed child has released its exclusive gate before this snapshot opens it.
            controls = PrivateStorageV2Data.Files(leaf);
        }
        Assert.Equal(originalIdentity, PrivateStorageV2Identity.Read(temp));
        Assert.Equal(bytes.AsSpan(0, 65536).ToArray(), File.ReadAllBytes(temp));
        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options,
            () => throw new InvalidOperationException("Physical recovery cannot resolve a live writer."));
        var claim = PrivateStorageV2Data.TemporaryClaim(publication); // Explicitly synthetic cleanup timing only.
        Assert.Equal(PrivateObjectRetirementOutcome.Deleted, await cleanup.RetireAsync(claim));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(root.FinalPath(reference)));
        var fence = Path.Combine(leaf, "temp.retired");
        Assert.Equal(0, new FileInfo(fence).Length);
        Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(fence));
        foreach (var control in controls) Assert.Equal(control.Value, File.ReadAllBytes(Path.Combine(leaf, control.Key)));
        Assert.Equal(PrivateObjectRetirementOutcome.Missing, await cleanup.RetireAsync(claim));
    }
}
