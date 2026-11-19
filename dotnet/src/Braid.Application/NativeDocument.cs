using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// One closed native-only document format with its own versioned profile, capability, settings schema
/// and media type. The pinned Ferrule codec owns schema, instance and serialization semantics.
/// </summary>
public sealed class NativeDocumentFormat
{
    public static readonly NativeDocumentFormat FlexText = new("flextext", "FlexText", PreviewExecutionProfile.FlexTextDocument, "text/plain", "txt", true);
    public static readonly NativeDocumentFormat Xlsx = new("xlsx", "XLSX", PreviewExecutionProfile.XlsxDocument, XlsxDocumentPolicy.MediaType, "xlsx", false);
    public static readonly NativeDocumentFormat Protobuf = new("protobuf", "Protocol Buffers", PreviewExecutionProfile.ProtobufDocument,
        "application/x-protobuf", "pb", requiresUtf8: false);
    public static readonly NativeDocumentFormat Xbrl = new("xbrl", "XBRL", PreviewExecutionProfile.XbrlDocument,
        "application/xbrl+xml", "xbrl", requiresUtf8: true);
    public static IReadOnlyList<NativeDocumentFormat> All { get; } = Array.AsReadOnly(new[] { FlexText, Xlsx, Protobuf, Xbrl });
    public const int MaximumSettingsBytes = 16 * 1024;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private NativeDocumentFormat(string key, string label, PreviewExecutionProfile execution, string mediaType, string extension, bool requiresUtf8)
    {
        Key = key;
        Label = label;
        Execution = execution;
        MediaType = mediaType;
        Extension = extension;
        RequiresUtf8 = requiresUtf8;
        var upper = key.ToUpperInvariant();
        EnabledSetting = $"BRAID_PREVIEWS_{upper}_ENABLED";
        RegressionEnabledSetting = $"BRAID_REGRESSIONS_{upper}_ENABLED";
        Profile = $"braid.ferrule-{key}-preview.v1";
        Capability = key == "xlsx" ? XlsxDocumentPolicy.Capability : $"braid.{key}.document.v1";
        SettingsSchema = $"braid.{key}-settings.v1";
        AdapterBuild = $"braid.ferrule-{key}-preview-adapter.v1";
        RequestSchema = $"braid.{key}-preview-request.v1";
        CaseSchema = $"braid.{key}-regression-case.v1";
        ResultSchema = $"braid.{key}-regression-run-result.v1";
        BuildStatus = $"unsupported{execution.ToString().Replace("Document", "", StringComparison.Ordinal)}Build";
        BuildRefusal = $"The pinned Ferrule engine has no generated C# {label} document adapter. Native preview and exact-byte regression do not qualify a generated build.";
    }

    /// <summary>The saved native boundary option key, for example <c>protobuf</c>.</summary>
    public string Key { get; }
    public string Label { get; }
    public PreviewExecutionProfile Execution { get; }
    public string MediaType { get; }
    public string Extension { get; }
    public bool RequiresUtf8 { get; }
    public string EnabledSetting { get; }
    public string RegressionEnabledSetting { get; }
    public string Profile { get; }
    public string Capability { get; }
    public string SettingsSchema { get; }
    public string AdapterBuild { get; }
    public string RequestSchema { get; }
    public string CaseSchema { get; }
    public string ResultSchema { get; }
    public string BuildStatus { get; }
    public string BuildRefusal { get; }

    public static IReadOnlyList<NativeDocumentFormat> CatalogFormats { get; } = Array.AsReadOnly(new[] { FlexText, Xlsx, Protobuf, Xbrl });
    public const string XlsxOutputRefusal = "XLSX output requires a worksheet thread unavailable in the restricted service runner.";

