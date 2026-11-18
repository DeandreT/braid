using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed partial class AiQuotaAdmissionPersistenceTests
{
    [PostgresFact]
    public async Task Concurrent_conversations_cannot_overspend_a_principal_cap()
    {
        await using var f = await Context.CreateAsync(fixture, maximum: 60);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, null, 200));
        await f.SetAsync(new(f.Actor.PrincipalId.Value, AiQuotaPeriod.Day, null, 100));
        var first = await f.ConversationAsync(f.Actor); var second = await f.ConversationAsync(f.Actor);
        var a = await f.IntentAsync(); var b = await f.IntentAsync();
        var results = await Task.WhenAll(f.Writer.StartAsync(f.Actor, first, a), f.Writer.StartAsync(f.Actor, second, b));
        Assert.Single(results, item => item.Outcome == ChatStoreOutcome.Applied);
        Assert.Single(results, item => item.Outcome == ChatStoreOutcome.QuotaTokenLimitReached);
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
        var other = await f.ConversationAsync(f.Other);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Other, other, await f.IntentAsync())).Outcome);
    }

    [PostgresFact]
    public async Task Admission_waits_for_a_policy_write_and_observes_its_committed_limit()
    {
        await using var f = await Context.CreateAsync(fixture);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, 2, null));
        var first = await f.ConversationAsync(f.Actor);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, first, await f.IntentAsync())).Outcome);
        var second = await f.ConversationAsync(f.Other); var intent = await f.IntentAsync();
        await using var connection = PostgresServerSessionTestSupport.Unpooled(f.Case.RegressionConnectionString("braid_administration"));
        await connection.OpenAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new Npgsql.NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        await AdministrationRoleGuard.ValidateAsync(connection, transaction, default);
        await using (var scope = new Npgsql.NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", connection, transaction))
        { scope.Parameters.AddWithValue(f.Org.ToString("D")); await scope.ExecuteScalarAsync(); }
        await using (var write = new Npgsql.NpgsqlCommand("SELECT braid.write_ai_quota($1,$2,2,NULL,2,1,NULL)", connection, transaction))
        {
            write.Parameters.AddWithValue(f.Org); write.Parameters.AddWithValue(f.Actor.PrincipalId.Value);
            Assert.Equal("updated", await write.ExecuteScalarAsync());
        }
        var pending = f.Writer.StartAsync(f.Other, second, intent);
        var released = false;
        try
        {
            var waiting = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (await f.Case.ScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND usename='braid_chat_writer'
                        AND $1=ANY(pg_blocking_pids(pid)))
                    """, connection.ProcessID)) { waiting = true; break; }
                await Task.Delay(25);
            }
            Assert.True(waiting, "Turn admission must wait on the actual policy write's organization lock.");
            await transaction.CommitAsync(); released = true;
            Assert.Equal(ChatStoreOutcome.QuotaTurnLimitReached, (await pending.WaitAsync(TimeSpan.FromSeconds(15))).Outcome);
            Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE identity_id=$1", intent.TurnId));
            Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException) { }
        }
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE braid.chat_turns DISABLE TRIGGER ai_quota_turn")]
    [InlineData("ALTER TABLE braid.ai_usage_turns DISABLE TRIGGER ai_quota_usage")]
    [InlineData("ALTER TABLE braid.ai_quota_reservations ADD COLUMN private_prompt text")]
    [InlineData("GRANT SELECT ON braid.ai_quota_limits TO braid_chat_writer")]
    [InlineData("GRANT EXECUTE ON FUNCTION braid.reserve_ai_quota(uuid,uuid,uuid,uuid,text,text,timestamp with time zone,bigint,text,integer) TO braid_administration")]
    public async Task Tampered_quota_objects_refuse_reads_and_new_turns_without_spending_capacity(string change)
    {
        await using var f = await Context.CreateAsync(fixture);
        var key = await f.ConversationAsync(f.Actor); var intent = await f.IntentAsync();
        await f.Case.ExecuteAsync(change);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.StartAsync(f.Actor, key, intent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admin.ReadQuotasAsync(f.Actor, f.Org, null, default));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
    }
}
