using System.Globalization;
using Microsoft.AspNetCore.Http;
using Braid.Application;
using Braid.Contracts.Catalog;
using Braid.Contracts.Operations;
using Braid.Domain;

namespace Braid.Api.Catalog;

internal static class CatalogRevisionReportMapper
{
    private const string Schema = "braid.catalog-revision-report.v1";

    internal static CatalogRevisionReportMappedResponse Read(ActorContext actor, CatalogRevision admittedRevision,
        RevisionReportOperation operation, Guid? retainedDerivationId, Guid? projectionDerivationId,
        RevisionReportReadResult result, CatalogRevisionReportOptions options, CancellationToken cancellationToken)
    {
        Admission(actor, admittedRevision, operation);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (retainedDerivationId == Guid.Empty || projectionDerivationId == Guid.Empty ||
            operation == RevisionReportOperation.Projection && projectionDerivationId is not null) throw Inconsistent();
        if (result.Source is null)
        {
            if (result.Snapshot is not null || result.SelectedResult is not null || result.Report is not null) throw Inconsistent();
            return result.Outcome switch
            {
                RevisionReportReadOutcome.NotAuthorizedOrNotFound => Error(404, CatalogErrorCodeV1.NotFound),
                RevisionReportReadOutcome.AdmissionBusy => Error(429, CatalogErrorCodeV1.RevisionReportAdmissionBusy),
                RevisionReportReadOutcome.ProfileUnavailable => Error(503, CatalogErrorCodeV1.RevisionReportUnavailable),
                _ => throw Inconsistent()
            };
        }
        Source(actor, admittedRevision, result.Source);
        if (result.Outcome == RevisionReportReadOutcome.SelectedProjectionUnavailable)
        {
            if (operation != RevisionReportOperation.Validation || projectionDerivationId is null ||
                result.SelectedResult is null || result.Report is not null) throw Inconsistent();
            Key(result.SelectedResult.Key, result.Source, operation);
            if (retainedDerivationId is { } id && result.SelectedResult.DerivationId != id) throw Inconsistent();
            return Error(404, CatalogErrorCodeV1.NotFound, result.Source);
        }
        return Observation(operation, retainedDerivationId, projectionDerivationId, result, options, 200, null, null, cancellationToken);
    }

    internal static CatalogRevisionReportMappedResponse Ensure(ActorContext actor, CatalogRevision admittedRevision,
        RevisionReportOperation operation, Guid? repairDerivationId, RevisionReportEnsureResult result,
        CatalogRevisionReportOptions options, CancellationToken cancellationToken)
    {
        Admission(actor, admittedRevision, operation);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (repairDerivationId == Guid.Empty) throw Inconsistent();
        if (result.Outcome == RevisionReportEnsureOutcome.Observed)
            return Read(actor, admittedRevision, operation, null, null, result.Read ?? throw Inconsistent(), options, cancellationToken);
        if (result.Outcome is RevisionReportEnsureOutcome.Pending or RevisionReportEnsureOutcome.RetryDelayed)
        {
            var snapshot = result.Snapshot ?? throw Inconsistent();
            Source(actor, admittedRevision, snapshot.Source);
            Key(snapshot.Key, snapshot.Source, operation);
            if (result.Outcome == RevisionReportEnsureOutcome.Pending && !snapshot.IsClaimLive ||
                result.Outcome == RevisionReportEnsureOutcome.RetryDelayed &&
                    (snapshot.State != RevisionReportState.Failed || snapshot.NextAttemptAtUtc <= snapshot.Source.ObservedAtUtc)) throw Inconsistent();
            var next = snapshot.NextAttemptAtUtc ?? throw Inconsistent();
            var ticks = (next - snapshot.Source.ObservedAtUtc).Ticks;
            var seconds = ticks / TimeSpan.TicksPerSecond + (ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);
            if (seconds is < 1 or > int.MaxValue) throw Inconsistent();
            return Observation(operation, null, null, RevisionReportReadResult.ForSnapshot(snapshot), options, 202,
                CurrentUrl(admittedRevision.Summary.Scope, operation), (int)seconds, cancellationToken);
        }
        if (result.Read is not null || result.Snapshot is not null) throw Inconsistent();
        return result.Outcome switch
        {
            RevisionReportEnsureOutcome.NotAuthorizedOrNotFound => Error(404, CatalogErrorCodeV1.NotFound),
            RevisionReportEnsureOutcome.AdmissionBusy => Error(429, CatalogErrorCodeV1.RevisionReportAdmissionBusy),
            RevisionReportEnsureOutcome.ClaimLost => Error(409, CatalogErrorCodeV1.RevisionReportClaimLost),
            RevisionReportEnsureOutcome.SelectionChanged => Error(409, CatalogErrorCodeV1.RevisionReportSelectionChanged),
            RevisionReportEnsureOutcome.GenerationExhausted => Error(409, CatalogErrorCodeV1.RevisionReportGenerationExhausted),
            RevisionReportEnsureOutcome.ProfileUnavailable => Error(503, CatalogErrorCodeV1.RevisionReportUnavailable),
            _ => throw Inconsistent()
        };
    }

