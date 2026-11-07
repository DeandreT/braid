using System.Text.Json;
using Weft.Identity;

namespace Weft.Tests;

public sealed class BrowserReturnPathTests
{
    // The Angular sign-in page reads the same vectors (apps/web/tests/return-path.spec.ts).
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Contracts", "browser-return-paths.json"))).RootElement;

    public static TheoryData<string> Valid() => Read("valid");

    public static TheoryData<string> Invalid() => Read("invalid");

    [Theory]
    [MemberData(nameof(Valid))]
    public void AcceptsApplicationSectionPaths(string value) => Assert.True(BrowserReturnPath.IsValid(value));

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RefusesOtherOriginsApiRoutesSignInAndHiddenSeparators(string value) =>
        Assert.False(BrowserReturnPath.IsValid(value));

    [Fact]
    public void BoundsTheLengthAndDefaultsToTheCatalog()
    {
        var bound = Vectors.GetProperty("tooLong");
        var prefix = bound.GetProperty("prefix").GetString()!;
        var maximum = bound.GetProperty("maximumLength").GetInt32();
        Assert.Equal(BrowserReturnPath.MaximumPathLength, maximum);
        Assert.Equal(BrowserReturnPath.MaximumLength, Vectors.GetProperty("bounds").GetProperty("maximumReturnLength").GetInt32());
        var longest = prefix + new string(bound.GetProperty("fill").GetString()![0], maximum - prefix.Length);
        Assert.True(BrowserReturnPath.IsValid(longest));
        Assert.False(BrowserReturnPath.IsValid(longest + "a"));
        Assert.False(BrowserReturnPath.IsValid(null));
        Assert.True(BrowserReturnPath.IsValid(BrowserReturnPath.Default));
    }

    [Theory]
    [InlineData("generatedValid", true)]
    [InlineData("generatedInvalid", false)]
    public void SharedLargeVectorsRetainTheirComponentBounds(string group, bool expected)
    {
        foreach (var item in Vectors.GetProperty(group).EnumerateArray())
        {
            var value = item.GetProperty("prefix").GetString()! + string.Concat(Enumerable.Repeat(
                item.GetProperty("repeat").GetString(), item.GetProperty("count").GetInt32()));
            Assert.True(BrowserReturnPath.IsValid(value) == expected, item.GetProperty("name").GetString());
        }
        Assert.Equal(Weft.Application.CatalogSearchQuery.MaximumBytes, BrowserReturnPath.MaximumSearchQueryBytes);
        Assert.Equal(4096, BrowserReturnPath.MaximumSearchCursorLength);
        Assert.Equal(2048, BrowserReturnPath.MaximumResourceBytes);
        Assert.Equal(12288, BrowserReturnPath.MaximumProtectedStateLength);
    }

    private static TheoryData<string> Read(string name)
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.GetProperty(name).EnumerateArray()) data.Add(item.GetString()!);
        return data;
    }
}
