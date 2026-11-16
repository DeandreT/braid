using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionSecurityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task BothProfilesHaveExactColumnRightsAndCannotReadPrivateParentsWriteHistoryOrLockParents()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var first = await test.CreateAsync();
        await test.CreateAsync(await test.IntentAsync(test.Key(test.OtherOrganization)));
        foreach (var writer in new[] { false, true })
        {
            var route = writer ? test.Case.SessionWriterConnectionString : test.Case.SessionReaderConnectionString;
            await using var connection = PostgresServerSessionTestSupport.Unpooled(route);
            await connection.OpenAsync();
            await using (var rights = new NpgsqlCommand("""
                SELECT current_user=$1 AND session_user=current_user AND NOT rolsuper AND NOT rolbypassrls
                    AND NOT rolinherit AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication
                    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=pg_roles.oid OR m.roleid=pg_roles.oid)
                    AND NOT has_table_privilege(current_user,'braid.server_sessions','SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
                    AND (SELECT count(*) FROM information_schema.column_privileges
                        WHERE grantee=current_user AND table_schema='braid' AND privilege_type='SELECT')=$2
                    AND (SELECT count(*) FROM information_schema.column_privileges
                        WHERE grantee=current_user AND table_schema='braid' AND privilege_type='INSERT')=$3
                    AND (SELECT count(*) FROM information_schema.column_privileges
                        WHERE grantee=current_user AND table_schema='braid' AND privilege_type='UPDATE')=$4
                FROM pg_catalog.pg_roles WHERE rolname=current_user
                """, connection))
            {
                rights.Parameters.AddWithValue(writer ? "braid_session_writer" : "braid_session_reader");
                rights.Parameters.AddWithValue(writer ? 20 : 19);
                rights.Parameters.AddWithValue(writer ? 13 : 0);
                rights.Parameters.AddWithValue(writer ? 3 : 0);
                Assert.True(Assert.IsType<bool>(await rights.ExecuteScalarAsync()));
            }
            await using (var exactColumns = new NpgsqlCommand("""
                WITH expected(table_name,column_name,privilege_type) AS (
                    SELECT 'server_sessions',name,'SELECT' FROM unnest($1::text[]) name
                    UNION ALL SELECT 'external_identity_bindings',name,'SELECT'
                        FROM unnest(ARRAY['binding_id','principal_id','enabled','version']) name
                    UNION ALL SELECT 'principals',name,'SELECT' FROM unnest(ARRAY['id','active']) name
                    UNION ALL SELECT 'organizations','id','SELECT' WHERE $2
                    UNION ALL SELECT 'server_sessions',name,'INSERT' FROM unnest($1::text[]) name WHERE $2
                    UNION ALL SELECT 'server_sessions',name,'UPDATE'
                        FROM unnest(ARRAY['idle_expires_at','version','revoked_at']) name WHERE $2
                ), actual AS (
                    SELECT table_name,column_name,privilege_type FROM information_schema.column_privileges
                    WHERE grantee=current_user AND table_schema='braid'
                )
                SELECT NOT EXISTS(SELECT * FROM actual EXCEPT SELECT * FROM expected)
                    AND NOT EXISTS(SELECT * FROM expected EXCEPT SELECT * FROM actual)
                    AND NOT EXISTS(SELECT 1 FROM information_schema.column_privileges
                        WHERE grantee=current_user AND table_schema='braid'
                            AND (is_grantable<>'NO' OR grantor<>'braid_migrator'))
                """, connection))
            {
                exactColumns.Parameters.AddWithValue(PostgresServerSessionTestSupport.Columns.Split(','));
                exactColumns.Parameters.AddWithValue(writer);
                Assert.True(Assert.IsType<bool>(await exactColumns.ExecuteScalarAsync()), "The session profile did not have its exact named column rights.");
            }
            await using (var unbound = new NpgsqlCommand("SELECT handle_digest FROM braid.server_sessions", connection))
            await using (var rows = await unbound.ExecuteReaderAsync()) Assert.False(await rows.ReadAsync());
            await using var transaction = await connection.BeginTransactionAsync();
            await BindAsync(connection, transaction, test.Organization);
            await using (var visible = new NpgsqlCommand("SELECT organization_id,handle_digest FROM braid.server_sessions", connection, transaction))
            await using (var rows = await visible.ExecuteReaderAsync())
            {
                Assert.True(await rows.ReadAsync());
                Assert.True(rows.GetGuid(0) == first.Key.OrganizationId && rows.GetFieldValue<byte[]>(1).AsSpan()
                    .SequenceEqual(PostgresServerSessionTestSupport.Bytes(first.Key)), "A restricted session read disclosed a foreign row.");
                Assert.False(await rows.ReadAsync());
            }
            await transaction.RollbackAsync();
            foreach (var sql in new[]
            {
                "SELECT provider FROM braid.external_identity_bindings", "SELECT subject FROM braid.external_identity_bindings",
                "SELECT created_at FROM braid.principals", "SELECT name FROM braid.organizations",
                "SELECT principal_id FROM braid.organization_memberships", "SELECT principal_id FROM braid.project_payload_read_grants",
                "SELECT name FROM braid.mappings", "SELECT version FROM braid.schema_migrations",
                "UPDATE braid.principals SET active=false", "UPDATE braid.external_identity_bindings SET enabled=false",
                "SELECT binding_id FROM braid.external_identity_bindings FOR UPDATE",
                "SELECT id FROM braid.principals FOR SHARE", "SELECT id FROM braid.organizations FOR KEY SHARE",
                "DELETE FROM braid.server_sessions", "TRUNCATE braid.server_sessions",
                "UPDATE braid.server_sessions SET protected_ticket=decode('01','hex')",
                "UPDATE braid.server_sessions SET login_epoch=gen_random_uuid()",
                "UPDATE braid.server_sessions SET absolute_expires_at=absolute_expires_at+interval '1 second'",
                "SELECT braid.guard_server_sessions_transition()"
            }) await RefusedSqlAsync(route, sql, PostgresErrorCodes.InsufficientPrivilege);
        }
        await RefusedSqlAsync(test.Case.SessionReaderConnectionString, "SELECT id FROM braid.organizations", PostgresErrorCodes.InsufficientPrivilege);
        await RefusedSqlAsync(test.Case.SessionReaderConnectionString, "UPDATE braid.server_sessions SET version=version+1", PostgresErrorCodes.InsufficientPrivilege);
        foreach (var route in new[] { test.Case.RuntimeConnectionString, test.Case.IngestionConnectionString,
            test.Case.IdentityConnectionString, test.Case.PayloadPermissionConnectionString, test.Case.CatalogConnectionString,
            test.Case.MetadataConnectionString, test.Case.ProjectCreationConnectionString, test.Case.ReportConnectionString,
            test.Case.DerivationConnectionString, test.Case.MaintenanceConnectionString })
            await RefusedSqlAsync(route, "SELECT handle_digest FROM braid.server_sessions", PostgresErrorCodes.InsufficientPrivilege);
        var before = await test.RowsAsync();
        await RefusedSqlAsync(test.Case.SessionWriterConnectionString, $"""
            INSERT INTO braid.server_sessions({PostgresServerSessionTestSupport.Columns})
            VALUES($1,$2,$3,1,gen_random_uuid(),statement_timestamp(),statement_timestamp()+interval '20 minutes',
                statement_timestamp()+interval '60 minutes',1,NULL,1,1,$4)
            """, PostgresErrorCodes.InsufficientPrivilege, test.Organization,
            PostgresServerSessionTestSupport.Bytes(test.Key()), test.OtherOrganization, test.Binding.BindingId,
            PostgresServerSessionTestSupport.Ticket());
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task RoleOrAclDriftRefusesBothPortsAndExactRestorationRecoversWithoutChangingRows()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.CreateAsync();
        var before = await test.RowsAsync();
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ObserveAsync(facts.Key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Writer.RevokeAsync(facts.Key));
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        }
        foreach (var role in new[] { "braid_session_reader", "braid_session_writer" })
        {
            foreach (var (change, restore) in new[]
            {
                ($"ALTER ROLE {role} INHERIT", $"ALTER ROLE {role} NOINHERIT"),
                ($"ALTER ROLE {role} BYPASSRLS", $"ALTER ROLE {role} NOBYPASSRLS"),
                ($"GRANT pg_read_all_data TO {role}", $"REVOKE pg_read_all_data FROM {role}")
            })
            {
                await DriftAsync(test, change, restore, Refused, bootstrap: true);
                await test.CurrentAsync(facts);
            }
            foreach (var (change, restore) in new[]
            {
                ($"GRANT SELECT ON braid.server_sessions TO {role}",
                    $"REVOKE SELECT ON braid.server_sessions FROM {role}; GRANT SELECT({PostgresServerSessionTestSupport.Columns}) ON braid.server_sessions TO {role}"),
                ($"GRANT SELECT(subject) ON braid.external_identity_bindings TO {role}", $"REVOKE SELECT(subject) ON braid.external_identity_bindings FROM {role}"),
                ($"REVOKE SELECT(protected_ticket) ON braid.server_sessions FROM {role}", $"GRANT SELECT(protected_ticket) ON braid.server_sessions TO {role}"),
                ($"GRANT SELECT(protected_ticket) ON braid.server_sessions TO {role} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT(protected_ticket) ON braid.server_sessions FROM {role}"),
                ($"GRANT EXECUTE ON FUNCTION braid.guard_server_sessions_transition() TO {role}", $"REVOKE EXECUTE ON FUNCTION braid.guard_server_sessions_transition() FROM {role}")
            })
            {
                await DriftAsync(test, change, restore, Refused);
                await test.CurrentAsync(facts);
            }
        }
        await DriftAsync(test, "GRANT SELECT(protected_ticket) ON braid.server_sessions TO PUBLIC",
            "REVOKE SELECT(protected_ticket) ON braid.server_sessions FROM PUBLIC", Refused);
        await test.CurrentAsync(facts);
    }

    [PostgresFact]
    public async Task SourceRlsPolicyAndStableTriggerDriftRefusesBeforeMutationAndRestorationIsPositive()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.CreateAsync();
        var before = await test.RowsAsync();
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ObserveAsync(facts.Key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Writer.RevokeAsync(facts.Key));
            PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE braid.principals RENAME COLUMN created_at TO session_unknown_probe", "ALTER TABLE braid.principals RENAME COLUMN session_unknown_probe TO created_at"),
            ("ALTER TABLE braid.server_sessions DISABLE ROW LEVEL SECURITY", "ALTER TABLE braid.server_sessions ENABLE ROW LEVEL SECURITY"),
            ("ALTER POLICY server_sessions_organization_scope ON braid.server_sessions USING(true) WITH CHECK(true)",
                "ALTER POLICY server_sessions_organization_scope ON braid.server_sessions USING(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid) WITH CHECK(organization_id=NULLIF(current_setting('braid.organization_id',true),'')::uuid)"),
            ("ALTER TABLE braid.server_sessions DISABLE TRIGGER server_sessions_transition_guard", "ALTER TABLE braid.server_sessions ENABLE TRIGGER server_sessions_transition_guard"),
            ("ALTER FUNCTION braid.guard_server_sessions_transition() VOLATILE", "ALTER FUNCTION braid.guard_server_sessions_transition() STABLE")
        })
        {
            await DriftAsync(test, change, restore, Refused);
            await test.CurrentAsync(facts);
        }
        await using var extra = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var extraFacts = await extra.CreateAsync();
        var extraBefore = await extra.RowsAsync();
        await extra.Case.ExecuteAsync("ALTER TABLE braid.server_sessions ADD COLUMN session_unknown_probe integer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.Reader.ObserveAsync(extraFacts.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.Writer.RevokeAsync(extraFacts.Key));
        PostgresServerSessionTestSupport.SamePrivate(extraBefore, await extra.RowsAsync());
        // Dropping an added attribute leaves a physical attnum hole. This isolated case
        // must still fail; it is never reused or described as restored source.
        await extra.Case.ExecuteAsync("ALTER TABLE braid.server_sessions DROP COLUMN session_unknown_probe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.Reader.ObserveAsync(extraFacts.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => extra.Writer.RevokeAsync(extraFacts.Key));
        PostgresServerSessionTestSupport.SamePrivate(extraBefore, await extra.RowsAsync());
    }

    [PostgresFact]
    public async Task CredentialSwapsAndCanceledAdmissionDoNotMutateOrBecomeAnonymousSuccess()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.CreateAsync();
        var before = await test.RowsAsync();
        foreach (var route in new[] { test.Case.MigrationConnectionString, test.Case.SessionWriterConnectionString })
        {
            await using var database = new PostgresServerSessionDatabase(route, ServerSessionDatabaseProfile.Reader);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresServerSessionReader(database).ObserveAsync(facts.Key));
        }
        foreach (var route in new[] { test.Case.MigrationConnectionString, test.Case.SessionReaderConnectionString })
        {
            await using var database = new PostgresServerSessionDatabase(route, ServerSessionDatabaseProfile.Writer);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresServerSessionWriter(database).RevokeAsync(facts.Key));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var intent = await test.IntentAsync();
        foreach (var call in new Func<Task>[] { () => test.Reader.ObserveAsync(facts.Key, cancellation.Token),
            () => test.Writer.CreateOnlyAsync(intent, cancellation.Token), () => test.Writer.RenewAsync(facts.Key, 1, cancellation.Token),
            () => test.Writer.RevokeAsync(facts.Key, cancellation.Token) })
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        await test.CurrentAsync(facts);
    }

    [PostgresFact]
    public async Task ActualConnectionFailureReturnsOnlyFixedPrivateSafeErrorsWithoutWritingOrCachingSuccess()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.CreateAsync();
        var before = await test.RowsAsync();
        var secret = "private-session-" + Guid.NewGuid().ToString("N");
        var readerRoute = new NpgsqlConnectionStringBuilder(test.Case.SessionReaderConnectionString) { Password = secret, Pooling = false }.ConnectionString;
        var writerRoute = new NpgsqlConnectionStringBuilder(test.Case.SessionWriterConnectionString) { Password = secret, Pooling = false }.ConnectionString;
        await using var readerDatabase = new PostgresServerSessionDatabase(readerRoute, ServerSessionDatabaseProfile.Reader);
        await using var writerDatabase = new PostgresServerSessionDatabase(writerRoute, ServerSessionDatabaseProfile.Writer);
        var reader = new PostgresServerSessionReader(readerDatabase);
        var writer = new PostgresServerSessionWriter(writerDatabase);
        var intent = await test.IntentAsync();
        foreach (var call in new Func<Task>[] { () => reader.ObserveAsync(facts.Key), () => writer.CreateOnlyAsync(intent),
            () => writer.RenewAsync(facts.Key, 1), () => writer.RevokeAsync(facts.Key) })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Null(error.InnerException);
            Assert.True(error.Message is "Server session observation failed." or "Server session mutation failed.", "A session storage failure had an unexpected diagnostic category.");
            Assert.False(error.ToString().Contains(secret, StringComparison.Ordinal), "A session error exposed a private credential.");
            Assert.False(error.ToString().Contains(facts.Key.HandleDigest.Value, StringComparison.Ordinal), "A session error exposed a private digest.");
        }
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
    }

    [PostgresFact]
    public async Task AllowedWriterSqlStillSharesExactStatementClockAndCannotReviveExpiredOrExtendPastPolicy()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var live = await test.HistoricalAsync();
        var expired = await test.HistoricalAsync(ageMinutes: 30);
        var before = await test.RowsAsync();
        await RefusedSqlAsync(test.Case.SessionWriterConnectionString,
            "UPDATE braid.server_sessions SET idle_expires_at=statement_timestamp()+interval '20 minutes 1 microsecond',version=version+1 WHERE handle_digest=$1",
            PostgresErrorCodes.CheckViolation, test.Organization, PostgresServerSessionTestSupport.Bytes(live.Key));
        await RefusedSqlAsync(test.Case.SessionWriterConnectionString,
            "UPDATE braid.server_sessions SET idle_expires_at=statement_timestamp()+interval '20 minutes',version=version+1 WHERE handle_digest=$1",
            PostgresErrorCodes.CheckViolation, test.Organization, PostgresServerSessionTestSupport.Bytes(expired.Key));
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        await using var connection = PostgresServerSessionTestSupport.Unpooled(test.Case.SessionWriterConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await BindAsync(connection, transaction, test.Organization);
        await using (var exact = new NpgsqlCommand("UPDATE braid.server_sessions SET idle_expires_at=statement_timestamp()+interval '20 minutes',version=version+1 WHERE handle_digest=$1 RETURNING idle_expires_at=statement_timestamp()+interval '20 minutes'", connection, transaction))
        {
            exact.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(live.Key));
            Assert.True(Assert.IsType<bool>(await exact.ExecuteScalarAsync()));
        }
        await transaction.CommitAsync();
        var renewed = await test.StoredAsync(live.Key);
        PostgresServerSessionTestSupport.SameImmutable(live, renewed);
        Assert.Equal(2, renewed.Version);
        await test.CurrentAsync(renewed);
    }

    [PostgresFact]
    public async Task OrdinaryOwnerSqlCannotRewriteIssuedFactsEraseTombstonesOrCounterfeitVersionTransitions()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        var facts = await test.HistoricalAsync();
        var before = await test.RowsAsync();
        foreach (var assignment in new[] { "handle_digest=decode(repeat('01',32),'hex')", "organization_id=gen_random_uuid()",
            "binding_id=gen_random_uuid()", "binding_version=binding_version+1", "login_epoch=gen_random_uuid()",
            "issued_at=issued_at-interval '1 second'", "absolute_expires_at=absolute_expires_at+interval '1 second'",
            "policy_version=2", "codec_version=2", "protected_ticket=decode('01','hex')", "version=version+1",
            "idle_expires_at=idle_expires_at+interval '1 minute'", "revoked_at=statement_timestamp()" })
            await RefusedSqlAsync(test.Case.MigrationConnectionString, "UPDATE braid.server_sessions SET " + assignment, PostgresErrorCodes.CheckViolation);
        foreach (var sql in new[] { "DELETE FROM braid.server_sessions", "TRUNCATE braid.server_sessions" })
            await RefusedSqlAsync(test.Case.MigrationConnectionString, sql, PostgresErrorCodes.CheckViolation);
        PostgresServerSessionTestSupport.SamePrivate(before, await test.RowsAsync());
        var revoked = PostgresServerSessionTestSupport.Acknowledged(await test.Writer.RevokeAsync(facts.Key), ServerSessionWriteOutcome.Revoked);
        foreach (var sql in new[] { "UPDATE braid.server_sessions SET revoked_at=NULL,version=version+1", "DELETE FROM braid.server_sessions", "TRUNCATE braid.server_sessions" })
            await RefusedSqlAsync(test.Case.MigrationConnectionString, sql, PostgresErrorCodes.CheckViolation);
        PostgresServerSessionTestSupport.SameFacts(revoked, await test.StoredAsync(facts.Key));
    }

    [PostgresFact]
    public async Task DirectInitialWritesRejectInvalidByteBoundsVersionsAndNonfiniteOrUnsupportedInstants()
    {
        await using var test = await PostgresServerSessionTestSupport.CreateAsync(fixture);
        foreach (var ticket in new[] { Array.Empty<byte>(), new byte[16385] })
        {
            await using var connection = PostgresServerSessionTestSupport.Unpooled(test.Case.MigrationConnectionString);
            await connection.OpenAsync();
            await using var insert = PostgresServerSessionTestSupport.HistoricalInsert(connection, null, test.Key(), test.Binding, Guid.NewGuid(), ticket);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var issued in new[] { "'infinity'::timestamptz", "'-infinity'::timestamptz",
            "'0001-01-01 00:00:00+00'::timestamptz", "'10000-01-01 00:00:00+00'::timestamptz" })
        {
            await using var command = test.Case.Owner.CreateCommand($"""
                INSERT INTO braid.server_sessions({PostgresServerSessionTestSupport.Columns})
                VALUES($1,$2,$3,1,$4,{issued},{issued}+interval '20 minutes',{issued}+interval '60 minutes',1,NULL,1,1,$5)
                """);
            command.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(test.Key()));
            command.Parameters.AddWithValue(test.Organization);
            command.Parameters.AddWithValue(test.Binding.BindingId);
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(PostgresServerSessionTestSupport.Ticket());
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var (digest, bindingVersion, version, policy, codec, epoch, revoked) in new[]
        {
            ("substring($1::bytea from 1 for 1)", "1", "1", "1", "1", "gen_random_uuid()", "NULL"),
            ("$1", "0", "1", "1", "1", "gen_random_uuid()", "NULL"),
            ("$1", "1", "0", "1", "1", "gen_random_uuid()", "NULL"),
            ("$1", "1", "2", "1", "1", "gen_random_uuid()", "NULL"),
            ("$1", "1", "1", "2", "1", "gen_random_uuid()", "NULL"),
            ("$1", "1", "1", "1", "2", "gen_random_uuid()", "NULL"),
            ("$1", "1", "1", "1", "1", "'00000000-0000-0000-0000-000000000000'::uuid", "NULL"),
            ("$1", "1", "1", "1", "1", "gen_random_uuid()", "statement_timestamp()")
        })
        {
            await using var command = test.Case.Owner.CreateCommand($"""
                INSERT INTO braid.server_sessions({PostgresServerSessionTestSupport.Columns})
                VALUES({digest},$2,$3,{bindingVersion},{epoch},statement_timestamp(),
                    statement_timestamp()+interval '20 minutes',statement_timestamp()+interval '60 minutes',
                    {version},{revoked},{policy},{codec},$4)
                """);
            command.Parameters.AddWithValue(PostgresServerSessionTestSupport.Bytes(test.Key()));
            command.Parameters.AddWithValue(test.Organization);
            command.Parameters.AddWithValue(test.Binding.BindingId);
            command.Parameters.AddWithValue(PostgresServerSessionTestSupport.Ticket());
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.True(error.SqlState == PostgresErrorCodes.CheckViolation, "Invalid initial metadata did not fail its source guard or check.");
        }
        PostgresServerSessionTestSupport.SamePrivate("[]", await test.RowsAsync());
    }

    private static async Task BindAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organization)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(organization.ToString("D"));
        await command.ExecuteScalarAsync();
    }

    private static async Task RefusedSqlAsync(string route, string sql, string sqlState, Guid? organization = null, params object[] values)
    {
        await using var connection = PostgresServerSessionTestSupport.Unpooled(route);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (organization is { } configured) await BindAsync(connection, transaction, configured);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(string.Equals(error.SqlState, sqlState, StringComparison.Ordinal), "A direct session SQL refusal had an unexpected category.");
    }

    private static async Task DriftAsync(PostgresServerSessionTestSupport test, string change, string restore,
        Func<Task> probe, bool bootstrap = false)
    {
        if (bootstrap) await test.Case.BootstrapAsync(change);
        else await test.Case.ExecuteAsync(change);
        try { await probe(); }
        finally
        {
            if (bootstrap) await test.Case.BootstrapAsync(restore);
            else await test.Case.ExecuteAsync(restore);
        }
    }
}
