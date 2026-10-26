using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Frozen target selected before conversion. Append requires an explicit observed head.</summary>
public sealed record ImportDestination
{
    private ImportDestination(ProjectScope project, string backendId, IngestionCreateMetadata? metadata,
        MappingScope? target, EqualHead? head)
    {
        Project = project;
        BackendId = RevisionIngestionGuard.RequireBackendId(backendId);
        Metadata = metadata;
        Target = target;
        Head = head;
    }
    public ProjectScope Project { get; }
    public string BackendId { get; }
    public IngestionCreateMetadata? Metadata { get; }
    public MappingScope? Target { get; }
    public EqualHead? Head { get; }
    public static ImportDestination Create(ProjectScope project, string backendId, IngestionCreateMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(metadata);
        return new(project, backendId, metadata, null, null);
    }
    public static ImportDestination Append(MappingScope target, string backendId, EqualHead head)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(head);
        return new(new ProjectScope(target.OrganizationId, target.ProjectId), backendId, null, target, head);
    }
}

/// <summary>Server-owned snapshot. Never deserialize this type from a client request.</summary>
public sealed class ImportReview
{
    public ImportReview(Guid id, PrincipalId actor, ImportDestination destination, InterchangePackage package,
        InterchangeImportOutput conversion, DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Review identifier cannot be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(conversion);
        if (conversion.PackageDigest != package.Digest || conversion.Profile.BackendId != destination.BackendId)
            throw new ArgumentException("Conversion does not match the input package or destination.", nameof(conversion));
        Id = id;
        Actor = actor;
        Destination = destination;
        Package = package;
        Conversion = conversion;
        // PostgreSQL timestamps have microsecond precision; use portable milliseconds
        // before hashing so a persisted/reloaded review retains exactly its identity.
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.ToUnixTimeMilliseconds());
        // A JSON array of fixed typed fields avoids delimiter ambiguity and binds the full destination.
        Digest = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Id,
            Actor = actor.Value,
            destination.Project,
            destination.BackendId,
            destination.Metadata,
            destination.Target,
            destination.Head,
            Package = package.Digest.Value,
            Native = conversion.NativeDigest.Value,
            Report = conversion.Report.Digest.Value,
            conversion.Profile,
            ExpiresAt
        }));
        Revision = new ReviewedImportRevision(conversion.Native,
            new InterchangeImportProvenance(package, conversion.NativeDigest, Digest,
                conversion.Report.Digest, conversion.Profile));
    }
    public Guid Id { get; }
    public PrincipalId Actor { get; }
    public ImportDestination Destination { get; }
    public InterchangePackage Package { get; }
    public InterchangeImportOutput Conversion { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Sha256Digest Digest { get; }
    internal ReviewedImportRevision Revision { get; }
}

/// <summary>Only a validated server-owned review can construct this ingestion input.</summary>
public sealed class ReviewedImportRevision
{
    private readonly byte[] _native;
    internal ReviewedImportRevision(ReadOnlySpan<byte> native, InterchangeImportProvenance provenance)
    {
        _native = native.ToArray();
        Provenance = provenance;
        if (Sha256Digest.FromBytes(_native) != provenance.NativeDigest)
            throw new ArgumentException("Reviewed native bytes do not match provenance.", nameof(native));
    }
    public InterchangeImportProvenance Provenance { get; }
    internal Stream OpenNative() => new MemoryStream(_native, writable: false);
}

/// <summary>
/// Scoped durable storage port. Save is create-only; snapshots never mutate. Find must check the
/// original actor and fresh Publisher authority. Retention must preserve snapshots referenced by
/// ingestion until their original package has been published atomically with the native revision.
/// </summary>
public interface IImportReviewStore
{
    Task SaveAsync(ActorContext actor, ImportReview review, CancellationToken cancellationToken);
    Task<ImportReview?> FindAsync(ActorContext actor, ProjectScope project, Guid reviewId,
        CancellationToken cancellationToken);
}

public interface IReviewedRevisionIngestionService
{
    Task<RevisionIngestionResult> CreateReviewedAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
        CreateRevisionRequest request, ReviewedImportRevision reviewed, CancellationToken cancellationToken);
    Task<RevisionIngestionResult> AppendReviewedAsync(ActorContext actor, MappingScope target, Guid idempotencyKey,
        AppendRevisionRequest request, ReviewedImportRevision reviewed, CancellationToken cancellationToken);
}

public sealed record ImportReviewSummary(Guid Id, Sha256Digest Digest, Sha256Digest PackageDigest,
    Sha256Digest NativeDigest, IngestionBackendProfile Profile, InterchangeReport Report, DateTimeOffset ExpiresAt,
    ImportDestination? Destination = null);

public sealed record ImportConfirmation(Guid ReviewId, Sha256Digest ReviewDigest, Guid IdempotencyKey,
    bool AcknowledgeWarnings);

public enum ImportConfirmationOutcome { Submitted, NotAuthorizedOrNotFound, ReviewMismatch, Expired, WarningsNotAcknowledged }
public sealed record ImportConfirmationResult(ImportConfirmationOutcome Outcome, RevisionIngestionResult? Ingestion = null);
