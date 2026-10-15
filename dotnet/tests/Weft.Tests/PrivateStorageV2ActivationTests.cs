using System.Diagnostics;
using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Owned local activation evidence; an offline acknowledgement is an operator assertion, never OS proof.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageV2ActivationTests
{
    [Fact]
    public async Task ActivationPreservesRootIdBothPayloadClassesAndUnknownLegacyAttempts()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        var rootIdentity = PrivateStorageV2Identity.Read(root.RootPath);
        var result = await FileSystemV2PrivateObjectStore.ActivateAsync(root.Options, true);
        Assert.Equal(PrivateStorageV2ActivationOutcome.Activated, result.Outcome);
        var receipt = Assert.IsType<PrivateStorageV2ActivationReceipt>(result.Receipt);
        Assert.Equal(fixture.MarkerRootId, receipt.RootId);
        Assert.NotEqual(Guid.Empty, receipt.ActivationId);
        Assert.NotEqual(Guid.Empty, receipt.ControlNamespaceId);
        Assert.Equal(Sha256Digest.FromBytes(fixture.MarkerBytes), receipt.OriginalMarkerDigest);
        Assert.Equal(Sha256Digest.FromBytes(File.ReadAllBytes(root.MarkerPath)), receipt.ActiveMarkerDigest);
        Assert.Equal(rootIdentity, PrivateStorageV2Identity.Read(root.RootPath));
        PrivateStorageActivationData.AssertSealed(fixture, installed: true, active: true);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => { using var old = root.Open(); });
        await fixture.AssertReadableAsync();
        var beforeReplay = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        var replay = await FileSystemV2PrivateObjectStore.ActivateAsync(root.Options, true);
        Assert.Equal(PrivateStorageV2ActivationOutcome.AlreadyActive, replay.Outcome);
        Assert.Equal(receipt, replay.Receipt);
        beforeReplay.AssertUnchanged(root.RootPath);
    }

    [Fact]
    public async Task MissingOfflineAcknowledgementRefusesBeforeAnyRootWorkOnFreshOrExistingPaths()
    {
        using var missing = new PrivateStorageTestRoot();
        await Assert.ThrowsAsync<ArgumentException>(() => FileSystemV2PrivateObjectStore.ActivateAsync(missing.Options, false));
        Assert.False(Directory.Exists(missing.RootPath));
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var before = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
        await Assert.ThrowsAsync<ArgumentException>(() => FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, false));
        before.AssertUnchanged(fixture.Root.RootPath);
        Assert.False(Directory.Exists(PrivateStorageActivationData.DirectoryPath(fixture.Root)));
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("control")]
    public async Task UnknownExistingActivationArtifactsAreNeverAdoptedOrSwept(string kind)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var path = kind == "preparation" ? PrivateStorageActivationData.DirectoryPath(fixture.Root) :
            PrivateStorageActivationData.InstalledControlPath(fixture.Root);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, PrivateStorageTestRoot.DirectoryMode);
        File.WriteAllBytes(Path.Combine(path, "sentinel"), [0, 255, 17]);
        var before = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true));
        before.AssertUnchanged(fixture.Root.RootPath);
        fixture.AssertPreserved();
    }

    [Fact]
    public async Task ExactReceiptRemainsReplayableAfterLegitimateV2PublicationPopulatesControlObjects()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var initial = await FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true);
        var registry = new PrivateStorageV2PhysicalRegistry();
        var bytes = "new v2 publication after offline activation"u8.ToArray();
        var reference = PrivateStorageData.Reference(bytes, PrivateObjectClass.DerivedArtifact);
        using (var store = FileSystemV2PrivateObjectStore.Open(fixture.Root.Options, () => registry))
            Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(
                PrivateStorageV2Data.Owner(reference), reference.Id.Scope, reference, new MemoryStream(bytes)));
        var before = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
        var replay = await FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true);
        Assert.Equal(PrivateStorageV2ActivationOutcome.AlreadyActive, replay.Outcome);
        Assert.Equal(initial.Receipt, replay.Receipt);
        before.AssertUnchanged(fixture.Root.RootPath);
        using var reader = FileSystemV2PrivateObjectStore.Open(fixture.Root.Options);
        Assert.Equal(bytes, await reader.ReadVerifiedAsync(reference.Id.Scope, reference));
        await fixture.AssertReadableAsync();
    }

    [Fact]
    public async Task OrdinaryV2OpenAndReadsIgnoreDamagedHistoricalActivationReceiptWithoutRepair()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        await FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true);
        // First reads legitimately provision permanent per-object control leaves.
        // Warm them before testing historical-receipt independence and an unchanged root.
        await fixture.AssertReadableAsync();
        // Authored provenance corruption models unavailable historical evidence;
        // ordinary Open is not a resume and makes no cross-incarnation assertion.
        PrivateStorageActivationData.Tamper(fixture.Root, "boot");
        var before = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
        await fixture.AssertReadableAsync();
        before.AssertUnchanged(fixture.Root.RootPath);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true));
        before.AssertUnchanged(fixture.Root.RootPath);
    }

    [Fact]
    public async Task ReceiptBackedIndependentGateContentionReturnsBusyWithoutChangingEvidence()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new PrivateStorageActivationObserver(async (value, token) =>
        {
            if (value.Phase != V2ActivationPhase.ReceiptSealed) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var first = FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true, observer, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            PrivateStorageActivationData.AssertSealed(fixture, installed: false);
            var before = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
            var busy = await FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true, deadline.Token);
            Assert.Equal(PrivateStorageV2ActivationOutcome.Busy, busy.Outcome);
            Assert.Null(busy.Receipt);
            before.AssertUnchanged(fixture.Root.RootPath);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(PrivateStorageV2ActivationOutcome.Activated, (await first).Outcome);
    }

    [Fact]
    public async Task CancellationImmediatelyBeforePivotLeavesExactReceiptBackedResumableState()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        using var canceled = new CancellationTokenSource();
        var observer = new PrivateStorageActivationObserver((value, _) =>
        {
            if (value.Phase == V2ActivationPhase.ControlInstalled) canceled.Cancel();
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileSystemV2PrivateObjectStore.ActivateAsync(
            fixture.Root.Options, true, observer, canceled.Token));
        PrivateStorageActivationData.AssertSealed(fixture, installed: true);
        var receipt = File.ReadAllBytes(PrivateStorageActivationData.ReceiptPath(fixture.Root));
        var result = await FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true);
        Assert.Equal(PrivateStorageV2ActivationOutcome.Activated, result.Outcome);
        Assert.Equal(receipt, File.ReadAllBytes(PrivateStorageActivationData.ReceiptPath(fixture.Root)));
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
    public async Task SameByteIdentityReplacementDuringFinalObserverRefusesBeforePivot(string target)
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        PrivateStorageActivationSnapshot? afterMutation = null;
        var observer = new PrivateStorageActivationObserver((value, _) =>
        {
            if (value.Phase == V2ActivationPhase.ControlInstalled)
            {
                PrivateStorageActivationData.AssertSealed(fixture, installed: true);
                PrivateStorageActivationData.Tamper(fixture.Root, target);
                afterMutation = PrivateStorageActivationSnapshot.Capture(fixture.Root.RootPath);
            }
            return ValueTask.CompletedTask;
        });
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => FileSystemV2PrivateObjectStore.ActivateAsync(fixture.Root.Options, true, observer));
        Assert.NotNull(afterMutation);
        afterMutation.AssertUnchanged(fixture.Root.RootPath);
        Assert.Equal(fixture.MarkerBytes, File.ReadAllBytes(fixture.Root.MarkerPath));
        fixture.AssertPreserved();
    }

    [Fact]
    public async Task ExplicitV2InitializationDoesNotInventAnActivationReceiptForLaterReplay()
    {
        using var root = new PrivateStorageTestRoot();
        var initialized = await RunToolAsync(root, "initialize-v2", root.RootPath, root.ApplicationRoot);
        Assert.Equal(0, initialized.ExitCode);
        Assert.Equal("InitializedV2", initialized.Output.Trim());
        Assert.Empty(initialized.Error);
        var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage,
            () => FileSystemV2PrivateObjectStore.ActivateAsync(root.Options, true));
        before.AssertUnchanged(root.RootPath);
        Assert.False(Directory.Exists(PrivateStorageActivationData.DirectoryPath(root)));
        using var reopened = FileSystemV2PrivateObjectStore.Open(root.Options);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => { using var old = root.Open(); });
    }

    [Fact]
    public async Task OwnerToolRequiresExplicitOfflineFlagAndReturnsOnlyBoundedCategories()
    {
        using var fixture = await PrivateStorageActivationFixture.CreateAsync();
        var root = fixture.Root;
        foreach (var arguments in new[]
        {
            new[] { "activate-v2", root.RootPath, root.ApplicationRoot },
            new[] { "activate-v2", "--online", root.RootPath, root.ApplicationRoot }
        })
        {
            var before = PrivateStorageActivationSnapshot.Capture(root.RootPath);
            var refused = await RunToolAsync(root, arguments);
            Assert.Equal(2, refused.ExitCode);
            Assert.Empty(refused.Output);
            Assert.NotEmpty(refused.Error);
            before.AssertUnchanged(root.RootPath);
        }
        var activated = await RunToolAsync(root, "activate-v2", "--offline", root.RootPath, root.ApplicationRoot);
        Assert.Equal(0, activated.ExitCode);
        Assert.Equal("Activated", activated.Output.Trim());
        Assert.Empty(activated.Error);
        var beforeReplay = PrivateStorageActivationSnapshot.Capture(root.RootPath);
        var replay = await RunToolAsync(root, "activate-v2", "--offline", root.RootPath, root.ApplicationRoot);
        Assert.Equal(0, replay.ExitCode);
        Assert.Equal("AlreadyActive", replay.Output.Trim());
        Assert.Empty(replay.Error);
        beforeReplay.AssertUnchanged(root.RootPath);
        await fixture.AssertReadableAsync();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunToolAsync(
        PrivateStorageTestRoot root, params string[] arguments)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "StorageTool", "Weft.StorageTool.dll");
        Assert.True(File.Exists(dll), "The owner storage tool must be copied to the test output.");
        var start = new ProcessStartInfo(PrivateStorageActivationProbe.Host())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned activation tool did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = PrivateStorageActivationProbe.CaptureAsync(process.StandardOutput, 1024, deadline.Token);
        var error = PrivateStorageActivationProbe.CaptureAsync(process.StandardError, 1024, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            var stdout = await output;
            var stderr = await error;
            Assert.DoesNotContain(root.BasePath, stdout + stderr, StringComparison.Ordinal);
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
