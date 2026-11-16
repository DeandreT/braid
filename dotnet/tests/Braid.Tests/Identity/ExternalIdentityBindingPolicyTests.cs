using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Synthetic policy observations only; actual database privileges/concurrency/grants remain separate.</summary>
public sealed class ExternalIdentityBindingPolicyTests
{
    private static readonly Guid BindingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly PrincipalId Principal = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly PrincipalId OtherPrincipal = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Created = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static ExternalIdentityKey Key => new("provider", "private-authority", "private-subject");
    private static ExternalIdentityBinding Binding(bool enabled = true, long version = 1) =>
        new(BindingId, Key, Principal, version, enabled, Created, enabled ? null : Created.AddMinutes(1));

    [Fact]
    public void UnknownDisabledAndInactivePrincipalExposeNoResolvedBindingDetail()
    {
        AssertClosed(ExternalIdentityResolution.FromCurrent(null, true), ExternalIdentityResolutionOutcome.Unknown);
        AssertClosed(ExternalIdentityResolution.FromCurrent(Binding(false, 2), true), ExternalIdentityResolutionOutcome.Disabled);
        AssertClosed(ExternalIdentityResolution.FromCurrent(Binding(), false), ExternalIdentityResolutionOutcome.PrincipalInactive);
        var observed = Binding();
        var resolved = ExternalIdentityResolution.FromCurrent(observed, true);
        Assert.Equal(ExternalIdentityResolutionOutcome.Resolved, resolved.Outcome);
        Assert.Same(observed, resolved.Binding);
    }

    [Fact]
    public void EarlierPositiveObservationCannotOverrideLaterCurrentDisableOrPrincipalInactivity()
    {
        var earlier = ExternalIdentityResolution.FromCurrent(Binding(), true);
        var earlierBinding = Assert.IsType<ExternalIdentityBinding>(earlier.Binding);
        AssertClosed(ExternalIdentityResolution.FromCurrent(Binding(false, 2), true), ExternalIdentityResolutionOutcome.Disabled);
        AssertClosed(ExternalIdentityResolution.FromCurrent(Binding(), false), ExternalIdentityResolutionOutcome.PrincipalInactive);
        Assert.True(earlierBinding.Enabled); // An immutable past fact is not a cached authority grant.
    }

