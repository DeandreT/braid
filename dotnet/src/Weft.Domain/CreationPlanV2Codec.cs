using System.Globalization;
using System.Text.Json;

namespace Weft.Domain;

/// <summary>Canonical application encoding and deterministic lowering to the pinned compiler's closed grammar.</summary>
internal static class CreationPlanV2Codec
{
    internal static byte[] Write(CreationPlanV2 plan, bool compiler)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString(compiler ? "format" : "schema", compiler ? "weft.authoring.v1" : "weft.creation-plan.v2");
            w.WritePropertyName("source"); Boundary(w, plan.Source);
            w.WritePropertyName("target"); Boundary(w, plan.Target);
            if (compiler)
            {
                w.WritePropertyName("edit"); w.WriteStartObject(); w.WriteString("format", "ferrule.edit.v1");
                w.WritePropertyName("operations"); w.WriteStartArray();
                var id = 0;
                Operations(w, plan.Scope, [], ref id);
                w.WriteEndArray(); w.WriteEndObject();
            }
            else { w.WritePropertyName("scope"); Scope(w, plan.Scope); }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void Boundary(Utf8JsonWriter w, CreationBoundary boundary)
    {
        w.WriteStartObject(); w.WritePropertyName("schema"); Schema(w, boundary.Schema);
        w.WritePropertyName("options"); w.WriteStartObject();
        switch (boundary.Options)
        {
            case CreationJsonOptions: w.WriteString("kind", "json"); break;
            case CreationXmlOptions: w.WriteString("kind", "xml"); break;
            case CreationX12Options x12: w.WriteString("kind", "x12"); w.WriteString("interchange_version", x12.InterchangeVersion); break;
            case CreationEdifactOptions: w.WriteString("kind", "edifact"); break;
            case CreationCsvOptions csv: Tabular(w, "csv", csv.Header, csv.Quote, csv.Bom, csv.PreserveEmptyStrings); break;
            case CreationTsvOptions tsv: Tabular(w, "tsv", tsv.Header, tsv.Quote, tsv.Bom, tsv.PreserveEmptyStrings); break;
            default: throw new ArgumentException("Unsupported boundary options.");
        }
        w.WriteEndObject(); w.WriteEndObject();
    }

    private static void Tabular(Utf8JsonWriter w, string kind, bool header, bool quote, bool bom, bool empty)
    {
        w.WriteString("kind", kind); w.WriteBoolean("header", header); w.WriteBoolean("quote", quote);
        w.WriteBoolean("bom", bom); w.WriteBoolean("preserve_empty_strings", empty);
    }

    private static void Schema(Utf8JsonWriter w, CreationSchemaNode node)
    {
        w.WriteStartObject(); w.WriteString("name", node.Name); w.WritePropertyName("shape"); w.WriteStartObject();
        w.WriteString("kind", node.ScalarType is null ? "group" : "scalar");
        if (node.ScalarType is { } type) w.WriteString("scalar", Type(type));
        else
        {
            w.WritePropertyName("children"); w.WriteStartArray();
            foreach (var child in node.Children) Schema(w, child);
            w.WriteEndArray();
        }
        w.WriteEndObject(); w.WriteBoolean("repeating", node.Repeating); w.WriteBoolean("nullable", node.Nullable);
        Strings(w, "required", node.Required);
        w.WritePropertyName("xml");
        if (node.Xml is { } xml)
        {
            w.WriteStartObject(); w.WriteString("namespace", xml.Namespace); w.WriteBoolean("attribute", xml.Attribute);
            w.WriteBoolean("text", xml.Text); w.WriteBoolean("optional", xml.Optional); w.WriteBoolean("nillable", xml.Nillable);
            w.WriteEndObject();
        }
        else w.WriteNullValue();
        w.WriteEndObject();
    }

    private static void Scope(Utf8JsonWriter w, CreationScope scope)
    {
        w.WriteStartObject(); w.WriteString("target", scope.Target);
        if (scope.SourceIteration is { } iteration) Strings(w, "iteration", iteration); else w.WriteNull("iteration");
        w.WritePropertyName("bindings"); w.WriteStartArray();
        foreach (var binding in scope.Bindings)
        {
            w.WriteStartObject(); w.WriteString("target", binding.Target); w.WritePropertyName("value"); Value(w, binding.Value, null);
            w.WriteEndObject();
        }
        w.WriteEndArray(); w.WritePropertyName("children"); w.WriteStartArray();
        foreach (var child in scope.Children) Scope(w, child);
        w.WriteEndArray(); w.WriteEndObject();
    }

