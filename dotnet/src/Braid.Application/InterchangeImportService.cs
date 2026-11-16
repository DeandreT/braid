using Braid.Domain;

namespace Braid.Application;

/// <summary>Review is conversion only. Confirmation never converts again or accepts replacement bytes.</summary>
public sealed class InterchangeImportService(IProjectReader projects, IIngestionWorkflowReader mappings,
    IInterchangeBackendRegistry backends, IImportReviewStore reviews, IReviewedRevisionIngestionService ingestion,
    TimeProvider time)
{
    public static readonly TimeSpan ReviewLifetime = TimeSpan.FromHours(1);

    public async Task<ImportReviewSummary?> GetReviewAsync(ActorContext actor, ProjectScope project, Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Review ID cannot be empty.", nameof(id));
        var review = await reviews.FindAsync(actor, project, id, cancellationToken);
        if (review is null) return null;
        if (review.Id != id || review.Actor != actor.PrincipalId || review.Destination.Project != project)
            throw new InvalidOperationException("Review storage returned an inconsistent snapshot.");
        if (!await HasAccessAsync(actor, review.Destination, cancellationToken)) return null;
        return new(review.Id, review.Digest, review.Package.Digest, review.Conversion.NativeDigest,
            review.Conversion.Profile, review.Conversion.Report, review.ExpiresAt, review.Destination);
    }

    public async Task<ImportReviewSummary?> ReviewAsync(ActorContext actor, ImportDestination destination,
        InterchangePackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(package);
        if (!package.DesignPath.EndsWith(".mfd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The design entry must be an .mfd file.", nameof(package));
        if (!await HasAccessAsync(actor, destination, cancellationToken)) return null;
        var backend = backends.GetCurrent(destination.BackendId);
        if (backend is null) throw new InvalidOperationException("The interchange backend is unavailable.");
        var profile = backend.Profile;
        if (profile.BackendId != destination.BackendId)
            throw new InvalidOperationException("The interchange registration is inconsistent.");
        var conversion = await backend.ImportAsync(package, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (conversion is null || conversion.Profile != profile || conversion.PackageDigest != package.Digest)
            throw new InvalidOperationException("The interchange backend returned inconsistent provenance.");
        var review = new ImportReview(Guid.NewGuid(), actor.PrincipalId, destination, package,
            conversion, time.GetUtcNow() + ReviewLifetime);
        // Authority can change while the sandbox is running. Do not retain its output after revocation.
        if (!await HasAccessAsync(actor, destination, cancellationToken)) return null;
        await reviews.SaveAsync(actor, review, cancellationToken);
        return new(review.Id, review.Digest, package.Digest, conversion.NativeDigest, profile,
            conversion.Report, review.ExpiresAt, destination);
    }

    public async Task<ImportConfirmationResult> ConfirmAsync(ActorContext actor, ProjectScope project,
        ImportConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(confirmation.ReviewDigest);
        if (confirmation.ReviewId == Guid.Empty || confirmation.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("Confirmation identifiers cannot be empty.", nameof(confirmation));
        var access = await projects.GetAccessAsync(actor, project, cancellationToken);
        if (access is null || access.Scope != project || access.PrincipalId != actor.PrincipalId ||
            !access.Can(ProjectAction.UploadRevision)) return Hidden();
        var review = await reviews.FindAsync(actor, project, confirmation.ReviewId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (review is null) return Hidden();
        if (review.Id != confirmation.ReviewId || review.Actor != actor.PrincipalId || review.Destination.Project != project)
            throw new InvalidOperationException("Review storage returned an inconsistent snapshot.");
        if (!await HasAccessAsync(actor, review.Destination, cancellationToken)) return Hidden();
        if (review.Digest != confirmation.ReviewDigest)
            return new(ImportConfirmationOutcome.ReviewMismatch);
        if (time.GetUtcNow() >= review.ExpiresAt)
        {
            // Expiry prevents starting new work, not recovery of a transaction already
            // reserved from these exact server-owned bytes (including a lost commit reply).
            var reserved = await mappings.FindOwnReceiptAsync(actor, project,
                review.Destination.Target is null ? IngestionOperationKind.Create : IngestionOperationKind.Append,
                review.Destination.Target, review.Id, cancellationToken);
            if (reserved is null)
                return new(ImportConfirmationOutcome.Expired);
            if (reserved.Facts.OriginalActor != actor.PrincipalId || reserved.Facts.Project != project ||
                reserved.Facts.IdempotencyKey != review.Id ||
                !review.Revision.Provenance.Equals(reserved.Facts.Intent.ImportProvenance))
                throw new InvalidOperationException("The reserved import does not match its review.");
        }
        if (review.Conversion.Report.Warnings.Count != 0 && !confirmation.AcknowledgeWarnings)
            return new(ImportConfirmationOutcome.WarningsNotAcknowledged);
        var destination = review.Destination;
        var filename = review.Package.DesignPath.Split('/')[^1];
        if (System.Text.Encoding.UTF8.GetByteCount(filename) > 255)
            filename = "import.mfd";
        // Bind one review to one durable ingestion key. A caller cannot duplicate the reviewed
        // conversion by changing a client key; that key is only an exact replay assertion.
        if (confirmation.IdempotencyKey != review.Id)
            return new(ImportConfirmationOutcome.ReviewMismatch);
        var result = destination.Target is { } target
            ? await ingestion.AppendReviewedAsync(actor, target, review.Id,
                new AppendRevisionRequest(destination.Head!, filename), review.Revision, cancellationToken)
            : await ingestion.CreateReviewedAsync(actor, project, review.Id,
                new CreateRevisionRequest(destination.Metadata!, destination.BackendId, filename), review.Revision, cancellationToken);
        return new(ImportConfirmationOutcome.Submitted, result);
    }

    private async Task<bool> HasAccessAsync(ActorContext actor, ImportDestination destination, CancellationToken token)
    {
        var access = await projects.GetAccessAsync(actor, destination.Project, token);
        token.ThrowIfCancellationRequested();
        if (access is null || access.Scope != destination.Project || access.PrincipalId != actor.PrincipalId ||
            !access.Can(ProjectAction.UploadRevision)) return false;
        if (destination.Target is not { } target) return true;
        var mapping = await mappings.GetAppendTargetAsync(actor, target, token);
        token.ThrowIfCancellationRequested();
        return mapping is not null && mapping.Scope == target && mapping.BackendId == destination.BackendId;
    }

    private static ImportConfirmationResult Hidden() => new(ImportConfirmationOutcome.NotAuthorizedOrNotFound);
}
