using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Saved flat-sheet settings with the pinned engine's defaults resolved. The native codec owns cell semantics.</summary>
public sealed record XlsxDocumentSettings
{
    public XlsxDocumentSettings(string schema, string? sheet, int startRow, IReadOnlyList<int> columns, IReadOnlyList<string> headers, bool hasHeaderRow)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(headers);
        if (schema != XlsxDocumentPolicy.SettingsSchema) throw new PreviewRunException("Unknown XLSX settings schema.");
        if (sheet is not null && (sheet.Length == 0 || sheet.EnumerateRunes().Count() > 31 || sheet.Any(char.IsControl)) ||
            startRow is < 1 or > 1_048_576 || columns.Count > XlsxDocumentPolicy.MaximumColumns ||
            columns.Any(column => column is < 1 or > 16_384) || headers.Count > XlsxDocumentPolicy.MaximumColumns || headers.Any(header => header is null))
            throw new PreviewRunException("XLSX sheet settings are outside their metadata limits.");
        Schema = schema;
        Sheet = sheet;
        StartRow = startRow;
        Columns = columns.ToArray();
        Headers = headers.ToArray();
        HasHeaderRow = hasHeaderRow;
        if (XlsxSettingsJson.Utf8ByteCount(this) > XlsxDocumentPolicy.MaximumSettingsBytes)
            throw new PreviewRunException("XLSX sheet settings exceed their metadata limit.");
    }

    public string Schema { get; }
    public string? Sheet { get; }
    public int StartRow { get; }
    public IReadOnlyList<int> Columns { get; }
    public IReadOnlyList<string> Headers { get; }
    public bool HasHeaderRow { get; }

    public bool Equals(XlsxDocumentSettings? other) => other is not null && Schema == other.Schema && Sheet == other.Sheet &&
        StartRow == other.StartRow && HasHeaderRow == other.HasHeaderRow && Columns.SequenceEqual(other.Columns) &&
        Headers.SequenceEqual(other.Headers, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Schema, Sheet, StartRow, HasHeaderRow, Columns.Count, Headers.Count);
    public override string ToString() => nameof(XlsxDocumentSettings);
}

public static class XlsxDocumentPolicy
{
    public const string EnabledSetting = "BRAID_PREVIEWS_XLSX_ENABLED";
    public const string RegressionEnabledSetting = "BRAID_REGRESSIONS_XLSX_ENABLED";
    public const string Profile = "braid.ferrule-xlsx-preview.v1";
    public const string Capability = "braid.xlsx.flat-workbook.v1";
    public const string SettingsSchema = "braid.xlsx-settings.v1";
    public const string MediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string BuildRefusal = "The pinned Ferrule engine has no generated C# XLSX workbook adapter. Native preview and exact-byte regression do not qualify a generated build.";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumEntries = 64;
    public const long MaximumEntryBytes = 16 * 1024 * 1024;
    public const long MaximumExpandedBytes = 32 * 1024 * 1024;
    public const int MaximumExpansionRatio = 200;
    public const int MaximumPartDepth = 4;
    public const int MaximumColumns = 256;
    public const int MaximumSettingsBytes = 16 * 1024;

    public static bool Admits(XlsxDocumentSettings? settings, string mediaType) =>
        settings is not null && settings.Schema == SettingsSchema && mediaType == MediaType;

    /// <summary>
    /// Central-directory admission before storage or dispatch: entry count, declared per-entry and total
    /// expansion, per-entry and package ratios, part nesting and macro/embedded binaries. Nothing is
    /// decompressed here; the native reader enforces the same budgets against actual expansion.
    /// </summary>
    public static void CheckBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new PreviewRunException("XLSX workbook is empty or exceeds its byte limit.");
        if (!bytes.StartsWith("PK\x03\x04"u8)) throw new PreviewRunException("XLSX requires an Office Open XML ZIP package.");
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes.ToArray(), writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count is 0 or > MaximumEntries) throw new PreviewRunException("XLSX package exceeds its entry limit.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (name.Length is 0 or > 256 || name.Any(c => c is < '!' or > '~' or '\\') || name.EndsWith('/') ||
                    name.Count(c => c == '/') >= MaximumPartDepth ||
                    name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || name.Contains("vba", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new PreviewRunException("XLSX package contains an unsupported part.");
                if (entry.Length > MaximumEntryBytes || entry.Length > Math.Max(entry.CompressedLength, 1) * MaximumExpansionRatio)
                    throw new PreviewRunException("XLSX package part exceeds its expansion limit.");
                total += entry.Length;
            }
            if (total > Math.Min(MaximumExpandedBytes, (long)bytes.Length * MaximumExpansionRatio))
                throw new PreviewRunException("XLSX package exceeds its expansion limit.");
        }
        catch (InvalidDataException) { throw new PreviewRunException("XLSX package is not a readable ZIP archive."); }
    }
}

