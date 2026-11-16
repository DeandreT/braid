using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatSecurityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ExactProfilesHaveOnlySourceDeclaredRightsAndNoOtherProfileReadsPrivateChat()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        await f.StartAsync();
        foreach (var profile in new[] { ChatDatabaseProfile.Reader, ChatDatabaseProfile.Writer })
        {
            await using var connection = Open(Route(f, profile));
            await connection.OpenAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT r.relname::text || '|' || a.attname::text || '|' || x.privilege_type
                FROM pg_catalog.pg_class r JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
                JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
                CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) x JOIN pg_catalog.pg_roles p ON p.oid=x.grantee
                WHERE n.nspname='braid' AND p.rolname=current_user
                ORDER BY 1
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                var actual = new List<string>();
                while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
                Assert.True(actual.SequenceEqual(ChatSchemaContract.ColumnGrants(profile).Order(StringComparer.Ordinal)),
                    "The chat profile is not its exact source-declared column profile.");
            }
            foreach (var sql in new[]
            {
                "SELECT original_request FROM braid.chat_turns", "SELECT private_text FROM braid.chat_events"
            })
            {
                await using var invisible = new NpgsqlCommand(sql, connection);
                await using var rows = await invisible.ExecuteReaderAsync();
                Assert.False(await rows.ReadAsync());
            }
            foreach (var sql in new[]
            {
                "SELECT protected_ticket FROM braid.server_sessions", "SELECT subject FROM braid.external_identity_bindings",
                "SELECT native_digest FROM braid.mapping_revisions", "SELECT name FROM braid.projects",
                "SELECT principal_id FROM braid.project_payload_read_grants", "SELECT applied_at FROM braid.schema_migrations",
                "UPDATE braid.principals SET active=false", "UPDATE braid.project_memberships SET active=false",
                "SELECT id FROM braid.principals FOR SHARE", "SELECT principal_id FROM braid.project_memberships FOR UPDATE",
                "TRUNCATE braid.chat_conversations", "DELETE FROM braid.chat_events", "UPDATE braid.chat_events SET private_text='rewrite'",
                "SELECT braid.guard_chat_child()", "UPDATE braid.schema_migrations SET checksum=repeat('a',64)"
            }) await RefusedSqlAsync(f, Route(f, profile), sql, PostgresErrorCodes.InsufficientPrivilege);
        }
        foreach (var route in new[] { f.Case.RuntimeConnectionString, f.Case.CatalogConnectionString, f.Case.MetadataConnectionString,
            f.Case.IngestionConnectionString, f.Case.ProjectCreationConnectionString, f.Case.ReportConnectionString, f.Case.DerivationConnectionString,
            f.Case.MaintenanceConnectionString, f.Case.IdentityConnectionString, f.Case.PayloadPermissionConnectionString,
            f.Case.SessionReaderConnectionString, f.Case.SessionWriterConnectionString })
            await RefusedSqlAsync(f, route, "SELECT private_text FROM braid.chat_events", PostgresErrorCodes.InsufficientPrivilege);
        await RefusedSqlAsync(f, f.Case.ChatReaderConnectionString, "UPDATE braid.chat_turns SET version=version+1", PostgresErrorCodes.InsufficientPrivilege);
    }

    [PostgresFact]
    public async Task EitherBroadenedSiblingProfileRefusesFreshReadWriteAndReplayWithoutChangingPrivateRows()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        foreach (var role in new[] { "braid_chat_reader", "braid_chat_writer" })
        {
            var turnReads = string.Join(',', ChatSchemaContract.ColumnGrants(role == "braid_chat_reader" ? ChatDatabaseProfile.Reader : ChatDatabaseProfile.Writer)
                .Select(g => g.Split('|')).Where(g => g[0] == "chat_turns" && g[2] == "SELECT").Select(g => g[1]));
            foreach (var (change, restore) in new[]
            {
                ($"ALTER ROLE {role} INHERIT", $"ALTER ROLE {role} NOINHERIT"),
                ($"ALTER ROLE {role} BYPASSRLS", $"ALTER ROLE {role} NOBYPASSRLS"),
                ($"ALTER ROLE {role} NOLOGIN", $"ALTER ROLE {role} LOGIN"),
                ($"GRANT pg_read_all_data TO {role}", $"REVOKE pg_read_all_data FROM {role}"),
                ($"ALTER ROLE {role} SET work_mem='8MB'", $"ALTER ROLE {role} RESET work_mem")
            }) await DriftAsync(f, turn, change, restore, bootstrap: true);
            foreach (var (change, restore) in new[]
            {
                ($"GRANT SELECT ON braid.chat_turns TO {role}", $"REVOKE SELECT ON braid.chat_turns FROM {role}; GRANT SELECT({turnReads}) ON braid.chat_turns TO {role}"),
                ($"GRANT SELECT(native_digest) ON braid.mapping_revisions TO {role}", $"REVOKE SELECT(native_digest) ON braid.mapping_revisions FROM {role}"),
                ($"REVOKE SELECT(original_request) ON braid.chat_turns FROM {role}", $"GRANT SELECT(original_request) ON braid.chat_turns TO {role}"),
                ($"GRANT SELECT(original_request) ON braid.chat_turns TO {role} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT(original_request) ON braid.chat_turns FROM {role}"),
                ($"GRANT EXECUTE ON FUNCTION braid.guard_chat_child() TO {role}", $"REVOKE EXECUTE ON FUNCTION braid.guard_chat_child() FROM {role}"),
                ($"GRANT CREATE ON SCHEMA braid TO {role}", $"REVOKE CREATE ON SCHEMA braid FROM {role}"),
                ($"ALTER DEFAULT PRIVILEGES IN SCHEMA braid GRANT SELECT ON TABLES TO {role}", $"ALTER DEFAULT PRIVILEGES IN SCHEMA braid REVOKE SELECT ON TABLES FROM {role}")
            }) await DriftAsync(f, turn, change, restore);
        }
        await DriftAsync(f, turn, "GRANT SELECT(private_text) ON braid.chat_events TO PUBLIC", "REVOKE SELECT(private_text) ON braid.chat_events FROM PUBLIC");
        await DriftAsync(f, turn, "GRANT SELECT(private_text) ON braid.chat_events TO braid_session_reader", "REVOKE SELECT(private_text) ON braid.chat_events FROM braid_session_reader");
        await DriftAsync(f, turn, "GRANT SET ON PARAMETER session_replication_role TO braid_chat_writer", "REVOKE SET ON PARAMETER session_replication_role FROM braid_chat_writer", bootstrap: true);
        await DriftAsync(f, turn, $"GRANT TEMPORARY ON DATABASE {f.Case.Name} TO braid_chat_reader", $"REVOKE TEMPORARY ON DATABASE {f.Case.Name} FROM braid_chat_reader", bootstrap: true);
        await DriftAsync(f, turn, $"GRANT CONNECT ON DATABASE {f.Case.Name} TO braid_chat_writer WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {f.Case.Name} FROM braid_chat_writer", bootstrap: true);
    }

    [PostgresFact]
    public async Task SchemaPoliciesFunctionsChecksIndexesAndOrdinaryAndDeferredTriggersAreNotAdopted()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE braid.chat_turns DISABLE ROW LEVEL SECURITY", "ALTER TABLE braid.chat_turns ENABLE ROW LEVEL SECURITY"),
            ("CREATE POLICY chat_unknown_policy ON braid.chat_events USING(true) WITH CHECK(true)", "DROP POLICY chat_unknown_policy ON braid.chat_events"),
            ("ALTER POLICY creator_scope ON braid.chat_turns USING(true) WITH CHECK(true)",
                "ALTER POLICY creator_scope ON braid.chat_turns USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid) WITH CHECK(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid AND creator_id=NULLIF(current_setting('braid.chat_principal_id',true),'')::uuid)"),
            ("ALTER TABLE braid.chat_events DISABLE TRIGGER chat_child_guard", "ALTER TABLE braid.chat_events ENABLE TRIGGER chat_child_guard"),
            ("ALTER TABLE braid.chat_rounds DISABLE TRIGGER chat_counters", "ALTER TABLE braid.chat_rounds ENABLE TRIGGER chat_counters"),
            ("ALTER TABLE braid.chat_turns ENABLE REPLICA TRIGGER chat_counters", "ALTER TABLE braid.chat_turns ENABLE TRIGGER chat_counters"),
            ("ALTER FUNCTION braid.chat_authorized(uuid,uuid,uuid) VOLATILE", "ALTER FUNCTION braid.chat_authorized(uuid,uuid,uuid) STABLE"),
            ("ALTER FUNCTION braid.guard_chat_child() SECURITY DEFINER", "ALTER FUNCTION braid.guard_chat_child() SECURITY INVOKER"),
            ("ALTER FUNCTION braid.check_chat_counters() SET search_path=public", "ALTER FUNCTION braid.check_chat_counters() SET search_path=pg_catalog,braid,pg_temp"),
            ("CREATE FUNCTION braid.chat_authorized(uuid) RETURNS boolean LANGUAGE sql AS 'SELECT true'; REVOKE ALL ON FUNCTION braid.chat_authorized(uuid) FROM PUBLIC", "DROP FUNCTION braid.chat_authorized(uuid)"),
            ("ALTER TABLE braid.chat_turns RENAME COLUMN version TO chat_unknown_version", "ALTER TABLE braid.chat_turns RENAME COLUMN chat_unknown_version TO version"),
            ("ALTER TABLE braid.chat_turns ADD CONSTRAINT chat_unknown_check CHECK(version>0)", "ALTER TABLE braid.chat_turns DROP CONSTRAINT chat_unknown_check"),
            ("ALTER TABLE braid.chat_conversations DROP CONSTRAINT chat_conversations_projection_fkey; ALTER TABLE braid.chat_conversations ADD CONSTRAINT chat_conversations_projection_fkey FOREIGN KEY(organization_id,project_id,mapping_id,revision_id,projection_id) REFERENCES braid.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,derivation_id) ON DELETE RESTRICT ON UPDATE RESTRICT NOT VALID",
                "ALTER TABLE braid.chat_conversations VALIDATE CONSTRAINT chat_conversations_projection_fkey"),
            ("ALTER TABLE braid.chat_events DROP CONSTRAINT chat_events_pkey; ALTER TABLE braid.chat_events ADD CONSTRAINT chat_events_pkey PRIMARY KEY(organization_id,creator_id,conversation_id,turn_id,event_id)",
                "ALTER TABLE braid.chat_events DROP CONSTRAINT chat_events_pkey; ALTER TABLE braid.chat_events ADD CONSTRAINT chat_events_pkey PRIMARY KEY(organization_id,creator_id,conversation_id,turn_id,sequence)"),
            ("CREATE INDEX chat_unknown_index ON braid.chat_turns(turn_id)", "DROP INDEX braid.chat_unknown_index"),
            ("DROP INDEX braid.chat_one_active_turn; CREATE UNIQUE INDEX chat_one_active_turn ON braid.chat_turns(organization_id,creator_id,conversation_id) WHERE phase IN (1,2)",
                "DROP INDEX braid.chat_one_active_turn; CREATE UNIQUE INDEX chat_one_active_turn ON braid.chat_turns(organization_id,creator_id,conversation_id) WHERE phase=1"),
            ("ALTER INDEX braid.chat_one_active_turn RENAME TO chat_unknown_index", "ALTER INDEX braid.chat_unknown_index RENAME TO chat_one_active_turn"),
            ("ALTER TABLE braid.chat_events ALTER COLUMN private_text SET DEFAULT ''", "ALTER TABLE braid.chat_events ALTER COLUMN private_text DROP DEFAULT")
        }) await DriftAsync(f, turn, change, restore);
        await DriftAsync(f, turn, "ALTER TABLE braid.chat_conversations DISABLE TRIGGER ALL", "ALTER TABLE braid.chat_conversations ENABLE TRIGGER ALL", bootstrap: true);
        var originalBody = await f.Case.ScalarAsync<string>("SELECT pg_catalog.pg_get_functiondef('braid.guard_chat_child()'::regprocedure)");
        await DriftAsync(f, turn,
            "CREATE OR REPLACE FUNCTION braid.guard_chat_child() RETURNS trigger LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY INVOKER SET search_path=pg_catalog,braid,pg_temp AS 'BEGIN RETURN NEW; END;'",
            originalBody);
        await using var hole = await PostgresChatTestSupport.CreateAsync(fixture);
        var other = await hole.StartAsync();
        await hole.Case.ExecuteAsync("ALTER TABLE braid.chat_turns ADD COLUMN chat_unknown_attribute integer");
        await RefusedPortsAsync(hole, other);
        await hole.Case.ExecuteAsync("ALTER TABLE braid.chat_turns DROP COLUMN chat_unknown_attribute");
        await RefusedPortsAsync(hole, other); // A dropped attnum is not pristine restoration.
    }

    [PostgresFact]
    public async Task DirectRestrictedSqlCannotCommitVersionOnlyCountersOrRoundCompletionWithoutItsVersionStep()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        var before = await RowsAsync(f);
        foreach (var assignment in new[] { "version=version+1", "version=version+1,round_count=round_count+1", "version=version+1,event_count=event_count+1,event_bytes=event_bytes+1" })
            await RefusedSqlAsync(f, f.Case.ChatWriterConnectionString, "UPDATE braid.chat_turns SET " + assignment, PostgresErrorCodes.CheckViolation);
        Same(before, await RowsAsync(f));
        var round = PostgresChatTestSupport.Round();
        var reserved = await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round);
        Assert.Equal(ChatStoreOutcome.Applied, reserved.Outcome);
        turn = Assert.IsType<ChatTurnFacts>(reserved.Turn);
        before = await RowsAsync(f);
        await RefusedSqlAsync(f, f.Case.ChatWriterConnectionString,
            "UPDATE braid.chat_rounds SET outcome=3,finished_at=statement_timestamp()", PostgresErrorCodes.CheckViolation);
        await RefusedSqlAsync(f, f.Case.ChatWriterConnectionString,
            "UPDATE braid.chat_turns SET version=version+1; UPDATE braid.chat_rounds SET outcome=3,finished_at=statement_timestamp(); UPDATE braid.chat_turns SET version=version+1",
            PostgresErrorCodes.CheckViolation);
        await RefusedSqlAsync(f, f.Case.ChatWriterConnectionString,
            "UPDATE braid.chat_turns SET version=version+1,event_count=event_count+1,event_bytes=event_bytes+1; INSERT INTO braid.chat_events(organization_id,creator_id,conversation_id,turn_id,sequence,event_id,kind,private_text,round_id,dispatch_fence) SELECT organization_id,creator_id,conversation_id,turn_id,event_count,gen_random_uuid(),2,'x',round_id,gen_random_uuid() FROM braid.chat_turns CROSS JOIN LATERAL (SELECT round_id FROM braid.chat_rounds LIMIT 1) r",
            PostgresErrorCodes.CheckViolation);
        Same(before, await RowsAsync(f));
        // Positive command conserves exactly one completion and exactly one version.
        var finished = await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence, ChatRoundOutcome.Stopped);
        Assert.Equal(ChatStoreOutcome.Applied, finished.Outcome);
        turn = Assert.IsType<ChatTurnFacts>(finished.Facts!);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Completed)).Outcome);
        Assert.True(await f.Case.ScalarAsync<bool>("""
            SELECT bool_and(t.version=t.event_count+t.round_count+(SELECT count(*) FROM braid.chat_rounds r
                WHERE r.turn_id=t.turn_id AND r.outcome<>1)) FROM braid.chat_turns t
            """));
    }

    [PostgresFact]
    public async Task WrongOrganizationProjectAndCreatorNeverReturnPrivateFactsOrChangeRows()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        var before = await RowsAsync(f);
        foreach (var revision in new[]
        {
            new RevisionScope(Guid.NewGuid(), f.Key.PinnedRevision.ProjectId, f.Key.PinnedRevision.MappingId, f.Key.PinnedRevision.RevisionId),
            new RevisionScope(f.Key.PinnedRevision.OrganizationId, Guid.NewGuid(), f.Key.PinnedRevision.MappingId, f.Key.PinnedRevision.RevisionId)
        })
        {
            var key = new ChatConversationKey(revision, f.Actor.PrincipalId, f.Key.ConversationId);
            Assert.Null(await f.Reader.ReadAsync(f.Actor, key));
            Assert.Null(await f.Reader.ReadTurnAsync(f.Actor, key, turn.Intent.TurnId));
            Assert.Empty(await f.Reader.ReadEventsAsync(f.Actor, key, turn.Intent.TurnId, 0, 10));
            Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await f.Writer.FinishAsync(f.Actor, key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled)).Outcome);
        }
        Assert.Null(await f.Reader.ReadAsync(f.OtherActor, f.Key));
        Same(before, await RowsAsync(f));
    }

    private static async Task DriftAsync(PostgresChatTestSupport f, ChatTurnFacts turn, string change, string restore, bool bootstrap = false)
    {
        var before = await RowsAsync(f);
        if (bootstrap) await f.Case.BootstrapAsync(change); else await f.Case.ExecuteAsync(change);
        try { await RefusedPortsAsync(f, turn); Same(before, await RowsAsync(f)); }
        finally { if (bootstrap) await f.Case.BootstrapAsync(restore); else await f.Case.ExecuteAsync(restore); }
        Assert.True(await f.Reader.ReadAsync(f.Actor, f.Key) == f.Conversation);
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.StartAsync(f.Actor, f.Key, turn.Intent)).Outcome);
        Same(before, await RowsAsync(f));
    }
    private static async Task RefusedPortsAsync(PostgresChatTestSupport f, ChatTurnFacts turn)
    {
        foreach (var call in new Func<Task>[] { () => f.Reader.ReadAsync(f.Actor, f.Key), () => f.Writer.StartAsync(f.Actor, f.Key, turn.Intent),
            () => f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Cancelled) })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Null(error.InnerException);
        }
    }
    internal static NpgsqlConnection Open(string route) => new(new NpgsqlConnectionStringBuilder(route) { Pooling = false }.ConnectionString);
    private static string Route(PostgresChatTestSupport f, ChatDatabaseProfile p) => p == ChatDatabaseProfile.Reader ? f.Case.ChatReaderConnectionString : f.Case.ChatWriterConnectionString;
    internal static async Task BindAsync(NpgsqlConnection c, NpgsqlTransaction t, PostgresChatTestSupport f)
    {
        await using var bind = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true),set_config('braid.chat_principal_id',$2,true)", c, t);
        bind.Parameters.AddWithValue(f.Key.PinnedRevision.OrganizationId.ToString("D")); bind.Parameters.AddWithValue(f.Actor.PrincipalId.Value.ToString("D"));
        await bind.ExecuteNonQueryAsync();
    }
    private static async Task RefusedSqlAsync(PostgresChatTestSupport f, string route, string sql, string state)
    {
        await using var connection = Open(route); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await BindAsync(connection, transaction, f);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync(); // Deferred checks are part of the direct-writer boundary.
        });
        Assert.True(error.SqlState == state, "The restricted chat SQL refusal category changed.");
    }
    internal static async Task<byte[]> RowsAsync(PostgresChatTestSupport f)
    {
        var text = new StringBuilder();
        // Positional row values, so a refused column rename is not mistaken for a row change.
        foreach (var table in ChatSchemaContract.ChatTables) text.Append(await f.Case.ScalarAsync<string>($"SELECT coalesce(string_agg(x::text,E'\\n' ORDER BY x::text),'') FROM braid.{table} x")).Append('\n');
        return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
    }
    internal static void Same(byte[] before, byte[] after) => Assert.True(CryptographicOperations.FixedTimeEquals(before, after), "Private chat rows changed across a refused operation.");
}
