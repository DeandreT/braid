using System.Text;
using Braid.Application;

namespace Braid.Deployment;

public enum CsvDocumentBoundResult { Accepted, Invalid, Limit }

/// <summary>Allocation-free bounds on canonical generated CSV output. This does not transform or coerce cells.</summary>
public static class CsvDocumentBounds
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CsvDocumentBoundResult Check(ReadOnlySpan<byte> bytes, CsvDocumentDialect dialect, string mediaType)
    {
        if (!CsvDocumentPolicy.Admits(dialect, mediaType)) return CsvDocumentBoundResult.Invalid;
        if (bytes.Length > CsvDocumentPolicy.MaximumBytes) return CsvDocumentBoundResult.Limit;
        try { _ = StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { return CsvDocumentBoundResult.Invalid; }
        var delimiter = (byte)dialect.Delimiter[0];
        byte? quote = dialect.QuoteDisabled ? null : (byte)dialect.Quote![0];
        var quoted = false; var afterQuote = false; var present = false;
        var cellBytes = 0; var columns = 0; var records = 0; var nodes = 1L;
        var start = bytes.StartsWith("\uFEFF"u8) ? 3 : 0;
        for (var index = start; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (quoted)
            {
                if (value == quote)
                {
                    if (index + 1 < bytes.Length && bytes[index + 1] == quote) { index++; cellBytes++; }
                    else { quoted = false; afterQuote = true; }
                }
                else cellBytes++;
            }
            else if (value == delimiter)
            {
                present = true;
                if (++columns >= CsvDocumentPolicy.MaximumColumns) return CsvDocumentBoundResult.Limit;
                cellBytes = 0; afterQuote = false;
            }
            else if (value is (byte)'\r' or (byte)'\n')
            {
                if (present && !FinishRecord()) return CsvDocumentBoundResult.Limit;
                if (value == '\r' && index + 1 < bytes.Length && bytes[index + 1] == '\n') index++;
                cellBytes = 0; afterQuote = false;
            }
            else
            {
                if (afterQuote) return CsvDocumentBoundResult.Invalid;
                present = true;
                if (cellBytes == 0 && value == quote) quoted = true;
                else cellBytes++;
            }
            if (cellBytes > CsvDocumentPolicy.MaximumCellBytes) return CsvDocumentBoundResult.Limit;
        }
        if (quoted) return CsvDocumentBoundResult.Invalid;
        return !present || FinishRecord() ? CsvDocumentBoundResult.Accepted : CsvDocumentBoundResult.Limit;

        bool FinishRecord()
        {
            columns++;
            records++;
            if (columns > CsvDocumentPolicy.MaximumColumns || records > CsvDocumentPolicy.MaximumRecords) return false;
            if (!(dialect.HasHeaderRow && records == 1)) nodes += 1 + columns;
            if (nodes > CsvDocumentPolicy.MaximumNodes) return false;
            columns = 0; present = false;
            return true;
        }
    }
}
