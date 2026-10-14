namespace Weft.Contracts.Catalog;

/// <summary>An actual ingestion result; only a committed outcome links to a committed revision.</summary>
public sealed record CatalogRevisionIngestionResultV1
{
    public CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1 outcome,
        CatalogRevisionIngestionReceiptV1? receipt = null, CatalogRevisionIngestionRejectionV1? rejection = null,
        CatalogRevisionIngestionFailureV1? failure = null, string? committedRevisionUrl = null)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), "The ingestion outcome must be known.");
        if (rejection is { } rejected && !Enum.IsDefined(rejected))
            throw new ArgumentOutOfRangeException(nameof(rejection), "The ingestion rejection must be known.");
        if (failure is { } failed && !Enum.IsDefined(failed))
            throw new ArgumentOutOfRangeException(nameof(failure), "The ingestion failure must be known.");
        var pending = receipt?.Phase is CatalogRevisionIngestionPhaseV1.Reserved or CatalogRevisionIngestionPhaseV1.Staged or
            CatalogRevisionIngestionPhaseV1.Ready;
        var agrees = outcome switch
        {
            CatalogRevisionIngestionOutcomeV1.CommittedValid => receipt is
            { Phase: CatalogRevisionIngestionPhaseV1.Committed, CommittedAdmission: CatalogAdmissionV1.Valid },
            CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid => receipt is
            { Phase: CatalogRevisionIngestionPhaseV1.Committed, CommittedAdmission: CatalogAdmissionV1.DecodedInvalid },
            CatalogRevisionIngestionOutcomeV1.InProgress => pending && receipt!.LatestFailure is null,
            CatalogRevisionIngestionOutcomeV1.NeedsSource => pending && receipt!.LatestFailure == CatalogRevisionIngestionFailureV1.NeedsSource,
            CatalogRevisionIngestionOutcomeV1.RetryableFailure => pending && receipt!.LatestFailure is not null &&
                receipt.LatestFailure is not (CatalogRevisionIngestionFailureV1.NeedsSource or CatalogRevisionIngestionFailureV1.EvidenceLimit),
            CatalogRevisionIngestionOutcomeV1.Rejected => receipt is null
                ? rejection is CatalogRevisionIngestionRejectionV1.EmptyNative or CatalogRevisionIngestionRejectionV1.NativeLimit or
                    CatalogRevisionIngestionRejectionV1.UnregisteredBackend
                : receipt.Phase == CatalogRevisionIngestionPhaseV1.Rejected && rejection is
                    (CatalogRevisionIngestionRejectionV1.NativeDecode or CatalogRevisionIngestionRejectionV1.UnsupportedArtifact or
                    CatalogRevisionIngestionRejectionV1.ResourceLimit),
            CatalogRevisionIngestionOutcomeV1.HeadConflict => receipt?.Phase == CatalogRevisionIngestionPhaseV1.HeadConflict,
            CatalogRevisionIngestionOutcomeV1.AttemptExhausted => receipt?.Phase == CatalogRevisionIngestionPhaseV1.AttemptExhausted,
            CatalogRevisionIngestionOutcomeV1.IdempotencyConflict or CatalogRevisionIngestionOutcomeV1.ClaimLost or
                CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound => receipt is null,
            _ => false
        };
        if (!agrees || failure != receipt?.LatestFailure || (outcome != CatalogRevisionIngestionOutcomeV1.Rejected && rejection is not null) ||
            (failure == CatalogRevisionIngestionFailureV1.EvidenceLimit && rejection != CatalogRevisionIngestionRejectionV1.ResourceLimit))
            throw new ArgumentException("The ingestion result must agree with its actual receipt and admission facts.");

        var committed = outcome is CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid;
        if (committed)
        {
            var scope = receipt!.FutureRevision;
            var expectedUrl = $"/api/v1/organizations/{scope.OrganizationId}/projects/{scope.ProjectId}/mappings/{scope.MappingId}/revisions/{scope.RevisionId}";
            if (!string.Equals(committedRevisionUrl, expectedUrl, StringComparison.Ordinal))
                throw new ArgumentException("A committed revision URL must identify the exact committed scope.", nameof(committedRevisionUrl));
        }
        else if (committedRevisionUrl is not null)
            throw new ArgumentException("Only a committed result links to a committed revision.", nameof(committedRevisionUrl));

        Outcome = outcome;
        Receipt = receipt;
        Rejection = rejection;
        Failure = failure;
        CommittedRevisionUrl = committedRevisionUrl;
    }

    public CatalogRevisionIngestionOutcomeV1 Outcome { get; }
    public CatalogRevisionIngestionReceiptV1? Receipt { get; }
    public CatalogRevisionIngestionRejectionV1? Rejection { get; }
    public CatalogRevisionIngestionFailureV1? Failure { get; }
    public string? CommittedRevisionUrl { get; }
}