    private static void Operations(Utf8JsonWriter w, CreationScope scope, string[] address, ref int id, IReadOnlyList<string>? activeIteration = null)
    {
        if (scope.SourceIteration is { } iteration)
        {
            activeIteration = iteration;
            w.WriteStartObject(); w.WriteString("op", "set_iteration"); ScopeReference(w, "scope", address);
            w.WritePropertyName("source"); w.WriteStartObject(); w.WriteString("kind", "legacy"); Strings(w, "path", iteration);
            w.WriteEndObject(); w.WriteEndObject();
        }
        foreach (var binding in scope.Bindings)
        {
            w.WriteStartObject(); w.WriteString("op", "bind"); w.WritePropertyName("binding"); w.WriteStartObject();
            ScopeReference(w, "scope", address); w.WriteString("field", binding.Target); w.WriteEndObject();
            w.WritePropertyName("expression"); Value(w, binding.Value, (++id).ToString(CultureInfo.InvariantCulture), activeIteration);
            w.WriteEndObject();
        }
        for (var index = 0; index < scope.Children.Count; index++)
        {
            var child = scope.Children[index];
            w.WriteStartObject(); w.WriteString("op", "add_scope"); ScopeReference(w, "parent", address);
            w.WriteString("field", child.Target); w.WriteEndObject();
            Operations(w, child, [.. address, index.ToString(CultureInfo.InvariantCulture)], ref id, activeIteration);
        }
    }

    private static void ScopeReference(Utf8JsonWriter w, string name, IEnumerable<string> address)
    {
        w.WritePropertyName(name); w.WriteStartObject(); w.WritePropertyName("target"); w.WriteStartObject();
        w.WriteString("kind", "primary"); w.WriteEndObject(); Strings(w, "children", address); w.WriteEndObject();
    }

    private static void Value(Utf8JsonWriter w, CreationValue value, string? id, IReadOnlyList<string>? activeIteration = null)
    {
        w.WriteStartObject();
        switch (value)
        {
            case CreationSourceValue source:
                w.WriteString("kind", "source"); if (id is not null) w.WriteString("id", id);
                // Legacy SourceField frame pins use a relative path, unlike the application's absolute path.
                // Empty frames are not a primary-root selector: they only select a real empty-path iteration.
                if (id is not null && activeIteration is { Count: > 0 } &&
                    source.Path.Take(activeIteration.Count).SequenceEqual(activeIteration))
                {
                    Strings(w, "path", source.Path.Skip(activeIteration.Count));
                    Strings(w, "frame", activeIteration);
                }
                else Strings(w, "path", source.Path);
                break;
            case CreationConstantValue constant:
                w.WriteString("kind", "constant"); if (id is not null) w.WriteString("id", id);
                w.WritePropertyName("value"); w.WriteStartObject();
                w.WriteString("type", constant.Type is { } type ? Type(type) : "json_null");
                if (constant.Type is not null) w.WriteString("text", constant.Text);
                w.WriteEndObject(); break;
            default: throw new ArgumentException("Unsupported expression.");
        }
        w.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WritePropertyName(name); w.WriteStartArray(); foreach (var value in values) w.WriteStringValue(value); w.WriteEndArray();
    }

    private static string Type(CreationValueType type) => type switch
    {
        CreationValueType.String => "string",
        CreationValueType.Integer => "int",
        CreationValueType.Float => "float",
        CreationValueType.Boolean => "bool",
        _ => throw new ArgumentException("Unsupported scalar type.")
    };

    internal static CreationPlanV2 Parse(JsonElement root)
    {
        var count = 0;
        CheckJson(root, 0, ref count);
        Properties(root, "schema", "source", "target", "scope");
        return new(ReadBoundary(root.GetProperty("source")), ReadBoundary(root.GetProperty("target")), ReadScope(root.GetProperty("scope"), 0));
    }

