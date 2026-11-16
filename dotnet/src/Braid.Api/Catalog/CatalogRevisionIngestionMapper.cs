using System.Globalization;
using Microsoft.AspNetCore.Http;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Domain;

namespace Braid.Api.Catalog;

internal static class CatalogRevisionIngestionMapper
{
    internal static CatalogRevisionIngestionReceiptV1 Receipt(RevisionIngestionReceipt receipt) => new(
        receipt.OperationId.ToString("D"), CatalogPublicMapper.ProjectScope(receipt.Project),
        CatalogPublicMapper.MappingScope(receipt.FutureMapping), CatalogPublicMapper.RevisionScope(receipt.FutureRevision),
        Phase(receipt.Phase), receipt.ClaimCount, receipt.ReservedAtUtc, receipt.CompletedAtUtc, receipt.OriginalFilename,
        receipt.CommittedSequence?.ToString(CultureInfo.InvariantCulture), receipt.CommittedAdmission is { } admission
            ? admission switch
            {
                IngestionAdmissionClassification.EligibleValid => CatalogAdmissionV1.Valid,
                IngestionAdmissionClassification.EligibleDecodedInvalid => CatalogAdmissionV1.DecodedInvalid,
                _ => throw new InvalidOperationException("The ingestion receipt has an unknown committed admission.")
            } : null,
        receipt.LatestFailure is { } failure ? Failure(failure) : null);

    internal static CatalogRevisionIngestionResultV1 Result(RevisionIngestionResult result) => new(
        Outcome(result.Outcome), result.Receipt is { } receipt ? Receipt(receipt) : null,
        result.Rejection is { } rejection ? Rejection(rejection) : null,
        result.Failure is { } failure ? Failure(failure) : null,
        result.Outcome is RevisionIngestionOutcome.CommittedValid or RevisionIngestionOutcome.CommittedDecodedInvalid
            ? RevisionUrl(result.Receipt!.FutureRevision) : null);

    internal static int Status(RevisionIngestionResult result) => result.Outcome switch
    {
        RevisionIngestionOutcome.CommittedValid or RevisionIngestionOutcome.CommittedDecodedInvalid => StatusCodes.Status200OK,
        RevisionIngestionOutcome.InProgress => StatusCodes.Status202Accepted,
        RevisionIngestionOutcome.NeedsSource or RevisionIngestionOutcome.IdempotencyConflict or RevisionIngestionOutcome.HeadConflict or
            RevisionIngestionOutcome.AttemptExhausted or RevisionIngestionOutcome.ClaimLost => StatusCodes.Status409Conflict,
        RevisionIngestionOutcome.RetryableFailure => StatusCodes.Status503ServiceUnavailable,
        RevisionIngestionOutcome.NotAuthorizedOrNotFound => StatusCodes.Status404NotFound,
        RevisionIngestionOutcome.Rejected => result.Rejection switch
        {
            RevisionIngestionRejection.EmptyNative => StatusCodes.Status400BadRequest,
            RevisionIngestionRejection.NativeLimit => StatusCodes.Status413PayloadTooLarge,
            RevisionIngestionRejection.UnregisteredBackend or RevisionIngestionRejection.NativeDecode or
                RevisionIngestionRejection.UnsupportedArtifact or RevisionIngestionRejection.ResourceLimit => StatusCodes.Status422UnprocessableEntity,
            _ => throw new InvalidOperationException("The ingestion rejection is unknown.")
        },
        _ => throw new InvalidOperationException("The ingestion result is unknown.")
    };

    internal static string ReceiptUrl(ProjectScope project, Guid operationId) =>
        $"/api/v1/organizations/{project.OrganizationId:D}/projects/{project.ProjectId:D}/revision-ingestions/{operationId:D}";

    private static string RevisionUrl(RevisionScope revision) =>
        $"/api/v1/organizations/{revision.OrganizationId:D}/projects/{revision.ProjectId:D}/mappings/{revision.MappingId:D}/revisions/{revision.RevisionId:D}";

