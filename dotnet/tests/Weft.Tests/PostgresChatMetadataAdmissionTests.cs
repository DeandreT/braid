using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Direct restricted-writer regression source. Every malformed append advances
// real counters with the correct round/fence; a valid control uses the same SQL.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatMetadataAdmissionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task RequiredUsageFailureAndCitationFieldsRejectMissingNullAndWrongJsonTypesBeforePublication()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await StartAsync(f); var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var unknown = ChatMetadataCodec.Usage(new(round.RoundId, ChatUsageOrigin.ProviderObservation, null, null, null));
        var partial = ChatMetadataCodec.Usage(new(round.RoundId, ChatUsageOrigin.ProviderObservation, 0, 7, null));
        var failure = ChatMetadataCodec.Failure(new(round.RoundId, ChatProviderFailureCode.TruncatedResponse, ChatDispatchObservation.Attempted));
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var malformed in new[] { "null", "{}", "[]", "1", "\"metadata\"" })
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Usage, malformed, before);
        var malformedUsage = RequiredMutations(unknown, ["v", "round", "origin", "known"])
            .Concat(NullableMutations(unknown, ["input", "output", "total"]))
            .Append(With(unknown, "round", JsonValue.Create(Guid.Empty)))
            .Append(With(unknown, "round", JsonValue.Create(Guid.NewGuid())))
            .Append(With(unknown, "origin", JsonValue.Create(0)))
            .Append(With(unknown, "known", JsonValue.Create(true)))
            .Append(With(unknown, "input", JsonValue.Create("0")));
        foreach (var malformed in malformedUsage)
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Usage, malformed, before);
        foreach (var malformed in RequiredMutations(failure, ["v", "round", "code", "dispatch"]))
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.ProviderFailure, malformed, before);

        foreach (var target in new ChatCitationTarget[] { new(ChatCitationKind.ProjectionReport), new(ChatCitationKind.ValidationReport) })
        {
            var pin = target.Kind == ChatCitationKind.ValidationReport ? f.Conversation.Validation : f.Conversation.Projection;
            var citation = ChatMetadataCodec.Citation(new(f.Key.Revision, pin, target));
            foreach (var malformed in RequiredMutations(citation, ["v", "organization", "project", "mapping", "revision", "entry", "attempt", "key", "digest", "kind"])
                .Concat(NullableMutations(citation, ["resource", "ordinal"]))
                .Append(With(citation, "resource", JsonValue.Create("not-a-whole-report-target")))
                .Append(With(citation, "ordinal", JsonValue.Create(0))))
                await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Citation, malformed, before);
            // These remain structurally valid metadata. The exact scope comparison
            // in the child guard, not scalar validation, must refuse each mismatch.
            foreach (var field in new[] { "organization", "project", "mapping", "revision" })
                await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Citation,
                    With(citation, field, JsonValue.Create(Guid.NewGuid())), before, structurallyValid: true);
        }
        var diagnostic = ChatMetadataCodec.Citation(ChatValidatedCitation.Validate(f.Conversation, f.VerifiedValidation,
            new(ChatCitationKind.ValidationDiagnostic, diagnosticOrdinal: 0)).Facts);
        foreach (var malformed in RequiredMutations(diagnostic, ["ordinal"]))
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Citation, malformed, before);
        var fieldShape = ChatMetadataCodec.Citation(new(f.Key.Revision, f.Conversation.Projection, new(ChatCitationKind.Field, "synthetic-field")));
        // This checks the conditional resource representation, not node existence;
        // production proof admission still requires an exact verified report.
        foreach (var malformed in RequiredMutations(fieldShape, ["resource"]))
            await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.Citation, malformed, before);

        var controls = new (ChatEventKind Kind, string Metadata)[]
        {
            (ChatEventKind.Usage, unknown), (ChatEventKind.Usage, partial), (ChatEventKind.ProviderFailure, failure),
            (ChatEventKind.Citation, ChatMetadataCodec.Citation(new(f.Key.Revision, f.Conversation.Projection, new(ChatCitationKind.ProjectionReport)))),
            (ChatEventKind.Citation, ChatMetadataCodec.Citation(new(f.Key.Revision, f.Conversation.Validation, new(ChatCitationKind.ValidationReport)))),
            (ChatEventKind.Citation, diagnostic)
        };
        foreach (var control in controls)
        {
            await ScalarAsync(f, round, control.Kind, control.Metadata, expected: true);
            await PublishAsync(f, turn.Intent.TurnId, round, control.Kind, control.Metadata);
        }
        await using var reopened = new PostgresChatDatabase(f.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var events = await new PostgresChatStore(reopened).ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100);
        Assert.Equal(controls.Length + 1, events.Count);
        Assert.False(events[1].Usage!.IsKnown); Assert.Null(events[1].Usage!.TotalTokens);
        Assert.True(events[2].Usage!.IsKnown); Assert.Equal(0L, events[2].Usage!.InputTokens); Assert.Null(events[2].Usage!.TotalTokens);
        Assert.Equal(ChatProviderFailureCode.TruncatedResponse, events[3].ProviderFailure!.Code);
        Assert.Equal(ChatCitationKind.ProjectionReport, events[4].Citation!.Target.Kind);
        Assert.Equal(ChatCitationKind.ValidationReport, events[5].Citation!.Target.Kind);
        Assert.Equal(0, events[6].Citation!.Target.DiagnosticOrdinal);
    }

    [PostgresFact]
    public async Task NestedCompletionAndTerminalMetadataRejectMalformedFieldsWithoutChangingAccountingOrHistory()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await StartAsync(f); var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var usage = new ChatUsageFact(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, null, 7, null);
        var failure = new ChatProviderFailureFact(round.RoundId, ChatProviderFailureCode.TruncatedResponse, ChatDispatchObservation.Attempted);
        var terminal = new ChatTerminalMetadata(usage, failure);
        var metadata = ChatMetadataCodec.Terminal(terminal);
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        var malformed = RequiredMutations(metadata, ["v"]).Concat(NullableMutations(metadata, ["usage", "failure", "finish"]))
            .Concat(RequiredMutations(ChatMetadataCodec.Usage(usage), ["v", "round", "origin", "known"])
                .Concat(NullableMutations(ChatMetadataCodec.Usage(usage), ["input", "output", "total"]))
                .Select(child => With(metadata, "usage", JsonNode.Parse(child))))
            .Concat(RequiredMutations(ChatMetadataCodec.Failure(failure), ["v", "round", "code", "dispatch"])
                .Select(child => With(metadata, "failure", JsonNode.Parse(child))));
        foreach (var kind in new[] { ChatEventKind.RoundCompleted, ChatEventKind.Failed })
            foreach (var invalid in malformed)
                await RefuseAsync(f, turn.Intent.TurnId, round, kind, invalid, before);
        // Finish is optional/null, but a present finish must be a numeric closed
        // value. A numeric string was previously coercible in PL/pgSQL.
        var stopped = ChatMetadataCodec.Terminal(new(usage, finishReason: ChatProviderFinishReason.Stop));
        await RefuseAsync(f, turn.Intent.TurnId, round, ChatEventKind.RoundCompleted,
            With(stopped, "finish", JsonValue.Create("1")), before);
        // Scalar shape controls only; completed-vs-failed transition restrictions
        // remain independently enforced by the child guard.
        foreach (var valid in new[] { metadata, stopped, ChatMetadataCodec.Terminal(new(usage)), ChatMetadataCodec.Terminal(new(failure: failure)) })
            foreach (var kind in new[] { ChatEventKind.Completed, ChatEventKind.Failed, ChatEventKind.Cancelled, ChatEventKind.Interrupted, ChatEventKind.RoundCompleted })
                await ScalarAsync(f, round, kind, valid, expected: true);

        await PublishAsync(f, turn.Intent.TurnId, round, ChatEventKind.RoundCompleted, metadata);
        await PublishAsync(f, turn.Intent.TurnId, round, ChatEventKind.Failed, metadata);
        await using var reopened = new PostgresChatDatabase(f.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var store = new PostgresChatStore(reopened);
        var events = await store.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100);
        Assert.Equal(new[] { ChatEventKind.Started, ChatEventKind.RoundCompleted, ChatEventKind.Failed }, events.Select(e => e.Kind));
        Assert.Equal(terminal, events[1].TerminalMetadata); Assert.Equal(terminal, events[2].TerminalMetadata);
        var facts = Assert.IsType<ChatTurnFacts>(await store.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(4L, facts.Version); Assert.Equal(3, facts.EventCount); Assert.Equal(Encoding.UTF8.GetByteCount(metadata), facts.EventBytes);
        Assert.Equal(terminal, (await store.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId))!.Metadata);
    }

    private static async Task<ChatTurnFacts> StartAsync(PostgresChatTestSupport f)
    {
        var prototype = await f.IntentAsync(budget: new(64, 1048576, 3));
        // Explicitly longer fixture-only persistence window for the bounded SQL
        // mutation matrix. No provider lease/time budget is opened or restarted.
        return await f.StartAsync(new(prototype.TurnId, prototype.Envelope, prototype.Profile, prototype.Budget, prototype.DeadlineUtc.AddMinutes(9)));
    }

    private static IEnumerable<string> RequiredMutations(string valid, string[] fields)
    {
        foreach (var field in fields)
        {
            var node = JsonNode.Parse(valid)!.AsObject(); node.Remove(field); yield return node.ToJsonString();
            yield return With(valid, field, null);
            var value = JsonNode.Parse(valid)![field]!;
            yield return With(valid, field, value.GetValueKind() == JsonValueKind.String ? JsonValue.Create(123) : JsonValue.Create(value.ToJsonString()));
        }
    }
    private static IEnumerable<string> NullableMutations(string valid, string[] fields)
    {
        foreach (var field in fields)
        {
            var node = JsonNode.Parse(valid)!.AsObject(); node.Remove(field); yield return node.ToJsonString();
            yield return With(valid, field, new JsonArray());
        }
    }
    private static string With(string valid, string field, JsonNode? value)
    {
        var node = JsonNode.Parse(valid)!.AsObject(); node[field] = value; return node.ToJsonString();
    }

    private static async Task ScalarAsync(PostgresChatTestSupport f, ChatRoundIntent round, ChatEventKind kind, string metadata, bool expected)
    {
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT weft.chat_metadata_valid($1,$2,$3)", connection);
        command.Parameters.AddWithValue((int)kind); command.Parameters.AddWithValue(metadata); command.Parameters.AddWithValue(round.RoundId);
        // SQL NULL is not an acceptable refusal/result from this predicate.
        Assert.Equal(expected, Assert.IsType<bool>(await command.ExecuteScalarAsync()));
    }
    private static async Task RefuseAsync(PostgresChatTestSupport f, Guid turn, ChatRoundIntent round, ChatEventKind kind,
        string metadata, byte[] before, bool structurallyValid = false)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => PublishAsync(f, turn, round, kind, metadata));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await ScalarAsync(f, round, kind, metadata, structurallyValid);
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }
    private static async Task PublishAsync(PostgresChatTestSupport f, Guid turn, ChatRoundIntent round, ChatEventKind kind, string metadata)
    {
        var terminal = kind == ChatEventKind.Failed;
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
        await using (var advance = new NpgsqlCommand(terminal
            ? "UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,phase=3,finished_at=statement_timestamp() WHERE turn_id=$1"
            : "UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+$2 WHERE turn_id=$1", connection, transaction))
        {
            advance.Parameters.AddWithValue(turn); if (!terminal) advance.Parameters.AddWithValue(Encoding.UTF8.GetByteCount(metadata));
            Assert.Equal(1, await advance.ExecuteNonQueryAsync());
        }
        await using (var append = new NpgsqlCommand("""
            INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence,metadata_json)
            SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,$3,'',
                CASE WHEN $4 THEN NULL ELSE $5 END,CASE WHEN $4 THEN NULL ELSE $6 END,$7 FROM weft.chat_turns WHERE turn_id=$1
            """, connection, transaction))
        {
            append.Parameters.AddWithValue(turn); append.Parameters.AddWithValue(Guid.NewGuid()); append.Parameters.AddWithValue((int)kind);
            append.Parameters.AddWithValue(terminal); append.Parameters.AddWithValue(round.RoundId); append.Parameters.AddWithValue(round.Fence);
            append.Parameters.AddWithValue(metadata); Assert.Equal(1, await append.ExecuteNonQueryAsync());
        }
        if (kind == ChatEventKind.RoundCompleted)
        {
            await using var complete = new NpgsqlCommand("UPDATE weft.chat_rounds SET outcome=4,finished_at=statement_timestamp(),result_metadata=$2 WHERE round_id=$1", connection, transaction);
            complete.Parameters.AddWithValue(round.RoundId); complete.Parameters.AddWithValue(metadata); Assert.Equal(1, await complete.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }
}
