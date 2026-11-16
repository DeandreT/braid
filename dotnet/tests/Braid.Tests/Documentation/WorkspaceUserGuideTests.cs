using System.Diagnostics.CodeAnalysis;
using Braid.Api.Chat.Workspace;
using Braid.Api.Help;
using Braid.Application;
using Braid.Contracts.Help;

namespace Braid.Tests;

public sealed class WorkspaceUserGuideTests
{
    [Fact]
    public async Task The_shipped_guide_is_readable_and_searchable_through_the_workspace_tools()
    {
        var reader = new WorkspaceUserGuide(UserGuide.Embedded);
        Assert.Equal(UserGuide.Embedded.Contents.Sections.Select(section => section.Id), reader.Sections.Select(section => section.Id));
        Assert.All(reader.Sections, section => Assert.Contains(section.Title, section.Text, StringComparison.Ordinal));
        var fixture = new WorkspaceToolFixture();
        var service = fixture.Service(reader: reader);
        var section = await fixture.CallAsync(service, "get_user_guide_section", "{\"sectionId\":\"overview\"}");
        Assert.Equal("available", section.Outcome);
        Assert.Equal("overview", section.Data.GetProperty("sectionId").GetString());
        Assert.Equal("overview", Assert.Single(section.Result.WorkspaceCitations).SectionId);
        var search = await fixture.CallAsync(service, "search_user_guide", "{\"query\":\"workbench\"}");
        Assert.Equal("available", search.Outcome);
        Assert.NotEmpty(search.Data.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public void All_supported_structured_content_becomes_text_without_interpreting_it()
    {
        HelpInlineV1[] inline = [new HelpTextV1("plain "), new HelpStrongV1("strong "), new HelpEmphasisV1("emphasis "),
            new HelpCodeV1("code "), new HelpLinkV1("guide", "overview", "start")];
        var source = new HelpSectionV1("overview", "Guide title", "Summary", [new HelpHeadingV1(2, "start", "Heading"),
            new HelpParagraphV1(inline), new HelpListV1(false, [new(inline)]),
            new HelpTableV1([new(inline)], [new([new(inline)])]), new HelpCodeBlockV1("<untrusted>")]);
        var text = Assert.Single(new WorkspaceUserGuide(new Reader(source)).Sections).Text;
        Assert.Contains("Heading", text, StringComparison.Ordinal);
        Assert.Contains("plain strong emphasis code guide (/help/overview#start)", text, StringComparison.Ordinal);
        Assert.Contains("- plain", text, StringComparison.Ordinal);
        Assert.Contains("<untrusted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HelpParagraph", text, StringComparison.Ordinal);
    }

    private sealed class Reader(HelpSectionV1 section) : IUserGuideReader
    {
        public HelpSectionListV1 Contents => new([new(section.Id, section.Title, section.Summary)]);
        public bool TryGetSection(string sectionId, [NotNullWhen(true)] out HelpSectionV1? value)
        {
            value = sectionId == section.Id ? section : null;
            return value is not null;
        }
    }
}
