using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Braid.Contracts.Help;

namespace Braid.Api.Help;

/// <summary>A guide file that is outside the supported Markdown subset or breaks a guide rule.</summary>
public sealed class UserGuideFormatException(string message) : Exception(message);

internal sealed record UserGuideLink(string SectionId, string? Anchor, int Line);

internal sealed record UserGuideParsedSection(int Order, HelpSectionV1 Section, IReadOnlySet<string> Anchors,
    IReadOnlyList<UserGuideLink> Links);

/// <summary>
/// Parses one guide file: strict front matter and a closed Markdown subset (headings, paragraphs,
/// single-level lists, pipe tables, fenced code, emphasis, inline code and links to guide sections).
/// Anything else, including raw HTML, images and syntax that GitHub would render differently, is
/// refused rather than passed through. The limits match the browser's response checks.
/// </summary>
internal static partial class UserGuideMarkdown
{
    internal const int MaximumTitleCharacters = 120;
    internal const int MaximumSummaryCharacters = 300;
    internal const int MaximumBlocks = 512;
    /// <summary>One text run, code span, code block, heading or link text.</summary>
    internal const int MaximumTextCharacters = 8192;
    /// <summary>List items, table rows, cells per row and inline runs per block.</summary>
    internal const int MaximumItems = 256;
    internal const int MaximumAnchorCharacters = 128;

    internal static UserGuideParsedSection Parse(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(fileName, text).Parse();
    }

