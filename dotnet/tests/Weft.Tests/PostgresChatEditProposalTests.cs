using System.Text;
using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Requires the integrated 0041/0042/0043 inventory and actual PostgreSQL. Never substitute offline assertions.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatEditProposalTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Lost_delivery_is_discoverable_and_creation_never_prepares_or_approves()
    {
        await using var f = await ProposalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditCreateOutcome.Created, (await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        Assert.Equal(ChatMappingEditCreateOutcome.Existing, (await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        await using var restarted = new PostgresChatDatabase(f.Test.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var list = await new PostgresChatStore(restarted).ListAsync(f.Test.Actor, f.Proposal.Conversation, f.Proposal.TurnId, default);
        var state = Assert.Single(list);
        Assert.Equal(ChatMappingEditStatus.AwaitingApproval, state.Status);
        Assert.Equal(f.Proposal.Fingerprint, state.Proposal.Fingerprint);
        Assert.Null(state.Receipt);
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.Empty(await f.Writer.ListAsync(new(new(f.Test.Seed.Admitted)), f.Proposal.Conversation, f.Proposal.TurnId, default));
        Assert.Empty(await f.Writer.ListAsync(f.Test.Actor, f.Proposal.Conversation, Guid.NewGuid(), default));
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        Assert.Empty(await f.Writer.ListAsync(f.Test.Actor, f.Proposal.Conversation, f.Proposal.TurnId, default));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_proposals"));
    }

    [PostgresFact]
    public async Task Terminal_turn_exact_approval_is_atomic_idempotent_and_receipt_first()
    {
        await using var f = await ProposalFixture.CreateAsync(fixture);
        Assert.Equal(ChatMappingEditCreateOutcome.Created, (await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default)).Outcome);
        var p = f.Proposal;
        var turn = (await f.Writer.ReadTurnAsync(f.Test.Actor, p.Conversation, p.TurnId))!;
        await f.Writer.FinishAsync(f.Test.Actor, p.Conversation, p.TurnId, turn.Version, ChatTurnPhase.Cancelled);
        Assert.Null(await f.Approver.ApproveAndPrepareAsync(f.Test.Actor, p.Conversation, p.TurnId, p.ProposalId, Sha256Digest.FromBytes("wrong"u8), default));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_intents"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_content"));
        var scope = p.Content.Base;
        var reservation = await f.Test.ReserveAsync(p.RequestKey, IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId),
            "ferrule", null, new EqualHead(scope.RevisionId), p.Content.Checkpoint.Digest, p.Content.Checkpoint.ByteCount));
        var claim = await f.Test.ClaimAsync(reservation); await f.Test.ReadyAsync(reservation, claim);
        var committed = await f.Test.Store.FinalizeAsync(f.Test.Actor, claim);
        Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
        // Head has changed as part of this append; receipt replay must still win, including after publish demotion.
        await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
        var saved = (await f.ApproveAsync())!;
        Assert.Equal(ChatMappingEditStatus.Saved, saved.Status);
        Assert.Equal(committed.Receipt.Facts.FutureRevision.RevisionId, saved.Receipt!.RevisionId);
    }

    [PostgresFact]
    public async Task Immutable_content_and_audits_have_no_direct_role_access_or_model_approval_grant()
    {
        await using var f = await ProposalFixture.CreateAsync(fixture);
        await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default);
        foreach (var table in new[] { "chat_edit_proposals", "chat_edit_content", "chat_edit_approvals" })
        {
            Assert.False(await f.Test.Case.ScalarAsync<bool>($"SELECT has_table_privilege('weft_chat_writer','weft.{table}','SELECT,INSERT,UPDATE,DELETE,TRUNCATE')"));
            Assert.False(await f.Test.Case.ScalarAsync<bool>($"SELECT has_table_privilege('weft_chat_approver','weft.{table}','SELECT,INSERT,UPDATE,DELETE,TRUNCATE')"));
        }
        Assert.False(await f.Test.Case.ScalarAsync<bool>("SELECT has_function_privilege('weft_chat_writer','weft.chat_edit_approve(uuid,uuid,uuid,uuid,uuid,text)','EXECUTE')"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Test.Case.ExecuteAsync("UPDATE weft.chat_edit_content SET encoding='changed'::bytea"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => f.Test.Case.ExecuteAsync("DELETE FROM weft.chat_edit_proposals"));
    }

    [PostgresFact]
    public async Task Concurrent_commit_after_pending_observation_wins_refusals_only_with_current_read_authority()
    {
        foreach (var authority in new[] { "publisher", "viewer", "revoked" })
        {
            await using var f = await ProposalFixture.CreateAsync(fixture);
            var p = f.Proposal; var scope = p.Content.Base;
            await f.Writer.CreateAsync(f.Test.Actor, p, default);
            Assert.Equal(ChatMappingEditStatus.Approved, (await f.ApproveAsync())!.Status);
            var reservation = await f.Test.ReserveAsync(p.RequestKey, IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId),
                "ferrule", null, new EqualHead(scope.RevisionId), p.Content.Checkpoint.Digest, p.Content.Checkpoint.ByteCount));
            var claim = await f.Test.ClaimAsync(reservation); await f.Test.ReadyAsync(reservation, claim);

            // Disposable-fixture scheduling hook only: pause the real approval body AFTER
            // it has observed Pending, before the publish/head statements. No timing sleeps,
            // synthetic receipts, or changes to finalization/audit logic. Raw role invocation
            // intentionally bypasses application schema admission for this instrumented body.
            const long gateKey = 437190216;
            const string signature = "weft.chat_edit_approve(uuid,uuid,uuid,uuid,uuid,text)";
            var original = await f.Test.Case.ScalarAsync<string>($"SELECT pg_get_functiondef('{signature}'::regprocedure)");
            const string marker = "    IF NOT EXISTS(SELECT 1 FROM weft.principals a";
            Assert.Equal(2, original.Split(marker, StringSplitOptions.None).Length);
            var instrumented = original.Replace(marker, $"""
                    IF observed::jsonb->>'status' IS DISTINCT FROM 'Pending' THEN
                        RAISE EXCEPTION 'Fixture expected the initial pending observation';
                    END IF;
                    PERFORM pg_catalog.pg_advisory_xact_lock({gateKey});
                """ + "\n" + marker, StringComparison.Ordinal);

            await using var gate = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(f.Test.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
            await gate.OpenAsync();
            await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({gateKey})", gate)) await hold.ExecuteNonQueryAsync();
            var application = "weft-proposal-receipt-race-" + authority;
            await using var caller = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ChatApproverConnectionString(f.Test.Case))
                { Pooling = false, ApplicationName = application }.ConnectionString);
            await caller.OpenAsync();
            await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,false),set_config('weft.chat_principal_id',$2,false)", caller))
            {
                bind.Parameters.AddWithValue(scope.OrganizationId.ToString()); bind.Parameters.AddWithValue(f.Test.Actor.PrincipalId.Value.ToString());
                await bind.ExecuteNonQueryAsync();
            }
            await using var approve = new NpgsqlCommand("SELECT weft.chat_edit_approve($1,$2,$3,$4,$5,$6)", caller) { CommandTimeout = 30 };
            approve.Parameters.AddWithValue(scope.OrganizationId); approve.Parameters.AddWithValue(f.Test.Actor.PrincipalId.Value);
            approve.Parameters.AddWithValue(p.Conversation.ConversationId); approve.Parameters.AddWithValue(p.TurnId);
            approve.Parameters.AddWithValue(p.ProposalId); approve.Parameters.AddWithValue(p.Fingerprint.Value);
            Task<object?>? retry = null;
            try
            {
                await f.Test.Case.ExecuteAsync(instrumented);
                await using var blocked = await PostgresServerSessionTestSupport.PrepareBlockedAsync(gate, application, gate.ProcessID);
                retry = approve.ExecuteScalarAsync();
                await PostgresServerSessionTestSupport.WaitBlockedAsync(blocked, retry);
                // Finalization uses a different connection and commits while approval holds
                // its conversation/proposal locks. A timeout here also detects a lock cycle.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var committed = await f.Test.Store.FinalizeAsync(f.Test.Actor, claim, deadline.Token);
                Assert.Equal(IngestionPhase.Committed, committed.Receipt!.Phase);
                if (authority == "viewer")
                    await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
                else if (authority == "revoked")
                    await f.Test.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", f.Test.Actor.PrincipalId.Value);
                await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({gateKey})", gate)) Assert.True(await release.ExecuteScalarAsync() is true);
                var result = await retry.WaitAsync(TimeSpan.FromSeconds(10));
                if (authority == "revoked") Assert.IsType<DBNull>(result);
                else
                {
                    using var json = JsonDocument.Parse(Assert.IsType<string>(result));
                    Assert.Equal("Saved", json.RootElement.GetProperty("status").GetString());
                    Assert.Equal(committed.Receipt.Facts.FutureRevision.RevisionId, json.RootElement.GetProperty("revision").GetGuid());
                }
            }
            finally
            {
                await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({gateKey})", gate)) await release.ExecuteScalarAsync();
                if (retry is not null) await PostgresServerSessionTestSupport.SettleAsync(retry);
                await f.Test.Case.ExecuteAsync(original);
            }
        }
    }

    [PostgresFact]
    public async Task Publisher_cannot_commit_an_unapproved_proposal_key()
    {
        await using var f = await ProposalFixture.CreateAsync(fixture);
        await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default);
        var p = f.Proposal; var scope = p.Content.Base;
        var reservation = await f.Test.ReserveAsync(p.RequestKey, IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId),
            "ferrule", null, new EqualHead(scope.RevisionId), p.Content.Checkpoint.Digest, p.Content.Checkpoint.ByteCount));
        var claim = await f.Test.ClaimAsync(reservation); await f.Test.ReadyAsync(reservation, claim);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Test.Store.FinalizeAsync(f.Test.Actor, claim));
        Assert.Contains("23514", error.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));
    }

    [PostgresFact]
    public async Task Expiry_hides_discovery_and_erasure_deletes_private_content_but_not_provenance()
    {
        await using var f = await ProposalFixture.CreateAsync(fixture);
        await f.Writer.CreateAsync(f.Test.Actor, f.Proposal, default);
        var p = f.Proposal;
        var turn = (await f.Writer.ReadTurnAsync(f.Test.Actor, p.Conversation, p.TurnId))!;
        await f.Writer.FinishAsync(f.Test.Actor, p.Conversation, p.TurnId, turn.Version, ChatTurnPhase.Cancelled);
        await f.Test.Case.ExecuteAsync("""
            ALTER TABLE weft.chat_conversations DISABLE TRIGGER chat_conversation_guard;
            UPDATE weft.chat_conversations SET retain_until=created_at+interval '1 millisecond';
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE weft.chat_conversations ENABLE TRIGGER chat_conversation_guard;
            """);
        Assert.Empty(await f.Writer.ListAsync(f.Test.Actor, p.Conversation, p.TurnId, default));
        Assert.Null(await f.ApproveAsync());
        await using var database = new PostgresChatLifecycleDatabase(f.Test.Case.ChatLifecycleConnectionString);
        var now = new DateTimeOffset(await f.Test.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        Assert.Equal(ChatStoreOutcome.Applied, await new PostgresChatLifecycleStore(database).EraseExpiredAsync(p.Conversation, new("fixture-erasure", 1, now.AddDays(1))));
        Assert.Equal(0L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_content"));
        Assert.Equal(1L, await f.Test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_proposals WHERE generation_provenance::jsonb->>'Confirmed'='true'"));
    }

    internal sealed class ProposalFixture(PostgresIngestionTestSupport test, PostgresChatDatabase writerDb, PostgresChatDatabase approvalDb,
        ChatMappingEditProposal proposal) : IAsyncDisposable
    {
        internal PostgresIngestionTestSupport Test => test;
        internal PostgresChatStore Writer { get; } = new(writerDb);
        internal PostgresChatStore Approver { get; } = new(approvalDb);
        internal ChatMappingEditProposal Proposal => proposal;
        internal Task<ChatMappingEditState?> ApproveAsync() => Approver.ApproveAndPrepareAsync(test.Actor, proposal.Conversation,
            proposal.TurnId, proposal.ProposalId, proposal.Fingerprint, default);

        internal static async Task<ProposalFixture> CreateAsync(PostgresFixture fixture)
        {
            var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
            PostgresChatDatabase? writer = null, approval = null;
            try
            {
                var scope = (await test.CommitCreateAsync()).Facts.FutureRevision;
                writer = new(test.Case.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
                approval = new(fixture.ChatApproverConnectionString(test.Case), ChatDatabaseProfile.Approver);
                var store = new PostgresChatStore(writer);
                var now = new DateTimeOffset(await test.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
                var key = ChatConversationKey.ForWorkspace(scope.OrganizationId, test.Actor.PrincipalId, Guid.NewGuid());
                Assert.Equal(ChatStoreOutcome.Applied, await store.CreateAsync(test.Actor, ChatConversationIntent.ForWorkspace(key, null, now.AddHours(1))));
                var id = Guid.NewGuid();
                var turn = new ChatTurnIntent(id, new(id, "Set code", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
                    ChatTurnPolicyTests.Profile(), new(64, 1048576, 3), now.AddMinutes(5));
                var started = (await store.StartAsync(test.Actor, key, turn)).Facts!;
                var round = PostgresChatTestSupport.Round();
                var reserved = await store.ReserveRoundAsync(test.Actor, key, id, started.Version, round);
                await store.AppendAsync(test.Actor, key, id, reserved.Turn!.Version, round.RoundId, round.Fence,
                    new(Guid.NewGuid(), ChatEventKind.ToolStarted, "{\"v\":1,\"call\":\"call-a\",\"name\":\"propose_mapping_edit\",\"ordinal\":1}"));
                var checkpoint = MappingDraftPersistenceTests.Checkpoint(scope);
                // Test-only trusted backend fixture admission; production mints this only inside ProposeAsync.
                var content = new MappingEditProposal(checkpoint.Artifact, MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind)),
                    [new("code", "Not mapped", "Constant")])
                { VerifiedGeneration = new(test.Actor.PrincipalId, checkpoint.Evidence.Facts.Provenance) };
                return new(test, writer, approval, new(key, id, round.RoundId, "call-a", 1, content));
            }
            catch
            {
                if (writer is not null) await writer.DisposeAsync();
                if (approval is not null) await approval.DisposeAsync();
                await test.DisposeAsync(); throw;
            }
        }
        public async ValueTask DisposeAsync() { await writerDb.DisposeAsync(); await approvalDb.DisposeAsync(); await test.DisposeAsync(); }
    }
}
