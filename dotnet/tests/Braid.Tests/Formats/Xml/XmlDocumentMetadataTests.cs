using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Braid.Application;

namespace Braid.Tests;

public sealed class XmlDocumentMetadataTests
{
    private const string Empty = "{\"schema\":\"braid.xml-boundary.v1\",\"safetyProfile\":\"braid.xml-safe.v1\",\"schemaHints\":null}";

    [Fact]
    public void Metadata_is_explicit_closed_role_correct_and_compared_by_value()
    {
        var metadata = XmlDocumentMetadata.WithoutHints;
        Assert.Equal(Empty, XmlDocumentMetadataJson.Encode(metadata, XmlDocumentRole.Input));
        Assert.Equal(metadata, XmlDocumentMetadataJson.Decode(Empty, XmlDocumentRole.Output));
        Assert.True(XmlDocumentPolicy.Admits(metadata, XmlDocumentRole.Input, "application/xml"));
        Assert.False(XmlDocumentPolicy.Admits(metadata, XmlDocumentRole.Input, "text/xml"));
        Assert.False(XmlDocumentPolicy.Admits(metadata with { SafetyProfile = "future" }, XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(metadata with { Schema = "future" }, XmlDocumentRole.Output));
        var hinted = Hinted("relative.xsd");
        Assert.True(XmlDocumentPolicy.Admits(hinted, XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(hinted, XmlDocumentRole.Input));
        Assert.Equal(hinted, XmlDocumentMetadataJson.Decode(XmlDocumentMetadataJson.Encode(hinted, XmlDocumentRole.Output), XmlDocumentRole.Output));
        var pairs = new[] { new XmlSchemaLocation("urn:first", "one.xsd"), new XmlSchemaLocation("urn:second", "two.xsd") };
        var hints = new XmlSchemaHints(null, pairs);
        pairs[0] = new("urn:changed", "changed.xsd");
        Assert.Equal("urn:first", hints.Locations[0].Namespace);
        Assert.NotEqual(hints, new XmlSchemaHints(null, hints.Locations.Reverse().ToArray()));
    }

    [Theory]
    [InlineData("\"", "\\u0022")]
    [InlineData("\\", "\\\\")]
    [InlineData("<", "\\u003C")]
    [InlineData(">", "\\u003E")]
    [InlineData("&", "\\u0026")]
    [InlineData("'", "\\u0027")]
    [InlineData("+", "\\u002B")]
    [InlineData("`", "\\u0060")]
    [InlineData("\u007f", "\\u007F")]
    [InlineData("\u00e9", "\\u00E9")]
    [InlineData("\U0001f680", "\\uD83D\\uDE80")]
    public void Golden_encoding_and_count_include_actual_escaped_bytes(string token, string escaped)
    {
        var metadata = Hinted(token);
        var expected = "{\"schema\":\"braid.xml-boundary.v1\",\"safetyProfile\":\"braid.xml-safe.v1\",\"schemaHints\":{\"noNamespaceLocation\":\"" +
            escaped + "\",\"locations\":[]}}";
        Assert.Equal(expected, XmlDocumentMetadataJson.Encode(metadata, XmlDocumentRole.Output));
        Assert.Equal(Encoding.UTF8.GetByteCount("\"xmlMetadata\":" + expected + ","),
            XmlDocumentMetadataJson.MeasureProperty(metadata, XmlDocumentRole.Output));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            XmlDocumentMetadataJson.Write(writer, metadata, XmlDocumentRole.Output);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes.ToArray()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"schema\":\"braid.xml-boundary.v1\",\"safetyProfile\":\"braid.xml-safe.v1\"}")]
    [InlineData("{\"schema\":\"braid.xml-boundary.v1\",\"safetyProfile\":\"braid.xml-safe.v1\",\"schemaHints\":null,\"foreign\":true}")]
    [InlineData("{\"schema\":\"braid.xml-boundary.v1\",\"schema\":\"braid.xml-boundary.v1\",\"safetyProfile\":\"braid.xml-safe.v1\",\"schemaHints\":null}")]
    [InlineData("{\"schema\":4,\"safetyProfile\":\"braid.xml-safe.v1\",\"schemaHints\":null}")]
    public void Absent_unknown_duplicate_and_wrong_type_fields_are_refused(string encoded)
    {
        Assert.Equal(XmlDocumentFailure.InvalidMetadata,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Decode(encoded, XmlDocumentRole.Output)).Reason);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"noNamespaceLocation\":null,\"locations\":[]}")]
    [InlineData("{\"noNamespaceLocation\":\"\",\"locations\":[]}")]
    [InlineData("{\"noNamespaceLocation\":\"x\",\"locations\":null}")]
    [InlineData("{\"noNamespaceLocation\":\"x\",\"locations\":[],\"extra\":null}")]
    [InlineData("{\"noNamespaceLocation\":\"x\",\"noNamespaceLocation\":\"x\",\"locations\":[]}")]
    [InlineData("{\"noNamespaceLocation\":null,\"locations\":[{\"namespace\":\"urn:x\",\"location\":\"x\",\"extra\":null}]}")]
    [InlineData("{\"noNamespaceLocation\":null,\"locations\":[{\"namespace\":\"urn:x\",\"namespace\":\"urn:x\",\"location\":\"x\"}]}")]
    [InlineData("{\"noNamespaceLocation\":null,\"locations\":[{\"namespace\":\"urn:x\",\"location\":\"x\"},{\"namespace\":\"urn:x\",\"location\":\"y\"}]}")]
    public void Hints_are_not_an_open_or_empty_bag(string hints)
    {
        var encoded = Empty.Replace("\"schemaHints\":null", "\"schemaHints\":" + hints, StringComparison.Ordinal);
        Assert.Equal(XmlDocumentFailure.InvalidMetadata,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Decode(encoded, XmlDocumentRole.Output)).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("has\nline")]
    [InlineData("has\rreturn")]
    [InlineData("\0")]
    [InlineData("\ufffe")]
    public void Hint_tokens_follow_upstream_lexical_not_uri_rules(string token)
    {
        Assert.False(XmlDocumentPolicy.Admits(Hinted(token), XmlDocumentRole.Output));
    }

    [Fact]
    public void Hint_utf8_length_surrogates_pair_limit_and_source_null_are_checked()
    {
        Assert.True(XmlDocumentPolicy.Admits(Hinted(new string('\u00e9', 2048)), XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(Hinted(new string('\u00e9', 2049)), XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(Hinted(new string('\ud800', 1)), XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(Hinted(new string('\udc00', 1)), XmlDocumentRole.Output));
        var pairs = Enumerable.Range(0, 32).Select(index => new XmlSchemaLocation($"urn:{index}", "relative.xsd")).ToArray();
        var metadata = XmlDocumentMetadata.WithoutHints with { SchemaHints = new XmlSchemaHints(null, pairs) };
        Assert.True(XmlDocumentPolicy.Admits(metadata, XmlDocumentRole.Output));
        Assert.False(XmlDocumentPolicy.Admits(metadata, XmlDocumentRole.Input));
        Assert.Throws<XmlDocumentException>(() => new XmlSchemaHints(null, pairs.Append(new("urn:extra", "x")).ToArray()));
        Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Encode(metadata, XmlDocumentRole.Input));
    }

    [Fact]
    public void Escaped_unpaired_surrogates_never_escape_as_raw_json_parser_failures()
    {
        var encoded = Empty.Replace("\"schemaHints\":null",
            "\"schemaHints\":{\"noNamespaceLocation\":\"\\uD800\",\"locations\":[]}", StringComparison.Ordinal);
        Assert.Equal(XmlDocumentFailure.InvalidMetadata,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Decode(encoded, XmlDocumentRole.Output)).Reason);
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal(XmlDocumentFailure.InvalidMetadata,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Read(document.RootElement, XmlDocumentRole.Output)).Reason);
    }

    [Fact]
    public void One_operation_ledger_counts_all_eighty_occurrences_without_deduplication()
    {
        var metadata = XmlDocumentMetadata.WithoutHints;
        var inputs = Enumerable.Repeat(metadata, 16).ToArray();
        var outputs = Enumerable.Repeat(metadata, 64).ToArray();
        var total = XmlDocumentMetadataJson.MeasureOperation(inputs, outputs);
        Assert.Equal(80 * XmlDocumentMetadataJson.MeasureProperty(metadata, XmlDocumentRole.Input), total);
        Assert.True(total < 10 * 1024);
        Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.MeasureOperation(inputs.Append(metadata).ToArray(), outputs));
        Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.MeasureOperation(inputs, outputs.Append(metadata).ToArray()));
    }

    [Fact]
    public void Complete_encoded_ledger_accepts_exact_boundary_and_rejects_one_more_byte()
    {
        XmlDocumentMetadata[] inputs = [XmlDocumentMetadata.WithoutHints];
        var outputs = Enumerable.Range(0, 8).Select(_ => Hinted("x")).ToArray();
        var remaining = XmlDocumentPolicy.MaximumMetadataBytes - XmlDocumentMetadataJson.MeasureOperation(inputs, outputs);
        var final = 0;
        for (var index = 0; index < outputs.Length && remaining > 0; index++)
        {
            var added = Math.Min(remaining, XmlDocumentPolicy.MaximumSchemaHintTokenBytes - 1);
            outputs[index] = Hinted(new string('x', added + 1));
            remaining -= added;
            final = index;
        }
        Assert.Equal(0, remaining);
        Assert.Equal(32_768, XmlDocumentMetadataJson.MeasureOperation(inputs, outputs));
        outputs[final] = Hinted(outputs[final].SchemaHints!.NoNamespaceLocation + "x");
        var error = Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.MeasureOperation(inputs, outputs));
        Assert.Equal(XmlDocumentFailure.MetadataTooLarge, error.Reason);
        Assert.Equal(32_769L, error.Observed);
        Assert.Equal(32_768L, error.Limit);
    }

    [Fact]
    public void Upstream_valid_hints_and_small_individual_records_can_exceed_the_shared_encoded_limit()
    {
        var large = Hinted(new string('<', 4096));
        var encoded = XmlDocumentMetadataJson.MeasureProperty(large, XmlDocumentRole.Output);
        Assert.True(encoded > 24_000);
        Assert.Equal(XmlDocumentFailure.MetadataTooLarge, Assert.Throws<XmlDocumentException>(() =>
            XmlDocumentMetadataJson.MeasureOperation([XmlDocumentMetadata.WithoutHints], [large, large])).Reason);
        var pairs = Enumerable.Range(0, 32).Select(index => new XmlSchemaLocation($"urn:{index}", new string('x', 4096))).ToArray();
        var upstreamValid = XmlDocumentMetadata.WithoutHints with { SchemaHints = new XmlSchemaHints(null, pairs) };
        Assert.True(XmlDocumentPolicy.Admits(upstreamValid, XmlDocumentRole.Output));
        Assert.Equal(XmlDocumentFailure.MetadataTooLarge,
            Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.Encode(upstreamValid, XmlDocumentRole.Output)).Reason);
        var small = Hinted(new string('&', 100));
        Assert.True(XmlDocumentMetadataJson.MeasureProperty(small, XmlDocumentRole.Output) < 1024);
        Assert.Throws<XmlDocumentException>(() => XmlDocumentMetadataJson.MeasureOperation(
            [XmlDocumentMetadata.WithoutHints], Enumerable.Repeat(small, 64).ToArray()));
    }

    private static XmlDocumentMetadata Hinted(string token) => XmlDocumentMetadata.WithoutHints with
    {
        SchemaHints = new XmlSchemaHints(token, [])
    };
}
