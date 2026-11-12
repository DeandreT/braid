using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Transport-decoder tests only; actual PDF/process tests belong to the separate Rust worker.</summary>
public sealed class SpecPdfWorkerResponseTests
{
    private static readonly SpecSourceIdentity Source = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    private static readonly byte[] Captured = "%PDF test captured bytes"u8.ToArray();
    private static readonly Sha256Digest Digest = Sha256Digest.FromBytes(Captured);
    private static JsonObject Response(string status = "partial") => new()
    {
        ["format"] = SpecPdfWorkerResponse.Format,
        ["extractor"] = SpecPdfWorkerResponse.Extractor,
        ["sourceSha256"] = Digest.Value,
        ["sourceByteCount"] = Captured.Length,
        ["status"] = status,
        ["pages"] = status == "partial" ? new JsonArray(Page(1, "café 東京"), Page(2, "")) : new JsonArray()
    };
    private static JsonObject Page(int number, string text) => new() { ["page"] = number, ["text"] = text, ["missingText"] = string.IsNullOrWhiteSpace(text) };
    private static SpecPdfWorkerResponse Decode(JsonObject json) => Decode(Encoding.UTF8.GetBytes(json.ToJsonString()));
    private static SpecPdfWorkerResponse Decode(byte[] bytes) => SpecPdfWorkerResponse.Decode(bytes, 0, Source, Digest, Captured.Length);

    [Fact]
    public void Validated_partial_keeps_exact_page_numbers_unicode_blank_pages_and_source_pin()
    {
        var response = Response(); var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        var result = Decode(bytes);
        Assert.Equal(SpecPdfWorkerStatus.Partial, result.Status); Assert.Equal(Source, result.Source);
        Assert.Equal(Digest, result.SourceDigest); Assert.Equal(Captured.Length, result.SourceByteCount);
        Assert.Equal(Sha256Digest.FromBytes(bytes), result.ResponseDigest);
        Assert.Equal(new[] { 1, 2 }, result.Pages.Select(p => p.Locator.Page));
        Assert.Equal("café 東京", result.Pages[0].Text); Assert.False(result.Pages[0].MissingText);
        Assert.Equal("", result.Pages[1].Text); Assert.True(result.Pages[1].MissingText);
    }

    [Theory]
    [InlineData("encrypted", SpecPdfWorkerStatus.Encrypted)]
    [InlineData("malformed", SpecPdfWorkerStatus.Malformed)]
    [InlineData("limitExceeded", SpecPdfWorkerStatus.LimitExceeded)]
    public void Document_refusals_require_empty_pages(string status, SpecPdfWorkerStatus expected)
    {
        var json = Response(status); Assert.Equal(expected, Decode(json).Status);
        json["pages"] = new JsonArray(Page(1, "private"));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
    }

    [Fact]
    public void Needs_ocr_preserves_all_blank_pages_but_cannot_contain_text_or_no_pages()
    {
        var json = Response("needsOcr");
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json["pages"] = new JsonArray(Page(1, ""), Page(2, " \n\u3000"));
        Assert.Equal(2, Decode(json).Pages.Count);
        json["pages"] = new JsonArray(Page(1, "visible"));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["pages"] = new JsonArray(Page(1, ""));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
    }

    [Theory]
    [InlineData("format", "weft.spec-pdf-extraction.v2")]
    [InlineData("extractor", "unreviewed")]
    [InlineData("sourceSha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("status", "complete")]
    public void Identity_and_closed_status_mismatches_fail(string property, string value)
    {
        var json = Response(); json[property] = value;
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
    }

    [Fact]
    public void Closed_shapes_duplicate_fields_scalar_types_and_source_count_are_strict()
    {
        foreach (var key in Response().Select(p => p.Key))
        { var missing = Response(); missing.Remove(key); Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(missing)); }
        var json = Response(); json["sourceByteCount"] = Captured.Length + 1;
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["sourceByteCount"] = Captured.Length.ToString();
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["extra"] = "private";
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["pages"]![0]!["extra"] = 1;
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["pages"]![0]!["missingText"] = true;
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["pages"]![0]!["missingText"] = "false";
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        var encoded = Response().ToJsonString();
        foreach (var duplicate in new[] { encoded.Insert(1, "\"status\":\"partial\","), encoded.Replace("\"page\":1", "\"page\":1,\"page\":1", StringComparison.Ordinal) })
            Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(Encoding.UTF8.GetBytes(duplicate)));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    public void Missing_duplicate_reordered_or_zero_page_numbers_are_not_renumbered(int first, int second)
    {
        var json = Response(); json["pages"] = new JsonArray(Page(first, "first"), Page(second, "second"));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
    }

    [Fact]
    public void Response_text_page_and_input_limits_and_nonzero_exit_are_enforced()
    {
        var json = Response(); json["pages"] = new JsonArray(Enumerable.Range(1, 257).Select(i => (JsonNode)Page(i, "x")).ToArray());
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        json = Response(); json["pages"] = new JsonArray(Page(1, new string('é', SpecPdfWorkerResponse.MaximumTextBytes / 2 + 1)));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(json));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(new byte[SpecPdfWorkerResponse.MaximumResponseBytes + 1]));
        var bytes = Encoding.UTF8.GetBytes(Response().ToJsonString());
        Assert.Throws<SpecPdfWorkerProtocolException>(() => SpecPdfWorkerResponse.Decode(bytes, 2, Source, Digest, Captured.Length));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => SpecPdfWorkerResponse.Decode(bytes, 0, Source, Digest, SpecPdfWorkerResponse.MaximumSourceBytes + 1));
    }

    [Theory]
    [InlineData("{}{}")]
    [InlineData("{\"private\":")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Invalid_transport_never_echoes_output(string text)
    {
        var failure = Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("PDF worker response could not be admitted.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void Invalid_utf8_and_unpaired_surrogates_are_not_normalized()
    {
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode([0xff, 0xfe]));
        var json = Response(); json["pages"] = new JsonArray(Page(1, "sentinel"));
        var raw = json.ToJsonString().Replace("sentinel", "\\uD800", StringComparison.Ordinal);
        Assert.Throws<SpecPdfWorkerProtocolException>(() => Decode(Encoding.UTF8.GetBytes(raw)));
    }
}
