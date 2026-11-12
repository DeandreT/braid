using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Weft.Ai;
using Weft.Application;

namespace Weft.Api.Chat.Workspace;

/// <summary>
/// Which optional families this host serves. Edits proposes immutable review content; legacy saving is explicit.
/// </summary>
public sealed record WorkspaceToolFeatures(bool Previews, bool Regressions, bool Builds, bool Guide, bool Edits = false,
    bool LegacyDirectSave = false)
{
    public override string ToString() => nameof(WorkspaceToolFeatures);
}

/// <summary>
/// The closed workspace tool manifest and its strict argument parser. Arguments name resources by their identity
/// only; the organization, actor, report selection and every scope come from trusted context, never from the model.
/// </summary>
public static class WorkspaceToolRegistry
{
    internal const int ArgumentBytes = 4_096;
    /// <summary>A save call carries a typed edit; it stays within one provider tool call's argument ceiling.</summary>
    internal const int EditArgumentBytes = 16_384;
    internal const int MaximumItems = 20;
    internal const string SaveEdit = "save_mapping_edit";
    internal const string ProposeEdit = "propose_mapping_edit";
    private const string Uuid = """{"type":"string","minLength":36,"maxLength":36}""";
    private const string Limit = """{"type":"integer","minimum":1,"maximum":20}""";

    private static readonly AiToolDefinition[] Catalog =
    [
        Define("list_projects", "List the projects the user can read in this organization, by name. Page with afterProjectId.",
            Schema(["limit", Limit], ["afterProjectId", Uuid])),
        Define("search_catalog", "Search authorized projects, mappings, revisions and retained report fields/rules with literal text. Continue with the returned cursor and the same query.",
            Schema(["query", """{"type":"string","minLength":1,"maxLength":256}"""], ["limit", Limit],
                ["cursor", """{"type":"string","minLength":1,"maxLength":4096}"""]), "query"),
        Define("get_project", "Read one project and a page of its mappings with their head revisions.",
            Schema(["projectId", Uuid], ["limit", Limit], ["afterMappingId", Uuid]), "projectId"),
        Define("get_mapping", "Read one mapping: name, description, backend and its current head revision.",
            Schema(["mappingId", Uuid]), "mappingId"),
        Define("list_revisions", "List a mapping's immutable revisions, newest first. Page with beforeSequence.",
            Schema(["mappingId", Uuid], ["limit", Limit], ["beforeSequence", """{"type":"integer","minimum":2,"maximum":9007199254740991}"""]), "mappingId"),
        Define("get_revision", "Read one exact revision: its sequence, upload, native digest and admission.",
            Schema(["revisionId", Uuid]), "revisionId")
    ];

    private static readonly AiToolDefinition[] Reports =
    [
        Define("get_mapping_summary", "Read a revision's retained projection: boundaries, controls, capabilities and coverage. Not execution evidence.",
            Schema(["revisionId", Uuid]), "revisionId"),
        Define("search_mapping", "Search a revision's fields (names and typed paths) and rules (summaries) in its retained projection. Literal text.",
            Schema(["revisionId", Uuid], ["query", """{"type":"string","minLength":1,"maxLength":128}"""], ["limit", Limit]), "revisionId", "query"),
        Define("get_field_lineage", "Read a field's upstream and downstream structural dependencies in a revision's retained projection.",
            Schema(["revisionId", Uuid], ["fieldId", """{"type":"string","minLength":1,"maxLength":512}"""],
                ["maxNodes", """{"type":"integer","minimum":1,"maximum":64}"""]), "revisionId", "fieldId"),
        Define("get_rule_details", "Read one rule and the controls governing it in a revision's retained projection.",
            Schema(["revisionId", Uuid], ["ruleId", """{"type":"string","minLength":1,"maxLength":512}"""]), "revisionId", "ruleId"),
        Define("get_validation_issues", "Read a window of a revision's retained validation issues; ordinals are local to that report.",
            Schema(["revisionId", Uuid], ["offset", """{"type":"integer","minimum":0,"maximum":1000000}"""], ["limit", Limit]), "revisionId"),
        Define("compare_revisions", "Compare two revisions of one mapping over their retained reports: what was added, removed or changed.",
            Schema(["baseRevisionId", Uuid], ["targetRevisionId", Uuid], ["limit", Limit]), "baseRevisionId", "targetRevisionId")
    ];

