using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

/// <summary>Saved layout metadata only. The pinned native codec owns layout and instance semantics.</summary>
public sealed record FlexTextDocumentSettings
{
    public FlexTextDocumentSettings(string schema, string layoutJson)
    {
        if (schema != FlexTextDocumentPolicy.SettingsSchema) throw new PreviewRunException("Unknown FlexText settings schema.");
        Schema = schema;
        LayoutJson = FlexTextDocumentPolicy.CanonicalLayout(layoutJson);
    }
    public string Schema { get; }
    public string LayoutJson { get; }
    public override string ToString() => nameof(FlexTextDocumentSettings);
}

public static class FlexTextDocumentPolicy
{
    public const string EnabledSetting = "BRAID_PREVIEWS_FLEXTEXT_ENABLED";
    public const string RegressionEnabledSetting = "BRAID_REGRESSIONS_FLEXTEXT_ENABLED";
    public const string Profile = "braid.ferrule-flextext-preview.v1";
    public const string Capability = "braid.flextext.document.v1";
    public const string SettingsSchema = "braid.flextext-settings.v1";
    public const string MediaType = "text/plain";
    public const string BuildRefusal = "The pinned Ferrule engine has no generated C# FlexText document-byte adapter. Native preview and exact-byte regression do not qualify a generated build.";
    public const int MaximumLayoutBytes = 16 * 1024;
    public const int MaximumRawLayoutBytes = PreviewPolicy.MaximumNativeBytes;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool Admits(FlexTextDocumentSettings? settings, string mediaType) =>
        settings is not null && settings.Schema == SettingsSchema && mediaType == MediaType;

    public static void CheckBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new PreviewRunException("FlexText document exceeds its byte limit.");
        try { _ = Utf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new PreviewRunException("FlexText requires strict UTF-8."); }
    }

    internal static string CanonicalLayout(string text) => CanonicalJson(text, MaximumLayoutBytes, "FlexText layout");

    internal static string CanonicalJson(string text, int maximumBytes, string label)
    {
        try
        {
            if (Utf8.GetByteCount(text) > MaximumRawLayoutBytes) throw new PreviewRunException($"{label} exceeds its outer metadata limit.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            using var stream = new MemoryStream();
            // This is an inner JSON string, never rendered as HTML. Native serde_json
            // counts literal UTF-8, rather than HTML-safe \u escaping, against its cap.
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                Write(document.RootElement, json);
            // Utf8JsonWriter escapes supplementary scalars even with the relaxed
            // encoder. Serde writes them as UTF-8. Normalize only JSON string
            // escapes, never payload bytes, so both metadata budgets count the
            // same representation (and an escaped literal backslash stays literal).
            var canonical = UnescapeUnicode(Utf8.GetString(stream.ToArray()));
            if (Utf8.GetByteCount(canonical) > maximumBytes) throw new PreviewRunException($"{label} exceeds its metadata limit.");
            return canonical;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new PreviewRunException($"Invalid {label} metadata."); }

        static void Write(JsonElement value, Utf8JsonWriter json)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new JsonException();
                json.WriteStartObject();
                foreach (var property in properties) { json.WritePropertyName(property.Name); Write(property.Value, json); }
                json.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            { json.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, json); json.WriteEndArray(); }
            else value.WriteTo(json);
        }
    }

    private static string UnescapeUnicode(string json)
    {
        var result = new StringBuilder(json.Length);
        for (var index = 0; index < json.Length; index++)
        {
            if (json[index] != '\\' || index + 1 >= json.Length)
            {
                result.Append(json[index]);
                continue;
            }
            if (json[index + 1] != 'u')
            {
                result.Append(json[index]).Append(json[++index]);
                continue;
            }
            var scalar = Convert.ToInt32(json.Substring(index + 2, 4), 16);
            var length = 6;
            if (scalar is >= 0xD800 and <= 0xDBFF && index + 11 < json.Length &&
                json[index + 6] == '\\' && json[index + 7] == 'u')
            {
                var low = Convert.ToInt32(json.Substring(index + 8, 4), 16);
                if (low is >= 0xDC00 and <= 0xDFFF)
                {
                    scalar = char.ConvertToUtf32((char)scalar, (char)low);
                    length = 12;
                }
            }
            if (scalar >= 0x20 && scalar is not (0x22 or 0x5C) && scalar is not (>= 0xD800 and <= 0xDFFF))
                result.Append(char.ConvertFromUtf32(scalar));
            else
                result.Append(json, index, length);
            index += length - 1;
        }
        return result.ToString();
    }
}

/// <summary>Closed document metadata discrimination shared by stored and wire descriptors.</summary>
public static class NativeDocumentPolicy
{
    public static bool Enabled(Func<string, string?> read, string setting, bool parent)
    {
        var enabled = read(setting) switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw new ArgumentException($"{setting} must be true or false.")
        };
        if (enabled && !parent) throw new ArgumentException($"{setting} requires its parent workflow.");
        return enabled;
    }
    public static bool AllowsEmpty(PreviewExecutionProfile profile) => profile is PreviewExecutionProfile.CsvDocument or PreviewExecutionProfile.FlexTextDocument
        or PreviewExecutionProfile.ProtobufDocument or PreviewExecutionProfile.XbrlDocument;
    public static bool Admits(string media, CsvDocumentDialect? csv, FlexTextDocumentSettings? flex, NativeDocumentSettings? native = null) =>
        native is not null ? csv is null && flex is null && native.Admits(media) :
        flex is not null ? csv is null && FlexTextDocumentPolicy.Admits(flex, media) :
        CsvDocumentPolicy.AdmitsMediaType(media) ? CsvDocumentPolicy.Admits(csv, media) :
        csv is null && DocumentPreviewPolicy.AdmitsMediaType(media);
}

public static class FlexTextSettingsJson
{
    public static void Write(Utf8JsonWriter json, FlexTextDocumentSettings value)
    {
        json.WriteStartObject(); json.WriteString("schema", value.Schema); json.WriteString("layoutJson", value.LayoutJson); json.WriteEndObject();
    }
    public static string Encode(FlexTextDocumentSettings value)
    {
        using var stream = new MemoryStream(); using (var json = new Utf8JsonWriter(stream)) Write(json, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static FlexTextDocumentSettings Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "layoutJson", "schema" }))
            throw new PreviewRunException("Invalid FlexText settings.");
        return new(value.GetProperty("schema").GetString()!, value.GetProperty("layoutJson").GetString()!);
    }
    public static FlexTextDocumentSettings Decode(string value)
    { using var document = JsonDocument.Parse(value); return Read(document.RootElement); }
}

/// <summary>Reads only explicit saved metadata; no file paths, graph evaluation or resources are opened.</summary>
public static class FlexTextNativeSettings
{
    public static (FlexTextDocumentSettings Input, FlexTextDocumentSettings Output) Read(NativeArtifact artifact)
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
            return (ReadOptions(root.GetProperty("source_options")), ReadOptions(root.GetProperty("target_options")));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new PreviewRunException("The revision does not declare primary FlexText layouts."); }
    }
    private static FlexTextDocumentSettings ReadOptions(JsonElement options)
    {
        // Full closed option/schema/graph admission remains native. This projection cannot enable execution.
        if (options.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != options.EnumerateObject().Count()) throw new JsonException();
        return new(FlexTextDocumentPolicy.SettingsSchema, options.GetProperty("flextext").GetRawText());
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) throw new JsonException();
    }
}
