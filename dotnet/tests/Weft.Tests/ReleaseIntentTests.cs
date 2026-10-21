using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class ReleaseIntentTests
{
    [Fact]
    public void Exact_activation_and_rollback_intents_have_stable_distinct_fingerprints()
    {
        var fixture = new BuildQualificationTests.Fixture(); var qualification = fixture.Create(Guid.NewGuid());
        var scope = qualification.Artifact.Intent.Revision;
        var target = new ReleaseTargetReference(new(scope.OrganizationId, scope.ProjectId, scope.MappingId), qualification.Artifact.Intent.Environment, Guid.NewGuid());
        var id = Guid.NewGuid();
        var first = new ReleaseIntent(id, fixture.Actor, target, qualification, ReleaseAction.Activate, null);
        var replay = new ReleaseIntent(id, fixture.Actor, target, qualification, ReleaseAction.Activate, null);
        Assert.Equal(first.Fingerprint, replay.Fingerprint);
        var prior = Guid.NewGuid();
        var rollback = new ReleaseIntent(id, fixture.Actor, target, qualification, ReleaseAction.Rollback, Guid.NewGuid(), prior);
        Assert.NotEqual(first.Fingerprint, rollback.Fingerprint);
        Assert.Equal(qualification.Artifact.ContentDigest, rollback.Qualification.Artifact.ContentDigest);
        Assert.Equal(prior, rollback.RollbackOfReleaseId);
        Assert.Equal(nameof(ReleaseIntent), first.ToString());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("mapping")]
    [InlineData("environment-version")]
    [InlineData("environment-id")]
    public void Qualified_identity_cannot_be_retargeted(string changed)
    {
        var fixture = new BuildQualificationTests.Fixture(); var qualification = fixture.Create(Guid.NewGuid());
        var scope = qualification.Artifact.Intent.Revision;
        var environment = qualification.Artifact.Intent.Environment;
        var target = new ReleaseTargetReference(new(scope.OrganizationId, scope.ProjectId, changed == "mapping" ? Guid.NewGuid() : scope.MappingId),
            new(environment.Project, changed == "environment-id" ? Guid.NewGuid() : environment.EnvironmentId, changed == "environment-version" ? 2 : environment.Version), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new ReleaseIntent(Guid.NewGuid(), changed == "actor" ? new PrincipalId(Guid.NewGuid()) : fixture.Actor,
            target, qualification, ReleaseAction.Activate, null));
    }

    [Fact]
    public void Rollback_requires_both_a_prior_release_and_a_current_pointer_precondition()
    {
        var fixture = new BuildQualificationTests.Fixture(); var qualification = fixture.Create(Guid.NewGuid());
        var scope = qualification.Artifact.Intent.Revision;
        var target = new ReleaseTargetReference(new(scope.OrganizationId, scope.ProjectId, scope.MappingId), qualification.Artifact.Intent.Environment, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new ReleaseIntent(Guid.NewGuid(), fixture.Actor, target, qualification, ReleaseAction.Rollback, null, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new ReleaseIntent(Guid.NewGuid(), fixture.Actor, target, qualification, ReleaseAction.Rollback, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new ReleaseIntent(Guid.NewGuid(), fixture.Actor, target, qualification, ReleaseAction.Activate, null, Guid.NewGuid()));
    }
}
