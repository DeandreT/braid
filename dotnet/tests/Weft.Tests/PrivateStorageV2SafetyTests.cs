using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Real owned files and child-local kernel restrictions; claim authority and mismatched captures are explicitly synthetic.</summary>
[SupportedOSPlatform("linux")]
public sealed class PrivateStorageV2SafetyTests
{
    [Theory]
    [InlineData("boot")]
    [InlineData("mount-namespace")]
    [InlineData("control-namespace")]
    [InlineData("root")]
    public async Task MismatchedCapturedTemporaryIdentityRetainsTheRealInodeBytesAndUnfencedSeals(string mismatch)
    {
        using var root = new PrivateStorageTestRoot();
        using (FileSystemV2PrivateObjectStore.Initialize(root.Options)) { }
        var bytes = PrivateStorageData.Pattern(65543);
        var reference = PrivateStorageData.Reference(bytes);
        var directory = root.NewDirectory("captured-identity");
        await using (var child = await PrivateStorageV2Probe.StartAsync(root, reference, "publish",
            PrivateStorageV2Data.Owner(reference), bytes, directory, "PayloadPrepared"))
        {
            await child.WaitReadyAsync(directory);
            Assert.False(File.Exists(root.FinalPath(reference)));
            await child.KillAsync();
        }

        // The seals and file are genuine child output. Only the separately captured
        // metadata below is authored: no boot, namespace, marker or seal is changed.
        var original = PrivateStorageV2Data.SyntheticRecordFromSeals(root, reference, bound: true);
        var temp = PrivateStorageV2Data.TempPath(root, original.Reservation.Key);
        var leaf = PrivateStorageV2Data.TempLeaf(root, original.Reservation.Key);
        var inode = PrivateStorageV2Identity.Read(temp);
        var controls = PrivateStorageV2Data.Files(leaf);
        Assert.Equal(bytes, File.ReadAllBytes(temp));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(temp));
        var captured = WithMismatchedIdentity(original, mismatch);
        Assert.NotEqual(original, captured);

        using var cleanup = FileSystemV2PrivateObjectStore.Open(root.Options,
            () => throw new InvalidOperationException("Physical cleanup cannot resolve a publication registry."));
        Assert.Equal(PrivateObjectRetirementOutcome.UnsafeStorage,
            await cleanup.RetireAsync(PrivateStorageV2Data.TemporaryClaim(captured)));

