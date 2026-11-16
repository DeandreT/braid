using System.Text.Json;
using Braid.Domain;

namespace Braid.Application;

public sealed record XmlNativeBoundaryMetadata(string Id, XmlDocumentRole Role, XmlDocumentMetadata Metadata);

/// <summary>
/// Extracts explicit saved options from the native artifact, never a filename, media type, schema display or sample.
/// This is a metadata projection, not native decoding, schema/graph admission, an emission proof, or build approval.
/// The caller must obtain the exact authorized revision/digest through the authoritative native-source port first.
/// </summary>
public static class XmlNativeMetadata
{
    // Pinned FormatOptions inventory, including defaults that other profiles are allowed to retain.
    // Do not ignore a new upstream field or erase a nondefault value to manufacture XML support.
    private static readonly HashSet<string> FalseDefaults = new(StringComparer.Ordinal)
    {
        "lenient_segments", "xml_allow_inactive_root_type_members", "xml_root_view_read_policy", "local_xml_file_set",
        "json_document", "json5", "csv_quote_disabled", "csv_utf8_bom", "csv_preserve_empty_strings", "json_lines",
        "xlsx_update_existing"
    };
    private static readonly HashSet<string> NullDefaults = new(StringComparer.Ordinal)
    {
        "edi_kind", "edi_config_reference", "edi_autocomplete", "x12_separators", "x12_interchange_version",
        "idoc", "idoc_native_config", "idoc_native_text_settings", "swift_mt", "wsdl", "json_schema_unresolved_reference",
        "tabular_kind", "delimiter", "csv_quote", "csv_text_repair_dependency", "has_header_row", "fixed_width",
        "flextext", "pdf", "http_get", "external_source", "protobuf", "xbrl", "xlsx_sheet", "xlsx_start_row",
        "xlsx_composite", "xlsx_worksheet_set", "xlsx_grid", "xlsx_hierarchical", "sqlite_read"
    };
    private static readonly HashSet<string> EmptyArrayDefaults = new(StringComparer.Ordinal)
    {
        "edi_implied_decimals", "edi_lexical_formats", "edi_value_constraints", "xlsx_columns", "xlsx_headers", "xlsx_rows"
    };

