using Weft.Contracts.Validation;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Preserves bounded actual validation observations without inferring missing validity.</summary>
public static class RevisionReportValidationValidation
{
    public static RevisionReportObservation Validate(RevisionReportKey key, ValidationReportV1 ownedReport,
        RevisionReportPolicy activeCaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var budget = new RevisionReportValidationBudget(RevisionReportWorkflowValue.EffectiveReadCaps(key, activeCaps), cancellationToken);
        budget.Required(ownedReport);
        RevisionReportValidationBudget.Require(key is ValidationCacheKey && key.SchemaVersion == "weft.mapping-validation.v1");
        var status = budget.Status(ownedReport.Status);
        var provenance = budget.Provenance(key, ownedReport.Provenance);
        budget.Count(ownedReport.TotalIssueCount, budget.Caps.ValidationIssueLimit);
        budget.Count(ownedReport.EmittedIssueCount, budget.Caps.DiagnosticLimit);
        RevisionReportValidationBudget.Require(ownedReport.EmittedIssueCount <= ownedReport.TotalIssueCount);
        var count = budget.Array(ownedReport.Diagnostics, budget.Caps.DiagnosticLimit);
        if (ownedReport.IsValid is { } isValid)
        {
            RevisionReportValidationBudget.Require(provenance is { Confirmed: true } && count == ownedReport.EmittedIssueCount &&
                status == (isValid ? RevisionReportBackendStatus.Succeeded : RevisionReportBackendStatus.InvalidMapping));
        }
        for (var index = 0; index < count; index++)
        {
            budget.Check();
            var diagnostic = budget.Required(ownedReport.Diagnostics[index]);
            budget.Text(diagnostic.Code, false); budget.Known(diagnostic.Severity); budget.Text(diagnostic.Message);
            budget.OptionalText(diagnostic.Location);
            if (diagnostic.NativeLocator is not null) budget.Locator(diagnostic.NativeLocator);
            if (diagnostic.Owner is not null) Owner(budget, diagnostic.Owner);
            // Native-only storage and selected-projection proof are enforced by their owning factories/codec.
            if (diagnostic.ProjectionLocator is not null) budget.ReferenceId(diagnostic.ProjectionLocator);
        }
        budget.ReportJson(ownedReport);
        return new(RevisionReportOperation.Validation, status, provenance, null,
            new(ownedReport.IsValid, ownedReport.TotalIssueCount, ownedReport.EmittedIssueCount, ownedReport.Truncated, count));
    }

    private static void Owner(RevisionReportValidationBudget budget, NativeDiagnosticOwnerV1 owner)
    {
        budget.Check();
        switch (owner)
        {
            case GraphNodeNativeOwnerV1 graph:
                budget.Text(graph.NodeId, false);
                if (graph.FunctionId is not null) budget.Text(graph.FunctionId, false);
                break;
            case UserFunctionNativeOwnerV1 function: budget.Text(function.FunctionId, false); break;
            case EndpointNativeOwnerV1 endpoint: Endpoint(budget, endpoint.Endpoint); break;
            case FailureRuleNativeOwnerV1 failure: budget.Unsigned(failure.Index); break;
            case SchemaNodeNativeOwnerV1 schema:
                Endpoint(budget, schema.Endpoint);
                var schemaCount = budget.Array(schema.Path);
                for (var index = 0; index < schemaCount; index++)
                {
                    budget.Check();
                    switch (schema.Path[index])
                    {
                        case ChildSchemaNativeOwnerStepV1 child: budget.Unsigned(child.Index); break;
                        case ContainsSchemaNativeOwnerStepV1 contains: budget.Unsigned(contains.Index); break;
                        case DependentSchemaNativeOwnerStepV1 dependent: budget.Unsigned(dependent.Index); break;
                        case DynamicFieldSchemaNativeOwnerStepV1: break;
                        default: throw RevisionReportValidationBudget.Integrity();
                    }
                }
                break;
            case ScopeNativeOwnerV1 scope:
                Endpoint(budget, scope.Target);
                var scopeCount = budget.Array(scope.Path);
                for (var index = 0; index < scopeCount; index++)
                {
                    budget.Check();
                    switch (scope.Path[index])
                    {
                        case ChildScopeNativeOwnerStepV1 child: budget.Unsigned(child.Index); break;
                        case DynamicChildScopeNativeOwnerStepV1 dynamicChild: budget.Unsigned(dynamicChild.Index); break;
                        case SegmentScopeNativeOwnerStepV1 segment: budget.Unsigned(segment.Index); break;
                        default: throw RevisionReportValidationBudget.Integrity();
                    }
                }
                break;
            default: throw RevisionReportValidationBudget.Integrity();
        }
    }

    private static void Endpoint(RevisionReportValidationBudget budget, NativeEndpointV1? endpoint)
    {
        budget.Check();
        switch (endpoint)
        {
            case SourceNativeEndpointV1: case TargetNativeEndpointV1: break;
            case NamedSourceNativeEndpointV1 source: budget.Unsigned(source.Index); budget.Text(source.Name); break;
            case NamedTargetNativeEndpointV1 target: budget.Unsigned(target.Index); budget.Text(target.Name); break;
            default: throw RevisionReportValidationBudget.Integrity();
        }
    }
}
