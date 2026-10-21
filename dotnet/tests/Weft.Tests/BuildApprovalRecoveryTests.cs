using Weft.Application;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Tests;

public sealed class BuildApprovalRecoveryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retained_request_recovers_without_current_runtime_resolution_or_renewed_approval(bool revoked, bool inactive)
    {
        var data = new Fixture();
        data.Reader.Value = new(data.Actor.PrincipalId, data.Intent, revoked, !inactive, DateTimeOffset.UtcNow);
        var result = await data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, data.Selection);
        Assert.Equal(BuildApprovalRequestOutcome.Replayed, result.Outcome);
        Assert.Same(data.Reader.Value, result.Approval);
        Assert.Equal(0, data.Factory.Calls);
        Assert.Equal(2, data.Projects.Reads);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("revision")]
    [InlineData("environment")]
    [InlineData("version")]
    [InlineData("evidence")]
    public async Task Changed_selectors_conflict_without_replacing_an_original_request(string field)
    {
        var data = new Fixture();
        data.Reader.Value = new(data.Actor.PrincipalId, data.Intent, false, true, DateTimeOffset.UtcNow);
        var selection = data.Selection with
        {
            BuildId = field == "build" ? Guid.NewGuid() : data.Selection.BuildId,
            RevisionId = field == "revision" ? Guid.NewGuid() : data.Selection.RevisionId,
            EnvironmentId = field == "environment" ? Guid.NewGuid() : data.Selection.EnvironmentId,
            EnvironmentVersion = field == "version" ? 2 : 1,
            RegressionEvidenceId = field == "evidence" ? Guid.NewGuid() : data.Selection.RegressionEvidenceId
        };
        Assert.Equal(BuildApprovalRequestOutcome.Conflict, (await data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, selection)).Outcome);
        Assert.Equal(0, data.Factory.Calls);
    }

    [Fact]
    public async Task Denied_publication_access_does_not_resolve_retained_or_current_build_facts()
    {
        var data = new Fixture(); data.Projects.DenyAt = 1;
        Assert.Equal(BuildApprovalRequestOutcome.Refused,
            (await data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, data.Selection)).Outcome);
        Assert.Equal(0, data.Reader.Calls); Assert.Equal(0, data.Factory.Calls);
    }

    [Fact]
    public async Task Revocation_before_recovery_delivery_does_not_return_the_retained_record()
    {
        var data = new Fixture(); data.Projects.DenyAt = 2;
        data.Reader.Value = new(data.Actor.PrincipalId, data.Intent, false, true, DateTimeOffset.UtcNow);
        var result = await data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, data.Selection);
        Assert.Equal(BuildApprovalRequestOutcome.Refused, result.Outcome); Assert.Null(result.Approval);
    }

    [Fact]
    public async Task An_uncommitted_request_cannot_select_new_runtime_facts_after_a_precondition_failure()
    {
        var data = new Fixture(); data.Factory.ReturnUnavailable = true;
        Assert.Equal(BuildApprovalRequestOutcome.PreconditionFailed,
            (await data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, data.Selection)).Outcome);
        Assert.Equal(1, data.Factory.Calls);
    }

    [Fact]
    public async Task Foreign_retained_actor_is_a_contract_refusal_not_adopted_ownership()
    {
        var data = new Fixture();
        data.Reader.Value = new(new(Guid.NewGuid()), data.Intent, false, true, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Service.PutAsync(data.Actor, data.Mapping, data.Intent.ApprovalId, data.Selection));
        Assert.Equal(0, data.Factory.Calls);
    }

    private sealed class Fixture
    {
        internal ActorContext Actor { get; } = new(new(Guid.NewGuid()));
        internal BuildIntent Intent { get; }
        internal MappingScope Mapping => new(Intent.Revision.OrganizationId, Intent.Revision.ProjectId, Intent.Revision.MappingId);
        internal BuildApprovalSelection Selection => new(Intent.BuildId, Intent.Revision.RevisionId, Intent.Environment.EnvironmentId, 1, Intent.RegressionEvidenceId);
        internal Metadata Reader { get; } = new();
        internal Projects Projects { get; } = new();
        internal Factory Factory { get; } = new();
        internal BuildApprovalRecoveryService Service { get; }
        internal Fixture()
        {
            var scope = new RevisionScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Intent = new(Guid.NewGuid(), scope, Sha256Digest.FromBytes([1]), FixtureBuildCompiler.Backend, FixtureBuildCompiler.Profile,
                [new(BuildBoundaryKind.PrimaryInput, null, "fixture-bytes"), new(BuildBoundaryKind.PrimaryOutput, null, "fixture-bytes")],
                new(new(scope.OrganizationId, scope.ProjectId), Guid.NewGuid(), 1), Guid.NewGuid(), Guid.NewGuid());
            // Any accidental active-admission path fails instead of masquerading as historical recovery.
            Service = new(Reader, Factory, new BuildControlService(null!, null!, null!, null!), Projects);
        }
    }
    private sealed class Factory : IBuildIntentFactory
    {
        internal int Calls;
        internal bool ReturnUnavailable;
        public Task<BuildIntent?> CreateAsync(ActorContext actor, MappingScope mapping, Guid approval, BuildApprovalSelection selection, CancellationToken token = default)
        {
            Calls++;
            return ReturnUnavailable ? Task.FromResult<BuildIntent?>(null) : throw new InvalidOperationException("Current configuration must not replace a retained request.");
        }
    }
    private sealed class Projects : IProjectReader
    {
        internal int Reads, DenyAt;
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope project, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectAccess?>(++Reads == DenyAt ? null : new(project, actor.PrincipalId, ProjectRole.Publisher));
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit, Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Metadata : IBuildMetadataReader
    {
        internal int Calls;
        internal RetainedBuildApproval? Value;
        public Task<RetainedBuildApproval?> ReadApprovalAsync(ActorContext actor, MappingScope mapping, Guid approvalId, CancellationToken token = default)
        { Calls++; return Task.FromResult(Value); }
        public Task<RetainedBuildEnvironment?> ReadEnvironmentAsync(ActorContext actor, BuildEnvironmentReference environment, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildEnvironmentPage?> ListEnvironmentsAsync(ActorContext actor, ProjectScope project, int maximum, BuildEnvironmentCursor? after = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildApprovalPage?> ListApprovalsAsync(ActorContext actor, MappingScope mapping, int maximum, Guid? afterApprovalId = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<BuildRunPage?> ListRunsAsync(ActorContext actor, MappingScope mapping, int maximum, Guid? afterBuildId = null, CancellationToken token = default) => throw new NotSupportedException();
    }
}
