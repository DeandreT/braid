using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>Synthetic owner-policy facts only; no claim about actual SQL/grants or command execution.</summary>
public sealed class ExternalIdentityOwnerPreparationTests
{
    private static readonly PrincipalId Principal = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid Organization = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void PrincipalPreparationCreatesOnlyMissingAndNeverReactivatesInactiveIdentity()
    {
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Created,
            ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(Principal, null));
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Existing,
            ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(Principal, true));
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Inactive,
            ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(Principal, false));
    }

    [Theory]
    [InlineData(false, null, ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing)]
    [InlineData(true, null, ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalMissing)]
    [InlineData(true, false, ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalInactive)]
    public void AdmissionPreparationCannotCreateOrReactivateItsParents(bool organizationExists,
        bool? principalActive, ExternalIdentityOrganizationAdmissionPreparationOutcome expected)
        => Assert.Equal(expected, ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
            Organization, Principal, organizationExists, principalActive, null));

    [Fact]
    public void CurrentInactivePrincipalOverridesAnEarlierActiveAdmissionObservation()
    {
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, true, true));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalInactive,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, false, true));
    }

    [Fact]
    public void RepeatingPreparationCannotReactivateAnInactiveAdmission()
    {
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.AdmissionInactive,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, true, false));
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Existing,
            ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(Principal, true));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.AdmissionInactive,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, true, false));
    }

    [Fact]
    public void ExplicitAdmissionIsSeparateFromPreparingAPrincipalAndActiveReplayRemainsUnchanged()
    {
        Assert.Equal(ExternalIdentityPrincipalPreparationOutcome.Created,
            ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(Principal, null));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, false, true, null));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Created,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, true, null));
        Assert.Equal(ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing,
            ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(Organization, Principal, true, true, true));
        // These pure decisions do not assert a DB write or any inferred binding/project/capability grant.
    }

    [Fact]
    public void BrokenAdmissionParentFactsRemainIntegrityFailureRatherThanMissingAccess()
    {
        Assert.Throws<InvalidOperationException>(() => ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
            Organization, Principal, false, true, true));
        Assert.Throws<InvalidOperationException>(() => ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
            Organization, Principal, true, null, false));
    }

    [Fact]
    public void EmptyOrganizationAndMissingInternalPrincipalAreRefusedBeforeAnyOwnerPreparation()
    {
        Assert.Throws<ArgumentNullException>(() => ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(null!, null));
        Assert.Throws<ArgumentException>(() => ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
            Guid.Empty, Principal, true, true, null));
        Assert.Throws<ArgumentNullException>(() => ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
            Organization, null!, true, true, null));
    }
}
