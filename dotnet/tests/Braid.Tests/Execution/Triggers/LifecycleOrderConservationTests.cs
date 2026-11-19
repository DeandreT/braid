using Npgsql;
using Braid.Application.Lifecycle;
using Braid.Application.Retention;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LifecycleOrderConservationTests(PostgresFixture fixture)
{
    [PostgresTheory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    public async Task Clean_schema_matches_the_restricted_lifecycle_catalog_contract(int prefix)
    {
        await using var db = await fixture.CreateCaseAsync(false);
        await using var migrate = new MigrationRunner(db.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(prefix).ToArray());
        await migrate.MigrateAsync();
        await using var cn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.RegressionConnectionString("braid_lifecycle_worker")) { Pooling = false }.ConnectionString);
        await cn.OpenAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; " + LifecycleCatalogAdmission.CaptureSql, cn, tx);
        var captured = await command.ExecuteScalarAsync() as string;
        Assert.True(captured == (prefix switch { 64 => LifecycleCatalogAdmission.Fingerprint, 65 => LifecycleCatalogAdmission.NativeProfileFingerprint, 66 => LifecycleCatalogAdmission.RetentionHookFingerprint, 67 => LifecycleCatalogAdmission.CompiledXmlFingerprint, _ => throw new ArgumentOutOfRangeException(nameof(prefix)) }), $"Catalog fingerprint: {captured}");
    }

    [PostgresFact]
    public async Task Archived_retired_target_and_due_order_keep_exact_private_outbox_during_purge_and_expiry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var test = await TriggerPersistenceTests.Context.CreateAsync(fixture, migrations: 57);
        var work = await test.CompletedWorkAsync(await test.EnableAsync(), "synthetic-private-order");
        var db = test.Case;
        var target = test.Target;
        var org = target.Mapping.OrganizationId;
        var project = target.Mapping.ProjectId;
        var key = new ResourceKey(new(org, project), target.Mapping.MappingId);
        // Move only this disposable fixture's terminal timestamps into the past,
        // then restore the guard before snapshotting or upgrading the database.
        await db.ExecuteAsync("""
            BEGIN;
            ALTER TABLE braid.machine_invocations DISABLE TRIGGER machine_invocation_guard;
            ALTER TABLE braid.machine_invocation_results DISABLE TRIGGER machine_invocation_result_guard;
            ALTER TABLE braid.machine_invocation_audit DISABLE TRIGGER machine_invocation_audit_guard;
            UPDATE braid.machine_invocation_audit SET recorded_at=recorded_at-interval '2 hours';
            UPDATE braid.machine_invocation_results SET retained_at=retained_at-interval '2 hours',expires_at=expires_at-interval '2 hours';
            UPDATE braid.machine_invocations SET created_at=created_at-interval '2 hours',finished_at=finished_at-interval '2 hours' WHERE state='completed';
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE braid.machine_invocation_results ENABLE TRIGGER machine_invocation_result_guard;
            ALTER TABLE braid.machine_invocation_audit ENABLE TRIGGER machine_invocation_audit_guard;
            ALTER TABLE braid.machine_invocations ENABLE TRIGGER machine_invocation_guard;
            COMMIT;
            """);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocations WHERE finished_at<now()-interval '1 hour'"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE work_id=$1", work));
        Assert.True(await db.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM braid.trigger_payloads WHERE octet_length(body)>0)"));
        var before = await SnapshotAsync(db);
        var scripts = MigrationRunner.EmbeddedMigrations.Take(66).ToList();
        Assert.Equal(66, scripts.Count);
        await using (var upgrade = new MigrationRunner(db.MigrationConnectionString, scripts)) await upgrade.MigrateAsync(token);
        Assert.Equal(before, await SnapshotAsync(db));
        await db.ExecuteAsync("INSERT INTO braid.organization_administration_roles(organization_id,principal_id,role) VALUES($1,$2,'administrator') ON CONFLICT (organization_id,principal_id) DO UPDATE SET role='administrator'", org, test.Actor.PrincipalId.Value);
        await using var admin = new PostgresAdministrationStore(db.RegressionConnectionString("braid_administration"));
        await using var jobs = new PostgresDeletionJobStore(db.RegressionConnectionString("braid_lifecycle_worker"));
        var archive = await admin.ChangeAsync(test.Actor, new(key, ResourceLifecycleAction.Archive, Guid.NewGuid(), new(1, 1)), token);
        Assert.Equal(ResourceLifecycleOutcome.Archived, archive.Outcome);
        Assert.Equal(ResourcePurgeOutcome.ActiveRelease,
            (await admin.RequestAsync(test.Actor, new(key, Guid.NewGuid(), Guid.NewGuid(), archive.Receipt!.Version), token)).Outcome);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM braid.deletion_jobs"));
        var version = await db.ScalarAsync<long>("SELECT max(version) FROM braid.release_target_events WHERE target_id=$1", target.TargetId);
        var retired = await admin.RetireAsync(test.Actor, new(org, project, target.TargetId, Guid.NewGuid(), version, test.Invocation.Release.ReleaseId), token);
        Assert.Equal(TargetRetirementOutcome.Retired, retired.Outcome);
        Assert.Equal(RetentionPolicyOutcome.Updated, await admin.ReplaceRetentionAsync(test.Actor, org, 1,
            RetentionFamilyPolicy.Families.Select(f => f == RetentionFamily.InvocationHistory ? new RetentionFamilyPolicy(f, RetentionMode.Expire, 3600, true) : new(f, RetentionMode.NotConfigured, null, false)).ToArray(), token));

        // Hold the real organization gate on a third connection. The purge must
        // wait while the bounded sweep independently skips the pinned, due row.
        await using var holding = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await holding.OpenAsync(token);
        await using var barrier = await holding.BeginTransactionAsync(token);
        await LifecycleTransactionGate.AcquireAsync(holding, barrier, org, token);
        var purging = admin.RequestAsync(test.Actor, new(key, Guid.NewGuid(), Guid.NewGuid(), archive.Receipt!.Version), token);
        while (!await db.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND locktype='advisory' AND classid=1464158804 AND NOT granted)"))
        {
            if (purging.IsCompleted) throw new InvalidOperationException("Purge did not wait for the gate.", purging.Exception);
            await Task.Delay(10, token);
        }
        Assert.Equal(0, await jobs.SweepAsync(Guid.NewGuid(), 10, token));
        Assert.False(purging.IsCompleted);
        await barrier.CommitAsync(token);
        Assert.Equal(ResourcePurgeOutcome.ActiveRelease, (await purging).Outcome);
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM braid.deletion_jobs"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('braid_lifecycle_worker','braid.trigger_payloads','SELECT') OR has_table_privilege('braid_administration','braid.trigger_deliveries','SELECT')"));
    }

    // Compare all fields, including private bytes, without emitting any row contents.
    private static async Task<string> SnapshotAsync(PostgresCase db)
    {
        var digests = new List<string>();
        foreach (var table in new[] { "host_order_pins", "machine_invocations", "machine_invocation_audit", "machine_invocation_results", "local_releases", "trigger_work", "trigger_receipts", "trigger_payloads", "trigger_deliveries", "trigger_events" })
            digests.Add(await db.ScalarAsync<string>($"SELECT encode(sha256(convert_to(coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text)::text,'[]'),'UTF8')),'hex') FROM ONLY braid.{table} r"));
        return string.Join(':', digests);
    }
}
