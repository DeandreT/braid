using System.Text;
using Microsoft.AspNetCore.Http;
using Braid.Api.Catalog;

namespace Braid.Tests;

public sealed class BuildAdministrationParserTests
{
    [Theory]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"1\",\"runtimeVersion\":\"caller-owned\"}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"1\",\"engineBuild\":\"caller-owned\"}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"1\",\"active\":true}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"1\",\"profileId\":\"other\"}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":1}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"01\"}")]
    [InlineData("{\"profileId\":\"profile\",\"profileVersion\":\"0\"}")]
    [InlineData("{\"profileId\":\" \",\"profileVersion\":\"1\"}")]
    public async Task Only_exact_profile_identity_selectors_are_accepted(string body)
    {
        var request = Request(body);
        Assert.Equal(StatusCodes.Status400BadRequest,
            (await Assert.ThrowsAsync<CatalogBuildRequestException>(() => CatalogBuildRequest.EnvironmentAsync(request, default))).Status);
    }

    [Fact]
    public async Task Profile_selection_preserves_exact_case_and_canonical_positive_version()
    {
        var selection = await CatalogBuildRequest.EnvironmentAsync(Request("{\"profileId\":\"CaseSensitiveProfile\",\"profileVersion\":\"3\"}"), default);
        Assert.Equal("CaseSensitiveProfile", selection.ProfileId); Assert.Equal(3, selection.ProfileVersion);
    }

    private static HttpRequest Request(string body)
    {
        var context = new DefaultHttpContext(); var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.ContentType = CatalogBuildRequest.EnvironmentMediaType;
        context.Request.ContentLength = bytes.Length; context.Request.Body = new MemoryStream(bytes, false);
        return context.Request;
    }
}