    public static IReadOnlyList<NativeDocumentFormat> EnabledFormats(Func<string,string?> read, bool regression) =>
        CatalogFormats.Where(f => NativeDocumentPolicy.Enabled(read, regression ? f.RegressionEnabledSetting : f.EnabledSetting, f != Xlsx)).ToArray();
    public static NativeDocumentFormat? FromProfile(string? profile) => All.FirstOrDefault(format => format.Profile == profile);
    public static NativeDocumentFormat? FromExecution(PreviewExecutionProfile profile) => All.FirstOrDefault(format => format.Execution == profile);
    public static NativeDocumentFormat? FromSettingsSchema(string? schema) => All.FirstOrDefault(format => format.SettingsSchema == schema);
    public static NativeDocumentFormat? FromMediaType(string? media) => All.FirstOrDefault(format => format.MediaType == media);

    public void CheckBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new PreviewRunException($"{Label} document exceeds its byte limit.");
        if (this == Xlsx) { if (bytes.Length > 256 * 1024) throw new PreviewRunException("Portable workbook exceeds 256 KiB."); XlsxDocumentPolicy.CheckBytes(bytes); }
        if (!RequiresUtf8) return;
        try { _ = Utf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new PreviewRunException($"{Label} requires strict UTF-8."); }
    }

    public override string ToString() => Key;
}

/// <summary>Saved boundary options only, as canonical JSON. Never a caller override.</summary>
public sealed record NativeDocumentSettings
{
    public NativeDocumentSettings(string schema, string optionsJson)
    {
        Format = NativeDocumentFormat.FromSettingsSchema(schema) ?? throw new PreviewRunException("Unknown native document settings schema.");
        Schema = schema;
        OptionsJson = FlexTextDocumentPolicy.CanonicalJson(optionsJson, NativeDocumentFormat.MaximumSettingsBytes, $"{Format.Label} options");
    }
    public string Schema { get; }
    public string OptionsJson { get; }
    [JsonIgnore] public NativeDocumentFormat Format { get; }
    public bool Admits(string mediaType) => Format.MediaType == mediaType;
    public override string ToString() => nameof(NativeDocumentSettings);
}

public static class NativeDocumentSettingsJson
{
    public static void Write(Utf8JsonWriter json, NativeDocumentSettings value)
    {
        json.WriteStartObject(); json.WriteString("schema", value.Schema); json.WriteString("optionsJson", value.OptionsJson); json.WriteEndObject();
    }
    public static string Encode(NativeDocumentSettings value)
    {
        using var stream = new MemoryStream(); using (var json = new Utf8JsonWriter(stream)) Write(json, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    public static NativeDocumentSettings Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "optionsJson", "schema" }))
            throw new PreviewRunException("Invalid native document settings.");
        return new(value.GetProperty("schema").GetString()!, value.GetProperty("optionsJson").GetString()!);
    }
    public static NativeDocumentSettings Decode(string value)
    { using var document = JsonDocument.Parse(value); return Read(document.RootElement); }
}

/// <summary>Reads only explicit saved metadata; no schema, taxonomy, file or network resource is opened.</summary>
public static class NativeDocumentNativeSettings
{
    public static (NativeDocumentSettings Input, NativeDocumentSettings Output) Read(NativeArtifact artifact, NativeDocumentFormat format)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(format);
        if (format == NativeDocumentFormat.FlexText)
        {
            var settings = FlexTextNativeSettings.Read(artifact);
            return (new(format.SettingsSchema, settings.Input.LayoutJson), new(format.SettingsSchema, settings.Output.LayoutJson));
        }
        if (format == NativeDocumentFormat.Xlsx)
        {
            var settings = XlsxNativeSettings.Read(artifact);
            return (new(format.SettingsSchema, XlsxSettingsJson.Encode(settings.Input)), new(format.SettingsSchema, XlsxSettingsJson.Encode(settings.Output)));
        }
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
            return (ReadOptions(root.GetProperty("source_options"), format), ReadOptions(root.GetProperty("target_options"), format));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new PreviewRunException($"The revision does not declare primary {format.Label} boundaries."); }
    }

    private static NativeDocumentSettings ReadOptions(JsonElement options, NativeDocumentFormat format)
    {
        // Full closed option admission remains native. This projection cannot enable execution.
        Unique(options);
        return new(format.SettingsSchema, options.GetProperty(format.Key).GetRawText());
    }

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) throw new JsonException();
    }
}