    /// <summary>The GitHub heading slug, restricted so that both renderings agree.</summary>
    internal static string Anchor(string heading)
    {
        var builder = new StringBuilder(heading.Length);
        foreach (var character in heading.ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                builder.Append(character);
            else if (character == ' ')
                builder.Append('-');
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    internal static partial Regex AnchorPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 ,.'?()&:/-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\d{1,3})\. ", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedItemPattern();

    [GeneratedRegex(@"^\|(?: *:?-{3,}:? *\|)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparatorPattern();

    /// <summary>A line GitHub would take as the end of a fenced code block.</summary>
    [GeneratedRegex(@"^ {0,3}`{3,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ClosingFencePattern();

    [GeneratedRegex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterReferencePattern();

    [GeneratedRegex(@"\G:[a-z][a-z0-9_+-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex EmojiShortcodePattern();

    [GeneratedRegex(@"\G(?:https?://|www\.)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BareAddressPattern();

    private sealed class Parser
    {
        private readonly string _fileName;
        private readonly string _expectedId;
        private readonly string[] _lines;
        private readonly List<HelpBlockV1> _blocks = [];
        private readonly HashSet<string> _anchors = new(StringComparer.Ordinal);
        private readonly List<UserGuideLink> _links = [];
        private string _id = "";
        private int _index;

        internal Parser(string fileName, string text)
        {
            _fileName = fileName;
            if (!fileName.EndsWith(".md", StringComparison.Ordinal) || !UserGuide.IsSectionId(fileName[..^3]))
                throw Error(0, "Guide file names must be '<section-id>.md'.");
            _expectedId = fileName[..^3];
            if (text.Length > 0 && text[0] == '\uFEFF')
                throw Error(1, "Guide files must not start with a byte order mark.");
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var control = normalized.AsSpan().IndexOfAnyInRange('\0', '\u001f');
            while (control >= 0 && normalized[control] == '\n')
            {
                var next = normalized.AsSpan(control + 1).IndexOfAnyInRange('\0', '\u001f');
                control = next < 0 ? -1 : control + 1 + next;
            }
            if (control >= 0 || normalized.Contains('\u007f'))
                throw Error(0, "Guide files contain only printable text, spaces and line feeds.");
            _lines = normalized.EndsWith('\n') ? normalized[..^1].Split('\n') : normalized.Split('\n');
            for (var line = 0; line < _lines.Length; line++)
                if (_lines[line].EndsWith(' '))
                    throw Error(line + 1, "Lines must not end with spaces; hard line breaks are not supported.");
        }

        internal UserGuideParsedSection Parse()
        {
            var (order, title, summary) = FrontMatter();
            while (_index < _lines.Length)
            {
                var line = _lines[_index];
                if (line.Length == 0) { _index++; continue; }
                if (_blocks.Count == MaximumBlocks)
                    throw Error(_index + 1, "A guide section has too many blocks.");
                if (line.StartsWith("```", StringComparison.Ordinal)) CodeBlock();
                else if (line.StartsWith('#')) Heading(line);
                else if (line.StartsWith("- ", StringComparison.Ordinal)) List(ordered: false);
                else if (OrderedItemPattern().IsMatch(line)) List(ordered: true);
                else if (line.StartsWith('|')) Table();
                else Paragraph();
            }
            if (_blocks.Count == 0)
                throw Error(_index, "A guide section needs content after its front matter.");
            return new(order, new HelpSectionV1(_id, title, summary, _blocks.AsReadOnly()), _anchors, _links.AsReadOnly());
        }

        private (int Order, string Title, string Summary) FrontMatter()
        {
            if (_lines.Length == 0 || _lines[0] != "---")
                throw Error(1, "A guide file starts with '---' front matter.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            _index = 1;
            for (; _index < _lines.Length && _lines[_index] != "---"; _index++)
            {
                var line = _lines[_index];
                var separator = line.IndexOf(": ", StringComparison.Ordinal);
                var key = separator > 0 ? line[..separator] : "";
                if (key is not ("id" or "title" or "order" or "summary") || !values.TryAdd(key, line[(separator + 2)..]))
                    throw Error(_index + 1, "Front matter holds exactly one each of id, title, order and summary.");
                if (values[key].Length == 0 || values[key] != values[key].Trim())
                    throw Error(_index + 1, $"Front matter '{key}' must be nonempty without surrounding spaces.");
            }
            if (_index == _lines.Length || values.Count != 4)
                throw Error(_index, "Front matter holds exactly one each of id, title, order and summary.");
            _index++;
            _id = values["id"];
            if (_id != _expectedId)
                throw Error(2, "The front matter id must equal the file name.");
            var orderText = values["order"];
            if (orderText.Length > 3 || orderText[0] == '0' || !int.TryParse(orderText, NumberStyles.None, CultureInfo.InvariantCulture, out var order))
                throw Error(0, "The front matter order must be a canonical integer from 1 to 999.");
            if (values["title"].Length > MaximumTitleCharacters || values["summary"].Length > MaximumSummaryCharacters)
                throw Error(0, "The front matter title or summary is too long.");
            return (order, values["title"], values["summary"]);
        }

        private void CodeBlock()
        {
            var start = _index;
            var language = _lines[_index][3..];
            if (language.Length > 0 && !language.All(character => character is >= 'a' and <= 'z'))
                throw Error(start + 1, "A code fence may name only a lowercase language.");
            var text = new List<string>();
            for (_index++; _index < _lines.Length && _lines[_index] != "```"; _index++)
            {
                if (ClosingFencePattern().IsMatch(_lines[_index]))
                    throw Error(_index + 1, "Close a code block with exactly ``` at the start of the line.");
                text.Add(_lines[_index]);
            }
            if (_index == _lines.Length)
                throw Error(start + 1, "A code block is not closed.");
            _index++;
            if (text.Count == 0)
                throw Error(start + 1, "A code block is empty.");
            _blocks.Add(new HelpCodeBlockV1(Bounded(string.Join('\n', text), start + 1)));
        }

        private void Heading(string line)
        {
            var level = line.StartsWith("### ", StringComparison.Ordinal) ? 3 : line.StartsWith("## ", StringComparison.Ordinal) ? 2 : 0;
            if (level == 0)
                throw Error(_index + 1, "Only '## ' and '### ' headings are supported; the title comes from the front matter.");
            var text = line[(level + 1)..];
            var anchor = Anchor(text);
            if (!HeadingPattern().IsMatch(text) || text != text.Trim() || !AnchorPattern().IsMatch(anchor) ||
                anchor.Length > MaximumAnchorCharacters)
                throw Error(_index + 1, "Headings are short plain ASCII words whose anchor has no repeated hyphens.");
            if (!_anchors.Add(anchor))
                throw Error(_index + 1, $"The heading anchor '{anchor}' is already used in this section.");
            _blocks.Add(new HelpHeadingV1(level, anchor, text));
            _index++;
        }

        private void List(bool ordered)
        {
            var items = new List<HelpListItemV1>();
            var indent = 0;
            while (_index < _lines.Length && _lines[_index].Length > 0)
            {
                var line = _lines[_index];
                var start = _index + 1;
                if (items.Count == MaximumItems)
                    throw Error(start, $"A list holds at most {MaximumItems} items.");
                string first;
                if (ordered)
                {
                    var match = OrderedItemPattern().Match(line);
                    if (!match.Success || match.Groups[1].Value != (items.Count + 1).ToString(CultureInfo.InvariantCulture))
                        throw Error(start, "Ordered list items are numbered 1, 2, 3 without other blocks between them.");
                    first = line[match.Length..];
                    indent = match.Length;
                }
                else
                {
                    if (!line.StartsWith("- ", StringComparison.Ordinal))
                        throw Error(start, "Unordered list items start with '- ' without other blocks between them.");
                    first = line[2..];
                    indent = 2;
                }
                if (StartsBlock(first) || Unsupported(first) || first.StartsWith(' '))
                    throw Error(start, "List items hold only text: nested lists, quotes, headings, tables and code are not supported.");
                var text = new List<string> { first };
                for (_index++; _index < _lines.Length && _lines[_index].StartsWith(' '); _index++)
                {
                    var continuation = _lines[_index].TrimStart(' ');
                    if (_lines[_index].Length - continuation.Length != indent)
                        throw Error(_index + 1, $"Indent list continuation lines by exactly {indent} spaces, the width of the item marker.");
                    if (StartsBlock(continuation) || Unsupported(continuation))
                        throw Error(_index + 1, "List items hold only text: nested lists, quotes, headings, tables and code are not supported.");
                    text.Add(continuation);
                }
                items.Add(new HelpListItemV1(Inline(Join(text, start), start)));
            }
            _blocks.Add(new HelpListV1(ordered, items.AsReadOnly()));
        }

        private void Table()
        {
            var start = _index + 1;
            var rows = new List<string>();
            for (; _index < _lines.Length && _lines[_index].StartsWith('|'); _index++)
                rows.Add(_lines[_index]);
            if (rows.Count < 3 || !TableSeparatorPattern().IsMatch(rows[1]))
                throw Error(start, "A table needs a header row, a '| --- |' separator row and at least one body row.");
            if (rows.Count - 2 > MaximumItems)
                throw Error(start, $"A table holds at most {MaximumItems} rows.");
            var header = Cells(rows[0], start);
            if (rows[1].Count(character => character == '|') - 1 != header.Count)
                throw Error(start + 1, "The table separator must have one column per header cell.");
            var body = new List<HelpTableRowV1>();
            for (var row = 2; row < rows.Count; row++)
            {
                var cells = Cells(rows[row], start + row);
                if (cells.Count != header.Count)
                    throw Error(start + row, "Every table row needs one cell per header cell.");
                body.Add(new HelpTableRowV1(cells));
            }
            if (_index < _lines.Length && _lines[_index].Length > 0)
                throw Error(_index + 1, "Separate a table from the next block with a blank line.");
            _blocks.Add(new HelpTableV1(header, body.AsReadOnly()));
        }

        private IReadOnlyList<HelpTableCellV1> Cells(string row, int line)
        {
            if (row.Length < 2 || !row.EndsWith('|') || row[^2] == '\\')
                throw Error(line, "Table rows start and end with '|'.");
            var cells = new List<HelpTableCellV1>();
            var current = new StringBuilder();
            for (var position = 1; position < row.Length; position++)
            {
                var character = row[position];
                if (character == '\\' && position + 1 < row.Length)
                {
                    // As in GitHub tables, an escaped pipe is a literal pipe, even inside a code span.
                    if (row[position + 1] != '|')
                        current.Append(character);
                    current.Append(row[++position]);
                    continue;
                }
                if (character != '|')
                {
                    current.Append(character);
                    continue;
                }
                var text = current.ToString().Trim(' ');
                if (text.Length == 0)
                    throw Error(line, "Table cells must not be empty.");
                if (cells.Count == MaximumItems)
                    throw Error(line, $"A table row holds at most {MaximumItems} cells.");
                cells.Add(new HelpTableCellV1(Inline(text, line)));
                current.Clear();
            }
            return cells.AsReadOnly();
        }

        private void Paragraph()
        {
            var start = _index + 1;
            var text = new List<string>();
            for (; _index < _lines.Length && _lines[_index].Length > 0; _index++)
            {
                var line = _lines[_index];
                if (text.Count > 0 && StartsBlock(line))
                    break;
                if (Unsupported(line) || line.StartsWith("    ", StringComparison.Ordinal))
                    throw Error(_index + 1, "Block quotes, HTML, indented code, '*' or '+' lists and underlined headings are not supported.");
                text.Add(line.TrimStart(' '));
            }
            _blocks.Add(new HelpParagraphV1(Inline(Join(text, start), start)));
        }

        private static bool StartsBlock(string line) => line.StartsWith('#') || line.StartsWith("- ", StringComparison.Ordinal) ||
            OrderedItemPattern().IsMatch(line) || line.StartsWith('|') || line.StartsWith("```", StringComparison.Ordinal);

        private static bool Unsupported(string line) => line.StartsWith('>') || line.StartsWith('<') ||
            line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal) ||
            line.StartsWith("===", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal);

        private string Join(List<string> lines, int line)
        {
            var joined = string.Join(' ', lines);
            if (joined.Trim(' ').Length == 0 || joined.Contains("  ", StringComparison.Ordinal))
                throw Error(line, "Text must not be empty or contain repeated spaces.");
            return joined.Trim(' ');
        }

        private IReadOnlyList<HelpInlineV1> Inline(string text, int line)
        {
            var content = new List<HelpInlineV1>();
            var plain = new StringBuilder();
            var position = 0;
            while (position < text.Length)
            {
                var character = text[position];
                switch (character)
                {
                    case '\\':
                        if (position + 1 == text.Length || !IsEscapable(text[position + 1]))
                            throw Error(line, "A backslash escapes only Markdown punctuation.");
                        plain.Append(text[position + 1]);
                        position += 2;
                        break;
                    case '`':
                        Flush();
                        content.Add(new HelpCodeV1(Delimited(text, ref position, "`", line, escapes: false)));
                        break;
                    case '*' when position + 1 < text.Length && text[position + 1] == '*':
                        Flush();
                        content.Add(new HelpStrongV1(Delimited(text, ref position, "**", line, escapes: true)));
                        break;
                    case '*':
                        Flush();
                        content.Add(new HelpEmphasisV1(Delimited(text, ref position, "*", line, escapes: true)));
                        break;
                    case '[':
                        Flush();
                        content.Add(Link(text, ref position, line));
                        break;
                    case ']':
                        throw Error(line, "An unmatched ']' must be escaped.");
                    case '!' when position + 1 < text.Length && text[position + 1] == '[':
                        throw Error(line, "Images are not supported.");
                    default:
                        RequireText(text, position, line);
                        plain.Append(character);
                        position++;
                        break;
                }
                if (content.Count > MaximumItems)
                    throw Error(line, $"A block holds at most {MaximumItems} formatted runs.");
            }
            Flush();
            if (content.Count > MaximumItems)
                throw Error(line, $"A block holds at most {MaximumItems} formatted runs.");
            return content.AsReadOnly();

            void Flush()
            {
                if (plain.Length == 0) return;
                content.Add(new HelpTextV1(Bounded(plain.ToString(), line)));
                plain.Clear();
            }
        }

        /// <summary>Refuses unescaped syntax that GitHub renders but this subset would show as text.</summary>
        private void RequireText(string text, int position, int line)
        {
            var character = text[position];
            if (character == '<' && position + 1 < text.Length && (char.IsAsciiLetter(text[position + 1]) || text[position + 1] is '/' or '!' or '?'))
                throw Error(line, "Raw HTML and autolinks are not supported.");
            if (character is '_' or '~')
                throw Error(line, $"Escape '{character}' as '\\{character}'; write emphasis as *text* and strikethrough is not supported.");
            if (character == '&' && CharacterReferencePattern().IsMatch(text, position))
                throw Error(line, "Character references are not supported; write the character itself or escape '&' as '\\&'.");
            if (character == ':' && EmojiShortcodePattern().IsMatch(text, position))
                throw Error(line, "Emoji shortcodes are not supported; escape ':' as '\\:'.");
            if ((position == 0 || !char.IsAsciiLetterOrDigit(text[position - 1])) && BareAddressPattern().IsMatch(text, position))
                throw Error(line, "Bare web addresses are not supported.");
        }

        private string Bounded(string value, int line) => value.Length <= MaximumTextCharacters ? value
            : throw Error(line, $"A text run, code span, code block or link holds at most {MaximumTextCharacters} characters.");

        private string Delimited(string text, ref int position, string marker, int line, bool escapes)
        {
            var start = position + marker.Length;
            var value = new StringBuilder();
            for (var current = start; current < text.Length; current++)
            {
                var character = text[current];
                if (escapes && character == '\\')
                {
                    if (current + 1 == text.Length || !IsEscapable(text[current + 1]))
                        throw Error(line, "A backslash escapes only Markdown punctuation.");
                    value.Append(text[++current]);
                    continue;
                }
                if (string.CompareOrdinal(text, current, marker, 0, marker.Length) == 0 &&
                    !(marker == "*" && current + 1 < text.Length && text[current + 1] == '*'))
                {
                    var result = value.ToString();
                    if (result.Length == 0 || result != result.Trim(' '))
                        throw Error(line, $"Text inside '{marker}' must be nonempty without surrounding spaces.");
                    position = current + marker.Length;
                    return Bounded(result, line);
                }
                if (escapes)
                {
                    if (character is '*' or '`' or '[' or ']')
                        throw Error(line, $"Text inside '{marker}' is plain; escape Markdown punctuation.");
                    RequireText(text, current, line);
                }
                value.Append(character);
            }
            throw Error(line, $"'{marker}' is not closed.");
        }

        private HelpLinkV1 Link(string text, ref int position, int line)
        {
            var close = text.IndexOf("](", position, StringComparison.Ordinal);
            var end = close < 0 ? -1 : text.IndexOf(')', close);
            if (close < 0 || end < 0)
                throw Error(line, "Links are written '[text](section.md#anchor)'.");
            var label = text[(position + 1)..close];
            var target = text[(close + 2)..end];
            if (label.Length == 0 || label != label.Trim(' ') || label.IndexOfAny(['[', ']', '*', '`', '\\', '<']) >= 0)
                throw Error(line, "Link text is plain and nonempty.");
            for (var current = position + 1; current < close; current++)
                RequireText(text, current, line);
            Bounded(label, line);
            string sectionId;
            string? anchor = null;
            var hash = target.IndexOf('#', StringComparison.Ordinal);
            var path = hash < 0 ? target : target[..hash];
            if (hash >= 0)
                anchor = target[(hash + 1)..];
            if (path.Length == 0)
                sectionId = _id;
            else if (path.EndsWith(".md", StringComparison.Ordinal) && UserGuide.IsSectionId(path[..^3]))
                sectionId = path[..^3];
            else
                throw Error(line, "Links point only to guide sections, as 'section.md' or 'section.md#anchor'.");
            if (anchor is not null && !AnchorPattern().IsMatch(anchor))
                throw Error(line, "A link anchor is a heading slug.");
            if (path.Length == 0 && anchor is null)
                throw Error(line, "A link needs a section or an anchor.");
            _links.Add(new UserGuideLink(sectionId, anchor, line));
            position = end + 1;
            return new HelpLinkV1(label, sectionId, anchor);
        }

        private static bool IsEscapable(char character) => character is '\\' or '`' or '*' or '_' or '[' or ']' or '(' or ')'
            or '|' or '#' or '-' or '.' or '!' or '<' or '>' or '~' or '&' or ':';

        private UserGuideFormatException Error(int line, string message) =>
            new(line > 0 ? $"{_fileName}:{line}: {message}" : $"{_fileName}: {message}");
    }
}