        Assert.Equal(inode, PrivateStorageV2Identity.Read(temp));
        Assert.Equal(bytes, File.ReadAllBytes(temp));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(temp));
        var after = PrivateStorageV2Data.Files(leaf);
        Assert.Equal(controls.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var control in controls) Assert.Equal(control.Value, after[control.Key]);
        Assert.False(File.Exists(Path.Combine(leaf, "temp.retired")));
        Assert.False(File.Exists(Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.retired")));
        Assert.False(File.Exists(root.FinalPath(reference)));
    }

    [Fact]
    public void ActualProcFilesystemIsRejectedBeforeCreatingAnyV2StorageRootOrResolvingARegistry()
    {
        using var root = new PrivateStorageTestRoot();
        var unsupportedRoot = $"/proc/weft-v2-storage-unsupported-{Guid.NewGuid():N}";
        var options = new FileSystemStorageOptions(unsupportedRoot, root.ApplicationRoot);
        var registryResolutions = 0;
        Assert.False(Directory.Exists(unsupportedRoot));
        PrivateStorageData.Error(PrivateObjectStorageError.Unsupported, () =>
            FileSystemV2PrivateObjectStore.Initialize(options, () =>
            {
                registryResolutions++;
                throw new InvalidOperationException("Unsupported roots cannot resolve a publication registry.");
            }));
        Assert.False(Directory.Exists(unsupportedRoot));
        Assert.Equal(0, registryResolutions);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("read")]
    [InlineData("retire")]
    public async Task ActualChildKernelFlockDenialRefusesWithoutPayloadRegistryFenceOrUnlink(string operation)
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(8197);
        var reference = PrivateStorageData.Reference(bytes);
        var owner = PrivateStorageV2Data.Owner(reference);
        using (var writer = FileSystemV2PrivateObjectStore.Initialize(root.Options, () => new PrivateStorageV2PhysicalRegistry()))
            Assert.Equal(PrivateObjectPublishResult.Created,
                await writer.PublishCreateOnlyAsync(owner, reference.Id.Scope, reference, new MemoryStream(bytes)));
        var final = root.FinalPath(reference);
        var finalIdentity = PrivateStorageV2Identity.Read(final);
        var gate = Path.Combine(PrivateStorageV2Data.ObjectLeaf(root, reference.Id), "object.gate");
        var gateIdentity = PrivateStorageV2Identity.Read(gate);

        // The child applies and proves a real seccomp EPERM filter after Open.
        // Synthetic claim metadata does not stand in for the kernel refusal.
        await using var child = await PrivateStorageV2Probe.StartAsync(root, reference, operation,
            owner: operation == "publish" ? owner : null,
            bytes: operation == "publish" ? bytes : null,
            cleanup: operation == "retire" ? PrivateStorageV2Data.FinalClaim(reference, owner) : null,
            denyFlockAfterOpen: true);
        var result = await child.CompleteAsync();
        Assert.True(result.KernelFlockDenied);
        if (operation == "retire")
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Io", result.Status);
            Assert.Null(result.Error);
        }
        else
        {
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("Error", result.Status);
            Assert.Equal("Io", result.Error);
        }
        Assert.Equal(0, result.SourceReadCalls);
        Assert.Equal(0, result.SourceBytes);
        Assert.Equal(0, result.RegistryFactoryResolutions);
        Assert.Equal(0, result.ReserveCalls);
        Assert.Equal(0, result.BindCalls);
        Assert.Equal(0, result.GetCalls);
        Assert.Equal(finalIdentity, PrivateStorageV2Identity.Read(final));
        Assert.Equal(gateIdentity, PrivateStorageV2Identity.Read(gate));
        Assert.Equal(bytes, File.ReadAllBytes(final));
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(final));
        Assert.Empty(Directory.GetFiles(root.RootPath, ".attempt-*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(PrivateStorageV2Data.ControlRoot(root), "*.retired", SearchOption.AllDirectories));
        // Publish may create an empty TempID control leaf before its first flock.
        // It must not CreateNew a payload, reserve metadata, read source or fence.
    }

    private static PrivatePublicationRecord WithMismatchedIdentity(PrivatePublicationRecord original, string mismatch)
    {
        var reservation = original.Reservation;
        var binding = Assert.IsType<PrivatePublicationBinding>(original.Binding);
        var identity = binding.ActualIdentity;
        var rootId = mismatch == "root" ? DifferentId(identity.RootId) : identity.RootId;
        var changed = new PrivatePublicationFileIdentity(rootId,
            mismatch == "control-namespace" ? DifferentId(identity.ControlNamespaceId) : identity.ControlNamespaceId,
            mismatch == "boot" ? DifferentId(identity.LinuxBootId) : identity.LinuxBootId,
            mismatch == "mount-namespace" ? identity.MountNamespaceDevice ^ 1UL : identity.MountNamespaceDevice,
            identity.MountNamespaceInode, identity.EffectiveUserId, identity.FileIdentity, identity.DirectoryIdentity);
        var key = new PrivatePublicationKey(rootId, reservation.Key.Object, reservation.Key.TempId);
        return new(new(reservation.PublicationId, key, reservation.StoreKind, reservation.Owner, reservation.AbsenceRecordDigest,
            reservation.RegisteredAtUtc), new(changed, binding.BoundRecordDigest, binding.BoundAtUtc));
    }

    private static Guid DifferentId(Guid original)
    {
        var first = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        return original == first ? Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee") : first;
    }
}
