using System.Text.Json;
using Braid.Ai;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

public sealed class WorkspaceSearchToolTests
{
    [Fact]
    public async Task Shared_search_keeps_organization_scope_pages_and_exact_retained_resource_citations()
    {
        var fixture = new WorkspaceToolFixture { ExtraProjects = 1 };
        var service = fixture.Service();
        var other = new ProjectScope(WorkspaceToolFixture.Organization, Guid.Parse("00000001-0000-4000-8000-000000000000"));
        var mapping = Guid.Parse("76767676-7676-4767-8767-767676767676");
        var report = fixture.Reports.Projection.Metadata.DerivationId;
        fixture.SearchHits = [
            new(new(CatalogSearchKind.Mapping, other, mapping), new("Other project's mapping"), new("Matches the shared search"), Guid.NewGuid()),
            new(new(CatalogSearchKind.Field, WorkspaceToolFixture.Project, WorkspaceToolFixture.Mapping.MappingId,
                WorkspaceToolFixture.Head.RevisionId, "field.out", report), new("Total"), new("Output/total")),
            new(new(CatalogSearchKind.Rule, WorkspaceToolFixture.Project, WorkspaceToolFixture.Mapping.MappingId,
                WorkspaceToolFixture.Head.RevisionId, "rule.first", report), new("Choose incoming value"), new("Output/total"))
        ];
        var conversation = fixture.Conversation(new(WorkspaceToolFixture.Mapping, WorkspaceToolFixture.Head.RevisionId));
        var context = fixture.Context(service, conversation: conversation);
        string Arguments(string? cursor) => JsonSerializer.Serialize(new { query = " café ", limit = 1, cursor });
        // Omit null rather than sending a value outside the strict tool schema.
        var first = await fixture.CallAsync(service, "search_catalog", "{\"query\":\" café \",\"limit\":1}", context);
        Assert.Equal("available", first.Outcome);
        Assert.Equal("café", first.Data.GetProperty("query").GetString());
        Assert.Equal(other.ProjectId.ToString("D"), Assert.Single(first.Data.GetProperty("hits").EnumerateArray()).GetProperty("projectId").GetString());
        Assert.True(first.Data.GetProperty("hasMore").GetBoolean());
        var cursor = first.Data.GetProperty("nextCursor").GetString()!;
        var second = await fixture.CallAsync(service, "search_catalog", Arguments(cursor), context);
        var field = Assert.Single(second.Result.WorkspaceCitations);
        Assert.Equal(ChatWorkspaceCitationKind.Report, field.Kind);
        Assert.Equal("field.out", field.Target!.ResourceId);
        Assert.Equal(report, field.Report!.DerivationId);
        Assert.Single(await service.VerifyAsync(WorkspaceToolFixture.Actor, conversation, [field], CancellationToken.None));
        var third = await fixture.CallAsync(service, "search_catalog", Arguments(second.Data.GetProperty("nextCursor").GetString()), context);
        Assert.Equal("rule.first", Assert.Single(third.Result.WorkspaceCitations).Target!.ResourceId);
        Assert.False(third.Data.GetProperty("hasMore").GetBoolean());
        Assert.All(fixture.SearchReads, call =>
        {
            Assert.Equal(WorkspaceToolFixture.Actor, call.Actor);
            Assert.Equal(WorkspaceToolFixture.Organization, call.Organization);
            Assert.Equal("café", call.Query.Text);
            Assert.Equal(1, call.Limit);
        });
        Assert.DoesNotContain(fixture.CatalogReads, read => read is "projects" or "mappings");
        var before = fixture.SearchReads.Count;
        Assert.Equal("invalidCall", (await fixture.CallAsync(service, "search_catalog",
            JsonSerializer.Serialize(new { query = "different", cursor }))).Outcome);
        Assert.Equal(before, fixture.SearchReads.Count);
    }

    [Fact]
    public async Task A_retained_hit_that_loses_authority_is_not_released_as_uncited_metadata()
    {
        var fixture = new WorkspaceToolFixture();
        var service = fixture.Service();
        fixture.SearchHits = [new(new(CatalogSearchKind.Field, WorkspaceToolFixture.Project, WorkspaceToolFixture.Mapping.MappingId,
            WorkspaceToolFixture.Head.RevisionId, "field.out", fixture.Reports.Projection.Metadata.DerivationId),
            new("stale-private-hit"), new("stale-private-description"))];
        fixture.Reports.Missing = true;
        var result = await fixture.CallAsync(service, "search_catalog", "{\"query\":\"stale\"}");
        Assert.Equal("notFound", result.Outcome);
        Assert.DoesNotContain("stale-private", result.Json, StringComparison.Ordinal);
        Assert.Empty(result.Result.WorkspaceCitations);
    }

    [Fact]
    public async Task Search_never_returns_a_cursor_that_skips_results_cropped_by_the_tool_budget()
    {
        var fixture = new WorkspaceToolFixture();
        var service = fixture.Service();
        var result = await fixture.CallAsync(service, "search_catalog", "{\"query\":\"order\",\"limit\":1}",
            fixture.Context(service, remainingNodes: 0));
        Assert.Equal("available", result.Outcome);
        Assert.True(result.Truncated);
        Assert.Empty(result.Data.GetProperty("hits").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("nextCursor").ValueKind);
        Assert.Empty(result.Result.WorkspaceCitations);
    }

    [Fact]
    public async Task Shared_search_authority_and_pending_index_failures_are_not_replaced_by_a_local_scan()
    {
        var fixture = new WorkspaceToolFixture();
        var service = fixture.Service();
        fixture.SearchFailure = new CatalogSearchIndexPendingException();
        Assert.Equal("unavailable", (await fixture.CallAsync(service, "search_catalog", "{\"query\":\"order\"}")).Outcome);
        Assert.DoesNotContain(fixture.CatalogReads, read => read is "projects" or "mappings");
        fixture.SearchFailure = null;
        fixture.Role = null;
        var denied = await fixture.CallAsync(service, "search_catalog", "{\"query\":\"order\"}");
        Assert.Empty(denied.Data.GetProperty("hits").EnumerateArray());
        Assert.Empty(denied.Result.WorkspaceCitations);
        Assert.False(await service.ValidateAsync(fixture.Context(service), AiToolIntent.Create("invalid",
            "search_catalog", AiJsonObject.CopyFrom(JsonSerializer.Serialize(new { query = new string('é', 129) }))), CancellationToken.None));
    }
}
