using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.Blob;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Offline contracts through the real Blob adapter; the SDK seam cannot make a network request.</summary>
public sealed class SpecObjectStorageTests
{
    private static readonly SpecBundleScope Scope = new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
    private static SpecObjectReference Reference(byte[] bytes, SpecObjectKind kind = SpecObjectKind.Original, SpecBundleScope? scope = null) =>
        new(new(new(scope ?? Scope, Guid.NewGuid(), 1), kind, Guid.NewGuid()), Sha256Digest.FromBytes(bytes), bytes.Length);

    [Theory]
    [InlineData(SpecObjectKind.Original)]
    [InlineData(SpecObjectKind.Extraction)]
    public async Task Create_only_read_replay_and_exact_retirement(SpecObjectKind kind)
    {
        var fixture = await Fixture.Create();
        byte[] bytes = [1, 2, 3];
        var reference = Reference(bytes, kind);
        var claim = fixture.Bind(reference);
        using var input = new MemoryStream(bytes);
        Assert.Equal(SpecPublishResult.Created, await fixture.Store.PublishAsync(Scope, claim, input));
        Assert.True(input.CanRead);
        Assert.Equal(bytes, await fixture.Store.ReadAsync(Scope, reference));
        Assert.Equal(SpecPublishResult.AlreadyPresent, await fixture.Store.PublishAsync(Scope, claim, new MemoryStream(bytes)));
        var retirement = fixture.Registry.IssueRetirement(reference, fixture.Store.StoreId);
        Assert.Equal(SpecRetireResult.Retired, await fixture.Store.RetireAsync(Scope, retirement));
        Assert.Equal(SpecRetireResult.Missing, await fixture.Store.RetireAsync(Scope, retirement));
        var fence = fixture.Cloud.Objects[SpecObjectStore.ObjectName(reference)];
        Assert.Empty(fence.Bytes);
        Assert.Equal("retired", fence.Metadata["state"]);
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.ReadAsync(Scope, reference));
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Invalid_source_length_or_digest_never_publishes_and_overlong_stream_is_bounded()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1, 2, 3]);
        var claim = fixture.Bind(reference);
        foreach (var bytes in new byte[][] { [1], [1, 2, 4], new byte[100] })
        {
            using var source = new MemoryStream(bytes);
            await Error(SpecStorageError.Integrity, () => fixture.Store.PublishAsync(Scope, claim, source));
            Assert.True(source.Position <= 4);
            Assert.True(source.CanRead);
        }
        Assert.False(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
        Assert.Equal(0, fixture.Registry.FinalAdmissions);
    }

    [Fact]
    public async Task Existing_corrupt_content_is_never_replaced_or_retired()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1, 2, 3]);
        var claim = fixture.Bind(reference);
        await fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1, 2, 3]));
        fixture.Cloud.Put(SpecObjectStore.ObjectName(reference), [4, 5, 6]);
        await Error(SpecStorageError.Integrity, () => fixture.Store.ReadAsync(Scope, reference));
        await Error(SpecStorageError.Conflict, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1, 2, 3])));
        var retirement = fixture.Registry.IssueRetirement(reference, fixture.Store.StoreId);
        await Error(SpecStorageError.Integrity, () => fixture.Store.RetireAsync(Scope, retirement));
        Assert.Equal(new byte[] { 4, 5, 6 }, fixture.Cloud.Objects[SpecObjectStore.ObjectName(reference)].Bytes);
    }

    [Fact]
    public async Task Replacement_between_verified_read_and_retirement_fails_etag_condition()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        await fixture.Store.PublishAsync(Scope, fixture.Bind(reference), new MemoryStream([1]));
        var retirement = fixture.Registry.IssueRetirement(reference, fixture.Store.StoreId);
        fixture.Cloud.BeforeReplace = name => fixture.Cloud.Put(name, [1]);
        await Error(SpecStorageError.Conflict, () => fixture.Store.RetireAsync(Scope, retirement));
        Assert.True(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
    }

    [Fact]
    public async Task Tombstone_after_capture_but_before_final_admission_prevents_publication()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        fixture.Registry.BeforeFinal = () => fixture.Registry.Tombstoned = true;
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1])));
        Assert.False(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
    }

    [Fact]
    public async Task Lost_publication_acknowledgement_replays_same_claim_against_verified_object()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        fixture.Registry.LoseAcknowledgement = true;
        await Error(SpecStorageError.Io, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1])));
        Assert.True(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
        fixture.Registry.LoseAcknowledgement = false;
        Assert.Equal(SpecPublishResult.AlreadyPresent, await fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1])));
        Assert.Equal(new byte[] { 1 }, await fixture.Store.ReadAsync(Scope, reference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_retirement_fence_blocks_late_publish_even_with_stale_registry_admission(bool empty)
    {
        var fixture = await Fixture.Create();
        byte[] bytes = empty ? [] : [1, 2];
        var reference = Reference(bytes);
        var claim = fixture.Bind(reference);
        // Retire before a delayed publication has created any physical bytes.
        var retirement = fixture.Registry.IssueRetirement(reference, fixture.Store.StoreId);
        Assert.Equal(SpecRetireResult.Missing, await fixture.Store.RetireAsync(Scope, retirement));
        fixture.Registry.Tombstoned = false; // Deliberately simulate a stale worker/registry observation.
        await Error(SpecStorageError.Conflict, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream(bytes)));
        var fence = fixture.Cloud.Objects[SpecObjectStore.ObjectName(reference)];
        Assert.Empty(fence.Bytes);
        Assert.Equal("retired", fence.Metadata["state"]);
    }

    [Fact]
    public async Task Registry_fence_holds_through_physical_publication_and_acknowledgement()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cloud.BeforeUpload = async () => { entered.SetResult(); await release.Task; };
        var publishing = fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tombstoning = fixture.Registry.IssueRetirementAsync(reference, fixture.Store.StoreId);
        Assert.False(tombstoning.IsCompleted);
        release.SetResult();
        Assert.Equal(SpecPublishResult.Created, await publishing);
        var retirement = await tombstoning;
        fixture.Cloud.BeforeUpload = null;
        Assert.Equal(SpecRetireResult.Retired, await fixture.Store.RetireAsync(Scope, retirement));
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.ReadAsync(Scope, reference));
    }

    [Fact]
    public async Task Cancellation_and_source_io_failure_never_publish_partial_content()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1]), cancelled.Token));
        using var failing = new FailingStream();
        var error = await Assert.ThrowsAsync<SpecStorageException>(() => fixture.Store.PublishAsync(Scope, claim, failing));
        Assert.Equal(SpecStorageError.Io, error.Error);
        Assert.DoesNotContain("private path", error.ToString());
        Assert.True(failing.CanRead);
        Assert.False(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
    }

    [Fact]
    public async Task Claims_and_scope_are_checked_before_stream_consumption_or_cloud_access()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        using var source = new MemoryStream([1]);
        var calls = fixture.Cloud.Calls;
        await Error(SpecStorageError.ScopeMismatch, () => fixture.Store.PublishAsync(new(Scope.Project, Guid.NewGuid()), claim, source));
        await Error(SpecStorageError.ScopeMismatch, () => fixture.Store.PublishAsync(Scope, new(Guid.NewGuid(), claim.AttemptId, reference), source));
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.PublishAsync(Scope, new(claim.StoreId, Guid.NewGuid(), reference), source));
        var altered = new SpecObjectReference(reference.Id, Sha256Digest.FromBytes([2]), 1);
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.PublishAsync(Scope, new(claim.StoreId, claim.AttemptId, altered), source));
        await Error(SpecStorageError.NotAdmitted, () => fixture.Store.RetireAsync(Scope, new(claim.StoreId, Guid.NewGuid(), reference)));
        Assert.Equal(0, source.Position);
        Assert.Equal(calls, fixture.Cloud.Calls);
    }

    [Fact]
    public async Task Unavailable_registry_and_filesystem_never_touch_cloud_stream_or_filesystem()
    {
        var cloud = new FakeContainer();
        await Error(SpecStorageError.Unavailable, () => BlobSpecObjectStore.OpenAsync(cloud, Guid.NewGuid(), null, true));
        Assert.Equal(0, cloud.Calls);
        var id = Guid.NewGuid();
        var reference = Reference([1]);
        var registry = new Registry();
        var store = new FileSystemSpecObjectStore(new("/not-created/spec-storage", "/application", "/repository"), id, registry);
        using var source = new MemoryStream([1]);
        await Error(SpecStorageError.Unavailable, () => store.PublishAsync(Scope, new(id, Guid.NewGuid(), reference), source));
        await Error(SpecStorageError.Unavailable, () => store.ReadAsync(Scope, reference));
        await Error(SpecStorageError.Unavailable, () => store.RetireAsync(Scope, new(id, Guid.NewGuid(), reference)));
        Assert.Equal(0, source.Position);
        Assert.Equal(0, registry.FinalAdmissions);
    }

    [Fact]
    public async Task Container_privacy_marker_identity_and_marker_incarnation_are_rechecked()
    {
        var fixture = await Fixture.Create();
        var reference = Reference([1]);
        var claim = fixture.Bind(reference);
        fixture.Cloud.PublicAccess = PublicAccessType.Blob;
        await Error(SpecStorageError.UnsafeStorage, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1])));
        fixture.Cloud.PublicAccess = PublicAccessType.None;
        await Error(SpecStorageError.UnsafeStorage, () => BlobSpecObjectStore.OpenAsync(fixture.Cloud, Guid.NewGuid(), fixture.Registry, false));
        var originalMarker = fixture.Cloud.Objects[BlobSpecObjectStore.MarkerName].Bytes;
        fixture.Cloud.Put(BlobSpecObjectStore.MarkerName, originalMarker);
        await Error(SpecStorageError.UnsafeStorage, () => fixture.Store.PublishAsync(Scope, claim, new MemoryStream([1])));
        Assert.False(fixture.Cloud.Objects.ContainsKey(SpecObjectStore.ObjectName(reference)));
    }

    [Fact]
    public void Names_are_disjoint_and_do_not_accept_arbitrary_path_components()
    {
        var reference = Reference([1]);
        var source = reference.Id.Source;
        var names = new[]
        {
            SpecObjectStore.ObjectName(reference),
            SpecObjectStore.ObjectName(new(new(source, SpecObjectKind.Extraction, reference.Id.ObjectId), reference.Digest, 1)),
            SpecObjectStore.ObjectName(new(new(new(source.Bundle, source.SourceId, 2), reference.Id.Kind, reference.Id.ObjectId), reference.Digest, 1)),
            SpecObjectStore.ObjectName(new(new(new(new(Scope.Project, Guid.NewGuid()), source.SourceId, 1), reference.Id.Kind, reference.Id.ObjectId), reference.Digest, 1))
        };
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, name => { Assert.StartsWith("spec-v1-", name); Assert.Matches("^[a-z0-9-]+$", name); Assert.True(name.Length < 255); });
        Assert.Throws<ArgumentException>(() => new FileSystemStorageOptions("/private/../public", "/app"));
        Assert.Throws<ArgumentException>(() => new FileSystemStorageOptions("/private\\escape", "/app"));
        Assert.Throws<ArgumentException>(() => new FileSystemStorageOptions("/app/private", "/app"));
    }

    private static async Task Error(SpecStorageError expected, Func<Task> action) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<SpecStorageException>(action)).Error);

    private sealed record Fixture(BlobSpecObjectStore Store, FakeContainer Cloud, Registry Registry)
    {
        internal static async Task<Fixture> Create()
        {
            var registry = new Registry();
            var cloud = new FakeContainer();
            return new(await BlobSpecObjectStore.OpenAsync(cloud, Guid.NewGuid(), registry, true), cloud, registry);
        }
        internal SpecPublicationClaim Bind(SpecObjectReference reference)
        {
            var claim = new SpecPublicationClaim(Store.StoreId, Guid.NewGuid(), reference);
            Registry.Claims.Add(claim);
            return claim;
        }
    }

    // Deliberately test-only. Production needs durable claims and a database-backed fence, not this semaphore.
    private sealed class Registry : ISpecObjectRegistry
    {
        internal HashSet<SpecPublicationClaim> Claims { get; } = [];
        private readonly HashSet<SpecObjectReference> retained = [];
        private readonly HashSet<SpecRetirementClaim> retirements = [];
        private readonly SemaphoreSlim gate = new(1, 1);
        internal bool Tombstoned;
        internal bool LoseAcknowledgement;
        internal int FinalAdmissions;
        internal Action? BeforeFinal;
        internal SpecRetirementClaim IssueRetirement(SpecObjectReference reference, Guid store)
        {
            Tombstoned = true;
            var claim = new SpecRetirementClaim(store, Guid.NewGuid(), reference);
            retirements.Add(claim);
            return claim;
        }
        internal async Task<SpecRetirementClaim> IssueRetirementAsync(SpecObjectReference reference, Guid store)
        {
            await gate.WaitAsync();
            try { return IssueRetirement(reference, store); }
            finally { gate.Release(); }
        }
        public Task AdmitPublicationAsync(SpecPublicationClaim claim, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Tombstoned || !Claims.Contains(claim)) throw new SpecStorageException(SpecStorageError.NotAdmitted);
            return Task.CompletedTask;
        }
        public async Task<SpecPublishResult> ExecutePublicationAsync(SpecPublicationClaim claim, Func<CancellationToken, Task<SpecPublishResult>> publish, CancellationToken token)
        {
            BeforeFinal?.Invoke();
            await gate.WaitAsync(token);
            try
            {
                await AdmitPublicationAsync(claim, token);
                FinalAdmissions++;
                var result = await publish(token);
                if (LoseAcknowledgement) throw new IOException("Lost acknowledgement");
                retained.Add(claim.Reference);
                return result;
            }
            finally { gate.Release(); }
        }
        public async Task<byte[]> ExecuteReadAsync(Guid storeId, SpecObjectReference reference, Func<CancellationToken, Task<byte[]>> read, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (Tombstoned || !retained.Contains(reference)) throw new SpecStorageException(SpecStorageError.NotAdmitted);
                return await read(token);
            }
            finally { gate.Release(); }
        }
        public async Task<SpecRetireResult> ExecuteRetirementAsync(SpecRetirementClaim claim, Func<CancellationToken, Task<SpecRetireResult>> retire, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                if (!Tombstoned || !retirements.Contains(claim)) throw new SpecStorageException(SpecStorageError.NotAdmitted);
                return await retire(token);
            }
            finally { gate.Release(); }
        }
    }

    private sealed record Stored(byte[] Bytes, ETag Tag, IDictionary<string, string> Metadata);
    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("private path and document information"));
    }
    private sealed class FakeContainer : BlobContainerClient
    {
        internal Dictionary<string, Stored> Objects { get; } = [];
        internal PublicAccessType PublicAccess;
        internal Action<string>? BeforeReplace;
        internal Func<Task>? BeforeUpload;
        internal int Calls;
        private int version;
        internal void Put(string name, byte[] bytes, IDictionary<string, string>? metadata = null) => Objects[name] =
            new(bytes.ToArray(), new ETag($"\"{++version}\""), new Dictionary<string, string>(metadata ??
                (Objects.TryGetValue(name, out var old) ? old.Metadata : new Dictionary<string, string>())));
        public override BlobClient GetBlobClient(string blobName) => new FakeBlob(this, blobName);
        public override Task<Response<BlobContainerProperties>> GetPropertiesAsync(BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContainerProperties(default, new ETag("container"),
                (LeaseStatus?)null, null, null, PublicAccess, false, false, new Dictionary<string, string>()), null!));
        }
    }

    private sealed class FakeBlob(FakeContainer owner, string name) : BlobClient
    {
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            owner.Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.BeforeUpload is { } before) await before();
            if (options.Conditions.IfMatch is { } tag)
            {
                owner.BeforeReplace?.Invoke(name);
                if (!owner.Objects.TryGetValue(name, out var prior) || prior.Tag != tag) throw new RequestFailedException(412, "Changed");
            }
            else
            {
                Assert.Equal(ETag.All, options.Conditions.IfNoneMatch);
                if (owner.Objects.ContainsKey(name)) throw new RequestFailedException(412, "Already exists");
            }
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, cancellationToken);
            owner.Put(name, bytes.ToArray(), options.Metadata);
            return Response.FromValue(BlobsModelFactory.BlobContentInfo(eTag: owner.Objects[name].Tag), null!);
        }
        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions options, CancellationToken cancellationToken = default)
        {
            owner.Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.Objects.TryGetValue(name, out var item)) throw new RequestFailedException(404, "Missing");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(item.Bytes, false),
                BlobsModelFactory.BlobDownloadDetails(contentLength: item.Bytes.Length, eTag: item.Tag, metadata: item.Metadata)), null!));
        }
        public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = default, BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Retirement must preserve the physical fence, never delete the name.");
        }
    }
}
