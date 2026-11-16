using System.Text;
using Microsoft.AspNetCore.Http;
using Braid.Api.Catalog;
using Braid.Domain;

namespace Braid.Tests;

public sealed class CatalogInterchangeParserTests
{
    [Theory]
    [InlineData("{\"reviewDigest\":\"x\",\"acknowledgeWarnings\":true}")]
    [InlineData("{\"reviewDigest\":\"DIGEST\",\"acknowledgeWarnings\":true,\"native\":\"replacement\"}")]
    [InlineData("{\"reviewDigest\":\"DIGEST\",\"reviewDigest\":\"DIGEST\",\"acknowledgeWarnings\":true}")]
    [InlineData("{\"reviewDigest\":\"DIGEST\",\"acknowledgeWarnings\":\"true\"}")]
    public async Task ConfirmationRefusesUnknownDuplicateAndMalformedFields(string json)
    {
        var request = Request("application/json", Encoding.UTF8.GetBytes(json.Replace("DIGEST", new string('a', 64), StringComparison.Ordinal)));
        await Assert.ThrowsAsync<CatalogRevisionRequestException>(() => CatalogInterchangeParser.ConfirmationAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DedicatedPackageAdmissionAcceptsOverNativeLimitButRefusesEncodedOverflow()
    {
        var files = Enumerable.Range(0, 5).Select(index => new InterchangeFile(index == 0 ? "design.mfd" : $"{index}.schema", new byte[1024 * 1024]));
        var package = new InterchangePackage("design.mfd", files);
        var parsed = await CatalogInterchangeParser.PackageAsync(Request(CatalogInterchangeParser.MediaType, package.Encode()), CancellationToken.None);
        Assert.Equal(package.Digest, parsed.Digest);
        var oversized = Request(CatalogInterchangeParser.MediaType, []);
        oversized.ContentLength = InterchangePackage.MaximumEncodedBytes + 1L;
        var error = await Assert.ThrowsAsync<CatalogRevisionRequestException>(() => CatalogInterchangeParser.PackageAsync(oversized, CancellationToken.None));
        Assert.Equal(413, error.StatusCode);
    }

    [Theory]
    [InlineData("application/vnd.braid.mfd-package.v1; charset=utf-8", null)]
    [InlineData("application/vnd.braid.mfd-package.v1", "gzip")]
    [InlineData("application/json", null)]
    public async Task PackageContentIsExactAndUncompressed(string media, string? encoding)
    {
        var request = Request(media, []);
        if (encoding is not null) request.Headers.ContentEncoding = encoding;
        var error = await Assert.ThrowsAsync<CatalogRevisionRequestException>(() => CatalogInterchangeParser.PackageAsync(request, CancellationToken.None));
        Assert.Equal(415, error.StatusCode);
    }

    private static HttpRequest Request(string media, byte[] bytes)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = media;
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
        return request;
    }
}
