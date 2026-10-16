using System.Text.Json;
using Weft.Application;

namespace Weft.Tests;

public sealed class ExternalIdentityKeyTests
{
    [Fact]
    public void ExactUtf8BoundariesPreserveValidScalarText()
    {
        var provider = "a" + new string('0', 63);
        var authority = new string('é', 512);
        var subject = string.Concat(Enumerable.Repeat("😀", 128));
        var key = new ExternalIdentityKey(provider, authority, subject);
        Assert.Equal(provider, key.Provider);
        Assert.Equal(authority, key.Authority);
        Assert.Equal(subject, key.Subject);
        Assert.Equal(key, new ExternalIdentityKey(provider, authority, subject));
        Assert.True(key == new ExternalIdentityKey(provider, authority, subject));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OneByteBeyondEachBudgetIsRefused(int component)
    {
        var provider = component == 0 ? new string('a', 65) : "provider";
        var authority = component == 1 ? new string('é', 512) + "x" : "authority";
        var subject = component == 2 ? string.Concat(Enumerable.Repeat("😀", 128)) + "x" : "subject";
        Assert.Throws<ArgumentException>(() => new ExternalIdentityKey(provider, authority, subject));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Provider")]
    [InlineData("1provider")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("é")]
    public void ProviderIsAnExactLowerAsciiIdentifierNotANormalizationRequest(string provider)
        => Assert.Throws<ArgumentException>(() => new ExternalIdentityKey(provider, "authority", "subject"));

    [Fact]
    public void NullEmptyControlsAndInvalidUtf16NeverBecomeUsableOpaqueKeys()
    {
        Assert.Throws<ArgumentNullException>(() => new ExternalIdentityKey(null!, "authority", "subject"));
        Assert.Throws<ArgumentNullException>(() => new ExternalIdentityKey("provider", null!, "subject"));
        Assert.Throws<ArgumentNullException>(() => new ExternalIdentityKey("provider", "authority", null!));
        foreach (var text in new[] { "", "\0", "\n", "\r", "\u007f", "\u0085", "\ud800", "\udfff" })
        {
            Assert.Throws<ArgumentException>(() => new ExternalIdentityKey("provider", text, "subject"));
            Assert.Throws<ArgumentException>(() => new ExternalIdentityKey("provider", "authority", text));
        }
    }

    [Fact]
    public void CaseWhitespaceAndCanonicalUnicodeVariantsAreDistinctAccounts()
    {
        var exact = new ExternalIdentityKey("provider", "Authority", "é");
        foreach (var other in new[]
        {
            new ExternalIdentityKey("other", "Authority", "é"),
            new ExternalIdentityKey("provider", "authority", "é"),
            new ExternalIdentityKey("provider", " Authority", "é"),
            new ExternalIdentityKey("provider", "Authority ", "é"),
            new ExternalIdentityKey("provider", "Authority", "e\u0301"),
            new ExternalIdentityKey("provider", "Authority", "É"),
            new ExternalIdentityKey("provider", "Authority", " é"),
            new ExternalIdentityKey("provider", "Authority", "é ")
        })
        {
            Assert.NotEqual(exact, other);
            Assert.True(exact != other);
        }
    }

    [Fact]
    public void TupleBoundariesAndTenantSubjectsCannotAliasThroughAReadableName()
    {
        var a = new ExternalIdentityKey("provider", "tenant|subject", "person@example.invalid");
        var b = new ExternalIdentityKey("provider", "tenant", "subject|person@example.invalid");
        var c = new ExternalIdentityKey("provider", "different-tenant", "person@example.invalid");
        var values = new Dictionary<ExternalIdentityKey, string> { [a] = "first", [b] = "second", [c] = "third" };
        Assert.Equal(3, values.Count);
        Assert.Equal("first", values[new ExternalIdentityKey("provider", "tenant|subject", "person@example.invalid")]);
    }

    [Fact]
    public void KeyAndRejectedInputDiagnosticsDoNotSerializeExternalIdentifiers()
    {
        var key = new ExternalIdentityKey("private-provider", "private-authority", "private-subject");
        Assert.Equal("ExternalIdentityKey", key.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(key));
        var bad = "private-rejected-subject\ud800";
        var error = Assert.Throws<ArgumentException>(() => new ExternalIdentityKey("provider", "authority", bad));
        Assert.DoesNotContain("private-rejected-subject", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }
}
