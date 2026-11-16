using System.Text;
using Braid.Ai;
using Braid.Api.Catalog;
using Braid.Application;
using Braid.Application.Comparisons;
using Braid.Application.MappingTools;
using Braid.Contracts.Comparisons;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    /// <summary>
    /// One of the five report tools at an exact revision. It reads the revision's current retained reports only and
    /// never derives one; a report that has not been produced is reported as notDerived with its revision.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> ReportAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var revision = await LocateRevisionAsync(scope, call.Id("revisionId"), token);
        var validationTool = call.Name == "get_validation_issues";
        var context = await RetainedContextAsync(scope.Actor, revision, validationTool, token);
        if (context is null)
            return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.NotDerived, new
            {
                revisionId = Id(revision.RevisionId),
                report = validationTool ? "validation" : "projection"
            }, [ChatWorkspaceCitationFact.Revision(revision)]);
        var reply = await reports.InvokeAsync(scope.Actor, context, call.Name, call.Without("revisionId"),
            scope.Budget.Bytes, scope.Budget.Nodes, token);
        if (reply.Outcome is MappingToolOutcome.NotAuthorizedOrNotFound) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        // Report-local candidates become workspace citations of the exact report each was read from.
        var citations = reply.Citations.Select(target => ChatWorkspaceCitationFact.ReportResource(revision,
            target.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport ? context.Validation! : context.Projection, target));
        return new(reply.Result, reply.RetrievedNodes, workspaceCitations: citations.Take(ChatReadOnlyToolResult.MaxCitationCandidates));
    }

    /// <summary>The revision's current retained, verified reports; null when either needed report has not been derived.</summary>
    private async Task<MappingReportContext?> RetainedContextAsync(ActorContext actor, RevisionScope revision, bool validation, CancellationToken token)
    {
        var reader = Required<IRevisionReportReader>();
        var projection = Verified<VerifiedProjectionReport>(await reader.GetCurrentAsync(actor, revision, RevisionReportOperation.Projection, null, token));
        if (projection is null) return null;
        ChatReportPin? validationPin = null;
        if (validation)
        {
            var read = Verified<VerifiedValidationReport>(await reader.GetCurrentAsync(actor, revision, RevisionReportOperation.Validation,
                projection.Metadata.DerivationId, token));
            if (read is null) return null;
            validationPin = MappingToolService.Pin(read.Metadata);
        }
        return new(revision, MappingToolService.Pin(projection.Metadata), validationPin);
    }

    private static T? Verified<T>(RevisionReportReadResult read) where T : VerifiedRevisionReport => read.Outcome switch
    {
        RevisionReportReadOutcome.NotAuthorizedOrNotFound => throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound),
        RevisionReportReadOutcome.AdmissionBusy => throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Busy),
        RevisionReportReadOutcome.ResourceLimit => throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit),
        RevisionReportReadOutcome.Observed => read.Report as T,
        _ => throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable)
    };

    /// <summary>
    /// The comparison the mapping's comparison route computes, over each revision's current retained projection and
    /// validation. Items without an observed difference or uncertainty are counted, not listed.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> CompareRevisionsAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var left = await LocateRevisionAsync(scope, call.Id("baseRevisionId"), token);
        var right = await LocateRevisionAsync(scope, call.Id("targetRevisionId"), token);
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 20);
        if (left.ProjectId != right.ProjectId || left.MappingId != right.MappingId || left == right)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        var leftSide = await SideAsync(scope.Actor, left, token);
        var rightSide = await SideAsync(scope.Actor, right, token);
        if (leftSide.Side is null || rightSide.Side is null)
            return Outcome(call.Name, scope.Budget, WorkspaceToolOutcome.NotDerived, new
            {
                baseRevisionId = Id(left.RevisionId),
                baseDerived = leftSide.Side is not null,
                targetRevisionId = Id(right.RevisionId),
                targetDerived = rightSide.Side is not null
            }, [ChatWorkspaceCitationFact.Revision(left), ChatWorkspaceCitationFact.Revision(right)]);
        RevisionComparisonV1 comparison;
        try { comparison = new RevisionComparisonService().Compare(leftSide.Side, rightSide.Side, new RevisionComparisonLimits(), token); }
        catch (RevisionComparisonException error) when (error.Failure == RevisionComparisonFailure.ResourceLimit)
        { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit); }
        catch (RevisionReportValidationException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.ResourceLimit); }
        // Both immutable sources and authority are read again before the result is released, as the route does.
        var freshLeft = await CatalogEndpoints.ReadableRevisionAsync(services, scope.Actor, left, token);
        var freshRight = await CatalogEndpoints.ReadableRevisionAsync(services, scope.Actor, right, token);
        if (freshLeft is null || freshRight is null || freshLeft.Summary != leftSide.Revision || freshRight.Summary != rightSide.Revision)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var leftNames = Names(leftSide.Projection!.Report.Document);
        var rightNames = Names(rightSide.Projection!.Report.Document);
        var changes = comparison.Items.Where(item => item.Match != ComparisonMatchV1.Matched || item.Facets.Any(f => f.HasObservedDifference || f.HasUncertainty))
            .Select(item => new ComparisonChange(Camel(item.Entity), Camel(item.Match),
                item.Left.Select(candidate => Candidate(candidate, leftNames)).ToArray(),
                item.Right.Select(candidate => Candidate(candidate, rightNames)).ToArray(),
                item.Facets.Where(f => f.HasObservedDifference).Select(f => Camel(f.Aspect)).ToArray(),
                item.Facets.Any(f => f.HasUncertainty), item.CandidatesTruncated))
            .ToArray();
        var listed = changes.Take(limit).ToArray();
        var result = Reply(call.Name, scope.Budget, listed, changes.Length > limit || comparison.Truncated, kept => new
        {
            baseRevision = new { revisionId = Id(left.RevisionId), sequence = leftSide.Revision!.Sequence },
            targetRevision = new { revisionId = Id(right.RevisionId), sequence = rightSide.Revision!.Sequence },
            hasObservedDifferences = comparison.HasObservedDifferences,
            hasUncertainty = comparison.HasUncertainty,
            comparedItems = comparison.TotalItemCount,
            changedItems = changes.Length,
            changes = kept
        }, _ => []);
        return new(result.Result, result.RetrievedNodes, workspaceCitations: [ChatWorkspaceCitationFact.Revision(left), ChatWorkspaceCitationFact.Revision(right)]);
    }

    private async Task<(CatalogRevisionSummary? Revision, RevisionComparisonSide? Side, VerifiedProjectionReport? Projection)> SideAsync(
        ActorContext actor, RevisionScope revision, CancellationToken token)
    {
        var read = await CatalogEndpoints.ReadableRevisionAsync(services, actor, revision, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var reader = Required<IRevisionReportReader>();
        var projection = Verified<VerifiedProjectionReport>(await reader.GetCurrentAsync(actor, revision, RevisionReportOperation.Projection, null, token));
        if (projection is null) return (read.Summary, null, null);
        var validation = Verified<VerifiedValidationReport>(await reader.GetCurrentAsync(actor, revision, RevisionReportOperation.Validation,
            projection.Metadata.DerivationId, token));
        return (read.Summary, RevisionComparisonSide.Create(read.Summary, projection, validation, token), projection);
    }

    private static Dictionary<string, string> Names(ProjectionDocumentV1? document)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document is null) return names;
        foreach (var field in document.Fields) names.TryAdd("field:" + field.Id, FieldLabel(field));
        foreach (var rule in document.Rules) names.TryAdd("rule:" + rule.Id, rule.Summary);
        foreach (var boundary in document.Boundaries) names.TryAdd("boundary:" + boundary.Id, boundary.Id);
        foreach (var parameter in document.Parameters) names.TryAdd("parameter:" + parameter.Id, parameter.Name);
        foreach (var control in document.Controls) names.TryAdd("control:" + control.Id, control.Id);
        return names;
    }

    /// <summary>A field's name with its typed path, as users see it in the viewer.</summary>
    internal static string FieldLabel(ProjectionField field)
    {
        var path = new StringBuilder();
        foreach (var segment in field.Path)
        {
            path.Append(segment switch
            {
                FieldPathSegment item => "/" + item.Name,
                AttributePathSegment item => "/@" + item.Name,
                IndexPathSegment item => "[" + item.Index + "]",
                AnyItemPathSegment => "[*]",
                _ => "/?"
            });
        }
        return path.Length == 0 ? field.Name : field.Name + " (" + path + ")";
    }

    private static ComparisonNode Candidate(ComparisonCandidateV1 candidate, IReadOnlyDictionary<string, string> names)
    {
        var (kind, id) = candidate.Node switch
        {
            FieldNodeReference node => ("field", node.FieldId),
            RuleNodeReference node => ("rule", node.RuleId),
            BoundaryNodeReference node => ("boundary", node.BoundaryId),
            ParameterNodeReference node => ("parameter", node.ParameterId),
            ControlNodeReference node => ("control", node.ControlId),
            null => ("relationship", candidate.RelationshipId ?? ""),
            _ => ("unknown", "")
        };
        return new(kind, id, names.TryGetValue(kind + ":" + id, out var name) ? name : null);
    }

    private static string Camel<T>(T value) where T : Enum => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private T Required<T>() where T : class => Optional<T>() ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);

    private sealed record ComparisonNode(string Kind, string Id, string? Name);
    private sealed record ComparisonChange(string Entity, string Match, ComparisonNode[] Base, ComparisonNode[] Target,
        string[] ChangedAspects, bool Uncertain, bool CandidatesTruncated);
}
