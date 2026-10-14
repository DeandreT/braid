using System.Text.Json.Serialization;
using Weft.Contracts.Operations;
using Weft.Contracts.Validation;

namespace Weft.Contracts.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogValidationReportV1
{
    [JsonConstructor]
    public CatalogValidationReportV1(string schemaVersion, CatalogRevisionScopeV1 revision, string operation,
        CatalogRevisionReportStateV1 state, CatalogRevisionReportReadOutcomeV1 readOutcome, OperationStatusV1? status,
        CatalogRevisionReportResultV1? result, CatalogRevisionReportAttemptV1? currentAttempt,
        DateTimeOffset? nextAttemptAtUtc, CatalogRevisionReportFailureV1? failure, ValidationReportV1? report,
        string? projectionDerivationId)
    {
        CatalogRevisionReportValueV1.Shape(schemaVersion, revision, operation, "validation", state, readOutcome,
            status, result, currentAttempt, nextAttemptAtUtc, failure, report is not null);
        if (state == CatalogRevisionReportStateV1.Available && status is not (OperationStatusV1.Succeeded or OperationStatusV1.InvalidMapping) ||
            report is not null && (report.Status != status || report.IsValid is null ||
                report.IsValid != (status == OperationStatusV1.Succeeded)))
            throw new ArgumentException("An available validation requires its actual true or false result.", nameof(report));
        if (projectionDerivationId is not null)
        {
            CatalogValueV1.RequireId(projectionDerivationId, nameof(projectionDerivationId));
            if (report is null && (readOutcome != CatalogRevisionReportReadOutcomeV1.ResourceLimit || state != CatalogRevisionReportStateV1.Available))
                throw new ArgumentException("A projection selection requires a verified payload or its resource-limit replacement.", nameof(projectionDerivationId));
        }
        SchemaVersion = schemaVersion;
        Revision = revision;
        Operation = operation;
        State = state;
        ReadOutcome = readOutcome;
        Status = status;
        Result = result;
        CurrentAttempt = currentAttempt;
        NextAttemptAtUtc = nextAttemptAtUtc?.ToUniversalTime();
        Failure = failure;
        Report = report;
        ProjectionDerivationId = projectionDerivationId;
    }

    public string SchemaVersion { get; }
    public CatalogRevisionScopeV1 Revision { get; }
    public string Operation { get; }
    public CatalogRevisionReportStateV1 State { get; }
    public CatalogRevisionReportReadOutcomeV1 ReadOutcome { get; }
    public OperationStatusV1? Status { get; }
    public CatalogRevisionReportResultV1? Result { get; }
    public CatalogRevisionReportAttemptV1? CurrentAttempt { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; }
    public CatalogRevisionReportFailureV1? Failure { get; }
    public ValidationReportV1? Report { get; }
    public string? ProjectionDerivationId { get; }
}
