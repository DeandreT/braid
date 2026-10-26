using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weft.Tests.Cli;

/// <summary>The committed <c>contracts/weft-v1.json</c>, its operations and a validator for the JSON Schema subset it uses.</summary>
internal sealed class CliOpenApi
{
    private static readonly Lazy<CliOpenApi> Shared = new(() => new CliOpenApi(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "weft-v1.json")))!.AsObject()));

    private CliOpenApi(JsonObject document)
    {
        Document = document;
        Operations = [.. document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
            .Where(method => method.Key is "get" or "post" or "put" or "patch" or "delete")
            .Select(method => new CliOpenApiOperation(method.Value!["operationId"]!.GetValue<string>(), method.Key.ToUpperInvariant(),
                path.Key, method.Value!.AsObject(), Template(path.Key))))];
    }

    public static CliOpenApi Load() => Shared.Value;

    public JsonObject Document { get; }
    public IReadOnlyList<CliOpenApiOperation> Operations { get; }

    public CliOpenApiOperation Operation(string operationId) => Operations.Single(operation => operation.Id == operationId);

    public CliOpenApiOperation? Match(string method, string path, out Dictionary<string, string> values)
    {
        // A literal summary route takes precedence over the same prefix's section parameter,
        // just as it does in ASP.NET routing; document insertion order is not routing precedence.
        foreach (var operation in Operations.Where(operation => operation.Method == method)
            .OrderBy(operation => operation.Path.Count(character => character == '{')))
        {
            var match = operation.Pattern.Match(path);
            if (!match.Success)
                continue;
            values = operation.Pattern.GetGroupNames().Where(name => !char.IsDigit(name[0]))
                .ToDictionary(name => name, name => Uri.UnescapeDataString(match.Groups[name].Value), StringComparer.Ordinal);
            return operation;
        }
        values = [];
        return null;
    }

    public JsonObject Schema(string reference) =>
        Document["components"]!["schemas"]![reference.Replace("#/components/schemas/", "", StringComparison.Ordinal)]!.AsObject();

    /// <summary>Every violation of <paramref name="schema"/> by <paramref name="value"/>; empty when the value conforms.</summary>
    public List<string> Validate(JsonNode? value, JsonNode schema, string at = "$")
    {
        var errors = new List<string>();
        Check(value, schema, at, errors, 0);
        return errors;
    }

    private void Check(JsonNode? value, JsonNode schemaNode, string at, List<string> errors, int depth)
    {
        if (depth > 64)
        {
            errors.Add(at + ": schema recursion too deep");
            return;
        }
        var schema = schemaNode.AsObject();
        if (schema["$ref"] is { } reference)
        {
            Check(value, Schema(reference.GetValue<string>()), at, errors, depth + 1);
            return;
        }
        if (schema["type"] is { } type)
        {
            var allowed = type is JsonArray list ? list.Select(item => item!.GetValue<string>()).ToArray() : [type.GetValue<string>()];
            if (!allowed.Any(name => Is(value, name)))
            {
                errors.Add($"{at}: expected {string.Join("|", allowed)}");
                return;
            }
        }
        if (value is null)
            return;
        if (schema["enum"] is JsonArray values && !values.Any(item => JsonNode.DeepEquals(item, value)))
            errors.Add($"{at}: not one of the enumerated values");
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            if (schema["pattern"] is { } pattern && !Regex.IsMatch(text, pattern.GetValue<string>()))
                errors.Add($"{at}: does not match its pattern");
            if (schema["maxLength"] is { } maximum && text.Length > maximum.GetValue<int>())
                errors.Add($"{at}: longer than its maximum");
            if (schema["minLength"] is { } minimum && text.Length < minimum.GetValue<int>())
                errors.Add($"{at}: shorter than its minimum");
        }
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (schema[keyword] is JsonArray choices)
            {
                var matches = choices.Count(choice => Validate(value, choice!, at).Count == 0);
                if (keyword == "oneOf" ? matches != 1 : matches == 0)
                    errors.Add($"{at}: matches {matches} of its {keyword} alternatives");
            }
        if (value is JsonObject item)
        {
            var properties = schema["properties"]?.AsObject();
            foreach (var required in schema["required"]?.AsArray() ?? [])
                if (!item.ContainsKey(required!.GetValue<string>()))
                    errors.Add($"{at}: missing required {required}");
            foreach (var (name, child) in item)
            {
                if (properties?[name] is { } property)
                    Check(child, property, at + "." + name, errors, depth + 1);
                else if (schema["additionalProperties"] is JsonValue additional && !additional.GetValue<bool>())
                    errors.Add($"{at}: undeclared property {name}");
                else if (schema["additionalProperties"] is JsonObject additionalSchema)
                    Check(child, additionalSchema, at + "." + name, errors, depth + 1);
                else if (properties is not null && schema["anyOf"] is null && schema["oneOf"] is null)
                    errors.Add($"{at}: undeclared property {name}");
            }
        }
        if (value is JsonArray array && schema["items"] is { } items)
            for (var i = 0; i < array.Count; i++)
                Check(array[i], items, $"{at}[{i}]", errors, depth + 1);
    }

    private static bool Is(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue text && text.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "integer" => value is JsonValue number && number.GetValueKind() == JsonValueKind.Number && number.TryGetValue<long>(out _),
        "number" => value is JsonValue number && number.GetValueKind() == JsonValueKind.Number,
        _ => false
    };

    private static Regex Template(string path) => new("^" + Regex.Replace(Regex.Escape(path).Replace(@"\{", "{", StringComparison.Ordinal),
        "{([A-Za-z]+)}", "(?<$1>[^/]+)") + "$", RegexOptions.CultureInvariant);
}

internal sealed record CliOpenApiOperation(string Id, string Method, string Path, JsonObject Node, Regex Pattern)
{
    public IEnumerable<(string In, string Name, bool Required)> Parameters => (Node["parameters"]?.AsArray() ?? [])
        .Select(parameter => (parameter!["in"]!.GetValue<string>(), parameter["name"]!.GetValue<string>(),
            parameter["required"]?.GetValue<bool>() ?? false));

    public IReadOnlyList<string> RequestMediaTypes => Node["requestBody"]?["content"]?.AsObject().Select(pair => pair.Key).ToArray() ?? [];

    public JsonNode? RequestSchema(string mediaType) => Node["requestBody"]?["content"]?[mediaType]?["schema"];

    public JsonObject? Response(int status) => Node["responses"]?[status.ToString(System.Globalization.CultureInfo.InvariantCulture)]?.AsObject();

    public IEnumerable<int> Statuses => Node["responses"]!.AsObject().Select(pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture));
}
