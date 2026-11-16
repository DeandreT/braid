using Braid.Application;
using Braid.Contracts.Administration;

namespace Braid.Tests;

public sealed class AdministrationContractTests
{
    [Fact]
    public void Invitation_credentials_are_random_bounded_and_never_rendered_in_diagnostics()
    {
        var first = OrganizationInvitationSecret.Create(); var second = OrganizationInvitationSecret.Create();
        Assert.NotEqual(first, second);
        Assert.True(OrganizationInvitationSecret.IsValid(first));
        Assert.NotEqual(first, OrganizationInvitationSecret.Hash(first));
        Assert.DoesNotContain(first, new AdministrationInvitationCreatedV1(Guid.NewGuid(), "/invitations#" + first).ToString());
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_invitation_credentials_are_refused(string? value) => Assert.False(OrganizationInvitationSecret.IsValid(value));
}
