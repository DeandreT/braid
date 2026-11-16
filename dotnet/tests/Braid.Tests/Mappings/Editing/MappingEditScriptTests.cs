using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Braid.Application;
using Braid.Contracts.Editing;

namespace Braid.Tests;

public sealed class MappingEditScriptTests
{
    internal const string Bind = """
        {"format":"ferrule.edit.v1","operations":[{"op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"code"},"expression":{"kind":"constant","id":"4294967294","value":{"type":"int","text":"9007199254740993"}}}]}
        """;

    [Fact]
    public void ExactValuesAndImmutableOwnedCanonicalLogArePreserved()
    {
        var input = Encoding.UTF8.GetBytes(Bind);
        var script = MappingEditScript.Parse(input);
        Array.Fill(input, (byte)'x');
        Assert.Equal(1, script.Count);
        var operation = Assert.IsType<BindEditV1>(Assert.Single(script.CopyDocument().Operations));
        Assert.Equal("4294967294", Assert.IsType<ConstantEditExpressionV1>(operation.Expression).Id);
        Assert.Equal("9007199254740993", Assert.IsType<IntEditScalarV1>(Assert.IsType<ConstantEditExpressionV1>(operation.Expression).Value).Text);
        var copy = script.CopyUtf8();
        Array.Fill(copy, (byte)'x');
        Assert.Equal(script.Digest, MappingEditScript.Parse(script.CopyUtf8()).Digest);
        Assert.Equal(0, MappingEditScript.Empty.Count);
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1.0")]
    public void IdentitiesAreCanonicalBoundedDecimalText(string id) =>
        Assert.Throws<ArgumentException>(() => MappingEditScript.Parse(Encoding.UTF8.GetBytes(Bind.Replace("4294967294", id, StringComparison.Ordinal))));

    [Fact]
    public void UnknownDuplicateMissingAndNumericMembersAreRefusedBeforeAnyWorker()
    {
        var original = JsonNode.Parse(Bind)!.AsObject();
        var unknown = original.DeepClone(); unknown["actor"] = "other";
        var nested = original.DeepClone(); nested["operations"]![0]!["expression"]!["native"] = "{}";
        var numeric = original.DeepClone(); numeric["operations"]![0]!["expression"]!["id"] = 1;
        var missing = original.DeepClone(); missing["operations"]![0]!["expression"]!.AsObject().Remove("value");
        var unknownOp = original.DeepClone(); unknownOp["operations"]![0]!["op"] = "run_command";
        foreach (var document in new[] { unknown, nested, numeric, missing, unknownOp })
            Assert.Throws<ArgumentException>(() => MappingEditScript.Parse(Encoding.UTF8.GetBytes(document.ToJsonString())));
        Assert.Throws<ArgumentException>(() => MappingEditScript.Parse("{\"format\":\"ferrule.edit.v1\",\"format\":\"ferrule.edit.v1\",\"operations\":[]}"u8));
    }

    [Fact]
    public void OptionalRustMembersCanBeOmittedAndAllNineteenOperationsAreAdmitted()
    {
        const string scope = "{\"target\":{\"kind\":\"primary\"},\"children\":[]}";
        const string expression = "{\"kind\":\"existing\",\"node\":\"1\"}";
        var binding = $"{{\"scope\":{scope},\"field\":\"code\"}}";
        const string rule = "{\"iteration\":{\"kind\":\"source\",\"collection\":{\"kind\":\"primary\",\"path\":[]}},\"selection\":{\"kind\":\"all\"}}";
        var operations = new[]
        {
            $"{{\"op\":\"bind\",\"binding\":{binding},\"expression\":{expression}}}",
            $"{{\"op\":\"retarget_binding\",\"binding\":{binding},\"to\":{binding},\"node\":\"1\"}}",
            $"{{\"op\":\"remove_binding\",\"binding\":{binding}}}",
            $"{{\"op\":\"connect\",\"node\":\"1\",\"input\":\"0\",\"expression\":{expression}}}",
            "{\"op\":\"disconnect\",\"node\":\"1\",\"input\":\"0\"}",
            "{\"op\":\"edit_constant\",\"node\":\"1\",\"value\":{\"type\":\"float\",\"text\":\"-0.0\"}}",
            "{\"op\":\"edit_call\",\"node\":\"1\",\"function\":\"string\",\"args\":[\"1\"]}",
            "{\"op\":\"edit_value_map\",\"node\":\"1\",\"value\":{\"table\":[]}}",
            $"{{\"op\":\"remove_nodes\",\"scope\":{scope},\"nodes\":[\"1\"],\"replacements\":[]}}",
            $"{{\"op\":\"add_scope\",\"parent\":{scope},\"field\":\"items\"}}",
            $"{{\"op\":\"remove_scope\",\"scope\":{scope}}}",
            $"{{\"op\":\"set_iteration\",\"scope\":{scope}}}",
            $"{{\"op\":\"set_filter\",\"scope\":{scope}}}",
            $"{{\"op\":\"set_sort\",\"scope\":{scope},\"keys\":[{{\"node\":\"1\",\"descending\":false}}]}}",
            $"{{\"op\":\"set_grouping\",\"scope\":{scope},\"grouping\":{{\"kind\":\"none\"}}}}",
            $"{{\"op\":\"add_failure_rule\",\"rule\":{rule}}}",
            "{\"op\":\"remove_failure_rule\",\"index\":\"0\"}",
            "{\"op\":\"move_failure_rule\",\"from\":\"0\",\"to\":\"1\"}",
            $"{{\"op\":\"edit_failure_rule\",\"index\":\"0\",\"rule\":{rule}}}"
        };
        var script = MappingEditScript.Parse(Encoding.UTF8.GetBytes("{\"format\":\"ferrule.edit.v1\",\"operations\":[" + string.Join(',', operations) + "]}"));
        Assert.Equal(19, script.Count);
        Assert.Equal(19, script.CopyDocument().Operations.Select(operation => operation.GetType()).Distinct().Count());
    }

    [Fact]
    public void OperationAndByteBoundsAreEnforced()
    {
        var operation = JsonNode.Parse(Bind)!["operations"]![0]!.ToJsonString();
        var atLimit = "{\"format\":\"ferrule.edit.v1\",\"operations\":[" + string.Join(',', Enumerable.Repeat(operation, 2000)) + "]}";
        Assert.Equal(2000, MappingEditScript.Parse(Encoding.UTF8.GetBytes(atLimit)).Count);
        Assert.Throws<ArgumentException>(() => MappingEditScript.Parse(Encoding.UTF8.GetBytes(atLimit.Replace("]}", "," + operation + "]}", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => MappingEditScript.Parse(new byte[MappingEditPolicy.MaximumOperationsBytes + 1]));
    }
}
