using Braid.BrowserQualification;

namespace Braid.Tests;

public sealed partial class AuthBrowserHttpsQualificationTests
{
    [OwnedHttpsBrowserTheory]
    [InlineData("location-host")]
    [InlineData("location-ip")]
    [InlineData("location-port")]
    [InlineData("location-http")]
    [InlineData("location-file")]
    [InlineData("location-document")]
    [InlineData("location-duplicate")]
    [InlineData("refresh")]
    [InlineData("popup")]
    [InlineData("frame")]
    [InlineData("worker")]
    [InlineData("audio")]
    [InlineData("paint")]
    [InlineData("disabled")]
    [InlineData("preflight")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("mismatch")]
    [InlineData("stale")]
    [InlineData("detach")]
    [InlineData("seal")]
    [InlineData("teardown")]
    [InlineData("proxy")]
    public async Task ActualOwnedHttpsNetworkAdversary(string category)
    {
        QualificationWire.Require(PostgresFixture.IsConfigured && QualificationNetwork.Cases.Contains(category));
        await AuthBrowserHttpsCase.RunAsync("network-" + category);
    }
}