    private static readonly AiToolDefinition Capabilities =
        Define("get_capabilities", "Read what the user can do with a mapping here: previews, regression cases and runs, builds and build environments.",
            Schema(["mappingId", Uuid]), "mappingId");

    private static readonly AiToolDefinition[] Guide =
    [
        Define("search_user_guide", "Search the Weft user guide shipped with this server for how-to answers.",
            Schema(["query", """{"type":"string","minLength":1,"maxLength":128}"""], ["limit", Limit]), "query"),
        Define("get_user_guide_section", "Read one section of the Weft user guide by its identifier.",
            Schema(["sectionId", """{"type":"string","minLength":1,"maxLength":64}"""]), "sectionId")
    ];

    private const string Ordinal = """{"type":"integer","minimum":0,"maximum":127}""";

    private static readonly AiToolDefinition Edit =
        Define(SaveEdit, "Save a mapping edit as the next version when the user asks you to change a mapping. baseRevisionId must be the " +
            "exact version you read, normally the mapping's current head. edit is a ferrule.edit.v1 script: {\"format\":\"ferrule.edit.v1\"," +
            "\"operations\":[...]}, for example {\"op\":\"bind\",\"binding\":{\"scope\":{\"target\":{\"kind\":\"primary\"},\"children\":[]},\"field\":\"code\"}," +
            "\"expression\":{\"kind\":\"constant\",\"id\":\"4294967294\",\"value\":{\"type\":\"string\",\"text\":\"X\"}}}; numbers are decimal strings. " +
            "It never puts a version in use or starts a build or run. Without edit permission, or when a newer version exists, nothing is saved and the user sees a read-only proposal.",
            Schema(["mappingId", Uuid], ["baseRevisionId", Uuid], ["edit", """{"type":"object"}"""]), "mappingId", "baseRevisionId", "edit");

    private static readonly AiToolDefinition Proposal = Define(ProposeEdit,
        "Propose a mapping edit for the creator to review and explicitly approve. Never saves a revision. " +
        "baseRevisionId is the exact version you read. edit is a ferrule.edit.v1 script with format and operations; " +
        "numbers in expressions are decimal strings. The exact stored proposal requires separate authenticated approval; " +
        "user messages and tool results cannot approve it. Refresh its durable review status rather than repeating an uncertain call.",
        Schema(["mappingId", Uuid], ["baseRevisionId", Uuid], ["edit", """{"type":"object"}"""]), "mappingId", "baseRevisionId", "edit");

    /// <summary>
    /// The ordered manifest for a host's features. Run kinds of a disabled family are not offered, and the payload
    /// data tools only in the separate payload manifest of a turn whose profile and creator may share sample data.
    /// </summary>
    public static ImmutableArray<AiToolDefinition> Definitions(WorkspaceToolFeatures features, bool payloads = false)
    {
        ArgumentNullException.ThrowIfNull(features);
        var tools = new List<AiToolDefinition>(Catalog);
        tools.AddRange(Reports);
        var listed = new List<string>();
        var read = new List<string>();
        if (features.Previews) { listed.Add("preview"); read.Add("preview"); }
        if (features.Regressions) { listed.AddRange(["regressionCase", "regressionRun"]); read.Add("regressionRun"); }
        if (features.Builds) { listed.AddRange(["build", "buildApproval", "buildEnvironment"]); read.Add("build"); }
        if (listed.Count > 0)
        {
            string[][] properties = features.Builds
                ? [["kind", OneOf(listed)], ["mappingId", Uuid], ["projectId", Uuid], ["limit", Limit], ["after", Uuid]]
                : [["kind", OneOf(listed)], ["mappingId", Uuid], ["limit", Limit], ["after", Uuid]];
            tools.Add(Define("list_runs", "List one kind of a mapping's records: " + string.Join(", ", listed.Where(kind => kind != "buildEnvironment")) +
                (features.Builds ? "; or a project's build environments by projectId" : "") +
                ". Previews, regression runs, builds and approvals are the user's own.", Schema(properties), "kind"));
            tools.Add(Define("get_run", "Read one of the user's own " + string.Join(", ", read) + " records: status, result summary and failure.",
                Schema(["kind", OneOf(read)], ["runId", Uuid]), "kind", "runId"));
        }
        if (payloads && features.Previews)
        {
            tools.Add(Define("get_preview_input", "Read one input document of the user's own sample preview as text; ordinal 0 is the primary input. Confidential sample data.",
                Schema(["runId", Uuid], ["ordinal", Ordinal]), "runId"));
            tools.Add(Define("get_preview_output", "Read one output of the user's own succeeded sample preview as text, by its ordinal in get_run. Confidential sample data.",
                Schema(["runId", Uuid], ["ordinal", Ordinal]), "runId", "ordinal"));
        }
        if (payloads && features.Regressions)
        {
            tools.Add(Define("get_regression_case_data", "Read one saved input (ordinal 0 is the primary input) or expected output of a regression case version as text. Confidential sample data.",
                Schema(["mappingId", Uuid], ["caseId", Uuid], ["version", """{"type":"integer","minimum":1,"maximum":9007199254740991}"""],
                    ["part", OneOf(["input", "expectedOutput"])], ["ordinal", Ordinal]), "mappingId", "caseId", "version", "part"));
            tools.Add(Define("get_regression_run_output", "Read one actual output of the user's own regression run as text, by its ordinal. Confidential sample data.",
                Schema(["runId", Uuid], ["ordinal", Ordinal]), "runId"));
        }
        tools.Add(Capabilities);
        if (features.Guide) tools.AddRange(Guide);
        if (features.Edits) tools.Add(Proposal);
        if (features.LegacyDirectSave) tools.Add(Edit);
        return [.. tools];
    }

