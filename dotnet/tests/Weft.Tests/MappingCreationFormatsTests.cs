using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

public sealed class MappingCreationFormatsTests
{
    private const string X12 = "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *261112*1200*U*00401*000000001*0*P*:~GS*PO*SENDER*RECEIVER*20261112*1200*1*X*004010~ST*850*0001~BEG*00*SA*alpha*beta~SE*3*0001~GE*1*1~IEA*1*000000001~";
    private const string Edifact = "UNB+UNOC:3+SENDER+RECEIVER+261112:1200+1'UNH+1+ORDERS:D:96A:UN'BGM+220+alpha+beta'UNT+3+1'UNZ+1+1'";
    private static readonly string[] Formats = ["json", "xml", "csv", "tsv", "x12", "edifact"];

    [NativeFormatsFact]
    public async Task All_36_typed_boundary_pairs_compile_and_execute_two_fields()
    {
        foreach (var sourceFormat in Formats)
            foreach (var targetFormat in Formats)
            {
                var source = Fixture(sourceFormat); var target = Fixture(targetFormat);
                var plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, target.Boundary,
                    TargetScope(target.Boundary.Schema, target.Constants, source.Paths, target.Paths, [])));
                var output = await CompileAndExecute(plan, source.Sample);
                AssertOutput(targetFormat, output, "alpha", "beta");
                var changed = source.Sample.Replace("alpha", "changedA").Replace("beta", "changedB");
                AssertOutput(targetFormat, await CompileAndExecute(plan, changed), "changedA", "changedB");
            }
    }

    [NativeFormatsFact]
    public async Task Json_to_tabular_preserves_quote_bom_header_and_empty_string_options()
    {
        foreach (var tsv in new[] { false, true })
        {
            var source = Fixture("json");
            var options = tsv ? (CreationFormatOptions)new CreationTsvOptions(true, true, true, true) : new CreationCsvOptions(true, true, true, true);
            var target = new CreationBoundary(Group("Output", [Scalar("code"), Scalar("other")]), options);
            var plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, target,
                new(null, [Bind("code", "code"), Bind("other", "other")])));
            var output = await CompileAndExecute(plan, "{\"code\":\"a,\\\"b\\\"\\n\\t<&é\",\"other\":\"\"}");
            var delimiter = tsv ? '\t' : ',';
            Assert.Equal($"\ufeffcode{delimiter}other\n\"a,\"\"b\"\"\n\t<&é\"{delimiter}\n", output);
        }
    }

    [NativeFormatsFact]
    public async Task Xml_namespace_attribute_simple_text_and_optional_element_execute()
    {
        var source = Fixture("json");
        var body = Group("body", [Scalar("#text", xml: new(null, Text: true)), Scalar("lang", xml: new("urn:meta", Attribute: true))]);
        var target = new CreationBoundary(Group("Document", [Scalar("id", xml: new("", Attribute: true)), body,
            Scalar("absent", xml: new("", Optional: true))], xml: new("urn:creation")), new CreationXmlOptions());
        var scope = new CreationScope(null, [Bind("id", "code")],
            [new("body", [Bind("#text", "other"), new("lang", new CreationConstantValue(CreationValueType.String, "en"))])]);
        var output = await CompileAndExecute(new(new CreationPlanV2(source.Boundary, target, scope)),
            "{\"code\":\"a&b\",\"other\":\"<text>\"}");
        var root = XDocument.Parse(output).Root!;
        Assert.Equal(XName.Get("Document", "urn:creation"), root.Name);
        Assert.Equal("a&b", root.Attribute("id")!.Value);
        var element = root.Element(XName.Get("body", "urn:creation"))!;
        Assert.Equal("<text>", element.Value); Assert.Equal("en", element.Attribute(XName.Get("lang", "urn:meta"))!.Value);
        Assert.DoesNotContain(root.Elements(), e => e.Name.LocalName == "absent");
        var reverse = new MappingCreationPlan(new CreationPlanV2(target, source.Boundary,
            new(null, [Bind("code", "id"), Bind("other", "body", "#text")])));
        using var json = JsonDocument.Parse(await CompileAndExecute(reverse, output));
        Assert.Equal("a&b", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("<text>", json.RootElement.GetProperty("other").GetString());
    }

    [NativeFormatsFact]
    public async Task Nested_source_iteration_constructs_repeating_target_groups()
    {
        var source = new CreationBoundary(Group("Input", [Group("rows", [Scalar("code"), Scalar("other")], repeating: true)]), new CreationJsonOptions());
        var target = new CreationBoundary(Group("Output", [Group("items", [Scalar("first"), Scalar("second")], repeating: true, required: ["first", "second"])], required: ["items"]), new CreationJsonOptions());
        var scope = new CreationScope(null, [], [new("items", [Bind("first", "rows", "code"), Bind("second", "rows", "other")], sourceIteration: ["rows"])]);
        var plan = new MappingCreationPlan(new CreationPlanV2(source, target, scope));
        var output = await CompileAndExecute(plan, "{\"rows\":[{\"code\":\"a\",\"other\":\"b\"},{\"code\":\"c\",\"other\":\"d\"}]}");
        using var json = JsonDocument.Parse(output);
        var rows = json.RootElement.GetProperty("items"); Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("a", rows[0].GetProperty("first").GetString()); Assert.Equal("d", rows[1].GetProperty("second").GetString());
    }

    [NativeFormatsFact]
    public async Task Absolute_source_paths_refuse_root_item_collisions_and_pin_nested_scope_reads()
    {
        foreach (var nestedCollection in new[] { false, true })
        {
            string[] iteration = nestedCollection ? ["batch", "rows"] : ["rows"];
            var rows = Group("rows", [Scalar("code"), Group("details", [Scalar("code")])], repeating: true);
            var source = new CreationBoundary(Group("Input", [Scalar("code"), Scalar("global"), Group("details", [Scalar("code")]),
                nestedCollection ? Group("batch", [rows]) : rows]), new CreationJsonOptions());
            var target = new CreationBoundary(Group("Output", [Scalar("header"), Group("items", [Scalar("code"), Scalar("global"),
                Group("nested", [Scalar("value")])], repeating: true)]), new CreationJsonOptions());
            CreationScope Scope(CreationFieldBinding item, CreationFieldBinding nested) => new(null, [Bind("header", "code")],
                [new("items", [item, Bind("global", "global")], [new("nested", [nested])], sourceIteration: iteration)]);
            var itemRead = Bind("code", [.. iteration, "code"]);
            var nestedRead = Bind("value", [.. iteration, "details", "code"]);
            Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, Scope(Bind("code", "code"), nestedRead)));
            Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, Scope(itemRead, Bind("value", "details", "code"))));

            var plan = new MappingCreationPlan(new CreationPlanV2(source, target, Scope(itemRead, nestedRead)));
            var invalid = JsonNode.Parse(plan.CopyBytes())!;
            invalid["scope"]!["children"]![0]!["bindings"]![0]!["value"]!["path"] = new JsonArray("code");
            Assert.Throws<ArgumentException>(() => MappingCreationPlan.Parse(JsonSerializer.SerializeToUtf8Bytes(invalid)));
            const string data = "\"rows\":[{\"code\":\"ITEM1\",\"details\":{\"code\":\"NESTED1\"}},{\"code\":\"ITEM2\",\"details\":{\"code\":\"NESTED2\"}}]";
            var sample = "{\"code\":\"HEADER\",\"global\":\"ROOT\",\"details\":{\"code\":\"ROOT_DETAIL\"}," +
                (nestedCollection ? "\"batch\":{" + data + "}" : data) + "}";
            using var output = JsonDocument.Parse(await CompileAndExecute(plan, sample));
            Assert.Equal("HEADER", output.RootElement.GetProperty("header").GetString());
            var items = output.RootElement.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal("ITEM" + (i + 1), items[i].GetProperty("code").GetString());
                Assert.Equal("ROOT", items[i].GetProperty("global").GetString());
                Assert.Equal("NESTED" + (i + 1), items[i].GetProperty("nested").GetProperty("value").GetString());
            }
            var compiler = JsonNode.Parse(plan.CopyCompilerPlanBytes())!;
            var itemExpression = compiler["edit"]!["operations"]!.AsArray().Single(o =>
                (string?)o!["op"] == "bind" && (string?)o["binding"]!["field"] == "code")!["expression"]!;
            Assert.Equal(iteration, itemExpression["frame"]!.AsArray().Select(v => (string)v!).ToArray());
            Assert.Equal(new[] { "code" }, itemExpression["path"]!.AsArray().Select(v => (string)v!).ToArray());
            // Reproduce the old unpinned absolute-root spelling through the real compiler/evaluator.
            // The intended root code is HEADER, but legacy fallback silently reads each ITEM instead.
            itemExpression.AsObject().Remove("frame");
            using var broken = JsonDocument.Parse(await CompileAndExecute(plan, sample,
                compilerPlan: JsonSerializer.SerializeToUtf8Bytes(compiler)));
            Assert.Equal("ITEM1", broken.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
        }
    }

    [NativeFormatsFact]
    public async Task Xml_refuses_root_iteration_and_executes_repeating_child_iteration()
    {
        var source = new CreationBoundary(Group("Input", [Scalar("code"), Group("rows", [Scalar("code")], repeating: true)]), new CreationJsonOptions());
        var flatTarget = new CreationBoundary(Group("Output", [Scalar("code")]), new CreationXmlOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, flatTarget, new(null, [Bind("code", "code")], sourceIteration: [])));
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, flatTarget, new(null, [Bind("code", "rows", "code")], sourceIteration: ["rows"])));
        var flat = new MappingCreationPlan(new CreationPlanV2(source, flatTarget, new(null, [Bind("code", "code")])));
        var parsed = JsonNode.Parse(flat.CopyBytes())!; parsed["scope"]!["iteration"] = new JsonArray();
        Assert.Throws<ArgumentException>(() => MappingCreationPlan.Parse(JsonSerializer.SerializeToUtf8Bytes(parsed)));
        var compiler = JsonNode.Parse(flat.CopyCompilerPlanBytes())!;
        compiler["edit"]!["operations"]!.AsArray().Insert(0, JsonNode.Parse("""
            {"op":"set_iteration","scope":{"target":{"kind":"primary"},"children":[]},"source":{"kind":"legacy","path":[]}}
            """));
        await CompileAndExecute(flat, "{\"code\":\"one\"}", "executionFailed", JsonSerializer.SerializeToUtf8Bytes(compiler));

        var target = new CreationBoundary(Group("Output", [Group("item", [Scalar("code")], repeating: true)], xml: new("urn:rows")), new CreationXmlOptions());
        var plan = new MappingCreationPlan(new CreationPlanV2(source, target, new(null, [],
            [new("item", [Bind("code", "rows", "code")], sourceIteration: ["rows"])])));
        var output = XDocument.Parse(await CompileAndExecute(plan, "{\"rows\":[{\"code\":\"first\"},{\"code\":\"second\"}]}"));
        Assert.Equal(XName.Get("Output", "urn:rows"), output.Root!.Name);
        Assert.Equal(new[] { "first", "second" }, output.Root.Elements(XName.Get("item", "urn:rows"))
            .Select(e => e.Element(XName.Get("code", "urn:rows"))!.Value).ToArray());
    }

    [Fact]
    public void Root_reads_refuse_potential_empty_repetition_shadow()
    {
        var source = new CreationBoundary(Group("Input", [Group("details", [Scalar("code")]),
            Group("rows", [Group("details", [Scalar("other")], repeating: true)], repeating: true)]), new CreationJsonOptions());
        var target = new CreationBoundary(Group("Output", [Group("items", [Scalar("code")], repeating: true)]), new CreationJsonOptions());
        // Even without a matching leaf, legacy lookup returns Null at an empty item-local repetition
        // before it can fall back to the declared root details/code. Admission must not depend on sample data.
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target,
            new(null, [], [new("items", [Bind("code", "details", "code")], sourceIteration: ["rows"])])));
    }

    [NativeFormatsFact]
    public async Task Nullable_group_null_and_absence_cannot_shadow_an_absolute_root_scalar()
    {
        var source = new CreationBoundary(Group("Input", [Scalar("code"), Group("rows", [Scalar("item"),
            new CreationSchemaNode("code", null, [Scalar("member")], nullable: true)], repeating: true)]), new CreationJsonOptions());
        var target = new CreationBoundary(Group("Output", [Scalar("header"), Group("items", [Scalar("code", nullable: true)], repeating: true)]), new CreationJsonOptions());
        CreationScope Scope(CreationFieldBinding binding) => new(null, [Bind("header", "code")],
            [new("items", [binding], sourceIteration: ["rows"])]);
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, Scope(Bind("code", "code"))));
        var safe = new MappingCreationPlan(new CreationPlanV2(source, target, Scope(Bind("code", "rows", "item"))));
        var application = JsonNode.Parse(safe.CopyBytes())!;
        application["scope"]!["children"]![0]!["bindings"]![0]!["value"]!["path"] = new JsonArray("code");
        Assert.Throws<ArgumentException>(() => MappingCreationPlan.Parse(JsonSerializer.SerializeToUtf8Bytes(application)));

        var compiler = JsonNode.Parse(safe.CopyCompilerPlanBytes())!;
        var expression = compiler["edit"]!["operations"]!.AsArray().Single(o =>
            (string?)o!["op"] == "bind" && (string?)o["binding"]!["field"] == "code")!["expression"]!;
        expression["path"] = new JsonArray("code"); expression.AsObject().Remove("frame");
        foreach (var group in new[] { ",\"code\":null", "" })
        {
            var sample = "{\"code\":\"HEADER\",\"rows\":[{\"item\":\"ITEM\"" + group + "}]}";
            using var valid = JsonDocument.Parse(await CompileAndExecute(safe, sample));
            Assert.Equal("HEADER", valid.RootElement.GetProperty("header").GetString());
            Assert.Equal("ITEM", valid.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
            // Bypass application admission to reproduce the former bug against the actual compiler/reader.
            using var broken = JsonDocument.Parse(await CompileAndExecute(safe, sample,
                compilerPlan: JsonSerializer.SerializeToUtf8Bytes(compiler)));
            Assert.Equal("HEADER", broken.RootElement.GetProperty("header").GetString());
            var item = broken.RootElement.GetProperty("items")[0];
            Assert.True(!item.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("x12")]
    [InlineData("edifact")]
    public void Terminal_group_shadow_admission_is_conservative_across_structured_formats(string format)
    {
        var options = Fixture(format).Boundary.Options;
        var source = new CreationBoundary(Group("Input", [Group(format == "edifact" ? "UNB" : "ISA", []), Scalar("code"),
            Group("rows", [Group("code", [Scalar("member")])], repeating: true)]), options);
        var target = new CreationBoundary(Group("Output", [Group("items", [Scalar("code", nullable: true)], repeating: true)]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target,
            new(null, [], [new("items", [Bind("code", "code")], sourceIteration: ["rows"])])));
    }

    [NativeFormatsFact]
    public async Task Root_tabular_iteration_executes_all_rows()
    {
        var source = Fixture("csv"); var target = Fixture("json");
        var scope = new CreationScope(null, [Bind("code", "code"), Bind("other", "other")], sourceIteration: []);
        var output = await CompileAndExecute(new(new CreationPlanV2(source.Boundary, target.Boundary, scope)), "code,other\na,b\nc,d\n");
        using var json = JsonDocument.Parse(output); Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal("c", json.RootElement[1].GetProperty("code").GetString());
    }

    [NativeFormatsFact]
    public async Task Tabular_disabled_options_and_json_nullable_source_are_preserved()
    {
        foreach (var options in new CreationFormatOptions[] { new CreationCsvOptions(false, false, false, false), new CreationTsvOptions(false, false, false, false) })
        {
            var target = new CreationBoundary(Group("Output", [Scalar("code"), Scalar("other")]), options);
            var plan = new MappingCreationPlan(new CreationPlanV2(Fixture("json").Boundary, target, new(null, [Bind("code", "code"), Bind("other", "other")])));
            var output = await CompileAndExecute(plan, "{\"code\":\"alpha\",\"other\":\"beta\"}");
            Assert.Equal(options is CreationCsvOptions ? "alpha,beta\n" : "alpha\tbeta\n", output);
        }
        var nullable = new CreationBoundary(Group("Input", [Scalar("code", nullable: true), Scalar("other")], required: ["code", "other"]), new CreationJsonOptions());
        var nullablePlan = new MappingCreationPlan(new CreationPlanV2(nullable, nullable, new(null, [Bind("code", "code"), Bind("other", "other")])));
        using var json = JsonDocument.Parse(await CompileAndExecute(nullablePlan, "{\"code\":null,\"other\":\"present\"}"));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("code").ValueKind);
        Assert.Equal("present", json.RootElement.GetProperty("other").GetString());
    }

    [NativeFormatsFact]
    public async Task Edi_escaping_and_envelope_checks_use_actual_execution()
    {
        var source = Fixture("json"); var edifact = Fixture("edifact");
        var plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, edifact.Boundary,
            TargetScope(edifact.Boundary.Schema, edifact.Constants, source.Paths, edifact.Paths, [])));
        var output = await CompileAndExecute(plan, "{\"code\":\"a+b:c'd?e\",\"other\":\"beta\"}");
        Assert.Contains("BGM+220+a?+b?:c?'d??e+beta'", output);
        var x12 = Fixture("x12");
        var x12Plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, x12.Boundary,
            TargetScope(x12.Boundary.Schema, x12.Constants, source.Paths, x12.Paths, [])));
        await CompileAndExecute(x12Plan, "{\"code\":\"a*b\",\"other\":\"beta\"}", "executionFailed");
        // Static typing cannot certify envelope counts/references: the real executor rejects bad controls.
        x12.Constants["SE/element_1"] = "4";
        var wrongCount = new MappingCreationPlan(new CreationPlanV2(source.Boundary, x12.Boundary,
            TargetScope(x12.Boundary.Schema, x12.Constants, source.Paths, x12.Paths, [])));
        await CompileAndExecute(wrongCount, source.Sample, "executionFailed");
        var fromX12 = new MappingCreationPlan(new CreationPlanV2(x12.Boundary, source.Boundary,
            TargetScope(source.Boundary.Schema, source.Constants, x12.Paths, source.Paths, [])));
        await CompileAndExecute(fromX12, X12.Replace("SE*3*", "SE*4*"), "invalidSample");
    }

    [NativeFormatsFact]
    public async Task Typed_integer_float_boolean_and_json_null_constants_execute()
    {
        var target = new CreationBoundary(Group("Output", [Scalar("integer", CreationValueType.Integer), Scalar("number", CreationValueType.Float),
            Scalar("boolean", CreationValueType.Boolean), Scalar("nothing", nullable: true)], required: ["integer", "number", "boolean", "nothing"]), new CreationJsonOptions());
        var scope = new CreationScope(null, [new("integer", new CreationConstantValue(CreationValueType.Integer, "9223372036854775807")),
            new("number", new CreationConstantValue(CreationValueType.Float, "-1.25e2")),
            new("boolean", new CreationConstantValue(CreationValueType.Boolean, "true")), new("nothing", new CreationConstantValue(null))]);
        var output = await CompileAndExecute(new(new CreationPlanV2(Fixture("json").Boundary, target, scope)), "{}");
        using var json = JsonDocument.Parse(output);
        Assert.Equal(long.MaxValue, json.RootElement.GetProperty("integer").GetInt64());
        Assert.Equal(-125, json.RootElement.GetProperty("number").GetDouble());
        Assert.True(json.RootElement.GetProperty("boolean").GetBoolean()); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nothing").ValueKind);
    }

    [Fact]
    public void V1_canonical_encoding_and_public_properties_are_unchanged()
    {
        var plan = new MappingCreationPlan([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]);
        const string expected = "{\"schema\":\"weft.creation-plan.v1\",\"format\":\"json-record\",\"source\":[{\"Name\":\"a\",\"Type\":1,\"Required\":true}],\"target\":[{\"Name\":\"b\",\"Type\":1,\"Required\":true}],\"bindings\":[{\"Source\":\"a\",\"Target\":\"b\"}]}";
        const string expectedCompiler = """
            {"format":"weft.authoring.v1","source":{"schema":{"name":"Input","shape":{"kind":"group","children":[{"name":"a","shape":{"kind":"scalar","scalar":"string"}}]},"required":["a"]},"options":{"kind":"json"}},"target":{"schema":{"name":"Output","shape":{"kind":"group","children":[{"name":"b","shape":{"kind":"scalar","scalar":"string"}}]},"required":["b"]},"options":{"kind":"json"}},"edit":{"format":"ferrule.edit.v1","operations":[{"op":"bind","binding":{"scope":{"target":{"kind":"primary"},"children":[]},"field":"b"},"expression":{"kind":"source","id":"1","path":["a"]}}]}}
            """;
        Assert.Equal(expected, Encoding.UTF8.GetString(plan.CopyBytes()));
        Assert.Equal(expectedCompiler, Encoding.UTF8.GetString(plan.CopyCompilerPlanBytes()));
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(expected)), plan.Digest);
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(expectedCompiler)), plan.CompilerDigest);
        var restored = MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(expected));
        Assert.Equal(plan.CopyBytes(), restored.CopyBytes()); Assert.Equal(plan.CopyCompilerPlanBytes(), restored.CopyCompilerPlanBytes());
        Assert.Equal(plan.Source, restored.Source); Assert.Equal(plan.Target, restored.Target); Assert.Equal(plan.Bindings, restored.Bindings);
        Assert.Equal(1, restored.Version); Assert.Null(restored.TypedPlan);
    }

    [Fact]
    public void V2_roundtrips_all_formats_with_deterministic_unique_graph_ids_and_defensive_collections()
    {
        foreach (var format in Formats)
        {
            var source = Fixture("json"); var target = Fixture(format);
            var plan = new MappingCreationPlan(new CreationPlanV2(source.Boundary, target.Boundary,
                TargetScope(target.Boundary.Schema, target.Constants, source.Paths, target.Paths, [])));
            var restored = MappingCreationPlan.Parse(plan.CopyBytes());
            Assert.Equal(2, restored.Version); Assert.NotNull(restored.TypedPlan);
            Assert.Equal(plan.CopyBytes(), restored.CopyBytes()); Assert.Equal(plan.CopyCompilerPlanBytes(), restored.CopyCompilerPlanBytes());
            Assert.Equal(plan.Digest, restored.Digest); Assert.Equal(plan.CompilerDigest, restored.CompilerDigest);
            using var compiler = JsonDocument.Parse(plan.CopyCompilerPlanBytes());
            var ids = compiler.RootElement.GetProperty("edit").GetProperty("operations").EnumerateArray().Where(o => o.GetProperty("op").GetString() == "bind")
                .Select(o => o.GetProperty("expression").GetProperty("id").GetString()).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count()); Assert.True(ids.Length >= 2);
        }
        var fields = new[] { Scalar("code") }; var node = Group("Input", fields); fields[0] = Scalar("mutated");
        Assert.Equal("code", node.Children[0].Name);
        var path = new[] { "code" }; var value = new CreationSourceValue(path); path[0] = "mutated"; Assert.Equal("code", value.Path[0]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("operator")]
    [InlineData("position")]
    [InlineData("native")]
    [InlineData("format")]
    [InlineData("option")]
    [InlineData("schema")]
    [InlineData("version")]
    [InlineData("depth")]
    [InlineData("size")]
    public void Strict_v2_parser_refuses_unrecognized_and_unbounded_input(string mutation)
    {
        var plan = Simple(); var root = JsonNode.Parse(plan.CopyBytes())!;
        switch (mutation)
        {
            case "unknown": root["surprise"] = true; break;
            case "missing": root.AsObject().Remove("scope"); break;
            case "operator": root["scope"]!["bindings"]![0]!["value"]!["kind"] = "call"; break;
            case "position": root["scope"]!["bindings"]![0]!["value"]!["kind"] = "position"; break;
            case "native": root["native"] = "{}"; break;
            case "format": root["target"]!["options"]!["kind"] = "jsonLines"; break;
            case "option": root["target"]!["options"]!["autocomplete"] = true; break;
            case "schema": root["target"]!["schema"]!["constraints"] = new JsonObject(); break;
            case "version": root["schema"] = "weft.creation-plan.v3"; break;
        }
        var text = root.ToJsonString();
        if (mutation == "duplicate") text = text.Replace("\"kind\":\"json\"", "\"kind\":\"json\",\"kind\":\"json\"");
        if (mutation == "depth") text = "{\"schema\":\"weft.creation-plan.v2\",\"deep\":" + new string('[', 65) + "0" + new string(']', 65) + "}";
        if (mutation == "size") text = new string(' ', MappingCreationPlan.MaximumBytes + 1);
        Assert.ThrowsAny<Exception>(() => MappingCreationPlan.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Structural_type_required_iteration_and_xml_rules_are_enforced_before_compilation()
    {
        var source = Fixture("json").Boundary; var target = source;
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, new(null, [Bind("code", "undeclared")])));
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, new(null, [Bind("code", "code"), Bind("code", "other")])));
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, target, new(null, [new("code", new CreationConstantValue(CreationValueType.Boolean, "true"))])));
        var required = new CreationBoundary(Group("Output", [Scalar("code"), Scalar("other")], required: ["code", "other"]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, required, new(null, [Bind("code", "code")])));
        var xml = new CreationBoundary(Group("Output", [Scalar("code", xml: new("", Nillable: true))]), new CreationXmlOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, xml, new(null, [Bind("code", "code")])));
        var repeated = new CreationBoundary(Group("Output", [Scalar("code", nullable: true, repeating: true)]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, repeated, new(null, [Bind("code", "code")])));
        var overNodes = new CreationBoundary(Group("Input", Enumerable.Range(0, 512).Select(i => Scalar("v" + i))), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(overNodes, target, new(null, [Bind("code", "v0")])));
        var nestedSource = new CreationBoundary(Group("Input", [Group("rows", [Scalar("code")], repeating: true)]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(nestedSource, target, new(null, [Bind("code", "rows", "code")])));
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(nestedSource, target, new(null, [Bind("code", "rows", "code")], sourceIteration: [])));
        var nestedTarget = new CreationBoundary(Group("Output", [Group("rows", [Scalar("code")], repeating: true)]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(nestedSource, nestedTarget,
            new(null, [], [new("rows", [Bind("code", "rows", "code")], sourceIteration: ["rows"])], sourceIteration: [])));
    }

    [Fact]
    public void Typed_schema_depth_names_literal_and_encoded_byte_budgets_are_bounded()
    {
        var nested = Scalar("leaf");
        for (var i = 0; i < CreationPlanV2.MaximumDepth + 2; i++) nested = Group("level" + i, [nested]);
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(new(nested, new CreationJsonOptions()), Fixture("json").Boundary, new(null, [Bind("code", "code")])));
        var largeName = new CreationBoundary(Group("Input", [Scalar(new string('x', 257))]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(largeName, Fixture("json").Boundary, new(null, [Bind("code", "code")])));
        var source = Fixture("json").Boundary;
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(source, source,
            new(null, [new("code", new CreationConstantValue(CreationValueType.String, new string('x', 4097)))])));
        var fields = Enumerable.Range(0, 20).Select(i => Scalar("v" + i)).ToArray();
        var typed = new CreationPlanV2(source, new(Group("Output", fields), new CreationJsonOptions()),
            new(null, fields.Select(f => new CreationFieldBinding(f.Name, new CreationConstantValue(CreationValueType.String, new string('x', 4096))))));
        Assert.Throws<ArgumentException>(() => new MappingCreationPlan(typed));
        var immutable = Simple(); var bytes = immutable.CopyBytes(); bytes[0] = 0;
        Assert.Equal((byte)'{', immutable.CopyBytes()[0]);
        var compilerBytes = immutable.CopyCompilerPlanBytes(); compilerBytes[0] = 0;
        Assert.Equal((byte)'{', immutable.CopyCompilerPlanBytes()[0]);
    }

    [Theory]
    [InlineData("xmlns", "", true)]
    [InlineData("nil", "http://www.w3.org/2001/XMLSchema-instance", true)]
    [InlineData("code", "http://www.w3.org/2000/xmlns/", false)]
    [InlineData("code", "urn:bad namespace", false)]
    [InlineData("code", "urn:{ambiguous}", false)]
    [InlineData("prefix:code", "urn:valid", false)]
    public void Xml_roles_and_namespace_identities_fail_closed(string name, string uri, bool attribute)
    {
        var target = new CreationBoundary(Group("Output", [Scalar(name, xml: new(uri, Attribute: attribute))]), new CreationXmlOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(Fixture("json").Boundary, target, new(null, [Bind(name, "code")])));
    }

    [Theory]
    [InlineData("content", null, true)]
    [InlineData("#text", "", true)]
    [InlineData("#text", "urn:invalid", true)]
    [InlineData("code", null, false)]
    [InlineData("#text", "", false)]
    public void Xml_text_requires_its_reserved_role_and_null_namespace(string name, string? uri, bool text)
    {
        var target = new CreationBoundary(Group("Output", [Scalar(name, xml: new(uri, Text: text))]), new CreationXmlOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(Fixture("json").Boundary, target, new(null, [Bind(name, "code")])));
    }

    [Theory]
    [InlineData(CreationValueType.Integer, "01")]
    [InlineData(CreationValueType.Integer, "9223372036854775808")]
    [InlineData(CreationValueType.Float, "NaN")]
    [InlineData(CreationValueType.Float, "1e9999")]
    [InlineData(CreationValueType.Float, " 1")]
    [InlineData(CreationValueType.Boolean, "True")]
    public void Constants_require_exact_finite_scalar_syntax(CreationValueType type, string text)
    {
        var target = new CreationBoundary(Group("Output", [Scalar("value", type)]), new CreationJsonOptions());
        Assert.Throws<ArgumentException>(() => new CreationPlanV2(Fixture("json").Boundary, target,
            new(null, [new("value", new CreationConstantValue(type, text))])));
    }

    private static MappingCreationPlan Simple() => new(new CreationPlanV2(Fixture("json").Boundary, Fixture("json").Boundary,
        new(null, [Bind("code", "code"), Bind("other", "other")])));
    private static CreationFieldBinding Bind(string target, params string[] source) => new(target, new CreationSourceValue(source));
    private static CreationSchemaNode Scalar(string name, CreationValueType type = CreationValueType.String, bool nullable = false,
        bool repeating = false, CreationXmlMetadata? xml = null) => new(name, type, nullable: nullable, repeating: repeating, xml: xml);
    private static CreationSchemaNode Group(string name, IEnumerable<CreationSchemaNode> children, bool repeating = false,
        IEnumerable<string>? required = null, CreationXmlMetadata? xml = null) => new(name, null, children, repeating, required: required, xml: xml);

    private sealed record FormatFixture(CreationBoundary Boundary, string Sample, string[][] Paths, Dictionary<string, string> Constants);
    private static FormatFixture Fixture(string format)
    {
        if (format is not ("x12" or "edifact"))
        {
            CreationFormatOptions options = format switch
            {
                "json" => new CreationJsonOptions(),
                "xml" => new CreationXmlOptions(),
                "csv" => new CreationCsvOptions(true, true, false, true),
                _ => new CreationTsvOptions(true, true, false, true)
            };
            var schema = Group("Document", [Scalar("code", xml: format == "xml" ? new("", Attribute: true) : null), Scalar("other")],
                xml: format == "xml" ? new("urn:creation") : null);
            var sample = format switch
            {
                "json" => "{\"code\":\"alpha\",\"other\":\"beta\"}",
                "xml" => "<Document xmlns=\"urn:creation\" code=\"alpha\"><other>beta</other></Document>",
                "csv" => "code,other\nalpha,beta\n",
                _ => "code\tother\nalpha\tbeta\n"
            };
            return new(new(schema, options), sample, [["code"], ["other"]], []);
        }
        var wire = format == "x12" ? X12 : Edifact; var separator = format == "x12" ? '*' : '+'; var terminator = format == "x12" ? '~' : '\'';
        var constants = new Dictionary<string, string>(); var groups = new List<CreationSchemaNode>();
        foreach (var segment in wire.TrimEnd(terminator).Split(terminator))
        {
            var parts = segment.Split(separator); var elements = new List<CreationSchemaNode>();
            for (var i = 1; i < parts.Length; i++)
            {
                var name = "element_" + i;
                if (format == "edifact" && parts[i].Contains(':'))
                {
                    var components = parts[i].Split(':');
                    elements.Add(Group(name, components.Select((_, index) => Scalar("part_" + (index + 1)))));
                    for (var c = 0; c < components.Length; c++) constants[$"{parts[0]}/{name}/part_{c + 1}"] = components[c];
                }
                else { elements.Add(Scalar(name)); constants[$"{parts[0]}/{name}"] = parts[i]; }
            }
            groups.Add(Group(parts[0], elements));
        }
        return new(new(Group("Envelope", groups), format == "x12" ? new CreationX12Options("00401") : new CreationEdifactOptions()), wire,
            format == "x12" ? [["BEG", "element_3"], ["BEG", "element_4"]] : [["BGM", "element_2"], ["BGM", "element_3"]], constants);
    }

    private static CreationScope TargetScope(CreationSchemaNode node, Dictionary<string, string> constants, string[][] sources, string[][] targets, string[] path)
    {
        var bindings = new List<CreationFieldBinding>(); var children = new List<CreationScope>();
        foreach (var child in node.Children)
        {
            string[] location = [.. path, child.Name];
            if (child.ScalarType is null) children.Add(TargetScope(child, constants, sources, targets, location));
            else
            {
                var index = Array.FindIndex(targets, t => t.SequenceEqual(location));
                bindings.Add(new(child.Name, index >= 0 ? new CreationSourceValue(sources[index]) : new CreationConstantValue(CreationValueType.String, constants[string.Join('/', location)])));
            }
        }
        return new(path.Length == 0 ? null : node.Name, bindings, children);
    }

    private static void AssertOutput(string format, string output, string first, string second)
    {
        switch (format)
        {
            case "json":
                using (var json = JsonDocument.Parse(output))
                { Assert.Equal(first, json.RootElement.GetProperty("code").GetString()); Assert.Equal(second, json.RootElement.GetProperty("other").GetString()); }
                break;
            case "xml":
                var root = XDocument.Parse(output).Root!; Assert.Equal("urn:creation", root.Name.NamespaceName);
                Assert.Equal(first, root.Attribute("code")!.Value); Assert.Equal(second, root.Element(XName.Get("other", "urn:creation"))!.Value); break;
            case "csv": Assert.Equal($"code,other\n{first},{second}\n", output); break;
            case "tsv": Assert.Equal($"code\tother\n{first}\t{second}\n", output); break;
            case "x12": Assert.Equal(X12.Replace("alpha", first).Replace("beta", second), output.Replace("\r", "").Replace("\n", "")); break;
            case "edifact": Assert.Equal(Edifact.Replace("alpha", first).Replace("beta", second), output.Replace("\r", "").Replace("\n", "")); break;
        }
    }

    private static async Task<string> CompileAndExecute(MappingCreationPlan plan, string sample, string? expectedFailure = null, byte[]? compilerPlan = null)
    {
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid()); var authoring = Guid.NewGuid();
        var backend = new FerruleInspectionBackend(new MappingAuthoringBackendTests.NativeRunner());
        var result = await backend.AuthorAsync(project, authoring, compilerPlan ?? plan.CopyCompilerPlanBytes());
        Assert.True(result.Failure is null, JsonSerializer.Serialize(result.Failure));
        Assert.True(result.Checkpoint!.Validation.IsValid, JsonSerializer.Serialize(result.Checkpoint.Validation));
        var native = result.Checkpoint.CopyNativeBytes();
        var replay = await backend.AuthorAsync(project, authoring, compilerPlan ?? MappingCreationPlan.Parse(plan.CopyBytes()).CopyCompilerPlanBytes());
        Assert.Null(replay.Failure); Assert.Equal(native, replay.Checkpoint!.CopyNativeBytes());
        var sampleBytes = Encoding.UTF8.GetBytes(sample);
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = "weft.ferrule-authored-preview.v1",
            requestId = Guid.NewGuid().ToString("D"),
            operation = "previewAuthored",
            organizationId = project.OrganizationId.ToString("D"),
            projectId = project.ProjectId.ToString("D"),
            authoringId = authoring.ToString("D"),
            proposalDigest = plan.Digest.Value,
            nativeDigest = Sha256Digest.FromBytes(native).Value,
            sampleDigest = Sha256Digest.FromBytes(sampleBytes).Value
        });
        var response = await Preview([.. Frame(header), .. Frame(native), .. Frame(sampleBytes)]);
        using var stream = new MemoryStream(response);
        using var reply = JsonDocument.Parse(ReadFrame(stream));
        Assert.Equal("238369bf0071ee8ea097d8c54fa5061f433807fb", reply.RootElement.GetProperty("engineRevision").GetString());
        if (expectedFailure is not null)
        {
            Assert.Equal(expectedFailure, reply.RootElement.GetProperty("failure").GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("result").ValueKind);
            Assert.Equal(stream.Length, stream.Position);
            return string.Empty;
        }
        Assert.True(reply.RootElement.GetProperty("failure").ValueKind == JsonValueKind.Null, reply.RootElement.GetProperty("failure").GetRawText());
        var output = ReadFrame(stream); Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(output.Length, reply.RootElement.GetProperty("result").GetProperty("outputByteCount").GetInt32());
        Assert.Equal(Sha256Digest.FromBytes(output).Value, reply.RootElement.GetProperty("result").GetProperty("outputDigest").GetString());
        return Encoding.UTF8.GetString(output);
    }

    private static byte[] Frame(byte[] bytes)
    {
        var frame = new byte[4 + bytes.Length]; BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length); bytes.CopyTo(frame, 4); return frame;
    }
    private static byte[] ReadFrame(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[4]; stream.ReadExactly(prefix); var size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Assert.InRange(size, 0, 1_048_576); var value = new byte[size]; stream.ReadExactly(value); return value;
    }
    private static async Task<byte[]> Preview(byte[] request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE")!)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token); using var output = new MemoryStream();
            var drain = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(request, timeout.Token); process.StandardInput.Close();
            await Task.WhenAll(drain, process.WaitForExitAsync(timeout.Token)); Assert.Equal(0, process.ExitCode); Assert.Empty(await stderr);
            return output.ToArray();
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    public sealed class NativeFormatsFactAttribute : FactAttribute
    {
        public NativeFormatsFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE") is null || Environment.GetEnvironmentVariable("WEFT_AUTHORED_PREVIEW_TEST_EXECUTABLE") is null)
                Skip = "Set both portable Rust compiler/preview executable paths for real format qualification.";
        }
    }
}
