using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Workspace conversations on the actual ledger: ownership, focus authority, listings, events, audit and erasure.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspaceChatStoreTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Workspace_conversations_create_replay_conflict_and_list_beside_revision_conversations()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        var mapping = new MappingScope(f.Revision.OrganizationId, f.Revision.ProjectId, f.Revision.MappingId);
        var mappingFocused = Workspace(f, new(mapping), now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, mappingFocused));
        Assert.Equal(ChatStoreOutcome.Replayed, await f.Writer.CreateAsync(f.Actor, mappingFocused));
        // The same identity with another focus is a different original request.
        Assert.Equal(ChatStoreOutcome.Conflict, await f.Writer.CreateAsync(f.Actor,
            ChatConversationIntent.ForWorkspace(mappingFocused.Key, new(mapping, f.Revision.RevisionId), mappingFocused.RetainUntilUtc)));
        Assert.Equal(mappingFocused, await f.Reader.ReadAsync(f.Actor, mappingFocused.Key));
        var revisionFocused = Workspace(f, new(mapping, f.Revision.RevisionId), now.AddHours(1));
        var unfocused = Workspace(f, null, now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, revisionFocused));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, unfocused));

        var all = await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 10, null);
        Assert.Equal(new[] { unfocused.Key, revisionFocused.Key, mappingFocused.Key, f.Key }, all.Conversations.Select(item => item.Conversation.Key));
        Assert.False(all.HasMore);
        // The revision filter lists the revision's own conversations and workspace conversations focused on exactly it.
        var focused = await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, f.Revision, 10, null);
        Assert.Equal(new[] { revisionFocused.Key, f.Key }, focused.Conversations.Select(item => item.Conversation.Key));
        var first = await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 2, null);
        Assert.True(first.HasMore);
        var next = await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 2, first.Conversations[^1].Conversation.Key.ConversationId);
        Assert.Equal(new[] { mappingFocused.Key, f.Key }, next.Conversations.Select(item => item.Conversation.Key));
        Assert.Empty((await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 10, Guid.NewGuid())).Conversations);
        Assert.Equal(f.Conversation, (await f.Reader.FindConversationAsync(f.Actor, f.Key.OrganizationId, f.Key.ConversationId))!.Conversation);
        Assert.Equal(revisionFocused, (await f.Reader.FindConversationAsync(f.Actor, f.Key.OrganizationId, revisionFocused.Key.ConversationId))!.Conversation);

        // Conversations are private to their creator and to their organization.
        Assert.Empty((await f.Reader.ListOrganizationConversationsAsync(f.OtherActor, f.Key.OrganizationId, null, 10, null)).Conversations);
        Assert.Null(await f.Reader.FindConversationAsync(f.OtherActor, f.Key.OrganizationId, unfocused.Key.ConversationId));
        Assert.Null(await f.Reader.ReadAsync(f.OtherActor, unfocused.Key));
        Assert.Empty((await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Seed.OrganizationB, null, 10, null)).Conversations);
        Assert.Null(await f.Reader.FindConversationAsync(f.Actor, f.Seed.OrganizationB, unfocused.Key.ConversationId));
        var foreignKey = ChatConversationKey.ForWorkspace(f.Seed.OrganizationB, f.Actor.PrincipalId, unfocused.Key.ConversationId);
        Assert.Null(await f.Reader.ReadAsync(f.Actor, foreignKey));
    }

    [PostgresFact]
    public async Task A_focus_outside_the_creators_readable_projects_is_refused_without_reserving_its_identity()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        var hidden = Workspace(f, new(new(f.Revision.OrganizationId, f.Seed.HiddenProjectA, f.Revision.MappingId)), now.AddHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.CreateAsync(f.Actor, hidden));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_identities WHERE conversation_id=$1", hidden.Key.ConversationId));
        Assert.Null(await f.Reader.ReadAsync(f.Actor, hidden.Key));
    }

    [PostgresFact]
    public async Task Workspace_turns_record_their_focus_publish_verified_citations_and_audit_shared_data()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        var mapping = new MappingScope(f.Revision.OrganizationId, f.Revision.ProjectId, f.Revision.MappingId);
        var conversation = Workspace(f, new(mapping), now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, conversation));
        var turn = await StartAsync(f, conversation, ChatTurnFocus.Resolved(f.Revision));
        Assert.Equal(ChatTurnFocus.Resolved(f.Revision), turn.Intent.Envelope.Focus);
        Assert.Equal(turn, await f.Reader.ReadTurnAsync(f.Actor, conversation.Key, turn.Intent.TurnId));
        var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, conversation.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
        var facts = reserved.Turn!;

        var project = ChatValidatedCitation.ForReadResource(conversation, ChatWorkspaceCitationFact.Project(new(mapping.OrganizationId, mapping.ProjectId)));
        var report = ChatValidatedCitation.ValidateWorkspaceReport(conversation, f.VerifiedValidation, new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0));
        foreach (var citation in new[] { project, report })
        {
            var written = await f.Writer.AppendCitationAsync(f.Actor, conversation.Key, turn.Intent.TurnId, facts.Version, round.RoundId, round.Fence,
                new(Guid.NewGuid(), citation));
            Assert.Equal(ChatStoreOutcome.Applied, written.Outcome);
            facts = written.Facts!;
        }
        // A revision conversation's citation never enters a workspace conversation, nor the reverse.
        await Assert.ThrowsAsync<ArgumentException>(() => f.Writer.AppendCitationAsync(f.Actor, conversation.Key, turn.Intent.TurnId, facts.Version,
            round.RoundId, round.Fence, new(Guid.NewGuid(), ChatValidatedCitation.Validate(f.Conversation, f.VerifiedValidation, new(ChatCitationKind.ValidationReport)))));
        var shared = new ChatPayloadDisclosureFact(mapping.ProjectId, ChatPayloadSource.PreviewOutput, Guid.NewGuid(), null, 512);
        var data = ChatEventData.ForDataShared(Guid.NewGuid(), shared);
        var appended = await f.Writer.AppendAsync(f.Actor, conversation.Key, turn.Intent.TurnId, facts.Version, round.RoundId, round.Fence, data);
        Assert.Equal(ChatStoreOutcome.Applied, appended.Outcome);

        var events = await f.Reader.ReadEventsAsync(f.Actor, conversation.Key, turn.Intent.TurnId, 0, 10);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.WorkspaceCitation, ChatEventKind.WorkspaceCitation, ChatEventKind.DataShared },
            events.Select(item => item.Kind));
        Assert.Equal(project.Workspace, events[1].WorkspaceCitation);
        Assert.Equal(report.Workspace, events[2].WorkspaceCitation);
        Assert.Equal(shared, events[3].DataShared);

        // The audit row names who shared what from where with which profile, and nothing of the content.
        var audit = await f.Case.ScalarAsync<string>("SELECT to_jsonb(a)::text FROM weft.chat_payload_audit a WHERE event_id=$1", data.EventId);
        using (var row = JsonDocument.Parse(audit))
        {
            var root = row.RootElement;
            Assert.Equal(f.Actor.PrincipalId.Value, root.GetProperty("creator_id").GetGuid());
            Assert.Equal(mapping.ProjectId, root.GetProperty("project_id").GetGuid());
            Assert.Equal(shared.SubjectId, root.GetProperty("subject_id").GetGuid());
            Assert.Equal(2, root.GetProperty("source_kind").GetInt32());
            Assert.Equal(512, root.GetProperty("byte_count").GetInt32());
            Assert.Equal(turn.Intent.Profile.ProfileId, root.GetProperty("profile_id").GetString());
            Assert.Equal(turn.Intent.Profile.ModelId, root.GetProperty("model_id").GetString());
            Assert.Equal(new[] { "byte_count", "conversation_id", "creator_id", "event_id", "metadata_policy_id", "metadata_policy_version",
                "model_id", "organization_id", "profile_id", "profile_version", "project_id", "provider_id", "recorded_at", "round_id",
                "source_kind", "subject_id", "subject_version", "turn_id" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }
        foreach (var change in new[] { "UPDATE weft.chat_payload_audit SET byte_count=1", "DELETE FROM weft.chat_payload_audit" })
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => f.Case.ExecuteAsync(change))).SqlState);
        // No chat login can read the audit.
        foreach (var route in new[] { f.Case.ChatReaderConnectionString, f.Case.ChatWriterConnectionString })
        {
            await using var connection = PostgresChatSecurityTests.Open(route); await connection.OpenAsync();
            await using var read = new NpgsqlCommand("SELECT count(*) FROM weft.chat_payload_audit", connection);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync())).SqlState);
        }
    }

    [PostgresFact]
    public async Task Ledger_admission_refuses_malformed_workspace_metadata_wrong_kinds_and_unreadable_shared_projects()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        var conversation = Workspace(f, null, now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, conversation));
        var turn = await StartAsync(f, conversation, ChatTurnFocus.None);
        var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.ReserveRoundAsync(f.Actor, conversation.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var mapping = new MappingScope(f.Revision.OrganizationId, f.Revision.ProjectId, f.Revision.MappingId);
        var pin = f.Conversation.PinnedProjection;
        var citations = new[]
        {
            ChatMetadataCodec.WorkspaceCitation(ChatWorkspaceCitationFact.Mapping(mapping)),
            ChatMetadataCodec.WorkspaceCitation(ChatWorkspaceCitationFact.RegressionCase(mapping, Guid.NewGuid(), 3)),
            ChatMetadataCodec.WorkspaceCitation(ChatWorkspaceCitationFact.GuideSection(mapping.OrganizationId, "regression-tests")),
            ChatMetadataCodec.WorkspaceCitation(ChatWorkspaceCitationFact.ReportResource(f.Revision, pin, new(ChatCitationKind.Field, "field.one")))
        };
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var valid in citations)
            foreach (var key in JsonNode.Parse(valid)!.AsObject().Select(p => p.Key).ToArray())
            {
                var node = JsonNode.Parse(valid)!.AsObject(); node.Remove(key);
                await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, node.ToJsonString(), before);
                await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, With(valid, key, new JsonArray()), before);
            }
        // Structurally valid, but not this organization or not an exact retained report.
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, With(citations[0], "organization", JsonValue.Create(f.Seed.OrganizationB)), before, valid: true);
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, With(citations[3], "digest", JsonValue.Create(new string('c', 64))), before, valid: true);
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, With(citations[3], "targetKind", JsonValue.Create(9)), before);
        // A revision citation shape belongs only to revision conversations.
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Citation,
            ChatMetadataCodec.Citation(new(f.Key.PinnedRevision, pin, new(ChatCitationKind.ProjectionReport))), before, valid: true);
        var shared = ChatMetadataCodec.Disclosure(new(mapping.ProjectId, ChatPayloadSource.RegressionCase, Guid.NewGuid(), 2, 100));
        foreach (var key in new[] { "v", "project", "source", "subject", "version", "bytes" })
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.DataShared, With(shared, key, null), before);
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.DataShared, With(shared, "bytes", JsonValue.Create(0)), before);
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.DataShared, With(shared, "version", JsonValue.Create("2")), before);
        // Shared data is recorded only for a project its creator can currently read.
        var error = await Assert.ThrowsAsync<PostgresException>(() => PublishAsync(f, turn.Intent.TurnId, round, ChatEventKind.DataShared,
            ChatMetadataCodec.Disclosure(new(f.Seed.HiddenProjectA, ChatPayloadSource.PreviewInput, Guid.NewGuid(), null, 10))));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
        foreach (var valid in citations) await PublishAsync(f, turn.Intent.TurnId, round, ChatEventKind.WorkspaceCitation, valid);
        await PublishAsync(f, turn.Intent.TurnId, round, ChatEventKind.DataShared, shared);
        var events = await f.Reader.ReadEventsAsync(f.Actor, conversation.Key, turn.Intent.TurnId, 0, 10);
        Assert.Equal(citations, events.Skip(1).Take(4).Select(item => ChatMetadataCodec.WorkspaceCitation(item.WorkspaceCitation!)));
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_payload_audit WHERE source_kind=3 AND subject_version=2"));
    }

    [PostgresFact]
    public async Task Revocation_hides_revision_conversations_with_their_project_and_everything_with_the_organization()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        var workspace = Workspace(f, null, now.AddHours(1));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, workspace));
        await f.Case.ExecuteAsync("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            f.Actor.PrincipalId.Value, f.Revision.ProjectId);
        Assert.Equal(new[] { workspace.Key }, (await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 10, null))
            .Conversations.Select(item => item.Conversation.Key));
        Assert.Null(await f.Reader.FindConversationAsync(f.Actor, f.Key.OrganizationId, f.Key.ConversationId));
        Assert.Equal(workspace, await f.Reader.ReadAsync(f.Actor, workspace.Key));
        await f.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE principal_id=$1 AND organization_id=$2",
            f.Actor.PrincipalId.Value, f.Key.OrganizationId);
        Assert.Empty((await f.Reader.ListOrganizationConversationsAsync(f.Actor, f.Key.OrganizationId, null, 10, null)).Conversations);
        Assert.Null(await f.Reader.ReadAsync(f.Actor, workspace.Key));
        Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await f.Writer.StartAsync(f.Actor, workspace.Key,
            await IntentAsync(f, workspace, ChatTurnFocus.None))).Outcome);
    }

    [PostgresFact]
    public async Task Trusted_lifecycle_closes_and_erases_an_expired_workspace_conversation_but_keeps_its_audit()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var now = await NowAsync(f);
        // Fixture-only deadline, not an application retention default.
        var workspace = Workspace(f, null, now.AddSeconds(12));
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, workspace));
        var turn = await StartAsync(f, workspace, ChatTurnFocus.None, deadline: now.AddSeconds(11));
        var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, workspace.Key, turn.Intent.TurnId, turn.Version, round);
        var shared = ChatEventData.ForDataShared(Guid.NewGuid(), new(f.Revision.ProjectId, ChatPayloadSource.PreviewInput, Guid.NewGuid(), null, 9));
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.AppendAsync(f.Actor, workspace.Key, turn.Intent.TurnId, reserved.Turn!.Version,
            round.RoundId, round.Fence, shared)).Outcome);
        await using var lifecycleDatabase = new PostgresChatLifecycleDatabase(f.Case.ChatLifecycleConnectionString);
        var lifecycle = new PostgresChatLifecycleStore(lifecycleDatabase);
        var observed = Assert.IsType<ChatLifecycleObservation>(await lifecycle.ObserveAsync(workspace.Key, turn.Intent.TurnId));
        Assert.Equal(ChatStoreOutcome.Applied, await lifecycle.CloseAsync(workspace.Key, turn.Intent.TurnId, observed.Version, ChatTurnPhase.Interrupted));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await NowAsync(f) < workspace.RetainUntilUtc) await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        Assert.Equal(ChatStoreOutcome.Applied, await lifecycle.EraseExpiredAsync(workspace.Key,
            new("fixture-retention", 1, workspace.RetainUntilUtc.AddHours(1))));
        Assert.True(await f.Case.ScalarAsync<bool>("""
            SELECT NOT EXISTS(SELECT 1 FROM weft.chat_conversations WHERE conversation_id=$1)
                AND NOT EXISTS(SELECT 1 FROM weft.chat_identities WHERE conversation_id=$1 AND erased_at IS NULL)
                AND EXISTS(SELECT 1 FROM weft.chat_identities WHERE conversation_id=$1 AND erased_at IS NOT NULL)
                AND EXISTS(SELECT 1 FROM weft.chat_payload_audit WHERE conversation_id=$1 AND event_id=$2)
            """, workspace.Key.ConversationId, shared.EventId));
        // The erased identity is never reused.
        Assert.Equal(ChatStoreOutcome.IdentityRetired, await f.Writer.CreateAsync(f.Actor,
            ChatConversationIntent.ForWorkspace(workspace.Key, null, (await NowAsync(f)).AddHours(1))));
    }

    private static ChatConversationIntent Workspace(PostgresChatTestSupport f, ChatConversationFocus? focus, DateTimeOffset retain) =>
        ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(f.Key.OrganizationId, f.Actor.PrincipalId, Guid.NewGuid()), focus, retain);

    private static async Task<DateTimeOffset> NowAsync(PostgresChatTestSupport f) =>
        new(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()"));

    private static async Task<ChatTurnIntent> IntentAsync(PostgresChatTestSupport f, ChatConversationIntent conversation, ChatTurnFocus focus,
        DateTimeOffset? deadline = null)
    {
        var now = await NowAsync(f);
        var turnId = Guid.NewGuid();
        var end = deadline ?? now.AddMinutes(9);
        return new(turnId, new ChatOriginalEnvelope(turnId, "workspace question", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), focus),
            ChatTurnPolicyTests.Profile(), new(64, 1048576, 3), end < conversation.RetainUntilUtc ? end : conversation.RetainUntilUtc);
    }

    private static async Task<ChatTurnFacts> StartAsync(PostgresChatTestSupport f, ChatConversationIntent conversation, ChatTurnFocus focus,
        DateTimeOffset? deadline = null)
    {
        var result = await f.Writer.StartAsync(f.Actor, conversation.Key, await IntentAsync(f, conversation, focus, deadline));
        Assert.Equal(ChatStoreOutcome.Applied, result.Outcome);
        return result.Facts!;
    }

    private static string With(string valid, string field, JsonNode? value)
    {
        var node = JsonNode.Parse(valid)!.AsObject(); node[field] = value; return node.ToJsonString();
    }

    private static async Task RefuseAsync(PostgresChatTestSupport f, Guid turn, ChatRoundIntent round, ChatEventKind kind, string metadata,
        byte[] before, bool valid = false)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => PublishAsync(f, turn, round, kind, metadata));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await using (var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT weft.chat_metadata_valid($1,$2,$3)", connection);
            command.Parameters.AddWithValue((int)kind); command.Parameters.AddWithValue(metadata); command.Parameters.AddWithValue(round.RoundId);
            Assert.Equal(valid, Assert.IsType<bool>(await command.ExecuteScalarAsync()));
        }
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }

    // The writer's raw statements, so admission alone decides; the store would refuse earlier.
    private static async Task PublishAsync(PostgresChatTestSupport f, Guid turn, ChatRoundIntent round, ChatEventKind kind, string metadata)
    {
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
        await using (var advance = new NpgsqlCommand(
            "UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+$2 WHERE turn_id=$1", connection, transaction))
        {
            advance.Parameters.AddWithValue(turn); advance.Parameters.AddWithValue(Encoding.UTF8.GetByteCount(metadata));
            Assert.Equal(1, await advance.ExecuteNonQueryAsync());
        }
        await using (var append = new NpgsqlCommand("""
            INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
            SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,$3,'',$4,$5,$6 FROM weft.chat_turns WHERE turn_id=$1
            """, connection, transaction))
        {
            append.Parameters.AddWithValue(turn); append.Parameters.AddWithValue(Guid.NewGuid()); append.Parameters.AddWithValue((int)kind);
            append.Parameters.AddWithValue(round.RoundId); append.Parameters.AddWithValue(round.Fence); append.Parameters.AddWithValue(metadata);
            Assert.Equal(1, await append.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }
}