    private static CatalogRevisionIngestionOutcomeV1 Outcome(RevisionIngestionOutcome outcome) => outcome switch
    {
        RevisionIngestionOutcome.CommittedValid => CatalogRevisionIngestionOutcomeV1.CommittedValid,
        RevisionIngestionOutcome.CommittedDecodedInvalid => CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid,
        RevisionIngestionOutcome.InProgress => CatalogRevisionIngestionOutcomeV1.InProgress,
        RevisionIngestionOutcome.NeedsSource => CatalogRevisionIngestionOutcomeV1.NeedsSource,
        RevisionIngestionOutcome.RetryableFailure => CatalogRevisionIngestionOutcomeV1.RetryableFailure,
        RevisionIngestionOutcome.Rejected => CatalogRevisionIngestionOutcomeV1.Rejected,
        RevisionIngestionOutcome.HeadConflict => CatalogRevisionIngestionOutcomeV1.HeadConflict,
        RevisionIngestionOutcome.AttemptExhausted => CatalogRevisionIngestionOutcomeV1.AttemptExhausted,
        RevisionIngestionOutcome.IdempotencyConflict => CatalogRevisionIngestionOutcomeV1.IdempotencyConflict,
        RevisionIngestionOutcome.ClaimLost => CatalogRevisionIngestionOutcomeV1.ClaimLost,
        RevisionIngestionOutcome.NotAuthorizedOrNotFound => CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound,
        _ => throw new InvalidOperationException("The ingestion result is unknown.")
    };

    private static CatalogRevisionIngestionPhaseV1 Phase(IngestionPhase phase) => phase switch
    {
        IngestionPhase.Reserved => CatalogRevisionIngestionPhaseV1.Reserved,
        IngestionPhase.Staged => CatalogRevisionIngestionPhaseV1.Staged,
        IngestionPhase.Ready => CatalogRevisionIngestionPhaseV1.Ready,
        IngestionPhase.Committed => CatalogRevisionIngestionPhaseV1.Committed,
        IngestionPhase.Rejected => CatalogRevisionIngestionPhaseV1.Rejected,
        IngestionPhase.HeadConflict => CatalogRevisionIngestionPhaseV1.HeadConflict,
        IngestionPhase.AttemptExhausted => CatalogRevisionIngestionPhaseV1.AttemptExhausted,
        _ => throw new InvalidOperationException("The ingestion receipt phase is unknown.")
    };

    private static CatalogRevisionIngestionRejectionV1 Rejection(RevisionIngestionRejection rejection) => rejection switch
    {
        RevisionIngestionRejection.EmptyNative => CatalogRevisionIngestionRejectionV1.EmptyNative,
        RevisionIngestionRejection.NativeLimit => CatalogRevisionIngestionRejectionV1.NativeLimit,
        RevisionIngestionRejection.UnregisteredBackend => CatalogRevisionIngestionRejectionV1.UnregisteredBackend,
        RevisionIngestionRejection.NativeDecode => CatalogRevisionIngestionRejectionV1.NativeDecode,
        RevisionIngestionRejection.UnsupportedArtifact => CatalogRevisionIngestionRejectionV1.UnsupportedArtifact,
        RevisionIngestionRejection.ResourceLimit => CatalogRevisionIngestionRejectionV1.ResourceLimit,
        _ => throw new InvalidOperationException("The ingestion rejection is unknown.")
    };

    private static CatalogRevisionIngestionFailureV1 Failure(IngestionFailureCode failure) => failure switch
    {
        IngestionFailureCode.NeedsSource => CatalogRevisionIngestionFailureV1.NeedsSource,
        IngestionFailureCode.StorageIntegrity => CatalogRevisionIngestionFailureV1.StorageIntegrity,
        IngestionFailureCode.StorageIo => CatalogRevisionIngestionFailureV1.StorageIo,
        IngestionFailureCode.BackendUnavailable => CatalogRevisionIngestionFailureV1.BackendUnavailable,
        IngestionFailureCode.BackendFailure => CatalogRevisionIngestionFailureV1.BackendFailure,
        IngestionFailureCode.TimedOut => CatalogRevisionIngestionFailureV1.TimedOut,
        IngestionFailureCode.Canceled => CatalogRevisionIngestionFailureV1.Canceled,
        IngestionFailureCode.IsolationFailure => CatalogRevisionIngestionFailureV1.IsolationFailure,
        IngestionFailureCode.ProtocolFailure => CatalogRevisionIngestionFailureV1.ProtocolFailure,
        IngestionFailureCode.ReservedProfileUnavailable => CatalogRevisionIngestionFailureV1.ReservedProfileUnavailable,
        IngestionFailureCode.EvidenceLimit => CatalogRevisionIngestionFailureV1.EvidenceLimit,
        _ => throw new InvalidOperationException("The ingestion failure category is unknown.")
    };
}
