using System.Text;
using Braid.Application;
using Braid.Contracts.Operations;
using Braid.Contracts.Projections;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Ingestion;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL ownership/transitions with explicitly synthetic codec evidence; native fidelity is tested separately.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class MappingDraftPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task DraftPrivacyDemotionVersionCheckpointDiscardAndExpiryAreEnforcedByRestrictedFunctions()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var revision = await test.CommitCreateAsync();
        var scope = revision.Facts.FutureRevision;
        var mapping = new MappingScope(scope.OrganizationId, scope.ProjectId, scope.MappingId);
        await using var reader = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("braid_draft_reader"), MappingDraftDatabaseProfile.Reader);
        await using var writer = new PostgresMappingDraftDatabase(test.Case.RegressionConnectionString("braid_draft_writer"), MappingDraftDatabaseProfile.Writer);
        var store = new PostgresMappingDraftStore(reader, writer);
        var immutableBase = await store.SelectBaseAsync(test.Actor, mapping, scope.RevisionId, false);
        Assert.NotNull(immutableBase);
        var checkpoint = Checkpoint(scope);
        var key = Guid.NewGuid();
        var created = await store.CreateAsync(test.Actor, mapping, key, immutableBase, checkpoint);
        Assert.Equal(MappingDraftOutcome.Created, created.Outcome);
        var draft = Assert.IsType<MappingDraftSnapshot>(created.Draft);
        Assert.Equal(0, draft.Version);
        Assert.Equal(TimeSpan.FromDays(30), draft.ExpiresAtUtc - draft.ChangedAtUtc);
        Assert.Equal(MappingDraftOutcome.Replayed, (await store.CreateAsync(test.Actor, mapping, key, immutableBase, checkpoint)).Outcome);
        var exists = await store.CreateAsync(test.Actor, mapping, Guid.NewGuid(), immutableBase, checkpoint);
        Assert.Equal(MappingDraftOutcome.DraftExists, exists.Outcome);
        Assert.Equal(draft.Id, exists.Draft!.Id);
        Assert.Null(await store.ReadAsync(new(new(test.Seed.Admitted)), mapping, draft.Id));
        Assert.Null(await store.ReadOperationsAsync(new(new(test.Seed.Admitted)), mapping, draft.Id));
        Assert.Null(await store.ReadCheckpointAsync(new(new(test.Seed.Admitted)), mapping, draft.Id, 0));
        var operations = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        Assert.Equal(MappingDraftOutcome.PreconditionFailed, (await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 99, operations)).Outcome);
        var put = await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 0, operations);
        Assert.Equal(1, put.Draft!.Version);
        Assert.Null(put.Draft.Checkpoint);
        Assert.Equal(MappingDraftOutcome.Replayed, (await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 1, operations)).Outcome);
        Assert.Equal(MappingDraftOutcome.CheckpointRequired, (await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 1, Guid.NewGuid(), "edited.json")).Outcome);
        var checkedDraft = await store.PutCheckpointAsync(test.Actor, mapping, draft.Id, 1, checkpoint);
        Assert.Equal(1, checkedDraft.Draft!.Checkpoint!.Version);
        Assert.Equal(checkpoint.Artifact.CopyBytes(), (await store.ReadCheckpointAsync(test.Actor, mapping, draft.Id, 1))!.Artifact.CopyBytes());
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='runner',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", scope.OrganizationId, scope.ProjectId, test.Actor.PrincipalId.Value);
        Assert.Null(await store.ReadAsync(test.Actor, mapping, draft.Id));
        Assert.Equal(MappingDraftOutcome.NotAuthorizedOrNotFound, (await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 1, operations)).Outcome);
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", scope.OrganizationId, scope.ProjectId, test.Actor.PrincipalId.Value);
        Assert.NotNull(await store.ReadAsync(test.Actor, mapping, draft.Id));
        Assert.Equal(MappingDraftOutcome.Succeeded, (await store.DiscardAsync(test.Actor, mapping, draft.Id, 1)).Outcome);
        Assert.Null(await store.ReadOperationsAsync(test.Actor, mapping, draft.Id));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_draft_checkpoints"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_drafts WHERE operations IS NOT NULL"));
        var expiring = (await store.CreateAsync(test.Actor, mapping, Guid.NewGuid(), immutableBase, checkpoint)).Draft!;
        // Synthetic time movement is confined to this disposable database, not an API override.
        await test.Case.ExecuteAsync("UPDATE braid.mapping_drafts SET created_at=created_at-interval '31 days',changed_at=changed_at-interval '31 days',expires_at=expires_at-interval '31 days' WHERE id=$1", expiring.Id);
        Assert.Null(await store.ReadAsync(test.Actor, mapping, expiring.Id));
        Assert.Equal(1, await store.ExpireAsync(100));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_draft_checkpoints"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_drafts WHERE operations IS NOT NULL"));
    }

    [PostgresFact]
    public async Task ConditionalSaveRecoversProvenanceAfterCommitAndRejectsAnEmptyLog()
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
        Assert.Equal(MappingDraftOutcome.EmptyOperations, (await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 0, Guid.NewGuid(), "edited.json")).Outcome);
        var script = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 0, script);
        await store.PutCheckpointAsync(test.Actor, mapping, draft.Id, 1, checkpoint);
        var unrelatedKey = Guid.NewGuid();
        await test.ReserveAsync(unrelatedKey, PostgresIngestionTestSupport.AppendIntent(original));
        Assert.Equal(MappingDraftOutcome.Conflict, (await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 1, unrelatedKey, "edited.json")).Outcome);
        Assert.Equal(MappingDraftState.Open, (await store.ReadAsync(test.Actor, mapping, draft.Id))!.State);
        var racingKey = Guid.NewGuid();
        Assert.Equal(MappingDraftOutcome.Succeeded, (await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 1, racingKey, "edited.json")).Outcome);
        await test.ReserveAsync(racingKey, PostgresIngestionTestSupport.AppendIntent(original));
        await store.ReconcileSaveAsync(test.Actor, mapping, draft.Id);
        Assert.Equal(MappingDraftState.Open, (await store.ReadAsync(test.Actor, mapping, draft.Id))!.State);
        var key = Guid.NewGuid();
        Assert.Equal(MappingDraftOutcome.Succeeded, (await store.BeginSaveAsync(test.Actor, mapping, draft.Id, 1, key, "edited.json")).Outcome);
        Assert.Equal(MappingDraftOutcome.Conflict, (await store.PutOperationsAsync(test.Actor, mapping, draft.Id, 1, MappingEditScript.Empty)).Outcome);
        var bytes = checkpoint.Artifact.CopyBytes();
        var intent = IngestionIntent.ForAppend(mapping, "ferrule", "edited.json", new EqualHead(scope.RevisionId), checkpoint.Artifact.Digest, bytes.LongLength);
        var reserved = await test.ReserveAsync(key, intent);
        var claim = await test.ClaimAsync(reserved);
        await test.ReadyAsync(reserved, claim);
        var committed = await test.Store.FinalizeAsync(test.Actor, claim);
        Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_edit_provenance"));
        // A crash here loses no log: recovery observes the original immutable receipt.
        await store.ReconcileSaveAsync(test.Actor, mapping, draft.Id);
        await store.ReconcileSaveAsync(test.Actor, mapping, draft.Id);
        var saved = await store.ReadAsync(test.Actor, mapping, draft.Id);
        Assert.Equal(MappingDraftState.Saved, saved!.State);
        Assert.Equal(committed.Receipt.CommittedRevision!.Scope.RevisionId, saved.SavedRevisionId);
        Assert.Equal(script.CopyUtf8(), await test.Case.ScalarAsync<byte[]>("SELECT operations FROM braid.revision_edit_provenance"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_edit_provenance"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_draft_checkpoints"));
        Assert.Null(saved.OperationsDigest);
        Assert.Equal(key, saved.SavingKey);
    }

    internal static MappingEditCheckpoint Checkpoint(RevisionScope scope)
    {
        var artifact = NativeArtifact.Admit(scope, "synthetic native checkpoint bytes"u8, MappingEditPolicy.MaximumNativeBytes);
        var profile = FerruleReportTestData.Profile;
        var provenance = new MappingOperationProvenance(scope, artifact.Digest, "ferrule", profile.BackendRevision, profile.BackendBuild, profile.AdapterBuild, true);
        var validation = new MappingValidationResult(MappingOperationStatus.Succeeded, provenance, true, 0, 0, false, []);
        var document = FerruleReportTestData.Document(FerruleReportTestData.ProjectionKey(artifact));
        return new(artifact, IngestionValidationMapper.Map(validation).Evidence!,
            FerruleReportNormalizer.Validation(validation, artifact, new()), new ProjectionReportV1(OperationStatusV1.Succeeded,
                FerruleReportNormalizer.Provenance(provenance, artifact), document, []));
    }

}
