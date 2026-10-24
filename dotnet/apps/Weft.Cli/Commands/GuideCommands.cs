using System.CommandLine;
using System.Text.RegularExpressions;
using Weft.Cli.Http;
using Weft.Cli.Output;
using Weft.Contracts.Help;
using static Weft.Cli.Commands.CommandSupport;

namespace Weft.Cli.Commands;

internal static partial class GuideCommands
{
    public static Command Guide(CliGlobals globals)
    {
        var list = new Command("list", "List the signed-in server's user guide sections");
        list.Handle(globals, async invocation =>
        {
            var sections = await invocation.Api.ReadAsync<HelpSectionListV1>(new ApiRequest(ApiOperations.GetHelpSections), invocation.Cancellation);
            invocation.Output.Write(sections, output => output.Table(["SECTION", "TITLE", "SUMMARY"], sections.Sections.Select(section =>
                (IReadOnlyList<object?>)[section.Id, section.Title, section.Summary])));
        });
        var sectionId = Argument("sectionId", "The guide section ID from guide list");
        var get = new Command("get", "Read a section of the signed-in server's user guide").With(sectionId);
        get.Handle(globals, async invocation =>
        {
            var id = invocation.Value(sectionId)!;
            if (id.Length > 64 || !SectionId().IsMatch(id))
                throw CliException.Usage("sectionId must be a lowercase guide section ID from: weft guide list");
            var section = await invocation.Api.ReadAsync<HelpSectionV1>(new ApiRequest(ApiOperations.GetHelpSection).Path("sectionId", id), invocation.Cancellation);
            invocation.Output.Write(section, output =>
            {
                output.Line(CliOutput.Safe(section.Title));
                output.Line(CliOutput.Safe(section.Summary));
                foreach (var block in section.Blocks)
                {
                    output.Line();
                    switch (block)
                    {
                        case HelpHeadingV1 heading: output.Line(CliOutput.Safe(heading.Text)); break;
                        case HelpParagraphV1 paragraph: output.Line(Text(paragraph.Content)); break;
                        case HelpCodeBlockV1 code:
                            using (var lines = new StringReader(code.Text))
                                while (lines.ReadLine() is { } line)
                                    output.Line(CliOutput.Safe(line));
                            break;
                        case HelpListV1 items:
                            for (var index = 0; index < items.Items.Count; index++)
                                output.Line($"{(items.Ordered ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." : "-")} {Text(items.Items[index].Content)}");
                            break;
                        case HelpTableV1 table:
                            output.Table(table.Header.Select(cell => Text(cell.Content)).ToArray(), table.Rows.Select(row =>
                                (IReadOnlyList<object?>)row.Cells.Select(cell => (object?)Text(cell.Content)).ToArray()));
                            break;
                    }
                }
            });
        });
        return new Command("guide", "The server's authenticated user guide").With(list, get);
    }

    private static string Text(IReadOnlyList<HelpInlineV1> content) => CliOutput.Safe(string.Concat(content.Select(item => item switch
    {
        HelpTextV1 text => text.Text,
        HelpStrongV1 strong => strong.Text,
        HelpEmphasisV1 emphasis => emphasis.Text,
        HelpCodeV1 code => code.Text,
        HelpLinkV1 link => link.Text + " (weft guide get " + link.SectionId + (link.Anchor is null ? "" : "; heading " + link.Anchor) + ")",
        _ => ""
    })));

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionId();
}
