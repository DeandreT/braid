using Microsoft.Extensions.DependencyInjection;
using Weft.Api.Catalog;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    /// <summary>
    /// Verifies each candidate freshly, as the conversation's creator, through the same read a tool or route would make,
    /// and returns only the candidates that exist and are readable now. An unverifiable candidate is omitted; a
    /// failure of the admission itself yields no citations. Organization admission is observed before and after.
    /// </summary>
    public async Task<IReadOnlyList<ChatValidatedCitation>> VerifyAsync(ActorContext actor, ChatConversationIntent conversation,
        IReadOnlyList<ChatWorkspaceCitationFact> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(candidates);
        ct.ThrowIfCancellationRequested();
        if (conversation.Key.Kind != ChatConversationKind.Workspace || actor.PrincipalId != conversation.Key.Creator ||
            candidates.Count > ChatReadOnlyToolResult.MaxCitationCandidates || candidates.Any(candidate => candidate is null)) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CallDuration);
        var token = deadline.Token;
        var admitted = false;
        try
        {
            var scope = new CallScope(actor, conversation, null, default);
            await AdmitAsync(scope, token);
            if (!capacity.Wait(0)) return [];
            admitted = true;
            var verified = new List<ChatValidatedCitation>();
            var reports = new Dictionary<(RevisionScope, ChatReportPin, bool), VerifiedRevisionReport?>();
            foreach (var candidate in candidates.Distinct())
            {
                if (candidate.OrganizationId != conversation.Key.OrganizationId) continue;
                if (candidate.Kind == ChatWorkspaceCitationKind.Report)
                {
                    var validation = candidate.Target!.Kind is ChatCitationKind.ValidationDiagnostic or ChatCitationKind.ValidationReport;
                    var key = (candidate.RevisionScope!, candidate.Report!, validation);
                    if (!reports.TryGetValue(key, out var report)) reports[key] = report = await RetainedReportAsync(actor, key.Item1, key.Item2, validation, token);
                    if (report is null) continue;
                    try { verified.Add(ChatValidatedCitation.ValidateWorkspaceReport(conversation, report, candidate.Target)); }
                    catch (ArgumentException) { }
                    continue;
                }
                if (await ReadableAsync(actor, candidate, token)) verified.Add(ChatValidatedCitation.ForReadResource(conversation, candidate));
            }
            await AdmitAsync(scope, token);
            token.ThrowIfCancellationRequested();
            return verified.AsReadOnly();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { ct.ThrowIfCancellationRequested(); return []; }
        finally { if (admitted) capacity.Release(); }
    }

    /// <summary>The exact retained report a citation names, read by its derivation and checked against its whole pin.</summary>
    private async Task<VerifiedRevisionReport?> RetainedReportAsync(ActorContext actor, RevisionScope revision, ChatReportPin pin, bool validation,
        CancellationToken token)
    {
        var read = await Required<IRevisionReportReader>().GetRetainedAsync(actor, revision,
            validation ? RevisionReportOperation.Validation : RevisionReportOperation.Projection, pin.DerivationId, cancellationToken: token);
        if (read.Outcome != RevisionReportReadOutcome.Observed || read.Report is not { } report || read.Source?.Actor != actor.PrincipalId ||
            report.Metadata.Key.Scope != revision || MappingToolServicePin(report) != pin) return null;
        return report;
    }

    private static ChatReportPin MappingToolServicePin(VerifiedRevisionReport report) => Application.MappingTools.MappingToolService.Pin(report.Metadata);

    private async Task<bool> ReadableAsync(ActorContext actor, ChatWorkspaceCitationFact fact, CancellationToken token)
    {
        switch (fact.Kind)
        {
            case ChatWorkspaceCitationKind.Project:
                return await Catalog.GetProjectAsync(actor, fact.ProjectScope!, token) is { } project && project.Summary.Scope == fact.ProjectScope;
            case ChatWorkspaceCitationKind.Mapping:
                return await CatalogEndpoints.ReadableMappingAsync(services, actor, fact.MappingScope!, token) is not null;
            case ChatWorkspaceCitationKind.Revision:
                return await CatalogEndpoints.ReadableRevisionAsync(services, actor, fact.RevisionScope!, token) is not null;
            case ChatWorkspaceCitationKind.PreviewRun:
                return features.Previews && Optional<PreviewRunService>() is { } previews &&
                    await previews.ReadAsync(actor, fact.OrganizationId, fact.SubjectId!.Value, token) is { } preview && preview.Revision == fact.RevisionScope;
            case ChatWorkspaceCitationKind.RegressionCase:
                return features.Regressions && Optional<IRegressionCaseMetadataReader>() is { } cases &&
                    await CatalogEndpoints.ReadableMappingAsync(services, actor, fact.MappingScope!, token) is not null &&
                    await cases.ReadMetadataAsync(actor, fact.MappingScope!, fact.SubjectId!.Value, fact.Version!.Value, token) is { } version &&
                    version.CaseId == fact.SubjectId && version.Version == fact.Version &&
                    version.Revision.OrganizationId == fact.OrganizationId && version.Revision.ProjectId == fact.ProjectId &&
                    version.Revision.MappingId == fact.MappingId &&
                    await CatalogEndpoints.ReadableRevisionAsync(services, actor, version.Revision, token) is not null;
            case ChatWorkspaceCitationKind.RegressionRun:
                return features.Regressions && await CatalogEndpoints.RegressionRunAccessAsync(services, actor, fact.MappingScope!, token) &&
                    Optional<IRegressionRunMetadataReader>() is { } runs && await runs.ReadMetadataAsync(actor, fact.OrganizationId, fact.SubjectId!.Value, token) is { } run &&
                    run.RunId == fact.SubjectId && run.Revision.OrganizationId == fact.OrganizationId &&
                    run.Revision.ProjectId == fact.ProjectId && run.Revision.MappingId == fact.MappingId &&
                    await CatalogEndpoints.RegressionRunAccessAsync(services, actor, fact.MappingScope!, token);
            case ChatWorkspaceCitationKind.BuildRun:
                return features.Builds && await CatalogEndpoints.BuildRunAsync(services, actor, fact.MappingScope!, fact.SubjectId!.Value, token) is not null;
            case ChatWorkspaceCitationKind.BuildApproval:
                return features.Builds && await CatalogEndpoints.BuildApprovalAsync(services, actor, fact.MappingScope!, fact.SubjectId!.Value, token) is not null;
            case ChatWorkspaceCitationKind.BuildEnvironment:
                return features.Builds && await CatalogEndpoints.BuildEnvironmentAsync(services, actor,
                    new BuildEnvironmentReference(fact.ProjectScope!, fact.SubjectId!.Value, fact.Version!.Value), token) is not null;
            case ChatWorkspaceCitationKind.GuideSection:
                return guide is not null && guide.Sections.Any(section => section.Id == fact.SectionId);
            default:
                return false;
        }
    }
}
