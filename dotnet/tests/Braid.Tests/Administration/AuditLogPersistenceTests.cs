using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuditLogPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Journal_schema_matches_the_explicit_fingerprint()
    {
        Assert.Equal(AuditLogSchemaAdmission.MigrationChecksum, MigrationRunner.EmbeddedMigrations.Single(m => m.Version == 55).Checksum);
        await using var test = await fixture.CreateCaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction,
            ["audit_log_events"], default, validateAutomation: false, validateAudit: false);
        Assert.True(AuditLogSchemaAdmission.Fingerprint == fingerprint, "Audit journal schema fingerprint: " + fingerprint);
    }

    [PostgresFact]
    public async Task Current_scope_filters_paging_redaction_and_revocation_are_enforced()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("INSERT INTO braid.organization_administration_roles(organization_id,principal_id,role) VALUES($1,$2,'administrator')", seed.OrganizationA, seed.Alice);
        await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Admitted, "administrator");
        var target = Guid.NewGuid();
        foreach (var project in new Guid?[] { null, seed.ProjectA, seed.HiddenProjectA })
            await test.ExecuteAsync("INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id,project_id,before_state,after_state) VALUES($1,$2,'projectMember',$3,$4,'{\"secret\":\"PRIVATE MUST NOT LEAK\"}','{\"payload\":\"PRIVATE MUST NOT LEAK\"}')",
                seed.OrganizationA, seed.Alice, target, (object?)project ?? DBNull.Value);
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var since = AuditLogQuery.Microseconds(DateTimeOffset.UtcNow.AddDays(-1));
        var until = since.AddDays(2);
        var query = new AuditLogQuery(seed.OrganizationA, null, seed.Alice, "administration", since, until, 2);
        var first = await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, query, default);
        Assert.NotNull(first); Assert.True(first.OrganizationWide); Assert.Equal(2, first.Entries.Count); Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(first), StringComparison.Ordinal);
        var next = await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA,
            new(seed.OrganizationA, null, seed.Alice, "administration", since, until, 2, first.NextCursor), default);
        Assert.Single(next!.Entries); Assert.Null(next.NextCursor);
        Assert.DoesNotContain(next.Entries[0].EventId, first.Entries.Select(e => e.EventId));
        var projectOnly = await store.ReadAuditAsync(new(new(seed.Admitted)), seed.OrganizationA,
            new(seed.OrganizationA, null, null, "administration", since, until), default);
        Assert.NotNull(projectOnly); Assert.False(projectOnly.OrganizationWide);
        Assert.All(projectOnly.Entries, e => Assert.Equal(seed.ProjectA, e.ProjectId));
        Assert.Null(await store.ReadAuditAsync(new(new(seed.Admitted)), seed.OrganizationA,
            new(seed.OrganizationA, seed.HiddenProjectA, null, null, since, until), default));
        await test.ExecuteAsync("UPDATE braid.project_memberships SET active=false WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", seed.OrganizationA, seed.ProjectA, seed.Admitted);
        Assert.Null(await store.ReadAuditAsync(new(new(seed.Admitted)), seed.OrganizationA, query, default));
        Assert.Null(await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationB,
            new(seed.OrganizationB, null, null, null, since, until), default));
    }

    [PostgresFact]
    public async Task Upgrade_backfills_without_changing_private_source_audit_rows()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(54).ToArray())) await runner.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id,project_id,before_state) VALUES($1,$2,'projectMember',$3,$4,'{\"secret\":\"unchanged\"}')",
            seed.OrganizationA, seed.Alice, Guid.NewGuid(), seed.ProjectA);
        var before = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(a) ORDER BY sequence)::text FROM braid.administration_audit a");
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(a) ORDER BY sequence)::text FROM braid.administration_audit a"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events WHERE kind='administration'"));
        Assert.False(await test.ScalarAsync<bool>("SELECT bool_or(historical_observation) FROM braid.audit_log_events"));
    }

    [PostgresFact]
    public async Task Unknown_historical_transition_time_and_actor_are_explicit_observations()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var row = JsonSerializer.Serialize(new
        {
            organization_id = seed.OrganizationA,
            project_id = seed.ProjectA,
            mapping_id = Guid.NewGuid(),
            approval_id = Guid.NewGuid(),
            actor_id = seed.Alice,
            created_at = DateTimeOffset.UtcNow.AddDays(-2),
            revoked = true
        });
        await test.ExecuteAsync("SELECT braid.audit_log_record('build_approvals',$1::jsonb,NULL,true)", row);
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events WHERE action='revoked' AND actor_kind='unknown' AND actor_id IS NULL AND historical_observation"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events WHERE action='approved' AND NOT historical_observation"));
        await test.ExecuteAsync("SELECT braid.audit_log_record('build_approvals',$1::jsonb,NULL,true)", row);
        Assert.Equal(2L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events"));
    }

    [PostgresFact]
    public async Task Source_projection_never_serializes_private_columns()
    {
        await using var test = await fixture.CreateCaseAsync();
        foreach (var source in new[] { "administration_audit", "local_release_audit", "invocation_grant_audit", "machine_invocation_audit",
            "api_request_audit", "chat_payload_audit", "build_qualifications", "build_approvals", "build_runs", "build_publications" })
        {
            var value = await test.ScalarAsync<string>($$"""
                SELECT braid.audit_log_project('{{source}}',jsonb_populate_record(NULL::braid.{{source}},
                    '{"before_state":{"secret":"PRIVATE"},"after_state":{"secret":"PRIVATE"},"intent_body":"\\x50524956415445", "proof_body":"\\x50524956415445", "result_body":"\\x50524956415445"}'::jsonb))::text
                """);
            Assert.DoesNotContain("PRIVATE", value, StringComparison.Ordinal);
            using var projected = JsonDocument.Parse(value);
            Assert.All(projected.RootElement.EnumerateObject(), column =>
                Assert.DoesNotContain(column.Name, new[] { "before_state", "after_state", "intent_body", "proof_body", "result_body" }));
        }
    }

    [PostgresFact]
    public async Task Journal_is_append_only_and_rolls_back_with_the_source_write()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id) VALUES($1,$2,'organizationMember',$3)", connection, transaction);
            insert.Parameters.AddWithValue(seed.OrganizationA); insert.Parameters.AddWithValue(seed.Alice); insert.Parameters.AddWithValue(Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
            await using var count = new NpgsqlCommand("SELECT count(*) FROM braid.audit_log_events", connection, transaction);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        Assert.Equal(0L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events"));
        await test.ExecuteAsync("INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id) VALUES($1,$2,'organizationMember',$3)", seed.OrganizationA, seed.Alice, Guid.NewGuid());
        foreach (var sql in new[] { "UPDATE braid.audit_log_events SET outcome='changed'", "DELETE FROM braid.audit_log_events", "TRUNCATE braid.audit_log_events" })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql))).SqlState);
        foreach (var sql in new[] { "SELECT * FROM braid.audit_log_events", "DELETE FROM braid.audit_log_events", "TRUNCATE braid.audit_log_events" })
            await PostgresExternalIdentityTestSupport.DeniedAsync(test.RegressionConnectionString("braid_administration"), sql);
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.audit_log_events"));
    }

    [PostgresFact]
    public async Task Timestamp_ties_page_by_exact_uuid_without_duplicates_and_revocation_blocks_the_cursor()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("INSERT INTO braid.organization_administration_roles(organization_id,principal_id,role) VALUES($1,$2,'administrator')", seed.OrganizationA, seed.Alice);
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        foreach (var suffix in new[] { 1, 2, 3 })
            await test.ExecuteAsync("INSERT INTO braid.audit_log_events(organization_id,event_id,source,source_key,occurred_at,kind,action,actor_kind,resource_id,outcome) VALUES($1,$2,'test',$3,$4,'build','stateChanged','system',$2,'succeeded')",
                seed.OrganizationA, Guid.Parse($"12345678-0000-4000-8000-{suffix:000000000000}"), suffix.ToString(), at);
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var request = new AuditLogQuery(seed.OrganizationA, null, null, null, at.AddHours(-1), at.AddHours(1), 2);
        var first = await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, request, default);
        Assert.Equal(new[] { 3, 2 }, first!.Entries.Select(e => int.Parse(e.EventId.ToString("D")[^12..])));
        var nextRequest = new AuditLogQuery(seed.OrganizationA, null, null, null, request.Since, request.Until, 2, first.NextCursor);
        var last = await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, nextRequest, default);
        Assert.Single(last!.Entries); Assert.Null(last.NextCursor);
        Assert.EndsWith("000000000001", last.Entries[0].EventId.ToString("D"), StringComparison.Ordinal);
        await test.ExecuteAsync("DELETE FROM braid.organization_administration_roles WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, seed.Alice);
        Assert.Null(await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, nextRequest, default));
    }

    [PostgresFact]
    public async Task Existing_chat_and_regression_profiles_admit_only_the_complete_journal()
    {
        await using var test = await fixture.CreateCaseAsync();
        await RegressionSchemaTestContract.AdmitAsync(test);
        var principal = new PrincipalId(Guid.NewGuid());
        var key = new ChatConversationKey(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), principal, Guid.NewGuid());
        await using var chat = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        await using (var transaction = await chat.BeginAsync(new(principal), key, false, default)) { }
        await test.ExecuteAsync("ALTER TABLE braid.chat_payload_audit DISABLE TRIGGER audit_capture_payload");
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.BeginAsync(new(principal), key, false, default));
    }

    [PostgresFact]
    public async Task Removing_every_audit_object_does_not_restore_legacy_admission()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var actor = new ActorContext(new(seed.Alice));
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, Guid.NewGuid());
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        Assert.NotNull(await store.ReadOrganizationAsync(actor, seed.OrganizationA, default));
        await SchemaRemovalTestSupport.RemoveAsync(test, ["audit_log_events"], AuditLogSchemaAdmission.Functions, AuditLogSchemaAdmission.TriggerNames);
        Assert.Equal(AuditLogSchemaAdmission.MigrationChecksum, await test.ScalarAsync<string>("SELECT checksum FROM braid.schema_migrations WHERE version=55"));
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        Assert.True(await AuditLogSchemaAdmission.InstalledAsync(owner, transaction, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AuditLogSchemaAdmission.ValidateAsync(owner, transaction, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadOrganizationAsync(actor, seed.OrganizationA, default));
        await using var chat = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
        var key = ChatConversationKey.ForWorkspace(seed.OrganizationA, actor.PrincipalId, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.BeginAsync(actor, key, false, default));
    }

    [PostgresFact]
    public async Task Owner_can_distinguish_an_actual_pre_audit_migration_prefix()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(54).ToArray()))
            await prefix.MigrateAsync();
        await using var owner = PostgresServerSessionTestSupport.Unpooled(test.MigrationConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        Assert.False(await AuditLogSchemaAdmission.InstalledAsync(owner, transaction, default));
        await AuditLogSchemaAdmission.ValidateAsync(owner, transaction, default);
    }

    [PostgresTheory]
    [InlineData("ALTER TABLE braid.administration_audit DISABLE TRIGGER audit_capture_administration")]
    [InlineData("DROP TRIGGER audit_capture_build ON braid.build_runs")]
    [InlineData("GRANT SELECT ON braid.audit_log_events TO braid_administration")]
    [InlineData("ALTER TABLE braid.audit_log_events DISABLE TRIGGER audit_log_guard")]
    public async Task Altered_audit_boundary_is_refused_instead_of_hidden_from_legacy_contracts(string mutation)
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("INSERT INTO braid.organization_administration_roles(organization_id,principal_id,role) VALUES($1,$2,'administrator')", seed.OrganizationA, seed.Alice);
        await using var store = new PostgresAdministrationStore(test.RegressionConnectionString("braid_administration"));
        var query = new AuditLogQuery(seed.OrganizationA, null, null, null, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.NotNull(await store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, query, default));
        await test.ExecuteAsync(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAuditAsync(new(new(seed.Alice)), seed.OrganizationA, query, default));
    }
}
