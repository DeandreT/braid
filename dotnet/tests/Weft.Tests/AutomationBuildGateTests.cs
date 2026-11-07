using System.Text.Json;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AutomationBuildGateTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Approval_requires_a_complete_current_suite_and_preserves_original_approval_replay(bool documents, bool interchanges)
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture, documents: documents, interchanges: interchanges);
        await using var automation = new PostgresAutomationStore(test.Runs.Cases.Case.RegressionConnectionString("weft_run_writer"), true);
        await Enable(automation, test.Actor, test.Intent.Environment.Project);
        Assert.Equal(BuildControlOutcome.RegressionRequired, await test.Approve());
        await Complete(automation, test.Runs.Cases.Case, test.Actor, test.Intent.Revision, test.Runs.Value.Runtime, test.Runs.Success());
        Assert.Equal(BuildControlOutcome.Created, await test.Approve());
        await test.Runs.Cases.Writer.SaveAsync(test.Actor, test.Runs.Cases.Version(2));
        var original = test.Intent;
        var changed = new BuildIntent(Guid.NewGuid(), original.Revision, original.NativeDigest, original.Backend, original.Profile,
            original.Boundaries, original.Environment, Guid.NewGuid(), original.RegressionEvidenceId);
        Assert.Equal(BuildControlOutcome.RegressionRequired, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, changed, test.Native, test.Evidence)));
        Assert.Equal(BuildControlOutcome.Replayed, await test.Approve());
        Assert.Equal(1L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_approvals"));
    }

    [RegressionPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Qualification_rechecks_policy_even_when_the_build_was_approved_before_it_was_enabled(bool documents, bool interchanges)
    {
        await using var test = await BuildQualificationPersistenceTests.Context.CreateAsync(fixture, documents: documents, interchanges: interchanges);
        await using var automation = new PostgresAutomationStore(test.Case.RegressionConnectionString("weft_run_writer"), true);
        await Enable(automation, test.Actor, test.Control.Intent.Environment.Project);
        Assert.Equal(BuildQualificationWriteOutcome.RegressionRequired, await test.Store.SaveAsync(test.Actor, test.Qualification));
        await Complete(automation, test.Case, test.Actor, test.Control.Intent.Revision, test.Control.Runs.Value.Runtime, test.Control.Runs.Success());
        Assert.Equal(BuildQualificationWriteOutcome.Created, await test.Store.SaveAsync(test.Actor, test.Qualification));
        await test.Control.Runs.Cases.Writer.SaveAsync(test.Actor, test.Control.Runs.Cases.Version(2));
        Assert.Equal(BuildQualificationWriteOutcome.RegressionRequired, await test.Store.SaveAsync(test.Actor, test.Make(Guid.NewGuid())));
        Assert.Equal(BuildQualificationWriteOutcome.Replayed, await test.Store.SaveAsync(test.Actor, test.Qualification));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.build_qualifications"));
    }

    private static async Task Enable(PostgresAutomationStore store, ActorContext actor, ProjectScope project) =>
        Assert.True(await store.WriteAsync(actor, project, "policy", null, JsonSerializer.SerializeToElement(new
        { requirePassingRegression = true, errorRatePercent = 20, minimumRequests = 10, windowSeconds = 300 })));

    private static async Task Complete(PostgresAutomationStore store, PostgresCase test, ActorContext actor, RevisionScope revision, RegressionRuntimeIdentity runtime, RegressionCaseExecution result)
    {
        var id = Guid.NewGuid();
        Assert.True(await store.WriteAsync(actor, new(revision.OrganizationId, revision.ProjectId), "recheck", id,
            JsonSerializer.SerializeToElement(new { mappingId = revision.MappingId, revisionId = revision.RevisionId })));
        var claim = await store.ClaimEvaluationAsync(default);
        Assert.NotNull(claim); Assert.Equal(id, claim.SuiteId);
        Assert.True(await store.FinishEvaluationAsync(claim, result, default));
        await using var verifier = new PostgresAutomationComparisonSettler(test.RegressionConnectionString("weft_comparison_settler"), runtime);
        await verifier.SettleNextAsync(default);
    }
}
