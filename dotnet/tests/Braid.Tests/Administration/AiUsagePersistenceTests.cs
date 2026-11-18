using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AiUsagePersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Five_round_turn_accounts_for_every_maximum_token_observation_once()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(await f.IntentAsync(budget: new(32, 65536, 5), providerBudget: new(600_000_000, 5, 1048576, 4194304, 131072, 8, 2048)));
        const long maximum = 1_000_000_000_000;
        var instance = Guid.NewGuid();
        for (var ordinal = 1; ordinal <= 5; ordinal++)
        {
            var round = PostgresChatTestSupport.Round(instance);
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Turn);
            var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, maximum, maximum, maximum));
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version,
                round.RoundId, round.Fence, new ChatRoundCompletion(ordinal == 5 ? ChatRoundOutcome.Stopped : ChatRoundOutcome.ToolCalls, terminal))).Facts);
        }
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Completed)).Outcome);
        var usage = Assert.Single((await ReadAsync(f)).Groups).Usage;
        Assert.Equal("5", usage.Rounds);
        Assert.Equal("5000000000000", usage.InputTokens);
        Assert.Equal("5000000000000", usage.OutputTokens);
        Assert.Equal("5000000000000", usage.TotalTokens);
    }

    [PostgresFact]
    public async Task Usage_schema_matches_the_explicit_source_and_fingerprint()
    {
        Assert.Equal(AiUsageSchemaAdmission.MigrationChecksum, MigrationRunner.EmbeddedMigrations.Single(item => item.Version == 56).Checksum);
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, AiUsageSchemaAdmission.Tables,
            default, validateAutomation: false, validateAudit: false, validateUsage: false);
        Assert.True(fingerprint == AiUsageSchemaAdmission.Fingerprint, "Usage schema fingerprint: " + fingerprint);
    }

    [Fact]
    public void Usage_function_inventory_is_explicit()
    {
        var definitions = BuildRoleGuard.LoadFunctions("0056_ai_usage_quotas.sql", 10);
        Assert.Equal(AiUsageSchemaAdmission.Functions.Concat(AiQuotaSchemaAdmission.Functions).Order(StringComparer.Ordinal), definitions.Keys.Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task Actual_turn_round_and_tool_writes_capture_exact_totals_without_private_content()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(await f.IntentAsync(original: "PRIVATE original prompt"));
        var round = PostgresChatTestSupport.Round();
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Turn);
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.AppendAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence,
            new(Guid.NewGuid(), ChatEventKind.ToolStarted, "PRIVATE tool arguments"))).Facts);
        var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, 11, 7, 18), finishReason: ChatProviderFinishReason.Stop);
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence,
            new ChatRoundCompletion(ChatRoundOutcome.Stopped, terminal))).Facts);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishWithMetadataAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version,
            ChatTurnPhase.Completed, terminal)).Outcome);
        Assert.Equal(ChatStoreOutcome.Replayed, (await f.Writer.StartAsync(f.Actor, f.Key, turn.Intent)).Outcome);

        var page = await ReadAsync(f);
        var group = Assert.Single(page.Groups);
        Assert.Equal("1", group.Usage.Turns); Assert.Equal("1", group.Usage.Rounds); Assert.Equal("1", group.Usage.ToolCalls);
        Assert.Equal("11", group.Usage.InputTokens); Assert.Equal("7", group.Usage.OutputTokens); Assert.Equal("18", group.Usage.TotalTokens);
        Assert.Equal("0", group.Usage.UnknownTotalRounds); Assert.Equal("0", group.Usage.FailedTurns); Assert.Empty(group.Usage.Failures);
        Assert.True(page.IncludesPartialHistory);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(page), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", await f.Case.ScalarAsync<string>("SELECT to_jsonb(u)::text FROM braid.ai_usage_turns u"), StringComparison.Ordinal);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_metadata_counts_once_even_when_round_completion_is_uncertain(bool completeRound)
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Turn);
        var terminal = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderRoundTerminal, 13, null, null),
            new(round.RoundId, ChatProviderFailureCode.ProviderRateLimited, ChatDispatchObservation.Attempted));
        if (completeRound)
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.CompleteRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round.RoundId, round.Fence,
                new ChatRoundCompletion(ChatRoundOutcome.Failed, terminal))).Facts);
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishWithMetadataAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version,
            ChatTurnPhase.Failed, terminal)).Outcome);
        var usage = Assert.Single((await ReadAsync(f)).Groups).Usage;
        Assert.Equal("1", usage.Rounds); Assert.Equal("13", usage.InputTokens);
        Assert.Null(usage.OutputTokens); Assert.Null(usage.TotalTokens); Assert.Equal("1", usage.UnknownTotalRounds);
        Assert.Equal("1", Assert.Single(usage.Failures, item => item.Category == "ProviderRateLimited").Count);
        Assert.Equal("1", Assert.Single(usage.Failures, item => item.Category == "TurnFailed").Count);
        Assert.Equal(completeRound ? ChatRoundOutcome.Failed : ChatRoundOutcome.Uncertain,
            (await f.Reader.ReadRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, round.RoundId))!.Outcome);
    }

    [PostgresFact]
    public async Task Intermediate_provider_observations_do_not_establish_terminal_token_totals()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync(); var round = PostgresChatTestSupport.Round();
        turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, round)).Turn);
        var observation = new ChatTerminalMetadata(new(round.RoundId, ChatUsageOrigin.ProviderObservation, 11, 7, 18));
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishWithMetadataAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version,
            ChatTurnPhase.Interrupted, observation)).Outcome);
        var usage = Assert.Single((await ReadAsync(f)).Groups).Usage;
        Assert.Null(usage.InputTokens); Assert.Null(usage.OutputTokens); Assert.Null(usage.TotalTokens);
        Assert.Equal("0", usage.KnownTotalTokens); Assert.Equal("1", usage.UnknownTotalRounds);
        Assert.Equal("1", Assert.Single(usage.Failures, item => item.Category == "TurnInterrupted").Count);
    }

    [PostgresFact]
    public async Task Failed_turn_without_a_dispatched_round_has_a_separate_failure_category()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await f.StartAsync();
        Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, f.Key, turn.Intent.TurnId, turn.Version, ChatTurnPhase.Failed)).Outcome);
        var usage = Assert.Single((await ReadAsync(f)).Groups).Usage;
        Assert.Equal("0", usage.Rounds); Assert.Equal("0", usage.TotalTokens); Assert.Equal("1", usage.FailedTurns);
        Assert.Equal("TurnFailed", Assert.Single(usage.Failures).Category);
    }

    [PostgresFact]
    public async Task Restricted_usage_reads_recheck_project_authority_and_exclude_foreign_organizations()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        await f.StartAsync();
        await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            f.Key.OrganizationId, f.Revision.ProjectId, f.Actor.PrincipalId.Value);
        await using var store = new PostgresAdministrationStore(f.Case.RegressionConnectionString("braid_administration"));
        var query = Query(f.Key.OrganizationId, AiUsageGrouping.Project);
        var page = await store.ReadUsageAsync(f.Actor, query, default);
        Assert.NotNull(page); Assert.False(page.OrganizationWide); Assert.Equal(f.Revision.ProjectId, Assert.Single(page.Groups).ProjectId);
        Assert.Null(await store.ReadUsageAsync(f.Actor, Query(f.Seed.OrganizationB), default));
        await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND principal_id=$2",
            f.Key.OrganizationId, f.Actor.PrincipalId.Value);
        Assert.Null(await store.ReadUsageAsync(f.Actor, query, default));
        await PostgresExternalIdentityTestSupport.DeniedAsync(f.Case.RegressionConnectionString("braid_administration"), "SELECT * FROM braid.ai_usage_turns");
        await PostgresExternalIdentityTestSupport.DeniedAsync(f.Case.ChatWriterConnectionString, "DELETE FROM braid.ai_usage_turns");
    }

    [PostgresFact]
    public async Task Workspace_accounting_uses_each_original_turn_focus_instead_of_the_conversation_focus()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        var key = ChatConversationKey.ForWorkspace(f.Key.OrganizationId, f.Actor.PrincipalId, Guid.NewGuid());
        var conversation = ChatConversationIntent.ForWorkspace(key,
            new(new(f.Key.OrganizationId, f.Revision.ProjectId, f.Revision.MappingId)), f.Conversation.RetainUntilUtc);
        Assert.Equal(ChatStoreOutcome.Applied, await f.Writer.CreateAsync(f.Actor, conversation));
        foreach (var focus in new[] { ChatTurnFocus.None, ChatTurnFocus.Resolved(f.Revision) })
        {
            var original = await f.IntentAsync();
            var intent = new ChatTurnIntent(original.TurnId,
                new ChatOriginalEnvelope(original.Envelope.RequestId, original.OriginalRequest, [],
                    original.Envelope.ProviderBudget, original.Envelope.GroundingPolicy, focus),
                original.Profile, original.Budget, original.DeadlineUtc);
            var turn = Assert.IsType<ChatTurnFacts>((await f.Writer.StartAsync(f.Actor, key, intent)).Facts);
            var round = PostgresChatTestSupport.Round();
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.ReserveRoundAsync(f.Actor, key, intent.TurnId, turn.Version, round)).Turn);
            turn = Assert.IsType<ChatTurnFacts>((await f.Writer.CompleteRoundAsync(f.Actor, key, intent.TurnId, turn.Version,
                round.RoundId, round.Fence, ChatRoundOutcome.Stopped)).Facts);
            Assert.Equal(ChatStoreOutcome.Applied, (await f.Writer.FinishAsync(f.Actor, key, intent.TurnId, turn.Version, ChatTurnPhase.Completed)).Outcome);
            Assert.Equal(focus == ChatTurnFocus.None, await f.Case.ScalarAsync<bool>(
                "SELECT project_id IS NULL FROM braid.ai_usage_turns WHERE turn_id=$1", intent.TurnId));
        }
        await ReadAsync(f);
        await using var store = new PostgresAdministrationStore(f.Case.RegressionConnectionString("braid_administration"));
        var query = Query(f.Key.OrganizationId, AiUsageGrouping.Project);
        var first = await store.ReadUsageAsync(f.Actor, new(query.OrganizationId, query.Since, query.Until, query.Grouping, limit: 1), default);
        Assert.NotNull(first); Assert.Null(Assert.Single(first.Groups).ProjectId); Assert.NotNull(first.NextCursor);
        var next = await store.ReadUsageAsync(f.Actor, new(query.OrganizationId, query.Since, query.Until, query.Grouping, limit: 1, cursor: first.NextCursor), default);
        Assert.NotNull(next); Assert.Equal(f.Revision.ProjectId, Assert.Single(next.Groups).ProjectId); Assert.Null(next.NextCursor);
        await f.Case.ExecuteAsync("DELETE FROM braid.organization_administration_roles WHERE organization_id=$1 AND principal_id=$2", f.Key.OrganizationId, f.Actor.PrincipalId.Value);
        await f.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            f.Key.OrganizationId, f.Revision.ProjectId, f.Actor.PrincipalId.Value);
        var scoped = await store.ReadUsageAsync(f.Actor, query, default);
        Assert.NotNull(scoped); Assert.False(scoped.OrganizationWide); Assert.Equal(f.Revision.ProjectId, Assert.Single(scoped.Groups).ProjectId);
    }

    [PostgresFact]
    public async Task Upgrade_backfills_retained_usage_with_explicit_coverage_and_preserves_every_private_source_value()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await prefix.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1 AND project_id=$2", seed.Alice, seed.ProjectA);
        var actor = new ActorContext(new(seed.Alice));
        var receipt = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(31).ToArray()))
            await prefix.MigrateAsync();
        var revision = receipt.Facts.FutureRevision;
        var source = new RevisionReportSource(revision, receipt.Facts.NativeObject, receipt.Facts.Intent.LogicalDocument,
            receipt.Facts.Intent.DependencyIdentity, receipt.Facts.BackendProfile.BackendId);
        var projection = await PostgresWorkspaceChatUpgradeTests.SeedReportAsync(test, actor,
            new ProjectionCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
                "braid.mapping-projection.v1", "synthetic.usage-upgrade.v1", "synthetic.report.codec.v1", new()));
        var validation = await PostgresWorkspaceChatUpgradeTests.SeedReportAsync(test, actor,
            new ValidationCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
                "braid.mapping-validation.v1", "synthetic.usage-upgrade.v1", "synthetic.report.codec.v1", new()));
        var now = new DateTimeOffset(await test.ScalarAsync<DateTime>("SELECT statement_timestamp()"));
        var conversation = new ChatConversationIntent(new(revision, actor.PrincipalId, Guid.NewGuid()), projection, validation, now.AddHours(2));
        var id = Guid.NewGuid();
        var intent = new ChatTurnIntent(id, new ChatOriginalEnvelope(id, "PRIVATE retained question", [],
            ChatTurnPolicyTests.ProviderBudget(), ChatTurnPolicyTests.Grounding()), ChatTurnPolicyTests.Profile(), new(16, 65536, 3), now.AddMinutes(1));
        await PostgresWorkspaceChatUpgradeTests.WriteAsPrefixAsync(test, conversation, intent, PostgresChatTestSupport.Round(),
            ChatMetadataCodec.Citation(new(revision, projection, new(ChatCitationKind.ProjectionReport))));
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(55).ToArray()))
            await prefix.MigrateAsync();
        const string snapshot = """
            SELECT encode(sha256(convert_to(jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(c) ORDER BY conversation_id) FROM braid.chat_conversations c),
                (SELECT jsonb_agg(to_jsonb(t) ORDER BY turn_id) FROM braid.chat_turns t),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY round_id) FROM braid.chat_rounds r),
                (SELECT jsonb_agg(to_jsonb(e) ORDER BY turn_id,sequence) FROM braid.chat_events e),
                (SELECT jsonb_agg(to_jsonb(i) ORDER BY identity_kind,identity_id) FROM braid.chat_identities i),
                (SELECT jsonb_agg(to_jsonb(m) ORDER BY version) FROM braid.schema_migrations m WHERE version<=55))::text,'UTF8')),'hex')
            """;
        var before = await test.ScalarAsync<string>(snapshot);
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>(snapshot));
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, Guid.NewGuid());
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var page = await store.ReadUsageAsync(actor, Query(seed.OrganizationA), default);
        Assert.NotNull(page); Assert.True(page.IncludesPartialHistory); Assert.True(page.CompleteHistorySince > now);
        var usage = Assert.Single(page.Groups).Usage;
        Assert.Equal("1", usage.Turns); Assert.Equal("1", usage.Rounds); Assert.Null(usage.TotalTokens); Assert.Equal("1", usage.UnknownTotalRounds);
        await test.MigrateAsync();
        var repeated = await store.ReadUsageAsync(actor, Query(seed.OrganizationA), default);
        Assert.NotNull(repeated); Assert.Equal(page.CompleteHistorySince, repeated.CompleteHistorySince);
        Assert.Equal("1", Assert.Single(repeated.Groups).Usage.Turns);
    }

    [PostgresFact]
    public async Task Removing_every_usage_object_does_not_restore_legacy_admission()
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        await ReadAsync(f);
        await using var store = new PostgresAdministrationStore(f.Case.RegressionConnectionString("braid_administration"));
        Assert.NotNull(await store.ReadOrganizationAsync(f.Actor, f.Key.OrganizationId, default));
        await SchemaRemovalTestSupport.RemoveAsync(f.Case, AiUsageSchemaAdmission.Tables, AiUsageSchemaAdmission.Functions, AiUsageSchemaAdmission.TriggerNames);
        Assert.Equal(AiUsageSchemaAdmission.MigrationChecksum, await f.Case.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=56"));
        await using var owner = PostgresServerSessionTestSupport.Unpooled(f.Case.MigrationConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        Assert.True(await AiUsageSchemaAdmission.InstalledAsync(owner, transaction, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AiUsageSchemaAdmission.ValidateAsync(owner, transaction, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadOrganizationAsync(f.Actor, f.Key.OrganizationId, default));
    }

    [PostgresFact]
    public async Task Owner_can_distinguish_an_actual_pre_usage_migration_prefix()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(55).ToArray()))
            await prefix.MigrateAsync();
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        Assert.False(await AiUsageSchemaAdmission.InstalledAsync(owner, transaction, default));
        await AiUsageSchemaAdmission.ValidateAsync(owner, transaction, default);
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE braid.chat_turns DISABLE TRIGGER ai_usage_turn")]
    [InlineData("ALTER TABLE braid.ai_usage_turns ADD COLUMN private_text text")]
    [InlineData("GRANT SELECT ON braid.ai_usage_turns TO braid_administration")]
    public async Task Altered_usage_schema_is_refused_before_reading_or_admitting_chat(string change)
    {
        await using var f = await PostgresChatTestSupport.CreateAsync(fixture);
        await f.Case.ExecuteAsync(change);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reader.ReadAsync(f.Actor, f.Key));
        await using var store = new PostgresAdministrationStore(f.Case.RegressionConnectionString("braid_administration"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadUsageAsync(f.Actor, Query(f.Key.OrganizationId), default));
    }

    private static AiUsageQuery Query(Guid organization, AiUsageGrouping grouping = AiUsageGrouping.Organization) =>
        new(organization, new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero),
            new DateTimeOffset(DateTime.UtcNow.Date.AddDays(2), TimeSpan.Zero), grouping);

    private static async Task<AiUsagePageV1> ReadAsync(PostgresChatTestSupport f)
    {
        if (!await f.Case.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM braid.organization_administration_roles WHERE organization_id=$1 AND principal_id=$2)",
                f.Key.OrganizationId, f.Actor.PrincipalId.Value))
            await OrganizationAdministratorBootstrap.BootstrapAsync(f.Case.MigrationConnectionString, f.Key.OrganizationId, f.Actor.PrincipalId.Value, Guid.NewGuid());
        await using var store = new PostgresAdministrationStore(f.Case.RegressionConnectionString("braid_administration"));
        return Assert.IsType<AiUsagePageV1>(await store.ReadUsageAsync(f.Actor, Query(f.Key.OrganizationId), default));
    }
}
