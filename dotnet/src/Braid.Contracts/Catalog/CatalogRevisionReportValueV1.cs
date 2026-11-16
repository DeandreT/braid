using Braid.Contracts.Operations;

namespace Braid.Contracts.Catalog;

internal static class CatalogRevisionReportValueV1
{
    internal static void Known<T>(T? value, string name) where T : struct, Enum
    {
        if (value is { } known && !Enum.IsDefined(known)) throw new ArgumentOutOfRangeException(name);
    }

    internal static void Shape(string schemaVersion, CatalogRevisionScopeV1 revision, string operation,
        string expectedOperation, CatalogRevisionReportStateV1 state, CatalogRevisionReportReadOutcomeV1 readOutcome,
        OperationStatusV1? status, CatalogRevisionReportResultV1? result, CatalogRevisionReportAttemptV1? attempt,
        DateTimeOffset? next, CatalogRevisionReportFailureV1? failure, bool hasPayload)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (schemaVersion != "braid.catalog-revision-report.v1" || operation != expectedOperation)
            throw new ArgumentException("The report wrapper requires its exact schema and operation.");
        Known<CatalogRevisionReportStateV1>(state, nameof(state));
        Known<CatalogRevisionReportReadOutcomeV1>(readOutcome, nameof(readOutcome));
        Known(status, nameof(status));
        Known(failure, nameof(failure));
        var valid = state switch
        {
            CatalogRevisionReportStateV1.NotDerived => result is null && attempt is null && status is null && failure is null && next is null && !hasPayload,
            CatalogRevisionReportStateV1.Pending => result is null && attempt is { FinishedAtUtc: null, Status: null, Failure: null } &&
                status is null && failure is null && next is not null && !hasPayload,
            CatalogRevisionReportStateV1.Failed => result is null && attempt is { FinishedAtUtc: not null, Failure: not null } &&
                failure == attempt.Failure && status == attempt.Status && next is not null && !hasPayload,
            CatalogRevisionReportStateV1.Available => result is not null && attempt is null && next is null && failure is null && status is not null,
            _ => false
        };
        if (!valid) throw new ArgumentException("The report wrapper must agree with its actual lifecycle facts.");
        if (attempt is not null && next is { } nextTime && nextTime < (attempt.FinishedAtUtc ?? attempt.StartedAtUtc))
            throw new ArgumentException("The next attempt time cannot precede its observed attempt.", nameof(next));
        if (readOutcome == CatalogRevisionReportReadOutcomeV1.Observed)
        {
            if ((state == CatalogRevisionReportStateV1.Available) != hasPayload)
                throw new ArgumentException("An available observation requires its typed payload.");
        }
        else if (hasPayload || readOutcome != CatalogRevisionReportReadOutcomeV1.ResourceLimit && state != CatalogRevisionReportStateV1.Available)
            throw new ArgumentException("A report read refusal cannot contain a payload or invent available metadata.");
    }
}
