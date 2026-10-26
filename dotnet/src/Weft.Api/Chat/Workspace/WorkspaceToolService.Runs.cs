using System.Text.Json;
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
    /// Run and control listings through the same authorized reads as the GET routes. Previews, regression runs, builds
    /// and approvals are the caller's own; regression cases and build environments are the mapping's or project's.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> ListRunsAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var kind = call.Choice("kind", "preview", "regressionCase", "regressionRun", "build", "buildApproval", "buildEnvironment");
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 10);
        var after = call.OptionalId("after");
        if (kind == "buildEnvironment")
        {
            // Environment versions page by (environment, version); the tool reads the first page and reports truncation.
            if (call.Has("mappingId") || after is not null) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            RequireFeature(features.Builds);
            var project = new ProjectScope(scope.Organization, call.Id("projectId"));
            var environments = await CatalogEndpoints.BuildEnvironmentPageAsync(services, scope.Actor, project, limit, null, token)
                ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
            var items = environments.Environments.Select(CatalogBuildMapping.Environment).ToArray();
            return Reply(call.Name, scope.Budget, items, environments.Next is not null, kept => new { kind, projectId = Id(project.ProjectId), items = kept },
                item => [ChatWorkspaceCitationFact.BuildEnvironment(project, Guid.Parse(item.Reference.EnvironmentId), long.Parse(item.Reference.Version,
                    System.Globalization.CultureInfo.InvariantCulture))]);
        }
        if (call.Has("projectId")) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        switch (kind)
        {
            case "preview":
                {
                    RequireFeature(features.Previews);
                    var page = await Required<PreviewRunService>().ListAsync(scope.Actor, mapping, limit, after, token);
                    var items = page.Runs.Select(run => new PreviewItem(Id(run.RunId), Id(run.Revision.RevisionId), Name(run.State),
                        run.Outcome is { } outcome ? Name(outcome) : null, run.CreatedAtUtc, run.FinishedAtUtc, run.ExpiresAtUtc)).ToArray();
                    return Reply(call.Name, scope.Budget, items, false, kept => Page(kind, mapping, kept, items.Length, page.NextRunId),
                        item => [ChatWorkspaceCitationFact.PreviewRun(Revision(mapping, item.RevisionId), Guid.Parse(item.RunId))]);
                }
            case "regressionCase":
                {
                    RequireFeature(features.Regressions);
                    var page = await CatalogEndpoints.RegressionCasePageAsync(services, scope.Actor, mapping, limit, after, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var items = page.Cases.Select(item => new CaseItem(Id(item.CaseId), item.Version, Id(item.Revision.RevisionId), item.Name, item.CreatedAt)).ToArray();
                    return Reply(call.Name, scope.Budget, items, false, kept => Page(kind, mapping, kept, items.Length, page.NextCaseId),
                        item => [ChatWorkspaceCitationFact.RegressionCase(mapping, Guid.Parse(item.CaseId), item.Version)]);
                }
            case "regressionRun":
                {
                    RequireFeature(features.Regressions);
                    var page = await CatalogEndpoints.RegressionRunPageAsync(services, scope.Actor, mapping, limit, after, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var items = page.Runs.Select(run => new RegressionRunItem(Id(run.RunId), Id(run.CaseId), run.Version, Id(run.Revision.RevisionId),
                        Name(run.State), run.Attempt, run.CreatedAt)).ToArray();
                    return Reply(call.Name, scope.Budget, items, false, kept => Page(kind, mapping, kept, items.Length, page.NextRunId),
                        item => [ChatWorkspaceCitationFact.RegressionRun(mapping, Guid.Parse(item.RunId))]);
                }
            case "build":
                {
                    RequireFeature(features.Builds);
                    var page = await CatalogEndpoints.BuildRunPageAsync(services, scope.Actor, mapping, limit, after, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var items = page.Runs.Select(CatalogBuildMapping.Run).ToArray();
                    return Reply(call.Name, scope.Budget, items, false, kept => Page(kind, mapping, kept, items.Length, page.NextBuildId),
                        item => [ChatWorkspaceCitationFact.BuildRun(mapping, Guid.Parse(item.BuildId))]);
                }
            default:
                {
                    RequireFeature(features.Builds);
                    var page = await CatalogEndpoints.BuildApprovalPageAsync(services, scope.Actor, mapping, limit, after, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var items = page.Approvals.Select(CatalogBuildMapping.Approval).ToArray();
                    return Reply(call.Name, scope.Budget, items, false, kept => Page(kind, mapping, kept, items.Length, page.NextApprovalId),
                        item => [ChatWorkspaceCitationFact.BuildApproval(mapping, Guid.Parse(item.ApprovalId))]);
                }
        }
    }

    /// <summary>One of the caller's own runs: status, attempts and result summary. Runtime failure text may contain sample values and is not metadata.</summary>
    private async Task<ChatReadOnlyToolResult> GetRunAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var kind = call.Choice("kind", "preview", "regressionRun", "build");
        var runId = call.Id("runId");
        switch (kind)
        {
            case "preview":
                {
                    RequireFeature(features.Previews);
                    var run = await Required<PreviewRunService>().ReadAsync(scope.Actor, scope.Organization, runId, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    if (run.RunId != runId || run.Revision.OrganizationId != scope.Organization) throw new InvalidOperationException("Preview read scope mismatch.");
                    var data = new
                    {
                        kind,
                        runId = Id(run.RunId),
                        revisionId = Id(run.Revision.RevisionId),
                        mappingId = Id(run.Revision.MappingId),
                        state = Name(run.State),
                        outcome = run.Outcome is { } outcome ? Name(outcome) : null,
                        attempt = run.Attempt,
                        maxAttempts = run.MaxAttempts,
                        cancelRequested = run.CancelRequested,
                        failure = run.Failure is not null ? new { detailsOmitted = true } : null,
                        recordsWritten = run.RecordsWritten,
                        outputs = run.Outputs.Select(output => new
                        {
                            output.Ordinal,
                            boundaryId = output.BoundaryId,
                            output.TargetName,
                            output.MediaType,
                            output.RecordsWritten,
                            bytes = output.ByteCount
                        }).ToArray(),
                        createdAt = run.CreatedAtUtc,
                        finishedAt = run.FinishedAtUtc,
                        expiresAt = run.ExpiresAtUtc
                    };
                    return Single(call.Name, scope.Budget, data, [ChatWorkspaceCitationFact.PreviewRun(run.Revision, run.RunId)]);
                }
            case "regressionRun":
                {
                    RequireFeature(features.Regressions);
                    var run = await Required<IRegressionRunMetadataReader>().ReadMetadataAsync(scope.Actor, scope.Organization, runId, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    if (run.RunId != runId || run.Revision.OrganizationId != scope.Organization)
                        throw new InvalidOperationException("Regression metadata scope mismatch.");
                    var mapping = new MappingScope(scope.Organization, run.Revision.ProjectId, run.Revision.MappingId);
                    if (!await CatalogEndpoints.RegressionRunAccessAsync(services, scope.Actor, mapping, token))
                        throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var data = new
                    {
                        kind,
                        runId = Id(runId),
                        caseId = Id(run.CaseId),
                        caseVersion = run.Version,
                        revisionId = Id(run.Revision.RevisionId),
                        mappingId = Id(mapping.MappingId),
                        state = Name(run.State),
                        attempt = run.Attempt,
                        createdAt = run.CreatedAt,
                        completionDetails = "Stored completion includes sample data; use the run view for comparison details."
                    };
                    return Single(call.Name, scope.Budget, data, [ChatWorkspaceCitationFact.RegressionRun(mapping, runId)]);
                }
            default:
                {
                    RequireFeature(features.Builds);
                    var snapshot = await Required<IBuildRunReader>().ReadAsync(scope.Actor, scope.Organization, runId, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    var mapping = new MappingScope(scope.Organization, snapshot.Intent.Revision.ProjectId, snapshot.Intent.Revision.MappingId);
                    var build = await CatalogEndpoints.BuildRunAsync(services, scope.Actor, mapping, runId, token)
                        ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
                    return Single(call.Name, scope.Budget, new { kind, build = CatalogBuildMapping.Run(build) },
                        [ChatWorkspaceCitationFact.BuildRun(mapping, runId)]);
                }
        }
    }

    /// <summary>What the caller can do with a mapping on this server, each family read through its own capability route.</summary>
    private async Task<ChatReadOnlyToolResult> GetCapabilitiesAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var read = await CatalogEndpoints.ReadableMappingAsync(services, scope.Actor, mapping, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        object? regression = null, builds = null, environments = null;
        if (features.Regressions && Optional<CatalogRegressionCaseOptions>() is { Enabled: true } caseOptions &&
            await CatalogEndpoints.RegressionCapabilitiesAsync(services, scope.Actor, mapping, caseOptions, token) is { } cases)
            regression = new { canReadCaseSamples = cases.ReadSamples, canPublishCases = cases.PublishCases, canRunCases = cases.ReadSamples };
        if (features.Builds && Optional<CatalogBuildOptions>() is { Enabled: true } buildOptions)
        {
            var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
            builds = await CatalogEndpoints.BuildCapabilitiesAsync(services, scope.Actor, mapping, buildOptions, token) is { } capabilities
                ? new { canReadOwnBuilds = capabilities.ReadOwnMetadata, canApprove = capabilities.ApproveBuilds, canSubmit = capabilities.SubmitBuilds }
                : new { canReadOwnBuilds = false, canApprove = false, canSubmit = false };
            environments = new
            {
                canRead = await CatalogEndpoints.BuildProjectAccessAsync(services, scope.Actor, project, token),
                canCreateVersions = await CatalogEndpoints.BuildProjectAccessAsync(services, scope.Actor, project, token, administrator: true)
            };
        }
        var data = new
        {
            mappingId = Id(mapping.MappingId),
            name = read.Summary.Name,
            role = Role(read.Access),
            previews = features.Previews ? new { canRun = read.Access.Can(ProjectAction.SubmitPreview) } : null,
            regression,
            builds,
            buildEnvironments = environments,
            canUploadRevisions = read.Access.Can(ProjectAction.UploadRevision),
            canEditMetadata = read.Access.Can(ProjectAction.EditMetadata)
        };
        return Single(call.Name, scope.Budget, data, [ChatWorkspaceCitationFact.Mapping(mapping)]);
    }

    private static object Page<T>(string kind, MappingScope mapping, IReadOnlyList<T> kept, int listed, Guid? next) =>
        new { kind, mappingId = Id(mapping.MappingId), items = kept, nextAfter = kept.Count == listed && next is { } id ? Id(id) : null };

    private static RevisionScope Revision(MappingScope mapping, string revisionId) =>
        new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, Guid.Parse(revisionId));

    private static void RequireFeature(bool enabled)
    {
        if (!enabled) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);
    }

    private static string Name<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private sealed record PreviewItem(string RunId, string RevisionId, string State, string? Outcome, DateTimeOffset CreatedAt,
        DateTimeOffset? FinishedAt, DateTimeOffset ExpiresAt);
    private sealed record CaseItem(string CaseId, long Version, string RevisionId, string Name, DateTimeOffset CreatedAt);
    private sealed record RegressionRunItem(string RunId, string CaseId, long CaseVersion, string RevisionId, string State, int Attempt,
        DateTimeOffset CreatedAt);
}
