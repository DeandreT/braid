using System.Text.Json;
using Weft.Contracts.Auth;

namespace Weft.Tests;

public sealed class AccountDisplayNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\nname")]
    [InlineData("a\u0085name")]
    [InlineData("a\u061cname")]
    [InlineData("a\u202ename")]
    [InlineData("a\u2069name")]
    public void MissingOrUnsafeOptionalNamesAreUnavailable(string? value) => Assert.Null(AccountDisplayName.Optional(value));

    [Fact]
    public void BoundsAreUnicodeScalarsAndMalformedUtf16IsRejected()
    {
        var scalar = char.ConvertFromUtf32(0x1f642);
        var maximum = string.Concat(Enumerable.Repeat(scalar, 200));
        Assert.Equal(maximum, AccountDisplayName.Optional(maximum));
        Assert.Null(AccountDisplayName.Optional(maximum + scalar));
        Assert.Null(AccountDisplayName.Optional("broken\ud800"));
        Assert.Null(AccountDisplayName.Optional("broken\udfff"));
        Assert.Equal("  Zoë <script>&  ", AccountDisplayName.Optional("  Zoë <script>&  "));
    }

    [Fact]
    public void StatusCannotPublishAnonymousNamesAndNeverIncludesThemInDiagnostics()
    {
        Assert.Throws<ArgumentException>(() => new AuthSessionV1(1, "session", "anonymous", "AA", null, null, "Private Name"));
        var value = new AuthSessionV1(1, "session", "authenticated", "AA", "AA", "v1." + new string('A', 43), "<Name>");
        Assert.DoesNotContain("Name", value.ToString());
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("displayName", json);
        Assert.DoesNotContain("<Name>", json);
    }
}
