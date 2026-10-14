using Weft.Application;
using Weft.Contracts.Examples;
using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.MappingBackends;

/// <summary>Pure supported Ferrule rules over the historical key; no current image or runner is consulted.</summary>
public sealed class FerruleRevisionReportFormatValidator : IRevisionReportFormatValidator
{
    public void Validate(RevisionReportKey key, ProjectionReportV1 report, CancellationToken cancellationToken = default)
    {
        RequireFormat(key, RevisionReportOperation.Projection, cancellationToken);
        _ = RevisionReportProjectionValidation.Validate(key, report, key.Policy, cancellationToken);
        if (report.Document is { } document)
            FerruleInspectionBackend.ValidateStoredProjectionDocument(document, key.Policy, cancellationToken);
    }

    public void Validate(RevisionReportKey key, ValidationReportV1 report, CancellationToken cancellationToken = default)
    {
        RequireFormat(key, RevisionReportOperation.Validation, cancellationToken);
        _ = RevisionReportValidationValidation.Validate(key, report, key.Policy, cancellationToken);
        FerruleInspectionBackend.ValidateStoredValidationReport(report, key.Policy, cancellationToken);
    }

    private static void RequireFormat(RevisionReportKey key, RevisionReportOperation operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        if (key.Operation != operation || RevisionReportFormat.FromKey(key) != FerruleRevisionReportBackendFactory.Format(operation) ||
            key.Source.LogicalDocument != InspectionPolicy.DocumentId || key.Source.SourceBackendId != "ferrule")
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
    }
}
