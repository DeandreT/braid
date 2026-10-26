using System.Text;
using Weft.Application;
using Weft.Contracts.Help;

namespace Weft.Api.Chat.Workspace;

/// <summary>Plain text from the same embedded, parsed guide served by the help API; no runtime file or network reads.</summary>
public sealed class WorkspaceUserGuide : IWorkspaceUserGuide
{
    public WorkspaceUserGuide(IUserGuideReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Sections = reader.Contents.Sections.Select(summary =>
        {
            if (!reader.TryGetSection(summary.Id, out var section) || section.Id != summary.Id)
                throw new InvalidOperationException("The embedded guide contents and sections disagree.");
            return new WorkspaceGuideSection(section.Id, section.Title, Text(section));
        }).ToArray();
    }

    public IReadOnlyList<WorkspaceGuideSection> Sections { get; }

    private static string Text(HelpSectionV1 section)
    {
        var text = new StringBuilder().AppendLine(section.Title).AppendLine(section.Summary);
        foreach (var block in section.Blocks)
        {
            switch (block)
            {
                case HelpHeadingV1 heading:
                    text.AppendLine(heading.Text);
                    break;
                case HelpParagraphV1 paragraph:
                    text.AppendLine(Inline(paragraph.Content));
                    break;
                case HelpListV1 list:
                    foreach (var item in list.Items) text.Append("- ").AppendLine(Inline(item.Content));
                    break;
                case HelpTableV1 table:
                    text.AppendLine(string.Join(" | ", table.Header.Select(cell => Inline(cell.Content))));
                    foreach (var row in table.Rows)
                        text.AppendLine(string.Join(" | ", row.Cells.Select(cell => Inline(cell.Content))));
                    break;
                case HelpCodeBlockV1 code:
                    text.AppendLine(code.Text);
                    break;
                default:
                    throw new InvalidOperationException("The embedded guide has an unsupported block.");
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    private static string Inline(IReadOnlyList<HelpInlineV1> content) => string.Concat(content.Select(item => item switch
    {
        HelpTextV1 text => text.Text,
        HelpStrongV1 strong => strong.Text,
        HelpEmphasisV1 emphasis => emphasis.Text,
        HelpCodeV1 code => code.Text,
        HelpLinkV1 link => link.Text + " (/help/" + link.SectionId + (link.Anchor is null ? "" : "#" + link.Anchor) + ")",
        _ => throw new InvalidOperationException("The embedded guide has unsupported inline content.")
    }));
}
