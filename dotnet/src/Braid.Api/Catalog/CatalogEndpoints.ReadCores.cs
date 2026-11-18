using Microsoft.Extensions.DependencyInjection;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Contracts.Builds;
using Braid.Contracts.Regression;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Api.Catalog;

/// <summary>
/// The authorized read sequences behind the regression and build GET routes, shared with the workspace chat tools
/// so both observe the same fresh authority before and after each read. Null means absent, foreign or unreadable.
/// </summary>
public static partial class CatalogEndpoints
{
    internal static async Task<CatalogMapping?> ReadableMappingAsync(IServiceProvider services, ActorContext actor, MappingScope scope,
        CancellationToken ct)
    {
        var result = await services.GetRequiredService<ICatalogReader>().GetMappingAsync(actor, scope, ct);
        if (result is not null)
        {
            RequireScope(result.Summary.Scope == scope);
            RequireAccess(result.Access, actor, Project(scope));
        }
        return result;
    }

    internal static async Task<CatalogRevision?> ReadableRevisionAsync(IServiceProvider services, ActorContext actor, RevisionScope scope,
        CancellationToken ct)
    {
        var result = await services.GetRequiredService<ICatalogReader>().GetRevisionAsync(actor, scope, ct);
        if (result is not null)
        {
            RequireScope(result.Summary.Scope == scope);
            RequireAccess(result.Access, actor, new(scope.OrganizationId, scope.ProjectId));
        }
        return result;
    }

    internal static async Task<RegressionCaseCapabilitiesV1?> RegressionCapabilitiesAsync(IServiceProvider services, ActorContext actor,
        MappingScope mapping, CatalogRegressionCaseOptions options, CancellationToken ct)
    {
        if (await ReadableMappingAsync(services, actor, mapping, ct) is null) return null;
        var fresh = await ReadableMappingAsync(services, actor, mapping, ct);
        if (fresh is null) return null;
        return new RegressionCaseCapabilitiesV1(Runtime(options.Runtime!), Limits(PreviewLimits.Default),
            RegressionComparisonService.ExactBytesPolicy, CanReadCase(fresh.Access),
            CanPublishCase(fresh.Access) && fresh.Summary.BackendId == options.Runtime!.BackendId,
            options.DocumentRuntime is { } documentRuntime ? Runtime(documentRuntime) : null,
            options.DocumentRuntime is not null || options.CsvRuntime is not null
                ? new[] { RegressionComparisonService.ExactBytesPolicy }
                    .Concat(options.DocumentRuntime is null ? [] : new[] { RegressionComparisonService.EdiSegmentsPolicy })
                    .Concat(options.CsvRuntime is null ? [] : new[] { RegressionComparisonService.CsvRowsPolicy }).ToArray()
                : null,
            options.CsvRuntime is { } csvRuntime ? Runtime(csvRuntime) : null,
            options.XmlRuntime is { } xmlRuntime ? Runtime(xmlRuntime) : null, options.NativeRuntimes.Count == 0 ? null : options.NativeRuntimes.Select(Runtime).ToArray());
    }

