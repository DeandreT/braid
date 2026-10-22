using System.Text.RegularExpressions;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual PostgreSQL privileges/admission. All source, publication and clock fixtures are synthetic metadata.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPrivateObjectMaintenanceSecurityTests(PostgresFixture fixture)
{
    private const string Login = "weft_maintenance";
    private static readonly string[] Tables = ["private_object_publications", "private_object_retirements", "private_object_cleanup_attempts"];
    private static readonly string[] EntrySignatures =
    [
        "claim_private_object_cleanup_v1(uuid, uuid, uuid, jsonb)",
        "finish_private_object_cleanup_v1(uuid, uuid, jsonb, text)",
        "get_private_object_cleanup_attempt_v1(uuid, uuid, uuid, bigint)"
    ];
    private static readonly string[] FunctionNames =
    [
        "assert_private_object_context_v1", "lock_private_object_origin_v1", "private_publication_owner_live_v1",
        "private_object_cleanup_safety_v1", "guard_private_object_publications_v1", "guard_native_retirement_attachment_v1",
        "guard_derived_retirement_attachment_v1", "guard_private_object_retirements_v1", "guard_private_object_cleanup_attempts_v1",
        "private_publication_record_v1", "private_object_cleanup_claim_json_v1", "canonical_private_object_cleanup_claim_v1",
        "claim_private_object_cleanup_v1", "finish_private_object_cleanup_v1", "get_private_object_cleanup_attempt_v1"
    ];
    private static readonly string[] OrdinaryLogins =
        ["weft_runtime", "weft_ingest", "weft_catalog_reader", "weft_catalog_editor", "weft_project_creator", "weft_report_reader", "weft_deriver"];
    private static readonly string[] PublicationInsert =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id", "root_id", "temp_id",
        "owner_kind", "actor_principal_id", "ingestion_operation_id", "cache_entry_id", "derivation_id", "owner_generation",
        "owner_instance_id", "owner_token", "owner_lease_expires_at", "absence_record_digest", "storage_kind"
    ];
    private static readonly string[] PublicationBind =
    [
        "control_namespace_id", "linux_boot_id", "mount_namespace_device", "mount_namespace_inode", "effective_user_id",
        "file_inode", "file_device_major", "file_device_minor", "file_mount_id", "directory_inode", "directory_device_major",
        "directory_device_minor", "directory_mount_id", "bound_record_digest"
    ];
    private static readonly string[] PublicationRead =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id", "publication_id", "root_id",
        "temp_id", "format", "owner_kind", "actor_principal_id", "ingestion_operation_id", "cache_entry_id", "derivation_id",
        "native_object_id", "candidate_object_id",
        "owner_generation", "owner_instance_id", "owner_token", "owner_lease_expires_at", "absence_record_digest", "registered_at",
        .. PublicationBind, "bound_at", "storage_kind"
    ];
    private static readonly string[] RetirementRead =
        ["organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id", "target_kind", "publication_id"];
    private static readonly (string Table, string Name)[] Triggers =
    [
        ("private_object_publications", "private_object_publications_transition_guard"),
        ("private_object_retirements", "private_object_retirements_transition_guard"),
        ("private_object_cleanup_attempts", "private_object_cleanup_attempts_transition_guard"),
        ("revision_ingestions", "revision_ingestions_z_retirement_guard"),
        ("revision_ingestion_attempts", "revision_ingestion_attempts_z_retirement_guard"),
        ("mapping_revisions", "mapping_revisions_z_retirement_guard"),
        ("revision_derivation_attempts", "revision_derivation_attempts_z_retirement_guard"),
        ("revision_derivations", "revision_derivations_z_retirement_guard")
    ];
    private static readonly string[] Indexes =
    [
        "private_object_publications_temporary_scan", "private_object_retirements_final_key",
        "private_object_retirements_temporary_key", "private_object_retirements_due_scan",
        "revision_ingestions_cleanup_scan", "revision_derivation_attempts_cleanup_scan"
    ];

    [PostgresFact]
    public async Task MaintenanceLoginHasExactlyThreeReadRelationsAndThreeCommandsWithNoSourceOrAdministrativeRights()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        Assert.Equal(Tables.Order(StringComparer.Ordinal), await test.Case.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname::text COLLATE "C") FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='weft' AND c.relkind IN ('r','p','v','m','f')
              AND has_table_privilege('weft_maintenance',c.oid,'SELECT')
            """));
        Assert.Equal(EntrySignatures, await test.Case.ScalarAsync<string[]>("""
            SELECT array_agg(p.proname||'('||oidvectortypes(p.proargtypes)||')' ORDER BY p.proname COLLATE "C")
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='weft'
              AND has_function_privilege('weft_maintenance',p.oid,'EXECUTE')
            """));
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolsuper AND NOT r.rolbypassrls
              AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
              AND NOT EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
              AND has_database_privilege(r.oid,current_database(),'CONNECT')
              AND NOT has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
              AND has_schema_privilege(r.oid,'weft','USAGE')
              AND NOT has_schema_privilege(r.oid,'weft','CREATE,USAGE WITH GRANT OPTION')
              AND NOT has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
              AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relkind IN ('r','p','v','m','f') AND
                  (has_table_privilege(r.oid,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
                   OR has_any_column_privilege(r.oid,c.oid,'INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')))
              AND NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND CASE WHEN c.relkind='S'
                  THEN has_sequence_privilege(r.oid,c.oid,'USAGE,SELECT,UPDATE') ELSE false END)
            FROM pg_roles r WHERE rolname='weft_maintenance'
            """));
        var before = await test.OriginSnapshotAsync();
        await using var direct = NpgsqlDataSource.Create(test.Case.MaintenanceConnectionString);
        foreach (var sql in new[]
        {
            "SELECT * FROM weft.principals WHERE false", "SELECT * FROM weft.mapping_revisions WHERE false",
            "SELECT * FROM weft.revision_ingestions WHERE false", "SELECT * FROM weft.revision_derivation_attempts WHERE false",
            "SELECT checksum FROM weft.schema_migrations", "INSERT INTO weft.private_object_retirements(retirement_id) VALUES(gen_random_uuid())",
            "UPDATE weft.private_object_publications SET bound_at=clock_timestamp() WHERE false",
            "DELETE FROM weft.private_object_cleanup_attempts WHERE false", "TRUNCATE weft.private_object_retirements",
            "SELECT weft.assert_project_creation_contract_v1()", "SELECT weft.guard_revision_derivations_transition()",
            "SET ROLE weft_migrator", "SET ROLE weft_ingest", "SET session_replication_role=replica",
            "CREATE SCHEMA maintenance_probe", "CREATE TABLE weft.maintenance_probe(id integer)", "CREATE TEMP TABLE maintenance_probe(id integer)"
        }) await AssertDeniedAsync(direct, sql);
        Assert.Equal(before, await test.OriginSnapshotAsync());
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task OrdinaryPublicationRightsAreExactNamedColumnsAndNeverGrantCleanupCommandsOrPrivateRetirementFacts()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        foreach (var login in OrdinaryLogins)
        {
            var publishes = login is "weft_ingest" or "weft_deriver";
            foreach (var table in Tables)
            {
                Assert.False(await test.Case.ScalarAsync<bool>(
                    "SELECT has_table_privilege($1,$2,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')", login, "weft." + table));
                Assert.Equal((publishes && table == Tables[0] ? PublicationRead : publishes && table == Tables[1] ? RetirementRead : []).Order(StringComparer.Ordinal),
                    await ColumnsAsync(test.Case, login, table, "SELECT"));
                Assert.Equal((publishes && table == Tables[0] ? PublicationInsert : []).Order(StringComparer.Ordinal),
                    await ColumnsAsync(test.Case, login, table, "INSERT"));
                Assert.Equal((publishes && table == Tables[0] ? PublicationBind : []).Order(StringComparer.Ordinal),
                    await ColumnsAsync(test.Case, login, table, "UPDATE"));
                Assert.Empty(await ColumnsAsync(test.Case, login, table, "REFERENCES"));
                Assert.False(await test.Case.ScalarAsync<bool>(
                    "SELECT has_any_column_privilege($1,$2,'SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION,REFERENCES WITH GRANT OPTION')",
                    login, "weft." + table));
            }
            Assert.False(await test.Case.ScalarAsync<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                  WHERE n.nspname='weft' AND p.proname=ANY($2::text[]) AND has_function_privilege($1,p.oid,'EXECUTE'))
                """, login, FunctionNames));
        }
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task RawOrdinaryBindingRejectsEachFileDirectoryIdentityMismatchWithoutChangingTheReservedRows()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        const string bindSql = """
            UPDATE weft.private_object_publications SET control_namespace_id=$1,linux_boot_id=$2,
              mount_namespace_device=$3,mount_namespace_inode=$4,effective_user_id=$5,
              file_inode=$6,file_device_major=$7,file_device_minor=$8,file_mount_id=$9,
              directory_inode=$10,directory_device_major=$11,directory_device_minor=$12,directory_mount_id=$13,
              bound_record_digest=$14 WHERE organization_id=$15 AND project_id=$16 AND publication_id=$17
            """;
        foreach (var reportOwner in new[] { false, true })
        {
            IPrivateObjectPublicationRegistry registry;
            PrivatePublicationOwner owner;
            PrivateObjectId target;
            if (reportOwner)
            {
                var claim = await test.ReserveReportAsync();
                registry = test.ReportPublications;
                owner = PrivatePublicationOwner.ForReport(test.Actor, claim);
                target = claim.CandidateObjectId;
            }
            else
            {
                var facts = await test.ReserveNativeAsync();
                var claim = await test.ClaimNativeAsync(facts);
                registry = test.NativePublications;
                owner = PrivatePublicationOwner.ForIngestion(test.Actor, claim);
                target = facts.NativeObject.Id;
            }
            var root = Guid.NewGuid();
            var temp = Guid.NewGuid();
            var reserved = await registry.ReserveAsync(owner, PrivatePublicationStoreKind.FileSystem, root, target, temp,
                PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
            Assert.Equal(PrivatePublicationReserveOutcome.Reserved, reserved.Outcome);
            var record = reserved.Snapshot!.Record;
            Assert.Null(record.Binding);
            var original = await test.PublicationSnapshotAsync(record.Reservation.PublicationId);
            // These are authored Linux identity metadata, never descriptor/file evidence.
            // Start with a C#-materializable value and change exactly one SQL coherence relation.
            var identity = PostgresPrivateObjectMaintenanceTestSupport.SyntheticIdentity(root);
            foreach (var mismatch in new[] { "deviceMajor", "deviceMinor", "mountId", "sameInode" })
            {
                if (reportOwner)
                {
                    await using var transaction = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId);
                    await AssertIdentityBindingRefusedAsync(transaction.CreateCommand(bindSql), record.Reservation, identity, mismatch);
                }
                else
                {
                    await using var transaction = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId);
                    await AssertIdentityBindingRefusedAsync(transaction.CreateCommand(bindSql), record.Reservation, identity, mismatch);
                }
                // The failed real UPDATE has settled and its transaction has rolled back.
                Assert.Equal(original, await test.PublicationSnapshotAsync(record.Reservation.PublicationId));
                var recovered = await registry.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, root, target, temp);
                Assert.Equal(record, recovered!.Record);
                Assert.Null(recovered.Record.Binding);
            }
            var valid = await registry.BindAsync(record.Reservation, identity,
                PostgresPrivateObjectMaintenanceTestSupport.BoundDigest);
            Assert.Equal(PrivatePublicationBindOutcome.Bound, valid.Outcome);
            Assert.Equal(identity, valid.Snapshot!.Record.Binding!.ActualIdentity);
            Assert.Equal(valid.Snapshot.Record, (await registry.GetAsync(owner, PrivatePublicationStoreKind.FileSystem, root, target, temp))!.Record);
        }
        Assert.Equal(new long[] { 0, 0, 2 }, await test.CountsAsync());
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task UnboundForeignOrganizationAndOtherProjectReadsHideActualPublicationRetirementAndAttemptRows()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var facts = await test.ReserveNativeAsync();
        var native = await test.ClaimNativeAsync(facts);
        var published = await test.NativePublications.ReserveAsync(PrivatePublicationOwner.ForIngestion(test.Actor, native),
            PrivatePublicationStoreKind.FileSystem, Guid.NewGuid(), facts.NativeObject.Id, Guid.NewGuid(), PostgresPrivateObjectMaintenanceTestSupport.AbsenceDigest);
        Assert.Equal(PrivatePublicationReserveOutcome.Reserved, published.Outcome);
        var rejected = await test.CreateRejectedNativeAsync();
        var cleanup = await test.ClaimSingleAsync(rejected.NativeObject.Id);
        Assert.Equal(new long[] { 1, 1, 1 }, await test.CountsAsync());
        await using var direct = NpgsqlDataSource.Create(test.Case.MaintenanceConnectionString);
        foreach (var table in Tables)
        {
            await using var hidden = direct.CreateCommand("SELECT count(*) FROM weft." + table);
            Assert.Equal(0L, await hidden.ExecuteScalarAsync());
        }
        foreach (var scope in new[] { test.Project, new ProjectScope(test.Project.OrganizationId, test.Seed.HiddenProjectA),
            new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB) })
        {
            await using var connection = await direct.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var bind = new NpgsqlCommand("SELECT set_config('weft.organization_id',$1,true),set_config('weft.project_id',$2,true)", connection, transaction))
            {
                bind.Parameters.AddWithValue(scope.OrganizationId.ToString("D")); bind.Parameters.AddWithValue(scope.ProjectId.ToString("D"));
                await bind.ExecuteNonQueryAsync();
            }
            foreach (var table in Tables)
            {
                await using var visible = new NpgsqlCommand("SELECT count(*) FROM weft." + table, connection, transaction);
                Assert.Equal(scope == test.Project ? 1L : 0L, await visible.ExecuteScalarAsync());
            }
        }
        Assert.NotNull(await test.Maintenance.GetAttemptAsync(test.Project, cleanup.Retirement.RetirementId, cleanup.Generation));
    }

    [PostgresFact]
    public async Task MaintenanceLeaseUsesExactProjectSafeModesAndBothContextsResetOnTheSamePhysicalConnection()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var pool = test.MaintenanceDatabase.DataSource;
        var pid = await ProjectCreationTestSupport.BackendAsync(pool);
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            await using var command = lease.CreateCommand("""
                SELECT pg_backend_pid(),current_setting('transaction_isolation'),current_setting('transaction_read_only'),
                  current_setting('lock_timeout'),current_setting('statement_timeout'),current_setting('row_security'),
                  current_setting('session_replication_role'),current_setting('weft.organization_id'),current_setting('weft.project_id')
                """);
            await using var row = await command.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(pid, row.GetInt32(0)); Assert.Equal("read committed", row.GetString(1)); Assert.Equal("off", row.GetString(2));
            Assert.Equal("1s", row.GetString(3)); Assert.Equal("5s", row.GetString(4)); Assert.Equal("on", row.GetString(5));
            Assert.Equal("origin", row.GetString(6)); Assert.Equal(test.Project.OrganizationId.ToString("D"), row.GetString(7));
            Assert.Equal(test.Project.ProjectId.ToString("D"), row.GetString(8));
        }
        await AssertUnboundAsync(pool, pid);
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RequireProjectAsync(
                new ProjectScope(test.Project.OrganizationId, test.Seed.HiddenProjectA), CancellationToken.None));
        }
        await AssertUnboundAsync(pool, pid);
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task CanceledMaintenanceCommandSettlesAndReturnsAnUnboundReusablePhysicalConnection()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var pid = await ProjectCreationTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        using var cancellation = new CancellationTokenSource();
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            await using var command = lease.CreateCommand("SELECT pg_sleep(20)");
            var pending = command.ExecuteNonQueryAsync(cancellation.Token);
            await WaitForSleepAsync(test.Case, pid, pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(pending.IsCompleted);
        }
        await AssertUnboundAsync(test.MaintenanceDatabase.DataSource, pid);
        await AssertHealthyAsync(test);
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ActualMaintenanceLockAndStatementTimeoutsRollBackAndLeaveTheSamePoolReusable()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        var pid = await ProjectCreationTestSupport.BackendAsync(test.MaintenanceDatabase.DataSource);
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        await using (var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test.Case))
        await using (var blocker = await owner.BeginTransactionAsync())
        {
            await using (var hold = new NpgsqlCommand("LOCK TABLE weft.private_object_retirements IN ACCESS EXCLUSIVE MODE", owner, blocker))
                await hold.ExecuteNonQueryAsync();
            await using var blocked = lease.CreateCommand("SELECT count(*) FROM weft.private_object_retirements");
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => blocked.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, refusal.SqlState);
        }
        await AssertUnboundAsync(test.MaintenanceDatabase.DataSource, pid);
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project))
        {
            await using var slow = lease.CreateCommand("SELECT pg_sleep(20)");
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => slow.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.QueryCanceled, refusal.SqlState);
        }
        await AssertUnboundAsync(test.MaintenanceDatabase.DataSource, pid);
        await AssertHealthyAsync(test);
        Assert.Equal(new long[] { 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task MaintenancePoolRefusesWrongExistingLoginsAndPresetOrganizationOrProjectContexts()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        foreach (var connectionString in new[] { test.Case.MigrationConnectionString, test.Case.RuntimeConnectionString,
            test.Case.IngestionConnectionString, test.Case.CatalogConnectionString, test.Case.MetadataConnectionString,
            test.Case.ProjectCreationConnectionString, test.Case.ReportConnectionString, test.Case.DerivationConnectionString })
        {
            await using var wrong = new PostgresPrivateObjectMaintenanceDatabase(connectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            { await using var _ = await wrong.BeginTransactionAsync(test.Project); });
        }
        foreach (var setting in new[] { "weft.organization_id", "weft.project_id" })
        {
            var configured = new NpgsqlConnectionStringBuilder(test.Case.MaintenanceConnectionString)
            { Options = "-c " + setting + "=" + Guid.NewGuid().ToString("D") };
            await using var preset = new PostgresPrivateObjectMaintenanceDatabase(configured.ConnectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            { await using var _ = await preset.BeginTransactionAsync(test.Project); });
        }
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task MissingDisabledReplicaOrExtraSupplementalTriggersRefuseEveryAffectedOrdinaryPoolThenRestore()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var (table, trigger) in Triggers)
        {
            foreach (var mode in new[] { "DISABLE", "ENABLE REPLICA" })
                await MutationAsync(test, $"ALTER TABLE weft.{table} {mode} TRIGGER {trigger}",
                    $"ALTER TABLE weft.{table} ENABLE TRIGGER {trigger}", () => AssertAffectedRefusedAsync(test));
            await MutationAsync(test, $"DROP TRIGGER {trigger} ON weft.{table}", TrustedTrigger(trigger),
                () => AssertAffectedRefusedAsync(test));
            var extra = TrustedTrigger(trigger).Replace("CREATE TRIGGER " + trigger, "CREATE TRIGGER maintenance_extra_guard", StringComparison.Ordinal);
            await MutationAsync(test, extra, $"DROP TRIGGER maintenance_extra_guard ON weft.{table}",
                () => AssertAffectedRefusedAsync(test));
        }
    }

    [PostgresFact]
    public async Task MissingOrStructurallyDifferentCleanupIndexesRefuseEveryAffectedPoolThenRestore()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var index in Indexes)
        {
            var original = TrustedIndex(index);
            await MutationAsync(test, "DROP INDEX weft." + index, original, () => AssertAffectedRefusedAsync(test));
            // The same name, uniqueness and predicate do not excuse a different ordered key.
            var changed = original.Replace("(organization_id,project_id,", "(project_id,organization_id,", StringComparison.Ordinal);
            Assert.NotEqual(original, changed);
            await test.Case.ExecuteAsync("DROP INDEX weft." + index);
            try
            {
                await test.Case.ExecuteAsync(changed);
                await AssertAffectedRefusedAsync(test);
            }
            finally
            {
                await test.Case.ExecuteAsync("DROP INDEX IF EXISTS weft." + index);
                await test.Case.ExecuteAsync(original);
            }
            await AssertHealthyAsync(test);
        }
        await MutationAsync(test, "CREATE INDEX maintenance_extra_scan ON weft.private_object_retirements(retired_at)",
            "DROP INDEX weft.maintenance_extra_scan", () => AssertAffectedRefusedAsync(test));
    }

    [PostgresFact]
    public async Task ChangedRoutineBodySearchPathSecurityVolatilityOrPublicExecuteRefusesEveryAffectedPool()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        Assert.Equal(FunctionNames.Order(StringComparer.Ordinal), MaintenanceSchemaContract.Functions.Keys.Order(StringComparer.Ordinal));
        foreach (var function in MaintenanceSchemaContract.Functions.Values.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            var signature = "weft." + function.Name + "(" + function.IdentityArguments + ")";
            var original = TrustedFunction(function.Name);
            foreach (var change in new[] { "STABLE", "PARALLEL SAFE", "SECURITY INVOKER", "SET search_path=pg_catalog,weft" })
                await MutationAsync(test, "ALTER FUNCTION " + signature + " " + change,
                    original.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal), () => AssertAffectedRefusedAsync(test));
            var changed = original.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
                .Replace(function.Body, function.Body + "\n-- changed source body\n", StringComparison.Ordinal);
            await MutationAsync(test, changed, original.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal),
                () => AssertAffectedRefusedAsync(test));
            await MutationAsync(test, "GRANT EXECUTE ON FUNCTION " + signature + " TO PUBLIC",
                "REVOKE EXECUTE ON FUNCTION " + signature + " FROM PUBLIC", () => AssertAffectedRefusedAsync(test));
        }
    }

    [PostgresFact]
    public async Task MissingEntryRoutineOrAnExtraExecutableRoutineRefusesAllAffectedPoolsUntilExactRestoration()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var signature in EntrySignatures)
        {
            var name = signature[..signature.IndexOf('(')];
            await MutationAsync(test, "DROP FUNCTION weft." + signature,
                TrustedFunction(name) + "; REVOKE ALL ON FUNCTION weft." + signature + " FROM PUBLIC; GRANT EXECUTE ON FUNCTION weft." + signature + " TO weft_maintenance;",
                () => AssertAffectedRefusedAsync(test));
        }
        await MutationAsync(test, """
            CREATE FUNCTION weft.get_private_object_cleanup_attempt_v1() RETURNS jsonb
            LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER SET search_path=pg_catalog,weft,pg_temp
            AS $extra$ BEGIN RETURN NULL; END; $extra$;
            REVOKE ALL ON FUNCTION weft.get_private_object_cleanup_attempt_v1() FROM PUBLIC;
            """, "DROP FUNCTION weft.get_private_object_cleanup_attempt_v1()", () => AssertAffectedRefusedAsync(test));
        await test.Case.ExecuteAsync("CREATE FUNCTION weft.maintenance_security_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'; REVOKE ALL ON FUNCTION weft.maintenance_security_probe() FROM PUBLIC;");
        try
        {
            await MutationAsync(test, "GRANT EXECUTE ON FUNCTION weft.maintenance_security_probe() TO weft_maintenance",
                "REVOKE EXECUTE ON FUNCTION weft.maintenance_security_probe() FROM weft_maintenance", () => AssertMaintenanceRefusedAsync(test));
        }
        finally { await test.Case.ExecuteAsync("DROP FUNCTION weft.maintenance_security_probe()"); }
        await AssertHealthyAsync(test);
    }

    [PostgresFact]
    public async Task NewCheckNoInheritRlsOrUnknownPolicyTamperingRefusesEveryAffectedPoolAndRestoresTrustedSource()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var table in MaintenanceSchemaContract.Tables.Values)
        {
            await MutationAsync(test, "ALTER TABLE weft." + table.Name + " DISABLE ROW LEVEL SECURITY",
                "ALTER TABLE weft." + table.Name + " ENABLE ROW LEVEL SECURITY", () => AssertAffectedRefusedAsync(test));
            await MutationAsync(test, "CREATE POLICY maintenance_extra_policy ON weft." + table.Name + " USING(true)",
                "DROP POLICY maintenance_extra_policy ON weft." + table.Name, () => AssertAffectedRefusedAsync(test));
            foreach (var constraint in table.Constraints.Where(value => value.Kind == 'c'))
            {
                var declaration = TrustedCheck(constraint.Name);
                await test.Case.ExecuteAsync($"ALTER TABLE weft.{table.Name} DROP CONSTRAINT {constraint.Name}");
                try
                {
                    await test.Case.ExecuteAsync($"ALTER TABLE weft.{table.Name} ADD {declaration} NO INHERIT");
                    await AssertAffectedRefusedAsync(test);
                }
                finally
                {
                    await test.Case.ExecuteAsync($"ALTER TABLE weft.{table.Name} DROP CONSTRAINT IF EXISTS {constraint.Name}; ALTER TABLE weft.{table.Name} ADD {declaration}");
                }
                await AssertHealthyAsync(test);
            }
        }
    }

    [PostgresFact]
    public async Task MissingOrCascadingFullTargetForeignKeysRefuseEveryAffectedPoolAndRestoreExactDeclarations()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        foreach (var table in MaintenanceSchemaContract.Tables.Values)
            foreach (var constraint in table.Constraints.Where(value => value.Kind == 'f'))
            {
                var declaration = TrustedConstraint(table.Name, constraint.Name);
                var drop = $"ALTER TABLE weft.{table.Name} DROP CONSTRAINT {constraint.Name}";
                var restore = $"ALTER TABLE weft.{table.Name} ADD {declaration}";
                await MutationAsync(test, drop, restore, () => AssertAffectedRefusedAsync(test));
                var changed = declaration.Replace("ON DELETE RESTRICT", "ON DELETE CASCADE", StringComparison.Ordinal);
                Assert.NotEqual(declaration, changed);
                await test.Case.ExecuteAsync(drop);
                try
                {
                    await test.Case.ExecuteAsync($"ALTER TABLE weft.{table.Name} ADD {changed}");
                    await AssertAffectedRefusedAsync(test);
                }
                finally
                {
                    await test.Case.ExecuteAsync(drop);
                    await test.Case.ExecuteAsync(restore);
                }
                await AssertHealthyAsync(test);
            }
    }

    [PostgresFact]
    public async Task ChangedRoutineOwnerOrNewRelationColumnShapeRefusesAllAffectedPoolsThenRecovers()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await AssertHealthyAsync(test);
        const string signature = "weft.get_private_object_cleanup_attempt_v1(uuid,uuid,uuid,bigint)";
        await test.Case.BootstrapAsync("ALTER FUNCTION " + signature + " OWNER TO weft_runtime");
        try { await AssertAffectedRefusedAsync(test); }
        finally { await test.Case.BootstrapAsync("ALTER FUNCTION " + signature + " OWNER TO weft_migrator"); }
        await AssertHealthyAsync(test);
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE weft.private_object_publications ALTER object_id DROP NOT NULL", "ALTER TABLE weft.private_object_publications ALTER object_id SET NOT NULL"),
            ("ALTER TABLE weft.private_object_retirements ALTER policy_id SET DEFAULT 'changed'", "ALTER TABLE weft.private_object_retirements ALTER policy_id SET DEFAULT 'weft.orphan-maintenance.v1'"),
            ("ALTER TABLE weft.private_object_cleanup_attempts ADD COLUMN unknown_fact text", "ALTER TABLE weft.private_object_cleanup_attempts DROP COLUMN unknown_fact"),
            ("ALTER TABLE weft.private_object_publications FORCE ROW LEVEL SECURITY", "ALTER TABLE weft.private_object_publications NO FORCE ROW LEVEL SECURITY")
        }) await MutationAsync(test, change, restore, () => AssertAffectedRefusedAsync(test));
    }

    [PostgresFact]
    public async Task MissingExtraBroadOrGrantablePublicationColumnsRefuseEveryAffectedPoolAndRecover()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        foreach (var login in new[] { "weft_ingest", "weft_deriver" })
        {
            foreach (var (change, restore) in new[]
            {
                ($"REVOKE SELECT(owner_token) ON weft.private_object_publications FROM {login}", $"GRANT SELECT(owner_token) ON weft.private_object_publications TO {login}"),
                ($"REVOKE INSERT(temp_id) ON weft.private_object_publications FROM {login}", $"GRANT INSERT(temp_id) ON weft.private_object_publications TO {login}"),
                ($"REVOKE UPDATE(bound_record_digest) ON weft.private_object_publications FROM {login}", $"GRANT UPDATE(bound_record_digest) ON weft.private_object_publications TO {login}"),
                ($"REVOKE SELECT(target_kind) ON weft.private_object_retirements FROM {login}", $"GRANT SELECT(target_kind) ON weft.private_object_retirements TO {login}"),
                ($"GRANT SELECT(retired_at) ON weft.private_object_retirements TO {login}", $"REVOKE SELECT(retired_at) ON weft.private_object_retirements FROM {login}"),
                ($"GRANT INSERT(publication_id) ON weft.private_object_publications TO {login}", $"REVOKE INSERT(publication_id) ON weft.private_object_publications FROM {login}"),
                ($"GRANT UPDATE(owner_token) ON weft.private_object_publications TO {login}", $"REVOKE UPDATE(owner_token) ON weft.private_object_publications FROM {login}"),
                ($"GRANT SELECT ON weft.private_object_publications TO {login}", $"REVOKE SELECT ON weft.private_object_publications FROM {login}; GRANT SELECT({string.Join(',', PublicationRead)}) ON weft.private_object_publications TO {login}"),
                ($"GRANT SELECT(owner_token) ON weft.private_object_publications TO {login} WITH GRANT OPTION", $"REVOKE GRANT OPTION FOR SELECT(owner_token) ON weft.private_object_publications FROM {login}")
            })
                await MutationAsync(test, change, restore, () => AssertAffectedRefusedAsync(test));
        }
    }

    [PostgresFact]
    public async Task MaintenanceExtraColumnSourceSequenceDefaultPublicOrGrantOptionPrivilegesRefuseTheNextLease()
    {
        await using var test = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("CREATE SEQUENCE weft.maintenance_security_sequence");
        try
        {
            foreach (var (change, restore) in new[]
            {
                ("GRANT SELECT(id) ON weft.principals TO weft_maintenance", "REVOKE SELECT(id) ON weft.principals FROM weft_maintenance"),
                ("GRANT UPDATE(bound_at) ON weft.private_object_publications TO weft_maintenance", "REVOKE UPDATE(bound_at) ON weft.private_object_publications FROM weft_maintenance"),
                ("GRANT SELECT ON weft.private_object_retirements TO weft_maintenance WITH GRANT OPTION", "REVOKE GRANT OPTION FOR SELECT ON weft.private_object_retirements FROM weft_maintenance"),
                ("GRANT USAGE ON SEQUENCE weft.maintenance_security_sequence TO weft_maintenance", "REVOKE USAGE ON SEQUENCE weft.maintenance_security_sequence FROM weft_maintenance"),
                ("GRANT SELECT ON weft.private_object_retirements TO PUBLIC", "REVOKE SELECT ON weft.private_object_retirements FROM PUBLIC"),
                ("GRANT USAGE ON SCHEMA weft TO PUBLIC", "REVOKE USAGE ON SCHEMA weft FROM PUBLIC"),
                ("ALTER DEFAULT PRIVILEGES IN SCHEMA weft GRANT SELECT ON TABLES TO weft_maintenance", "ALTER DEFAULT PRIVILEGES IN SCHEMA weft REVOKE SELECT ON TABLES FROM weft_maintenance"),
                ($"GRANT TEMPORARY ON DATABASE {test.Case.Name} TO weft_maintenance", $"REVOKE TEMPORARY ON DATABASE {test.Case.Name} FROM weft_maintenance")
            }) await MutationAsync(test, change, restore, () => AssertMaintenanceRefusedAsync(test));
        }
        finally { await test.Case.ExecuteAsync("DROP SEQUENCE weft.maintenance_security_sequence"); }
    }

    private static Task<string[]> ColumnsAsync(PostgresCase test, string login, string table, string privilege) => test.ScalarAsync<string[]>("""
        SELECT coalesce(array_agg(a.attname::text ORDER BY a.attname::text COLLATE "C"),ARRAY[]::text[])
        FROM pg_attribute a WHERE a.attrelid=$2::regclass AND a.attnum>0 AND NOT a.attisdropped
          AND has_column_privilege($1,a.attrelid,a.attnum,$3)
        """, login, "weft." + table, privilege);

    private static async Task AssertIdentityBindingRefusedAsync(NpgsqlCommand command,
        PrivatePublicationReservation reservation, PrivatePublicationFileIdentity identity, string mismatch)
    {
        await using (command)
        {
            var file = identity.FileIdentity;
            var directory = identity.DirectoryIdentity;
            object[] values =
            [
                identity.ControlNamespaceId, identity.LinuxBootId, (decimal)identity.MountNamespaceDevice,
                (decimal)identity.MountNamespaceInode, (long)identity.EffectiveUserId, (decimal)file.Inode,
                (long)(mismatch == "deviceMajor" ? file.DeviceMajor-1 : file.DeviceMajor),
                (long)(mismatch == "deviceMinor" ? file.DeviceMinor-1 : file.DeviceMinor),
                (decimal)(mismatch == "mountId" ? file.MountId-1 : file.MountId),
                (decimal)(mismatch == "sameInode" ? file.Inode : directory.Inode),
                (long)directory.DeviceMajor, (long)directory.DeviceMinor, (decimal)directory.MountId,
                PostgresPrivateObjectMaintenanceTestSupport.BoundDigest.Value,
                reservation.Key.Scope.OrganizationId, reservation.Key.Scope.ProjectId, reservation.PublicationId
            ];
            foreach (var value in values) command.Parameters.AddWithValue(value);
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);
            Assert.Equal("private_object_publications_binding_check", refusal.ConstraintName);
        }
    }

    private static async Task AssertDeniedAsync(NpgsqlDataSource direct, string sql)
    {
        await using var command = direct.CreateCommand(sql);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private static async Task AssertHealthyAsync(PostgresPrivateObjectMaintenanceTestSupport test)
    {
        await using (var lease = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project)) { }
        await using (var lease = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId)) { }
        await using (var catalog = new PostgresCatalogDatabase(test.Case.CatalogConnectionString))
        await using (var lease = await catalog.BeginTransactionAsync(test.Project.OrganizationId)) { }
        await using (var lease = await test.ReportDatabase.BeginTransactionAsync(test.Project.OrganizationId)) { }
        await using (var lease = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId)) { }
        Assert.Equal(test.Source, (await test.ReportReader.GetSourceAsync(test.Actor, test.Revision))!.Source);
    }

    private static async Task AssertAffectedRefusedAsync(PostgresPrivateObjectMaintenanceTestSupport test)
    {
        await AssertMaintenanceRefusedAsync(test);
        await AssertWriterRefusedAsync(test, "weft_ingest");
        await using var catalog = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var _ = await catalog.BeginTransactionAsync(test.Project.OrganizationId); });
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var _ = await test.ReportDatabase.BeginTransactionAsync(test.Project.OrganizationId); });
        await AssertWriterRefusedAsync(test, "weft_deriver");
    }

    private static Task AssertMaintenanceRefusedAsync(PostgresPrivateObjectMaintenanceTestSupport test) =>
        Assert.ThrowsAsync<InvalidOperationException>(async () => { await using var _ = await test.MaintenanceDatabase.BeginTransactionAsync(test.Project); });

    private static Task AssertWriterRefusedAsync(PostgresPrivateObjectMaintenanceTestSupport test, string login) =>
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (login == "weft_ingest") { await using var _ = await test.IngestionDatabase.BeginTransactionAsync(test.Project.OrganizationId); }
            else { await using var _ = await test.DerivationDatabase.BeginTransactionAsync(test.Project.OrganizationId); }
        });

    private static async Task MutationAsync(PostgresPrivateObjectMaintenanceTestSupport test, string change, string restore, Func<Task> assertion)
    {
        await test.Case.ExecuteAsync(change);
        try { await assertion(); }
        finally { await test.Case.ExecuteAsync(restore); }
        await AssertHealthyAsync(test);
    }

    private static string Sql => MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 7).Sql;
    // 0023 replaces the publication guard body and format check; their trusted source is that later declaration.
    private static string StoreKindSql => MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 23).Sql;
    private static string TrustedTrigger(string name) => Trusted(@"^CREATE TRIGGER " + Regex.Escape(name) + @"\b.*?;");
    private static string TrustedIndex(string name) => Trusted(@"^CREATE (?:UNIQUE )?INDEX " + Regex.Escape(name) + @"\b.*?;");
    private static string TrustedFunction(string name)
    {
        var definition = @"\(.*?\$weft_" + Regex.Escape(name) + @"\$.*?\$weft_" + Regex.Escape(name) + @"\$;";
        return Replaced(@"^CREATE OR REPLACE FUNCTION weft\." + Regex.Escape(name) + definition) ??
            Trusted(@"^CREATE FUNCTION weft\." + Regex.Escape(name) + definition);
    }
    private static string TrustedCheck(string name)
    {
        var declaration = Replaced(@"(?<=^ALTER TABLE weft\.private_object_publications ADD )CONSTRAINT " + Regex.Escape(name) + @"\s+CHECK\b.*?(?=;$)") ??
            Trusted(@"^    CONSTRAINT " + Regex.Escape(name) + @" CHECK\b.*?(?=,\n    CONSTRAINT|\n\);)");
        return declaration.Trim();
    }
    private static string? Replaced(string pattern)
    {
        var matches = Regex.Matches(StoreKindSql, pattern, RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(matches.Count <= 1);
        return matches.Count == 1 ? matches[0].Value : null;
    }
    private static string TrustedConstraint(string table, string name)
    {
        var inline = Regex.Match(Sql, @"^    CONSTRAINT " + Regex.Escape(name) + @"\b.*?(?=,\n    CONSTRAINT|\n\);)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (inline.Success) return inline.Value.Trim();
        var altered = Trusted(@"^ALTER TABLE weft\." + Regex.Escape(table) + @" ADD CONSTRAINT " + Regex.Escape(name) + @"\b.*?;");
        return altered[(altered.IndexOf("CONSTRAINT ", StringComparison.Ordinal))..].TrimEnd(';');
    }
    private static string Trusted(string pattern)
    {
        var matches = Regex.Matches(Sql, pattern, RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.Single(matches);
        return matches[0].Value;
    }

    private static async Task AssertUnboundAsync(NpgsqlDataSource pool, int expectedPid)
    {
        await using var connection = await pool.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT pg_backend_pid(),NULLIF(current_setting('weft.organization_id',true),''),
              NULLIF(current_setting('weft.project_id',true),''),current_setting('transaction_read_only')
            """, connection);
        await using var row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.Equal(expectedPid, row.GetInt32(0));
        Assert.True(row.IsDBNull(1)); Assert.True(row.IsDBNull(2)); Assert.Equal("off", row.GetString(3));
    }

    private static async Task WaitForSleepAsync(PostgresCase test, int pid, Task pending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new NpgsqlConnectionStringBuilder(test.MaintenanceConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        while (true)
        {
            Assert.False(pending.IsCompleted);
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=$1 AND state='active' AND wait_event='PgSleep')", connection);
            command.Parameters.AddWithValue(pid);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }
}
