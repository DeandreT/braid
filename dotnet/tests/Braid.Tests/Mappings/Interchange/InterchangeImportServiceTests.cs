using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class InterchangeImportServiceTests
{
    [Fact]
    public async Task PrivateCodecRetainsExactSnapshotAndRejectsNonCanonicalOrTrailingData()
    {
        var fixture = new Ports();
        await fixture.Review();
        var bytes = ImportReviewCodec.Encode(fixture.Stored!);
        var decoded = ImportReviewCodec.Decode(bytes);
        Assert.Equal(fixture.Stored!.Digest, decoded.Digest);
        Assert.Equal(fixture.Stored.Package.Digest, decoded.Package.Digest);
        Assert.Throws<ArgumentException>(() => ImportReviewCodec.Decode([.. bytes, (byte)' ']));
    }

    [Fact]
    public async Task ReviewDoesNotPublishAndConfirmationReusesOnlyTheFrozenServerConversion()
    {
        var fixture = new Ports();
        var summary = await fixture.Review();
        Assert.Equal(1, fixture.Conversions);
        Assert.Equal(0, fixture.Publications);
        Assert.Single(summary.Report.Warnings);
        Assert.Equal(ImportConfirmationOutcome.WarningsNotAcknowledged,
            (await fixture.Confirm(summary, acknowledge: false)).Outcome);
        var confirmed = await fixture.Confirm(summary);
        Assert.Equal(ImportConfirmationOutcome.Submitted, confirmed.Outcome);
        Assert.Equal(1, fixture.Publications);
        Assert.Equal(1, fixture.Conversions);
        Assert.Equal(summary.PackageDigest, fixture.Published!.Provenance.Package.Digest);
        Assert.Equal(summary.NativeDigest, fixture.Published.Provenance.NativeDigest);
        Assert.Equal(summary.Digest, fixture.Published.Provenance.ReviewDigest);
        Assert.Equal(summary.Profile, fixture.Published.Provenance.Engine);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("digest")]
    [InlineData("key")]
    [InlineData("expired")]
    [InlineData("revoked")]
    public async Task ChangedAuthorityOrConfirmationCannotPublish(string change)
    {
        var fixture = new Ports();
        var summary = await fixture.Review();
        var actor = fixture.Actor;
        var confirmation = new ImportConfirmation(summary.Id, summary.Digest, summary.Id, true);
        switch (change)
        {
            case "actor": actor = new(new PrincipalId(Guid.NewGuid())); break;
            case "digest": confirmation = confirmation with { ReviewDigest = Sha256Digest.FromBytes("wrong"u8) }; break;
            case "key": confirmation = confirmation with { IdempotencyKey = Guid.NewGuid() }; break;
            case "expired": fixture.Now += TimeSpan.FromHours(2); break;
            case "revoked": fixture.Authorized = false; break;
        }
        var result = await fixture.Service.ConfirmAsync(actor, fixture.Project, confirmation);
        Assert.NotEqual(ImportConfirmationOutcome.Submitted, result.Outcome);
        Assert.Equal(0, fixture.Publications);
    }

    [Fact]
    public async Task AppendCarriesReviewedHeadToFreshIngestionAndDoesNotAcceptAnyHead()
    {
        var fixture = new Ports();
        var target = new MappingScope(fixture.Project.OrganizationId, fixture.Project.ProjectId, Guid.NewGuid());
        var head = new EqualHead(Guid.NewGuid());
        var summary = await fixture.Service.ReviewAsync(fixture.Actor,
            ImportDestination.Append(target, fixture.Profile.BackendId, head), fixture.Package);
        Assert.NotNull(summary);
        await fixture.Confirm(summary);
        Assert.Equal(target, fixture.AppendedTarget);
        Assert.Equal(head, fixture.AppendedHead);
    }

    [Fact]
    public async Task RevocationDuringConversionDoesNotRetainTheReview()
    {
        var fixture = new Ports { RevokeDuringConversion = true };
        Assert.Null(await fixture.Service.ReviewAsync(fixture.Actor, fixture.Destination, fixture.Package));
        Assert.Null(fixture.Stored);
    }

    [Fact]
    public async Task BackendIdentityOrPackageMismatchFailsClosed()
    {
        var fixture = new Ports { WrongPackage = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Review());
        Assert.Null(fixture.Stored);
    }

    private sealed class Ports : TimeProvider, IProjectReader, IIngestionWorkflowReader,
        IInterchangeBackendRegistry, IInterchangeBackend, IImportReviewStore, IReviewedRevisionIngestionService
    {
        internal readonly ActorContext Actor = new(new PrincipalId(Guid.NewGuid()));
        internal readonly ProjectScope Project = new(Guid.NewGuid(), Guid.NewGuid());
        public IngestionBackendProfile Profile { get; } = new("fake", "revision", "build", "adapter");
        internal readonly InterchangePackage Package = new("design.mfd", [new("design.mfd", "<self-authored/>"u8)]);
        internal bool Authorized = true, RevokeDuringConversion, WrongPackage;
        internal int Conversions, Publications;
        internal ImportReview? Stored;
        internal ReviewedImportRevision? Published;
        internal MappingScope? AppendedTarget;
        internal HeadCondition? AppendedHead;
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        internal InterchangeImportService Service => new(this, this, this, this, this, this);
        internal ImportDestination Destination => ImportDestination.Create(Project, "fake", new("Mapping", ""));
        internal async Task<ImportReviewSummary> Review() => (await Service.ReviewAsync(Actor, Destination, Package))!;
        internal Task<ImportConfirmationResult> Confirm(ImportReviewSummary summary, bool acknowledge = true) =>
            Service.ConfirmAsync(Actor, Project, new(summary.Id, summary.Digest, summary.Id, acknowledge));
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Authorized && actor == Actor && scope == Project ? new ProjectAccess(Project, Actor.PrincipalId, ProjectRole.Publisher) : null);
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit,
            Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target, CancellationToken cancellationToken = default) =>
            Task.FromResult<IngestionAppendTarget?>(new(target, "fake"));
        public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project, IngestionOperationKind kind,
            MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult<IngestionReceipt?>(null);
        public IInterchangeBackend? GetCurrent(string backendId) => this;
        public Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken)
        {
            Conversions++;
            if (RevokeDuringConversion) Authorized = false;
            return Task.FromResult(new InterchangeImportOutput(Profile, WrongPackage ? Sha256Digest.FromBytes("wrong"u8) : package.Digest,
                "native-owned-by-server"u8, new("One mapping", [new("unsupported", "Review this construct")])));
        }
        public Task<InterchangeExportOutput> ExportAsync(NativeArtifact native, string designPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SaveAsync(ActorContext actor, ImportReview review, CancellationToken cancellationToken)
        {
            Stored = review;
            return Task.CompletedTask;
        }
        public Task<ImportReview?> FindAsync(ActorContext actor, ProjectScope project, Guid reviewId, CancellationToken cancellationToken) =>
            Task.FromResult(Stored?.Actor == actor.PrincipalId && Stored.Destination.Project == project && Stored.Id == reviewId ? Stored : null);
        public Task<RevisionIngestionResult> CreateReviewedAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
            CreateRevisionRequest request, ReviewedImportRevision reviewed, CancellationToken cancellationToken)
        {
            Publications++;
            Published = reviewed;
            return Task.FromResult(RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.ClaimLost));
        }
        public Task<RevisionIngestionResult> AppendReviewedAsync(ActorContext actor, MappingScope target, Guid idempotencyKey,
            AppendRevisionRequest request, ReviewedImportRevision reviewed, CancellationToken cancellationToken)
        {
            AppendedTarget = target;
            AppendedHead = request.HeadCondition;
            return CreateReviewedAsync(actor, Project, idempotencyKey, new(new("unused", ""), "fake"), reviewed, cancellationToken);
        }
    }
}
