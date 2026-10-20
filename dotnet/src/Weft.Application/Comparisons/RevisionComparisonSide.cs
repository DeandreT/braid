using Weft.Contracts.Operations;
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
        Projection = projection;
        Validation = validation;
    }

    internal RevisionScope Scope { get; }
    internal long Sequence { get; }
    internal VerifiedProjectionReport Projection { get; }
    internal VerifiedValidationReport? Validation { get; }

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
