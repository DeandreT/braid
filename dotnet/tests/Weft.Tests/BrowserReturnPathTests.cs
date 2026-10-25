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
        Assert.Equal(BrowserReturnPath.MaximumLength, maximum);
        var longest = prefix + new string(bound.GetProperty("fill").GetString()![0], maximum - prefix.Length);
        Assert.True(BrowserReturnPath.IsValid(longest));
        Assert.False(BrowserReturnPath.IsValid(longest + "a"));
        Assert.False(BrowserReturnPath.IsValid(null));
        Assert.True(BrowserReturnPath.IsValid(BrowserReturnPath.Default));
    }

    private static TheoryData<string> Read(string name)
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.GetProperty(name).EnumerateArray()) data.Add(item.GetString()!);
        return data;
    }
}
