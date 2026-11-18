using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed partial class AiQuotaAdmissionPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Concurrent_conversations_share_one_organization_budget_and_denial_reserves_no_identity()
    {
        await using var f = await Context.CreateAsync(fixture);
        await f.SetAsync(new(null, AiQuotaPeriod.Hour, 1, null));
        var first = await f.ConversationAsync(f.Actor); var second = await f.ConversationAsync(f.Other);
        var a = await f.IntentAsync(); var b = await f.IntentAsync();
        var results = await Task.WhenAll(f.Writer.StartAsync(f.Actor, first, a), f.Writer.StartAsync(f.Other, second, b));
        Assert.Single(results, r => r.Outcome == ChatStoreOutcome.Applied);
        Assert.Single(results, r => r.Outcome == ChatStoreOutcome.QuotaTurnLimitReached);
        var refused = results[0].Outcome == ChatStoreOutcome.Applied ? b : a;
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE identity_id=$1", refused.TurnId));
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
        Assert.True(await f.Case.ScalarAsync<bool>("""
            SELECT bool_and(r.admitted_at=t.created_at) FROM braid.ai_quota_reservations r JOIN braid.chat_turns t
                ON t.organization_id=r.organization_id AND t.creator_id=r.principal_id AND t.turn_id=r.turn_id
            """));
    }

    [PostgresFact]
    public async Task Principal_limit_does_not_spend_organization_capacity_on_refusal()
    {
        await using var f = await Context.CreateAsync(fixture);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, 2, null));
        await f.SetAsync(new(f.Actor.PrincipalId.Value, AiQuotaPeriod.Day, 1, null));
        var one = await f.ConversationAsync(f.Actor); var two = await f.ConversationAsync(f.Actor); var other = await f.ConversationAsync(f.Other);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, one, await f.IntentAsync())).Outcome);
        Assert.Equal(ChatStoreOutcome.QuotaTurnLimitReached, (await f.Writer.StartAsync(f.Actor, two, await f.IntentAsync())).Outcome);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Other, other, await f.IntentAsync())).Outcome);
        Assert.Equal(2L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
    }

    [PostgresFact]
    public async Task Organization_budgets_do_not_leak_or_block_another_organization()
    {
        await using var f = await Context.CreateAsync(fixture);
        await f.SetAsync(new(null, AiQuotaPeriod.Hour, 1, null));
        var first = await f.ConversationAsync(f.Actor);
        var intent = await f.IntentAsync();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, first, intent)).Outcome);
        var bob = new ActorContext(new(f.Seed.Bob));
        var other = await f.ConversationAsync(bob, f.Seed.OrganizationB);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(bob, other, intent)).Outcome);
        Assert.Equal(2L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations WHERE turn_id=$1", intent.TurnId));
        Assert.Null(await f.Admin.ReadQuotasAsync(f.Actor, f.Seed.OrganizationB, null, default));
        Assert.Equal(ChatStoreOutcome.NotAuthorizedOrNotFound, (await f.Writer.StartAsync(f.Actor, other, await f.IntentAsync())).Outcome);
    }

    [PostgresFact]
    public async Task A_period_before_complete_accounting_cannot_be_used_as_empty_capacity()
    {
        await using var f = await Context.CreateAsync(fixture, maximum: 10);
        await f.Case.ExecuteAsync("UPDATE braid.organizations SET created_at=(SELECT complete_since-interval '2 months' FROM braid.ai_usage_coverage) WHERE id=$1", f.Org);
        await f.SetAsync(new(null, AiQuotaPeriod.Month, 100, 100));
        var key = await f.ConversationAsync(f.Actor); var intent = await f.IntentAsync();
        Assert.Equal(ChatStoreOutcome.QuotaCoverageUnavailable, (await f.Writer.StartAsync(f.Actor, key, intent)).Outcome);
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE identity_id=$1", intent.TurnId));
    }

    [PostgresFact]
    public async Task Replay_keeps_its_reservation_without_asking_for_another_bound()
    {
        await using var f = await Context.CreateAsync(fixture, maximum: 60);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, 1, 60));
        var key = await f.ConversationAsync(f.Actor); var intent = await f.IntentAsync();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, key, intent)).Outcome);
        Assert.Equal(1, f.Bounds.Calls);
        f.Bounds.Unavailable = true;
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.StartAsync(f.Actor, key, intent)).Outcome);
        Assert.Equal(1, f.Bounds.Calls);
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
    }

    [PostgresFact]
    public async Task Hard_caps_reserve_the_whole_turn_and_reject_unknown_or_mismatched_authority()
    {
        await using var f = await Context.CreateAsync(fixture, maximum: 60);
        await f.SetAsync(new(null, AiQuotaPeriod.Month, null, 100));
        var first = await f.ConversationAsync(f.Actor); var second = await f.ConversationAsync(f.Other);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, first, await f.IntentAsync())).Outcome);
        var original = await f.IntentAsync();
        Assert.Equal(ChatStoreOutcome.QuotaTokenLimitReached, (await f.Writer.StartAsync(f.Other, second, original)).Outcome);
        f.Bounds.Unavailable = true;
        Assert.Equal(ChatStoreOutcome.QuotaTokenBoundUnavailable, (await f.Writer.StartAsync(f.Other, second, original)).Outcome);
        f.Bounds.Unavailable = false; f.Bounds.Mismatch = true;
        Assert.Equal(ChatStoreOutcome.QuotaTokenBoundUnavailable, (await f.Writer.StartAsync(f.Other, second, original)).Outcome);
        Assert.Equal(0L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.chat_identities WHERE identity_id=$1", original.TurnId));
        Assert.Equal(60L, await f.Case.ScalarAsync<long>("SELECT sum(maximum_tokens)::bigint FROM braid.ai_quota_reservations"));
    }

    [PostgresTheory]
    [InlineData(true, true, ChatTurnPhase.Completed, true)]
    [InlineData(true, true, ChatTurnPhase.Failed, true)]
    [InlineData(true, false, ChatTurnPhase.Completed, false)]
    [InlineData(false, true, ChatTurnPhase.Failed, false)]
    [InlineData(false, true, ChatTurnPhase.Interrupted, false)]
    [InlineData(true, true, ChatTurnPhase.Cancelled, false)]
    public async Task Only_known_acknowledged_terminal_rounds_release_reserved_capacity(bool acknowledged, bool known,
        ChatTurnPhase phase, bool settled)
    {
        await using var f = await Context.CreateAsync(fixture, maximum: 60);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, null, 100));
        var key = await f.ConversationAsync(f.Actor); var intent = await f.IntentAsync();
        var turn = Assert.IsType<ChatTurnFacts>((await f.Writer.StartAsync(f.Actor, key, intent)).Facts);
        var round = PostgresChatTestSupport.Round();
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, key, intent.TurnId, turn.Version, round)).Turn);
        var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, 12, 8, known ? 20 : null),
            finishReason: ChatProviderFinishReason.Stop);
        if (acknowledged)
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.CompleteRoundAsync(f.Actor, key, intent.TurnId, turn.Version,
                round.RoundId, round.Fence, new ChatRoundCompletion(ChatRoundOutcome.Stopped, terminal))).Facts);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishWithMetadataAsync(f.Actor, key, intent.TurnId, turn.Version, phase, terminal)).Outcome);
        Assert.Equal(settled, await f.Case.ScalarAsync<bool>("SELECT settled_tokens IS NOT NULL FROM braid.ai_quota_reservations WHERE turn_id=$1", intent.TurnId));
        if (settled) Assert.Equal(20L, await f.Case.ScalarAsync<long>("SELECT settled_tokens FROM braid.ai_quota_reservations WHERE turn_id=$1", intent.TurnId));
        var next = await f.ConversationAsync(f.Actor);
        Assert.Equal(settled ? ChatStoreOutcome.Applied : ChatStoreOutcome.QuotaTokenLimitReached,
            (await f.Writer.StartAsync(f.Actor, next, await f.IntentAsync())).Outcome);
    }

    [PostgresFact]
    public async Task An_unbounded_active_turn_prevents_token_admission_after_policy_is_enabled()
    {
        await using var f = await Context.CreateAsync(fixture);
        var first = await f.ConversationAsync(f.Actor);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.StartAsync(f.Actor, first, await f.IntentAsync())).Outcome);
        await f.SetAsync(new(null, AiQuotaPeriod.Day, null, 100));
        f.Bounds.Unavailable = false;
        var next = await f.ConversationAsync(f.Other);
        Assert.Equal(ChatStoreOutcome.QuotaCoverageUnavailable, (await f.Writer.StartAsync(f.Other, next, await f.IntentAsync())).Outcome);
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.ai_quota_reservations"));
    }

    [PostgresFact]
    public async Task Configuration_is_version_conditional_and_requires_fresh_organization_administration()
    {
        await using var f = await Context.CreateAsync(fixture);
        var policy = new AiQuotaPolicy(null, AiQuotaPeriod.Day, 3, null);
        var before = await f.Admin.ReadQuotasAsync(f.Actor, f.Org, null, default);
        Assert.NotNull(before); Assert.Equal("1", before.Version);
        Assert.Equal("updated", await f.Admin.WriteQuotaAsync(f.Actor, f.Org, 1, policy, default));
        Assert.Equal("preconditionFailed", await f.Admin.WriteQuotaAsync(f.Actor, f.Org, 1, new(null, AiQuotaPeriod.Day, 4, null), default));
        Assert.Equal("unchanged", await f.Admin.WriteQuotaAsync(f.Actor, f.Org, 2, policy, default));
        Assert.Null(await f.Admin.ReadQuotasAsync(f.Other, f.Org, null, default));
        Assert.Equal("refused", await f.Admin.WriteQuotaAsync(f.Other, f.Org, 2, policy, default));
        await f.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", f.Org, f.Actor.PrincipalId.Value);
        Assert.Null(await f.Admin.ReadQuotasAsync(f.Actor, f.Org, null, default));
        Assert.Equal("refused", await f.Admin.WriteQuotaAsync(f.Actor, f.Org, 2, policy, default));
        Assert.Equal(1L, await f.Case.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events WHERE action='quotaChanged'"));
    }

    [PostgresFact]
    public async Task Removing_all_quota_objects_keeps_current_profiles_closed()
    {
        await using var f = await Context.CreateAsync(fixture);
        var key = await f.ConversationAsync(f.Actor);
        var intent = await f.IntentAsync();
        await SchemaRemovalTestSupport.RemoveAsync(f.Case, AiQuotaSchemaAdmission.Tables, AiQuotaSchemaAdmission.Functions, AiQuotaSchemaAdmission.TriggerNames);
        Assert.Equal(AiQuotaSchemaAdmission.MigrationChecksum, await f.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=56"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Writer.StartAsync(f.Actor, key, intent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admin.ReadQuotasAsync(f.Actor, f.Org, null, default));
    }

    private sealed class Bounds(long maximum) : IAiTokenBoundAuthority
    {
        internal bool Unavailable, Mismatch;
        internal int Calls;
        public Task<AiTokenUpperBound?> BoundAsync(ActorContext actor, ChatConversationKey conversation, ChatTurnIntent intent, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var key = Mismatch ? ChatConversationKey.ForWorkspace(conversation.OrganizationId, actor.PrincipalId, Guid.NewGuid()) : conversation;
            return Task.FromResult(Unavailable ? null : new AiTokenUpperBound(AiTokenAdmissionRequest.From(key, intent), "fixture-bound", 1, maximum));
        }
    }

    private sealed class Context : IAsyncDisposable
    {
        internal required PostgresCase Case;
        internal required PostgresSeedData Seed;
        internal required PostgresChatDatabase Database;
        internal required PostgresChatStore Writer;
        internal required PostgresAdministrationStore Admin;
        internal required Bounds Bounds;
        internal Guid Org => Seed.OrganizationA;
        internal ActorContext Actor => new(new(Seed.Alice));
        internal ActorContext Other => new(new(Seed.Admitted));
        internal static async Task<Context> CreateAsync(PostgresFixture fixture, long? maximum = null)
        {
            var test = await fixture.CreateCaseAsync();
            try
            {
                var seed = await PostgresSeedData.CreateAsync(test);
                await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, Guid.NewGuid());
                var database = new PostgresChatDatabase(test.ChatWriterConnectionString, ChatDatabaseProfile.Writer);
                var bounds = new Bounds(maximum ?? 60) { Unavailable = maximum is null };
                return new()
                {
                    Case = test,
                    Seed = seed,
                    Database = database,
                    Bounds = bounds,
                    Writer = new(database, bounds),
                    Admin = new(test.RegressionConnectionString("braid_administration"))
                };
            }
            catch { await test.DisposeAsync(); throw; }
        }
        internal async Task SetAsync(AiQuotaPolicy policy)
        {
            var snapshot = await Admin.ReadQuotasAsync(Actor, Org, policy.PrincipalId, default);
            Assert.NotNull(snapshot);
            Assert.Equal("updated", await Admin.WriteQuotaAsync(Actor, Org, AiQuotaPolicy.ParseLimit(snapshot.Version)!.Value, policy, default));
        }
        internal async Task<ChatConversationKey> ConversationAsync(ActorContext actor, Guid? organization = null)
        {
            var now = new DateTimeOffset(await Case.ScalarAsync<DateTime>("SELECT clock_timestamp()"));
            var key = ChatConversationKey.ForWorkspace(organization ?? Org, actor.PrincipalId, Guid.NewGuid());
            Assert.Equal(ChatStoreOutcome.Applied, await Writer.CreateAsync(actor, ChatConversationIntent.ForWorkspace(key, null, now.AddHours(1))));
            return key;
        }
        internal async Task<ChatTurnIntent> IntentAsync()
        {
            var now = new DateTimeOffset(await Case.ScalarAsync<DateTime>("SELECT clock_timestamp()"));
            var id = Guid.NewGuid();
            return new(id, new ChatOriginalEnvelope(id, "Synthetic quota request", [], ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding(), ChatTurnFocus.None),
                ChatTurnPolicyTests.Profile(), new(16, 65536, 3), now.AddMinutes(10));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Admin.DisposeAsync(); }
            finally { try { await Database.DisposeAsync(); } finally { await Case.DisposeAsync(); } }
        }
    }
}
