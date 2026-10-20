using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Source qualification of direct credentials, not application-port-only checks.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatScopeAdmissionRegressionTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ChangedClearedOrMalformedFinalScopeCannotHideDeferredConservationFailures()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        var original = await PostgresChatSecurityTests.RowsAsync(f);
        var org = f.Key.Revision.OrganizationId.ToString("D");
        var creator = f.Actor.PrincipalId.Value.ToString("D");
        var scopes = new[]
        {
            (Org: org, Creator: creator), // Unchanged-scope negative control.
            (Org: org, Creator: Guid.NewGuid().ToString("D")),
            (Org: Guid.NewGuid().ToString("D"), Creator: creator),
            (Org: "", Creator: ""),
            (Org: "malformed-final-organization", Creator: "malformed-final-creator")
        };
        foreach (var scope in scopes)
            foreach (var immediate in new[] { false, true })
                foreach (var assignment in new[]
                {
                    "version=version+1", "version=version+1,round_count=round_count+1",
                    "version=version+1,event_count=event_count+1,event_bytes=event_bytes+1"
                })
                    await RefuseAsync("UPDATE weft.chat_turns SET " + assignment + " WHERE turn_id=$1", scope, immediate);
        PostgresChatSecurityTests.Same(original, await PostgresChatSecurityTests.RowsAsync(f));

        var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied,
            (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var reserved = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var scope in scopes)
            foreach (var immediate in new[] { false, true })
                await RefuseAsync("UPDATE weft.chat_rounds SET outcome=3,finished_at=statement_timestamp() WHERE turn_id=$1", scope, immediate);
        PostgresChatSecurityTests.Same(reserved, await PostgresChatSecurityTests.RowsAsync(f));
        Assert.Equal(ChatRoundOutcome.Uncertain,
            (await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId))!.Outcome);

        async Task RefuseAsync(string sql, (string Org, string Creator) scope, bool immediate)
        {
            await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
            await using (var mutate = new NpgsqlCommand(sql, connection, transaction))
            {
                mutate.Parameters.AddWithValue(turn.Intent.TurnId);
                Assert.Equal(1, await mutate.ExecuteNonQueryAsync()); // Prove a real mutation, not an RLS no-op.
            }
            await using (var changeScope = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config('weft.chat_principal_id',$2,true)", connection, transaction))
            {
                changeScope.Parameters.AddWithValue(scope.Org); changeScope.Parameters.AddWithValue(scope.Creator);
                await changeScope.ExecuteNonQueryAsync();
            }
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                if (immediate)
                {
                    await using var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction);
                    await flush.ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            });
            Assert.True(error.SqlState == PostgresErrorCodes.CheckViolation,
                "Deferred chat conservation was hidden by final scope or failed for a different category.");
        }
    }

    [PostgresFact]
    public async Task ValidEventCompletionAndTerminalConserveAcrossScopeChangesAndRestoreIncomingSettings()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied,
            (await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        await using (var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
            await using (var advance = new NpgsqlCommand("UPDATE weft.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+1 WHERE turn_id=$1", connection, transaction))
            { advance.Parameters.AddWithValue(turn.Intent.TurnId); Assert.Equal(1, await advance.ExecuteNonQueryAsync()); }
            await using (var append = new NpgsqlCommand("""
                INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence)
                SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,2,'x',$3,$4 FROM weft.chat_turns WHERE turn_id=$1
                """, connection, transaction))
            {
                append.Parameters.AddWithValue(turn.Intent.TurnId); append.Parameters.AddWithValue(Guid.NewGuid());
                append.Parameters.AddWithValue(round.RoundId); append.Parameters.AddWithValue(round.Fence);
                Assert.Equal(1, await append.ExecuteNonQueryAsync());
            }
            await using (var advance = new NpgsqlCommand("UPDATE weft.chat_turns SET version=version+1 WHERE turn_id=$1", connection, transaction))
            { advance.Parameters.AddWithValue(turn.Intent.TurnId); Assert.Equal(1, await advance.ExecuteNonQueryAsync()); }
            await using (var complete = new NpgsqlCommand("UPDATE weft.chat_rounds SET outcome=3,finished_at=statement_timestamp() WHERE turn_id=$1", connection, transaction))
            { complete.Parameters.AddWithValue(turn.Intent.TurnId); Assert.Equal(1, await complete.ExecuteNonQueryAsync()); }
            await using (var scope = new NpgsqlCommand("""
                SELECT set_config('weft.organization_id','malformed-final-organization',true),
                    set_config('weft.chat_principal_id','malformed-final-creator',true)
                """, connection, transaction)) await scope.ExecuteNonQueryAsync();
            await using (var flush = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction)) await flush.ExecuteNonQueryAsync();
            // Every queued invocation must restore the caller's exact final values,
            // not leave its per-row scope bound after an explicit constraint flush.
            await using (var restored = new NpgsqlCommand("""
                SELECT current_setting('weft.organization_id')='malformed-final-organization'
                    AND current_setting('weft.chat_principal_id')='malformed-final-creator'
                    AND current_user='weft_chat_writer' AND session_user=current_user
                    AND NOT has_function_privilege(current_user,'weft.check_chat_counters()','EXECUTE')
                """, connection, transaction)) Assert.True(Assert.IsType<bool>(await restored.ExecuteScalarAsync()));
            await transaction.CommitAsync();
        }
        var completedRound = await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId);
        Assert.Equal(ChatRoundOutcome.Stopped, completedRound!.Outcome);
        var facts = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(4, facts.Version); Assert.Equal(2, facts.EventCount); Assert.Equal(1, facts.EventBytes);
        await using (var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
            await using (var finish = new NpgsqlCommand("UPDATE weft.chat_turns SET version=version+1,phase=2,finished_at=statement_timestamp(),event_count=event_count+1 WHERE turn_id=$1", connection, transaction))
            { finish.Parameters.AddWithValue(turn.Intent.TurnId); Assert.Equal(1, await finish.ExecuteNonQueryAsync()); }
            await using (var terminal = new NpgsqlCommand("""
                INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence)
                SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,5,'',NULL,NULL FROM weft.chat_turns WHERE turn_id=$1
                """, connection, transaction))
            { terminal.Parameters.AddWithValue(turn.Intent.TurnId); terminal.Parameters.AddWithValue(Guid.NewGuid()); Assert.Equal(1, await terminal.ExecuteNonQueryAsync()); }
            await using (var scope = new NpgsqlCommand("SELECT set_config('weft.organization_id','',true),set_config('weft.chat_principal_id','',true)", connection, transaction)) await scope.ExecuteNonQueryAsync();
            await transaction.CommitAsync(); // Separate COMMIT-time positive, without an explicit flush.
        }
        facts = Assert.IsType<ChatTurnFacts>(await f.Reader.ReadTurnAsync(f.Actor, f.Key, turn.Intent.TurnId));
        Assert.Equal(ChatTurnPhase.Completed, facts.Phase); Assert.Equal(5, facts.Version);
        Assert.Equal(3, facts.EventCount); Assert.Equal(1, facts.EventBytes);
        Assert.Equal(3, (await f.Reader.ReadEventsAsync(f.Actor, f.Key, turn.Intent.TurnId, 0, 100)).Count);
    }

    [PostgresFact]
    public async Task ActualGuardedRetentionCascadeStillAllowsQueuedChecksOfTrulyErasedTurns()
    {
        // Only genuine content erasure with durable identity protection may take
        // the missing-turn branch, even with queued close and tombstone checks.
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture, TimeSpan.FromSeconds(10));
        var turn = await f.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (new DateTimeOffset(await f.Case.ScalarAsync<DateTime>("SELECT statement_timestamp()")) < f.Conversation.RetainUntilUtc)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        await using var connection = PostgresChatSecurityTests.Open(f.Case.ChatWriterConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token);
        await PostgresChatSecurityTests.BindAsync(connection, transaction, f);
        await using (var close = new NpgsqlCommand("UPDATE weft.chat_turns SET version=version+1,phase=4,finished_at=statement_timestamp(),event_count=event_count+1 WHERE turn_id=$1", connection, transaction))
        { close.Parameters.AddWithValue(turn.Intent.TurnId); Assert.Equal(1, await close.ExecuteNonQueryAsync(deadline.Token)); }
        await using (var terminal = new NpgsqlCommand("""
            INSERT INTO weft.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence)
            SELECT organization_id,creator_id,conversation_id,turn_id,event_count,$2,7,'',NULL,NULL FROM weft.chat_turns WHERE turn_id=$1
            """, connection, transaction))
        { terminal.Parameters.AddWithValue(turn.Intent.TurnId); terminal.Parameters.AddWithValue(Guid.NewGuid()); Assert.Equal(1, await terminal.ExecuteNonQueryAsync(deadline.Token)); }
        await using (var erase = new NpgsqlCommand("SELECT weft.chat_erase_expired($1,$2,$3,$4,$5,$6,$7,$8,$9)", connection, transaction))
        {
            erase.Parameters.AddWithValue(f.Key.Revision.OrganizationId); erase.Parameters.AddWithValue(f.Key.Creator.Value);
            erase.Parameters.AddWithValue(f.Key.ConversationId); erase.Parameters.AddWithValue(f.Key.Revision.ProjectId);
            erase.Parameters.AddWithValue(f.Key.Revision.MappingId); erase.Parameters.AddWithValue(f.Key.Revision.RevisionId);
            erase.Parameters.AddWithValue(f.ErasurePolicy.PolicyId); erase.Parameters.AddWithValue(f.ErasurePolicy.Version);
            erase.Parameters.AddWithValue(f.ErasurePolicy.IdentityRetirementNotBeforeUtc);
            Assert.Equal("Applied", await erase.ExecuteScalarAsync(deadline.Token));
        }
        await using (var scope = new NpgsqlCommand("SELECT set_config('weft.organization_id','',true),set_config('weft.chat_principal_id','',true)", connection, transaction)) await scope.ExecuteNonQueryAsync(deadline.Token);
        await transaction.CommitAsync(deadline.Token);
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_turns"));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_events"));
        Assert.Equal(3L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM weft.chat_identities WHERE erased_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task FunctionLocalScopeConfigurationIsPartOfExactFreshAdmission()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        foreach (var parameter in new[] { "weft.organization_id", "weft.chat_principal_id" })
        {
            await f.Case.ExecuteAsync("ALTER FUNCTION weft.check_chat_counters() RESET " + parameter);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.CreateAsync(f.Actor, f.Conversation));
            }
            finally
            {
                // Restore the exact reviewed source order, not a live definition.
                await f.Case.ExecuteAsync("""
                    ALTER FUNCTION weft.check_chat_counters() RESET ALL;
                    ALTER FUNCTION weft.check_chat_counters() SET search_path=pg_catalog,weft,pg_temp;
                    ALTER FUNCTION weft.check_chat_counters() SET weft.organization_id='';
                    ALTER FUNCTION weft.check_chat_counters() SET weft.chat_principal_id=''
                    """);
            }
            Assert.True(await f.Reader.ReadAsync(f.Actor, f.Key) == f.Conversation);
        }
    }

    [PostgresFact]
    public async Task OrdinaryPgxNamespaceAndUnexpectedReaderOrWriterRightsAreNeverExcludedAsSystemObjects()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        await f.Case.ExecuteAsync("CREATE SCHEMA pgx; CREATE TABLE pgx.chat_admission_probe(id integer); INSERT INTO pgx.chat_admission_probe VALUES(1)");
        try
        {
            Assert.True(await f.Reader.ReadAsync(f.Actor, f.Key) == f.Conversation); // Inaccessible schema is a positive control.
            foreach (var role in new[] { "weft_chat_reader", "weft_chat_writer" })
                foreach (var right in new[] { "SELECT(id)", "SELECT" })
                {
                    await f.Case.ExecuteAsync($"GRANT USAGE ON SCHEMA pgx TO PUBLIC; GRANT {right} ON pgx.chat_admission_probe TO {role}");
                    try
                    {
                        Assert.True(await f.Case.ScalarAsync<bool>("SELECT has_schema_privilege($1,'pgx','USAGE') AND has_column_privilege($1,'pgx.chat_admission_probe','id','SELECT')", role));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.StartAsync(f.Actor, f.Key, turn.Intent));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled));
                        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
                    }
                    finally { await f.Case.ExecuteAsync($"REVOKE SELECT(id) ON pgx.chat_admission_probe FROM {role}; REVOKE SELECT ON pgx.chat_admission_probe FROM {role}; REVOKE USAGE ON SCHEMA pgx FROM PUBLIC"); }
                    Assert.True(await f.Reader.ReadAsync(f.Actor, f.Key) == f.Conversation);
                    Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.StartAsync(f.Actor, f.Key, turn.Intent)).Outcome);
                }
        }
        finally { await f.Case.ExecuteAsync("DROP TABLE pgx.chat_admission_probe; DROP SCHEMA pgx"); }
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }

    [PostgresFact]
    public async Task EquivalentSessionGuardAlsoRejectsPgxColumnAndTableRightsWithPublicNamespaceUsage()
    {
        await using var f = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await f.CreateAsync();
        await f.Case.ExecuteAsync("CREATE SCHEMA pgx; CREATE TABLE pgx.session_admission_probe(id integer); GRANT USAGE ON SCHEMA pgx TO PUBLIC");
        try
        {
            foreach (var role in new[] { "weft_session_reader", "weft_session_writer" })
                foreach (var right in new[] { "SELECT(id)", "SELECT" })
                {
                    await f.Case.ExecuteAsync($"GRANT {right} ON pgx.session_admission_probe TO {role}");
                    try
                    {
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ObserveAsync(facts.Key));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.RevokeAsync(facts.Key));
                    }
                    finally { await f.Case.ExecuteAsync($"REVOKE SELECT(id) ON pgx.session_admission_probe FROM {role}; REVOKE SELECT ON pgx.session_admission_probe FROM {role}"); }
                    await f.CurrentAsync(facts);
                }
        }
        finally { await f.Case.ExecuteAsync("DROP TABLE pgx.session_admission_probe; DROP SCHEMA pgx"); }
    }

    [PostgresFact]
    public async Task TemporarySchemaExceptionsRequireLiteralUnderscoresInEquivalentRuntimeAdmission()
    {
        await using var f = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await f.CreateAsync();
        foreach (var schema in new[] { "pgxtempx", "pgxtoastxtempx" })
        {
            await f.Case.ExecuteAsync("CREATE SCHEMA " + schema);
            try
            {
                foreach (var role in new[] { "weft_session_reader", "weft_session_writer" })
                {
                    await f.Case.ExecuteAsync($"GRANT CREATE ON SCHEMA {schema} TO {role}");
                    try
                    {
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ObserveAsync(facts.Key));
                        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.RevokeAsync(facts.Key));
                    }
                    finally { await f.Case.ExecuteAsync($"REVOKE CREATE ON SCHEMA {schema} FROM {role}"); }
                    await f.CurrentAsync(facts);
                }
            }
            finally { await f.Case.ExecuteAsync("DROP SCHEMA " + schema); }
        }
    }
}
