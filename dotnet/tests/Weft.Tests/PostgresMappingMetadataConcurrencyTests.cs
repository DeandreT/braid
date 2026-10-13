using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataConcurrencyTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ObservedIndependentSameVersionWritersHaveExactlyOneWinnerWithoutLostMetadata()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var original = await test.HeaderAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        await using var databaseA = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
        await using var databaseB = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
        var pidA = await PostgresMappingMetadataTestSupport.BackendAsync(databaseA.DataSource);
        var pidB = await PostgresMappingMetadataTestSupport.BackendAsync(databaseB.DataSource);
        Assert.NotEqual(pidA, pidB);
        await using var blocker = await PostgresMetadataRowBlocker.CreateAsync(test.Case, test.Scope);
        var a = new PostgresMappingMetadataWriter(databaseA).ReplaceAsync(test.Actor, test.Scope, new(test.Scope, 1), new("writer A", "complete A pair"));
        var b = new PostgresMappingMetadataWriter(databaseB).ReplaceAsync(test.Actor, test.Scope, new(test.Scope, 1), new("writer B", "complete B pair"));
        MappingMetadataWriteResult[] results;
        try
        {
            await test.WaitForBlockedAsync(blocker, [pidA, pidB], [a, b]);
            await blocker.ReleaseAsync();
            results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await blocker.ReleaseAsync(); await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10)); }
        var winner = Assert.Single(results, result => result.Outcome == MappingMetadataWriteOutcome.Updated);
        var loser = Assert.Single(results, result => result.Outcome == MappingMetadataWriteOutcome.PreconditionFailed);
        Assert.Null(loser.Metadata);
        Assert.Equal(2, winner.Metadata!.Version);
        Assert.Equal(winner.Metadata, await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope));
        Assert.Contains((winner.Metadata.Name, winner.Metadata.Description), new[] { ("writer A", "complete A pair"), ("writer B", "complete B pair") });
        var current = await test.HeaderAsync();
        PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(original, current);
        Assert.Equal(original.Head, current.Head);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
        Assert.Equal(pidA, await PostgresMappingMetadataTestSupport.BackendAsync(databaseA.DataSource));
        Assert.Equal(pidB, await PostgresMappingMetadataTestSupport.BackendAsync(databaseB.DataSource));
    }

    [PostgresFact]
    public async Task ObservedNoOpAndChangeContentionHasOnlyTheTwoAcceptedSerializations()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var original = await test.HeaderAsync();
        await using var noOpDatabase = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
        await using var changeDatabase = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
        var noOpPid = await PostgresMappingMetadataTestSupport.BackendAsync(noOpDatabase.DataSource);
        var changePid = await PostgresMappingMetadataTestSupport.BackendAsync(changeDatabase.DataSource);
        await using var blocker = await PostgresMetadataRowBlocker.CreateAsync(test.Case, test.Scope);
        var noOp = new PostgresMappingMetadataWriter(noOpDatabase).ReplaceAsync(test.Actor, test.Scope, new(test.Scope, 1), new(original.Name, original.Description));
        var change = new PostgresMappingMetadataWriter(changeDatabase).ReplaceAsync(test.Actor, test.Scope, new(test.Scope, 1), new("single real change", "preserve the pair"));
        try
        {
            await test.WaitForBlockedAsync(blocker, [noOpPid, changePid], [noOp, change]);
            await blocker.ReleaseAsync();
            await Task.WhenAll(noOp, change).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await blocker.ReleaseAsync(); await Task.WhenAll(noOp, change).WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(MappingMetadataWriteOutcome.Updated, change.Result.Outcome);
        Assert.Equal(2, change.Result.Metadata!.Version);
        if (noOp.Result.Outcome == MappingMetadataWriteOutcome.Unchanged)
            Assert.Equal(new CatalogMappingMetadata(test.Scope, original.Name, original.Description, 1), noOp.Result.Metadata);
        else
        {
            Assert.Equal(MappingMetadataWriteOutcome.PreconditionFailed, noOp.Result.Outcome);
            Assert.Null(noOp.Result.Metadata);
        }
        Assert.Equal(change.Result.Metadata, await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope));
        Assert.Equal(original.Head, (await test.HeaderAsync()).Head);
    }

    [PostgresFact]
    public async Task BothMetadataAndAppendOrdersAndObservedContentionPreserveBothDisjointAdvances()
    {
        foreach (var order in new[] { "metadata-first", "append-first", "contended" })
        {
            await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
            var original = await test.HeaderAsync();
            var immutable = await test.ImmutableSnapshotAsync();
            var ready = await test.ReadyAppendAsync();
            MappingMetadataWriteResult edit;
            IngestionStoreResult append;
            if (order == "metadata-first")
            {
                edit = await test.ReplaceAsync(1, "preserved metadata edit", order);
                append = await test.Ingestion.FinalizeAsync(test.Actor, ready.Claim);
            }
            else if (order == "append-first")
            {
                append = await test.Ingestion.FinalizeAsync(test.Actor, ready.Claim);
                edit = await test.ReplaceAsync(1, "preserved metadata edit", order);
            }
            else
            {
                await using var metadataDatabase = new PostgresMappingMetadataDatabase(test.Case.MetadataConnectionString);
                await using var ingestionDatabase = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
                var metadataPid = await PostgresMappingMetadataTestSupport.BackendAsync(metadataDatabase.DataSource);
                var ingestionPid = await PostgresMappingMetadataTestSupport.BackendAsync(ingestionDatabase.DataSource);
                await using var blocker = await PostgresMetadataRowBlocker.CreateAsync(test.Case, test.Scope);
                var metadataTask = new PostgresMappingMetadataWriter(metadataDatabase).ReplaceAsync(test.Actor, test.Scope,
                    new(test.Scope, 1), new("preserved metadata edit", order));
                var appendTask = new PostgresRevisionIngestionStore(ingestionDatabase).FinalizeAsync(test.Actor, ready.Claim);
                try
                {
                    await test.WaitForBlockedAsync(blocker, [metadataPid, ingestionPid], [metadataTask, appendTask]);
                    await blocker.ReleaseAsync();
                    await Task.WhenAll(metadataTask, appendTask).WaitAsync(TimeSpan.FromSeconds(10));
                    edit = await metadataTask;
                    append = await appendTask;
                }
                finally { await blocker.ReleaseAsync(); await Task.WhenAll(metadataTask, appendTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            Assert.Equal(MappingMetadataWriteOutcome.Updated, edit.Outcome);
            Assert.Equal(IngestionStoreOutcome.Applied, append.Outcome);
            Assert.Equal(IngestionPhase.Committed, append.Receipt!.Phase);
            Assert.Equal(2, append.Receipt.CommittedRevision!.Sequence);
            var current = await test.HeaderAsync();
            Assert.Equal(new CatalogMappingMetadata(test.Scope, "preserved metadata edit", order, 2), edit.Metadata);
            Assert.Equal(edit.Metadata, await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope));
            Assert.Equal(ready.Facts.FutureRevision, current.Head.Scope);
            Assert.Equal(2, current.Head.Sequence);
            Assert.True(current.UpdatedAtUtc >= original.UpdatedAtUtc);
            Assert.True(current.UpdatedAtUtc >= append.Receipt.CommittedRevision.CommittedAtUtc);
            PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(original, current);
            Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
            Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        }
    }

    [PostgresFact]
    public async Task MetadataChangeKeepsEqualHeadValidWhileActualHeadChangeStillConflictsWithoutUndoingMetadata()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var equal = await test.ReadyAppendAsync(new EqualHead(test.Original.Facts.FutureRevision.RevisionId));
        Assert.Equal(MappingMetadataWriteOutcome.Updated, (await test.ReplaceAsync(1, "equal head metadata", "metadata alone keeps head condition")).Outcome);
        var firstAppend = await test.Ingestion.FinalizeAsync(test.Actor, equal.Claim);
        Assert.Equal(IngestionPhase.Committed, firstAppend.Receipt!.Phase);
        Assert.Equal(2, (await test.HeaderAsync()).Version);

        var stale = await test.ReadyAppendAsync(new EqualHead(equal.Facts.FutureRevision.RevisionId));
        Assert.Equal(MappingMetadataWriteOutcome.Updated, (await test.ReplaceAsync(2, "retained through conflict", "head and metadata versions are independent")).Outcome);
        var competing = await test.CommitAppendAsync();
        var conflict = await test.Ingestion.FinalizeAsync(test.Actor, stale.Claim);
        Assert.Equal(IngestionPhase.HeadConflict, conflict.Receipt!.Phase);
        Assert.Null(conflict.Receipt.CommittedRevision);
        Assert.Equal(new CatalogMappingMetadata(test.Scope, "retained through conflict", "head and metadata versions are independent", 3),
            await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope));
        Assert.Equal(competing.Facts.FutureRevision, (await test.HeaderAsync()).Head.Scope);
        Assert.Equal(3L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }
}
