using System.IO.Compression;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class InterchangeExportServiceTests
{
    [Fact]
    public async Task WarningReviewPrecedesDownloadAndDownloadNeverRerunsTheBackend()
    {
        var ports = new Ports();
        var summary = await ports.Service.ReviewAsync(ports.Actor, ports.Scope);
        Assert.NotNull(summary);
        Assert.Equal(ExportDownloadOutcome.WarningsNotAcknowledged,
            (await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, summary.Digest, false)).Outcome);
        var download = await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, summary.Digest, true);
        Assert.Equal(ExportDownloadOutcome.Available, download.Outcome);
        Assert.Equal("application/zip", download.Download!.ContentType);
        using var archive = new ZipArchive(new MemoryStream(download.Download.CopyBytes()));
        Assert.Equal(["mapping.mfd", "schema.json"], archive.Entries.Select(e => e.FullName));
        var again = await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, summary.Digest, true);
        Assert.Equal(download.Download.Digest, again.Download!.Digest);
        Assert.Equal(1, ports.Calls);
        var restored = ExportReviewCodec.Decode(ExportReviewCodec.Encode(ports.Saved!));
        Assert.Equal(summary.Digest, restored.Digest);
    }

    [Fact]
    public async Task ChangedDigestExpiryOrRevocationCannotDownload()
    {
        var ports = new Ports();
        var summary = (await ports.Service.ReviewAsync(ports.Actor, ports.Scope))!;
        Assert.Equal(ExportDownloadOutcome.ReviewMismatch,
            (await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, Sha256Digest.FromBytes("wrong"u8), true)).Outcome);
        ports.Now += TimeSpan.FromHours(2);
        Assert.Equal(ExportDownloadOutcome.Expired,
            (await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, summary.Digest, true)).Outcome);
        ports.Allowed = false;
        Assert.Equal(ExportDownloadOutcome.NotAuthorizedOrNotFound,
            (await ports.Service.DownloadAsync(ports.Actor, ports.Scope, summary.Id, summary.Digest, true)).Outcome);
    }

    [Fact]
    public async Task RevocationDuringExportDoesNotSaveOrReturnBytes()
    {
        var ports = new Ports { RevokeDuringExport = true };
        Assert.Null(await ports.Service.ReviewAsync(ports.Actor, ports.Scope));
        Assert.Null(ports.Saved);
    }

    private sealed class Ports : TimeProvider, IRevisionReportMetadataReader, IPrivateObjectStore,
        IInterchangeBackendRegistry, IInterchangeBackend, IExportReviewStore
    {
        internal readonly ActorContext Actor = new(new PrincipalId(Guid.NewGuid()));
        internal readonly RevisionScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        internal bool Allowed = true, RevokeDuringExport;
        internal int Calls;
        internal ExportReview? Saved;
        public IngestionBackendProfile Profile { get; } = new("fake", "revision", "build", "adapter");
        internal InterchangeExportService Service => new(this, this, this, this, this);
        public override DateTimeOffset GetUtcNow() => Now;
        public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Allowed && actor == Actor && scope == Scope ? new AuthorizedRevisionReportSource(
                new(scope, new(PrivateObjectId.Restore(scope, Guid.Parse("11111111-1111-1111-1111-111111111111"), PrivateObjectClass.NativeArtifact),
                    Sha256Digest.FromBytes("native"u8), 6), IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, "fake"), Actor.PrincipalId, Now) : null);
        public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope, RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference, CancellationToken cancellationToken = default) => Task.FromResult("native"u8.ToArray());
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IInterchangeBackend? GetCurrent(string backendId) => this;
        public Task<InterchangeImportOutput> ImportAsync(InterchangePackage package, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InterchangeExportOutput> ExportAsync(NativeArtifact native, string designPath, CancellationToken cancellationToken)
        {
            Calls++;
            if (RevokeDuringExport) Allowed = false;
            return Task.FromResult(new InterchangeExportOutput(Profile, native.Digest,
                new("mapping.mfd", [new("mapping.mfd", "<self-authored/>"u8), new("schema.json", "{}"u8)]),
                new("Export summary", [new("lossy", "Review unsupported conversion")])));
        }
        public Task SaveAsync(ActorContext actor, ExportReview review, CancellationToken cancellationToken)
        {
            Saved = review;
            return Task.CompletedTask;
        }
        public Task<ExportReview?> FindExportAsync(ActorContext actor, RevisionScope scope, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved?.Actor == actor.PrincipalId && Saved.Scope == scope && Saved.Id == id ? Saved : null);
    }
}