    private static CatalogRevisionReportMappedResponse Observation(RevisionReportOperation operation, Guid? retainedId,
        Guid? projectionId, RevisionReportReadResult read, CatalogRevisionReportOptions options, int statusCode,
        string? location, int? retryAfter, CancellationToken token)
    {
        var source = read.Source ?? throw Inconsistent();
        var key = read.SelectedResult?.Key ?? read.Snapshot?.Key ?? throw Inconsistent();
        Key(key, source, operation);
        if (read.Snapshot is { } snapshot && (snapshot.Source != source || snapshot.Key != key)) throw Inconsistent();
        if (retainedId is { } requested && read.SelectedResult?.DerivationId != requested) throw Inconsistent();
        var selected = read.SelectedResult;
        if (selected is not null && (selected.Key != key || read.Snapshot is not null)) throw Inconsistent();
        if (read.Report is { } verified && (selected is null || verified.Metadata != selected ||
            operation == RevisionReportOperation.Projection && verified is not VerifiedProjectionReport ||
            operation == RevisionReportOperation.Validation && verified is not VerifiedValidationReport)) throw Inconsistent();
        if (read.Report is VerifiedValidationReport validation && validation.ProjectionDerivationId != projectionId) throw Inconsistent();
        if (read.Report is null && read.Outcome == RevisionReportReadOutcome.Observed && selected is not null) throw Inconsistent();
        token.ThrowIfCancellationRequested();
        var state = State(read.State ?? throw Inconsistent());
        var actualAttempt = selected is null ? read.Snapshot?.CurrentAttempt : null;
        var observed = selected?.Observation ?? actualAttempt?.Completion?.Observation;
        if (observed is not null && observed.Operation != operation) throw Inconsistent();
        var status = observed?.Status is { } actualStatus ? Status(actualStatus) : (OperationStatusV1?)null;
        var failure = actualAttempt?.Completion?.Failure is { } actualFailure ? Failure(actualFailure) : (CatalogRevisionReportFailureV1?)null;
        var attempt = actualAttempt is null ? null : new CatalogRevisionReportAttemptV1(Id(actualAttempt.DerivationId),
            Decimal(actualAttempt.Generation), actualAttempt.StartedAtUtc, actualAttempt.FinishedAtUtc, status, failure);
        var result = selected is null ? null : new CatalogRevisionReportResultV1(Id(selected.DerivationId), Decimal(selected.Generation),
            selected.StartedAtUtc, selected.FinishedAtUtc);
        var limits = new CatalogRevisionReportResponseLimits(Math.Min(key.Policy.ResponseByteLimit, options.ActiveReadCaps.ResponseByteLimit),
            Math.Min(key.Policy.ResponseItemLimit, options.ActiveReadCaps.ResponseItemLimit),
            Math.Min(key.Policy.JsonDepthLimit, options.ActiveReadCaps.JsonDepthLimit));
        var revision = CatalogPublicMapper.RevisionScope(source.Source.Scope);
        var outcome = ReadOutcome(read.Outcome);
        token.ThrowIfCancellationRequested();
        return operation == RevisionReportOperation.Projection
            ? CatalogRevisionReportMappedResponse.ForProjection(statusCode, location, retryAfter,
                new(Schema, revision, "projection", state, outcome, status, result, attempt, read.NextAttemptAtUtc, failure,
                    (read.Report as VerifiedProjectionReport)?.Report), source, limits)
            : CatalogRevisionReportMappedResponse.ForValidation(statusCode, location, retryAfter,
                new(Schema, revision, "validation", state, outcome, status, result, attempt, read.NextAttemptAtUtc, failure,
                    (read.Report as VerifiedValidationReport)?.Report,
                    (read.Report as VerifiedValidationReport)?.ProjectionDerivationId is { } linked ? Id(linked) : null), source, limits);
    }

