using System.Text;
using Npgsql;
using NpgsqlTypes;
using Weft.Api.Chat.Workspace;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Historical writer SQL runs only before the upgrade, against real unmodified migrations.
// The v40 ingestion helper uses guarded synthetic admission, not a native worker or an altered schema.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatApprovalUpgradeTests(PostgresFixture fixture)
{
    [PostgresFact]
    public Task Populated_version40_upgrade_preserves_committed_receipts_and_fences_pending_legacy_saves() => UpgradeAsync(false);

    [PostgresFact]
    public Task Populated_version40_upgrade_fences_legacy_keys_even_with_a_different_mapping_binding() => UpgradeAsync(true);

    private async Task UpgradeAsync(bool mismatchedBinding)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await MigrateThroughAsync(test, 2);
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var project = new ProjectScope(seed.OrganizationA, seed.ProjectA);
        var original = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, project, name: "Legacy committed mapping");
        var other = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, project, name: "Legacy pending mapping");
        await MigrateThroughAsync(test, 40);
        Assert.Equal(Enumerable.Range(1, 40), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.True(await test.ScalarAsync<bool>("SELECT to_regclass('weft.chat_edit_proposals') IS NULL"));

        var savedIntent = await PrepareHistoricalAsync(test, actor, original.Facts.FutureRevision);
        var saved = await HistoricalIngestionSeed.SeedChatAppendAsync(test, actor, project, AppendIntent(savedIntent), savedIntent.Key, commit: true);
        Assert.Equal(IngestionPhase.Committed, saved.Receipt.Phase);
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));
        var pendingIntent = await PrepareHistoricalAsync(test, actor, other.Facts.FutureRevision);
        var target = mismatchedBinding ? saved.Receipt.CommittedRevision!.Scope : pendingIntent.Proposal.Base;
        var pendingAppend = IngestionIntent.ForAppend(new(target.OrganizationId, target.ProjectId, target.MappingId), "ferrule", null,
            new EqualHead(target.RevisionId), pendingIntent.Native.Digest, pendingIntent.Native.ByteCount);
        var pending = await HistoricalIngestionSeed.SeedChatAppendAsync(test, actor, project, pendingAppend, pendingIntent.Key, commit: false);
        Assert.Equal(IngestionPhase.Ready, pending.Receipt.Phase);
        var before = await test.ScalarAsync<string>(HistoricalSnapshot);

        await test.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, 47), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(before, await test.ScalarAsync<string>(HistoricalSnapshot));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_proposals"));
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.chat_edit_approvals"));
        Assert.False(await test.ScalarAsync<bool>("SELECT has_function_privilege('weft_chat_writer','weft.chat_mapping_write_prepare(uuid,uuid,uuid,uuid,uuid,text,uuid,text,bytea)','EXECUTE')"));
        var denied = await Assert.ThrowsAsync<PostgresException>(() => PrepareCallAsync(test, pendingIntent));
        Assert.Equal("42501", denied.SqlState);

        await using var chat = new PostgresChatDatabase(test.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        var journal = new PostgresChatStore(chat);
        var receipt = Assert.Single(await journal.ReadAsync(actor, savedIntent.Conversation, savedIntent.TurnId, default));
        Assert.Equal("saved", receipt.Status);
        Assert.Equal(saved.Receipt.Facts.FutureRevision.RevisionId, receipt.Saved!.RevisionId);
        Assert.Equal(2, receipt.Saved.Sequence);
        await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
        var store = new PostgresRevisionIngestionStore(ingestion);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinalizeAsync(actor, pending.Claim));
        Assert.Contains("23514", refused.Message, StringComparison.Ordinal);
        Assert.Equal("ready", await test.ScalarAsync<string>("SELECT phase FROM weft.revision_ingestions WHERE operation_id=$1", pending.Claim.OperationId));
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.chat_mapping_write_commits"));

        await using var catalog = new PostgresCatalogDatabase(test.CatalogConnectionString);
        var resume = new LegacyResume(store, pending.Claim);
        var recovery = new ChatMappingWriteRecovery(journal, resume, new PostgresCatalogReader(catalog));
        var stillPending = Assert.Single(await recovery.ReadAsync(actor, pendingIntent.Conversation, pendingIntent.TurnId, true, default));
        Assert.Equal("pending", stillPending.Status); Assert.Null(stillPending.Saved);
        Assert.Equal(mismatchedBinding ? 0 : 1, resume.Resumes);
        Assert.Equal(0, resume.Appends);
        Assert.Equal(mismatchedBinding ? null : (Guid?)pending.Claim.OperationId, stillPending.OperationId);
        Assert.Equal(3L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.mapping_revisions"));
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>(HistoricalSnapshot));
    }

    private const string HistoricalSnapshot = """
        SELECT jsonb_build_array(
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY conversation_id) FROM weft.chat_conversations x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY turn_id) FROM weft.chat_turns x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY round_id) FROM weft.chat_rounds x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY turn_id,sequence) FROM weft.chat_events x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY request_key) FROM weft.chat_mapping_write_intents x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY request_key) FROM weft.chat_mapping_write_content x),
            (SELECT jsonb_agg(to_jsonb(x) ORDER BY request_key) FROM weft.chat_mapping_write_commits x))::text
        """;

    private static async Task MigrateThroughAsync(PostgresCase test, int count)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(count).ToArray());
        await runner.MigrateAsync();
    }

    private static IngestionIntent AppendIntent(ChatMappingWriteIntent intent)
    {
        var scope = intent.Proposal.Base;
        return IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId), "ferrule", null,
            new EqualHead(scope.RevisionId), intent.Native.Digest, intent.Native.ByteCount);
    }

    private static async Task<ChatMappingWriteIntent> PrepareHistoricalAsync(PostgresCase test, ActorContext actor, RevisionScope scope)
    {
        var now = new DateTimeOffset(await test.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var conversation = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(scope.OrganizationId, actor.PrincipalId, Guid.NewGuid()), null, now.AddHours(1));
        var key = conversation.Key;
        var id = Guid.NewGuid();
        var turn = new ChatTurnIntent(id, new(id, "Historical save", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
            ChatTurnPolicyTests.Profile(), new(64, 1048576, 3), now.AddMinutes(5));
        var round = PostgresChatTestSupport.Round();
        const string tool = "{\"v\":1,\"call\":\"call-a\",\"name\":\"save_mapping_edit\",\"ordinal\":1}";
        await using (var connection = PostgresChatSecurityTests.Open(test.ChatWriterConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            async Task<object?> RunAsync(string sql, params (string Name, NpgsqlDbType Type, object? Value)[] values)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                foreach (var (name, value) in new[] { ("org", key.OrganizationId), ("creator", key.Creator.Value), ("conversation", key.ConversationId), ("turn", id) })
                    command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value });
                foreach (var (name, type, value) in values) command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
                if (sql.StartsWith("INSERT", StringComparison.Ordinal) || sql.StartsWith("UPDATE", StringComparison.Ordinal))
                {
                    Assert.Equal(1, await command.ExecuteNonQueryAsync());
                    return null;
                }
                return await command.ExecuteScalarAsync();
            }
            await RunAsync("SELECT set_config('weft.organization_id',@org::text,true),set_config('weft.chat_principal_id',@creator::text,true)");
            async Task IdentityAsync(int kind, Guid identity, Guid? identityTurn) => Assert.Equal("Applied", await RunAsync(
                "SELECT weft.chat_reserve_identity(@org,@creator,@conversation,NULL,@kind,@identity,@identityTurn)",
                ("kind", NpgsqlDbType.Integer, kind), ("identity", NpgsqlDbType.Uuid, identity), ("identityTurn", NpgsqlDbType.Uuid, identityTurn)));
            await IdentityAsync(1, key.ConversationId, null);
            await RunAsync("INSERT INTO weft.chat_conversations(organization_id,creator_id,conversation_id,conversation_kind,retain_until) VALUES(@org,@creator,@conversation,2,@retain)",
                ("retain", NpgsqlDbType.TimestampTz, conversation.RetainUntilUtc));
            await IdentityAsync(2, id, id); await IdentityAsync(3, id, id);
            var canonical = ChatCanonicalEnvelope.Turn(conversation, turn);
            await RunAsync("""
                INSERT INTO weft.chat_turns(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,
                    profile_id,profile_version,provider_id,model_id,metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline)
                VALUES(@org,@creator,@conversation,@turn,@request,@turn,@envelope,@canonical,@envelopeFingerprint,@profile,@pv,@provider,@model,@policy,@mpv,@fingerprint,64,1048576,3,@deadline)
                """, ("request", NpgsqlDbType.Text, turn.OriginalRequest), ("envelope", NpgsqlDbType.Text, turn.Envelope.CanonicalJson),
                ("canonical", NpgsqlDbType.Text, canonical), ("envelopeFingerprint", NpgsqlDbType.Text, ChatCanonicalEnvelope.Fingerprint(canonical).Value),
                ("profile", NpgsqlDbType.Text, turn.Profile.ProfileId), ("pv", NpgsqlDbType.Integer, turn.Profile.Version),
                ("provider", NpgsqlDbType.Text, turn.Profile.ProviderId), ("model", NpgsqlDbType.Text, turn.Profile.ModelId),
                ("policy", NpgsqlDbType.Text, turn.Profile.MetadataPolicyId), ("mpv", NpgsqlDbType.Integer, turn.Profile.MetadataPolicyVersion),
                ("fingerprint", NpgsqlDbType.Text, turn.Profile.ConfigurationFingerprint.Value), ("deadline", NpgsqlDbType.TimestampTz, turn.DeadlineUtc));
            await RunAsync("INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text) VALUES(@org,@creator,@conversation,@turn,1,@event,1,'')",
                ("event", NpgsqlDbType.Uuid, Guid.NewGuid()));
            await IdentityAsync(4, round.RoundId, id);
            await RunAsync("UPDATE weft.chat_turns SET version=version+1,round_count=round_count+1 WHERE organization_id=@org AND creator_id=@creator AND conversation_id=@conversation AND turn_id=@turn AND version=1 AND phase=1");
            await RunAsync("""
                INSERT INTO weft.chat_rounds(organization_id,creator_id,conversation_id,turn_id,round_id,ordinal,fence,instance_id,exact_request)
                VALUES(@org,@creator,@conversation,@turn,@round,1,@fence,@instance,@exact)
                """, ("round", NpgsqlDbType.Uuid, round.RoundId), ("fence", NpgsqlDbType.Uuid, round.Fence),
                ("instance", NpgsqlDbType.Uuid, round.InstanceId), ("exact", NpgsqlDbType.Text, round.ExactRequest));
            await RunAsync("UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+@size WHERE organization_id=@org AND creator_id=@creator AND conversation_id=@conversation AND turn_id=@turn AND version=2 AND phase=1",
                ("size", NpgsqlDbType.Integer, Encoding.UTF8.GetByteCount(tool)));
            await RunAsync("""
                INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence)
                VALUES(@org,@creator,@conversation,@turn,2,@event,3,@text,@round,@fence)
                """, ("event", NpgsqlDbType.Uuid, Guid.NewGuid()), ("text", NpgsqlDbType.Text, tool),
                ("round", NpgsqlDbType.Uuid, round.RoundId), ("fence", NpgsqlDbType.Uuid, round.Fence));
            await transaction.CommitAsync();
        }
        var operations = MappingEditScript.Parse(Encoding.UTF8.GetBytes(MappingEditScriptTests.Bind));
        var fact = new ChatMappingWriteFact(ChatMappingWriteOutcome.Proposed, ChatMappingWriteReason.NotPermitted, scope, 1,
            null, null, null, null, operations.Count, operations.Digest, [], false);
        var intent = new ChatMappingWriteIntent(key, id, round.RoundId, "call-a", WorkspaceToolService.WriteKey(id, "call-a"), fact,
            MappingDraftPersistenceTests.Checkpoint(scope).Artifact);
        Assert.True(await PrepareCallAsync(test, intent));
        return intent;
    }

    private static async Task<bool> PrepareCallAsync(PostgresCase test, ChatMappingWriteIntent intent)
    {
        await using var connection = PostgresChatSecurityTests.Open(test.ChatWriterConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config('weft.chat_principal_id',$2,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(intent.Conversation.OrganizationId.ToString()); bind.Parameters.AddWithValue(intent.Conversation.Creator.Value.ToString());
            await bind.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand("SELECT weft.chat_mapping_write_prepare($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection, transaction);
        foreach (var value in new object[] { intent.Conversation.OrganizationId, intent.Conversation.Creator.Value, intent.Conversation.ConversationId,
            intent.TurnId, intent.RoundId, intent.CallId, intent.Key, ChatMetadataCodec.MappingWrite(intent.Proposal), intent.Native.CopyBytes() })
            command.Parameters.AddWithValue(value);
        var result = await command.ExecuteScalarAsync() is true;
        await transaction.CommitAsync();
        return result;
    }

    private sealed class LegacyResume(PostgresRevisionIngestionStore store, IngestionClaim claim) : IRevisionIngestionService
    {
        internal int Resumes, Appends;
        public async Task<RevisionIngestionResult> ResumeAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(claim.Project, project); Assert.Equal(claim.OperationId, operationId); Resumes++;
            await store.FinalizeAsync(actor, claim, cancellationToken);
            throw new InvalidOperationException("Legacy finalization unexpectedly passed the approval fence.");
        }
        public Task<RevisionIngestionResult> AppendAsync(ActorContext actor, MappingScope target, Guid key, AppendRevisionRequest request, Stream source, CancellationToken cancellationToken = default)
        { Appends++; throw new InvalidOperationException("Recovery cannot resupply source."); }
        public Task<RevisionIngestionResult> CreateAsync(ActorContext actor, ProjectScope project, Guid key, CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
