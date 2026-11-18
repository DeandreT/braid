using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReleaseOperationsPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Publishers_can_activate_but_target_state_changes_retain_administrator_authority()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await data.Control.Runs.Cases.RoleAsync("publisher");
        var service = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, data.Target.Reference, new(Guid.NewGuid(), "activate", 1, data.First.Id)));
        var disable = new ReleaseOperation(Guid.NewGuid(), "disable", 2);
        Assert.Equal(ReleaseTransitionOutcome.Refused, await service.OperateAsync(data.Actor, data.Target.Reference, disable));
        Assert.True((await data.Store.ReadTargetAsync(data.Actor, data.Target.Reference))!.Enabled);
        await data.Control.Runs.Cases.RoleAsync("administrator");
        Assert.Equal(ReleaseTransitionOutcome.Refused, await service.OperateAsync(data.Actor, data.Target.Reference, disable));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, data.Target.Reference, disable with { OperationId = Guid.NewGuid() }));
    }

    [PostgresFact]
    public async Task Concurrent_operations_have_one_winner_and_do_not_overwrite_a_newer_version()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var other = data.NewStore();
        var first = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
        var second = new ReleaseOperationsService(other, other, data.Qualifications);
        var commands = new[] { new ReleaseOperation(Guid.NewGuid(), "activate", 1, data.First.Id), new ReleaseOperation(Guid.NewGuid(), "disable", 1) };
        var results = await Task.WhenAll(first.OperateAsync(data.Actor, data.Target.Reference, commands[0]), second.OperateAsync(data.Actor, data.Target.Reference, commands[1]));
        Assert.Single(results, value => value == ReleaseTransitionOutcome.Activated);
        Assert.Single(results, value => value == ReleaseTransitionOutcome.Conflict);
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT max(version) FROM braid.release_target_events"));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
    }

    [RegressionPostgresTheory]
    [InlineData("GRANT SELECT ON braid.release_target_operations TO braid_release_reader")]
    [InlineData("ALTER TABLE braid.release_target_events DISABLE TRIGGER release_events_guard")]
    [InlineData("ALTER FUNCTION braid.read_release_operations(uuid,uuid,uuid,uuid,text,integer,integer,timestamp with time zone,timestamp with time zone,uuid,text) SECURITY INVOKER")]
    [InlineData("ALTER FUNCTION braid.begin_as2_send STABLE")]
    public async Task Broadened_or_altered_metadata_contracts_fail_closed(string change)
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await data.Case.ExecuteAsync(change);
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(data.Actor, data.Target.Reference.Mapping, null, new("targets")));
    }

    [RegressionPostgresTheory]
    [InlineData("ALTER TABLE braid.machine_invocations DROP CONSTRAINT machine_invocations_state_check", "invocations")]
    [InlineData("ALTER TABLE braid.invocation_grants DROP CONSTRAINT invocation_grants_provider_check", "callers")]
    [InlineData("ALTER TABLE braid.machine_invocation_audit DISABLE TRIGGER machine_invocation_audit_guard", "summary")]
    [InlineData("ALTER FUNCTION braid.guard_machine_invocation() SECURITY DEFINER", "invocations")]
    public async Task Operations_reader_requires_the_invocation_constraints_and_audit_protections(string change, string section)
    {
        await using var data = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        Assert.NotNull(await reader.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new(section)));
        await data.Case.ExecuteAsync(change);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new(section)));
    }

    [RegressionPostgresTheory]
    [InlineData("machine_invocations", "invocations")]
    [InlineData("machine_invocation_audit", "summary")]
    [InlineData("invocation_grants", "callers")]
    public async Task Operations_reader_refuses_inherited_invocation_or_grant_rows(string table, string section)
    {
        await using var data = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await data.Claim(request);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await data.Finish(request, claim, MachineInvocationOutcome.Completed, [1]));
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        Assert.NotNull(await reader.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new(section)));
        // Only test-owned constants enter this SQL. A child needs neither the parent's
        // unique keys nor its audit triggers, and must never become operations history.
        await data.Case.ExecuteAsync($"CREATE TABLE braid.forged_operations_rows () INHERITS (braid.{table}); INSERT INTO braid.forged_operations_rows SELECT * FROM ONLY braid.{table}");
        Assert.True(await data.Case.ScalarAsync<long>("SELECT count(*) FROM ONLY braid.forged_operations_rows") > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new(section)));

        // The SQL itself must also exclude child rows if inheritance changes after
        // schema admission. Invoke the restricted metadata function without the adapter.
        await using var connection = PostgresServerSessionTestSupport.Unpooled(data.Case.RegressionConnectionString("braid_release_reader"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT set_config('braid.organization_id',@organization,true),set_config('braid.build_principal_id',@actor,true)", connection, transaction);
        command.Parameters.AddWithValue("organization", data.Target.Mapping.OrganizationId.ToString("D"));
        command.Parameters.AddWithValue("actor", data.Actor.PrincipalId.Value.ToString("D"));
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        command.CommandText = "SELECT braid.read_release_operations(@org,@project,@mapping,@target,@section,0,50,now()-interval '1 day',now()+interval '1 minute',NULL,NULL)";
        command.Parameters.AddWithValue("org", data.Target.Mapping.OrganizationId);
        command.Parameters.AddWithValue("project", data.Target.Mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", data.Target.Mapping.MappingId);
        command.Parameters.AddWithValue("target", data.Target.TargetId);
        command.Parameters.AddWithValue("section", section);
        using var result = System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.Equal(1, section == "summary" ? result.RootElement.GetProperty("count").GetInt32() : result.RootElement.GetArrayLength());
    }

    [RegressionPostgresTheory]
    [InlineData("ALTER TABLE braid.local_release_audit DISABLE TRIGGER automation_release_event")]
    [InlineData("ALTER TABLE braid.machine_invocation_audit DISABLE TRIGGER automation_endpoint_event")]
    [InlineData("ALTER FUNCTION braid.automation_gate(uuid,uuid,uuid,uuid) SECURITY INVOKER")]
    [InlineData("GRANT SELECT ON braid.automation_evaluations TO braid_release_reader")]
    [InlineData("CREATE INDEX operations_unqualified_target_index ON braid.local_release_targets(target_id)")]
    public async Task Operations_reader_admits_automation_and_retains_its_own_index_contract(string change)
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        Assert.NotNull(await reader.ReadAsync(data.Actor, data.Target.Reference.Mapping, null, new("targets")));
        await data.Case.ExecuteAsync(change);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(data.Actor, data.Target.Reference.Mapping, null, new("targets")));
    }

    [PostgresFact]
    public async Task Invocation_metadata_filters_and_summaries_never_read_retained_response_bytes()
    {
        await using var data = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var digest = Braid.Domain.Sha256Digest.FromBytes([1, 2, 3]);
        var claim = await data.Ledger.ClaimWithMetadataAsync(data.Key(request), data.Application, digest, 3);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, claim.Status);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await data.Finish(request, claim, MachineInvocationOutcome.Completed, [7, 8, 9]));
        var replay = await data.Ledger.ClaimWithMetadataAsync(data.Key(request), data.Application, digest, 3);
        Assert.Equal(MachineInvocationClaimStatus.Recorded, replay.Status);
        var rows = (await data.Releases.Store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId,
            new("invocations", Caller: data.Application, Outcome: "completed")))!.Value;
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(request, rows[0].GetProperty("request_id").GetGuid());
        Assert.Equal(3, rows[0].GetProperty("input_bytes").GetInt32());
        Assert.Equal(3, rows[0].GetProperty("output_bytes").GetInt32());
        Assert.True(rows[0].GetProperty("replayed").GetBoolean());
        Assert.Equal(1, rows[0].GetProperty("replay_count").GetInt32());
        Assert.DoesNotContain("BwgJ", rows.GetRawText());
        var empty = (await data.Releases.Store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId,
            new("invocations", Caller: Guid.NewGuid())))!.Value;
        Assert.Equal(0, empty.GetArrayLength());
        var summary = (await data.Releases.Store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new("summary")))!.Value;
        Assert.Equal(1, summary.GetProperty("count").GetInt64());
        Assert.Equal(0, summary.GetProperty("failed").GetInt64());
        Assert.True(summary.GetProperty("p95LatencyMs").GetDouble() >= 0);
        var callers = (await data.Releases.Store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new("callers")))!.Value;
        Assert.Equal(data.Grant.GrantId, callers[0].GetProperty("grant_id").GetGuid());
    }

    [PostgresFact]
    public async Task Conditional_activation_disable_enable_and_recovery_preserve_every_effect()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        var target = data.Target.Reference;
        var service = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
        var activate = new ReleaseOperation(Guid.NewGuid(), "activate", 1, data.First.Id);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, activate));
        var disable = new ReleaseOperation(Guid.NewGuid(), "disable", 2);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, disable));
        Assert.False((await data.Store.ReadTargetAsync(data.Actor, target))!.Enabled);
        Assert.Equal(ReleaseTransitionOutcome.Conflict, await service.OperateAsync(data.Actor, target, new(Guid.NewGuid(), "enable", 2)));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, new(Guid.NewGuid(), "enable", 3)));
        Assert.True((await data.Store.ReadTargetAsync(data.Actor, target))!.Enabled);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, disable));
        Assert.True((await data.Store.ReadTargetAsync(data.Actor, target))!.Enabled);
        Assert.Equal(ReleaseTransitionOutcome.IdempotencyConflict, await service.OperateAsync(data.Actor, target, disable with { Action = "enable" }));
        Assert.Equal(4L, await data.Case.ScalarAsync<long>("SELECT max(version) FROM braid.release_target_events"));
        Assert.Equal(4L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_releases"));
        var page = (await data.Store.ReadAsync(data.Actor, target.Mapping, target.TargetId, new("actions", Limit: 1)))!.Value;
        Assert.Equal(2, page.GetArrayLength()); // one look-ahead item for the HTTP page
    }

    [PostgresFact]
    public async Task Lost_commit_reply_recovers_original_outcome_without_reapplying_state()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        await using var relay = new ServerSessionCommitAckLossRelay(data.Connection);
        await using var routed = new PostgresLocalReleaseStore(relay.RoutedConnectionString, true);
        var operation = new ReleaseOperation(Guid.NewGuid(), "disable", 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = routed.OperateAsync(data.Actor, data.Target.Reference, operation, null, deadline.Token);
        try
        {
            await Task.WhenAny(pending, relay.CommitAcknowledgementWithheld).WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            Assert.True(relay.CommitAcknowledgementWithheld.IsCompletedSuccessfully && !pending.IsCompleted);
            relay.DropAcknowledgement();
            await Assert.ThrowsAnyAsync<Npgsql.NpgsqlException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token));
            Assert.Equal(ReleaseTransitionOutcome.Activated, await data.Store.OperateAsync(data.Actor, data.Target.Reference,
                new(Guid.NewGuid(), "enable", 2), null));
            var service = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
            Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, data.Target.Reference, operation));
            Assert.True((await data.Store.ReadTargetAsync(data.Actor, data.Target.Reference))!.Enabled);
            Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
        }
        finally { relay.DropAcknowledgement(); try { await pending; } catch (Npgsql.NpgsqlException) { } }
    }

    [PostgresFact]
    public async Task Concurrent_reuse_of_an_operation_id_across_projects_has_one_effect()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        var seed = data.Control.Runs.Cases.Seed;
        var actor = new ActorContext(new(seed.OtherProjectAdmin));
        var project = new Braid.Domain.ProjectScope(seed.OrganizationA, seed.HiddenProjectA);
        await using var database = new PostgresIngestionDatabase(data.Case.IngestionConnectionString);
        var ingestion = new PostgresRevisionIngestionStore(database);
        var reserved = await ingestion.ReserveAsync(actor, project, Guid.NewGuid(), PostgresIngestionTestSupport.CreateIntent(),
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        var facts = reserved.Receipt!.Facts;
        var claim = (await ingestion.ClaimAsync(actor, project, facts.OperationId, Guid.NewGuid())).Claim!;
        await ingestion.MarkStagedAsync(actor, claim, facts.NativeObject);
        await ingestion.CompleteAttemptAsync(actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(facts));
        await ingestion.FinalizeAsync(actor, claim);
        var environment = new BuildEnvironmentReference(project, Guid.NewGuid(), 1);
        Assert.Equal(BuildControlOutcome.Created, await data.Control.Store.SaveEnvironmentAsync(actor, new(environment, data.Target.Profile, true)));
        var otherTarget = new LocalReleaseTarget(new(new(project.OrganizationId, project.ProjectId, facts.FutureRevision.MappingId), environment, Guid.NewGuid()),
            data.Target.Profile, data.Target.RuntimeImage);
        Assert.Equal(ReleaseWriteOutcome.Created, await data.Store.RegisterAsync(actor, otherTarget));
        await using var other = data.NewStore();
        var operation = new ReleaseOperation(Guid.NewGuid(), "disable", 1);
        var results = await Task.WhenAll(data.Store.OperateAsync(data.Actor, data.Target.Reference, operation, null),
            other.OperateAsync(actor, otherTarget.Reference, operation, null));
        Assert.Single(results, value => value == ReleaseTransitionOutcome.Activated);
        Assert.Single(results, value => value == ReleaseTransitionOutcome.IdempotencyConflict);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.local_release_targets WHERE NOT enabled"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.release_target_operations"));
    }

    [PostgresFact]
    public async Task Invocation_pages_windows_failures_and_retryable_summaries_are_exact()
    {
        await using var data = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var failed = Guid.NewGuid(); var retryable = Guid.NewGuid(); var running = Guid.NewGuid();
        var failedClaim = await data.Claim(failed);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await data.Finish(failed, failedClaim, MachineInvocationOutcome.InvalidInput, null));
        var retryClaim = await data.Claim(retryable);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await data.Finish(retryable, retryClaim, MachineInvocationOutcome.Busy, null, provenance: false));
        await data.Claim(running);
        var after = DateTimeOffset.UtcNow.AddMinutes(1);
        var store = data.Releases.Store;
        var summary = (await store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId,
            new("summary", Since: before, Until: after)))!.Value;
        Assert.Equal(3, summary.GetProperty("count").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(1, summary.GetProperty("running").GetInt32());
        Assert.Equal(1, summary.GetProperty("retryable").GetInt32());
        Assert.Equal(1, summary.GetProperty("failuresByCategory").GetProperty("invalidInput").GetInt32());
        var filtered = (await store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId,
            new("invocations", Since: before, Until: after, Outcome: "invalidInput")))!.Value;
        Assert.Equal(failed, filtered[0].GetProperty("request_id").GetGuid());
        var empty = (await store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId,
            new("invocations", Since: after, Until: after.AddMinutes(1))))!.Value;
        Assert.Empty(empty.EnumerateArray());
        var page = (await store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new("invocations", Limit: 1)))!.Value;
        Assert.Equal(2, page.GetArrayLength());
        var next = (await store.ReadAsync(data.Actor, data.Target.Mapping, data.Target.TargetId, new("invocations", Offset: 1, Limit: 1)))!.Value;
        Assert.NotEqual(page[0].GetProperty("request_id").GetGuid(), next[0].GetProperty("request_id").GetGuid());
    }

    [PostgresFact]
    public async Task Metadata_is_shared_only_with_current_publishers_without_private_proofs()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        var first = await data.Request(data.First, null); await data.Apply(first);
        var other = new ActorContext(new(data.Control.Runs.Cases.Seed.Admitted));
        var target = data.Target.Reference;
        await data.Control.Runs.Cases.Seed.AddMembershipAsync(data.Case, target.Mapping.OrganizationId, target.Mapping.ProjectId, other.PrincipalId.Value, "publisher");
        await using var reader = new PostgresLocalReleaseStore(data.Case.RegressionConnectionString("braid_release_reader"), false);
        Assert.Null(await reader.ReadAsync(other, target, first.ReleaseId));
        var metadata = (await reader.ReadAsync(other, target.Mapping, target.TargetId, new("releases")))!.Value;
        Assert.Equal(first.ReleaseId, metadata[0].GetProperty("release_id").GetGuid());
        var text = metadata.GetRawText();
        foreach (var privateName in new[] { "proof_body", "result_body", "intent_body", "envelope", "request_digest", "body" })
            Assert.DoesNotContain(privateName, text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.OperateAsync(other, target, new(Guid.NewGuid(), "disable", 2), null));
        await data.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer' WHERE principal_id=$1", other.PrincipalId.Value);
        Assert.Null(await reader.ReadAsync(other, target.Mapping, target.TargetId, new("releases")));
        Assert.Equal(ReleaseTransitionOutcome.Refused, await data.Store.OperateAsync(other, target, new(Guid.NewGuid(), "disable", 2), null));
    }

    [RegressionPostgresTheory]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    public async Task Legacy_pointer_changes_are_versioned_and_existing_installations_upgrade_without_invented_history(int prefixVersion)
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture, migrations: prefixVersion);
        var first = await data.Request(data.First, null); await data.Apply(first);
        var binding = Guid.NewGuid();
        await data.Case.ExecuteAsync("""
            INSERT INTO braid.external_identity_bindings(binding_id,provider,authority,subject,principal_id)
                VALUES ($1,'test','operations-upgrade','display-owner',$2)
            """, binding, data.Actor.PrincipalId.Value);
        await data.Case.ExecuteAsync("""
            INSERT INTO braid.account_display_metadata(organization_id,binding_id,display_name,observed_at)
                VALUES ($1,$2,'Retained display','2026-10-25T00:00:00Z')
            """, data.Target.Reference.Mapping.OrganizationId, binding);
        await SeedPrecedingFeaturesAsync(data, prefixVersion);
        Assert.Equal(Enumerable.Range(1, prefixVersion), await data.Case.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        var tables = await data.Case.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='braid' AND c.relkind IN ('r','p') AND c.relname<>'schema_migrations'
            """);
        var columns = new Dictionary<string, string>();
        foreach (var table in tables)
            columns.Add(table, await data.Case.ScalarAsync<string>("""
                SELECT string_agg(quote_ident(column_name),',' ORDER BY ordinal_position)
                FROM information_schema.columns WHERE table_schema='braid' AND table_name=$1
                """, table));
        var before = await SnapshotRows(data.Case, columns);
        var history = await data.Case.ScalarAsync<string>(
            "SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m");
        await data.Case.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, 66), await data.Case.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM braid.schema_migrations"));
        Assert.Equal(history, await data.Case.ScalarAsync<string>(
            "SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m WHERE version<=$1", prefixVersion));
        Assert.Equal(before, await SnapshotRows(data.Case, columns));
        var target = data.Target.Reference;
        var initial = (await data.Store.ReadAsync(data.Actor, target.Mapping, target.TargetId, new("history")))!.Value;
        Assert.Single(initial.EnumerateArray());
        Assert.Equal("baseline", initial[0].GetProperty("origin").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, initial[0].GetProperty("actor_id").ValueKind);
        Assert.Equal(first.ReleaseId, initial[0].GetProperty("active_release_id").GetGuid());
        Assert.True(await data.Store.DisableAsync(data.Actor, target));
        Assert.Equal(2L, await data.Case.ScalarAsync<long>("SELECT max(version) FROM braid.release_target_events"));
        var service = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
        Assert.Equal(ReleaseTransitionOutcome.Conflict, await service.OperateAsync(data.Actor, target, new(Guid.NewGuid(), "enable", 1)));
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, new(Guid.NewGuid(), "enable", 2)));
        Assert.Equal(ReleaseTransitionOutcome.Replayed, await data.Apply(first));
        Assert.Equal(3L, await data.Case.ScalarAsync<long>("SELECT max(version) FROM braid.release_target_events"));
    }

    private static async Task SeedPrecedingFeaturesAsync(LocalReleasePersistenceTests.Context data, int prefixVersion)
    {
        var mapping = data.Target.Reference.Mapping;
        var project = new ProjectScope(mapping.OrganizationId, mapping.ProjectId);
        if (prefixVersion >= 27)
        {
            await OrganizationAdministratorBootstrap.BootstrapAsync(data.Case.MigrationConnectionString,
                project.OrganizationId, data.Actor.PrincipalId.Value, Guid.NewGuid());
            Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.administration_audit"));
        }
        if (prefixVersion < 28) return;

        // Synthetic conversion evidence tests persistence only; no native worker is invoked.
        var package = new InterchangePackage("retained.mfd", [new("retained.mfd", "<self-authored/>"u8)]);
        var review = new ImportReview(Guid.NewGuid(), data.Actor.PrincipalId,
            ImportDestination.Create(project, "ferrule", new("Retained import", "")), package,
            new(PostgresIngestionTestSupport.Profile, package.Digest, PostgresIngestionTestSupport.SyntheticBody,
                new("Synthetic conversion", [new("unsupported", "Retained warning")])), DateTimeOffset.UtcNow.AddHours(1));
        await using var database = new PostgresIngestionDatabase(data.Case.IngestionConnectionString);
        var store = new PostgresRevisionIngestionStore(database);
        await store.SaveAsync(data.Actor, review);
        var provenance = new InterchangeImportProvenance(package, review.Conversion.NativeDigest,
            review.Digest, review.Conversion.Report.Digest, review.Conversion.Profile);
        var intent = IngestionIntent.ForCreate(new("Retained imported mapping", ""), "ferrule", "retained.mfd",
            review.Conversion.NativeDigest, review.Conversion.Native.Length, provenance);
        var result = await store.ReserveAsync(data.Actor, project, Guid.NewGuid(), intent,
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, result.Outcome);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.import_reviews"));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_import_provenance"));
        if (prefixVersion < 29) return;

        Assert.True(await CatalogSearchUpgradeTestSupport.AdmitAsync(data.Case));
        var reserved = result.Receipt!.Facts;
        var claim = (await store.ClaimAsync(data.Actor, project, reserved.OperationId, Guid.NewGuid())).Claim!;
        Assert.Equal(IngestionStoreOutcome.Applied, (await store.MarkStagedAsync(data.Actor, claim, reserved.NativeObject)).Outcome);
        await store.CompleteAttemptAsync(data.Actor, claim, PostgresIngestionTestSupport.SyntheticCompletion(reserved));
        var committed = (await store.FinalizeAsync(data.Actor, claim)).Receipt!;
        Assert.Equal(IngestionPhase.Committed, committed.Phase);
        var key = await PostgresServerSessionUpgradeTests.SeedHistoricalReportAsync(data.Case, data.Actor, committed);
        // Synthetic report/index metadata exercises conservation without reading private bytes or running a worker.
        await data.Case.ExecuteAsync("""
            INSERT INTO braid.catalog_search_report_index
                (organization_id,project_id,mapping_id,revision_id,derivation_id,derived_digest,terms,search_text)
            SELECT a.organization_id,a.project_id,a.mapping_id,a.revision_id,a.derivation_id,a.derived_digest,
                '[{"kind":4,"id":"retained-field","title":"Retained field","path":"/retained"}]'::jsonb,'Retained field /retained'
            FROM braid.revision_derivation_attempts a JOIN braid.revision_derivations d USING(cache_entry_id)
            WHERE d.key_fingerprint=$1 AND a.derivation_id=d.selected_derivation_id
            """, key.Fingerprint.Value);
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM braid.catalog_search_report_index"));
        if (prefixVersion < 30) return;

        var suiteId = Guid.NewGuid();
        await HistoricalRegressionAccess.SeedAutomationFailureAsync(data.Control.Runs, suiteId);
        foreach (var table in new[] { "automation_suites", "automation_evaluations", "notification_events", "notifications" })
            Assert.True(await data.Case.ScalarAsync<long>($"SELECT count(*) FROM ONLY braid.{table}") > 0);
    }

    private static async Task<string> SnapshotRows(PostgresCase test, IReadOnlyDictionary<string, string> columns)
    {
        var rows = new System.Text.StringBuilder();
        foreach (var (table, projection) in columns)
        {
            var quoted = '"' + table.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
            // Compare every pre-upgrade field. Additive migration columns are not old facts.
            rows.Append(table).Append('=').Append(await test.ScalarAsync<string>(
                $"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM (SELECT {projection} FROM ONLY braid.{quoted}) r")).Append('\n');
        }
        return rows.ToString();
    }

    [PostgresFact]
    public async Task Rollback_retains_original_qualification_and_revoked_approval_stays_refused()
    {
        await using var data = await LocalReleasePersistenceTests.Context.CreateAsync(fixture);
        var target = data.Target.Reference;
        var first = await data.Request(data.First, null); await data.Apply(first);
        var secondQualification = await data.Build([4, 5]);
        var second = await data.Request(secondQualification, first.ReleaseId); await data.Apply(second);
        var service = new ReleaseOperationsService(data.Store, data.Store, data.Qualifications);
        var rollback = new ReleaseOperation(Guid.NewGuid(), "rollback", 3, data.First.Id, first.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, rollback));
        Assert.Equal(rollback.OperationId, (await data.Store.ReadTargetAsync(data.Actor, target))!.ActiveReleaseId);
        await data.Control.Store.RevokeAsync(data.Actor, target.Mapping.OrganizationId, target.Mapping.ProjectId, data.First.Artifact.Intent.ApprovalId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await service.OperateAsync(data.Actor, target, rollback));
        Assert.Equal(ReleaseTransitionOutcome.Refused, await service.OperateAsync(data.Actor, target, new(Guid.NewGuid(), "activate", 4, data.First.Id)));
        Assert.Equal(rollback.OperationId, (await data.Store.ReadTargetAsync(data.Actor, target))!.ActiveReleaseId);
    }
}