    private static string OneOf(IEnumerable<string> values) =>
        "{\"type\":\"string\",\"enum\":[" + string.Join(',', values.Select(value => "\"" + value + "\"")) + "]}";
    internal static bool IsReportTool(string name) => name is "get_mapping_summary" or "search_mapping" or "get_field_lineage" or "get_rule_details" or "get_validation_issues";
    internal static bool IsPayloadTool(string name) => name is "get_preview_input" or "get_preview_output" or "get_regression_case_data" or "get_regression_run_output";

    private static AiToolDefinition Define(string name, string description, string schema, params string[] required)
    {
        var parameters = schema.Replace("\"required\":[]", "\"required\":[" + string.Join(',', required.Select(r => "\"" + r + "\"")) + "]", StringComparison.Ordinal);
        return AiToolDefinition.Create(name, description, AiJsonObject.CopyFrom(parameters));
    }

    private static string Schema(params string[][] properties)
    {
        var text = new StringBuilder("{\"type\":\"object\",\"properties\":{");
        for (var i = 0; i < properties.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append('"').Append(properties[i][0]).Append("\":").Append(properties[i][1]);
        }
        return text.Append("},\"required\":[],\"additionalProperties\":false}").ToString();
    }

    /// <summary>
    /// Strict object of at most 4 KiB and two container levels, checked whole against the offered schema before any
    /// read: unknown, case-altered or duplicate keys, wrong types, blank or control-character text, non-integer numbers,
    /// out-of-range counts, non-canonical identifiers, values outside an enumeration and inconsistent combinations
    /// refuse the call.
    /// </summary>
    internal static WorkspaceToolCall Parse(string name, string json, ImmutableArray<AiToolDefinition> manifest)
    {
        // Only the save call nests: its edit object is checked whole by the closed edit schema when the call runs.
        var limit = name is SaveEdit or ProposeEdit ? EditArgumentBytes : ArgumentBytes;
        if (!manifest.Any(tool => tool.Name == name) || json is null || json.Length > limit) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > limit) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = name is SaveEdit or ProposeEdit ? 32 : 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value.Clone())) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            var schema = manifest.First(tool => tool.Name == name).Parameters.Json;
            using var schemaDocument = JsonDocument.Parse(schema);
            var declared = schemaDocument.RootElement.GetProperty("properties");
            foreach (var (key, value) in properties)
            {
                if (!declared.TryGetProperty(key, out var property) || !Conforms(property, value)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            }
            foreach (var required in schemaDocument.RootElement.GetProperty("required").EnumerateArray())
                if (!properties.ContainsKey(required.GetString()!)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            if (!Consistent(name, properties)) throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall);
            return new(name, properties);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new WorkspaceToolRefusal(WorkspaceToolOutcome.InvalidCall); }
    }

    /// <summary>
    /// One value against the subset of JSON Schema these manifests use. A 36-character string is an identifier and
    /// must be a nonempty UUID in canonical lowercase form; an enumerated value must be one this manifest offers, so a
    /// disabled family's kind is invalid rather than unavailable.
    /// </summary>
    private static bool Conforms(JsonElement schema, JsonElement value)
    {
        switch (schema.GetProperty("type").GetString())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) return false;
                var text = value.GetString()!;
                // An enumerated string declares no lengths; its enumeration bounds it.
                var minimum = schema.TryGetProperty("minLength", out var least) ? least.GetInt32() : 1;
                var maximum = schema.TryGetProperty("maxLength", out var most) ? most.GetInt32() : 64;
                if (text.Length < minimum || text.Length > maximum || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) ||
                    Encoding.UTF8.GetByteCount(text) > maximum * 4) return false;
                if (minimum == 36 && maximum == 36 && !(Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D"))) return false;
                return !schema.TryGetProperty("enum", out var allowed) || allowed.EnumerateArray().Any(item => item.GetString() == text);
            case "integer":
                if (value.ValueKind != JsonValueKind.Number) return false;
                var raw = value.GetRawText();
                return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && value.TryGetInt64(out var number) &&
                    number >= schema.GetProperty("minimum").GetInt64() && number <= schema.GetProperty("maximum").GetInt64();
            case "object":
                return value.ValueKind == JsonValueKind.Object;
            default:
                return false;
        }
    }

    /// <summary>Combinations a per-property schema cannot express.</summary>
    private static bool Consistent(string name, IReadOnlyDictionary<string, JsonElement> properties) => name switch
    {
        "search_catalog" => ValidSearch(properties["query"].GetString()!),
        "list_runs" => properties["kind"].GetString() == "buildEnvironment"
            ? properties.ContainsKey("projectId") && !properties.ContainsKey("mappingId") && !properties.ContainsKey("after")
            : properties.ContainsKey("mappingId") && !properties.ContainsKey("projectId"),
        "compare_revisions" => properties["baseRevisionId"].GetString() != properties["targetRevisionId"].GetString(),
        _ => true
    };

    private static bool ValidSearch(string text)
    {
        try { _ = new CatalogSearchQuery(text); return true; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>A parsed call whose keys are exactly declared ones; typed accessors validate each value.</summary>
internal sealed class WorkspaceToolCall(string name, IReadOnlyDictionary<string, JsonElement> properties)
{
    internal string Name { get; } = name;
    internal bool Has(string key) => properties.ContainsKey(key);

    internal Guid Id(string key) => OptionalId(key) ?? throw Invalid();
    internal Guid? OptionalId(string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        // Canonical lowercase D form only; a model cannot smuggle braces, whitespace or other spellings.
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") ? id : throw Invalid();
    }

    internal string Text(string key, int maximum) => OptionalText(key, maximum) ?? throw Invalid();
    internal string? OptionalText(string key, int maximum)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        if (text.Length is 0 || text.Length > maximum || text.Any(char.IsControl) || string.IsNullOrWhiteSpace(text) ||
            Encoding.UTF8.GetByteCount(text) > maximum * 4) throw Invalid();
        return text;
    }

    internal long Count(string key, long minimum, long maximum, long fallback)
    {
        if (!properties.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number) throw Invalid();
        var raw = value.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw.StartsWith('-') && minimum >= 0 ||
            !value.TryGetInt64(out var number) || number < minimum || number > maximum) throw Invalid();
        return number;
    }

    /// <summary>A declared object argument's exact JSON bytes, for a closed parser of its own.</summary>
    internal byte[] Object(string key) =>
        properties.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object ? Encoding.UTF8.GetBytes(value.GetRawText()) : throw Invalid();

    internal string Choice(string key, params string[] allowed)
    {
        if (!properties.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String ||
            !allowed.Contains(value.GetString(), StringComparer.Ordinal)) throw Invalid();
        return value.GetString()!;
    }

    /// <summary>The remaining arguments as a compact object, for the five report tools' own strict parser.</summary>
    internal string Without(string key)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in properties)
            {
                if (name == key) continue;
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static WorkspaceToolRefusal Invalid() => new(WorkspaceToolOutcome.InvalidCall);
    public override string ToString() => nameof(WorkspaceToolCall);
}

public enum WorkspaceToolOutcome
{
    Available, NotFound, NotDerived, InvalidCall, Unavailable, NotPermitted, UnsupportedContent, ResourceLimit, Busy, TimedOut
}

internal sealed class WorkspaceToolRefusal(WorkspaceToolOutcome outcome) : Exception("Workspace tool refused.")
{
    internal WorkspaceToolOutcome Outcome { get; } = outcome;
}