public static class XlsxSettingsJson
{
    private static readonly JsonWriterOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Write(Utf8JsonWriter json, XlsxDocumentSettings value)
    {
        json.WriteStartObject();
        json.WriteString("schema", value.Schema);
        if (value.Sheet is null) json.WriteNull("sheet"); else json.WriteString("sheet", value.Sheet);
        json.WriteNumber("startRow", value.StartRow);
        json.WriteStartArray("columns"); foreach (var column in value.Columns) json.WriteNumberValue(column); json.WriteEndArray();
        json.WriteStartArray("headers"); foreach (var header in value.Headers) json.WriteStringValue(header); json.WriteEndArray();
        json.WriteBoolean("hasHeaderRow", value.HasHeaderRow);
        json.WriteEndObject();
    }

    public static string Encode(XlsxDocumentSettings value)
    {
        using var stream = new MemoryStream(); using (var json = new Utf8JsonWriter(stream)) Write(json, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The native budget counts literal UTF-8, not HTML-safe \u escapes.
    internal static long Utf8ByteCount(XlsxDocumentSettings value)
    {
        using var stream = new MemoryStream(); using (var json = new Utf8JsonWriter(stream, Relaxed)) Write(json, value);
        return stream.Length;
    }

    public static XlsxDocumentSettings Read(JsonElement value)
    {
        // A null sheet may be omitted by serializers that skip nulls; every other member is required.
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Where(name => name != "sheet").Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "columns", "hasHeaderRow", "headers", "schema", "startRow" }) ||
            value.EnumerateObject().Count(p => p.Name == "sheet") > 1)
            throw new PreviewRunException("Invalid XLSX settings.");
        try
        {
            var sheet = value.TryGetProperty("sheet", out var named) && named.ValueKind != JsonValueKind.Null ? named.GetString()! : null;
            return new(value.GetProperty("schema").GetString()!, sheet,
                value.GetProperty("startRow").GetInt32(), value.GetProperty("columns").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
                value.GetProperty("headers").EnumerateArray().Select(item => item.GetString()!).ToArray(), value.GetProperty("hasHeaderRow").GetBoolean());
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        { throw new PreviewRunException("Invalid XLSX settings."); }
    }

    public static XlsxDocumentSettings Decode(string value)
    {
        try { using var document = JsonDocument.Parse(value); return Read(document.RootElement); }
        catch (JsonException) { throw new PreviewRunException("Invalid XLSX settings."); }
    }
}

/// <summary>Reads only explicit saved flat-sheet metadata; no workbook, path, graph or resource is opened.</summary>
public static class XlsxNativeSettings
{

    public static (XlsxDocumentSettings Input, XlsxDocumentSettings Output) Read(NativeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.ByteCount > PreviewPolicy.MaximumNativeBytes) throw new PreviewRunException("Native metadata exceeds its byte limit.");
        try
        {
            using var document = JsonDocument.Parse(artifact.CopyBytes(), new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            Unique(root);
            if (root.TryGetProperty("__ferrule_file", out var marker))
            {
                if (marker.GetProperty("kind").GetString() != "project" || marker.GetProperty("version").GetInt32() != 2) throw new JsonException();
                root = root.GetProperty("document");
                Unique(root);
            }
            foreach (var key in new[] { "extra_sources", "extra_targets" })
                if (root.TryGetProperty(key, out var items) && (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 0)) throw new JsonException();
            var iteration = root.GetProperty("root").GetProperty("source");
            if (iteration.ValueKind != JsonValueKind.Array || iteration.GetArrayLength() != 0) throw new JsonException();
            return (Portable(root.GetProperty("source_options")), Portable(root.GetProperty("target_options")));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { throw new PreviewRunException("The revision does not declare primary flat XLSX workbooks."); }
    }

    private static XlsxDocumentSettings Portable(JsonElement options)
    {
        var settings = ReadOptions(options, ["tabular_kind", "xlsx_sheet", "has_header_row"]);
        if (!options.GetProperty("has_header_row").GetBoolean() || settings.Sheet is { } sheet &&
            (sheet.Equals("History", StringComparison.OrdinalIgnoreCase) || sheet.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))) throw new JsonException();
        return settings;
    }

    // Full closed option/schema/graph admission remains native. This projection cannot enable execution.
    private static XlsxDocumentSettings ReadOptions(JsonElement options, string[] allowed)
    {
        Unique(options);
        if (options.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)) ||
            options.GetProperty("tabular_kind").GetString() != "xlsx") throw new JsonException();
        string? sheet = options.TryGetProperty("xlsx_sheet", out var s) ? s.GetString() ?? throw new JsonException() : null;
        var start = options.TryGetProperty("xlsx_start_row", out var r) ? r.GetInt32() : 1;
        var header = !options.TryGetProperty("has_header_row", out var h) || h.ValueKind == JsonValueKind.Null || h.GetBoolean();
        int[] columns = options.TryGetProperty("xlsx_columns", out var c) ? [.. c.EnumerateArray().Select(item => item.GetInt32())] : [];
        string[] headers = options.TryGetProperty("xlsx_headers", out var t) ? [.. t.EnumerateArray().Select(item => item.GetString() ?? throw new JsonException())] : [];
        return new(XlsxDocumentPolicy.SettingsSchema, sheet, start, columns, headers, header);
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) throw new JsonException();
    }
}
