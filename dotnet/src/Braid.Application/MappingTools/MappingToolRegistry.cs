using System.Text;
using System.Text.Json;
using Braid.Ai;

namespace Braid.Application.MappingTools;

/// <summary>Closed schemas and parser. No provider-supplied scope, report selector, URL or action is admitted.</summary>
public static class MappingToolRegistry
{
    /// <summary>The exact ordered manifest to advertise and pin in the turn's grounding policy.</summary>
    public static IReadOnlyList<AiToolDefinition> Definitions { get; } = Array.AsReadOnly(
    [
        Define("get_mapping_summary", "Read the pinned mapping boundaries, controls, capabilities and coverage. Not execution evidence.",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
        Define("search_mapping", "Search field identifiers, names and typed paths and rule identifiers/summaries in retained order. Literal ordinal-ignore-case text, not a pattern.",
            """{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":128},"limit":{"type":"integer","minimum":1,"maximum":20}},"required":["query"],"additionalProperties":false}"""),
        Define("get_field_lineage", "Read typed dependency edges and conservative downstream output dependencies. Truncation/coverage are explicit; never executes a mapping.",
            """{"type":"object","properties":{"fieldId":{"type":"string","minLength":1,"maxLength":512},"maxNodes":{"type":"integer","minimum":1,"maximum":64}},"required":["fieldId"],"additionalProperties":false}"""),
        Define("get_rule_details", "Read one exact rule and its governing controls from the pinned projection, preserving literals and typed structure.",
            """{"type":"object","properties":{"ruleId":{"type":"string","minLength":1,"maxLength":512}},"required":["ruleId"],"additionalProperties":false}"""),
        Define("get_validation_issues", "Read a diagnostic window from the exact retained validation report. Ordinals are zero-based only in this report, never stable issue UUIDs.",
            """{"type":"object","properties":{"offset":{"type":"integer","minimum":0,"maximum":1000000},"limit":{"type":"integer","minimum":1,"maximum":20}},"required":[],"additionalProperties":false}""")
    ]);

    internal static readonly UTF8Encoding Utf8 = new(false, true);

    private static AiToolDefinition Define(string name, string description, string parameters) =>
        AiToolDefinition.Create(name, description, AiJsonObject.CopyFrom(parameters));

    internal static ParsedMappingToolCall Parse(string name, string json, MappingToolLimits limits)
    {
        if (name is not ("get_mapping_summary" or "search_mapping" or "get_field_lineage" or "get_rule_details" or "get_validation_issues"))
            throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        if (json is null || json.Length > 4_096) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        try
        {
            if (Utf8.GetByteCount(json) > 4_096) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value)) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
            }
            string? text = null;
            var limit = limits.ResultItems;
            var nodes = limits.LineageNodes;
            var offset = 0;
            foreach (var pair in properties)
            {
                switch (name, pair.Key)
                {
                    case ("search_mapping", "query"):
                        text = Text(pair.Value, 128, true);
                        break;
                    case ("get_field_lineage", "fieldId"):
                    case ("get_rule_details", "ruleId"):
                        text = Text(pair.Value, 512, false);
                        break;
                    case ("search_mapping" or "get_validation_issues", "limit"):
                        limit = Count(pair.Value, 1, limits.ResultItems);
                        break;
                    case ("get_validation_issues", "offset"):
                        offset = Count(pair.Value, 0, 1_000_000);
                        break;
                    case ("get_field_lineage", "maxNodes"):
                        nodes = Count(pair.Value, 1, limits.LineageNodes);
                        break;
                    default: throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
                }
            }
            if ((name is "search_mapping" or "get_field_lineage" or "get_rule_details") && text is null)
                throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
            return new(name, text, limit, nodes, offset);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new MappingToolRefusal(MappingToolOutcome.InvalidCall); }
    }

    private static string Text(JsonElement element, int maximum, bool rejectWhitespace)
    {
        if (element.ValueKind != JsonValueKind.String) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        var text = element.GetString()!;
        if (text.Length is 0 || text.Length > maximum || text.Any(char.IsControl) ||
            rejectWhitespace && string.IsNullOrWhiteSpace(text) || Utf8.GetByteCount(text) > maximum * 4)
            throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        return text;
    }

    private static int Count(JsonElement element, int minimum, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Number) throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') ||
            !element.TryGetInt32(out var value) || value < minimum || value > maximum)
            throw new MappingToolRefusal(MappingToolOutcome.InvalidCall);
        return value;
    }
}

internal sealed record ParsedMappingToolCall(string Name, string? Text, int Limit, int MaxNodes, int Offset)
{
    public override string ToString() => nameof(ParsedMappingToolCall);
}

internal sealed class MappingToolRefusal(MappingToolOutcome outcome) : Exception("Mapping tool refused.")
{
    internal MappingToolOutcome Outcome { get; } = outcome;
}

internal sealed class MappingToolWork(int maximum, CancellationToken token)
{
    private int remaining = maximum;
    internal void Charge(int count = 1)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > remaining) throw new MappingToolRefusal(MappingToolOutcome.ResourceLimit);
        remaining -= count;
    }

    internal bool Contains(string text, string query)
    {
        Charge(text.Length + 1);
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
