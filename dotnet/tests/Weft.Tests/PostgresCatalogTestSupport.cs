using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual catalog PostgreSQL reads; committed fixtures use explicitly synthetic C1 evidence, never a native worker.</summary>
internal sealed class PostgresCatalogTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal ActorContext Actor { get; }
    internal ProjectScope Project { get; }
    internal PostgresCatalogDatabase Database { get; }
    internal PostgresCatalogReader Reader { get; }
    internal PostgresIngestionDatabase IngestionDatabase { get; }
    internal PostgresRevisionIngestionStore Store { get; }
    private readonly bool historicalSeed;

    private PostgresCatalogTestSupport(PostgresCase test, PostgresSeedData seed, bool historicalSeed)
    {
        Case = test;
        Seed = seed;
        Actor = new(new PrincipalId(seed.Alice));
        Project = new(seed.OrganizationA, seed.ProjectA);
        Database = new(test.CatalogConnectionString);
        Reader = new(Database);
        IngestionDatabase = new(test.IngestionConnectionString);
        Store = new(IngestionDatabase);
        this.historicalSeed = historicalSeed;
    }

    internal static async Task<PostgresCatalogTestSupport> CreateAsync(PostgresFixture fixture, bool installCatalog = true)
    {
        var test = await fixture.CreateCaseAsync(migrate: installCatalog);
        try
        {
            if (!installCatalog)
            {
                // Exercise the real previously qualified two-migration database before
                // adding the read profile; neither historical script is rewritten.
                await using var old = new MigrationRunner(test.MigrationConnectionString,
                    MigrationRunner.EmbeddedMigrations.Take(2).ToArray());
                await old.MigrateAsync();
            }
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
            return new(test, seed, historicalSeed: !installCatalog);
        }
        catch { await test.DisposeAsync(); throw; }
    }

    internal async Task<IngestionReceipt> CommitCreateAsync(string name = "Synthetic catalog mapping",
        string description = "Synthetic C1 admission evidence only", bool valid = true, string? filename = null,
        ProjectScope? project = null, ActorContext? actor = null)
    {
        if (historicalSeed)
            return await HistoricalIngestionSeed.CommitCreateAsync(Case, actor ?? Actor, project ?? Project,
                name, description, valid, filename);
        var intent = PostgresIngestionTestSupport.CreateIntent(name, description, filename);
        return await CommitAsync(actor ?? Actor, project ?? Project, intent, valid);
    }

    internal Task<IngestionReceipt> CommitAppendAsync(IngestionReceipt mapping, bool valid = true, string? filename = null)
    {
        if (historicalSeed)
            return HistoricalIngestionSeed.CommitAppendAsync(Case, mapping, valid, filename);
        var facts = mapping.Facts;
        var body = PostgresIngestionTestSupport.SyntheticBody;
        var intent = IngestionIntent.ForAppend(Target(mapping), facts.BackendProfile.BackendId, filename,
            new AnyHead(), Sha256Digest.FromBytes(body), body.LongLength);
        return CommitAsync(new(facts.OriginalActor), facts.Project, intent, valid);
    }

    private async Task<IngestionReceipt> CommitAsync(ActorContext actor, ProjectScope project, IngestionIntent intent, bool valid)
    {
        var reserved = await Store.ReserveAsync(actor, project, Guid.NewGuid(), intent,
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
        var facts = Assert.IsType<IngestionReceipt>(reserved.Receipt).Facts;
        var claimed = await Store.ClaimAsync(actor, project, facts.OperationId, Guid.NewGuid());
        Assert.Equal(IngestionStoreOutcome.Claimed, claimed.Outcome);
        var claim = Assert.IsType<IngestionClaim>(claimed.Claim);
        Assert.Equal(IngestionStoreOutcome.Applied, (await Store.MarkStagedAsync(actor, claim, facts.NativeObject)).Outcome);
        var completed = await Store.CompleteAttemptAsync(actor, claim,
            PostgresIngestionTestSupport.SyntheticCompletion(facts, valid));
        Assert.Equal(IngestionPhase.Ready, completed.Receipt!.Phase);
        var finalized = await Store.FinalizeAsync(actor, claim);
        Assert.Equal(IngestionStoreOutcome.Applied, finalized.Outcome);
        var receipt = Assert.IsType<IngestionReceipt>(finalized.Receipt);
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        return receipt;
    }

    internal static MappingScope Target(IngestionReceipt receipt) =>
        new(receipt.Facts.FutureRevision.OrganizationId, receipt.Facts.FutureRevision.ProjectId, receipt.Facts.FutureRevision.MappingId);

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await IngestionDatabase.DisposeAsync();
        await Case.DisposeAsync();
    }
}
