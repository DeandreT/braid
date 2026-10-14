using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;
using Weft.Domain;
using Weft.MappingBackends.Ferrule;

namespace Weft.MappingBackends;

public sealed partial class FerruleInspectionBackend
{
    private static void ValidateReportValidationResult(HostValidationResult result, RevisionReportPolicy limits,
        CancellationToken cancellationToken)
    {
        var budget = new ProjectionBudget(limits, cancellationToken);
        budget.Check();
        Require(result is not null);
        budget.Array(result.Issues, limits.DiagnosticLimit);
        var count = result.Issues.Count;
        ReportValidationCounts(budget, result.TotalIssueCount, result.EmittedIssueCount);
        Require(result.EmittedIssueCount == count && result.IsValid == (result.TotalIssueCount == 0));
        var anyTextTruncated = false;
        for (var index = 0; index < count; index++)
        {
            budget.Check();
            var issue = result.Issues[index];
            budget.Check();
            Require(issue is not null);
            budget.Text(issue.Code, false);
            Require(issue.Code == "ferrule.validation");
            budget.Text(issue.Location); budget.Text(issue.Message, false);
            ReportValidationLocator(budget, issue.NativeLocator);
            if (issue.Owner is not null) ReportValidationOwner(budget, issue.Owner);
            if (issue.Owner is GraphNodeDiagnosticOwner graph)
                Require(issue.NativeLocator is GraphNodeNativeLocator locator &&
                    locator.NodeId == graph.NodeId && locator.FunctionId == graph.FunctionId);
            anyTextTruncated |= issue.LocationTruncated || issue.MessageTruncated;
        }
        budget.Check();
        Require(result.Truncated == (result.EmittedIssueCount < result.TotalIssueCount || anyTextTruncated));
    }

