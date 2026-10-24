using System.Text.RegularExpressions;
using Weft.Api.Help;
using Weft.Contracts.Help;

namespace Weft.Tests;

public sealed partial class UserGuideTests
{
    private static readonly string[] CoreSections = ["overview", "sign-in", "catalog", "uploads", "metadata", "workbench",
        "reports", "comparisons", "previews", "regression-tests", "builds", "chat", "unconfirmed-results", "cli"];

    [Fact]
    public void EveryGuideFileIsEmbeddedWithItsExactBytes()
    {
        var directory = Path.Combine(Repository(), "docs", "user-guide");
        var files = Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.All(files, file => Assert.EndsWith(".md", file, StringComparison.Ordinal));
        var assembly = typeof(UserGuide).Assembly;
        var resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith("Weft.Api.UserGuide.", StringComparison.Ordinal))
            .Select(name => name["Weft.Api.UserGuide.".Length..]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(files, resources);
        foreach (var file in files)
        {
            using var stream = assembly.GetManifestResourceStream("Weft.Api.UserGuide." + file)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, file!)), copy.ToArray());
        }
    }

    [Fact]
    public void EmbeddedGuideHasEverySectionInOrder()
    {
        var sections = UserGuide.Embedded.Contents.Sections;
        Assert.Equal("overview", sections[0].Id);
        Assert.All(CoreSections, id => Assert.Contains(sections, section => section.Id == id));
        Assert.Equal(sections.Count, sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var summary in sections)
        {
            Assert.True(UserGuide.Embedded.TryGetSection(summary.Id, out var section));
            Assert.Equal((summary.Title, summary.Summary), (section.Title, section.Summary));
            Assert.NotEmpty(section.Blocks);
        }
        Assert.False(UserGuide.Embedded.TryGetSection("missing", out _));
    }

    [Fact]
    public void EverySectionIdReferencedFromCodeExists()
    {
        var root = Repository();
        var references = new SortedSet<string>(StringComparer.Ordinal);
        var constants = File.ReadAllText(Path.Combine(root, "apps", "web", "src", "help", "help-sections.ts"));
        var list = HelpSectionListPattern().Match(constants);
        Assert.True(list.Success, "help-sections.ts must declare its helpSectionIds array.");
        foreach (Match id in QuotedPattern().Matches(list.Groups[1].Value))
            references.Add(id.Groups[1].Value);
        Assert.NotEmpty(references);
        foreach (var file in Sources(Path.Combine(root, "apps", "web", "src"), "*.ts", "*.html")
            .Concat(Sources(Path.Combine(root, "dotnet", "src"), "*.cs")))
        {
            var text = File.ReadAllText(file);
            foreach (Match link in HelpLinkPattern().Matches(text))
                references.Add(link.Groups[1].Value);
            foreach (Match call in HelpRoutePattern().Matches(text))
                references.Add(call.Groups[1].Value);
            foreach (Match element in HelpLinkElementPattern().Matches(text))
                references.Add(element.Groups[1].Value);
        }
        var missing = references.Where(id => !UserGuide.Embedded.TryGetSection(id, out _)).ToArray();
        Assert.True(missing.Length == 0, "Code links to missing guide sections: " + string.Join(", ", missing));
    }

    [Fact]
    public void SupportedMarkdownBecomesClosedStructure()
    {
        var guide = Parse(("first.md", Section("first", 1, """
            Plain **strong** and *emphasis* with `code` and a [link](second.md#where-to-go), \*escaped\*.
            A second line joins the paragraph.

            ## Where things are

            - one item
              continues here
            - [same page](#where-things-are)

            1. first
            2. second

            | Name | Meaning |
            | --- | :---: |
            | **Viewer** | Reads \| browses |
            | `a\|b` | Escapes \_ \~ \& \: stay literal, as do & and : alone. |

            ```sh
            weft help
              indented
            ```
            """)), ("second.md", Section("second", 2, "## Where to go\n\nSee [the first](first.md).")));
        Assert.Equal(["first", "second"], guide.Contents.Sections.Select(section => section.Id));
        Assert.True(guide.TryGetSection("first", out var first));
        var blocks = first.Blocks;
        Assert.Equal(6, blocks.Count);
        var paragraph = Assert.IsType<HelpParagraphV1>(blocks[0]);
        Assert.Equal<HelpInlineV1>([new HelpTextV1("Plain "), new HelpStrongV1("strong"), new HelpTextV1(" and "),
            new HelpEmphasisV1("emphasis"), new HelpTextV1(" with "), new HelpCodeV1("code"), new HelpTextV1(" and a "),
            new HelpLinkV1("link", "second", "where-to-go"), new HelpTextV1(", *escaped*. A second line joins the paragraph.")],
            paragraph.Content);
        Assert.Equal(new HelpHeadingV1(2, "where-things-are", "Where things are"), blocks[1]);
        var unordered = Assert.IsType<HelpListV1>(blocks[2]);
        Assert.False(unordered.Ordered);
        Assert.Equal<HelpInlineV1>([new HelpTextV1("one item continues here")], unordered.Items[0].Content);
        Assert.Equal<HelpInlineV1>([new HelpLinkV1("same page", "first", "where-things-are")], unordered.Items[1].Content);
        var ordered = Assert.IsType<HelpListV1>(blocks[3]);
        Assert.True(ordered.Ordered);
        Assert.Equal(2, ordered.Items.Count);
        var table = Assert.IsType<HelpTableV1>(blocks[4]);
        Assert.Equal<HelpInlineV1>([new HelpTextV1("Meaning")], table.Header[1].Content);
        Assert.Equal<HelpInlineV1>([new HelpStrongV1("Viewer")], table.Rows[0].Cells[0].Content);
        Assert.Equal<HelpInlineV1>([new HelpTextV1("Reads | browses")], table.Rows[0].Cells[1].Content);
        Assert.Equal<HelpInlineV1>([new HelpCodeV1("a|b")], table.Rows[1].Cells[0].Content);
        Assert.Equal<HelpInlineV1>([new HelpTextV1("Escapes _ ~ & : stay literal, as do & and : alone.")], table.Rows[1].Cells[1].Content);
        Assert.Equal(new HelpCodeBlockV1("weft help\n  indented"), blocks[5]);
    }

    [Theory]
    [InlineData("Some <b>bold</b> text.", "Raw HTML")]
    [InlineData("<script>alert(1)</script>", "HTML")]
    [InlineData("Visit <https://example.com>.", "Raw HTML")]
    [InlineData("![picture](first.md)", "Images")]
    [InlineData("[external](https://example.com)", "Links point only")]
    [InlineData("[file](../development.md)", "Links point only")]
    [InlineData("[script](javascript:alert(1))", "Links point only")]
    [InlineData("[missing](missing.md)", "does not exist")]
    [InlineData("[missing anchor](first.md#nowhere)", "has no heading")]
    [InlineData("[](first.md)", "Link text")]
    [InlineData("Unclosed **strong text.", "not closed")]
    [InlineData("Unclosed *emphasis.", "not closed")]
    [InlineData("A stray ] bracket.", "unmatched")]
    [InlineData("Bad \\q escape.", "backslash")]
    [InlineData("# Title", "Only '## '")]
    [InlineData("#### Deep", "Only '## '")]
    [InlineData("## Fancy <em>", "Headings")]
    [InlineData("## Two - hyphens", "Headings")]
    [InlineData("## Same\n\n## Same", "already used")]
    [InlineData("> quoted", "Block quotes")]
    [InlineData("* star list", "Block quotes")]
    [InlineData("    indented code", "Block quotes")]
    [InlineData("- item\n  - nested", "nested lists")]
    [InlineData("- item\n  > quoted", "nested lists")]
    [InlineData("- item\n  ## Heading", "nested lists")]
    [InlineData("- item\n  | A |", "nested lists")]
    [InlineData("- > quoted item", "nested lists")]
    [InlineData("- - nested item", "nested lists")]
    [InlineData("- item\n   too deep", "exactly 2 spaces")]
    [InlineData("1. item\n  too shallow", "exactly 3 spaces")]
    [InlineData("**<b>bold</b>**", "Raw HTML")]
    [InlineData("*<i>italic</i>*", "Raw HTML")]
    [InlineData("[<b>label</b>](first.md)", "Link text")]
    [InlineData("Some _emphasis_ here.", "Escape '_'")]
    [InlineData("Some ~~deleted~~ text.", "Escape '~'")]
    [InlineData("**snake_case**", "Escape '_'")]
    [InlineData("Fish &amp; chips.", "Character references")]
    [InlineData("A &#169; sign.", "Character references")]
    [InlineData("Smile :smile: now.", "Emoji")]
    [InlineData("See https://example.com today.", "Bare web addresses")]
    [InlineData("See www.example.com today.", "Bare web addresses")]
    [InlineData("[the www.example.com site](first.md)", "Bare web addresses")]
    [InlineData("```\ncode\n````\n```", "exactly ```")]
    [InlineData("```\ncode\n   ```\n```", "exactly ```")]
    [InlineData("A hard  \nbreak.", "end with spaces")]
    [InlineData("1. one\n3. three", "numbered")]
    [InlineData("| A | B |\n| --- | --- |\n| only |", "one cell per header")]
    [InlineData("| A |\n| B |", "separator")]
    [InlineData("```\nunclosed", "not closed")]
    [InlineData("```JS\ncode\n```", "lowercase language")]
    [InlineData("Two  spaces.", "repeated spaces")]
    public void UnsupportedMarkdownIsRefused(string body, string expected)
    {
        var error = Assert.Throws<UserGuideFormatException>(() => Parse(("first.md", Section("first", 1, body + "\n\n## Anchor\n\nText."))));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("first.md", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("---\ntitle: T\norder: 1\nsummary: S\n---\n\nText.", "exactly one each")]
    [InlineData("---\nid: other\ntitle: T\norder: 1\nsummary: S\n---\n\nText.", "equal the file name")]
    [InlineData("---\nid: first\ntitle: T\norder: 01\nsummary: S\n---\n\nText.", "canonical integer")]
    [InlineData("---\nid: first\ntitle: T\norder: 1\nsummary: S\nextra: x\n---\n\nText.", "exactly one each")]
    [InlineData("---\nid: first\ntitle:  T\norder: 1\nsummary: S\n---\n\nText.", "surrounding spaces")]
    [InlineData("---\nid: first\ntitle: T\norder: 1\nsummary: S\n---\n", "needs content")]
    [InlineData("id: first\n\nText.", "starts with '---'")]
    [InlineData("﻿---\nid: first\ntitle: T\norder: 1\nsummary: S\n---\n\nText.", "byte order mark")]
    [InlineData("---\nid: first\ntitle: T\norder: 1\nsummary: S\n---\n\nTab\there.", "printable text")]
    public void InvalidFrontMatterAndCharactersAreRefused(string text, string expected)
    {
        var error = Assert.Throws<UserGuideFormatException>(() => Parse(("first.md", text)));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GuideRulesSpanFiles()
    {
        Assert.Contains("own order", Assert.Throws<UserGuideFormatException>(() =>
            Parse(("first.md", Section("first", 1, "A.")), ("second.md", Section("second", 1, "B.")))).Message, StringComparison.Ordinal);
        Assert.Contains("file names", Assert.Throws<UserGuideFormatException>(() =>
            Parse(("First.md", Section("First", 1, "A.")))).Message, StringComparison.Ordinal);
        Assert.Contains("1 to 64", Assert.Throws<UserGuideFormatException>(() => Parse()).Message, StringComparison.Ordinal);
        var many = Enumerable.Range(1, UserGuide.MaximumSections + 1)
            .Select(index => ($"s{index}.md", Section($"s{index}", index, "Text."))).ToArray();
        Assert.Contains("1 to 64", Assert.Throws<UserGuideFormatException>(() => Parse(many)).Message, StringComparison.Ordinal);
        var guide = Parse(("second.md", Section("second", 20, "B.")), ("first.md", Section("first", 3, "A.")));
        Assert.Equal(["first", "second"], guide.Contents.Sections.Select(section => section.Id));
    }

    [Fact]
    public void LimitsMatchTheBrowserChecks()
    {
        var longest = new string('a', UserGuideMarkdown.MaximumTextCharacters);
        var items = string.Join('\n', Enumerable.Repeat("- item", UserGuideMarkdown.MaximumItems));
        var guide = Parse(("first.md", Section("first", 1, $"{longest}\n\n```\n{longest}\n```\n\n{items}")));
        Assert.True(guide.TryGetSection("first", out var section));
        Assert.Equal(new HelpTextV1(longest), Assert.Single(Assert.IsType<HelpParagraphV1>(section.Blocks[0]).Content));
        Assert.Equal(UserGuideMarkdown.MaximumItems, Assert.IsType<HelpListV1>(section.Blocks[2]).Items.Count);
        foreach (var (body, expected) in new[]
        {
            (longest + "a", "at most 8192"),
            ($"```\n{longest}a\n```", "at most 8192"),
            ($"`{longest}a`", "at most 8192"),
            (items + "\n- one more", "at most 256 items"),
            ("| A |\n| --- |\n" + string.Join('\n', Enumerable.Repeat("| b |", UserGuideMarkdown.MaximumItems + 1)), "at most 256 rows"),
            (string.Join(' ', Enumerable.Repeat("*e* x", UserGuideMarkdown.MaximumItems)), "at most 256 formatted runs"),
            ("## " + string.Join(' ', Enumerable.Repeat("word", 30)), "Headings")
        })
            Assert.Contains(expected, Assert.Throws<UserGuideFormatException>(() => Parse(("first.md", Section("first", 1, body)))).Message,
                StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("overview", true)]
    [InlineData("regression-tests", true)]
    [InlineData("a1-b2", true)]
    [InlineData("", false)]
    [InlineData("Overview", false)]
    [InlineData("1st", false)]
    [InlineData("sign--in", false)]
    [InlineData("sign-", false)]
    [InlineData("sign_in", false)]
    [InlineData("overview\n", false)]
    [InlineData(null, false)]
    public void SectionIdsAreStrict(string? value, bool valid)
    {
        Assert.Equal(valid, UserGuide.IsSectionId(value));
        Assert.True(UserGuide.IsSectionId(new string('a', 64)));
        Assert.False(UserGuide.IsSectionId(new string('a', 65)));
    }

    private static UserGuide Parse(params (string FileName, string Text)[] files) => UserGuide.Parse(files);

    private static string Section(string id, int order, string body) =>
        $"---\nid: {id}\ntitle: Title {id}\norder: {order}\nsummary: Summary of {id}.\n---\n\n{body}\n";

    private static IEnumerable<string> Sources(string directory, params string[] patterns) =>
        patterns.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Repository()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "docs", "user-guide")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The repository source is required to check the user guide.");
    }

    [GeneratedRegex(@"export const helpSectionIds = \[([^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex HelpSectionListPattern();

    [GeneratedRegex("\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();

    [GeneratedRegex("[\"'`]/help/([^\"'`/?#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HelpLinkPattern();

    [GeneratedRegex("helpRoute\\(\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex HelpRoutePattern();

    [GeneratedRegex("<weft-help-link\\s+\\[section\\]=\"'([^']+)'\"", RegexOptions.CultureInvariant)]
    private static partial Regex HelpLinkElementPattern();
}
