using System.Text;

namespace Weft.Application.Regression;

/// <summary>Strict UTF-8 CSV syntax with bounded logical records. No trimming, type coercion or header skipping.</summary>
internal static class CsvRowComparison
{
    internal static RegressionCsvLocation? Compare(RegressionOutput left, RegressionOutput right, CancellationToken token,
        out RegressionCsvDiagnostic? diagnostic)
    {
        diagnostic = null;
        List<string[]> expected;
        List<string[]> actual;
        try { expected = Parse(left, token); }
        catch (InvalidCsv error) { diagnostic = new(left.Key, "expected", error.Location); return null; }
        try { actual = Parse(right, token); }
        catch (InvalidCsv error) { diagnostic = new(right.Key, "actual", error.Location); return null; }
        for (var row = 0; row < Math.Max(expected.Count, actual.Count); row++)
        {
            token.ThrowIfCancellationRequested();
            if (row >= expected.Count || row >= actual.Count) return new(row + 1, 1);
            for (var column = 0; column < Math.Max(expected[row].Length, actual[row].Length); column++)
                if (column >= expected[row].Length || column >= actual[row].Length ||
                    !string.Equals(expected[row][column], actual[row][column], StringComparison.Ordinal)) return new(row + 1, column + 1);
        }
        return null;
    }

    private static List<string[]> Parse(RegressionOutput output, CancellationToken token)
    {
        if (output.ByteCount > CsvDocumentPolicy.MaximumBytes) throw Limit();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(output.Span); }
        catch (DecoderFallbackException)
        {
            var validBytes = 0;
            while (validBytes < output.ByteCount)
            {
                if ((validBytes & 4095) == 0) token.ThrowIfCancellationRequested();
                if (Rune.DecodeFromUtf8(output.Span[validBytes..], out _, out var consumed) != System.Buffers.OperationStatus.Done) break;
                validBytes += consumed;
            }
            var prefix = new UTF8Encoding(false, true).GetString(output.Span[..validBytes]);
            throw new InvalidCsv(PrefixLocation(prefix, output.CsvDialect!, token));
        }
        var offset = text.StartsWith('\ufeff') ? 1 : 0;
        var dialect = output.CsvDialect!;
        var delimiter = dialect.Delimiter[0];
        var quote = dialect.QuoteDisabled ? '\0' : dialect.Quote![0];
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closed = false;
        var started = false;
        var nodes = 0;
        for (var index = offset; index < text.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            var value = text[index];
            started = true;
            if (quoted)
            {
                if (value == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote) { cell.Append(quote); index++; }
                    else { quoted = false; closed = true; }
                }
                else cell.Append(value);
            }
            else if (value == delimiter) { AddCell(); closed = false; }
            else if (value is '\r' or '\n')
            {
                if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                AddRow();
            }
            else if (!dialect.QuoteDisabled && !closed && cell.Length == 0 && value == quote) quoted = true;
            else
            {
                if (closed || !dialect.QuoteDisabled && value == quote) throw Invalid();
                cell.Append(value);
            }
            if (cell.Length > CsvDocumentPolicy.MaximumCellBytes) throw Limit();
        }
        if (quoted) throw Invalid();
        if (started) AddRow();
        return rows;

        InvalidCsv Invalid() => new(new(rows.Count + 1, cells.Count + 1));
        void AddCell()
        {
            if (cells.Count >= CsvDocumentPolicy.MaximumColumns || ++nodes > CsvDocumentPolicy.MaximumNodes ||
                Encoding.UTF8.GetByteCount(cell.ToString()) > CsvDocumentPolicy.MaximumCellBytes) throw Limit();
            cells.Add(cell.ToString()); cell.Clear();
        }
        void AddRow()
        {
            AddCell();
            if (rows.Count >= CsvDocumentPolicy.MaximumRecords || ++nodes > CsvDocumentPolicy.MaximumNodes) throw Limit();
            rows.Add(cells.ToArray()); cells.Clear(); closed = false; started = false;
        }
    }

    private static RegressionCsvLocation PrefixLocation(string prefix, CsvDocumentDialect dialect, CancellationToken token)
    {
        var row = 1;
        var column = 1;
        var quoted = false;
        var cellStart = true;
        var quote = dialect.QuoteDisabled ? '\0' : dialect.Quote![0];
        for (var index = prefix.StartsWith('\ufeff') ? 1 : 0; index < prefix.Length; index++)
        {
            if ((index & 4095) == 0) token.ThrowIfCancellationRequested();
            var value = prefix[index];
            if (quoted)
            {
                if (value == quote)
                {
                    if (index + 1 < prefix.Length && prefix[index + 1] == quote) index++;
                    else quoted = false;
                }
            }
            else if (value == dialect.Delimiter[0]) { column++; cellStart = true; }
            else if (value is '\r' or '\n')
            {
                if (value == '\r' && index + 1 < prefix.Length && prefix[index + 1] == '\n') index++;
                row++; column = 1; cellStart = true;
            }
            else
            {
                if (cellStart && !dialect.QuoteDisabled && value == quote) quoted = true;
                cellStart = false;
            }
        }
        return new(row, column);
    }

    private static RegressionComparisonException Limit() => new(RegressionComparisonFailure.ResourceLimit);
    private sealed class InvalidCsv(RegressionCsvLocation location) : Exception
    {
        internal RegressionCsvLocation Location { get; } = location;
    }
}
