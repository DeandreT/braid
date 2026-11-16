using System.Text;
using System.Text.Json;

namespace Braid.Application;

/// <summary>One canonical dialect representation shared by private CSV envelopes.</summary>
public static class CsvDialectJson
{
    public static void Write(Utf8JsonWriter json, CsvDocumentDialect value)
    {
        if (!CsvDocumentPolicy.Admits(value, MediaType(value))) throw new ArgumentException("Invalid CSV dialect.", nameof(value));
        json.WriteStartObject();
        json.WriteString("schema", value.Schema);
        json.WriteString("delimiter", value.Delimiter);
        json.WriteString("quote", value.Quote);
        json.WriteBoolean("quoteDisabled", value.QuoteDisabled);
        json.WriteBoolean("hasHeaderRow", value.HasHeaderRow);
        json.WriteBoolean("utf8Bom", value.Utf8Bom);
        json.WriteBoolean("preserveEmptyStrings", value.PreserveEmptyStrings);
        json.WriteEndObject();
    }

    public static string Encode(CsvDocumentDialect value)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) Write(json, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static CsvDocumentDialect Read(JsonElement value)
    {
        string[] fields = ["schema", "delimiter", "quote", "quoteDisabled", "hasHeaderRow", "utf8Bom", "preserveEmptyStrings"];
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid CSV dialect.");
        foreach (var field in value.EnumerateObject())
            if (!remaining.Remove(field.Name)) throw new JsonException("Invalid CSV dialect.");
        if (remaining.Count != 0) throw new JsonException("Invalid CSV dialect.");
        var result = new CsvDocumentDialect(value.GetProperty("schema").GetString()!, value.GetProperty("delimiter").GetString()!,
            value.GetProperty("quote").GetString(), value.GetProperty("quoteDisabled").GetBoolean(),
            value.GetProperty("hasHeaderRow").GetBoolean(), value.GetProperty("utf8Bom").GetBoolean(),
            value.GetProperty("preserveEmptyStrings").GetBoolean());
        if (!CsvDocumentPolicy.Admits(result, MediaType(result))) throw new JsonException("Invalid CSV dialect.");
        return result;
    }

    public static CsvDocumentDialect Decode(string value)
    {
        using var document = JsonDocument.Parse(value);
        return Read(document.RootElement);
    }

    public static string MediaType(CsvDocumentDialect value) => value.Delimiter == "\t" ? "text/tab-separated-values" : "text/csv";
}