    internal static void ValidateStoredValidationReport(ValidationReportV1 report, RevisionReportPolicy limits,
        CancellationToken cancellationToken = default) => ReportProvider(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(report is not null && limits is not null);
        ValidateStoredValidationReportCore(report, new ProjectionBudget(limits, cancellationToken));
    });

    private static void ValidateStoredValidationReportCore(ValidationReportV1 report, ProjectionBudget budget)
    {
        budget.Check();
        Require(Enum.IsDefined(report.Status));
        budget.Array(report.Diagnostics, budget.Limits.DiagnosticLimit);
        var count = report.Diagnostics.Count;
        ReportValidationCounts(budget, report.TotalIssueCount, report.EmittedIssueCount);
        var actualValidity = report.IsValid;
        if (actualValidity is { } isValid)
        {
            Require(report.Status == (isValid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping));
            Require(isValid == (report.TotalIssueCount == 0) && report.EmittedIssueCount == count);
        }
        else Require(report.Status != OperationStatusV1.Succeeded);

        // Provenance is checked against the retained key by the external context, not a live worker.
        var anyTextTruncated = false;
        for (var index = 0; index < count; index++)
        {
            budget.Check();
            var diagnostic = report.Diagnostics[index];
            budget.Check();
            Require(diagnostic is not null && Enum.IsDefined(diagnostic.Severity) && diagnostic.ProjectionLocator is null);
            budget.Text(diagnostic.Code, false);
            budget.Text(diagnostic.Message, actualValidity is null);
            if (diagnostic.Location is not null) budget.Text(diagnostic.Location);
            if (diagnostic.NativeLocator is not null) ReportValidationLocator(budget, diagnostic.NativeLocator);
            if (diagnostic.Owner is not null) ReportValidationOwner(budget, diagnostic.Owner);
            if (actualValidity is not null)
                Require(diagnostic.Code == "ferrule.validation" && diagnostic.Severity == DiagnosticSeverity.Error &&
                    diagnostic.Location is not null && diagnostic.NativeLocator is not null);
            if (diagnostic.Owner is GraphNodeNativeOwnerV1 graph)
                Require(diagnostic.NativeLocator is GraphNodeNativeLocator locator &&
                    locator.NodeId == graph.NodeId && locator.FunctionId == graph.FunctionId);
            anyTextTruncated |= diagnostic.LocationTruncated || diagnostic.MessageTruncated;
        }
        budget.Check();
        if (actualValidity is not null)
            Require(report.Truncated == (report.EmittedIssueCount < report.TotalIssueCount || anyTextTruncated));
    }

    private static void ReportValidationCounts(ProjectionBudget budget, long total, long emitted)
    {
        budget.Check();
        Require(total >= 0 && emitted >= 0 && emitted <= total);
        if (total > budget.Limits.ValidationIssueLimit || emitted > budget.Limits.DiagnosticLimit)
            throw new ProjectionLimitException();
    }

    private static void ReportValidationOwner(ProjectionBudget budget, MappingDiagnosticOwner owner)
    {
        budget.Check();
        budget.Charge(1);
        switch (owner)
        {
            case GraphNodeDiagnosticOwner graph:
                ReportValidationDecimalId(budget, graph.NodeId);
                if (graph.FunctionId is not null) ReportValidationDecimalId(budget, graph.FunctionId);
                break;
            case UserFunctionDiagnosticOwner function: ReportValidationDecimalId(budget, function.FunctionId); break;
            case EndpointDiagnosticOwner endpoint: ReportValidationEndpoint(budget, endpoint.Endpoint); break;
            case FailureRuleDiagnosticOwner failure: ReportValidationDecimalId(budget, failure.Index); break;
            case SchemaNodeDiagnosticOwner schema:
                ReportValidationEndpoint(budget, schema.Endpoint);
                budget.Array(schema.Path);
                var schemaCount = schema.Path.Count;
                for (var index = 0; index < schemaCount; index++)
                {
                    budget.Check();
                    var step = schema.Path[index];
                    budget.Check();
                    switch (step)
                    {
                        case ChildSchemaOwnerStep child: ReportValidationDecimalId(budget, child.Index); break;
                        case ContainsSchemaOwnerStep contains: ReportValidationDecimalId(budget, contains.Index); break;
                        case DependentSchemaOwnerStep dependent: ReportValidationDecimalId(budget, dependent.Index); break;
                        case DynamicFieldSchemaOwnerStep: break;
                        default: throw new ProtocolException();
                    }
                }
                break;
            case ScopeDiagnosticOwner scope:
                ReportValidationEndpoint(budget, scope.Target);
                budget.Array(scope.Path);
                var scopeCount = scope.Path.Count;
                for (var index = 0; index < scopeCount; index++)
                {
                    budget.Check();
                    var step = scope.Path[index];
                    budget.Check();
                    switch (step)
                    {
                        case ChildScopeOwnerStep child: ReportValidationDecimalId(budget, child.Index); break;
                        case DynamicChildScopeOwnerStep dynamicStep: ReportValidationDecimalId(budget, dynamicStep.Index); break;
                        case SegmentScopeOwnerStep segment: ReportValidationDecimalId(budget, segment.Index); break;
                        default: throw new ProtocolException();
                    }
                }
                break;
            default: throw new ProtocolException();
        }
        budget.Check();
    }

    private static void ReportValidationOwner(ProjectionBudget budget, NativeDiagnosticOwnerV1 owner)
    {
        budget.Check();
        budget.Charge(1);
        switch (owner)
        {
            case GraphNodeNativeOwnerV1 graph:
                ReportValidationDecimalId(budget, graph.NodeId);
                if (graph.FunctionId is not null) ReportValidationDecimalId(budget, graph.FunctionId);
                break;
            case UserFunctionNativeOwnerV1 function: ReportValidationDecimalId(budget, function.FunctionId); break;
            case EndpointNativeOwnerV1 endpoint: ReportValidationEndpoint(budget, endpoint.Endpoint); break;
            case FailureRuleNativeOwnerV1 failure: ReportValidationDecimalId(budget, failure.Index); break;
            case SchemaNodeNativeOwnerV1 schema:
                ReportValidationEndpoint(budget, schema.Endpoint);
                budget.Array(schema.Path);
                var schemaCount = schema.Path.Count;
                for (var index = 0; index < schemaCount; index++)
                {
                    budget.Check();
                    var step = schema.Path[index];
                    budget.Check();
                    switch (step)
                    {
                        case ChildSchemaNativeOwnerStepV1 child: ReportValidationDecimalId(budget, child.Index); break;
                        case ContainsSchemaNativeOwnerStepV1 contains: ReportValidationDecimalId(budget, contains.Index); break;
                        case DependentSchemaNativeOwnerStepV1 dependent: ReportValidationDecimalId(budget, dependent.Index); break;
                        case DynamicFieldSchemaNativeOwnerStepV1: break;
                        default: throw new ProtocolException();
                    }
                }
                break;
            case ScopeNativeOwnerV1 scope:
                ReportValidationEndpoint(budget, scope.Target);
                budget.Array(scope.Path);
                var scopeCount = scope.Path.Count;
                for (var index = 0; index < scopeCount; index++)
                {
                    budget.Check();
                    var step = scope.Path[index];
                    budget.Check();
                    switch (step)
                    {
                        case ChildScopeNativeOwnerStepV1 child: ReportValidationDecimalId(budget, child.Index); break;
                        case DynamicChildScopeNativeOwnerStepV1 dynamicStep: ReportValidationDecimalId(budget, dynamicStep.Index); break;
                        case SegmentScopeNativeOwnerStepV1 segment: ReportValidationDecimalId(budget, segment.Index); break;
                        default: throw new ProtocolException();
                    }
                }
                break;
            default: throw new ProtocolException();
        }
        budget.Check();
    }

    private static void ReportValidationEndpoint(ProjectionBudget budget, MappingEndpoint endpoint)
    {
        budget.Check();
        switch (endpoint)
        {
            case SourceMappingEndpoint or TargetMappingEndpoint: break;
            case NamedSourceMappingEndpoint source:
                ReportValidationDecimalId(budget, source.Index); budget.Text(source.Name); break;
            case NamedTargetMappingEndpoint target:
                ReportValidationDecimalId(budget, target.Index); budget.Text(target.Name); break;
            default: throw new ProtocolException();
        }
        budget.Check();
    }

    private static void ReportValidationEndpoint(ProjectionBudget budget, NativeEndpointV1 endpoint)
    {
        budget.Check();
        switch (endpoint)
        {
            case SourceNativeEndpointV1 or TargetNativeEndpointV1: break;
            case NamedSourceNativeEndpointV1 source:
                ReportValidationDecimalId(budget, source.Index); budget.Text(source.Name); break;
            case NamedTargetNativeEndpointV1 target:
                ReportValidationDecimalId(budget, target.Index); budget.Text(target.Name); break;
            default: throw new ProtocolException();
        }
        budget.Check();
    }

    private static void ReportValidationLocator(ProjectionBudget budget, NativeLocator locator)
    {
        budget.Check();
        Require(locator is not null);
        budget.Text(locator.DocumentId, false);
        Require(locator.DocumentId == InspectionPolicy.DocumentId);
        switch (locator)
        {
            case DocumentNativeLocator: break;
            case GraphNodeNativeLocator graph:
                ReportValidationDecimalId(budget, graph.NodeId);
                if (graph.FunctionId is not null) ReportValidationDecimalId(budget, graph.FunctionId);
                break;
            case JsonPointerNativeLocator pointer:
                budget.Text(pointer.Pointer);
                Require(pointer.Pointer.Length == 0 || pointer.Pointer[0] == '/');
                for (var index = 0; index < pointer.Pointer.Length; index++)
                {
                    budget.Check();
                    if (pointer.Pointer[index] == '~')
                        Require(++index < pointer.Pointer.Length && pointer.Pointer[index] is '0' or '1');
                }
                break;
            case TextSpanNativeLocator span:
                ReportValidationDecimalId(budget, span.StartOffset); ReportValidationDecimalId(budget, span.Length);
                Require(Enum.IsDefined(span.Unit));
                break;
            case OpaqueNativeLocator opaque: budget.Text(opaque.Scheme, false); budget.Text(opaque.Value); break;
            default: throw new ProtocolException();
        }
        budget.Check();
    }

    private static void ReportValidationDecimalId(ProjectionBudget budget, string value)
    {
        budget.Text(value, false);
        Require(value.Length == 1 || value[0] != '0');
        foreach (var digit in value)
        {
            budget.Check();
            Require(digit is >= '0' and <= '9');
        }
        budget.Check();
    }
}
