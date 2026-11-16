using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Synthetic current-observation/CAS vectors only. Actual same-statement authority, PostgreSQL
/// grants/locks, concurrent commits, cancellation and durability require separate qualification.
/// </summary>
public sealed class ProjectPayloadPermissionContractTests
{
    private static readonly ProjectScope Scope = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly PrincipalId Principal = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static ActorContext Actor => new(Principal);
    private static ProjectAccess Access(ProjectRole role = ProjectRole.Viewer) => new(Scope, Principal, role);
    private static ProjectPayloadGrant Grant(bool active = true, long version = 1) =>
        new(Scope, Principal, active, version);

    [Fact]
    public void GrantRequiresFullOwnershipAndPositiveInt64Version()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectPayloadGrant(null!, Principal, true, 1));
        Assert.Throws<ArgumentNullException>(() => new ProjectPayloadGrant(Scope, null!, true, 1));
        foreach (var version in new[] { long.MinValue, -1, 0 })
            Assert.Throws<ArgumentOutOfRangeException>(() => Grant(version: version));
        var grant = Grant(version: long.MaxValue);
        Assert.Equal(Scope, grant.Scope);
        Assert.Equal(Principal, grant.PrincipalId);
        Assert.True(grant.Active);
        Assert.Equal(long.MaxValue, grant.Version);
        Assert.Equal(nameof(ProjectPayloadGrant), grant.ToString());
    }

    [Theory]
    [InlineData(ProjectRole.Viewer)]
    [InlineData(ProjectRole.Runner)]
    [InlineData(ProjectRole.Publisher)]
    [InlineData(ProjectRole.Administrator)]
    public void EveryKnownRoleRequiresAnIndependentActiveGrant(ProjectRole role)
    {
        var access = Access(role);
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, access, null));
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, access, Grant(false, 2)));
        var grant = Grant(version: 7);
        var observed = ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, access, grant);
        Assert.Same(grant, observed);
        Assert.Equal(Scope, observed!.Scope);
        Assert.Equal(Principal, observed.PrincipalId);
        Assert.Equal(7, observed.Version);
        // This observes only the separate grant: no role/action permission is added or inferred.
    }

    [Fact]
    public void CurrentAuthorityRevocationClosesAndRestorationDoesNotUndoExplicitGrantRevocation()
    {
        var originalGrant = Grant(version: 7);
        var earlier = ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, Access(), originalGrant);
        Assert.Same(originalGrant, earlier);
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, null, originalGrant));
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, null, null));
        var explicitlyRevoked = Grant(false, 8);
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, Access(), explicitlyRevoked));
        // An unchanged active row can be effective after independently restored current authority.
        Assert.Same(originalGrant, ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, Access(), originalGrant));
        Assert.True(earlier!.Active);
        Assert.Equal(7, earlier.Version); // An immutable past fact cannot override a later observation.
    }

    [Fact]
    public void ForeignOrganizationProjectAndPrincipalFactsAreIntegrityFailures()
    {
        var tuples = new[]
        {
            (new ProjectScope(Other, Scope.ProjectId), Principal),
            (new ProjectScope(Scope.OrganizationId, Other), Principal),
            (Scope, new PrincipalId(Other))
        };
        foreach (var (scope, principal) in tuples)
        {
            var foreignGrant = new ProjectPayloadGrant(scope, principal, true, 7);
            var foreignAccess = new ProjectAccess(scope, principal, ProjectRole.Administrator);
            Assert.Throws<InvalidOperationException>(() =>
                ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, Access(), foreignGrant));
            Assert.Throws<InvalidOperationException>(() =>
                ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, foreignAccess, Grant()));
            Assert.Throws<InvalidOperationException>(() =>
                ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, null, foreignGrant));
            Assert.Throws<InvalidOperationException>(() =>
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 7, null, foreignGrant));
            Assert.Throws<InvalidOperationException>(() =>
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 0, foreignAccess, null));
        }
    }

    [Fact]
    public void ObservationRequiresAnExplicitRequestedActorAndScope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProjectPayloadPermissionPolicy.ObserveCurrent(null!, Scope, Access(), Grant()));
        Assert.Throws<ArgumentNullException>(() =>
            ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, null!, Access(), Grant()));
        Assert.Throws<InvalidOperationException>(() => ProjectPayloadPermissionPolicy.ObserveCurrent(
            new ActorContext(new PrincipalId(Other)), Scope, Access(), Grant()));
    }

    [Fact]
    public void AbsentOwnerTupleRequiresZeroAndRevokeNeverCreatesAnInactiveRow()
    {
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Created,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 0, Access(), null));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionConflict,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 1, Access(), null));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Unchanged,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 0, null, null));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionConflict,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 1, null, null));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionConflict,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, long.MaxValue, null, null));
        Assert.Null(ProjectPayloadGrantOwnerResult.WithoutGrant(ProjectPayloadGrantOwnerOutcome.Unchanged).Grant);
    }

    [Fact]
    public void ActiveOwnerRequestsRequireCurrentAuthorityEvenForReplayAndExplicitRegrant()
    {
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 0, null, null));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 7, null, Grant(version: 7)));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 8, null, Grant(false, 8)));
    }

    [Fact]
    public void OwnerCanRevokeWithoutActiveAuthorityAndReplayThatExactRevocation()
    {
        var existing = Grant(version: 4);
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Updated,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 4, null, existing));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Unchanged,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 5, null, Grant(false, 5)));
        Assert.True(existing.Active);
        Assert.Equal(4, existing.Version);
    }

    [Fact]
    public void ExistingVersionMustMatchBeforeSameStateOrChangedState()
    {
        foreach (var currentActive in new[] { false, true })
            foreach (var requestedActive in new[] { false, true })
                foreach (var expected in new[] { 0L, 6, 8, long.MaxValue })
                    Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionConflict,
                        ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, requestedActive, expected,
                            Access(), Grant(currentActive, 7)));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Unchanged,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 7, Access(), Grant(version: 7)));
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Unchanged,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, 7, null, Grant(false, 7)));
    }

    [Fact]
    public void ExplicitConditionalOwnerRegrantDoesNotMutateTheRevokedObservation()
    {
        var revoked = Grant(false, 6);
        Assert.Equal(ProjectPayloadGrantOwnerOutcome.Updated,
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, true, 6, Access(), revoked));
        Assert.Null(ProjectPayloadPermissionPolicy.ObserveCurrent(Actor, Scope, Access(), revoked));
        Assert.False(revoked.Active);
        Assert.Equal(6, revoked.Version);
    }

    [Fact]
    public void MaximumVersionRefusesBothMutationDirectionsButAllowsExactReplay()
    {
        foreach (var active in new[] { false, true })
        {
            var current = Grant(active, long.MaxValue);
            Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionExhausted,
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, !active, long.MaxValue, Access(), current));
            Assert.Equal(ProjectPayloadGrantOwnerOutcome.Unchanged,
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, active, long.MaxValue, Access(), current));
            Assert.Equal(ProjectPayloadGrantOwnerOutcome.VersionConflict,
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, active, long.MaxValue - 1, Access(), current));
            Assert.Equal(long.MaxValue, current.Version);
            Assert.Equal(active, current.Active);
        }
    }

    [Fact]
    public void OwnerRequiresNonnegativeExpectationAndExplicitScopeAndPrincipal()
    {
        foreach (var expected in new[] { long.MinValue, -1 })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ProjectPayloadPermissionPolicy.ClassifySet(Scope, Principal, false, expected, null, null));
        Assert.Throws<ArgumentNullException>(() =>
            ProjectPayloadPermissionPolicy.ClassifySet(null!, Principal, false, 0, null, null));
        Assert.Throws<ArgumentNullException>(() =>
            ProjectPayloadPermissionPolicy.ClassifySet(Scope, null!, false, 0, null, null));
    }

    [Fact]
    public void ResultFactoriesSeparateCommittedFactsFromAbsenceAndRefusal()
    {
        foreach (var outcome in new[] { ProjectPayloadGrantOwnerOutcome.AuthorityUnavailable,
            ProjectPayloadGrantOwnerOutcome.VersionConflict, ProjectPayloadGrantOwnerOutcome.VersionExhausted })
        {
            Assert.Null(ProjectPayloadGrantOwnerResult.WithoutGrant(outcome).Grant);
            Assert.Throws<ArgumentException>(() => ProjectPayloadGrantOwnerResult.ForGrant(outcome, Grant()));
        }
        foreach (var outcome in new[] { ProjectPayloadGrantOwnerOutcome.Created, ProjectPayloadGrantOwnerOutcome.Updated })
            Assert.Throws<ArgumentException>(() => ProjectPayloadGrantOwnerResult.WithoutGrant(outcome));
        Assert.Throws<ArgumentException>(() => ProjectPayloadGrantOwnerResult.ForGrant((ProjectPayloadGrantOwnerOutcome)0, Grant()));
        Assert.Throws<ArgumentException>(() => ProjectPayloadGrantOwnerResult.WithoutGrant((ProjectPayloadGrantOwnerOutcome)999));
        Assert.Throws<ArgumentNullException>(() =>
            ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Unchanged, null!));
    }

    [Fact]
    public void ResultFactoriesRejectIncoherentCreationOrVersionOneUpdateAndPreserveSuccessFacts()
    {
        Assert.Throws<ArgumentException>(() =>
            ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Created, Grant(false)));
        Assert.Throws<ArgumentException>(() =>
            ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Created, Grant(version: 2)));
        Assert.Throws<ArgumentException>(() =>
            ProjectPayloadGrantOwnerResult.ForGrant(ProjectPayloadGrantOwnerOutcome.Updated, Grant()));
        foreach (var (outcome, grant) in new[]
        {
            (ProjectPayloadGrantOwnerOutcome.Created, Grant()),
            (ProjectPayloadGrantOwnerOutcome.Updated, Grant(false, 2)),
            (ProjectPayloadGrantOwnerOutcome.Unchanged, Grant(false, long.MaxValue))
        })
        {
            var result = ProjectPayloadGrantOwnerResult.ForGrant(outcome, grant);
            Assert.Equal(outcome, result.Outcome);
            Assert.Same(grant, result.Grant);
            Assert.Equal(nameof(ProjectPayloadGrantOwnerResult), result.ToString());
        }
    }
}
