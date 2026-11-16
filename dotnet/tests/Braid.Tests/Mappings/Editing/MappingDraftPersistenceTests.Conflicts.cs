using System.Text;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed partial class MappingDraftPersistenceTests
{
    [PostgresFact]
    public async Task HeadConflictCreatesNoRevisionAndOnlyExplicitCurrentHeadRebaseChangesTheBase()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var original = await test.CommitCreateAsync();
        var scope = original.Facts.FutureRevision;
        var mapping = new MappingScope(scope.OrganizationId, scope.ProjectId, scope.MappingId);
        await using var reader = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("braid_draft_reader"), MappingDraftDatabaseProfile.Reader);
        await using var writer = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("braid_draft_writer"), MappingDraftDatabaseProfile.Writer);
        var store = new PostgresMappingDraftStore(reader, writer);
        var immutableBase = (await store.SelectBaseAsync(test.Actor, mapping, scope.RevisionId, false))!;
        var checkpoint = Checkpoint(scope);
        var draft = (await store.CreateAsync(test.Actor, mapping, Guid.NewGuid(), immutableBase, checkpoint)).Draft!;
        var script = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 0, script);
        await store.PutCheckpointAsync(test.Actor, mapping, draft.Id, 1, checkpoint);

        var intervening = await test.ReserveAsync(intent: PostgresIngestionTestSupport.AppendIntent(original, body: "intervening revision"u8.ToArray()));
        var interveningClaim = await test.ClaimAsync(intervening);
        await test.ReadyAsync(intervening, interveningClaim);
        var advanced = await test.Store.FinalizeAsync(test.Actor, interveningClaim);
        Assert.Equal(IngestionPhase.Committed, advanced.Receipt!.Phase);
        var head = advanced.Receipt.Facts.FutureRevision;

        var key = Guid.NewGuid();
        await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 1, key, "edited.json");
        var intent = IngestionIntent.ForAppend(mapping, "ferrule", "edited.json", new EqualHead(scope.RevisionId), checkpoint.Artifact.Digest, checkpoint.Artifact.ByteCount);
        var reserved = await test.ReserveAsync(key, intent);
        var claim = await test.ClaimAsync(reserved);
        await test.ReadyAsync(reserved, claim);
        Assert.Equal(IngestionPhase.HeadConflict, (await test.Store.FinalizeAsync(test.Actor, claim)).Receipt!.Phase);
        await store.ReconcileSaveAsync(test.Actor, mapping, draft.Id);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        var conflicted = (await store.ReadAsync(test.Actor, mapping, draft.Id))!;
        Assert.Equal(MappingDraftState.Open, conflicted.State);
        Assert.Equal(scope, conflicted.Base.Revision);
        Assert.Equal(script.CopyUtf8(), (await store.ReadOperationsAsync(test.Actor, mapping, draft.Id))!.CopyUtf8());
        var target = (await store.SelectBaseAsync(test.Actor, mapping, head.RevisionId, true))!;
        Assert.Null(await store.SelectBaseAsync(test.Actor, mapping, scope.RevisionId, true));
        Assert.Equal(MappingDraftOutcome.PreconditionFailed, (await store.RebaseAsync(test.Actor, mapping, draft.Id, 0, target, Checkpoint(head))).Outcome);
        Assert.Equal(MappingDraftOutcome.Conflict, (await store.RebaseAsync(test.Actor, mapping, draft.Id, 1, immutableBase, checkpoint)).Outcome);
        var rebased = await store.RebaseAsync(test.Actor, mapping, draft.Id, 1, target, Checkpoint(head));
        Assert.Equal(MappingDraftOutcome.Succeeded, rebased.Outcome);
        Assert.Equal(head, rebased.Draft!.Base.Revision);
        Assert.Equal(2, rebased.Draft.Version);
        Assert.Equal(2, rebased.Draft.Checkpoint!.Version);
        Assert.Null(rebased.Draft.SavingKey);
        Assert.Equal(MappingDraftOutcome.PreconditionFailed, (await store.DiscardAsync(test.Actor, mapping, draft.Id, 1)).Outcome);
    }
}
