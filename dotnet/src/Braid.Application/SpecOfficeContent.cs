using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Braid.Domain;

namespace Braid.Application;

internal static partial class SpecOfficeParser
{
    private sealed partial class Package
    {
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = OfficeRel.TrimEnd('/');

        internal IReadOnlyList<SpecOfficeEvidenceChunk> Word()
        {
            var part = MainPart("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
            var root = Part(part).Root;
            if (root?.Name != W + "document") Bad("invalid-word-document");
            var bodies = root.Elements(W + "body").ToArray();
            if (bodies.Length != 1) Bad("invalid-word-body");
            if (root.Elements().Any(e => e.Name != W + "body")) Unsupported("unsupported-word-content");
            var paragraph = 0; var table = 0;
            foreach (var item in bodies[0].Elements())
            {
                token.ThrowIfCancellationRequested();
                if (item.Name == W + "p") Add(WordText(item), new SpecEvidenceLocator.WordParagraph(part, ++paragraph));
                else if (item.Name == W + "tbl") WordTable(part, item, ++table);
                else if (item.Name != W + "sectPr") Unsupported("unsupported-word-content");
            }
            return chunks;
        }

        private void WordTable(string part, XElement table, int ordinal)
        {
            var row = 0;
            foreach (var tr in table.Elements())
            {
                if (tr.Name == W + "tblPr" || tr.Name == W + "tblGrid") continue;
                if (tr.Name != W + "tr") Unsupported("unsupported-word-table");
                row++; var cell = 0;
                foreach (var tc in tr.Elements())
                {
                    if (tc.Name == W + "trPr") continue;
                    if (tc.Name != W + "tc") Unsupported("unsupported-word-table");
                    cell++; var paragraph = 0;
                    foreach (var p in tc.Elements())
                    {
                        if (p.Name == W + "tcPr") continue;
                        if (p.Name != W + "p") Unsupported("unsupported-word-cell");
                        Add(WordText(p), new SpecEvidenceLocator.WordParagraph(part, ++paragraph, ordinal, row, cell));
                    }
                    if (paragraph == 0) Bad("word-cell-without-paragraph");
                }
            }
        }

        private string WordText(XElement paragraph)
        {
            var text = new StringBuilder();
            foreach (var run in paragraph.Elements())
            {
                if (run.Name == W + "pPr") continue;
                if (run.Name != W + "r") Unsupported("unsupported-word-paragraph");
                foreach (var item in run.Elements())
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Name == W + "rPr") continue;
                    if (item.Name == W + "t")
                    {
                        if (item.HasElements) Bad("invalid-word-text");
                        text.Append(item.Value);
                    }
                    else if (item.Name == W + "tab") text.Append('\t');
                    else if (item.Name == W + "br" || item.Name == W + "cr")
                    {
                        if (item.Attribute(W + "type") is { Value: not "textWrapping" }) Unsupported("unsupported-word-break");
                        text.Append('\n');
                    }
                    else Unsupported("unsupported-word-run");
                    if (text.Length > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
                }
            }
            return text.ToString();
        }

