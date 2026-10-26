using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Domain;

namespace Weft.Application;

public sealed class ExportReview
{
    public ExportReview(Guid id, PrincipalId actor, RevisionScope scope, Sha256Digest nativeDigest,
        string sourceBackendId, InterchangeExportOutput output, DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("An export review ID cannot be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(output.Profile);
        ArgumentNullException.ThrowIfNull(output.Package);
        ArgumentNullException.ThrowIfNull(output.Report);
        if (output.NativeDigest != nativeDigest || output.Profile.BackendId != sourceBackendId ||
            !output.Package.DesignPath.EndsWith(".mfd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Export provenance does not match its revision.", nameof(output));
        Id = id;
        Actor = actor;
        Scope = scope;
        NativeDigest = nativeDigest;
        SourceBackendId = sourceBackendId;
        Output = output;
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.ToUnixTimeMilliseconds());
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Id,
            Actor = Actor.Value,
            Scope,
            Native = NativeDigest.Value,
            SourceBackendId,
            output.Profile,
            Package = output.Package.Digest.Value,
            Report = output.Report.Digest.Value,
            ExpiresAt
        }));
    }
    public Guid Id { get; }
    public PrincipalId Actor { get; }
    public RevisionScope Scope { get; }
    public Sha256Digest NativeDigest { get; }
    public string SourceBackendId { get; }
    public InterchangeExportOutput Output { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Sha256Digest Digest { get; }
}

public interface IExportReviewStore
{
    Task SaveAsync(ActorContext actor, ExportReview review, CancellationToken cancellationToken);
    Task<ExportReview?> FindExportAsync(ActorContext actor, RevisionScope scope, Guid id, CancellationToken cancellationToken);
}

public sealed record ExportReviewSummary(Guid Id, Sha256Digest Digest, Sha256Digest PackageDigest,
    Sha256Digest NativeDigest, IngestionBackendProfile Profile, InterchangeReport Report, DateTimeOffset ExpiresAt);
public enum ExportDownloadOutcome { Available, NotAuthorizedOrNotFound, ReviewMismatch, Expired, WarningsNotAcknowledged }
public sealed record ExportDownloadResult(ExportDownloadOutcome Outcome, ExportDownload? Download = null);

public sealed class ExportDownload
{
    public const int MaximumBytes = InterchangePackage.MaximumTotalBytes + 256 * 1024;
    private readonly byte[] _bytes;
    internal ExportDownload(InterchangePackage package)
    {
        if (package.Files.Count == 1)
        {
            _bytes = package.Files[0].Bytes.ToArray();
            ContentType = "application/xml";
            Filename = "mapping.mfd";
        }
        else
        {
            using var destination = new MemoryStream();
            using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var file in package.Files)
                {
                    var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(file.Bytes);
                }
            if (destination.Length > MaximumBytes) throw new InvalidOperationException("Export download exceeds its bound.");
            _bytes = destination.ToArray();
            ContentType = "application/zip";
            Filename = "mapping.zip";
        }
        Digest = Sha256Digest.FromBytes(_bytes);
    }
    public string ContentType { get; }
    public string Filename { get; }
    public Sha256Digest Digest { get; }
    public byte[] CopyBytes() => (byte[])_bytes.Clone();
}

