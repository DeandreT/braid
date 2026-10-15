using System.Runtime.Versioning;
using Weft.Application;
using Weft.Domain;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Authored metadata/worker ports verify composition only, not persisted claim authority or native execution.</summary>
public sealed class PrivatePublicationServiceForwardingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestionPrefersTheExactOwnedCapabilityAndRetainsExplicitLegacyAdapterCompatibility(bool capability)
    {
        var ports = new IngestionPorts();
        var storage = new RecordingStorage();
        IPrivateObjectStore adapter = capability ? storage : new CapabilityDroppingStore(storage);
        using var caller = new CancellationTokenSource();
        var bytes = "synthetic exact native bytes"u8.ToArray();
        using var source = new MemoryStream(bytes);
        var service = ports.Service(adapter);

        var result = await service.CreateAsync(ports.Actor, ports.Project, Guid.NewGuid(),
            new(new("Composition fixture", "No native execution."), ports.Profile.BackendId), source, caller.Token);

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(IngestionFailureCode.StorageIo, result.Failure);
        Assert.Equal(bytes, storage.PublishedBytes);
        Assert.Equal(ports.Receipt!.Facts.NativeObject, storage.Reference);
        Assert.Equal(ports.Receipt.Facts.FutureRevision, storage.Scope);
        Assert.Equal(caller.Token, storage.Token);
        Assert.Equal(capability ? 1 : 0, storage.CapabilityCalls);
        Assert.Equal(capability ? 0 : 1, storage.LegacyCalls);
        Assert.Equal(0, storage.ReadCalls);
        Assert.True(source.CanRead);
        if (capability)
        {
            var owner = Assert.IsType<PrivatePublicationOwner>(storage.Owner);
            var claim = Assert.IsType<IngestionClaim>(ports.Claim);
            Assert.Same(ports.Actor, owner.Actor);
            Assert.Same(claim, owner.IngestionClaim);
            Assert.Null(owner.ReportClaim);
            Assert.Equal(PrivateObjectOwnerKind.NativeIngestion, owner.Kind);
            Assert.Equal(claim.LeaseToken, owner.LeaseToken);
            Assert.Equal(claim.ServerInstanceId, owner.ServerInstanceId);
            Assert.Equal(claim.Generation, owner.Generation);
            Assert.Equal(claim.LeaseExpiresAtUtc, owner.LeaseExpiresAtUtc);
        }
        else Assert.Null(storage.Owner);
        Assert.Same(ports.Claim, ports.CompletedClaim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportPublicationForwardsTheExactClaimAndActorOrUsesTheDeliberateLegacyAdapter(bool capability)
    {
        var ports = new RevisionReportServiceTestPorts();
        var storage = new RecordingStorage(ports);
        IPrivateObjectStore adapter = capability ? storage : new CapabilityDroppingStore(storage);
        var registration = new RevisionReportStorageRegistration(4_194_304, 16_777_216, 16_777_216, () => adapter);
        var service = new RevisionReportService(ports, ports, ports, registration, RevisionReportServiceTestPorts.Instance);

        var result = await service.EnsureAsync(RevisionReportServiceTestPorts.Actor,
            RevisionReportServiceTestPorts.Scope, RevisionReportOperation.Projection);

        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.Equal(RevisionReportState.Available, result.Read!.State);
        Assert.NotNull(result.Read.Report);
        Assert.Equal(capability ? 1 : 0, storage.CapabilityCalls);
        Assert.Equal(capability ? 0 : 1, storage.LegacyCalls);
        Assert.Equal(1, ports.PublishCalls);
        Assert.Equal(1, ports.BackendCalls);
        Assert.Equal(1, ports.FinishCalls);
        var claim = Assert.IsType<RevisionReportClaim>(ports.Claimed);
        Assert.Equal(claim.CandidateObjectId, storage.Reference!.Id);
        Assert.Equal(claim.Key.Scope, storage.Scope);
        if (capability)
        {
            var owner = Assert.IsType<PrivatePublicationOwner>(storage.Owner);
            Assert.Same(RevisionReportServiceTestPorts.Actor, owner.Actor);
            Assert.Same(claim, owner.ReportClaim);
            Assert.Null(owner.IngestionClaim);
            Assert.Equal(PrivateObjectOwnerKind.ReportAttempt, owner.Kind);
            Assert.Equal(claim.LeaseToken, owner.LeaseToken);
            Assert.Equal(claim.ServerInstanceId, owner.ServerInstanceId);
            Assert.Equal(claim.Generation, owner.Generation);
            Assert.Equal(claim.LeaseExpiresAtUtc, owner.LeaseExpiresAtUtc);
        }
        else Assert.Null(storage.Owner);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task HidingTheCapabilityOverActualV2CannotCreateAnUntrackedObjectOrReadTheSource()
    {
        using var root = new PrivateStorageTestRoot();
        var resolutions = 0;
        using var actual = FileSystemV2PrivateObjectStore.Initialize(root.Options, () =>
        {
            resolutions++;
            throw new InvalidOperationException("A capability-dropping decorator cannot resolve registration.");
        });
        IPrivateObjectStore decorator = new CapabilityDroppingStore(actual);
        Assert.False(decorator is IPrivateObjectPublicationStore);
        var before = PrivateStorageV2Data.Files(root.RootPath);
        var bytes = PrivateStorageData.Pattern(97);
        var reference = PrivateStorageData.Reference(bytes);
        using var source = new PrivateStorageInput(bytes);

        await PrivateStorageData.ErrorAsync(PrivateObjectStorageError.Unsupported,
            () => decorator.PublishCreateOnlyAsync(reference.Id.Scope, reference, source));

        Assert.Equal(0, source.ReadCalls);
        Assert.True(source.CanRead);
        Assert.Equal(0, resolutions);
        Assert.False(File.Exists(root.FinalPath(reference)));
        Assert.Empty(DataAttemptFiles(root));
        var after = PrivateStorageV2Data.Files(root.RootPath);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
    }

    [SupportedOSPlatform("linux")]
    private static string[] DataAttemptFiles(PrivateStorageTestRoot root)
    {
        var objects = Path.Combine(root.RootPath, "objects");
        return Directory.Exists(objects) ? Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".blob", StringComparison.Ordinal)).ToArray() : [];
    }

    private sealed class CapabilityDroppingStore(IPrivateObjectStore inner) : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
            inner.PublishCreateOnlyAsync(expectedScope, reference, source, cancellationToken);
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default) => inner.ReadVerifiedAsync(expectedScope, reference, cancellationToken);
    }

    private sealed class RecordingStorage(IPrivateObjectStore? inner = null) : IPrivateObjectPublicationStore
    {
        internal int CapabilityCalls;
        internal int LegacyCalls;
        internal int ReadCalls;
        internal PrivatePublicationOwner? Owner;
        internal PrivateObjectReference? Reference;
        internal RevisionScope? Scope;
        internal byte[]? PublishedBytes;
        internal CancellationToken Token;
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner,
            RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            CapabilityCalls++;
            Owner = owner;
            return Publish(expectedScope, reference, source, cancellationToken);
        }
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            LegacyCalls++;
            return Publish(expectedScope, reference, source, cancellationToken);
        }
        private async Task<PrivateObjectPublishResult> Publish(RevisionScope scope, PrivateObjectReference reference,
            Stream source, CancellationToken token)
        {
            Scope = scope;
            Reference = reference;
            Token = token;
            if (inner is not null) return await inner.PublishCreateOnlyAsync(scope, reference, source, token);
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, token);
            PublishedBytes = copy.ToArray();
            Assert.Equal(reference.Digest, Sha256Digest.FromBytes(PublishedBytes));
            Assert.Equal(reference.ByteCount, PublishedBytes.LongLength);
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Io);
        }
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return inner is not null ? inner.ReadVerifiedAsync(expectedScope, reference, cancellationToken) :
                throw new InvalidOperationException("The synthetic failed publication cannot be read.");
        }
    }

    private sealed class IngestionPorts : IProjectReader, IIngestionWorkflowReader, IIngestionMetadataStore, IIngestionAdmissionBackend
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        internal ActorContext Actor { get; } = new(new(Guid.NewGuid()));
        internal ProjectScope Project { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal IngestionBackendProfile Profile { get; } = new("synthetic.forwarding", "engine", "worker", "adapter");
        internal IngestionReceipt? Receipt;
        internal IngestionClaim? Claim;
        internal IngestionClaim? CompletedClaim;
        internal RevisionIngestionService Service(IPrivateObjectStore store) => new(this, this, this,
            new(store, 4_194_304), new IngestionBackendRegistry([new(Profile, 4_194_304, this)]));

        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectAccess?>(new(scope, actor.PrincipalId, ProjectRole.Publisher));
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit,
            Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project,
            IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IngestionReceipt?>(null);
        public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId,
            CancellationToken cancellationToken = default) => Task.FromResult(Receipt);
        public Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
            IngestionIntent intent, IngestionBackendProfile proposedProfile, IngestionAdmissionPolicy proposedPolicy,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(Actor, actor);
            Assert.Same(Project, project);
            var scope = new RevisionScope(project.OrganizationId, project.ProjectId, Guid.NewGuid(), Guid.NewGuid());
            var reference = new PrivateObjectReference(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact),
                intent.NativeDigest, intent.NativeByteCount);
            var facts = new ReservedIngestionFacts(project, Guid.NewGuid(), actor.PrincipalId, idempotencyKey,
                CanonicalFingerprint.Create(actor.PrincipalId, project, intent), intent, scope, reference,
                proposedProfile, proposedPolicy, Start);
            Receipt = new(facts, IngestionPhase.Reserved, 0, 0, null, null, null, []);
            return Task.FromResult(IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Reserved, Receipt));
        }
        public Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operationId,
            Guid serverInstanceId, CancellationToken cancellationToken = default)
        {
            Claim = new(project, operationId, actor.PrincipalId, serverInstanceId, Guid.NewGuid(), 1, Start.AddSeconds(121));
            Receipt = new(Receipt!.Facts, IngestionPhase.Reserved, 1, 1, null, null, null,
                [new(1, Start.AddSeconds(1), null, null, null)]);
            return Task.FromResult(IngestionStoreResult.ForClaim(Receipt, Claim));
        }
        public Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim,
            IngestionAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            CompletedClaim = claim;
            Assert.Same(Claim, claim);
            Assert.Equal(IngestionFailureCode.StorageIo, completion.Failure);
            Receipt = new(Receipt!.Facts, IngestionPhase.Reserved, 1, 1, null, null, null,
                [new(1, Start.AddSeconds(1), Start.AddSeconds(2), completion, completion.Classify(Receipt.Facts))]);
            return Task.FromResult(IngestionStoreResult.ForReceipt(IngestionStoreOutcome.Applied, Receipt));
        }
        public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim,
            PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A failed synthetic publication cannot execute native validation.");
    }
}
