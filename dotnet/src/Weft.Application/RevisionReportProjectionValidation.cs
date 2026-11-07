using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Checks provider-independent v1 facts from a bounded, already owned snapshot.</summary>
public static partial class RevisionReportProjectionValidation
{
    public static RevisionReportObservation Validate(RevisionReportKey key, ProjectionReportV1 ownedReport,
        RevisionReportPolicy activeCaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var budget = new RevisionReportValidationBudget(RevisionReportWorkflowValue.EffectiveReadCaps(key, activeCaps), cancellationToken);
        budget.Required(ownedReport);
        RevisionReportValidationBudget.Require(key is ProjectionCacheKey && key.SchemaVersion == "weft.mapping-projection.v1");
        var status = budget.Status(ownedReport.Status);
        var provenance = budget.Provenance(key, ownedReport.Provenance);
        var diagnostics = budget.Array(ownedReport.Diagnostics, budget.Caps.DiagnosticLimit);
        for (var index = 0; index < diagnostics; index++)
        {
            budget.Check();
            var diagnostic = budget.Required(ownedReport.Diagnostics[index]);
            budget.Text(diagnostic.Code, false); budget.Known(diagnostic.Severity); budget.Text(diagnostic.Message);
            if (diagnostic.NativeLocator is not null) budget.Locator(diagnostic.NativeLocator);
        }

        RevisionReportProjectionSummary summary;
        if (ownedReport.Document is { } document)
        {
            RevisionReportValidationBudget.Require(status == RevisionReportBackendStatus.Succeeded && provenance is { Confirmed: true });
            budget.Required(document.Provenance);
            budget.Provenance(key, document.Provenance.Revision, document.Provenance.NativeArtifactDigest,
                document.Provenance.Backend, document.Provenance.AdapterVersion);
            budget.Text(document.Provenance.ProjectionVersion, false);
            RevisionReportValidationBudget.Require(document.SchemaVersion == key.SchemaVersion && document.Provenance.ProjectionVersion == key.AlgorithmVersion);
            summary = new GraphContext(budget, document).Validate(diagnostics);
        }
        else summary = new(null, 0, 0, 0, diagnostics);
        budget.ReportJson(ownedReport);
        return new(RevisionReportOperation.Projection, status, provenance, summary, null);
    }
}
