using Weft.Api.Chat.Workspace;
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
        await using var f = await JournalFixture.CreateAsync(fixture);
        // 0043 retired the writer's direct prepare: no intent without an exact approved proposal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal.PrepareAsync(f.Test.Actor, f.Intent, default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.Equal(ChatMappingEditCreateOutcome.Created, (await f.Journal.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        Assert.Null(await f.Approver.ApproveAndPrepareAsync(f.Test.Actor, f.Proposal.Conversation, f.Proposal.TurnId, f.Proposal.ProposalId,
            Sha256Digest.FromBytes("different"u8), default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.True(await f.ApproveAsync());
        Assert.True(await f.ApproveAsync());
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
        Assert.True(await f.ApproveAsync());
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
        var journal = new PostgresChatStore(restarted);
        var saved = Assert.Single(await journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal(committed.Receipt.Facts.FutureRevision.RevisionId, saved.Saved!.RevisionId);
        Assert.Equal(2, saved.Saved.Sequence);
        // No post-commit journal mutation or live-turn append is needed. Repeated read/approval preserves one version.
        Assert.Equal(ChatMappingEditStatus.Saved, (await f.Approver.ApproveAndPrepareAsync(f.Test.Actor, f.Proposal.Conversation,
            f.Proposal.TurnId, f.Proposal.ProposalId, f.Proposal.Fingerprint, default))!.Status);
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
        Assert.True(await f.ApproveAsync());
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
        Assert.True(await f.ApproveAsync());
        var turn = (await f.Journal.ReadTurnAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId))!;
        await f.Journal.FinishAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, turn.Version, ChatTurnPhase.Interrupted);
        var receipt = Assert.Single(await f.Journal.ReadAsync(f.Test.Actor, f.Intent.Conversation, f.Intent.TurnId, default));
        Assert.Equal("notSaved", receipt.Status); Assert.Null(receipt.OperationId); Assert.Null(receipt.Native);
    }

    [PostgresFact]
    public async Task Current_permission_withdrawal_prevents_ingestion_commit_but_preserves_the_accepted_intent()
    {
        await using var f = await JournalFixture.CreateAsync(fixture);
        Assert.True(await f.ApproveAsync());
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
        Assert.True(await f.ApproveAsync());
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
        Assert.True(await f.ApproveAsync());
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
        Assert.True(await f.ApproveAsync());
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

    private sealed class JournalFixture(PostgresIngestionTestSupport test, PostgresChatDatabase database, PostgresChatDatabase approval,
        ChatMappingEditProposal proposal, ChatMappingWriteIntent intent) : IAsyncDisposable
    {
        internal PostgresIngestionTestSupport Test => test;
        internal PostgresChatStore Journal { get; } = new(database);
        internal PostgresChatStore Approver { get; } = new(approval);
        internal ChatMappingEditProposal Proposal => proposal;
        // The intent the approved proposal records; its key is the proposal's request key.
        internal ChatMappingWriteIntent Intent => intent;
        internal IngestionIntent AppendIntent() => IngestionIntent.ForAppend(new(intent.Proposal.Base.OrganizationId, intent.Proposal.Base.ProjectId,
            intent.Proposal.Base.MappingId), "ferrule", null, new EqualHead(intent.Proposal.Base.RevisionId), intent.Native.Digest, intent.Native.ByteCount);

        /// <summary>The production entrance since 0043: an exact proposal, then exact approval, records the intent.</summary>
        internal async Task<bool> ApproveAsync()
        {
            var created = await Journal.CreateAsync(test.Actor, proposal, default);
            Assert.Contains(created.Outcome, new[] { ChatMappingEditCreateOutcome.Created, ChatMappingEditCreateOutcome.Existing });
            var state = await Approver.ApproveAndPrepareAsync(test.Actor, proposal.Conversation, proposal.TurnId, proposal.ProposalId, proposal.Fingerprint, default);
            return state is { Status: ChatMappingEditStatus.Approved or ChatMappingEditStatus.Pending };
        }

        internal static async Task<JournalFixture> CreateAsync(PostgresFixture fixture)
        {
            var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
            PostgresChatDatabase? database = null, approval = null;
            try
            {
                var original = await test.CommitCreateAsync();
                var scope = original.Facts.FutureRevision;
                database = new(test.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
                approval = new(fixture.ChatApproverConnectionString(test.Case), ChatDatabaseProfile.Approver);
                var journal = new PostgresChatStore(database);
                var now = new DateTimeOffset(await test.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
                var key = ChatConversationKey.ForWorkspace(scope.OrganizationId, test.Actor.PrincipalId, Guid.NewGuid());
                var conversation = ChatConversationIntent.ForWorkspace(key, null, now.AddHours(1));
                Assert.Equal(ChatStoreOutcome.Applied, await journal.CreateAsync(test.Actor, conversation));
                var id = Guid.NewGuid();
                var turn = new ChatTurnIntent(id, new(id, "Set code", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
                    ChatTurnPolicyTests.Profile(), new(64, 1048576, 3), now.AddMinutes(5));
                var started = (await journal.StartAsync(test.Actor, key, turn)).Facts!;
                var round = PostgresChatTestSupport.Round();
                var reserved = await journal.ReserveRoundAsync(test.Actor, key, id, started.Version, round);
                await journal.AppendAsync(test.Actor, key, id, reserved.Turn!.Version, round.RoundId, round.Fence,
                    new(Guid.NewGuid(), ChatEventKind.ToolStarted, "{\"v\":1,\"call\":\"call-a\",\"name\":\"propose_mapping_edit\",\"ordinal\":1}"));
                var operations = MappingEditScript.Parse(System.Text.Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
                var checkpoint = MappingDraftPersistenceTests.Checkpoint(scope);
                // Test-only trusted backend fixture admission; production mints this only inside ProposeAsync.
                var content = new MappingEditProposal(checkpoint.Artifact, operations, [])
                { VerifiedGeneration = new(test.Actor.PrincipalId, checkpoint.Evidence.Facts.Provenance) };
                var proposal = new ChatMappingEditProposal(key, id, round.RoundId, "call-a", 1, content);
                var fact = new ChatMappingWriteFact(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted,
                    scope, 1, null, null, null, null, operations.Count, operations.Digest, [new("code", "Not mapped", "Constant")], false);
                var intent = new ChatMappingWriteIntent(key, id, round.RoundId, "call-a", proposal.RequestKey, fact, checkpoint.Artifact);
                return new(test, database, approval, proposal, intent);
            }
            catch
            {
                if (database is not null) await database.DisposeAsync();
                if (approval is not null) await approval.DisposeAsync();
                await test.DisposeAsync(); throw;
            }
        }
        public async ValueTask DisposeAsync() { await database.DisposeAsync(); await approval.DisposeAsync(); await test.DisposeAsync(); }
    }
}