        internal IReadOnlyList<SpecOfficeEvidenceChunk> Excel()
        {
            var part = MainPart("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            var root = Part(part).Root;
            if (root?.Name != S + "workbook") Bad("invalid-workbook");
            var sheetLists = root.Elements(S + "sheets").ToArray();
            if (sheetLists.Length != 1) Bad("invalid-workbook-sheets");
            relationships.TryGetValue(part, out var rels);
            rels ??= new(StringComparer.Ordinal);
            var strings = new List<string>();
            var shared = rels.Values.Where(r => r.Type == OfficeRel + "sharedStrings").ToArray();
            if (shared.Length > 1) Bad("duplicate-shared-strings");
            if (shared.Length == 1)
            {
                var sst = Part(shared[0].Part).Root;
                if (sst?.Name != S + "sst") Bad("invalid-shared-strings");
                foreach (var si in sst.Elements())
                {
                    token.ThrowIfCancellationRequested();
                    if (si.Name != S + "si") Unsupported("unsupported-shared-strings");
                    strings.Add(SpreadsheetText(si));
                }
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetIds = new HashSet<uint>();
            var usedParts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sheet in sheetLists[0].Elements())
            {
                if (sheet.Name != S + "sheet") Unsupported("unsupported-sheet");
                var name = (string?)sheet.Attribute("name"); var id = (string?)sheet.Attribute(R + "id");
                var sheetId = (string?)sheet.Attribute("sheetId");
                if (string.IsNullOrEmpty(name) || name.Length > 31 || name.Any(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\') ||
                    !names.Add(name) || string.IsNullOrEmpty(id) || !uint.TryParse(sheetId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId == 0 || !sheetIds.Add(numericId)) Bad("invalid-sheet-identity");
                if (!rels.TryGetValue(id, out var link) || link.Type != OfficeRel + "worksheet" || !usedParts.Add(link.Part)) Bad("invalid-sheet-relationship");
                Sheet(link.Part, name, strings);
            }
            return chunks;
        }

        private void Sheet(string part, string name, List<string> strings)
        {
            var root = Part(part).Root;
            if (root?.Name != S + "worksheet") Bad("invalid-worksheet");
            var data = root.Elements(S + "sheetData").ToArray();
            if (data.Length != 1) Bad("invalid-sheet-data");
            // Ignored rendering/layout metadata is why all accepted Office output is Partial.
            // Features with alternative cell/text semantics are refused, rather than guessed.
            if (root.Elements().Any(e => e.Name.Namespace != S || e.Name.LocalName is not
                ("sheetData" or "dimension" or "sheetViews" or "sheetFormatPr" or "cols" or "sheetPr" or "pageMargins" or "pageSetup" or "printOptions" or "mergeCells"))) Unsupported("unsupported-worksheet-feature");
            var rows = new HashSet<int>(); var cells = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data[0].Elements())
            {
                token.ThrowIfCancellationRequested();
                if (row.Name != S + "row") Bad("invalid-sheet-row");
                if (!int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)) Bad("invalid-sheet-row");
                if (rowIndex is < 1 or > 1048576 || !rows.Add(rowIndex)) Bad("invalid-sheet-row");
                foreach (var cell in row.Elements())
                {
                    if (cell.Name != S + "c") Unsupported("unsupported-sheet-row");
                    var address = (string?)cell.Attribute("r");
                    if (!SpecEvidenceLocator.SheetCell.IsCell(address) || !cells.Add(address!)) Bad("invalid-cell-address");
                    var digit = address!.TakeWhile(char.IsAsciiLetter).Count();
                    if (int.Parse(address.AsSpan(digit), CultureInfo.InvariantCulture) != rowIndex) Bad("cell-row-mismatch");
                    Cell(part, name, address!, cell, strings);
                }
            }
        }

        private void Cell(string part, string sheet, string address, XElement cell, List<string> strings)
        {
            if (cell.Elements().Any(e => e.Name != S + "v" && e.Name != S + "f" && e.Name != S + "is")) Unsupported("unsupported-cell-content");
            var values = cell.Elements(S + "v").ToArray(); var formulas = cell.Elements(S + "f").ToArray(); var inline = cell.Elements(S + "is").ToArray();
            if (values.Length > 1 || formulas.Length > 1 || inline.Length > 1 || values.Any(v => v.HasElements)) Bad("invalid-cell-value");
            var formula = formulas.FirstOrDefault();
            if (formula is not null && (formula.HasElements || formula.Value.Length == 0 || formula.Attributes().Any(a => a.Name != "t" || a.Value != "normal")))
                Unsupported("unsupported-formula-kind");
            var type = (string?)cell.Attribute("t") ?? "n";
            var value = values.FirstOrDefault()?.Value ?? "";
            // Producers commonly emit <v/> for an uncalculated formula. Only string caches can be genuinely empty.
            var hasValue = values.Length != 0 && (value.Length != 0 || type == "str");
            SpecCellValueKind kind;
            switch (type)
            {
                case "s":
                    kind = SpecCellValueKind.SharedString;
                    if (!hasValue) Bad("invalid-shared-string-index");
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= strings.Count) Bad("invalid-shared-string-index");
                    value = strings[index]; break;
                case "inlineStr":
                    kind = SpecCellValueKind.InlineString;
                    if (inline.Length != 1 || values.Length != 0 || formula is not null) Bad("invalid-inline-string");
                    value = SpreadsheetText(inline[0]); break;
                case "n":
                    kind = hasValue ? SpecCellValueKind.Number : SpecCellValueKind.Blank;
                    if (hasValue && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))) Bad("invalid-numeric-cell");
                    break;
                case "b": kind = SpecCellValueKind.Boolean; if (hasValue && value is not ("0" or "1")) Bad("invalid-boolean-cell"); break;
                case "e":
                    kind = SpecCellValueKind.Error;
                    if (hasValue ? value is not ("#NULL!" or "#DIV/0!" or "#VALUE!" or "#REF!" or "#NAME?" or "#NUM!" or "#N/A" or "#GETTING_DATA") : formula is null)
                        Bad("invalid-error-cell");
                    break;
                case "str": kind = SpecCellValueKind.String; break;
                case "d":
                    kind = SpecCellValueKind.IsoDate;
                    if (hasValue ? !IsIsoDate(value) : formula is null) Bad("invalid-date-cell");
                    break;
                default: Unsupported("unsupported-cell-type"); return;
            }
            if (type != "inlineStr" && inline.Length != 0) Bad("invalid-inline-string");
            Add(value, new SpecEvidenceLocator.SheetCell(part, sheet, address, kind, formula is not null && hasValue), formula?.Value);
        }

        private static bool IsIsoDate(string value)
        {
            // Deliberately bounded ISO calendar-date/date-time profile; preserve the original lexical value.
            if (value.Length > 33 || !Regex.IsMatch(value,
                @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?)?(?:Z|[+-][0-9]{2}:[0-9]{2})?\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return false;
            if (value.Length >= 16 && value[^6] is '+' or '-')
            {
                var hours = int.Parse(value.AsSpan(value.Length - 5, 2), CultureInfo.InvariantCulture);
                var minutes = int.Parse(value.AsSpan(value.Length - 2, 2), CultureInfo.InvariantCulture);
                if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) return false;
            }
            try { _ = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.RoundtripKind); return true; }
            catch (FormatException) { return false; }
        }

        private string SpreadsheetText(XElement container)
        {
            var text = new StringBuilder();
            foreach (var item in container.Elements())
            {
                token.ThrowIfCancellationRequested();
                if (item.Name == S + "t" && !item.HasElements) text.Append(item.Value);
                else if (item.Name == S + "r")
                {
                    foreach (var run in item.Elements())
                        if (run.Name == S + "t" && !run.HasElements) text.Append(run.Value);
                        else if (run.Name != S + "rPr") Unsupported("unsupported-rich-string");
                }
                else Unsupported("unsupported-rich-string");
                if (text.Length > limits.MaximumEvidenceBytes) Limit("evidence-bytes");
            }
            return text.ToString();
        }
    }
}
