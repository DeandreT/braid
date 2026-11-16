using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresChatPoolTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task SeparateSingleConnectorPoolsResetEveryScopedSettingAfterSuccessRollbackAndCancellation()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var profile in new[] { ChatDatabaseProfile.Reader, ChatDatabaseProfile.Writer })
        {
            var route = new NpgsqlConnectionStringBuilder(profile == ChatDatabaseProfile.Reader ? f.Case.ChatReaderConnectionString : f.Case.ChatWriterConnectionString)
            { MaxPoolSize = 1, MinPoolSize = 0, ApplicationName = "braid-chat-pool-" + Guid.NewGuid().ToString("N") }.ConnectionString;
            await using var database = new PostgresChatDatabase(route, profile);
            var store = new PostgresChatStore(database);
            Assert.True(await store.ReadAsync(f.Actor, f.Key) == f.Conversation);
            await ResetAsync(database);
            // Session contamination is made inside this uniquely owned pool and
            // must be reset before the next checkout, not accepted as trusted scope.
            await using (var connection = await database.DataSource.OpenConnectionAsync())
            {
                await using var dirty = new NpgsqlCommand("""
                    SELECT set_config('braid.organization_id',gen_random_uuid()::text,false),
                        set_config('braid.chat_principal_id',gen_random_uuid()::text,false),
                        set_config('search_path','public',false),set_config('statement_timeout','100ms',false)
                    """, connection);
                await dirty.ExecuteNonQueryAsync();
            }
            await ResetAsync(database);
            await using (var held = await database.BeginAsync(f.Actor, f.Key, false, CancellationToken.None))
            { /* Dispose without COMMIT: rollback must reset both local scopes. */ }
            await ResetAsync(database);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(f.Actor, f.Key, canceled.Token));
            Assert.Equal(canceled.Token, error.CancellationToken);
            await ResetAsync(database);
            Assert.True(await store.ReadAsync(f.Actor, f.Key) == f.Conversation);
        }
        Assert.NotSame(f.ReaderDatabase.DataSource, f.WriterDatabase.DataSource);
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }

    [PostgresFact]
    public async Task SwappedAndOwnerCredentialsFailClosedAndAReaderProfileCannotMutate()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var before = await PostgresChatSecurityTests.RowsAsync(f);
        foreach (var (route, profile) in new[]
        {
            (f.Case.MigrationConnectionString, ChatDatabaseProfile.Reader), (f.Case.ChatWriterConnectionString, ChatDatabaseProfile.Reader),
            (f.Case.MigrationConnectionString, ChatDatabaseProfile.Writer), (f.Case.ChatReaderConnectionString, ChatDatabaseProfile.Writer)
        })
        {
            await using var database = new PostgresChatDatabase(route, profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresChatStore(database).ReadAsync(f.Actor, f.Key));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.CreateAsync(f.Actor, f.Conversation));
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }

    [PostgresFact]
    public async Task AdmissionHoldsSchemaAndPolicyRelationLocksUntilTransactionEndsWithoutAuthorityRowLocks()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        foreach (var database in new[] { f.ReaderDatabase, f.WriterDatabase })
        {
            await using (var admitted = await database.BeginAsync(f.Actor, f.Key, false, CancellationToken.None))
            {
                foreach (var table in ChatSchemaContract.ChatTables)
                {
                    await using var outsider = PostgresChatSecurityTests.Open(f.Case.MigrationConnectionString);
                    await outsider.OpenAsync();
                    await using var transaction = await outsider.BeginTransactionAsync();
                    await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout='100ms'", outsider, transaction)) await timeout.ExecuteNonQueryAsync();
                    await using var change = new NpgsqlCommand($"ALTER POLICY creator_scope ON braid.{table} USING(true) WITH CHECK(true)", outsider, transaction);
                    var blocked = await Assert.ThrowsAsync<PostgresException>(() => change.ExecuteNonQueryAsync());
                    Assert.True(blocked.SqlState == PostgresErrorCodes.LockNotAvailable, "Policy mutation was not fenced by the held admission relation lock.");
                }
                // No membership row is locked by admission: a separate owner can
                // revoke now, and a NEW statement must observe that committed change.
                await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE principal_id=$1", f.Actor.PrincipalId.Value);
                await using var authority = admitted.CreateCommand("SELECT braid.chat_authorized($1,$2,$3)");
                authority.Parameters.AddWithValue(f.Key.PinnedRevision.OrganizationId); authority.Parameters.AddWithValue(f.Key.PinnedRevision.ProjectId);
                authority.Parameters.AddWithValue(f.Actor.PrincipalId.Value);
                Assert.False(Assert.IsType<bool>(await authority.ExecuteScalarAsync()));
            }
            await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=true,version=version+1 WHERE principal_id=$1", f.Actor.PrincipalId.Value);
            await ResetAsync(database);
            Assert.True(await f.Reader.ReadAsync(f.Actor, f.Key) == f.Conversation);
        }
    }

    private static async Task ResetAsync(PostgresChatDatabase database)
        => await ResetAsync(database.DataSource);
    private static async Task ResetAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(current_setting('braid.organization_id',true),'')=''
                AND COALESCE(current_setting('braid.chat_principal_id',true),'')=''
                AND current_setting('transaction_read_only')='off' AND current_setting('transaction_isolation')='read committed'
                AND current_setting('statement_timeout')='0' AND current_setting('lock_timeout')='0'
                AND current_setting('search_path')='"$user", public' AND current_user=session_user
            """, connection);
        Assert.True(Assert.IsType<bool>(await command.ExecuteScalarAsync()), "The owned chat connector retained scoped state across pool return.");
    }

    [PostgresFact]
    public async Task LifecyclePoolResetsScopeOnReturnAndRejectsSwappedCredentialsWithoutGainingContentRights()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var before = await PostgresChatSecurityTests.RowsAsync(f);
        var route = new NpgsqlConnectionStringBuilder(f.Case.ChatLifecycleConnectionString)
        { MaxPoolSize = 1, MinPoolSize = 0, ApplicationName = "braid-chat-lifecycle-pool-" + Guid.NewGuid().ToString("N") }.ConnectionString;
        await using var database = new PostgresChatLifecycleDatabase(route);
        var lifecycle = new PostgresChatLifecycleStore(database);
        Assert.NotNull(await lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId)); await ResetAsync(database.DataSource);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            await using var dirty = new NpgsqlCommand("SELECT set_config('braid.organization_id','bad-org',false),set_config('braid.chat_principal_id','bad-owner',false),set_config('search_path','public',false)", connection);
            await dirty.ExecuteNonQueryAsync();
        }
        await ResetAsync(database.DataSource);
        await using (var rolledBack = await database.BeginAsync(f.Key, CancellationToken.None)) { }
        await ResetAsync(database.DataSource);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.ObserveAsync(f.Key, turn.Intent.TurnId, canceled.Token));
        Assert.Equal(canceled.Token, error.CancellationToken); await ResetAsync(database.DataSource);
        foreach (var other in new[] { f.Case.ChatReaderConnectionString, f.Case.ChatWriterConnectionString, f.Case.MigrationConnectionString })
        {
            await using var wrong = new PostgresChatLifecycleDatabase(other);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresChatLifecycleStore(wrong).ObserveAsync(f.Key, turn.Intent.TurnId));
        }
        PostgresChatSecurityTests.Same(before, await PostgresChatSecurityTests.RowsAsync(f));
    }
}
