namespace Weft.Application;

/// <summary>Versioned contract for direct, bounded primary CSV-to-CSV previews.</summary>
public static class CsvDocumentPolicy
{
    public const string Capability = "weft.csv.flat-document.v1";
    public const string Profile = "weft.ferrule-csv-preview.v1";
    public const string RegressionEnabledSetting = "WEFT_REGRESSIONS_CSV_ENABLED";
    public const string DialectSchema = "weft.csv-dialect.v1";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumCellBytes = 1024 * 1024;
    public const int MaximumRecords = 100_000; // Includes the physical header, when selected.
    public const int MaximumColumns = 256;
    public const int MaximumNodes = 1_000_000;

    public static bool AdmitsMediaType(string mediaType) => mediaType is "text/csv" or "text/tab-separated-values";

    public static bool Admits(CsvDocumentDialect? dialect, string mediaType) => dialect is not null &&
        dialect.Schema == DialectSchema && dialect.Delimiter is { Length: 1 } &&
        dialect.Delimiter[0] is > '\0' and <= '\x7f' and not '\r' and not '\n' &&
        (dialect.QuoteDisabled ? dialect.Quote is null :
            dialect.Quote is { Length: 1 } && dialect.Quote[0] is >= '!' and <= '~' && dialect.Quote != dialect.Delimiter) &&
        mediaType == (dialect.Delimiter == "\t" ? "text/tab-separated-values" : "text/csv");
}

/// <summary>Resolved native settings, not guessed from content or a filename. Raw bytes remain separate.</summary>
public sealed record CsvDocumentDialect(string Schema, string Delimiter, string? Quote, bool QuoteDisabled,
    bool HasHeaderRow, bool Utf8Bom, bool PreserveEmptyStrings);
