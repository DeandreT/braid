using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatMappingWriteJournalTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Intent_acknowledgement_is_idempotent_exact_and_precedes_ingestion()
    {
        await using var f = await JournalFixture.CreateAsync(fixture, recordProposal: false);
        // 0043 retired the writer's direct prepare: no intent without an exact approved proposal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal.PrepareAsync(f.Test.Actor, f.Intent, default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.Equal(ChatMappingEditCreateOutcome.Created, (await f.Journal.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        Assert.Equal(ChatMappingEditCreateOutcome.Existing, (await f.Journal.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        Assert.Null(await f.ApproveAsync(Sha256Digest.FromBytes("different"u8)));
        Assert.Null(await f.ApproveAsync(Sha256Digest.FromBytes("different native bytes"u8)));
        Assert.Null(await f.ApproveAsync(proposalId: Guid.NewGuid()));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        var p = f.Proposal;
        var substituted = new MappingEditProposal(NativeArtifact.Admit(p.Content.Base, "different"u8, 1024), p.Content.Operations, [])
        { VerifiedGeneration = p.Content.VerifiedGeneration };
        Assert.Equal(ChatMappingEditCreateOutcome.Refused, (await f.Journal.CreateAsync(f.Test.Actor,
            new ChatMappingEditProposal(p.Conversation, p.TurnId, p.RoundId, p.CallId, p.BaseSequence, substituted), default)).Outcome);
        // Unlike the malformed substitution above, this synthetic backend admission matches the changed bytes.
        // Keep the complete call identity so the real SQL immutable-content check must return Conflict.
        var changedArtifact = NativeArtifact.Admit(p.Content.Base, "different admitted native checkpoint bytes"u8, MappingEditPolicy.MaximumNativeBytes);
        var generation = Assert.IsType<VerifiedMappingEditGeneration>(p.Content.VerifiedGeneration);
        var changedContent = new MappingEditProposal(changedArtifact, p.Content.Operations, p.Content.Changes, p.Content.ChangesTruncated)
        { VerifiedGeneration = new(f.Test.Actor.PrincipalId, new(p.Content.Base, changedArtifact.Digest, generation.Provenance.BackendProfile, true)) };
        var changed = new ChatMappingEditProposal(p.Conversation, p.TurnId, p.RoundId, p.CallId, p.BaseSequence, changedContent);
        Assert.Equal(p.CanonicalIdentity, changed.CanonicalIdentity);
        Assert.Equal(p.ProposalId, changed.ProposalId); Assert.Equal(p.RequestKey, changed.RequestKey);
        Assert.NotEqual(p.Fingerprint, changed.Fingerprint);
        var conflict = await f.Journal.CreateAsync(f.Test.Actor, changed, default);
        Assert.Equal(ChatMappingEditCreateOutcome.Conflict, conflict.Outcome); Assert.Null(conflict.State);
        var retained = (await f.Journal.ReadAsync(f.Test.Actor, p.Conversation, p.TurnId, p.ProposalId, default))!;
        Assert.Equal(ChatMappingEditStatus.AwaitingApproval, retained.Status);
        Assert.Equal(p.Fingerprint, retained.Proposal.Fingerprint);
        Assert.Equal(p.Content.Checkpoint.CopyBytes(), retained.Proposal.Content.Checkpoint.CopyBytes());
        Assert.Equal(p.Content.Checkpoint.CopyBytes(), await f.Test.Case.ScalarAsync<byte[]>(
            "SELECT native FROM weft.chat_edit_content WHERE proposal_id=$1", p.ProposalId));
        Assert.Equal(ChatMappingEditProposalStorage.Encode(p), await f.Test.Case.ScalarAsync<byte[]>(
            "SELECT encoding FROM weft.chat_edit_content WHERE proposal_id=$1", p.ProposalId));
        Assert.Equal(p.Fingerprint.Value, await f.Test.Case.ScalarAsync<string>(
            "SELECT fingerprint FROM weft.chat_edit_proposals WHERE proposal_id=$1", p.ProposalId));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_proposals"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_content"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var replay = (await f.ApproveAsync())!;
        Assert.Equal(ChatMappingEditStatus.Approved, replay.Status);
        Assert.Equal(f.Intent.Key, replay.Receipt!.Key);
        Assert.Equal(f.Intent.Native.CopyBytes(), replay.Receipt.Native!.CopyBytes());
        Assert.Equal(f.Intent.Proposal.EditDigest, replay.Receipt.Proposal.EditDigest);
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions WHERE operation_kind='append'"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents WHERE request_key=$1", f.Intent.Key));
        var pending = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal("pending", pending.Status); Assert.Null(pending.OperationId); Assert.Null(pending.Saved);
        Assert.Empty(await f.Journal.ReadAsync(new(new(f.Test.Seed.Admitted)), f.Intent.Conversation, f.Intent.TurnId, default));
        // The no-content audit cannot be changed, deleted, or read by a chat credential.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Test.Case.ExecuteAsync("UPDATE weft.chat_mapping_write_intents SET operation_count=2"));
    }

    [PostgresFact]
    public async Task Pending_ingestion_commit_survives_cancelled_turn_lost_event_ack_and_process_restart()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved);
        await f.Test.ReadyAsync(reserved, claim);
        var pending = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal("pending", pending.Status); Assert.NotNull(pending.Native); Assert.Null(pending.Saved);
        // Cancellation ends normal event publication. The accepted ingestion may still commit or its acknowledgement may be lost.
        var turn = (await f.Journal.ReadTurnAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId))!;
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Journal.FinishAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
        var committed = await f.Test.Store.FinalizeAsync(f.Test.Actor, claim);
        Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
        Assert.Equal(committed.Receipt.Facts.FutureRevision.RevisionId, await f.Test.Case.ScalarAsync<Guid>("SELECT revision_id FROM weft.chat_mapping_write_commits"));
        await using var restarted = new PostgresChatDatabase(f.Test.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        await using var restartedApprover = new PostgresChatDatabase(fixture.ChatApproverConnectionString(f.Test.Case), ChatDatabaseProfile.Approver);
        var journal = new PostgresChatStore(restarted);
        var saved = Assert.Single(await journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal(committed.Receipt.Facts.FutureRevision.RevisionId, saved.Saved!.RevisionId);
        Assert.Equal(2, saved.Saved.Sequence);
        // No post-commit journal mutation or live-turn append is needed. Repeated read/approval preserves one version.
        var p = f.Proposal;
        var replay = (await new PostgresChatStore(restartedApprover).ApproveAndPrepareAsync(f.Test.Actor,
            p.Conversation, p.TurnId, p.ProposalId, p.Fingerprint, default))!;
        Assert.Equal(ChatMappingEditStatus.Saved, replay.Status);
        Assert.Equal(saved.Key, replay.Receipt!.Key);
        Assert.Equal(saved.Saved.RevisionId, replay.Receipt.Saved!.RevisionId);
        Assert.Equal(saved.Saved.Sequence, replay.Receipt.Saved.Sequence);
        Assert.Equal(ChatMetadataCodec.MappingWrite(saved.Saved), ChatMetadataCodec.MappingWrite((await journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default)).Single().Saved!));
        Assert.Equal(2L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_events WHERE kind=15"));
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        Assert.Empty(await journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
    }

    [PostgresFact]
    public async Task A_conflicting_head_never_becomes_a_saved_receipt()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved); await f.Test.ReadyAsync(reserved, claim);
        var other = await f.Test.ReserveAsync(Guid.NewGuid(), f.AppendIntent());
        var otherClaim = await f.Test.ClaimAsync(other); await f.Test.ReadyAsync(other, otherClaim);
        Assert.Equal(IngestionPhase.Committed, (await f.Test.Store.FinalizeAsync(f.Test.Actor, otherClaim)).Receipt!.Phase);
        Assert.Equal(IngestionPhase.HeadConflict, (await f.Test.Store.FinalizeAsync(f.Test.Actor, claim)).Receipt!.Phase);
        var receipt = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal("notSaved", receipt.Status); Assert.Null(receipt.Saved); Assert.Null(receipt.Native);
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));
    }

    [PostgresFact]
    public async Task A_terminal_turn_without_any_reservation_is_not_restarted_by_recovery()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var turn = (await f.Journal.ReadTurnAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId))!;
        await f.Journal.FinishAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, turn.Version, ChatTurnPhase.Interrupted);
        var receipt = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal("notSaved", receipt.Status); Assert.Null(receipt.OperationId); Assert.Null(receipt.Native);
        var ingestion = new NoReservationIngestion();
        var recovery = new ChatMappingWriteRecovery(f.Journal, ingestion, new PostgresCatalogReader(f.Catalog));
        var recovered = Assert.Single(await recovery.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, true, default));
        Assert.Equal(receipt.Key, recovered.Key); Assert.Equal(receipt.Status, recovered.Status);
        Assert.Null(recovered.OperationId); Assert.Null(recovered.Saved); Assert.Null(recovered.Native);
        Assert.Equal(0, ingestion.Calls);
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.revision_ingestions WHERE operation_kind='append'"));
    }

    [PostgresFact]
    public async Task Current_permission_withdrawal_prevents_ingestion_commit_but_preserves_the_accepted_intent()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved); await f.Test.ReadyAsync(reserved, claim);
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        Assert.Equal(IngestionStoreOutcome.NotAuthorizedOrNotFound, (await f.Test.Store.FinalizeAsync(f.Test.Actor, claim)).Outcome);
        var receipt = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Null(receipt.Saved); Assert.Equal("pending", receipt.Status);
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
    }

    [PostgresFact]
    public async Task An_expired_turn_can_read_a_later_commit_and_erasure_removes_only_private_replay_content()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved); await f.Test.ReadyAsync(reserved, claim);
        // Explicit synthetic clock movement inside an owned, disposable fixture only.
        await f.Test.Case.ExecuteAsync("""
            ALTER TABLE weft.chat_turns DISABLE TRIGGER chat_child_guard;
            UPDATE weft.chat_turns SET deadline=created_at+interval '1 millisecond';
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE weft.chat_turns ENABLE TRIGGER chat_child_guard;
            """);
        Assert.Equal(IngestionPhase.Committed, (await f.Test.Store.FinalizeAsync(f.Test.Actor, claim)).Receipt!.Phase);
        Assert.NotNull((await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default)).Single().Saved);
        await using var lifecycleDb = new PostgresChatLifecycleDatabase(f.Test.Case.ChatLifecycleConnectionString);
        var lifecycle = new PostgresChatLifecycleStore(lifecycleDb);
        var observed = (await lifecycle.ObserveAsync(f.Intent.Conversation, f.Intent.TurnId))!;
        await lifecycle.CloseAsync(f.Intent.Conversation, f.Intent.TurnId, observed.Version, ChatTurnPhase.Interrupted);
        await f.Test.Case.ExecuteAsync("""
            ALTER TABLE weft.chat_conversations DISABLE TRIGGER chat_conversation_guard;
            UPDATE weft.chat_conversations SET retain_until=created_at+interval '1 millisecond';
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE weft.chat_conversations ENABLE TRIGGER chat_conversation_guard;
            """);
        var now = new DateTimeOffset(await f.Test.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        Assert.Equal(ChatStoreOutcome.Applied, await lifecycle.EraseExpiredAsync(f.Intent.Conversation, new("fixture-erasure", 1, now.AddDays(1))));
        Assert.Empty(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_content"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM weft.chat_mapping_write_intents i JOIN weft.revision_ingestions x ON x.organization_id=i.organization_id
                AND x.project_id=i.project_id AND x.actor_principal_id=i.creator_id AND x.idempotency_key=i.request_key
                AND x.target_mapping_id=i.mapping_id AND x.native_digest=i.native_digest AND x.expected_head_revision_id=i.base_revision_id
            WHERE x.phase='committed'
            """));
    }

    [PostgresFact]
    public async Task A_saved_receipt_uses_its_self_contained_commit_fact_without_the_original_ingestion_lookup()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved); await f.Test.ReadyAsync(reserved, claim);
        var committed = (await f.Test.Store.FinalizeAsync(f.Test.Actor, claim)).Receipt!;
        // Synthetic loss of the own-receipt lookup in this disposable fixture. This does not qualify another
        // branch's permanent-deletion procedure; it proves the chat receipt no longer depends on that lookup.
        await f.Test.Case.ExecuteAsync("""
            ALTER TABLE weft.revision_ingestions DISABLE TRIGGER revision_ingestions_transition_guard;
            UPDATE weft.revision_ingestions SET idempotency_key=gen_random_uuid() WHERE operation_kind='append';
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE weft.revision_ingestions ENABLE TRIGGER revision_ingestions_transition_guard;
            """);
        var saved = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal(committed.Facts.FutureRevision.RevisionId, saved.Saved!.RevisionId);
        Assert.Equal(2, saved.Saved.Sequence);
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conrelid='weft.chat_mapping_write_commits'::regclass AND contype='f'"));
        foreach (var sql in new[] { "UPDATE weft.chat_mapping_write_commits SET revision_sequence=99", "DELETE FROM weft.chat_mapping_write_commits" })
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Test.Case.ExecuteAsync(sql));
    }

    [PostgresFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Independent_receipt_HTTP_reads_check_creator_project_and_antiforgery_without_a_live_turn()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        var reserved = await f.Test.ReserveAsync(f.Intent.Key, f.AppendIntent());
        var claim = await f.Test.ClaimAsync(reserved); await f.Test.ReadyAsync(reserved, claim);
        await f.Test.Store.FinalizeAsync(f.Test.Actor, claim);
        var turn = (await f.Journal.ReadTurnAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId))!;
        await f.Journal.FinishAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled);
        await using var provider = new AiHttpFixture((_, _) => throw new InvalidOperationException("Recovery must not invoke a model"));
        await using var api = await ChatHttpTestSupport.StartAsync(f.Test.Case, f.Test.Actor, f.Intent.Proposal.Base, ChatHttpTestSupport.Options(provider));
        var path = api.WorkspaceTurnPath(f.Intent.Conversation.ConversationId, f.Intent.TurnId) + "/mapping-writes";
        using (var response = await api.Client.GetAsync(path))
        {
            var receipt = (await CatalogHttpTestSupport.JsonAsync(response)).GetProperty("receipts")[0];
            Assert.Equal("saved", receipt.GetProperty("status").GetString());
            Assert.Equal("mapping.write", receipt.GetProperty("write").GetProperty("type").GetString());
            Assert.Equal(f.Intent.Key.ToString("D"), receipt.GetProperty("requestId").GetString());
        }
        using (var refused = await api.PostAsync(path + "/recover", null, null)) Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
        var token = await api.TokenAsync();
        using (var recover = await api.PostAsync(path + "/recover", null, token))
            Assert.Equal("saved", (await CatalogHttpTestSupport.JsonAsync(recover)).GetProperty("receipts")[0].GetProperty("status").GetString());
        await using (var other = await ChatHttpTestSupport.StartAsync(f.Test.Case, new(new(f.Test.Seed.Admitted)), f.Intent.Proposal.Base, ChatHttpTestSupport.Options(provider)))
        using (var privateReceipt = await other.Client.GetAsync(path)) Assert.Equal(System.Net.HttpStatusCode.NotFound, privateReceipt.StatusCode);
        Assert.Equal(0, provider.RequestCount);
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        using (var hidden = await api.Client.GetAsync(path))
            Assert.Empty((await CatalogHttpTestSupport.JsonAsync(hidden)).GetProperty("receipts").EnumerateArray());
    }

    private sealed class JournalFixture(PostgresChatEditProposalTests.ProposalFixture proposal,
        PostgresCatalogDatabase catalog, ChatMappingWriteIntent intent) : IAsyncDisposable
    {
        internal PostgresIngestionTestSupport Test => proposal.Test;
        internal PostgresChatStore Journal => proposal.Writer;
        internal PostgresCatalogDatabase Catalog => catalog;
        internal ChatMappingEditProposal Proposal => proposal.Proposal;
        // The intent the approved proposal records; its key is the proposal's request key.
        internal ChatMappingWriteIntent Intent => intent;
        /// <summary>The production entrance since 0043: exact approval of the fixture's recorded proposal creates the intent.</summary>
        internal Task<ChatMappingEditState?> ApproveAsync(Sha256Digest? fingerprint = null, Guid? proposalId = null) =>
            proposal.Approver.ApproveAndPrepareAsync(Test.Actor, intent.Conversation, intent.TurnId,
                proposalId ?? proposal.Proposal.ProposalId, fingerprint ?? proposal.Proposal.Fingerprint, default);
        internal IngestionIntent AppendIntent() => IngestionIntent.ForAppend(new(intent.Proposal.Base.OrganizationId, intent.Proposal.Base.ProjectId,
            intent.Proposal.Base.MappingId), "ferrule", null, new EqualHead(intent.Proposal.Base.RevisionId), intent.Native.Digest, intent.Native.ByteCount);

        internal static async Task<JournalFixture> CreateAsync(PostgresFixture fixture, bool recordProposal = true)
        {
            var proposal = await PostgresChatEditProposalTests.ProposalFixture.CreateAsync(fixture);
            PostgresCatalogDatabase? catalog = null;
            try
            {
                var p = proposal.Proposal;
                var content = p.Content;
                if (recordProposal)
                    Assert.Equal(ChatMappingEditCreateOutcome.Created, (await proposal.Writer.CreateAsync(proposal.Test.Actor, p, default)).Outcome);
                var fact = new ChatMappingWriteFact(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted,
                    content.Base, p.BaseSequence, null, null, null, null, content.Operations.Count, content.Operations.Digest,
                    content.Changes.Select(change => new ChatMappingChange(change.Field, change.Before, change.After)).ToArray(), content.ChangesTruncated);
                var intent = new ChatMappingWriteIntent(p.Conversation, p.TurnId, p.RoundId, p.CallId, p.RequestKey, fact, content.Checkpoint);
                catalog = new(proposal.Test.Case.CatalogConnectionString);
                return new(proposal, catalog, intent);
            }
            catch { if (catalog is not null) await catalog.DisposeAsync(); await proposal.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await catalog.DisposeAsync(); await proposal.DisposeAsync(); }
    }

    private sealed class NoReservationIngestion : IRevisionIngestionService
    {
        internal int Calls;
        private Task<RevisionIngestionResult> Unexpected() { Calls++; throw new InvalidOperationException("Recovery must not start ingestion without a reservation."); }
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Unexpected();
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope mapping, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => Unexpected();
        public Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => Unexpected();
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
