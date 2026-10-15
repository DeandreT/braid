using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Actual owned-process death on the same live host; no power-loss or operator quiescence proof.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageV2ActivationProcessTests
{
    [Theory]
    [InlineData("PreparationCreated")]
    [InlineData("ReceiptStageWritten")]
    public async Task ActualDeathBeforeSealedReceiptRefusesUnchangedWithoutAdoptingPartialPreparation(string phase)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-pre-receipt-gate");
        await using (var child = await PrivateStorageActivationProbe.StartAsync(root, directory, phase))
        {
            await child.WaitReadyAsync(directory);
            // Parent observations establish the real boundary. A readiness file
            // by itself is neither a receipt nor evidence of stopped processes.
            Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(root.MarkerPath));
            Assert.Equal(fixture.MarkerIdentity, PrivateStorageV2Identity.Read(root.MarkerPath));
            Assert.False(File.Exists(PrivateStorageActivationData.ReceiptPath(root)));
            Assert.False(Directory.Exists(PrivateStorageActivationData.InstalledControlPath(root)));
            Assert.Equal(0, new FileInfo(PrivateStorageActivationData.GatePath(root)).Length);
            var stage = Path.Combine(PrivateStorageActivationData.DirectoryPath(root), "receipt.stage");
            if (phase == "ReceiptStageWritten")
            {
                Assert.InRange(new FileInfo(stage).Length, 1, PrivateStorageActivationData.ReceiptSize - 1);
                Assert.Equal("WEFTV2A\n"u8.ToArray(), File.ReadAllBytes(stage)[..8]);
                Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(stage));
                Assert.True(Directory.Exists(PrivateStorageActivationData.StagedControlPath(root)));
                Assert.True(File.Exists(PrivateStorageActivationData.CounterpartPath(root)));
            }
            else
            {
                Assert.False(File.Exists(stage));
                Assert.False(Directory.Exists(PrivateStorageActivationData.StagedControlPath(root)));
            }
            fixture.AssertPreserved();
            await child.KillAsync();
        }
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using var resumed = await PrivateStorageActivationProbe.StartAsync(root);
        AssertFailure(await resumed.CompleteAsync(), "UnsafeStorage");
        before.AssertUnchanged(root.RootPath);
        fixture.AssertPreserved();
    }

    [Theory]
    [InlineData("ReceiptSealed", false)]
    [InlineData("ControlInstalled", true)]
    public async Task ActualDeathAfterSealedReceiptResumesOnlyItsExactRecordedControlAndMarkerState(string phase, bool installed)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-sealed-gate");
        byte[] receipt;
        PrivatePublicationInodeIdentity receiptIdentity;
        PrivatePublicationInodeIdentity newMarkerIdentity;
        await using (var child = await PrivateStorageActivationProbe.StartAsync(root, directory, phase))
        {
            await child.WaitReadyAsync(directory);
            PrivateStorageActivationData.AssertSealed(fixture, installed);
            receipt = File.ReadAllBytes(PrivateStorageActivationData.ReceiptPath(root));
            receiptIdentity = PrivateStorageV2Identity.Read(PrivateStorageActivationData.ReceiptPath(root));
            newMarkerIdentity = PrivateStorageV2Identity.Read(PrivateStorageActivationData.CounterpartPath(root));
            await child.KillAsync();
        }
        await using (var resumed = await PrivateStorageActivationProbe.StartAsync(root))
            AssertSuccess(await resumed.CompleteAsync(), "Activated");
        Assert.Equal(receipt, File.ReadAllBytes(PrivateStorageActivationData.ReceiptPath(root)));
        Assert.Equal(receiptIdentity, PrivateStorageV2Identity.Read(PrivateStorageActivationData.ReceiptPath(root)));
        Assert.Equal(newMarkerIdentity, PrivateStorageV2Identity.Read(root.MarkerPath));
        PrivateStorageActivationData.AssertSealed(fixture, installed: true, active: true);
        await fixture.AssertReadableAsync();
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => { using var old = root.Open(); });
    }

    [Fact]
    public async Task ActualDeathAfterReturnedPivotIsAlreadyActiveOnlyThroughTheSameExactReceipt()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-after-return-gate");
        await using (var child = await PrivateStorageActivationProbe.StartAsync(root, directory, "afterActivationReturned"))
        {
            await child.WaitReadyAsync(directory);
            // The external gate is reached only after ActivateAsync returned.
            // Independent v2 reads and exchanged inode observations establish success.
            PrivateStorageActivationData.AssertSealed(fixture, installed: true, active: true);
            await fixture.AssertReadableAsync();
            await child.KillAsync();
        }
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using (var replay = await PrivateStorageActivationProbe.StartAsync(root))
            AssertSuccess(await replay.CompleteAsync(), "AlreadyActive");
        before.AssertUnchanged(root.RootPath);
        PrivateStorageActivationData.Tamper(root, "receipt");
        var replaced = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using (var refused = await PrivateStorageActivationProbe.StartAsync(root))
            AssertFailure(await refused.CompleteAsync(), "UnsafeStorage");
        replaced.AssertUnchanged(root.RootPath);
        await fixture.AssertReadableAsync();
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("rootMarker")]
    [InlineData("marker")]
    [InlineData("gate")]
    [InlineData("control")]
    [InlineData("controlNamespace")]
    [InlineData("meta")]
    [InlineData("activation")]
    [InlineData("root")]
    [InlineData("missingReceipt")]
    [InlineData("missingGate")]
    [InlineData("missingMarker")]
    [InlineData("missingControl")]
    [InlineData("markerContent")]
    [InlineData("rootMarkerContent")]
    [InlineData("metaContent")]
    [InlineData("gateContent")]
    [InlineData("controlUnknown")]
    [InlineData("unknown")]
    [InlineData("bothControls")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("boot")]
    [InlineData("namespaceDevice")]
    [InlineData("namespaceInode")]
    [InlineData("uid")]
    [InlineData("ack")]
    [InlineData("rootIdentity")]
    [InlineData("receiptIdentity")]
    public async Task FreshProcessRefusesReplacedCorruptUnknownOrWrongIncarnationReceiptEvidenceUnchanged(string target)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-before-corruption-gate");
        await using (var child = await PrivateStorageActivationProbe.StartAsync(root, directory, "ControlInstalled"))
        {
            await child.WaitReadyAsync(directory);
            PrivateStorageActivationData.AssertSealed(fixture, installed: true);
            await child.KillAsync();
        }
        // These are explicit authored corruptions, not simulated kernel events.
        // Each new process must compare the evidence to actual current descriptors.
        PrivateStorageActivationData.Tamper(root, target);
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using (var refused = await PrivateStorageActivationProbe.StartAsync(root))
            AssertFailure(await refused.CompleteAsync(), target is "boot" or "namespaceDevice" or "namespaceInode"
                ? "Unsupported" : "UnsafeStorage");
        before.AssertUnchanged(root.RootPath);
        if (target != "rootMarkerContent")
            Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(root.MarkerPath));
        fixture.AssertPreserved();
    }

    [Fact]
    public async Task IndependentActivationWhileReceiptOwnerIsPausedReturnsBusyWithoutReadingPayloads()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-busy-gate");
        await using var first = await PrivateStorageActivationProbe.StartAsync(root, directory, "ReceiptSealed");
        await first.WaitReadyAsync(directory);
        PrivateStorageActivationData.AssertSealed(fixture, installed: false);
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using (var second = await PrivateStorageActivationProbe.StartAsync(root))
            AssertSuccess(await second.CompleteAsync(), "Busy");
        before.AssertUnchanged(root.RootPath);
        await PrivateStorageActivationProbe.ReleaseAsync(directory);
        AssertSuccess(await first.CompleteAsync(), "Activated");
        await fixture.AssertReadableAsync();
    }

    [Fact]
    public async Task LockedSameByteReplacementGateRefusesInsteadOfClaimingOriginalOwnerIsBusy()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var directory = root.NewDirectory("activation-before-gate-replacement");
        await using (var original = await PrivateStorageActivationProbe.StartAsync(root, directory, "ControlInstalled"))
        {
            await original.WaitReadyAsync(directory);
            PrivateStorageActivationData.AssertSealed(fixture, installed: true);
            await original.KillAsync();
        }
        var originalGate = PrivateStorageV2Identity.Read(PrivateStorageActivationData.GatePath(root));
        PrivateStorageActivationData.Tamper(root, "gate");
        var replacementGate = PrivateStorageV2Identity.Read(PrivateStorageActivationData.GatePath(root));
        Assert.NotEqual(originalGate, replacementGate);
        Assert.Equal(0, new FileInfo(PrivateStorageActivationData.GatePath(root)).Length);
        // This fresh ordinary descriptor takes an actual native EX|NB flock on
        // the substituted inode. Its contention cannot prove the recorded gate busy.
        using var replacementLock = new PrivateStorageV2IndependentGate(PrivateStorageActivationData.GatePath(root));
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using (var refused = await PrivateStorageActivationProbe.StartAsync(root))
            AssertFailure(await refused.CompleteAsync(), "UnsafeStorage");
        before.AssertUnchanged(root.RootPath);
        Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(root.MarkerPath));
        fixture.AssertPreserved();
    }

    [Fact]
    public async Task OldMarkerAndStaleWriterReadinessCannotProveQuiescenceAndRealLegacyAttemptSurvivesActivation()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync(legacySentinel: false);
        var root = fixture.Root;
        var bytes = PrivateStorageData.Pattern(100003);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("old-live-writer-gate");
        string attempt;
        byte[] attemptBytes;
        PrivatePublicationInodeIdentity attemptIdentity;
        await using (var oldWriter = await PrivateStorageProbe.StartAsync(root, reference, "publish", bytes,
            gateDirectory: directory, gateStage: "beforeBodyEof"))
        {
            await oldWriter.WaitReadyAsync(directory); // This assertion also observes the owned process still running.
            Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(root.MarkerPath));
            Assert.Equal(fixture.MarkerIdentity, PrivateStorageV2Identity.Read(root.MarkerPath));
            Assert.Equal("ready", File.ReadAllText(Path.Combine(directory, "ready")));
            attempt = Assert.Single(root.AttemptFiles());
            Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(attempt));
            Assert.False(File.Exists(root.FinalPath(reference)));
            // No activation is attempted while this old writer is alive.
            await oldWriter.KillAsync();
            attemptBytes = File.ReadAllBytes(attempt);
            attemptIdentity = PrivateStorageV2Identity.Read(attempt);
        }
        // Marker and readiness bytes are unchanged across the actual death.
        // They therefore cannot establish that all old users have stopped.
        Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(root.MarkerPath));
        Assert.Equal("ready", File.ReadAllText(Path.Combine(directory, "ready")));
        await using (var activation = await PrivateStorageActivationProbe.StartAsync(root))
            AssertSuccess(await activation.CompleteAsync(), "Activated");
        Assert.Equal(attemptBytes, File.ReadAllBytes(attempt));
        Assert.Equal(attemptIdentity, PrivateStorageV2Identity.Read(attempt));
        Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(attempt));
        Assert.False(File.Exists(root.FinalPath(reference)));
        fixture.AssertPreserved();
        await fixture.AssertReadableAsync();
        await using var oldReader = await PrivateStorageProbe.StartAsync(root, fixture.Finals[0].Reference, "read");
        var oldResult = await oldReader.CompleteAsync();
        Assert.Equal(2, oldResult.ExitCode);
        Assert.Equal("UnsafeStorage", oldResult.Error);
    }

    [Theory]
    [InlineData("missingAcknowledgment")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("array")]
    [InlineData("privateArgument")]
    public async Task ActivationProbeRejectsMalformedPrivateCommandsBeforeAnyFilesystemWork(string malformed)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var command = PrivateStorageActivationProbe.Command(root, offlineAcknowledged: malformed != "missingAcknowledgment");
        command = malformed switch
        {
            "duplicate" => command[..^1] + ",\"offlineAcknowledged\":true}",
            "unknown" => command[..^1] + ",\"privateUnknown\":\"secret-not-a-diagnostic\"}",
            "array" => command[..^1] + ",\"reference\":[]}",
            _ => command
        };
        using var document = JsonDocument.Parse(command);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await using var child = await PrivateStorageActivationProbe.StartRawAsync(root, command,
            malformed == "privateArgument" ? "private-not-a-command-argument" : null);
        var result = await child.CompleteAsync();
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("Error", result.Status);
        Assert.Equal("ProbeFailure", result.Error);
        before.AssertUnchanged(root.RootPath);
    }

    private static void AssertSuccess(PrivateStorageActivationProbeResult result, string status)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Error);
    }

    private static void AssertFailure(PrivateStorageActivationProbeResult result, string error)
    {
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Error", result.Status);
        Assert.Equal(error, result.Error);
    }
}
