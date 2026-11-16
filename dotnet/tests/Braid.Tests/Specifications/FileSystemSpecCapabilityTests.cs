using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>
/// Non-adoption contracts for the unavailable capability, NOT Linux publication/crash qualification.
/// These tests must be replaced with supported-profile qualification before enabling filesystem storage.
/// </summary>
public sealed class FileSystemSpecCapabilityTests
{
    [Theory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Every_operation_refuses_before_registry_admission_or_source_consumption(SpecObjectKind kind)
    {
        using var root = new EvidenceRoot();
        var registry = new ForbiddenRegistry();
        var storeId = Guid.NewGuid();
        var reference = Reference(kind);
        var store = new FileSystemSpecObjectStore(root.Options, storeId, registry);
        using var input = new ForbiddenStream();

        await Refuses(SpecStorageError.Unavailable, () => store.PublishAsync(reference.Id.Source.Bundle,
            new(storeId, Guid.NewGuid(), reference), input));
        await Refuses(SpecStorageError.Unavailable, () => store.ReadAsync(reference.Id.Source.Bundle, reference));
        await Refuses(SpecStorageError.Unavailable, () => store.RetireAsync(reference.Id.Source.Bundle,
            new(storeId, Guid.NewGuid(), reference)));

        Assert.Equal(0, registry.Calls);
        Assert.Equal(0, input.Accesses);
        Assert.False(input.Disposed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("absence.stage", "partial absence record")]
    [InlineData("reservation", "acknowledged reservation candidate")]
    [InlineData("bound.stage", "partial inode binding candidate")]
    [InlineData("payload.tmp", "partially captured private source")]
    [InlineData("prepared", "prepared candidate without acknowledgement")]
    [InlineData("final", "published candidate with unknown commit status")]
    [InlineData("object.retired", "permanent no-recreation fence candidate")]
    public async Task Retry_and_retirement_preserve_uncertain_recovery_candidates(string name, string content)
    {
        using var root = new EvidenceRoot();
        // Deliberately opaque fixtures, not claims of valid native seals or a simulated process crash.
        var path = System.IO.Path.Combine(root.Path, name);
        await File.WriteAllTextAsync(path, content);
        var before = await File.ReadAllBytesAsync(path);
        var storeId = Guid.NewGuid();
        var registry = new ForbiddenRegistry();
        var reference = Reference(SpecObjectKind.Original);
        var claim = new SpecPublicationClaim(storeId, Guid.NewGuid(), reference);
        var retirement = new SpecRetirementClaim(storeId, Guid.NewGuid(), reference);
        using var input = new ForbiddenStream();

        for (var retry = 0; retry < 2; retry++)
        {
            // Reopening an adapter must not adopt evidence merely because a filename or claim matches.
            var store = new FileSystemSpecObjectStore(root.Options, storeId, registry);
            await Refuses(SpecStorageError.Unavailable, () => store.PublishAsync(reference.Id.Source.Bundle, claim, input));
            await Refuses(SpecStorageError.Unavailable, () => store.RetireAsync(reference.Id.Source.Bundle, retirement));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(root.Path)));
        Assert.Equal(0, registry.Calls);
        Assert.Equal(0, input.Accesses);
    }

    [Fact]
    public async Task Concurrent_adapter_instances_do_not_create_gates_stages_or_fences()
    {
        using var root = new EvidenceRoot();
        var registry = new ForbiddenRegistry();
        var reference = Reference(SpecObjectKind.Original);
        var storeId = Guid.NewGuid();
        var claim = new SpecPublicationClaim(storeId, Guid.NewGuid(), reference);
        var retirement = new SpecRetirementClaim(storeId, Guid.NewGuid(), reference);
        using var input = new ForbiddenStream();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            var store = new FileSystemSpecObjectStore(root.Options, storeId, registry);
            await Refuses(SpecStorageError.Unavailable, () => store.PublishAsync(reference.Id.Source.Bundle, claim, input));
            await Refuses(SpecStorageError.Unavailable, () => store.ReadAsync(reference.Id.Source.Bundle, reference));
            await Refuses(SpecStorageError.Unavailable, () => store.RetireAsync(reference.Id.Source.Bundle, retirement));
        })));
        Assert.Equal(0, registry.Calls);
        Assert.Equal(0, input.Accesses);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Foreign_organization_project_or_bundle_is_rejected_without_opening_storage(int component)
    {
        using var root = new EvidenceRoot();
        var registry = new ForbiddenRegistry();
        var reference = Reference(SpecObjectKind.Original);
        var scope = reference.Id.Source.Bundle;
        var foreign = new SpecBundleScope(new ProjectScope(
            component == 0 ? Guid.NewGuid() : scope.Project.OrganizationId,
            component == 1 ? Guid.NewGuid() : scope.Project.ProjectId),
            component == 2 ? Guid.NewGuid() : scope.BundleId);
        var storeId = Guid.NewGuid();
        var store = new FileSystemSpecObjectStore(root.Options, storeId, registry);
        using var input = new ForbiddenStream();
        await Refuses(SpecStorageError.ScopeMismatch, () => store.PublishAsync(foreign, new(storeId, Guid.NewGuid(), reference), input));
        await Refuses(SpecStorageError.ScopeMismatch, () => store.ReadAsync(foreign, reference));
        await Refuses(SpecStorageError.ScopeMismatch, () => store.RetireAsync(foreign, new(storeId, Guid.NewGuid(), reference)));
        Assert.Equal(0, registry.Calls);
        Assert.Equal(0, input.Accesses);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task Cancellation_does_not_trigger_recovery_or_cleanup()
    {
        using var root = new EvidenceRoot();
        var registry = new ForbiddenRegistry();
        var reference = Reference(SpecObjectKind.Original);
        var storeId = Guid.NewGuid();
        var store = new FileSystemSpecObjectStore(root.Options, storeId, registry);
        using var input = new ForbiddenStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishAsync(reference.Id.Source.Bundle,
            new(storeId, Guid.NewGuid(), reference), input, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(reference.Id.Source.Bundle, reference, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RetireAsync(reference.Id.Source.Bundle,
            new(storeId, Guid.NewGuid(), reference), cancellation.Token));
        Assert.Equal(0, registry.Calls);
        Assert.Equal(0, input.Accesses);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.Path));
    }

    [Theory]
    [InlineData("/private/../public")]
    [InlineData("/private/./objects")]
    [InlineData("/private//objects")]
    [InlineData("/private\\objects")]
    [InlineData("/private/objects/")]
    [InlineData("/")]
    [InlineData("relative/objects")]
    [InlineData("/private/\0objects")]
    public void Noncanonical_path_configuration_is_rejected_without_normalizing_aliases(string path) =>
        Assert.Throws<ArgumentException>(() => new FileSystemStorageOptions(path, "/app", "/repo"));

    [LinuxSpecEvidenceFact]
    public async Task Unavailable_capability_leaves_real_symlink_and_its_target_untouched()
    {
        using var root = new EvidenceRoot();
        using var target = new EvidenceRoot();
        var evidence = System.IO.Path.Combine(target.Path, "private-evidence");
        await File.WriteAllTextAsync(evidence, "must remain unchanged");
        var link = System.IO.Path.Combine(root.Path, "linked-root");
        Directory.CreateSymbolicLink(link, target.Path);
        try
        {
            var registry = new ForbiddenRegistry();
            var storeId = Guid.NewGuid();
            var reference = Reference(SpecObjectKind.Original);
            var store = new FileSystemSpecObjectStore(new(link, "/application", "/repository"), storeId, registry);
            using var input = new ForbiddenStream();
            await Refuses(SpecStorageError.Unavailable, () => store.PublishAsync(reference.Id.Source.Bundle,
                new(storeId, Guid.NewGuid(), reference), input));
            await Refuses(SpecStorageError.Unavailable, () => store.ReadAsync(reference.Id.Source.Bundle, reference));
            await Refuses(SpecStorageError.Unavailable, () => store.RetireAsync(reference.Id.Source.Bundle,
                new(storeId, Guid.NewGuid(), reference)));
            Assert.Equal(target.Path, new DirectoryInfo(link).LinkTarget);
            Assert.Equal("must remain unchanged", await File.ReadAllTextAsync(evidence));
            Assert.Equal(evidence, Assert.Single(Directory.EnumerateFileSystemEntries(target.Path)));
            Assert.Equal(0, registry.Calls);
            Assert.Equal(0, input.Accesses);
        }
        finally { Directory.Delete(link); }
    }

    private static SpecObjectReference Reference(SpecObjectKind kind) => new(
        new(new(new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1), kind, Guid.NewGuid()),
        Sha256Digest.FromBytes([1, 2, 3]), 3);

    private static async Task Refuses(SpecStorageError error, Func<Task> action) =>
        Assert.Equal(error, (await Assert.ThrowsAsync<SpecStorageException>(action)).Error);

    private sealed class EvidenceRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"braid-spec-fs-contract-{Guid.NewGuid():N}");
        internal FileSystemStorageOptions Options { get; }
        internal EvidenceRoot()
        {
            Directory.CreateDirectory(Path);
            var configured = Path.Replace('\\', '/');
            // Canonical Unix spelling is required by the existing options even though this capability is unavailable.
            // The Windows tests exercise refusal, not handle-relative Unix I/O or reparse-point qualification.
            if (OperatingSystem.IsWindows()) configured = configured[2..];
            Options = new(configured, "/application", "/repository");
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ForbiddenRegistry : ISpecObjectRegistry
    {
        private int calls;
        internal int Calls => calls;
        private Exception Forbidden() { Interlocked.Increment(ref calls); return new InvalidOperationException("Unavailable storage reached the registry."); }
        public Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken token) => throw Forbidden();
        public Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim, Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken token) => throw Forbidden();
        public Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference, Func<CancellationToken, Task<byte[]>> read, CancellationToken token) => throw Forbidden();
        public Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim, Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken token) => throw Forbidden();
    }

    private sealed class ForbiddenStream : Stream
    {
        private int accesses;
        internal int Accesses => accesses;
        internal bool Disposed { get; private set; }
        private Exception Forbidden() { Interlocked.Increment(ref accesses); return new InvalidOperationException("Unavailable storage touched the input."); }
        public override bool CanRead => throw Forbidden();
        public override bool CanSeek => throw Forbidden();
        public override bool CanWrite => throw Forbidden();
        public override long Length => throw Forbidden();
        public override long Position { get => throw Forbidden(); set => throw Forbidden(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw Forbidden();
        public override int Read(byte[] buffer, int offset, int count) => throw Forbidden();
        public override void Flush() => throw Forbidden();
        public override long Seek(long offset, SeekOrigin origin) => throw Forbidden();
        public override void SetLength(long value) => throw Forbidden();
        public override void Write(byte[] buffer, int offset, int count) => throw Forbidden();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

public sealed class LinuxSpecEvidenceFactAttribute : FactAttribute
{
    public LinuxSpecEvidenceFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Real Unix symlink preservation requires Linux; Windows refusal tests are not Linux qualification.";
    }
}