    internal static async Task<RegressionCasePage?> RegressionCasePageAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        int limit, Guid? afterCaseId, CancellationToken ct)
    {
        if (await ReadableMappingAsync(services, actor, mapping, ct) is null) return null;
        var page = await services.GetRequiredService<IRegressionCaseReader>().ListAsync(actor, mapping, limit, afterCaseId, ct);
        if (page.Cases.Count > limit || page.Cases.Any(item => !Belongs(item.Revision, mapping))) throw new InvalidOperationException("Invalid regression metadata scope.");
        return await ReadableMappingAsync(services, actor, mapping, ct) is null ? null : page;
    }

    /// <summary>An exact saved case version's private sample content, under fresh Runner-or-higher authority.</summary>
    internal static async Task<RegressionCaseVersion?> RegressionCaseVersionAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        Guid caseId, long version, CancellationToken ct)
    {
        var current = await ReadableMappingAsync(services, actor, mapping, ct);
        if (current is null || !CanReadCase(current.Access)) return null;
        var value = await services.GetRequiredService<IRegressionCaseReader>().ReadAsync(actor, mapping, caseId, version, ct);
        if (value is null) return null;
        if (value.CaseId != caseId || value.Version != version || !Belongs(value.Revision, mapping)) throw new InvalidOperationException("Invalid regression version scope.");
        var revision = await ReadableRevisionAsync(services, actor, value.Revision, ct);
        if (revision is null || !CanReadCase(revision.Access)) return null;
        if (revision.Summary.NativeDigest != value.NativeDigest || revision.Summary.BackendId != value.Runtime.BackendId)
            throw new InvalidOperationException("Invalid regression native identity.");
        var fresh = await ReadableRevisionAsync(services, actor, value.Revision, ct);
        if (fresh is null || !CanReadCase(fresh.Access)) return null;
        RequireScope(fresh.Summary == revision.Summary);
        return value;
    }

    internal static async Task<bool> RegressionRunAccessAsync(IServiceProvider services, ActorContext actor, MappingScope mapping, CancellationToken ct) =>
        await ReadableMappingAsync(services, actor, mapping, ct) is { } current && CanReadCase(current.Access);

    internal static async Task<RegressionRunPage?> RegressionRunPageAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        int limit, Guid? afterRunId, CancellationToken ct)
    {
        if (!await RegressionRunAccessAsync(services, actor, mapping, ct)) return null;
        var page = await services.GetRequiredService<IRegressionRunReader>().ListAsync(actor, mapping, limit, afterRunId, ct);
        if (page.Runs.Count > limit || page.Runs.Any(run => !Belongs(run.Revision, mapping))) throw new InvalidOperationException("Run history scope mismatch.");
        return await RegressionRunAccessAsync(services, actor, mapping, ct) ? page : null;
    }

    /// <summary>The submitter's retained run with its recomputed result, rechecking sample authority before delivery.</summary>
    internal static async Task<RegressionRunView?> RegressionRunViewAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        Guid runId, CancellationToken ct)
    {
        if (!await RegressionRunAccessAsync(services, actor, mapping, ct)) return null;
        var view = await services.GetRequiredService<RegressionRunReadService>().ReadAsync(actor, mapping.OrganizationId, runId, ct);
        if (view is null || !Belongs(view.Run.Case.Revision, mapping)) return null;
        return await RegressionRunAccessAsync(services, actor, mapping, ct) ? view : null;
    }

    internal static async Task<bool> BuildMappingAccessAsync(IServiceProvider services, ActorContext actor, MappingScope mapping, CancellationToken ct) =>
        await ReadableMappingAsync(services, actor, mapping, ct) is { } current && current.Access.Can(ProjectAction.UploadRevision);

    internal static async Task<bool> BuildProjectAccessAsync(IServiceProvider services, ActorContext actor, ProjectScope project,
        CancellationToken ct, bool administrator = false)
    {
        var current = await services.GetRequiredService<ICatalogReader>().GetProjectAsync(actor, project, ct);
        if (current is null) return false;
        RequireScope(current.Summary.Scope == project);
        RequireAccess(current.Access, actor, project);
        return administrator ? current.Access.Role == ProjectRole.Administrator : current.Access.Can(ProjectAction.UploadRevision);
    }

    internal static async Task<BuildCapabilitiesV1?> BuildCapabilitiesAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        CatalogBuildOptions options, CancellationToken ct)
    {
        var current = await ReadableMappingAsync(services, actor, mapping, ct);
        if (current is null || !current.Access.Can(ProjectAction.UploadRevision)) return null;
        var supported = current.Summary.BackendId == options.Backend!.BackendId;
        var response = new BuildCapabilitiesV1(CatalogBuildMapping.Backend(options.Backend), CatalogBuildMapping.Profile(options.Profile!), true, supported, supported,
            options.DocumentBackend is { } documentBackend ? CatalogBuildMapping.Backend(documentBackend) : null,
            options.DocumentProfile is { } documentProfile ? CatalogBuildMapping.Profile(documentProfile) : null,
            options.InterchangeProfile is { } interchangeProfile ? CatalogBuildMapping.Profile(interchangeProfile) : null,
            options.CsvBackend is { } csvBackend ? CatalogBuildMapping.Backend(csvBackend) : null,
            options.CsvProfile is { } csvProfile ? CatalogBuildMapping.Profile(csvProfile) : null);
        return await BuildMappingAccessAsync(services, actor, mapping, ct) ? response : null;
    }

    internal static async Task<BuildEnvironmentPage?> BuildEnvironmentPageAsync(IServiceProvider services, ActorContext actor, ProjectScope project,
        int maximum, BuildEnvironmentCursor? cursor, CancellationToken ct)
    {
        if (!await BuildProjectAccessAsync(services, actor, project, ct)) return null;
        var page = await services.GetRequiredService<IBuildMetadataReader>().ListEnvironmentsAsync(actor, project, maximum, cursor, ct);
        if (page is null) return null;
        if (page.Environments.Count > maximum || page.Environments.Any(value => value.Environment.Reference.Project != project)) throw new InvalidOperationException("Build environment page scope mismatch.");
        return await BuildProjectAccessAsync(services, actor, project, ct) ? page : null;
    }

    internal static async Task<RetainedBuildEnvironment?> BuildEnvironmentAsync(IServiceProvider services, ActorContext actor,
        BuildEnvironmentReference reference, CancellationToken ct)
    {
        if (!await BuildProjectAccessAsync(services, actor, reference.Project, ct)) return null;
        var value = await services.GetRequiredService<IBuildMetadataReader>().ReadEnvironmentAsync(actor, reference, ct);
        if (value is null) return null;
        if (value.Environment.Reference != reference) throw new InvalidOperationException("Build environment scope mismatch.");
        return await BuildProjectAccessAsync(services, actor, reference.Project, ct) ? value : null;
    }

    internal static async Task<BuildApprovalPage?> BuildApprovalPageAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        int maximum, Guid? after, CancellationToken ct)
    {
        if (!await BuildMappingAccessAsync(services, actor, mapping, ct)) return null;
        var page = await services.GetRequiredService<IBuildMetadataReader>().ListApprovalsAsync(actor, mapping, maximum, after, ct);
        if (page is null) return null;
        if (page.Approvals.Count > maximum) throw new InvalidOperationException("Build approval page bound refused.");
        foreach (var value in page.Approvals) CheckBuildApproval(value, actor.PrincipalId, mapping, value.Intent.ApprovalId);
        return await BuildMappingAccessAsync(services, actor, mapping, ct) ? page : null;
    }

    internal static async Task<RetainedBuildApproval?> BuildApprovalAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        Guid id, CancellationToken ct)
    {
        if (!await BuildMappingAccessAsync(services, actor, mapping, ct)) return null;
        var value = await services.GetRequiredService<IBuildMetadataReader>().ReadApprovalAsync(actor, mapping, id, ct);
        if (value is null) return null;
        CheckBuildApproval(value, actor.PrincipalId, mapping, id);
        return await BuildMappingAccessAsync(services, actor, mapping, ct) ? value : null;
    }

    internal static async Task<BuildRunPage?> BuildRunPageAsync(IServiceProvider services, ActorContext actor, MappingScope mapping,
        int maximum, Guid? after, CancellationToken ct)
    {
        if (!await BuildMappingAccessAsync(services, actor, mapping, ct)) return null;
        var page = await services.GetRequiredService<IBuildMetadataReader>().ListRunsAsync(actor, mapping, maximum, after, ct);
        if (page is null) return null;
        if (page.Runs.Count > maximum || page.Runs.Any(value => value.Actor != actor.PrincipalId || !Belongs(value.Intent.Revision, mapping))) throw new InvalidOperationException("Build history scope mismatch.");
        return await BuildMappingAccessAsync(services, actor, mapping, ct) ? page : null;
    }

    internal static async Task<BuildRunSnapshot?> BuildRunAsync(IServiceProvider services, ActorContext actor, MappingScope mapping, Guid id,
        CancellationToken ct)
    {
        if (!await BuildMappingAccessAsync(services, actor, mapping, ct)) return null;
        var value = await services.GetRequiredService<IBuildRunReader>().ReadAsync(actor, mapping.OrganizationId, id, ct);
        if (value is null || !Belongs(value.Intent.Revision, mapping)) return null;
        CheckBuildRun(value, actor.PrincipalId, mapping, id);
        return await BuildMappingAccessAsync(services, actor, mapping, ct) ? value : null;
    }
}
