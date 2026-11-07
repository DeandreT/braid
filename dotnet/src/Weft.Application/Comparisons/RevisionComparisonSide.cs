using Weft.Contracts.Projections;
using Weft.Contracts.Operations;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Application.Comparisons;

/// <summary>Owned verified report facts only. This value is not an authority observation.</summary>
public sealed class RevisionComparisonSide
{
    private RevisionComparisonSide(CatalogRevisionSummary revision, VerifiedProjectionReport projection,
        VerifiedValidationReport? validation)
    {
        Scope = new(revision.Scope.OrganizationId, revision.Scope.ProjectId, revision.Scope.MappingId, revision.Scope.RevisionId);
        Sequence = revision.Sequence;
        Projection = new(projection.Report, projection.Metadata);
        Validation = validation is null ? null : new(validation.Report, validation.Metadata);
        NativeDigest = revision.NativeDigest;
        NativeByteCount = revision.NativeByteCount;
        Format = RevisionReportFormat.FromKey(projection.Metadata.Key);
        Profile = projection.Metadata.Key.Profile;
        Policy = projection.Metadata.Key.Policy;
    }

    internal RevisionScope Scope { get; }
    internal long Sequence { get; }
    internal ProjectionFacts Projection { get; }
    internal ValidationFacts? Validation { get; }
    internal Sha256Digest NativeDigest { get; }
    internal long NativeByteCount { get; }
    internal RevisionReportFormat Format { get; }
    internal RevisionReportBackendProfile Profile { get; }
    internal RevisionReportPolicy Policy { get; }
    internal Guid? DraftId { get; }
    internal long? DraftVersion { get; }
    internal sealed record ProjectionFacts(ProjectionReportV1 Report, RevisionReportResult? Metadata);
    internal sealed record ValidationFacts(ValidationReportV1 Report, RevisionReportResult? Metadata);

    private RevisionComparisonSide(MappingDraftSnapshot draft, MappingEditCheckpoint checkpoint,
        RevisionReportFormat format, RevisionReportPolicy policy, CancellationToken token)
    {
        Scope = checkpoint.Artifact.Scope; Sequence = draft.Base.Sequence;
        Projection = new(RevisionReportSnapshots.CopyProjection(checkpoint.Projection, policy, token), null);
        Validation = new(RevisionReportSnapshots.CopyValidation(checkpoint.Validation, policy, token), null);
        NativeDigest = checkpoint.Artifact.Digest; NativeByteCount = checkpoint.Artifact.ByteCount;
        Format = format; Policy = policy;
        var provenance = checkpoint.Projection.Document!.Provenance;
        Profile = new(provenance.Backend.Id, provenance.Backend.Revision, provenance.Backend.BuildId, provenance.AdapterVersion);
        DraftId = draft.Id; DraftVersion = draft.Version;
    }

    public static RevisionComparisonSide CreateDraft(MappingDraftSnapshot draft, MappingEditCheckpoint checkpoint,
        RevisionReportFormat format, RevisionReportPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (draft is null || checkpoint is null || format is null || policy is null || draft.Checkpoint is not { } summary || summary.Version != draft.Version
            || checkpoint.Artifact.Scope != draft.Base.Revision || checkpoint.Artifact.Digest != summary.NativeDigest
            || checkpoint.Artifact.ByteCount != summary.NativeByteCount
            || checkpoint.Projection.Status != OperationStatusV1.Succeeded || checkpoint.Projection.Document is not { } document
            || checkpoint.Validation.IsValid is null || checkpoint.Validation.Provenance?.Confirmed != true
            || document.Provenance.NativeArtifactDigest.Value != checkpoint.Artifact.Digest.Value
            || checkpoint.Validation.Provenance.NativeArtifactDigest.Value != checkpoint.Artifact.Digest.Value
            || format.Operation != RevisionReportOperation.Projection || format.BackendId != document.Provenance.Backend.Id
            || format.AlgorithmVersion != document.Provenance.ProjectionVersion) throw Invalid();
        return new(draft, checkpoint, format, policy, cancellationToken);
    }

    public static RevisionComparisonSide Create(CatalogRevisionSummary revision, VerifiedProjectionReport projection,
        VerifiedValidationReport? validation = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (revision is null || projection is null) throw Invalid();
        var source = projection.Metadata.Key.Source;
        if (projection.Report.Status != OperationStatusV1.Succeeded || projection.Report.Document is null ||
            source.Scope != revision.Scope || source.NativeObject.Digest != revision.NativeDigest ||
            source.NativeObject.ByteCount != revision.NativeByteCount || source.LogicalDocument != revision.LogicalDocument ||
            source.SourceBackendId != revision.BackendId) throw Invalid();
        if (validation is not null)
        {
            if (validation.Metadata.Key.Source != source ||
                validation.Report.Status is not (OperationStatusV1.Succeeded or OperationStatusV1.InvalidMapping) ||
                validation.Report.IsValid is null ||
                validation.Report.IsValid != (validation.Report.Status == OperationStatusV1.Succeeded) ||
                validation.ProjectionDerivationId is { } linked &&
                (linked != projection.Metadata.DerivationId || validation.Metadata.Key.Profile != projection.Metadata.Key.Profile))
                throw Invalid();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(revision, projection, validation);
    }

    private static RevisionComparisonException Invalid() => new(RevisionComparisonFailure.InvalidInput);
    public override string ToString() => "Owned revision comparison input.";
}