    public static IReadOnlyList<XmlNativeBoundaryMetadata> Read(NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (artifact.ByteCount > XmlDocumentPolicy.MaximumNativeBytes)
            throw new XmlDocumentException(XmlDocumentFailure.NativeByteLimit,
                observed: artifact.ByteCount, limit: XmlDocumentPolicy.MaximumNativeBytes);
        try
        {
            using var document = JsonDocument.Parse(artifact.CopyBytes(), new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            UniqueObject(root);
            if (root.TryGetProperty("__ferrule_file", out var marker))
            {
                UniqueObject(marker);
                if (Text(Required(marker, "kind")) != "project" ||
                    !Required(marker, "version").TryGetInt32(out var version) || version != 2 ||
                    root.TryGetProperty("source_options", out _) || root.TryGetProperty("target_options", out _) ||
                    root.TryGetProperty("extra_sources", out _) || root.TryGetProperty("extra_targets", out _)) throw Invalid();
                root = Required(root, "document");
                UniqueObject(root);
            }
            else if (root.TryGetProperty("document", out _)) throw Invalid();

            // These are not native Project fields. Do not infer endpoints from similar-looking names.
            if (root.TryGetProperty("named_sources", out _) || root.TryGetProperty("named_targets", out _)) throw Invalid();
            var inputs = new List<XmlDocumentMetadata> { Options(Required(root, "source_options"), XmlDocumentRole.Input) };
            var outputs = new List<XmlDocumentMetadata> { Options(Required(root, "target_options"), XmlDocumentRole.Output) };
            ReadNamed("extra_sources", inputs, XmlDocumentRole.Input, XmlDocumentPolicy.MaximumInputs);
            ReadNamed("extra_targets", outputs, XmlDocumentRole.Output, XmlDocumentPolicy.MaximumOutputs);
            XmlDocumentMetadataJson.MeasureOperation(inputs, outputs);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<XmlNativeBoundaryMetadata>(inputs.Count + outputs.Count);
            for (var index = 0; index < inputs.Count; index++)
                result.Add(new(index == 0 ? "primary-input" : $"named-input:{index - 1}", XmlDocumentRole.Input, inputs[index]));
            for (var index = 0; index < outputs.Count; index++)
                result.Add(new(index == 0 ? "primary-output" : $"named-output:{index - 1}", XmlDocumentRole.Output, outputs[index]));
            return result.AsReadOnly();

            void ReadNamed(string name, List<XmlDocumentMetadata> values, XmlDocumentRole role, int maximum)
            {
                if (!root.TryGetProperty(name, out var named)) return;
                if (named.ValueKind != JsonValueKind.Array) throw Invalid();
                if (named.GetArrayLength() >= maximum)
                    throw new XmlDocumentException(XmlDocumentFailure.DocumentCountLimit,
                        observed: named.GetArrayLength() + 1, limit: maximum);
                foreach (var boundary in named.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UniqueObject(boundary);
                    values.Add(Options(Required(boundary, "options"), role));
                }
            }
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
    }

    private static XmlDocumentMetadata Options(JsonElement value, XmlDocumentRole role)
    {
        UniqueObject(value);
        var declared = false;
        XmlSchemaHints? hints = null;
        foreach (var option in value.EnumerateObject())
        {
            if (option.Name == "xml_document")
            {
                if (option.Value.ValueKind != JsonValueKind.True) throw Invalid();
                declared = true;
            }
            else if (option.Name == "xml_schema_hints")
            {
                if (option.Value.ValueKind != JsonValueKind.Null)
                {
                    if (role != XmlDocumentRole.Output) throw Invalid();
                    hints = Hints(option.Value);
                }
            }
            else if (FalseDefaults.Contains(option.Name))
            {
                if (option.Value.ValueKind != JsonValueKind.False) throw Invalid();
            }
            else if (NullDefaults.Contains(option.Name))
            {
                if (option.Value.ValueKind != JsonValueKind.Null) throw Invalid();
            }
            else if (EmptyArrayDefaults.Contains(option.Name))
            {
                if (option.Value.ValueKind != JsonValueKind.Array || option.Value.GetArrayLength() != 0) throw Invalid();
            }
            else if (option.Name is "mfd_decimal_input_names" or "mfd_optional_parser_input_names")
            {
                if (option.Value.ValueKind != JsonValueKind.Object || option.Value.EnumerateObject().Any()) throw Invalid();
            }
            else throw Invalid();
        }
        var metadata = new XmlDocumentMetadata(XmlDocumentPolicy.MetadataSchema, XmlDocumentPolicy.SafetyProfile, hints);
        if (!declared || !XmlDocumentPolicy.Admits(metadata, role)) throw Invalid();
        return metadata;
    }

    private static XmlSchemaHints Hints(JsonElement value)
    {
        UniqueObject(value);
        string? noNamespace = null;
        var pairs = new List<XmlSchemaLocation>();
        foreach (var field in value.EnumerateObject())
        {
            switch (field.Name)
            {
                case "no_namespace_location":
                    noNamespace = field.Value.ValueKind == JsonValueKind.Null ? null : Text(field.Value);
                    break;
                case "locations":
                    if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() > XmlDocumentPolicy.MaximumSchemaHintPairs)
                        throw Invalid();
                    foreach (var pair in field.Value.EnumerateArray())
                    {
                        UniqueObject(pair);
                        if (pair.EnumerateObject().Count() != 2) throw Invalid();
                        pairs.Add(new(Text(Required(pair, "namespace")), Text(Required(pair, "location"))));
                    }
                    break;
                default: throw Invalid();
            }
        }
        return new(noNamespace, pairs);
    }

    private static JsonElement Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) ? field : throw Invalid();

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();

    private static void UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!seen.Add(field.Name)) throw Invalid();
    }

    private static XmlDocumentException Invalid() => new(XmlDocumentFailure.InvalidNativeMetadata);
}
