using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Reads saved native boundary options only. Filenames and sample contents never select a dialect.</summary>
public static class CsvNativeDialect
{
    public static (CsvDocumentDialect Input, CsvDocumentDialect Output) Read(NativeArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        try
        {
            using var document = JsonDocument.Parse(artifact.CopyBytes(), new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            foreach (var name in new[] { "named_sources", "named_targets", "extra_sources", "extra_targets" })
                if (root.TryGetProperty(name, out var named) && named.ValueKind != JsonValueKind.Null &&
                    (named.ValueKind != JsonValueKind.Array || named.GetArrayLength() != 0)) throw Invalid();
            return (ReadOptions(root.GetProperty("source_options")), ReadOptions(root.GetProperty("target_options")));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static CsvDocumentDialect ReadOptions(JsonElement options)
    {
        if (options.GetProperty("tabular_kind").GetString() != "csv") throw Invalid();
        // Resolve dialect settings only. Complete native option and flat-plan admission still
        // belongs to the worker; unrelated default options must not change this projection.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options.EnumerateObject())
            if (!seen.Add(option.Name)) throw Invalid();
        var disabled = Flag("csv_quote_disabled", false);
        var result = new CsvDocumentDialect(CsvDocumentPolicy.DialectSchema, Text("delimiter") ?? ",",
            disabled ? Text("csv_quote") : Text("csv_quote") ?? "\"", disabled,
            Flag("has_header_row", true), Flag("csv_utf8_bom", false), Flag("csv_preserve_empty_strings", false));
        if (!CsvDocumentPolicy.Admits(result, CsvDialectJson.MediaType(result))) throw Invalid();
        return result;

        string? Text(string name) => options.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
        bool Flag(string name, bool fallback) => options.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetBoolean() : fallback;
    }

    private static PreviewRunException Invalid() => new("The revision does not declare an admitted primary CSV dialect.");
}
