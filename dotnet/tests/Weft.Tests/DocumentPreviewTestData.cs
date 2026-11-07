using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Self-authored envelopes and identity graphs; no upstream reference corpus.</summary>
internal static class DocumentPreviewTestData
{
    internal const string X12 = "ISA*00*          *00*          *ZZ*WEFTSENDER     *ZZ*WEFTRECEIVER   *261024*1200*^*00501*000000330*0*T*:~GS*PO*WEFTSENDER*WEFTRECEIVER*20261024*1200*330*X*005010~ST*850*0330~BEG*00*SA*WEFT-330~SE*3*0330~GE*1*330~IEA*1*000000330~";
    internal const string Edifact = "UNB+UNOC:3+WEFTSENDER+WEFTRECEIVER+261024:1200+330'UNH+330+ORDERS:D:96A:UN'BGM+220+WEFT-330'UNT+3+330'UNZ+1+330'";

    internal static byte[] Input(string family) => Encoding.UTF8.GetBytes(family == "x12" ? X12 : Edifact);

    internal static string MixedX12(bool crossRole = true)
    {
        var first = X12.Replace("BEG*00*SA*WEFT-330~", "BEG*00*SA*WEFT-330*A:B^C:D~", StringComparison.Ordinal);
        var second = first.Replace("WEFT-330", "SECOND", StringComparison.Ordinal);
        if (crossRole) second = new string(second.Select(value => value switch { '*' => '|', ':' => '~', '~' => ':', '^' => '!', _ => value }).ToArray());
        return first + "\n" + second;
    }

    internal static byte[] InterchangeNative(bool configuredSyntax = false)
    {
        var document = JsonNode.Parse(Native("x12"))!;
        var segments = document["source"]!["kind"]!["children"]!.AsArray();
        JsonNode Segment(string tag) => segments.Single(value => (string?)value!["name"] == tag)!.DeepClone();
        var isa = Segment("ISA"); isa["repeating"] = true; isa["kind"]!["children"]![11]!["fixed"] = "00501";
        var gs = Segment("GS"); gs["repeating"] = true; gs["kind"]!["children"]![7]!["fixed"] = "005010";
        var ge = Segment("GE"); ge["repeating"] = true;
        var iea = Segment("IEA"); iea["repeating"] = true;
        var beg = Segment("BEG"); var repeated = Group("e4", new JsonArray(Scalar("c1"), Scalar("c2"))); repeated["repeating"] = true;
        beg["kind"]!["children"]!.AsArray().Add(repeated);
        var messages = Group("Messages", new JsonArray(Segment("ST"), beg, Segment("SE"))); messages["repeating"] = true;
        var groups = Group("Groups", new JsonArray(gs, messages, ge)); groups["repeating"] = true;
        var interchanges = Group("Interchanges", new JsonArray(isa, groups, iea)); interchanges["repeating"] = true;
        var schema = Group("Document", new JsonArray(interchanges));
        document["source"] = schema;
        document["target"] = schema.DeepClone();
        document["source_options"] = new JsonObject { ["edi_kind"] = "x12", ["x12_interchange_version"] = "00501" };
        if (configuredSyntax) document["source_options"]!["x12_separators"] = new JsonObject { ["element"] = "*", ["component"] = ":", ["segment"] = "~", ["repetition"] = "^", ["release"] = null };
        document["target_options"] = new JsonObject { ["json_document"] = true };
        document["graph"] = new JsonObject { ["nodes"] = new JsonObject() };
        document["root"] = JsonNode.Parse("""{"target_field":"","construction":"copy_current_source","bindings":[],"children":[]}""");
        document["extra_targets"] = new JsonArray(new JsonObject { ["name"] = "copy", ["schema"] = schema.DeepClone(), ["root"] = document["root"]!.DeepClone(), ["options"] = new JsonObject { ["json_document"] = true } });
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    internal static byte[] Native(string family)
    {
        var nodes = new JsonObject();
        var segments = new JsonArray();
        var scopes = new JsonArray();
        var next = 1;
        foreach (var segment in (family == "x12" ? X12 : Edifact).Split(family == "x12" ? '~' : '\'', StringSplitOptions.RemoveEmptyEntries))
        {
            var elements = segment.Split(family == "x12" ? '*' : '+');
            var fields = new JsonArray();
            var bindings = new JsonArray();
            var children = new JsonArray();
            for (var index = 1; index < elements.Length; index++)
            {
                var name = "e" + index;
                if (family == "edifact" && elements[index].Contains(':'))
                {
                    var components = new JsonArray();
                    var componentBindings = new JsonArray();
                    for (var part = 0; part < elements[index].Split(':').Length; part++)
                    {
                        var component = "c" + (part + 1);
                        components.Add(Scalar(component));
                        componentBindings.Add(Bind(component, [elements[0], name, component]));
                    }
                    fields.Add(Group(name, components));
                    children.Add(Scope(name, componentBindings, new JsonArray()));
                }
                else
                {
                    fields.Add(Scalar(name));
                    bindings.Add(Bind(name, [elements[0], name]));
                }
            }
            segments.Add(Group(elements[0], fields));
            scopes.Add(Scope(elements[0], bindings, children));
        }
        var schema = Group("Interchange", segments);
        var document = new JsonObject
        {
            ["source"] = schema,
            ["target"] = schema.DeepClone(),
            ["source_options"] = new JsonObject { ["edi_kind"] = family },
            ["target_options"] = new JsonObject { ["edi_kind"] = family },
            ["graph"] = new JsonObject { ["nodes"] = nodes },
            ["root"] = Scope("", new JsonArray(), scopes)
        };
        return Encoding.UTF8.GetBytes(document.ToJsonString());

        JsonObject Bind(string field, string[] path)
        {
            var id = next++;
            nodes[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            { ["kind"] = "source_field", ["path"] = new JsonArray(path.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
            return new JsonObject { ["target_field"] = field, ["node"] = id };
        }
    }

    private static JsonObject Scalar(string name) => new() { ["name"] = name, ["kind"] = new JsonObject { ["kind"] = "scalar", ["ty"] = "string" } };
    private static JsonObject Group(string name, JsonArray children) => new() { ["name"] = name, ["kind"] = new JsonObject { ["kind"] = "group", ["children"] = children } };
    private static JsonObject Scope(string name, JsonArray bindings, JsonArray children) => new() { ["target_field"] = name, ["bindings"] = bindings, ["children"] = children };

    internal static MappingPreviewRequest Request(RevisionScope revision, string family) => new(
        NativeArtifact.Admit(revision, Native(family), PreviewPolicy.MaximumNativeBytes), PreviewTargetSelection.All,
        new(null, Input(family)), [], [], "2026-10-24T12:00:00Z", PreviewLimits.Default)
    { Profile = PreviewExecutionProfile.Document };
}

public sealed class DocumentPreviewFactAttribute : FactAttribute
{
    public DocumentPreviewFactAttribute()
    {
        if (!PostgresFixture.IsConfigured || !OperatingSystem.IsLinux() || string.IsNullOrEmpty(FerruleServiceTestSupport.Executable))
            Skip = "Configure PostgreSQL and WEFT_FERRULE_HOST_EXECUTABLE for actual product document previews.";
    }
}