    private static void Admission(ActorContext actor, CatalogRevision revision, RevisionReportOperation operation)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(revision);
        if (!Enum.IsDefined(operation) || revision.Access.PrincipalId != actor.PrincipalId ||
            revision.Access.Scope.OrganizationId != revision.Summary.Scope.OrganizationId ||
            revision.Access.Scope.ProjectId != revision.Summary.Scope.ProjectId || !revision.Access.Can(ProjectAction.ReadMetadata)) throw Inconsistent();
    }

    private static void Source(ActorContext actor, CatalogRevision revision, AuthorizedRevisionReportSource source)
    {
        var summary = revision.Summary;
        var facts = source.Source;
        if (source.Actor != actor.PrincipalId || facts.Scope != summary.Scope || facts.NativeObject.Digest != summary.NativeDigest ||
            facts.NativeObject.ByteCount != summary.NativeByteCount || facts.LogicalDocument != summary.LogicalDocument ||
            facts.SourceBackendId != summary.BackendId) throw Inconsistent();
    }

    private static void Key(RevisionReportKey key, AuthorizedRevisionReportSource source, RevisionReportOperation operation)
    {
        if (key.Source != source.Source || key.Operation != operation ||
            operation == RevisionReportOperation.Projection && key is not ProjectionCacheKey ||
            operation == RevisionReportOperation.Validation && key is not ValidationCacheKey) throw Inconsistent();
    }

    private static CatalogRevisionReportStateV1 State(RevisionReportState state) => state switch
    {
        RevisionReportState.NotDerived => CatalogRevisionReportStateV1.NotDerived,
        RevisionReportState.Pending => CatalogRevisionReportStateV1.Pending,
        RevisionReportState.Available => CatalogRevisionReportStateV1.Available,
        RevisionReportState.Failed => CatalogRevisionReportStateV1.Failed,
        _ => throw Inconsistent()
    };

    private static CatalogRevisionReportReadOutcomeV1 ReadOutcome(RevisionReportReadOutcome outcome) => outcome switch
    {
        RevisionReportReadOutcome.Observed => CatalogRevisionReportReadOutcomeV1.Observed,
        RevisionReportReadOutcome.ObjectUnavailable => CatalogRevisionReportReadOutcomeV1.ObjectUnavailable,
        RevisionReportReadOutcome.StorageIntegrity => CatalogRevisionReportReadOutcomeV1.StorageIntegrity,
        RevisionReportReadOutcome.UnsupportedFormat => CatalogRevisionReportReadOutcomeV1.UnsupportedFormat,
        RevisionReportReadOutcome.ResourceLimit => CatalogRevisionReportReadOutcomeV1.ResourceLimit,
        _ => throw Inconsistent()
    };

    private static OperationStatusV1 Status(RevisionReportBackendStatus status) => status switch
    {
        RevisionReportBackendStatus.Succeeded => OperationStatusV1.Succeeded,
        RevisionReportBackendStatus.InvalidMapping => OperationStatusV1.InvalidMapping,
        RevisionReportBackendStatus.UnsupportedArtifact => OperationStatusV1.UnsupportedArtifact,
        RevisionReportBackendStatus.ResourceLimit => OperationStatusV1.ResourceLimit,
        RevisionReportBackendStatus.ProtocolFailure => OperationStatusV1.ProtocolFailure,
        RevisionReportBackendStatus.BackendUnavailable => OperationStatusV1.BackendUnavailable,
        RevisionReportBackendStatus.BackendFailure => OperationStatusV1.BackendFailure,
        RevisionReportBackendStatus.TimedOut => OperationStatusV1.TimedOut,
        RevisionReportBackendStatus.Canceled => OperationStatusV1.Canceled,
        RevisionReportBackendStatus.IsolationFailure => OperationStatusV1.IsolationFailure,
        _ => throw Inconsistent()
    };

    private static CatalogRevisionReportFailureV1 Failure(RevisionReportFailureCategory failure) => failure switch
    {
        RevisionReportFailureCategory.ObjectUnavailable => CatalogRevisionReportFailureV1.ObjectUnavailable,
        RevisionReportFailureCategory.StorageIntegrity => CatalogRevisionReportFailureV1.StorageIntegrity,
        RevisionReportFailureCategory.StorageIo => CatalogRevisionReportFailureV1.StorageIo,
        RevisionReportFailureCategory.BackendUnavailable => CatalogRevisionReportFailureV1.BackendUnavailable,
        RevisionReportFailureCategory.BackendFailure => CatalogRevisionReportFailureV1.BackendFailure,
        RevisionReportFailureCategory.TimedOut => CatalogRevisionReportFailureV1.TimedOut,
        RevisionReportFailureCategory.Canceled => CatalogRevisionReportFailureV1.Canceled,
        RevisionReportFailureCategory.IsolationFailure => CatalogRevisionReportFailureV1.IsolationFailure,
        RevisionReportFailureCategory.ProtocolFailure => CatalogRevisionReportFailureV1.ProtocolFailure,
        RevisionReportFailureCategory.ProfileUnavailable => CatalogRevisionReportFailureV1.ProfileUnavailable,
        RevisionReportFailureCategory.ResourceLimit => CatalogRevisionReportFailureV1.ResourceLimit,
        RevisionReportFailureCategory.UnsupportedArtifact => CatalogRevisionReportFailureV1.UnsupportedArtifact,
        _ => throw Inconsistent()
    };

    private static CatalogRevisionReportMappedResponse Error(int status, CatalogErrorCodeV1 code, AuthorizedRevisionReportSource? source = null) =>
        CatalogRevisionReportMappedResponse.ForError(status, code, source);
    private static string CurrentUrl(RevisionScope scope, RevisionReportOperation operation) =>
        $"/api/v1/organizations/{scope.OrganizationId:D}/projects/{scope.ProjectId:D}/mappings/{scope.MappingId:D}/revisions/{scope.RevisionId:D}/reports/{(operation == RevisionReportOperation.Projection ? "projection" : "validation")}";
    private static string Id(Guid id) => id.ToString("D");
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static InvalidOperationException Inconsistent() => new("The revision report response facts are inconsistent.");
}
