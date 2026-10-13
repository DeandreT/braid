using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class PrivateStorageProcessTests
{
    [Fact]
    public async Task IndependentWriterAndRestartedReaderPreserveExactOpaqueBytes()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var bytes = new byte[] { 0, 255, 13, 10 }.Concat(PrivateStorageData.Pattern(4099)).ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        await using (var writer = await PrivateStorageProbe.StartAsync(root, reference, "publish", bytes))
            Check(await writer.CompleteAsync(), "Created", reference);
        await using (var freshReader = await PrivateStorageProbe.StartAsync(root, reference, "read"))
        {
            var result = await freshReader.CompleteAsync();
            Check(result, "Read", reference);
            Assert.Equal(bytes, Convert.FromBase64String(result.Base64Content!));
        }
        using var reopened = root.Open();
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Fact]
    public Task IndependentIdenticalWritersProduceOneCreatedAndOneVerifiedAlreadyPresent() => RaceAsync(different: false);

    [Fact]
    public Task IndependentDifferentWritersProduceOneCreatedAndOneConflictWithoutPartialVisibility() => RaceAsync(different: true);

    [Fact]
    public async Task KillingBeforePublicationLeavesOnlyAPrivateAttemptAndNeverSweepsItOnReopen()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var bytes = PrivateStorageData.Pattern(100_003);
        var reference = PrivateStorageData.Reference(bytes);
        var gate = root.NewDirectory("before-publication-gate");
        await using (var writer = await PrivateStorageProbe.StartAsync(root, reference, "publish", patternLength: bytes.Length,
            gateDirectory: gate, gateStage: "beforeBodyEof"))
        {
            await writer.WaitReadyAsync(gate);
            Assert.False(File.Exists(root.FinalPath(reference)));
            var attempts = root.AttemptFiles();
            Assert.NotEmpty(attempts);
            foreach (var attempt in attempts)
            {
                Assert.Equal(PrivateStorageTestRoot.AttemptMode, File.GetUnixFileMode(attempt));
                Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(attempt).Uid);
            }
            await writer.KillAsync();
        }
        var left = root.AttemptFiles();
        Assert.NotEmpty(left);
        using (var reopened = root.Open())
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(left.Order(StringComparer.Ordinal), root.AttemptFiles().Order(StringComparer.Ordinal));
        await using (var retry = await PrivateStorageProbe.StartAsync(root, reference, "publish", patternLength: bytes.Length))
            Check(await retry.CompleteAsync(), "Created", reference);
        using var recovered = root.Open();
        Assert.Equal(bytes, await recovered.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.All(left, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task KillingAfterPublicationRetainsCompleteBytesAndMatchingRetryIsAlreadyPresent()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var bytes = PrivateStorageData.Pattern(200_007);
        var reference = PrivateStorageData.Reference(bytes);
        var gate = root.NewDirectory("after-publication-gate");
        await using (var writer = await PrivateStorageProbe.StartAsync(root, reference, "publish", patternLength: bytes.Length,
            gateDirectory: gate, gateStage: "afterPublication"))
        {
            await writer.WaitReadyAsync(gate);
            using (var independent = root.Open())
                Assert.Equal(bytes, await independent.ReadVerifiedAsync(reference.Id.Scope, reference));
            await writer.KillAsync();
        }
        await using (var restartedReader = await PrivateStorageProbe.StartAsync(root, reference, "read"))
            Check(await restartedReader.CompleteAsync(), "Read", reference);
        await using (var retry = await PrivateStorageProbe.StartAsync(root, reference, "publish", patternLength: bytes.Length))
            Check(await retry.CompleteAsync(), "AlreadyPresent", reference);
        Assert.Empty(root.AttemptFiles());
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
    }

    [Fact]
    public async Task IndependentMissingReadReturnsCategorizedFailureWithoutPhysicalPaths()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var reference = PrivateStorageData.Reference([1, 2, 3]);
        await using var reader = await PrivateStorageProbe.StartAsync(root, reference, "read");
        var result = await reader.CompleteAsync();
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Error", result.Status);
        Assert.Equal("Missing", result.Error);
        Assert.Null(result.Base64Content);
    }

    private static async Task RaceAsync(bool different)
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        var firstBytes = PrivateStorageData.Pattern(1_000_003);
        var secondBytes = different ? firstBytes.Select(value => (byte)(value ^ 1)).ToArray() : firstBytes;
        var first = PrivateStorageData.Reference(firstBytes);
        var second = new PrivateObjectReference(first.Id, Sha256Digest.FromBytes(secondBytes), secondBytes.LongLength);
        var gateA = root.NewDirectory("race-gate-a");
        var gateB = root.NewDirectory("race-gate-b");
        await using var writerA = await PrivateStorageProbe.StartAsync(root, first, "publish", firstBytes, gateDirectory: gateA, gateStage: "beforeBodyEof");
        await using var writerB = await PrivateStorageProbe.StartAsync(root, second, "publish", secondBytes, gateDirectory: gateB, gateStage: "beforeBodyEof");
        await Task.WhenAll(writerA.WaitReadyAsync(gateA), writerB.WaitReadyAsync(gateB));
        Assert.False(File.Exists(root.FinalPath(first)));
        using var stop = new CancellationTokenSource();
        var missing = 0;
        var complete = 0;
        var observedMissing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var observed = await File.ReadAllBytesAsync(root.FinalPath(first));
                    Assert.Equal(firstBytes.Length, observed.Length);
                    var digest = Sha256Digest.FromBytes(observed);
                    Assert.True(digest == first.Digest || digest == second.Digest);
                    complete++;
                    observedComplete.TrySetResult();
                }
                catch (FileNotFoundException)
                {
                    missing++;
                    observedMissing.TrySetResult();
                }
                await Task.Delay(TimeSpan.FromMilliseconds(2));
            }
        });
        PrivateStorageProbeResult resultA;
        PrivateStorageProbeResult resultB;
        try
        {
            await observedMissing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(PrivateStorageProbe.ReleaseAsync(gateA), PrivateStorageProbe.ReleaseAsync(gateB));
            resultA = await writerA.CompleteAsync();
            resultB = await writerB.CompleteAsync();
            await observedComplete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { stop.Cancel(); await observer; }
        Assert.True(missing > 0);
        Assert.True(complete > 0);
        var results = new[] { resultA, resultB };
        Assert.Single(results, result => result.Status == "Created" && result.ExitCode == 0);
        if (different)
            Assert.Single(results, result => result.Status == "Error" && result.Error == "Conflict" && result.ExitCode == 2);
        else
            Assert.Single(results, result => result.Status == "AlreadyPresent" && result.ExitCode == 0);
        var winner = resultA.Status == "Created" ? first : second;
        var expected = resultA.Status == "Created" ? firstBytes : secondBytes;
        using var reopened = root.Open();
        Assert.Equal(expected, await reopened.ReadVerifiedAsync(winner.Id.Scope, winner));
        Assert.Equal(expected, File.ReadAllBytes(root.FinalPath(winner)));
        Assert.Empty(root.AttemptFiles());
    }

    private static void Check(PrivateStorageProbeResult result, string status, PrivateObjectReference reference)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(reference.ByteCount, result.ByteCount);
        Assert.Equal(reference.Digest.Value, result.Sha256);
    }
}