    private static void CheckJson(JsonElement value, int depth, ref int count)
    {
        if (++count > 16_384 || depth > 64) throw new ArgumentException("Authoring JSON exceeds its structural budget.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate authoring property.");
                CheckJson(property.Value, depth + 1, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CheckJson(child, depth + 1, ref count);
    }

    private static CreationBoundary ReadBoundary(JsonElement value)
    {
        Properties(value, "schema", "options");
        var options = value.GetProperty("options");
        var kind = String(options.GetProperty("kind"));
        CreationFormatOptions selected;
        switch (kind)
        {
            case "json": Properties(options, "kind"); selected = new CreationJsonOptions(); break;
            case "xml": Properties(options, "kind"); selected = new CreationXmlOptions(); break;
            case "edifact": Properties(options, "kind"); selected = new CreationEdifactOptions(); break;
            case "x12": Properties(options, "kind", "interchange_version"); selected = new CreationX12Options(String(options.GetProperty("interchange_version"))); break;
            case "csv":
            case "tsv":
                Properties(options, "kind", "header", "quote", "bom", "preserve_empty_strings");
                var header = options.GetProperty("header").GetBoolean(); var quote = options.GetProperty("quote").GetBoolean();
                var bom = options.GetProperty("bom").GetBoolean(); var empty = options.GetProperty("preserve_empty_strings").GetBoolean();
                selected = kind == "csv" ? new CreationCsvOptions(header, quote, bom, empty) : new CreationTsvOptions(header, quote, bom, empty);
                break;
            default: throw new ArgumentException("Unsupported boundary format.");
        }
        return new(ReadSchema(value.GetProperty("schema"), 0), selected);
    }

    private static CreationSchemaNode ReadSchema(JsonElement value, int depth)
    {
        if (depth > CreationPlanV2.MaximumDepth) throw new ArgumentException("Schema depth budget exceeded.");
        Properties(value, "name", "shape", "repeating", "nullable", "required", "xml");
        var shape = value.GetProperty("shape");
        CreationValueType? scalar = null; CreationSchemaNode[] children = [];
        switch (String(shape.GetProperty("kind")))
        {
            case "scalar": Properties(shape, "kind", "scalar"); scalar = ReadType(shape.GetProperty("scalar")); break;
            case "group": Properties(shape, "kind", "children"); children = Array(shape.GetProperty("children"), CreationPlanV2.MaximumSchemaNodes).Select(c => ReadSchema(c, depth + 1)).ToArray(); break;
            default: throw new ArgumentException("Unsupported schema shape.");
        }
        CreationXmlMetadata? xml = null;
        var metadata = value.GetProperty("xml");
        if (metadata.ValueKind != JsonValueKind.Null)
        {
            Properties(metadata, "namespace", "attribute", "text", "optional", "nillable");
            var namespaceValue = metadata.GetProperty("namespace");
            xml = new(namespaceValue.ValueKind == JsonValueKind.Null ? null : String(namespaceValue), metadata.GetProperty("attribute").GetBoolean(),
                metadata.GetProperty("text").GetBoolean(), metadata.GetProperty("optional").GetBoolean(), metadata.GetProperty("nillable").GetBoolean());
        }
        return new(String(value.GetProperty("name")), scalar, children, value.GetProperty("repeating").GetBoolean(),
            value.GetProperty("nullable").GetBoolean(), Array(value.GetProperty("required"), CreationPlanV2.MaximumSchemaNodes).Select(String), xml);
    }

    private static CreationScope ReadScope(JsonElement value, int depth)
    {
        if (depth > CreationPlanV2.MaximumDepth) throw new ArgumentException("Scope depth budget exceeded.");
        Properties(value, "target", "iteration", "bindings", "children");
        var target = value.GetProperty("target"); var iteration = value.GetProperty("iteration");
        return new(target.ValueKind == JsonValueKind.Null ? null : String(target),
            Array(value.GetProperty("bindings"), CreationPlanV2.MaximumBindings).Select(binding =>
            {
                Properties(binding, "target", "value");
                return new CreationFieldBinding(String(binding.GetProperty("target")), ReadValue(binding.GetProperty("value")));
            }), Array(value.GetProperty("children"), CreationPlanV2.MaximumSchemaNodes).Select(child => ReadScope(child, depth + 1)),
            iteration.ValueKind == JsonValueKind.Null ? null : Array(iteration, CreationPlanV2.MaximumDepth).Select(String));
    }

    private static CreationValue ReadValue(JsonElement value)
    {
        switch (String(value.GetProperty("kind")))
        {
            case "source": Properties(value, "kind", "path"); return new CreationSourceValue(Array(value.GetProperty("path"), CreationPlanV2.MaximumDepth).Select(String));
            case "constant":
                Properties(value, "kind", "value"); var literal = value.GetProperty("value");
                if (String(literal.GetProperty("type")) == "json_null") { Properties(literal, "type"); return new CreationConstantValue(null); }
                Properties(literal, "type", "text"); return new CreationConstantValue(ReadType(literal.GetProperty("type")), String(literal.GetProperty("text")));
            default: throw new ArgumentException("Unsupported expression kind.");
        }
    }

    private static CreationValueType ReadType(JsonElement value) => String(value) switch
    {
        "string" => CreationValueType.String,
        "int" => CreationValueType.Integer,
        "float" => CreationValueType.Float,
        "bool" => CreationValueType.Boolean,
        _ => throw new ArgumentException("Unsupported scalar type.")
    };

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Expected an authoring string.");
    private static JsonElement.ArrayEnumerator Array(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw new ArgumentException("Authoring array exceeds its schema budget.");
        return value.EnumerateArray();
    }
    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an authoring object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(expected, StringComparer.Ordinal).Any())
            throw new ArgumentException("Unknown, duplicate or missing authoring property.");
    }
}
