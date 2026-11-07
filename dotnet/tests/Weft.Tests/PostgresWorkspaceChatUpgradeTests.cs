using System.Text;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Genuine upgrade from the current 31-script prefix. A revision conversation, its turn, a reserved round and a verified
/// citation are written through the prefix's own writer statements and guards, exactly as that build wrote them;
/// the reports they pin are authored usable attempts. After the workspace migration the rows are unchanged, the
/// current stores read and continue them, and workspace conversations coexist with them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspaceChatUpgradeTests(PostgresFixture fixture)
{
    private const int PrefixVersion = 31;

    [PostgresFact]
    public async Task ExistingRevisionConversationsSurviveUnchangedAndContinueBesideWorkspaceConversations()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        // The revision is committed by the original ingestion writer, which admits only the unchanged two-script schema.
        await MigrateThroughAsync(test, 2);
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1 AND project_id=$2",
            seed.Alice, seed.ProjectA);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await MigrateThroughAsync(test, PrefixVersion);
        Assert.False(await test.ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='weft' AND table_name='chat_conversations' AND column_name='conversation_kind')
                OR pg_catalog.to_regclass('weft.chat_payload_audit') IS NOT NULL
            """), "The upgrade did not start from the 31-script prefix.");

        var revision = receipt.Facts.FutureRevision;
        var source = new RevisionReportSource(revision, receipt.Facts.NativeObject, receipt.Facts.Intent.LogicalDocument,
            receipt.Facts.Intent.DependencyIdentity, receipt.Facts.BackendProfile.BackendId);
        var projection = await SeedReportAsync(test, actor, new ProjectionCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
            "weft.mapping-projection.v1", "synthetic.workspace-upgrade.v1", "synthetic.report.codec.v1", new()));
        var validation = await SeedReportAsync(test, actor, new ValidationCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
            "weft.mapping-validation.v1", "synthetic.workspace-upgrade.v1", "synthetic.report.codec.v1", new()));

        var now = new DateTimeOffset(await test.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var conversation = new ChatConversationIntent(new(revision, actor.PrincipalId, Guid.NewGuid()), projection, validation, now.AddHours(2));
        var turnId = Guid.NewGuid();
        var intent = new ChatTurnIntent(turnId, new ChatOriginalEnvelope(turnId, "historical question", [new(ChatCitationKind.Field, "field.one")],
            ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding()), ChatTurnPolicyTests.Profile(), new(16, 65536, 3), now.AddMinutes(1));
        var round = PostgresChatTestSupport.Round();
        var citation = ChatMetadataCodec.Citation(new(revision, projection, new(ChatCitationKind.ProjectionReport)));
        await WriteAsPrefixAsync(test, conversation, intent, round, citation);

        const string snapshot = """
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(c) ORDER BY conversation_id) FROM weft.chat_conversations c),
                (SELECT jsonb_agg(to_jsonb(t) ORDER BY turn_id) FROM weft.chat_turns t),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY round_id) FROM weft.chat_rounds r),
                (SELECT jsonb_agg(to_jsonb(e) ORDER BY turn_id,sequence) FROM weft.chat_events e),
                (SELECT jsonb_agg(to_jsonb(i) ORDER BY identity_kind,identity_id) FROM weft.chat_identities i))::text
            """;
        var before = await test.ScalarAsync<string>(snapshot);

        await test.MigrateAsync();

        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(ChatSchemaContract.WorkspaceMigrationChecksum, await test.ScalarAsync<string>(
            "SELECT checksum FROM weft.schema_migrations WHERE version=$1", ChatSchemaContract.WorkspaceMigrationVersion));
        // Every retained fact is unchanged; the stored row is explicitly a revision conversation without a focus.
        Assert.Equal(before, await test.ScalarAsync<string>(snapshot.Replace("to_jsonb(c)",
            "(to_jsonb(c)-'conversation_kind'-'focus_project_id'-'focus_mapping_id'-'focus_revision_id')", StringComparison.Ordinal)));
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT bool_and(conversation_kind=1 AND focus_project_id IS NULL AND focus_mapping_id IS NULL AND focus_revision_id IS NULL)
                AND NOT EXISTS(SELECT 1 FROM weft.chat_payload_audit) FROM weft.chat_conversations
            """));

        await using var readerDatabase = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        await using var writerDatabase = new PostgresChatDatabase(test.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
        var reader = new PostgresChatStore(readerDatabase);
        var writer = new PostgresChatStore(writerDatabase);
        Assert.Equal(conversation, await reader.ReadAsync(actor, conversation.Key));
        var turn = Assert.IsType<ChatTurnFacts>(await reader.ReadTurnAsync(actor, conversation.Key, turnId));
        Assert.Equal(intent, turn.Intent);
        var events = await reader.ReadEventsAsync(actor, conversation.Key, turnId, 0, 10);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.Citation }, events.Select(item => item.Kind));
        Assert.Equal(new ChatCitationFact(revision, projection, new(ChatCitationKind.ProjectionReport)), events[1].Citation);
        Assert.Equal(round, (await reader.ReadRoundAsync(actor, conversation.Key, turnId, round.RoundId))!.Intent);

        // The upgraded turn continues under its original pins; it was never reinterpreted.
        Assert.Equal(ChatStoreOutcome.Applied, (await writer.FinishAsync(actor, conversation.Key, turnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);

        // A workspace conversation of the same creator is listed beside the revision conversation.
        var workspace = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(seed.OrganizationA, actor.PrincipalId, Guid.NewGuid()),
            new(new(revision.OrganizationId, revision.ProjectId, revision.MappingId), revision.RevisionId), now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await writer.CreateAsync(actor, workspace));
        var listed = await reader.ListOrganizationConversationsAsync(actor, seed.OrganizationA, null, 10, null);
        Assert.Equal(new[] { workspace.Key, conversation.Key }, listed.Conversations.Select(item => item.Conversation.Key));
        var focused = await reader.ListOrganizationConversationsAsync(actor, seed.OrganizationA, revision, 10, null);
        Assert.Equal(2, focused.Conversations.Count);
        Assert.Equal(conversation, (await reader.FindConversationAsync(actor, seed.OrganizationA, conversation.Key.ConversationId))!.Conversation);
    }

    private static async Task MigrateThroughAsync(PostgresCase test, int count)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(count).ToArray());
        await runner.MigrateAsync();
    }

    // The prefix build's own writer statements: identity routine, conversation, turn and start event, round, citation.
    private static async Task WriteAsPrefixAsync(PostgresCase test, ChatConversationIntent conversation, ChatTurnIntent intent,
        ChatRoundIntent round, string citation)
    {
        var key = conversation.Key;
        var scope = key.PinnedRevision;
        await using var connection = PostgresChatSecurityTests.Open(test.ChatWriterConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            void Add(string name, NpgsqlDbType type, object? value) => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            Add("org", NpgsqlDbType.Uuid, key.OrganizationId); Add("creator", NpgsqlDbType.Uuid, key.Creator.Value);
            Add("conversation", NpgsqlDbType.Uuid, key.ConversationId); Add("project", NpgsqlDbType.Uuid, scope.ProjectId);
            Add("mapping", NpgsqlDbType.Uuid, scope.MappingId); Add("revision", NpgsqlDbType.Uuid, scope.RevisionId);
            Add("turn", NpgsqlDbType.Uuid, intent.TurnId);
            return command;
        }
        async Task RunAsync(string sql, params (string Name, NpgsqlDbType Type, object? Value)[] values)
        {
            await using var command = Command(sql);
            foreach (var (name, type, value) in values) command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
            await command.ExecuteNonQueryAsync();
        }
        async Task ReserveAsync(int kind, Guid identity, Guid? turn)
        {
            await using var command = Command("SELECT weft.chat_reserve_identity(@org,@creator,@conversation,@project,@kind,@identity,@identityTurn)");
            command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Integer) { Value = kind });
            command.Parameters.Add(new NpgsqlParameter("identity", NpgsqlDbType.Uuid) { Value = identity });
            command.Parameters.Add(new NpgsqlParameter("identityTurn", NpgsqlDbType.Uuid) { Value = turn is { } id ? id : DBNull.Value });
            Assert.Equal("Applied", await command.ExecuteScalarAsync());
        }
        await RunAsync("SELECT set_config('search_path','pg_catalog,weft,pg_temp',true),set_config('weft.organization_id',@org::text,true),set_config('weft.chat_principal_id',@creator::text,true)");
        await ReserveAsync(1, key.ConversationId, null);
        await RunAsync("""
            INSERT INTO weft.chat_conversations(organization_id,creator_id,conversation_id,project_id,mapping_id,revision_id,
                projection_entry_id,projection_id,projection_key,projection_digest,validation_entry_id,validation_id,validation_key,validation_digest,retain_until)
            VALUES(@org,@creator,@conversation,@project,@mapping,@revision,@pe,@pi,@pk,@pd,@ve,@vi,@vk,@vd,@retain)
            """, ("pe", NpgsqlDbType.Uuid, conversation.PinnedProjection.CacheEntryId), ("pi", NpgsqlDbType.Uuid, conversation.PinnedProjection.DerivationId),
            ("pk", NpgsqlDbType.Text, conversation.PinnedProjection.KeyFingerprint.Value), ("pd", NpgsqlDbType.Text, conversation.PinnedProjection.ObjectDigest.Value),
            ("ve", NpgsqlDbType.Uuid, conversation.PinnedValidation.CacheEntryId), ("vi", NpgsqlDbType.Uuid, conversation.PinnedValidation.DerivationId),
            ("vk", NpgsqlDbType.Text, conversation.PinnedValidation.KeyFingerprint.Value), ("vd", NpgsqlDbType.Text, conversation.PinnedValidation.ObjectDigest.Value),
            ("retain", NpgsqlDbType.TimestampTz, conversation.RetainUntilUtc));
        await ReserveAsync(2, intent.TurnId, intent.TurnId);
        await ReserveAsync(3, intent.Envelope.RequestId, intent.TurnId);
        var canonical = ChatCanonicalEnvelope.Turn(conversation, intent);
        await RunAsync("""
            INSERT INTO weft.chat_turns(organization_id,creator_id,conversation_id,turn_id,original_request,request_id,original_envelope,canonical_envelope,envelope_fingerprint,profile_id,profile_version,
                provider_id,model_id,metadata_policy_id,metadata_policy_version,profile_fingerprint,max_events,max_event_bytes,max_rounds,deadline)
            VALUES(@org,@creator,@conversation,@turn,@request,@requestId,@originalEnvelope,@canonicalEnvelope,@envelopeFingerprint,@profile,@pv,@provider,@model,@policy,@mpv,@fingerprint,@events,@bytes,@rounds,@deadline)
            """, ("request", NpgsqlDbType.Text, intent.OriginalRequest), ("requestId", NpgsqlDbType.Uuid, intent.Envelope.RequestId),
            ("originalEnvelope", NpgsqlDbType.Text, intent.Envelope.CanonicalJson), ("canonicalEnvelope", NpgsqlDbType.Text, canonical),
            ("envelopeFingerprint", NpgsqlDbType.Text, ChatCanonicalEnvelope.Fingerprint(canonical).Value), ("profile", NpgsqlDbType.Text, intent.Profile.ProfileId),
            ("pv", NpgsqlDbType.Integer, intent.Profile.Version), ("provider", NpgsqlDbType.Text, intent.Profile.ProviderId), ("model", NpgsqlDbType.Text, intent.Profile.ModelId),
            ("policy", NpgsqlDbType.Text, intent.Profile.MetadataPolicyId), ("mpv", NpgsqlDbType.Integer, intent.Profile.MetadataPolicyVersion),
            ("fingerprint", NpgsqlDbType.Text, intent.Profile.ConfigurationFingerprint.Value), ("events", NpgsqlDbType.Integer, intent.Budget.MaxEvents),
            ("bytes", NpgsqlDbType.Integer, intent.Budget.MaxEventBytes), ("rounds", NpgsqlDbType.Integer, intent.Budget.MaxRounds),
            ("deadline", NpgsqlDbType.TimestampTz, intent.DeadlineUtc));
        const string insertEvent = """
            INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
            VALUES(@org,@creator,@conversation,@turn,@sequence,@event,@eventKind,@text,@round,@fence,@metadata)
            """;
        await RunAsync(insertEvent, ("sequence", NpgsqlDbType.Integer, 1), ("event", NpgsqlDbType.Uuid, Guid.NewGuid()), ("eventKind", NpgsqlDbType.Integer, 1),
            ("text", NpgsqlDbType.Text, ""), ("round", NpgsqlDbType.Uuid, null), ("fence", NpgsqlDbType.Uuid, null), ("metadata", NpgsqlDbType.Text, null));
        await ReserveAsync(4, round.RoundId, intent.TurnId);
        await RunAsync("UPDATE weft.chat_turns SET version=version+1,round_count=round_count+1 WHERE organization_id=@org AND creator_id=@creator AND conversation_id=@conversation AND turn_id=@turn AND version=1 AND phase=1");
        await RunAsync("""
            INSERT INTO weft.chat_rounds(organization_id,creator_id,conversation_id,turn_id,round_id,ordinal,fence,instance_id,exact_request)
            VALUES(@org,@creator,@conversation,@turn,@roundId,1,@fenceId,@instance,@exact)
            """, ("roundId", NpgsqlDbType.Uuid, round.RoundId), ("fenceId", NpgsqlDbType.Uuid, round.Fence),
            ("instance", NpgsqlDbType.Uuid, round.InstanceId), ("exact", NpgsqlDbType.Text, round.ExactRequest));
        await RunAsync("UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+@size WHERE organization_id=@org AND creator_id=@creator AND conversation_id=@conversation AND turn_id=@turn AND version=2 AND phase=1",
            ("size", NpgsqlDbType.Integer, Encoding.UTF8.GetByteCount(citation)));
        await RunAsync(insertEvent, ("sequence", NpgsqlDbType.Integer, 2), ("event", NpgsqlDbType.Uuid, Guid.NewGuid()), ("eventKind", NpgsqlDbType.Integer, 11),
            ("text", NpgsqlDbType.Text, ""), ("round", NpgsqlDbType.Uuid, round.RoundId), ("fence", NpgsqlDbType.Uuid, round.Fence), ("metadata", NpgsqlDbType.Text, citation));
        await transaction.CommitAsync();
    }

    /// <summary>An authored usable retained attempt for one operation, as the derivation owner would have committed it.</summary>
    private static async Task<ChatReportPin> SeedReportAsync(PostgresCase test, ActorContext actor, RevisionReportKey key)
    {
        await using var connection = PostgresChatSecurityTests.Open(test.MigrationConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivations(organization_id,project_id,mapping_id,revision_id,operation_kind,
                native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,
                engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,policy_schema_version,policy_json,policy_fingerprint,
                native_byte_limit,worker_response_byte_limit,item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,
                validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34)
            RETURNING cache_entry_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(insert, key.Scope);
        var projection = key.Operation == RevisionReportOperation.Projection;
        object[] values = [projection ? "projection" : "validation", key.Source.NativeObject.Id.ObjectId, "native", key.NativeDigest.Value, key.Source.NativeObject.ByteCount,
            key.Source.LogicalDocument, key.Source.DependencyIdentity, key.BackendId, key.BackendRevision, key.BackendBuild, key.AdapterBuild,
            key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion, key.Policy.SchemaVersion, Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()),
            key.Policy.Fingerprint.Value, key.Policy.NativeByteLimit, key.Policy.WorkerResponseByteLimit, key.Policy.ItemLimit, key.Policy.ResponseItemLimit,
            key.Policy.JsonDepthLimit, key.Policy.DetailDepthLimit, key.Policy.DiagnosticLimit, key.Policy.ValidationIssueLimit, key.Policy.TextByteLimit,
            key.Policy.HeaderByteLimit, key.Policy.StoredObjectByteLimit, key.Policy.ResponseByteLimit, key.Fingerprint.Value];
        foreach (var value in values) insert.Parameters.AddWithValue(value);
        var entry = Assert.IsType<Guid>(await insert.ExecuteScalarAsync());
        await using var claim = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,claimant_instance_id)
            VALUES($1,$2,$3,$4,$5,1,$6,$7) RETURNING derivation_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(claim, key.Scope);
        claim.Parameters.AddWithValue(entry); claim.Parameters.AddWithValue(actor.PrincipalId.Value); claim.Parameters.AddWithValue(Guid.NewGuid());
        var derivation = Assert.IsType<Guid>(await claim.ExecuteScalarAsync());
        await using (var advance = new NpgsqlCommand("UPDATE weft.revision_derivations SET current_generation=1,current_attempt_id=$2 WHERE cache_entry_id=$1", connection, transaction))
        { advance.Parameters.AddWithValue(entry); advance.Parameters.AddWithValue(derivation); Assert.Equal(1, await advance.ExecuteNonQueryAsync()); }
        var digest = projection ? new string('a', 64) : new string('b', 64);
        await using (var finish = new NpgsqlCommand($"""
            UPDATE weft.revision_derivation_attempts a SET completion_kind='usable',observation_present=true,backend_status='succeeded',
                provenance_confirmed=true,provenance_organization_id=k.organization_id,provenance_project_id=k.project_id,
                provenance_mapping_id=k.mapping_id,provenance_revision_id=k.revision_id,provenance_native_digest=k.native_digest,
                provenance_backend_id=k.backend_id,provenance_engine_revision=k.engine_revision,provenance_backend_build=k.backend_build,
                provenance_adapter_build=k.adapter_build,
                {(projection ? "projection_coverage='complete',document_item_count=3,document_diagnostic_count=0,coverage_gap_count=0,operation_diagnostic_count=0"
                    : "is_valid=true,total_issue_count=0,emitted_issue_count=0,issues_truncated=false,validation_diagnostic_count=0")},
                derived_digest='{digest}',derived_byte_count=73
            FROM weft.revision_derivations k WHERE a.derivation_id=$1 AND k.cache_entry_id=a.cache_entry_id
            """, connection, transaction))
        { finish.Parameters.AddWithValue(derivation); Assert.Equal(1, await finish.ExecuteNonQueryAsync()); }
        await using (var select = new NpgsqlCommand("UPDATE weft.revision_derivations SET selected_derivation_id=$2 WHERE cache_entry_id=$1", connection, transaction))
        { select.Parameters.AddWithValue(entry); select.Parameters.AddWithValue(derivation); Assert.Equal(1, await select.ExecuteNonQueryAsync()); }
        await transaction.CommitAsync();
        return new(entry, derivation, key.Fingerprint, Sha256Digest.Parse(digest));
    }
}
