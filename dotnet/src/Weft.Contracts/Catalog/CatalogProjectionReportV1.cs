using System.Text.Json.Serialization;
using Weft.Contracts.Examples;
using Weft.Contracts.Operations;

namespace Weft.Contracts.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogProjectionReportV1
{
    [JsonConstructor]
    public CatalogProjectionReportV1(string schemaVersion, CatalogRevisionScopeV1 revision, string operation,
        CatalogRevisionReportStateV1 state, CatalogRevisionReportReadOutcomeV1 readOutcome, OperationStatusV1? status,
        CatalogRevisionReportResultV1? result, CatalogRevisionReportAttemptV1? currentAttempt,
        DateTimeOffset? nextAttemptAtUtc, CatalogRevisionReportFailureV1? failure, ProjectionReportV1? report)
    {
        CatalogRevisionReportValueV1.Shape(schemaVersion, revision, operation, "projection", state, readOutcome,
            status, result, currentAttempt, nextAttemptAtUtc, failure, report is not null);
        if (state == CatalogRevisionReportStateV1.Available && status != OperationStatusV1.Succeeded ||
            report is not null && (report.Status != status || report.Document is null))
            throw new ArgumentException("An available projection requires its actual successful document.", nameof(report));
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
    public ProjectionReportV1? Report { get; }
}