public sealed class InterchangeExportService(IRevisionReportMetadataReader sources, IPrivateObjectStore objects,
    IInterchangeBackendRegistry backends, IExportReviewStore reviews, TimeProvider time)
{
    public async Task<ExportReviewSummary?> ReviewAsync(ActorContext actor, RevisionScope scope, CancellationToken token = default)
    {
        var source = await SourceAsync(actor, scope, null, token);
        if (source is null) return null;
        var backend = backends.GetCurrent(source.SourceBackendId)
            ?? throw new InvalidOperationException("The interchange backend is unavailable.");
        var profile = backend.Profile;
        if (profile.BackendId != source.SourceBackendId) throw new InvalidOperationException("Inconsistent export backend.");
        if (await SourceAsync(actor, scope, source.NativeObject.Digest, token) is null) return null;
        var bytes = await objects.ReadVerifiedAsync(scope, source.NativeObject, token);
        var native = NativeArtifact.Admit(scope, bytes, (int)IngestionAdmissionPolicy.MaximumNativeByteCount);
        if (native.Digest != source.NativeObject.Digest || native.ByteCount != source.NativeObject.ByteCount)
            throw new InvalidOperationException("Export source failed integrity verification.");
        if (await SourceAsync(actor, scope, native.Digest, token) is null) return null;
        var output = await backend.ExportAsync(native, "mapping.mfd", token);
        token.ThrowIfCancellationRequested();
        if (output is null || output.Profile != profile) throw new InvalidOperationException("Inconsistent export response.");
        var review = new ExportReview(Guid.NewGuid(), actor.PrincipalId, scope, native.Digest,
            source.SourceBackendId, output, time.GetUtcNow() + InterchangeImportService.ReviewLifetime);
        if (await SourceAsync(actor, scope, native.Digest, token) is null) return null;
        await reviews.SaveAsync(actor, review, token);
        return new(review.Id, review.Digest, output.Package.Digest, native.Digest, profile, output.Report, review.ExpiresAt);
    }

    public async Task<ExportDownloadResult> DownloadAsync(ActorContext actor, RevisionScope scope, Guid reviewId,
        Sha256Digest digest, bool acknowledgeWarnings, CancellationToken token = default)
    {
        if (reviewId == Guid.Empty) throw new ArgumentException("An export review ID cannot be empty.", nameof(reviewId));
        ArgumentNullException.ThrowIfNull(digest);
        if (await SourceAsync(actor, scope, null, token) is null) return new(ExportDownloadOutcome.NotAuthorizedOrNotFound);
        var review = await reviews.FindExportAsync(actor, scope, reviewId, token);
        if (review is null) return new(ExportDownloadOutcome.NotAuthorizedOrNotFound);
        if (review.Actor != actor.PrincipalId || review.Scope != scope || review.Id != reviewId)
            throw new InvalidOperationException("Inconsistent export review.");
        if (review.Digest != digest) return new(ExportDownloadOutcome.ReviewMismatch);
        if (time.GetUtcNow() >= review.ExpiresAt) return new(ExportDownloadOutcome.Expired);
        if (review.Output.Report.Warnings.Count != 0 && !acknowledgeWarnings)
            return new(ExportDownloadOutcome.WarningsNotAcknowledged);
        if (await SourceAsync(actor, scope, review.NativeDigest, token) is null) return new(ExportDownloadOutcome.NotAuthorizedOrNotFound);
        var download = new ExportDownload(review.Output.Package);
        if (await SourceAsync(actor, scope, review.NativeDigest, token) is null) return new(ExportDownloadOutcome.NotAuthorizedOrNotFound);
        return new(ExportDownloadOutcome.Available, download);
    }

    private async Task<RevisionReportSource?> SourceAsync(ActorContext actor, RevisionScope scope, Sha256Digest? expected, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        var value = await sources.GetSourceAsync(actor, scope, token);
        token.ThrowIfCancellationRequested();
        if (value is null) return null;
        if (value.Actor != actor.PrincipalId || value.Source.Scope != scope ||
            expected is not null && value.Source.NativeObject.Digest != expected)
            throw new InvalidOperationException("Inconsistent export authority.");
        return value.Source;
    }
}

public static class ExportReviewCodec
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 10 };
    public static byte[] Encode(ExportReview review)
    {
        var value = new Envelope(1, review.Id, review.Actor.Value, review.Scope.OrganizationId, review.Scope.ProjectId,
            review.Scope.MappingId, review.Scope.RevisionId, review.NativeDigest.Value, review.SourceBackendId,
            review.Output.Profile.EngineRevision, review.Output.Profile.BackendBuild, review.Output.Profile.AdapterBuild,
            review.ExpiresAt, review.Output.Package.Encode(), review.Output.Report.Summary,
            review.Output.Report.Warnings.Select(w => new Warning(w.Code, w.Message)).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Export review exceeds its bound.");
        return bytes;
    }
    public static ExportReview Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes) throw new ArgumentException("Export review exceeds its bound.");
        var value = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new ArgumentException("Missing export review.");
        if (value.Version != 1 || value.Package is null || value.Warnings is null ||
            value.Warnings.Length > InterchangeReport.MaximumWarnings || value.Warnings.Any(w => w is null))
            throw new ArgumentException("Invalid export review.");
        var native = Sha256Digest.Parse(value.NativeDigest);
        var output = new InterchangeExportOutput(new(value.BackendId, value.EngineRevision, value.BackendBuild, value.AdapterBuild),
            native, InterchangePackage.Decode(value.Package), new(value.Summary, value.Warnings.Select(w => new InterchangeWarning(w.Code, w.Message))));
        var result = new ExportReview(value.Id, new(value.ActorId), new(value.OrganizationId, value.ProjectId, value.MappingId, value.RevisionId),
            native, value.BackendId, output, value.ExpiresAt);
        if (!bytes.SequenceEqual(Encode(result))) throw new ArgumentException("Export review must be canonical.");
        return result;
    }
    private sealed record Warning(string Code, string Message);
    private sealed record Envelope(int Version, Guid Id, Guid ActorId, Guid OrganizationId, Guid ProjectId,
        Guid MappingId, Guid RevisionId, string NativeDigest, string BackendId, string EngineRevision,
        string BackendBuild, string AdapterBuild, DateTimeOffset ExpiresAt, byte[] Package, string Summary, Warning[] Warnings);
}
