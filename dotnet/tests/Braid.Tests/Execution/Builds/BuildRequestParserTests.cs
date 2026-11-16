using System.Text;
using Microsoft.AspNetCore.Http;
using Braid.Api.Catalog;

namespace Braid.Tests;

public sealed class BuildRequestParserTests
{
    private const string Id = "3f702348-acde-4d9f-a123-4123c11def00";
    private static string Valid => "{\"buildId\":\"" + Id + "\",\"revisionId\":\"" + Id + "\",\"environmentId\":\"" + Id +
        "\",\"environmentVersion\":\"1\",\"regressionEvidenceId\":\"" + Id + "\"}";

    [Fact]
    public async Task Exact_selectors_are_preserved_without_accepting_runtime_or_source_authority()
    {
        var value = await CatalogBuildRequest.ApprovalAsync(Request(Valid), default);
        Assert.Equal(Guid.Parse(Id), value.BuildId); Assert.Equal(1, value.EnvironmentVersion);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("digest")]
    [InlineData("runtime")]
    [InlineData("numeric-version")]
    [InlineData("leading-zero")]
    [InlineData("uppercase-id")]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("nested")]
    public async Task Malformed_duplicate_unknown_or_authoritative_fields_are_rejected(string mutation)
    {
        var body = mutation switch
        {
            "duplicate" => Valid[..^1] + ",\"buildId\":\"" + Id + "\"}",
            "digest" => Valid[..^1] + ",\"nativeDigest\":\"" + new string('a', 64) + "\"}",
            "runtime" => Valid[..^1] + ",\"runtime\":\"dotnet\"}",
            "numeric-version" => Valid.Replace("\"environmentVersion\":\"1\"", "\"environmentVersion\":1", StringComparison.Ordinal),
            "leading-zero" => Valid.Replace("\"environmentVersion\":\"1\"", "\"environmentVersion\":\"01\"", StringComparison.Ordinal),
            "uppercase-id" => Valid.Replace(Id, Id.ToUpperInvariant(), StringComparison.Ordinal),
            "null" => Valid.Replace("\"buildId\":\"" + Id + "\"", "\"buildId\":null", StringComparison.Ordinal),
            "missing" => "{\"buildId\":\"" + Id + "\"}",
            _ => Valid.Replace("\"environmentVersion\":\"1\"", "\"environmentVersion\":{\"x\":1}", StringComparison.Ordinal)
        };
        var error = await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(Request(body), default));
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/vnd.braid.build-approval.v1+json; charset=utf-16")]
    [InlineData("application/vnd.braid.build-approval.v1+json; charset=utf-8; charset=utf-8")]
    [InlineData("application/vnd.braid.build-approval.v1+json; other=x")]
    public async Task Media_and_charset_shape_are_explicit(string media)
    {
        var request = Request(Valid); request.ContentType = media;
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(request, default))).Status);
    }

    [Fact]
    public async Task Encoded_payloads_and_invalid_UTF8_never_reach_selector_construction()
    {
        var encoded = Request(Valid); encoded.Headers.ContentEncoding = "gzip";
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(encoded, default))).Status);
        var invalid = Request(Valid); invalid.Body = new MemoryStream([0xff, 0xfe]); invalid.ContentLength = 2;
        Assert.Equal(StatusCodes.Status400BadRequest,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(invalid, default))).Status);
    }

    [Fact]
    public async Task Declared_and_streamed_body_limits_and_length_mismatch_are_rejected()
    {
        var tooLong = Request(new string(' ', CatalogBuildRequest.MaximumBytes + 1)); tooLong.ContentLength = null;
        Assert.Equal(StatusCodes.Status413PayloadTooLarge,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(tooLong, default))).Status);
        var declared = Request(Valid); declared.ContentLength = CatalogBuildRequest.MaximumBytes + 1;
        Assert.Equal(StatusCodes.Status413PayloadTooLarge,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(declared, default))).Status);
        var mismatch = Request(Valid); mismatch.ContentLength++;
        Assert.Equal(StatusCodes.Status400BadRequest,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.ApprovalAsync(mismatch, default))).Status);
    }

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext(); var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.ContentType = CatalogBuildRequest.ApprovalMediaType;
        context.Request.ContentLength = bytes.Length; context.Request.Body = new MemoryStream(bytes, false);
        return context.Request;
    }
}