    [Fact]
    public void BindingRequiresAnExistingActivePrincipalAndNeverCreatesOrReactivatesOne()
    {
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.PrincipalMissing,
            ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, null, null));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.PrincipalInactive,
            ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, null, false));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Created,
            ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, null, true));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.PrincipalInactive,
            ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, Binding(), false));
    }

    [Fact]
    public void ExistingBindingCannotBeTransferredAndDisabledReplayPreservesItsOriginalFacts()
    {
        var disabled = Binding(false, 2);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.BindingConflict,
            ExternalIdentityBindingPolicy.ClassifyBind(Key, OtherPrincipal, disabled, true));
        var outcome = ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, disabled, true);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Existing, outcome);
        var result = ExternalIdentityBindingOwnerResult.ForBinding(outcome, disabled);
        var facts = Assert.IsType<ExternalIdentityBinding>(result.Binding);
        Assert.Same(disabled, facts);
        Assert.False(facts.Enabled);
        Assert.Equal(2, facts.Version);
        Assert.Equal(Created.AddMinutes(1), facts.DisabledAtUtc);
    }

    [Fact]
    public void WrongReturnedKeyOrBrokenPrincipalReferenceIsAnIntegrityFailureNotUnknownIdentity()
    {
        var otherKey = new ExternalIdentityKey("provider", "other-authority", "private-subject");
        Assert.Throws<InvalidOperationException>(() => ExternalIdentityBindingPolicy.ClassifyBind(otherKey, Principal, Binding(), true));
        Assert.Throws<InvalidOperationException>(() => ExternalIdentityBindingPolicy.ClassifyBind(Key, Principal, Binding(), null));
        Assert.Throws<InvalidOperationException>(() => ExternalIdentityBindingPolicy.ClassifyDisable(otherKey, Principal, 1, Binding()));
    }

    [Fact]
    public void DisableRequiresExactOwnerAndCurrentVersionEvenWhenAlreadyDisabled()
    {
        var active = Binding();
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.UnknownBinding,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 1, null));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.OwnerConflict,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, OtherPrincipal, 1, active));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.VersionConflict,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 2, active));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 1, active));
        var disabled = Binding(false, 2);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.VersionConflict,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 1, disabled));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.AlreadyDisabled,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 2, disabled));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.OwnerConflict,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, OtherPrincipal, 2, disabled));
        Assert.True(active.Enabled);
        Assert.Equal(1, active.Version);
    }

    [Fact]
    public void MaximumVersionRefusesMutationWithoutOverflowAndDisabledMaximumRemainsAReadOnlyNoop()
    {
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.VersionExhausted,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, long.MaxValue, Binding(version: long.MaxValue)));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.AlreadyDisabled,
            ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, long.MaxValue, Binding(false, long.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExternalIdentityBindingPolicy.ClassifyDisable(Key, Principal, 0, Binding()));
    }

    [Fact]
    public void IdentityResolutionCannotReplaceCurrentProjectRoleOrSeparateCreationCapability()
    {
        var scope = new ProjectScope(Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var viewer = new ProjectAccess(scope, Principal, ProjectRole.Viewer);
        var administrator = new ProjectAccess(scope, Principal, ProjectRole.Administrator);
        var independentCapability = new OrganizationProjectCreationAdmission(scope.OrganizationId, Principal, false);
        var resolved = ExternalIdentityResolution.FromCurrent(Binding(), true);
        Assert.Equal(Principal, resolved.Binding?.PrincipalId);
        Assert.False(viewer.Can(ProjectAction.UploadRevision));
        Assert.False(viewer.Can(ProjectAction.EditMetadata));
        Assert.True(administrator.Can(ProjectAction.ManageMembership));
        Assert.False(independentCapability.CanCreateProjects);
        // This tests the separate pure facts, not persistence-side grant absence or a new payload API.
    }

    [Fact]
    public void ResultFactoriesCannotDisguiseRefusalAsBindingSuccessOrExposeDeniedIdentityDetails()
    {
        foreach (var outcome in new[] { ExternalIdentityBindingOwnerOutcome.UnknownBinding,
            ExternalIdentityBindingOwnerOutcome.BindingConflict, ExternalIdentityBindingOwnerOutcome.PrincipalMissing,
            ExternalIdentityBindingOwnerOutcome.PrincipalInactive, ExternalIdentityBindingOwnerOutcome.OwnerConflict,
            ExternalIdentityBindingOwnerOutcome.VersionConflict, ExternalIdentityBindingOwnerOutcome.VersionExhausted })
        {
            Assert.Null(ExternalIdentityBindingOwnerResult.WithoutBinding(outcome).Binding);
            Assert.Throws<ArgumentException>(() => ExternalIdentityBindingOwnerResult.ForBinding(outcome, Binding()));
        }
        Assert.Throws<ArgumentException>(() => ExternalIdentityBindingOwnerResult.WithoutBinding(ExternalIdentityBindingOwnerOutcome.Existing));
        Assert.Throws<ArgumentException>(() => ExternalIdentityBindingOwnerResult.ForBinding(ExternalIdentityBindingOwnerOutcome.Created, Binding(false, 2)));
        Assert.Throws<ArgumentException>(() => ExternalIdentityBindingOwnerResult.ForBinding(ExternalIdentityBindingOwnerOutcome.Disabled, Binding()));
        Assert.Throws<ArgumentException>(() => ExternalIdentityBindingOwnerResult.ForBinding((ExternalIdentityBindingOwnerOutcome)999, Binding()));
    }

    [Fact]
    public void BindingIdsVersionsAndUtcDisableHistoryMustBeCoherent()
    {
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(Guid.Empty, Key, Principal, 1, true, Created));
        Assert.Throws<ArgumentOutOfRangeException>(() => Binding(version: 0));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 1, false, Created, Created));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 2, true, Created, Created));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 2, false, Created));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 2, false, Created, Created.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 1, true, Created.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => new ExternalIdentityBinding(BindingId, Key, Principal, 2, false, Created, Created.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void BindingAndResultDiagnosticsDoNotExposeExternalKeyText()
    {
        var binding = Binding();
        var result = ExternalIdentityBindingOwnerResult.ForBinding(ExternalIdentityBindingOwnerOutcome.Existing, binding);
        Assert.Equal("ExternalIdentityBinding", binding.ToString());
        Assert.Equal("ExternalIdentityBindingOwnerResult", result.ToString());
        var diagnostic = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private-authority", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-subject", diagnostic, StringComparison.Ordinal);
    }

    private static void AssertClosed(ExternalIdentityResolution result, ExternalIdentityResolutionOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Binding);
    }
}
