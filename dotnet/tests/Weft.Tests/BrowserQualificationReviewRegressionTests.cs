using Weft.BrowserQualification;
using Weft.HostPrivacyProbe;

namespace Weft.Tests;

public sealed partial class AuthBrowserHttpsQualificationTests
{
    [Fact]
    public void ReturnTargetIsBoundToTheOwnedCatalog()
    {
        var organization = Guid.NewGuid();
        Assert.True(BrowserQualificationAdmission.IsReturnTarget("/catalog", organization));
        Assert.True(BrowserQualificationAdmission.IsReturnTarget($"/catalog/organizations/{organization:D}/projects", organization));
        foreach (var value in new[] { "https://example.invalid/catalog", "//example.invalid/catalog", "/catalog?other=1", "/catalog#other",
            $"/catalog/organizations/{Guid.NewGuid():D}/projects", "/sign-in", "/", "/catalog/../help" })
            Assert.False(BrowserQualificationAdmission.IsReturnTarget(value, organization));
    }
    [Fact]
    public async Task SealRaceAcknowledgesWithoutGrantOrWeakeningWireChecks()
    {
        var owner = new BrowserQualificationAdmission(InertAdmissionInput()); var grant = InertAdmission();
        var accepted = owner.RegisterIfOpen(grant); owner.Close();
        var late = owner.RegisterIfOpen(grant with { Proof = new string('b', 64) });
        int product = 0;
        await owner.InvokeAsync(InertContext(grant), _ => { product++; return Task.CompletedTask; });
        await owner.InvokeAsync(InertContext(grant with { Proof = new string('b', 64) }), _ => { product++; return Task.CompletedTask; });
        Assert.True(accepted.Status == 204 && late.Status == 409 && late.AdmissionSealed && late.ProofPending == 0 && product == 0);
    }
    [Fact]
    public async Task WithdrawRetiresOnlyTheExactPendingGrant()
    {
        var owner = new BrowserQualificationAdmission(InertAdmissionInput()); var grant = InertAdmission(); owner.Register(grant);
        owner.Withdraw(grant with { RawTarget = "/sign-in" });
        bool held = owner.Counts(QualificationReply.Ok()).ProofPending == 1;
        owner.Withdraw(grant);
        int product = 0; var late = InertContext(grant);
        await owner.InvokeAsync(late, _ => { product++; return Task.CompletedTask; });
        var used = grant with { Proof = new string('b', 64) }; owner.Register(used);
        await owner.InvokeAsync(InertContext(used), _ => { product++; return Task.CompletedTask; });
        owner.Withdraw(used);
        var counts = owner.Counts(QualificationReply.Ok());
        Assert.True(held && late.Response.StatusCode == 403 && product == 1 && counts.ProofPending == 0 && counts.ProofUsed == 1 && counts.AdmissionRefusals == 1);
    }
    [Theory]
    [InlineData("ordinary-host", false)]
    [InlineData("ordinary-proxy", false)]
    [InlineData("missing-one", true)]
    [InlineData("proxy-one", true)]
    [InlineData("wrong-vector", false)]
    [InlineData("extra-two", false)]
    [InlineData("resent-one", true)]
    [InlineData("resent-two", false)]
    [InlineData("proxy-speculative", true)]
    [InlineData("proxy-five", false)]
    [InlineData("proxy-other", false)]
    public void ParentFinalAckUsesClosedTotalBudget(string category, bool valid)
    {
        string? name = category is "ordinary-host" or "ordinary-proxy" ? null : category.StartsWith("proxy-", StringComparison.Ordinal) ? "proxy"
            : category == "wrong-vector" ? "popup" : category.StartsWith("resent", StringComparison.Ordinal) ? "location-host" : "missing";
        bool proxied = category is "ordinary-proxy" || category.StartsWith("proxy-", StringComparison.Ordinal);
        int host = proxied ? 0 : category is "extra-two" or "resent-two" ? 2 : 1;
        int proxy = category switch { "proxy-speculative" => 3, "proxy-five" => 5, "proxy-other" => 2, _ => proxied ? 1 : 0 };
        int forbidden = category switch { "ordinary-proxy" => 0, "proxy-other" => 1, _ => proxy };
        bool accepted = true; try { QualificationBackstopPolicy.Validate(name, host, proxy, forbidden); } catch (QualificationFailure) { accepted = false; }
        Assert.True(accepted == valid);
    }
}
