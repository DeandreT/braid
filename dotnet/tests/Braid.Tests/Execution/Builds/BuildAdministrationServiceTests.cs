using Braid.Application;
using Braid.Deployment;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildAdministrationServiceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Administrator_revocation_after_a_committed_version_hides_delivery_without_replacing_the_saved_request()
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var metadata = new PostgresBuildMetadataReader(test.Runs.Cases.Case.RegressionConnectionString("braid_build_metadata_reader"));
        await using var catalog = new PostgresCatalogDatabase(test.Runs.Cases.Case.CatalogConnectionString);
        var commands = new DemotingCommands(test.Store, () => test.Runs.Cases.RoleAsync("publisher"));
        var service = new BuildAdministrationService(metadata, commands, test.Store, new PostgresCatalogReader(catalog), test.Intent.Profile);
        var reference = new BuildEnvironmentReference(test.Intent.Environment.Project, test.Intent.Environment.EnvironmentId, 2);
        var selection = new BuildEnvironmentSelection(test.Intent.Profile.Id, test.Intent.Profile.Version);
        var hidden = await service.PutEnvironmentAsync(test.Actor, reference, selection);
        Assert.Equal(BuildEnvironmentRequestOutcome.Refused, hidden.Outcome); Assert.Null(hidden.Environment);
        Assert.Equal(2L, await test.Runs.Cases.Case.ScalarAsync<long>("SELECT count(*) FROM braid.build_environments"));
        await test.Runs.Cases.RoleAsync("administrator");
        var recovered = await service.PutEnvironmentAsync(test.Actor, reference, selection);
        Assert.Equal(BuildEnvironmentRequestOutcome.Replayed, recovered.Outcome);
        Assert.Equal(reference, recovered.Environment!.Environment.Reference); Assert.Equal(1, commands.Calls);
    }

    private sealed class DemotingCommands(IBuildEnvironmentCommands inner, Func<Task> demote) : IBuildEnvironmentCommands
    {
        internal int Calls;
        public async Task<BuildControlOutcome> SaveEnvironmentAsync(ActorContext actor, BuildEnvironment environment, CancellationToken token = default)
        {
            Calls++;
            var outcome = await inner.SaveEnvironmentAsync(actor, environment, token);
            await demote();
            return outcome;
        }
    }
}
