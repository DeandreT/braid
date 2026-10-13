using System.Runtime.Versioning;
using System.Text;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class PrivateFileSystemStorageTests
{
    [Fact]
    public async Task OpaqueBytesAndEmptyDerivedObjectsRoundTripExactly()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = new byte[] { 0, 255, 128, 13, 10 }.Concat(Encoding.UTF8.GetBytes(" {\n  \"unknown\": 9007199254740993, \"negative\": -0.0 }\r\n")).ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes)));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        var empty = PrivateStorageData.Reference([], PrivateObjectClass.DerivedArtifact);
        Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(empty.Id.Scope, empty, new MemoryStream([])));
        Assert.Empty(await store.ReadVerifiedAsync(empty.Id.Scope, empty));
    }

    [Fact]
    public async Task NativeZeroAndDeclaredExcessAreRejectedBeforeReadingTheInput()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        foreach (var reference in new[]
        {
            PrivateStorageData.Reference([]),
            new PrivateObjectReference(PrivateObjectId.Create(PrivateStorageData.Scope(), PrivateObjectClass.NativeArtifact), Sha256Digest.FromBytes([]), PrivateStorageTestRoot.NativeLimit + 1),
            new PrivateObjectReference(PrivateObjectId.Create(PrivateStorageData.Scope(), PrivateObjectClass.DerivedArtifact), Sha256Digest.FromBytes([]), PrivateStorageTestRoot.DerivedLimit + 1)
        })
        {
            using var input = new PrivateStorageInput([7]);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
            Assert.Equal(0, input.ReadCalls);
            Assert.False(File.Exists(root.FinalPath(reference)));
        }
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public async Task NativeFourMiBAndIndependentDerivedSixteenMiBCeilingsRoundTrip()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        foreach (var (size, objectClass) in new[]
        {
            (PrivateStorageTestRoot.NativeLimit, PrivateObjectClass.NativeArtifact),
            (PrivateStorageTestRoot.NativeLimit + 17, PrivateObjectClass.DerivedArtifact),
            (PrivateStorageTestRoot.DerivedLimit, PrivateObjectClass.DerivedArtifact)
        })
        {
            var bytes = PrivateStorageData.Pattern(size);
            var reference = PrivateStorageData.Reference(bytes, objectClass);
            using var input = new PrivateStorageInput(bytes, chunkSize: 65521);
            await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input);
            Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
            Assert.InRange(input.MaximumRequested, 1, 64 * 1024);
        }
    }

    [Fact]
    public async Task ReducedProfilesAndHardConfigurationMaximaAreEnforced()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemPrivateObjectStore.Initialize(new(root.RootPath, root.ApplicationRoot, nativeByteLimit: 8, derivedByteLimit: 16, maximumConcurrency: 1));
        var reference = PrivateStorageData.Reference(new byte[9]);
        using var input = new PrivateStorageInput(new byte[9]);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(0, input.ReadCalls);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystemStorageOptions(root.NewPath("other"), root.ApplicationRoot, nativeByteLimit: PrivateStorageTestRoot.NativeLimit + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystemStorageOptions(root.NewPath("other"), root.ApplicationRoot, derivedByteLimit: PrivateStorageTestRoot.DerivedLimit + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystemStorageOptions(root.NewPath("other"), root.ApplicationRoot, maximumConcurrency: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystemStorageOptions(root.NewPath("other"), root.ApplicationRoot, maximumConcurrency: 5));
    }

    [Fact]
    public void ActualProcFilesystemIsRejectedAsUnsupportedBeforeCreatingStorage()
    {
        using var root = new PrivateStorageTestRoot();
        var unsupportedRoot = $"/proc/weft-storage-unsupported-{Guid.NewGuid():N}";
        Assert.False(Directory.Exists(unsupportedRoot));
        var options = new FileSystemStorageOptions(unsupportedRoot, root.ApplicationRoot);
        PrivateStorageData.Error(PrivateObjectStorageError.Unsupported, () => FileSystemPrivateObjectStore.Initialize(options));
        Assert.False(Directory.Exists(unsupportedRoot));
    }

    [Fact]
    public async Task EveryExpectedScopeComponentIsCheckedBeforeReadingOrOpeningTheObject()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(123);
        var reference = PrivateStorageData.Reference(bytes);
        var scope = reference.Id.Scope;
        foreach (var foreign in new[]
        {
            new RevisionScope(Guid.NewGuid(), scope.ProjectId, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, Guid.NewGuid(), scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, scope.MappingId, Guid.NewGuid())
        })
        {
            using var input = new PrivateStorageInput(bytes);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch, () => store.PublishCreateOnlyAsync(foreign, reference, input));
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.ScopeMismatch, () => store.ReadVerifiedAsync(foreign, reference));
            Assert.Equal(0, input.ReadCalls);
        }
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public async Task ClosedClassesAndEveryScopeComponentSeparatePhysicalNamespaces()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(17);
        var scope = PrivateStorageData.Scope();
        var id = Guid.NewGuid();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in new[]
        {
            scope, new RevisionScope(Guid.NewGuid(), scope.ProjectId, scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, Guid.NewGuid(), scope.MappingId, scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, Guid.NewGuid(), scope.RevisionId),
            new RevisionScope(scope.OrganizationId, scope.ProjectId, scope.MappingId, Guid.NewGuid())
        })
            foreach (var objectClass in new[] { PrivateObjectClass.NativeArtifact, PrivateObjectClass.DerivedArtifact })
            {
                var reference = PrivateStorageData.Reference(bytes, objectClass, variant, id);
                Assert.Equal(PrivateObjectPublishResult.Created, await store.PublishCreateOnlyAsync(variant, reference, new MemoryStream(bytes)));
                Assert.True(paths.Add(root.FinalPath(reference)));
                Assert.Equal(bytes, await store.ReadVerifiedAsync(variant, reference));
            }
        Assert.Equal(10, Directory.EnumerateFiles(root.RootPath, "*.blob", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task NonseekableShortExtraAndWrongDigestInputsNeverPublish()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var desired = PrivateStorageData.Pattern(100_003);
        foreach (var body in new[] { desired[..^1], desired.Concat(new byte[] { 1 }).ToArray(), desired.Select(value => (byte)(value ^ 1)).ToArray() })
        {
            var reference = PrivateStorageData.Reference(desired);
            using var input = new PrivateStorageInput(body, chunkSize: 137);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
            Assert.False(File.Exists(root.FinalPath(reference)));
            Assert.Empty(root.AttemptFiles());
            Assert.True(input.CanRead);
            Assert.InRange(input.MaximumRequested, 1, 64 * 1024);
        }
    }

    [Fact]
    public async Task ActualOverBudgetInputStopsAtTheBoundAndPreservesAnotherAttempt()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = FileSystemPrivateObjectStore.Initialize(new(root.RootPath, root.ApplicationRoot, nativeByteLimit: 8));
        var original = PrivateStorageData.Reference(new byte[] { 1, 2, 3 });
        await store.PublishCreateOnlyAsync(original.Id.Scope, original, new MemoryStream([1, 2, 3]));
        var otherAttempt = Path.Combine(Path.GetDirectoryName(root.FinalPath(original))!, ".unrelated-owned-test-attempt");
        File.WriteAllBytes(otherAttempt, [99]);
        File.SetUnixFileMode(otherAttempt, PrivateStorageTestRoot.AttemptMode);
        var reference = PrivateStorageData.Reference(new byte[8], scope: original.Id.Scope);
        using var input = new PrivateStorageInput(new byte[9], chunkSize: 1);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Limit, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(otherAttempt));
        Assert.Equal(new[] { otherAttempt }, root.AttemptFiles());
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.ReadVerifiedAsync(original.Id.Scope, original));
    }

    [Fact]
    public async Task CancellationAndThrowingInputCleanOnlyTheirUnpublishedAttempts()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(200_000);
        var original = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(original.Id.Scope, original, new MemoryStream(bytes));
        using (var cancellation = new CancellationTokenSource())
        using (var input = new PrivateStorageInput(bytes, chunkSize: 123, afterRead: _ => cancellation.Cancel()))
        {
            var canceled = PrivateStorageData.Reference(bytes, scope: original.Id.Scope);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishCreateOnlyAsync(canceled.Id.Scope, canceled, input, cancellation.Token));
            Assert.False(File.Exists(root.FinalPath(canceled)));
            Assert.True(input.CanRead);
        }
        using (var input = new PrivateStorageInput(bytes, chunkSize: 123, throwAfter: 123))
        {
            var failed = PrivateStorageData.Reference(bytes, scope: original.Id.Scope);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Io, () => store.PublishCreateOnlyAsync(failed.Id.Scope, failed, input));
            Assert.False(File.Exists(root.FinalPath(failed)));
            Assert.True(input.CanRead);
        }
        Assert.Empty(root.AttemptFiles());
        Assert.Equal(bytes, await store.ReadVerifiedAsync(original.Id.Scope, original));
    }

    [Fact]
    public async Task IdenticalRetryStillChecksIncomingBodyAndConflictingRetryCannotOverwrite()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(12_345);
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        using (var retry = new PrivateStorageInput(bytes, chunkSize: 101))
        {
            Assert.Equal(PrivateObjectPublishResult.AlreadyPresent, await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, retry));
            Assert.True(retry.ReadCalls > 1);
        }
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new PrivateStorageInput(bytes[..^1])));
        var different = bytes.Select(value => (byte)(value ^ 1)).ToArray();
        var conflicting = new PrivateObjectReference(reference.Id, Sha256Digest.FromBytes(different), different.LongLength);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => store.PublishCreateOnlyAsync(reference.Id.Scope, conflicting, new PrivateStorageInput(different)));
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public async Task MissingIsDistinctFromTruncatedExtendedAndCorruptObjectsAndRetryCannotRepairThem()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(257);
        var reference = PrivateStorageData.Reference(bytes);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Missing, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var path = root.FinalPath(reference);
        foreach (var damage in new[] { bytes[..^1], bytes.Concat(new byte[] { 42 }).ToArray(), bytes.Select(value => (byte)(value ^ 1)).ToArray() })
        {
            File.SetUnixFileMode(path, PrivateStorageTestRoot.AttemptMode);
            File.WriteAllBytes(path, damage);
            File.SetUnixFileMode(path, PrivateStorageTestRoot.FinalMode);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Integrity, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Conflict, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes)));
            Assert.Equal(damage, File.ReadAllBytes(path));
        }
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public async Task SourceAndReadBufferMutationCannotMutateTheRetainedObject()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(333);
        var expected = bytes.ToArray();
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        Array.Fill(bytes, (byte)0);
        var read = await store.ReadVerifiedAsync(reference.Id.Scope, reference);
        Assert.Equal(expected, read);
        Array.Fill(read, (byte)255);
        Assert.Equal(expected, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
    }

    [Fact]
    public async Task IndependentlyReopenedAdapterPreservesMarkerAndExactObject()
    {
        using var root = new PrivateStorageTestRoot();
        var bytes = PrivateStorageData.Pattern(4097);
        var reference = PrivateStorageData.Reference(bytes);
        using (var initial = root.Initialize())
            await initial.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var marker = File.ReadAllBytes(root.MarkerPath);
        using (var reopened = root.Open())
            Assert.Equal(bytes, await reopened.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Initialize());
        Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
    }

    [Fact]
    public async Task RealDirectoriesMarkerAndObjectsHavePrivateModesSingleLinksAndServiceOwnership()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(71);
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        foreach (var directory in Directory.EnumerateDirectories(root.RootPath, "*", SearchOption.AllDirectories).Prepend(root.RootPath))
        {
            Assert.Equal(PrivateStorageTestRoot.DirectoryMode, File.GetUnixFileMode(directory));
            Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(directory).Uid);
        }
        foreach (var path in new[] { root.MarkerPath, root.FinalPath(reference) })
        {
            Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(path));
            var metadata = PrivateStorageLinux.Metadata(path);
            Assert.Equal(PrivateStorageLinux.EffectiveUid, metadata.Uid);
            Assert.Equal(1u, metadata.Links);
            Assert.Equal(0x8000, metadata.Mode & 0xf000);
        }
    }

    [Fact]
    public void InitializationNeverAdoptsOrRepairsAnExistingUnmarkedDirectory()
    {
        using var root = new PrivateStorageTestRoot();
        Directory.CreateDirectory(root.RootPath);
        File.SetUnixFileMode(root.RootPath, PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupRead);
        var sentinel = Path.Combine(root.RootPath, "retained-owned-test-file");
        File.WriteAllBytes(sentinel, [7, 8, 9]);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Initialize());
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(sentinel));
        Assert.Equal(PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupRead, File.GetUnixFileMode(root.RootPath));
        Assert.False(File.Exists(root.MarkerPath));
    }

    [Fact]
    public async Task UnsafeNonstickyParentIsNeverAdoptedOrRepairedDuringInitializationOrReopen()
    {
        using var root = new PrivateStorageTestRoot();
        var sentinel = root.NewPath("parent-owned-sentinel");
        File.WriteAllBytes(sentinel, [17, 18]);
        File.SetUnixFileMode(sentinel, PrivateStorageTestRoot.AttemptMode);
        foreach (var mode in new[]
        {
            PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupWrite,
            PrivateStorageTestRoot.DirectoryMode | UnixFileMode.OtherWrite
        })
        {
            File.SetUnixFileMode(root.BasePath, mode);
            try
            {
                PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Initialize());
                Assert.False(Directory.Exists(root.RootPath));
                Assert.Equal(mode, File.GetUnixFileMode(root.BasePath));
                Assert.Equal(new byte[] { 17, 18 }, File.ReadAllBytes(sentinel));
            }
            finally { File.SetUnixFileMode(root.BasePath, PrivateStorageTestRoot.DirectoryMode); }
        }
        var bytes = PrivateStorageData.Pattern(37);
        var reference = PrivateStorageData.Reference(bytes);
        using (var store = root.Initialize())
            await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var marker = File.ReadAllBytes(root.MarkerPath);
        var unsafeMode = PrivateStorageTestRoot.DirectoryMode | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(root.BasePath, unsafeMode);
        try
        {
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
            Assert.Equal(unsafeMode, File.GetUnixFileMode(root.BasePath));
            Assert.Equal(marker, File.ReadAllBytes(root.MarkerPath));
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        }
        finally { File.SetUnixFileMode(root.BasePath, PrivateStorageTestRoot.DirectoryMode); }

        var nestedParent = root.NewDirectory("private-nested-parent");
        var nestedRoot = Path.Combine(nestedParent, "store");
        var nestedOptions = new FileSystemStorageOptions(nestedRoot, root.ApplicationRoot);
        var unsafeAncestorMode = PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(root.BasePath, unsafeAncestorMode);
        try
        {
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => FileSystemPrivateObjectStore.Initialize(nestedOptions));
            Assert.False(Directory.Exists(nestedRoot));
            Assert.Equal(PrivateStorageTestRoot.DirectoryMode, File.GetUnixFileMode(nestedParent));
            Assert.Equal(unsafeAncestorMode, File.GetUnixFileMode(root.BasePath));
            Assert.Equal(new byte[] { 17, 18 }, File.ReadAllBytes(sentinel));
        }
        finally { File.SetUnixFileMode(root.BasePath, PrivateStorageTestRoot.DirectoryMode); }
        using (var nested = FileSystemPrivateObjectStore.Initialize(nestedOptions))
            await nested.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var nestedMarkerPath = Path.Combine(nestedRoot, ".weft-private-storage");
        var nestedMarker = File.ReadAllBytes(nestedMarkerPath);
        File.SetUnixFileMode(root.BasePath, unsafeAncestorMode);
        try
        {
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => FileSystemPrivateObjectStore.Open(nestedOptions));
            Assert.Equal(nestedMarker, File.ReadAllBytes(nestedMarkerPath));
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference, nestedRoot)));
            Assert.Equal(PrivateStorageTestRoot.DirectoryMode, File.GetUnixFileMode(nestedParent));
            Assert.Equal(unsafeAncestorMode, File.GetUnixFileMode(root.BasePath));
        }
        finally { File.SetUnixFileMode(root.BasePath, PrivateStorageTestRoot.DirectoryMode); }
    }

    [Fact]
    public async Task MissingActiveMarkerOrRootIsUnsafeStorageAndCannotChangeThePublishedObject()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = PrivateStorageData.Pattern(53);
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var savedMarker = root.NewPath("retained-active-marker");
        File.Move(root.MarkerPath, savedMarker);
        try
        {
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            using var input = new PrivateStorageInput(bytes);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
            Assert.Equal(0, input.ReadCalls);
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference)));
        }
        finally { File.Move(savedMarker, root.MarkerPath); }
        var savedRoot = root.NewPath("retained-active-root");
        Directory.Move(root.RootPath, savedRoot);
        try
        {
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
            using var input = new PrivateStorageInput(bytes);
            await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, input));
            Assert.Equal(0, input.ReadCalls);
            Assert.Equal(bytes, File.ReadAllBytes(root.FinalPath(reference, savedRoot)));
        }
        finally { Directory.Move(savedRoot, root.RootPath); }
        Assert.Equal(bytes, await store.ReadVerifiedAsync(reference.Id.Scope, reference));
        Assert.Empty(root.AttemptFiles());
    }

    [Fact]
    public void MalformedUnknownOrZeroIdentityMarkersAreRefusedWithoutRewrite()
    {
        using var root = new PrivateStorageTestRoot();
        using (root.Initialize()) { }
        foreach (var marker in new[] { "", "weft-private-storage-v2\n" + Guid.NewGuid().ToString("N") + "\n", "weft-private-storage-v1\n" + new string('0', 32) + "\n", "weft-private-storage-v1\nnot-an-id\n", "weft-private-storage-v1\n" + Guid.NewGuid().ToString("N") + "\nextra\n" })
        {
            File.SetUnixFileMode(root.MarkerPath, PrivateStorageTestRoot.AttemptMode);
            File.WriteAllText(root.MarkerPath, marker);
            File.SetUnixFileMode(root.MarkerPath, PrivateStorageTestRoot.FinalMode);
            PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
            Assert.Equal(marker, File.ReadAllText(root.MarkerPath));
        }
    }

    [Fact]
    public async Task UnsafeRootMarkerGeneratedDirectoryAndFinalModesAreRefused()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = new byte[] { 1, 2 };
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        foreach (var (path, unsafeMode, safeMode) in new[]
        {
            (root.RootPath, PrivateStorageTestRoot.DirectoryMode | UnixFileMode.OtherExecute, PrivateStorageTestRoot.DirectoryMode),
            (root.MarkerPath, PrivateStorageTestRoot.AttemptMode, PrivateStorageTestRoot.FinalMode)
        })
        {
            File.SetUnixFileMode(path, unsafeMode);
            try { PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open()); }
            finally { File.SetUnixFileMode(path, safeMode); }
        }
        foreach (var (path, unsafeMode, safeMode) in new[]
        {
            (Path.GetDirectoryName(root.FinalPath(reference))!, PrivateStorageTestRoot.DirectoryMode | UnixFileMode.GroupExecute, PrivateStorageTestRoot.DirectoryMode),
            (root.FinalPath(reference), PrivateStorageTestRoot.AttemptMode, PrivateStorageTestRoot.FinalMode)
        })
        {
            File.SetUnixFileMode(path, unsafeMode);
            try { await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference)); }
            finally { File.SetUnixFileMode(path, safeMode); }
        }
    }

    [Fact]
    public void SymlinkAncestorAndRootAndMarkerAreRefused()
    {
        using var root = new PrivateStorageTestRoot();
        var parent = root.NewDirectory("actual-parent");
        var alias = root.NewPath("linked-parent");
        Directory.CreateSymbolicLink(alias, parent);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => FileSystemPrivateObjectStore.Initialize(new(Path.Combine(alias, "store"), root.ApplicationRoot)));
        using (root.Initialize()) { }
        var retained = root.NewPath("retained-store");
        Directory.Move(root.RootPath, retained);
        Directory.CreateSymbolicLink(root.RootPath, retained);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
        Directory.Delete(root.RootPath);
        Directory.Move(retained, root.RootPath);
        var realMarker = root.NewPath("retained-marker");
        File.Move(root.MarkerPath, realMarker);
        File.CreateSymbolicLink(root.MarkerPath, realMarker);
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
        Assert.Equal(PrivateStorageTestRoot.FinalMode, File.GetUnixFileMode(realMarker));
    }

    [Fact]
    public async Task SymlinkNamespaceAndFinalObjectAreRefusedWithoutTouchingTheirTargets()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = new byte[] { 31, 32 };
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var native = Path.Combine(root.RootPath, "objects", "native");
        var retained = Path.Combine(root.RootPath, "objects", "retained-native");
        Directory.Move(native, retained);
        Directory.CreateSymbolicLink(native, retained);
        try { await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference)); }
        finally { Directory.Delete(native); Directory.Move(retained, native); }
        var target = root.NewPath("retained-object");
        File.Move(root.FinalPath(reference), target);
        File.CreateSymbolicLink(root.FinalPath(reference), target);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes)));
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task MarkerAndFinalHardLinksAreRefusedAndFifoDoesNotBlock()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        PrivateStorageLinux.HardLink(root.MarkerPath, root.NewPath("marker-alias"));
        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
        File.Delete(root.NewPath("marker-alias"));
        var bytes = new byte[] { 1 };
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        var path = root.FinalPath(reference);
        var alias = root.NewPath("object-alias");
        PrivateStorageLinux.HardLink(path, alias);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
        File.Delete(alias);
        File.Delete(path);
        PrivateStorageLinux.Fifo(path);
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference).WaitAsync(TimeSpan.FromSeconds(5)));
        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0x1000, PrivateStorageLinux.Metadata(path).Mode & 0xf000);
    }

    [Fact]
    public async Task RealOwnershipIsInspectedAndWrongOwnershipIsRefusedWhenQualificationCanChangeUid()
    {
        using var root = new PrivateStorageTestRoot();
        using var store = root.Initialize();
        var bytes = new byte[] { 41 };
        var reference = PrivateStorageData.Reference(bytes);
        await store.PublishCreateOnlyAsync(reference.Id.Scope, reference, new MemoryStream(bytes));
        Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(root.RootPath).Uid);
        Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(root.MarkerPath).Uid);
        Assert.Equal(PrivateStorageLinux.EffectiveUid, PrivateStorageLinux.Metadata(root.FinalPath(reference)).Uid);
        // The independent root-run qualification also executes the alternate-UID read proof.
        // An ordinary developer identity cannot chown; this case still executes its real checks.
        if (PrivateStorageLinux.EffectiveUid == 0)
        {
            foreach (var path in new[] { root.BasePath, root.RootPath, root.MarkerPath, root.FinalPath(reference) })
            {
                PrivateStorageLinux.SetOwner(path, 65534);
                try
                {
                    if (path == root.FinalPath(reference))
                        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.UnsafeStorage, () => store.ReadVerifiedAsync(reference.Id.Scope, reference));
                    else
                        PrivateStorageData.Error(PrivateObjectStorageError.UnsafeStorage, () => root.Open());
                }
                finally { PrivateStorageLinux.SetOwner(path, 0); }
            }
        }
    }

    [Fact]
    public void NoncanonicalAliasesAndApplicationRepositoryAndGitRootsAreRejected()
    {
        using var root = new PrivateStorageTestRoot();
        foreach (var path in new[] { "relative-root", "/", root.RootPath + "/", root.BasePath + "//store", root.BasePath + "/./store", root.BasePath + "/nested/../store" })
            Assert.ThrowsAny<ArgumentException>(() => new FileSystemStorageOptions(path, root.ApplicationRoot));
        Assert.ThrowsAny<ArgumentException>(() => FileSystemPrivateObjectStore.Initialize(new(Path.Combine(root.ApplicationRoot, "private"), root.ApplicationRoot)));
        var repository = root.NewDirectory("repository");
        Assert.ThrowsAny<ArgumentException>(() => FileSystemPrivateObjectStore.Initialize(new(Path.Combine(repository, "private"), root.ApplicationRoot, repository)));
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Assert.ThrowsAny<ArgumentException>(() => FileSystemPrivateObjectStore.Initialize(new(Path.Combine(repository, "private"), root.ApplicationRoot)));
        Assert.False(Directory.Exists(Path.Combine(repository, "private")));
    }

    [Fact]
    public async Task BoundedConcurrencyQueuedCancellationAndAsyncDisposalPreserveActiveOperations()
    {
        using var root = new PrivateStorageTestRoot();
        var store = FileSystemPrivateObjectStore.Initialize(new(root.RootPath, root.ApplicationRoot, maximumConcurrency: 1));
        var bytes = PrivateStorageData.Pattern(119);
        var first = PrivateStorageData.Reference(bytes);
        var second = PrivateStorageData.Reference(bytes, scope: first.Id.Scope);
        var third = PrivateStorageData.Reference(bytes, scope: first.Id.Scope);
        using var inputA = new PrivateStorageGatedInput(bytes);
        using var inputB = new PrivateStorageGatedInput(bytes);
        using var inputC = new PrivateStorageInput(bytes);
        using var cancellation = new CancellationTokenSource();
        var publishA = store.PublishCreateOnlyAsync(first.Id.Scope, first, inputA);
        Task<PrivateObjectPublishResult>? publishB = null;
        try
        {
            await inputA.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            publishB = store.PublishCreateOnlyAsync(second.Id.Scope, second, inputB);
            var canceled = store.PublishCreateOnlyAsync(third.Id.Scope, third, inputC, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            Assert.False(inputB.Entered.Task.IsCompleted);
            Assert.Equal(0, inputB.ReadCalls);
            Assert.Equal(0, inputC.ReadCalls);
            Assert.Single(root.AttemptFiles());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(0, inputC.ReadCalls);
            Assert.Single(root.AttemptFiles());
            Assert.False(File.Exists(root.FinalPath(third)));
            inputA.Release();
            Assert.Equal(PrivateObjectPublishResult.Created, await publishA.WaitAsync(TimeSpan.FromSeconds(5)));
            await inputB.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispose = store.DisposeAsync().AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            Assert.False(dispose.IsCompleted);
            inputB.Release();
            Assert.Equal(PrivateObjectPublishResult.Created, await publishB.WaitAsync(TimeSpan.FromSeconds(5)));
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadVerifiedAsync(first.Id.Scope, first));
            using var afterDispose = new PrivateStorageInput(bytes);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PublishCreateOnlyAsync(third.Id.Scope, third, afterDispose));
            Assert.Equal(0, afterDispose.ReadCalls);
        }
        finally
        {
            inputA.Release();
            inputB.Release();
            await publishA.WaitAsync(TimeSpan.FromSeconds(5));
            if (publishB is not null)
                await publishB.WaitAsync(TimeSpan.FromSeconds(5));
            await store.DisposeAsync();
        }
        using var reopened = root.Open();
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(first.Id.Scope, first));
        Assert.Equal(bytes, await reopened.ReadVerifiedAsync(second.Id.Scope, second));
        Assert.Empty(root.AttemptFiles());
    }
}
