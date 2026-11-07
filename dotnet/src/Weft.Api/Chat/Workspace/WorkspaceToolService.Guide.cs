using Weft.Application;
using Weft.Domain;

namespace Weft.Api.Chat.Workspace;

/// <summary>One section of the user guide shipped with the server: a stable identifier, its title and its plain text.</summary>
public sealed record WorkspaceGuideSection(string Id, string Title, string Text)
{
    public override string ToString() => nameof(WorkspaceGuideSection);
}

/// <summary>The embedded user guide, read in process; nothing is fetched at run time.</summary>
public interface IWorkspaceUserGuide
{
    IReadOnlyList<WorkspaceGuideSection> Sections { get; }
}

public sealed partial class WorkspaceToolService
{
    private const int GuideExcerptCharacters = 300;

    private Task<ChatReadOnlyToolResult> SearchGuideAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sections = Guide.Sections;
        var query = call.Text("query", 128);
        var limit = (int)call.Count("limit", 1, WorkspaceToolRegistry.MaximumItems, 5);
        var hits = sections.Where(section => GuideMatches(section.Title, query) || GuideMatches(section.Text, query))
            .Select(section => new GuideHit(section.Id, section.Title, GuideExcerpt(section.Text, query))).ToArray();
        var listed = hits.Take(limit).ToArray();
        return Task.FromResult(Reply(call.Name, scope.Budget, listed, hits.Length > limit, kept => new { hits = kept },
            item => [ChatWorkspaceCitationFact.GuideSection(scope.Organization, item.SectionId)]));
    }

    private Task<ChatReadOnlyToolResult> GetGuideSectionAsync(CallScope scope, WorkspaceToolCall call, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = call.Text("sectionId", 64);
        var section = Guide.Sections.FirstOrDefault(item => item.Id == id) ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.NotFound);
        // A long section keeps its longest leading text that fits the call budget and says so.
        var characters = section.Text.Length;
        while (true)
        {
            var text = section.Text[..characters];
            try
            {
                return Task.FromResult(Single(call.Name, scope.Budget, new
                {
                    sectionId = section.Id,
                    title = section.Title,
                    text,
                    textTruncated = characters < section.Text.Length
                }, [ChatWorkspaceCitationFact.GuideSection(scope.Organization, section.Id)]));
            }
            catch (WorkspaceToolRefusal refusal) when (refusal.Outcome == WorkspaceToolOutcome.ResourceLimit && characters > 0)
            {
                characters /= 2;
                if (characters > 0 && char.IsHighSurrogate(section.Text[characters - 1])) characters--;
            }
        }
    }

    private IWorkspaceUserGuide Guide => guide ?? throw new WorkspaceToolRefusal(WorkspaceToolOutcome.Unavailable);

    private static bool GuideMatches(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string GuideExcerpt(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, (index < 0 ? 0 : index) - GuideExcerptCharacters / 3);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        var end = Math.Min(text.Length, start + GuideExcerptCharacters);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        return text[start..end];
    }

    private sealed record GuideHit(string SectionId, string Title, string Excerpt);
}
