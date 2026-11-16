namespace Braid.Domain;

/// <summary>Closed locator family. Office coordinates describe package structure, never source byte offsets or rendered pages.</summary>
public abstract record SpecEvidenceLocator
{
    private SpecEvidenceLocator() { }

    /// <summary>One-based physical PDF page. No byte range or OCR/text coverage is implied.</summary>
    public sealed record PdfPage : SpecEvidenceLocator
    {
        public PdfPage(int page)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
            Page = page;
        }
        public int Page { get; }
    }

    public sealed record ByteRange : SpecEvidenceLocator
    {
        public ByteRange(int offset, int length, int startLine, int endLine, string? jsonPointer)
        {
            if (offset < 0 || length < 0 || startLine < 1 || endLine < startLine) throw new ArgumentOutOfRangeException(nameof(offset));
            Offset = offset; Length = length; StartLine = startLine; EndLine = endLine; JsonPointer = jsonPointer;
        }
        public int Offset { get; }
        public int Length { get; }
        public int StartLine { get; }
        public int EndLine { get; }
        public string? JsonPointer { get; }
    }

    /// <summary>One-based paragraph among direct body paragraphs, or direct paragraphs of the specified table cell.
    /// Table/row/cell indices count XML siblings, not a rendered grid (merges are not expanded).</summary>
    public sealed record WordParagraph : SpecEvidenceLocator
    {
        public WordParagraph(string part, int paragraph, int? table = null, int? row = null, int? cell = null)
        {
            ValidatePart(part);
            if (paragraph < 1 || (table is null) != (row is null) || (table is null) != (cell is null) || table < 1 || row < 1 || cell < 1)
                throw new ArgumentOutOfRangeException(nameof(paragraph));
            Part = part; Paragraph = paragraph; Table = table; Row = row; Cell = cell;
        }
        public string Part { get; }
        public int Paragraph { get; }
        public int? Table { get; }
        public int? Row { get; }
        public int? Cell { get; }
    }

    public sealed record SheetCell : SpecEvidenceLocator
    {
        public SheetCell(string part, string sheet, string cell, SpecCellValueKind valueKind, bool hasCachedValue)
        {
            ValidatePart(part);
            if (string.IsNullOrEmpty(sheet) || sheet.Length > 31 || !IsCell(cell) || !Enum.IsDefined(valueKind)) throw new ArgumentException("Invalid cell locator.");
            Part = part; Sheet = sheet; Cell = cell; ValueKind = valueKind; HasCachedValue = hasCachedValue;
        }
        public string Part { get; }
        public string Sheet { get; }
        public string Cell { get; }
        public SpecCellValueKind ValueKind { get; }
        public bool HasCachedValue { get; }
        public static bool IsCell(string? value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 10) return false;
            var i = 0; var column = 0;
            while (i < value.Length && value[i] is >= 'A' and <= 'Z')
            {
                if (i == 3) return false;
                column = column * 26 + value[i++] - 'A' + 1;
            }
            return column is >= 1 and <= 16384 && i < value.Length && value[i] != '0' &&
                value.AsSpan(i).ToArray().All(char.IsAsciiDigit) && int.TryParse(value.AsSpan(i), out var row) && row is >= 1 and <= 1048576;
        }
    }

    private static void ValidatePart(string part)
    {
        if (string.IsNullOrEmpty(part) || part.Length > 240 || part.Split('/').Any(p => p.Length == 0 || p is "." or "..") ||
            part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' or '[' or ']')))
            throw new ArgumentException("Invalid package part.", nameof(part));
    }
}

public enum SpecCellValueKind { Blank = 1, Number = 2, Boolean = 3, Error = 4, SharedString = 5, InlineString = 6, String = 7, IsoDate = 8 }
