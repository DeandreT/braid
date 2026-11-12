using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class XmlNativeMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_saved_options_in_bare_or_versioned_native_artifacts_in_declaration_order(bool envelope)
    {
        var project = Project();
        project["source_path"] = "this-is-not-xml.csv";
        project["target_path"] = "this-is-not-xml.json";
        project["target_options"]!["xml_schema_hints"] = JsonNode.Parse("""
            {"no_namespace_location":"relative.xsd","locations":[{"namespace":"urn:second","location":"two.xsd"},{"namespace":"urn:first","location":"one.xsd"}]}
            """);
        project["extra_sources"] = new JsonArray(Named("source-name"));
        project["extra_targets"] = new JsonArray(Named("target-name"));
        var native = envelope ? new JsonObject
        {
            ["__ferrule_file"] = new JsonObject { ["kind"] = "project", ["version"] = 2, ["float_bits"] = new JsonObject() },
            ["document"] = project
        } : project;
        var result = XmlNativeMetadata.Read(Artifact(native.ToJsonString()));
        Assert.Equal(new[] { "primary-input", "named-input:0", "primary-output", "named-output:0" }, result.Select(value => value.Id));
        Assert.Equal(XmlDocumentMetadata.WithoutHints, result[0].Metadata);
        Assert.Equal(XmlDocumentRole.Input, result[1].Role);
        Assert.Equal(XmlDocumentRole.Output, result[2].Role);
        Assert.Equal("relative.xsd", result[2].Metadata.SchemaHints!.NoNamespaceLocation);
        Assert.Equal(new[] { "urn:second", "urn:first" }, result[2].Metadata.SchemaHints!.Locations.Select(value => value.Namespace));
        Assert.Null(result[3].Metadata.SchemaHints);
    }

    [Fact]
    public void Filenames_xml_looking_content_and_display_schema_never_infer_xml_identity()
    {
        var project = Project();
        project["source_path"] = "input.xml";
        project["target_path"] = "output.xml";
        project["source_options"] = new JsonObject();
        AssertNativeInvalid(project);
        project["source_options"] = new JsonObject { ["json_document"] = true };
        AssertNativeInvalid(project);
    }

    [Theory]
    [InlineData("xml_allow_inactive_root_type_members", "true")]
    [InlineData("xml_root_view_read_policy", "true")]
    [InlineData("local_xml_file_set", "true")]
    [InlineData("wsdl", "{}")]
    [InlineData("http_get", "{}")]
    [InlineData("external_source", "{}")]
    [InlineData("json_document", "true")]
    [InlineData("tabular_kind", "\"csv\"")]
    [InlineData("delimiter", "\",\"")]
    [InlineData("has_header_row", "true")]
    [InlineData("mfd_decimal_input_names", "{\"1\":\"value\"}")]
    [InlineData("mfd_optional_parser_input_names", "{\"1\":\"value\"}")]
    [InlineData("unknown_future_option", "null")]
    [InlineData("lenient_segments", "null")]
    public void Refuses_foreign_nondefault_or_unknown_options_without_stripping_them(string name, string encodedValue)
    {
        var project = Project();
        project["source_options"]![name] = JsonNode.Parse(encodedValue);
        AssertNativeInvalid(project);
    }

    [Fact]
    public void Known_default_fields_do_not_change_the_metadata_projection()
    {
        var project = Project();
        project["source_options"] = JsonNode.Parse("""
            {"xml_document":true,"xml_schema_hints":null,"lenient_segments":false,"edi_kind":null,
             "xml_allow_inactive_root_type_members":false,"xml_root_view_read_policy":false,"wsdl":null,
             "local_xml_file_set":false,"json_document":false,"json_schema_unresolved_reference":null,
             "mfd_decimal_input_names":{},"mfd_optional_parser_input_names":{},"json5":false,
             "tabular_kind":null,"delimiter":null,"csv_quote":null,"csv_quote_disabled":false,
             "csv_utf8_bom":false,"csv_preserve_empty_strings":false,"has_header_row":null,
             "edi_implied_decimals":[],"edi_lexical_formats":[],"edi_value_constraints":[],
             "xlsx_columns":[],"xlsx_headers":[],"xlsx_rows":[],"xlsx_update_existing":false}
            """);
        Assert.All(XmlNativeMetadata.Read(Artifact(project.ToJsonString())),
            boundary => Assert.Equal(XmlDocumentMetadata.WithoutHints, boundary.Metadata));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"no_namespace_location\":null,\"locations\":[]}")]
    [InlineData("{\"no_namespace_location\":\"\",\"locations\":[]}")]
    [InlineData("{\"no_namespace_location\":\"has space\"}")]
    [InlineData("{\"noNamespaceLocation\":\"x\"}")]
    [InlineData("{\"no_namespace_location\":\"x\",\"unknown\":null}")]
    [InlineData("{\"locations\":null}")]
    [InlineData("{\"locations\":[{\"namespace\":\"urn:x\",\"location\":\"x\",\"extra\":null}]}")]
    [InlineData("{\"locations\":[{\"namespace\":\"urn:x\",\"location\":\"x\"},{\"namespace\":\"urn:x\",\"location\":\"y\"}]}")]
    public void Invalid_native_hints_are_not_normalized_into_admitted_metadata(string hints)
    {
        var project = Project();
        project["target_options"]!["xml_schema_hints"] = JsonNode.Parse(hints);
        AssertNativeInvalid(project);
    }

    [Fact]
    public void Source_hints_must_be_null_even_when_upstream_target_hints_would_be_valid()
    {
        var project = Project();
        project["source_options"]!["xml_schema_hints"] = JsonNode.Parse("{\"no_namespace_location\":\"x.xsd\"}");
        AssertNativeInvalid(project);
    }

    [Theory]
    [InlineData("{\"source_options\":{\"xml_document\":true,\"xml_document\":true},\"target_options\":{\"xml_document\":true}}")]
    [InlineData("{\"source_options\":{\"xml_document\":true},\"source_options\":{\"xml_document\":true},\"target_options\":{\"xml_document\":true}}")]
    [InlineData("{\"source_options\":{\"xml_document\":true},\"target_options\":{\"xml_document\":true,\"xml_schema_hints\":{\"no_namespace_location\":\"x\",\"no_namespace_location\":\"x\"}}}")]
    [InlineData("{\"__ferrule_file\":{\"kind\":\"project\",\"version\":99},\"document\":{}}")]
    [InlineData("{\"__ferrule_file\":{\"kind\":\"project\",\"version\":2},\"source_options\":{},\"document\":{}}")]
    [InlineData("{\"document\":{\"source_options\":{\"xml_document\":true},\"target_options\":{\"xml_document\":true}}}")]
    public void Duplicate_fields_and_ambiguous_or_unsupported_envelopes_are_refused(string native)
    {
        Assert.Equal(XmlDocumentFailure.InvalidNativeMetadata,
            Assert.Throws<XmlDocumentException>(() => XmlNativeMetadata.Read(Artifact(native))).Reason);
    }

    [Fact]
    public void Every_declared_boundary_and_unselected_hint_is_charged()
    {
        var project = Project();
        var targets = new JsonArray();
        for (var index = 0; index < 63; index++)
        {
            var target = Named($"target{index}");
            target["options"]!["xml_schema_hints"] = new JsonObject { ["no_namespace_location"] = new string('&', 100) };
            targets.Add(target);
        }
        project["extra_targets"] = targets;
        Assert.Equal(XmlDocumentFailure.MetadataTooLarge,
            Assert.Throws<XmlDocumentException>(() => XmlNativeMetadata.Read(Artifact(project.ToJsonString()))).Reason);
        project["extra_targets"] = new JsonArray(Named("unselected"));
        project["extra_targets"]![0]!["options"]!["json_document"] = true;
        AssertNativeInvalid(project);
    }

    [Fact]
    public void Native_projection_respects_native_bytes_endpoint_counts_and_cancellation()
    {
        var project = Project();
        project["extra_sources"] = new JsonArray(Enumerable.Range(0, 16).Select(index => (JsonNode)Named($"source{index}")).ToArray());
        var tooMany = Assert.Throws<XmlDocumentException>(() => XmlNativeMetadata.Read(Artifact(project.ToJsonString())));
        Assert.Equal(XmlDocumentFailure.DocumentCountLimit, tooMany.Reason);
        Assert.Equal(17L, tooMany.Observed);
        Assert.Equal(16L, tooMany.Limit);
        var oversized = new byte[XmlDocumentPolicy.MaximumNativeBytes + 1];
        Assert.Equal(XmlDocumentFailure.NativeByteLimit,
            Assert.Throws<XmlDocumentException>(() => XmlNativeMetadata.Read(NativeArtifact.Admit(Scope(), oversized, oversized.Length))).Reason);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => XmlNativeMetadata.Read(Artifact(Project().ToJsonString()), cancellation.Token));
    }

    private static void AssertNativeInvalid(JsonObject project) => Assert.Equal(XmlDocumentFailure.InvalidNativeMetadata,
        Assert.Throws<XmlDocumentException>(() => XmlNativeMetadata.Read(Artifact(project.ToJsonString()))).Reason);

    private static RevisionScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static NativeArtifact Artifact(string json) => NativeArtifact.Admit(Scope(), Encoding.UTF8.GetBytes(json), XmlDocumentPolicy.MaximumNativeBytes);

    private static JsonObject Named(string name) => new()
    {
        ["name"] = name,
        ["path"] = "never-open.xml",
        ["schema"] = JsonNode.Parse("{\"name\":\"Row\",\"kind\":{\"kind\":\"group\",\"children\":[]}}"),
        ["options"] = new JsonObject { ["xml_document"] = true }
    };

    private static JsonObject Project() => JsonNode.Parse("""
        {"source":{"name":"Row","kind":{"kind":"group","children":[]}},
         "target":{"name":"Row","kind":{"kind":"group","children":[]}},
         "source_options":{"xml_document":true},"target_options":{"xml_document":true},
         "graph":{"nodes":{}},"root":{"target_field":"","bindings":[],"children":[]}}
        """)!.AsObject();
}
