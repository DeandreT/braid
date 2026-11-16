using Braid.Application;
using Braid.Domain;

namespace Braid.Api.Chat.Workspace;

public sealed partial class WorkspaceToolService
{
    /// <summary>Descriptions longer than this are cut with an explicit flag; names are never cut.</summary>
    private const int DescriptionCharacters = 400;

    private async Task<ChatReadOnlyToolResult> ListProjectsAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 10);
        var after = call.OptionalId("afterProjectId");
        var page = await Catalog.ListProjectsAsync(scope.Actor, scope.Organization, limit,
            after is { } id ? new CatalogProjectCursor(scope.Organization, id) : null, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var projects = page.Projects.Select(Project).ToArray();
        return Reply(call.Name, scope.Budget, projects, false,
            kept => new { projects = kept, nextAfterProjectId = kept.Count == projects.Length && page.NextCursor is { } next ? Id(next.AfterProjectId) : null },
            item => [ChatWorkspaceCitationFact.Project(new(scope.Organization, Guid.Parse(item.ProjectId)))]);
    }

    /// <summary>
    /// The same authorized query service as catalog search. Focus supplies no filter or authority.
    /// </summary>
    private async Task<ChatReadOnlyToolResult> SearchCatalogAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        CatalogSearchQuery query;
        CatalogSearchCursor? cursor;
        try
        {
            query = new CatalogSearchQuery(call.Text("query", 256));
            cursor = call.OptionalText("cursor", 4096) is { } encoded
                ? CatalogSearchCursorCodec.Decode(encoded, scope.Organization, query) : null;
        }
        catch (ArgumentException) { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall); }
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 10);
        var page = await Required<ICatalogSearch>().SearchAsync(scope.Actor, scope.Organization, query, limit, cursor, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        if (page.Admission.PrincipalId != scope.Actor.PrincipalId || page.Admission.OrganizationId != scope.Organization ||
            page.Query != query || page.Hits.Count > limit) throw new InvalidOperationException("Search scope mismatch.");
        var items = new List<SearchItem>();
        foreach (var hit in page.Hits)
        {
            var key = hit.Key;
            ChatWorkspaceCitationFact? citation = key.Kind switch
            {
                CatalogSearchKind.Project => ChatWorkspaceCitationFact.Project(key.Project),
                CatalogSearchKind.Mapping => ChatWorkspaceCitationFact.Mapping(new(scope.Organization, key.Project.ProjectId, key.MappingId!.Value)),
                CatalogSearchKind.Revision => ChatWorkspaceCitationFact.Revision(new(scope.Organization, key.Project.ProjectId, key.MappingId!.Value, key.RevisionId!.Value)),
                CatalogSearchKind.Field or CatalogSearchKind.Rule => await SearchReportCitationAsync(scope, key, token),
                _ => throw new InvalidOperationException("Unknown search resource.")
            };
            items.Add(new(new
            {
                kind = Name(key.Kind),
                projectId = Id(key.Project.ProjectId),
                mappingId = key.MappingId is { } mapping ? Id(mapping) : null,
                revisionId = hit.TargetRevisionId is { } revision ? Id(revision) : null,
                reportDerivationId = key.ProjectionDerivationId is { } report ? Id(report) : null,
                resourceId = key.ResourceId,
                title = hit.Title.Text,
                description = hit.Description.Text
            }, citation));
        }
        return Reply(call.Name, scope.Budget, items, false,
            kept => new
            {
                query = query.Text,
                hits = kept.Select(item => item.Data).ToArray(),
                hasMore = page.NextCursor is not null || kept.Count < items.Count,
                nextCursor = kept.Count == items.Count && page.NextCursor is { } next ? CatalogSearchCursorCodec.Encode(next) : null
            },
            item => item.Citation is { } citation ? [citation] : []);
    }

    private async Task<ChatWorkspaceCitationFact> SearchReportCitationAsync(CallScope scope, CatalogSearchKey key, CancellationToken token)
    {
        var revision = new RevisionScope(scope.Organization, key.Project.ProjectId, key.MappingId!.Value, key.RevisionId!.Value);
        var read = await Required<IRevisionReportReader>().GetRetainedAsync(scope.Actor, revision, RevisionReportOperation.Projection,
            key.ProjectionDerivationId!.Value, cancellationToken: token);
        if (read.Report is not VerifiedProjectionReport report || read.Source?.Actor != scope.Actor.PrincipalId ||
            report.Metadata.Key.Scope != revision || report.Metadata.DerivationId != key.ProjectionDerivationId)
            throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        return ChatWorkspaceCitationFact.ReportResource(revision, MappingToolServicePin(report),
            new(key.Kind == CatalogSearchKind.Field ? ChatCitationKind.Field : ChatCitationKind.Rule, key.ResourceId));
    }

    private async Task<ChatReadOnlyToolResult> GetProjectAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var project = new ProjectScope(scope.Organization, call.Id("projectId"));
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 10);
        var after = call.OptionalId("afterMappingId");
        var read = await Catalog.GetProjectAsync(scope.Actor, project, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var page = await Catalog.ListMappingsAsync(scope.Actor, project, limit, after is { } id ? new CatalogMappingCursor(project, id) : null, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var mappings = page.Mappings.Select(Mapping).ToArray();
        var summary = Project(read);
        return Reply(call.Name, scope.Budget, mappings, false,
            kept => new { project = summary, mappings = kept, nextAfterMappingId = kept.Count == mappings.Length && page.NextCursor is { } next ? Id(next.AfterMappingId) : null },
            item => [ChatWorkspaceCitationFact.Mapping(new(scope.Organization, project.ProjectId, Guid.Parse(item.MappingId)))],
            extraNodes: 1, retainedCitations: [ChatWorkspaceCitationFact.Project(project)]);
    }

    private async Task<ChatReadOnlyToolResult> GetMappingAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var read = await Catalog.GetMappingAsync(scope.Actor, mapping, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var project = await Catalog.GetProjectAsync(scope.Actor, new(mapping.OrganizationId, mapping.ProjectId), token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var data = new
        {
            mapping = Mapping(read.Summary),
            project = new { projectId = Id(mapping.ProjectId), name = project.Summary.Name },
            role = Role(read.Access),
            backendId = read.Summary.BackendId,
            createdAt = read.Summary.CreatedAtUtc,
            updatedAt = read.Summary.UpdatedAtUtc
        };
        return Single(call.Name, scope.Budget, data,
            [ChatWorkspaceCitationFact.Mapping(mapping), ChatWorkspaceCitationFact.Revision(read.Summary.Head.Scope)]);
    }

    private async Task<ChatReadOnlyToolResult> ListRevisionsAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var mapping = await LocateMappingAsync(scope, call.Id("mappingId"), token);
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 10);
        var before = call.Has("beforeSequence") ? call.Count("beforeSequence", 2, 9_007_199_254_740_991, 0) : (long?)null;
        var page = await Catalog.ListRevisionsAsync(scope.Actor, mapping, limit, before is { } sequence ? new CatalogRevisionCursor(mapping, sequence) : null, token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var revisions = page.Revisions.Select(Revision).ToArray();
        return Reply(call.Name, scope.Budget, revisions, false,
            kept => new { mappingId = Id(mapping.MappingId), revisions = kept, nextBeforeSequence = kept.Count == revisions.Length ? page.NextCursor?.BeforeSequence : null },
            item => [ChatWorkspaceCitationFact.Revision(new(scope.Organization, mapping.ProjectId, mapping.MappingId, Guid.Parse(item.RevisionId)))]);
    }

    private async Task<ChatReadOnlyToolResult> GetRevisionAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        var revision = await LocateRevisionAsync(scope, call.Id("revisionId"), token);
        var read = await Catalog.GetRevisionAsync(scope.Actor, revision, token) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var mapping = await Catalog.GetMappingAsync(scope.Actor, new(revision.OrganizationId, revision.ProjectId, revision.MappingId), token)
            ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        var data = new
        {
            revision = Revision(read.Summary),
            mapping = new { mappingId = Id(revision.MappingId), name = mapping.Summary.Name, headRevisionId = Id(mapping.Summary.Head.Scope.RevisionId) },
            isHead = mapping.Summary.Head.Scope == revision,
            nativeDigest = read.Summary.NativeDigest.Value,
            nativeBytes = read.Summary.NativeByteCount,
            backendId = read.Summary.BackendId
        };
        return Single(call.Name, scope.Budget, data,
            [ChatWorkspaceCitationFact.Revision(revision), ChatWorkspaceCitationFact.Mapping(new(revision.OrganizationId, revision.ProjectId, revision.MappingId))]);
    }

    private static ProjectItem Project(CatalogProject project) => new(Id(project.Summary.Scope.ProjectId), project.Summary.Name,
        Excerpt(project.Summary.Description), project.Summary.Description.Length > DescriptionCharacters, Role(project.Access));

    private static MappingItem Mapping(CatalogMappingSummary mapping) => new(Id(mapping.Scope.MappingId), mapping.Name,
        Excerpt(mapping.Description), mapping.Description.Length > DescriptionCharacters, Id(mapping.Head.Scope.RevisionId), mapping.Head.Sequence);

    private static RevisionItem Revision(CatalogRevisionSummary revision) => new(Id(revision.Scope.RevisionId), revision.Sequence,
        revision.CommittedAtUtc, revision.OriginalFilename,
        revision.Admission == IngestionAdmissionClassification.EligibleValid ? "valid" : "decodedInvalid");

    private static string Role(ProjectAccess access) => access.Role switch
    {
        ProjectRole.Viewer => "viewer",
        ProjectRole.Runner => "runner",
        ProjectRole.Publisher => "publisher",
        ProjectRole.Administrator => "administrator",
        _ => throw new InvalidOperationException("The project role is unknown.")
    };

    private static string Excerpt(string description)
    {
        if (description.Length <= DescriptionCharacters) return description;
        var end = DescriptionCharacters;
        // Never split a surrogate pair.
        if (char.IsHighSurrogate(description[end - 1])) end--;
        return description[..end];
    }

    private sealed record ProjectItem(string ProjectId, string Name, string Description, bool DescriptionTruncated, string Role);
    private sealed record MappingItem(string MappingId, string Name, string Description, bool DescriptionTruncated, string HeadRevisionId, long HeadSequence);
    private sealed record RevisionItem(string RevisionId, long Sequence, DateTimeOffset CommittedAt, string OriginalFilename, string Admission);
    private sealed record SearchItem(object Data, ChatWorkspaceCitationFact? Citation);
}
