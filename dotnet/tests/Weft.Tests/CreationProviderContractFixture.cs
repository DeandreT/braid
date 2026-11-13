using System.Text.Json.Nodes;

namespace Weft.Tests;

// Synthetic provider output built directly in the advertised wire grammar, never via the domain encoder.
internal static class CreationProviderContractFixture
{
    internal static readonly string[] Formats = ["json", "xml", "csv", "tsv", "x12", "edifact"];
    internal const string X12 = "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *261112*1200*U*00401*000000001*0*P*:~GS*PO*SENDER*RECEIVER*20261112*1200*1*X*004010~ST*850*0001~BEG*00*SA*alpha*20261112~SE*3*0001~GE*1*1~IEA*1*000000001~";
    internal const string Edifact = "UNB+UNOC:3+SENDER+RECEIVER+261112:1200+1'UNH+1+ORDERS:D:96A:UN'BGM+220+alpha+9'UNT+3+1'UNZ+1+1'";

    internal static JsonObject Node(string name, params JsonNode[] children) => new()
    {
        ["name"] = name,
        ["shape"] = children.Length == 0 ? new JsonObject { ["kind"] = "scalar", ["scalar"] = "string" }
            : new JsonObject { ["kind"] = "group", ["children"] = new JsonArray(children) },
        ["repeating"] = false,
        ["nullable"] = false,
        ["required"] = new JsonArray(),
        ["xml"] = null
    };

    internal static JsonObject Source(params string[] path) => new() { ["kind"] = "source", ["path"] = Strings(path) };
    internal static JsonObject Constant(string text) => new()
    { ["kind"] = "constant", ["value"] = new JsonObject { ["type"] = "string", ["text"] = text } };
    internal static JsonObject Binding(string target, JsonObject value) => new() { ["target"] = target, ["value"] = value };
    internal static JsonObject Scope(string? target, JsonArray bindings, JsonArray children, JsonArray? iteration = null) => new()
    { ["target"] = target, ["iteration"] = iteration, ["bindings"] = bindings, ["children"] = children };
    internal static JsonArray Strings(params string[] values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    internal sealed record Boundary(JsonObject Wire, string[] ValuePath, Dictionary<string, string> Constants);
    internal static Boundary Format(string format)
    {
        var options = new JsonObject { ["kind"] = format };
        if (format is "csv" or "tsv")
        { options["header"] = true; options["quote"] = true; options["bom"] = false; options["preserve_empty_strings"] = true; }
        if (format == "x12") options["interchange_version"] = "00401";
        var constants = new Dictionary<string, string>();
        JsonObject schema;
        if (format is "x12" or "edifact")
        {
            var segments = new List<JsonNode>();
            var terminator = format == "x12" ? '~' : '\'';
            foreach (var segment in (format == "x12" ? X12 : Edifact).TrimEnd(terminator).Split(terminator))
            {
                var parts = segment.Split(format == "x12" ? '*' : '+');
                var fields = new List<JsonNode>();
                for (var i = 1; i < parts.Length; i++)
                {
                    var name = "e" + i;
                    if (format == "edifact" && parts[i].Contains(':'))
                    {
                        var components = parts[i].Split(':');
                        fields.Add(Node(name, components.Select((_, c) => (JsonNode)Node("c" + (c + 1))).ToArray()));
                        for (var c = 0; c < components.Length; c++) constants[$"{parts[0]}/{name}/c{c + 1}"] = components[c];
                    }
                    else { fields.Add(Node(name)); constants[$"{parts[0]}/{name}"] = parts[i]; }
                }
                segments.Add(Node(parts[0], fields.ToArray()));
            }
            schema = Node("Document", segments.ToArray());
        }
        else schema = Node("Document", Node("code"));
        return new(new JsonObject { ["schema"] = schema, ["options"] = options },
            format == "x12" ? ["BEG", "e3"] : format == "edifact" ? ["BGM", "e2"] : ["code"], constants);
    }

    internal static JsonObject Plan(string sourceFormat, string targetFormat)
    {
        var source = Format(sourceFormat); var target = Format(targetFormat);
        JsonObject Construct(JsonNode node, string[] path)
        {
            var bindings = new JsonArray(); var scopes = new JsonArray();
            foreach (var child in node["shape"]!["children"]!.AsArray())
            {
                var name = child!["name"]!.GetValue<string>(); string[] address = [.. path, name];
                if (child["shape"]!["kind"]!.GetValue<string>() == "group") scopes.Add(Construct(child, address));
                else bindings.Add(Binding(name, address.SequenceEqual(target.ValuePath) ? Source(source.ValuePath)
                    : Constant(target.Constants[string.Join('/', address)])));
            }
            return Scope(path.Length == 0 ? null : path[^1], bindings, scopes);
        }
        return new()
        {
            ["schema"] = "weft.creation-plan.v2",
            ["source"] = source.Wire,
            ["target"] = target.Wire,
            ["scope"] = Construct(target.Wire["schema"]!, [])
        };
    }

    internal static string Arguments(CreationRuntimeFixture f, JsonObject plan) => new JsonObject
    {
        ["projectId"] = f.Bundle.Project.ProjectId.ToString("D"),
        ["bundleId"] = f.Bundle.BundleId.ToString("D"),
        ["extractionIds"] = Strings(f.ExtractionId.ToString("D")),
        ["snapshotDigest"] = f.Specification.Snapshot.Digest.Value,
        ["name"] = "Synthetic mapping",
        ["plan"] = plan.DeepClone(),
        ["notes"] = new JsonObject
        {
            ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = f.Source.Identity.SourceId.ToString("D"), ["sourceVersion"] = 1, ["chunkOrdinal"] = 0 }),
            ["assumptions"] = new JsonArray(),
            ["questions"] = new JsonArray()
        }
    }.ToJsonString();
}
