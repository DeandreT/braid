using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Xunit.Abstractions;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MachineInvocationSchemaTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int PrefixVersion = 23;

    [PostgresFact]
    public async Task Machine_invocation_schema_is_exact_and_has_separate_credentials()
    {
        PostgresCase test;
        try { test = await fixture.CreateCaseAsync(); }
        catch (PostgresException error)
        {
            output.WriteLine($"Schema diagnostic: {error.SqlState}; position={error.Position}; internal={error.InternalPosition}; context={error.Where}");
            throw;
        }
        await using var owned = test;
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, MachineInvocationRoleGuard.Names,
            BuildRoleGuard.LoadFunctions("0024_machine_invocation.sql", MachineInvocationRoleGuard.Names.Length), default);
        var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, MachineInvocationRoleGuard.Tables, default);
        output.WriteLine("MachineInvocationFingerprint=" + fingerprint);
        Assert.Equal(MachineInvocationRoleGuard.Fingerprint, fingerprint);
        // The release contract the 0024 functions read is unchanged by the new foreign keys.
        Assert.Equal(LocalReleaseRoleGuard.Fingerprint, await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, LocalReleaseRoleGuard.Tables, default));
        Assert.Equal(RegressionSchemaTestContract.MachineInvocationChecksum, MigrationRunner.EmbeddedMigrations[PrefixVersion].Checksum);
        // Each login executes only its own functions, and neither has any table privilege.
        foreach (var (role, functions) in new[] { ("weft_invocation_grant_writer", MachineInvocationRoleGuard.GrantFunctions),
            ("weft_invocation_writer", MachineInvocationRoleGuard.InvocationFunctions) })
        {
            await using var command = new NpgsqlCommand("""
                SELECT array_agg(p.proname::text ORDER BY p.proname) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='weft' AND pg_catalog.has_function_privilege(@role,p.oid,'EXECUTE')
                """, connection, transaction);
            command.Parameters.AddWithValue("role", role);
            Assert.Equal(functions.Order(StringComparer.Ordinal), (string[])(await command.ExecuteScalarAsync())!);
        }
    }

    [PostgresFact]
    public async Task Missing_credentials_preserve_schema23()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionMachineInvocations: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
        Assert.Equal((long)PrefixVersion, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.False(await test.ScalarAsync<bool>("SELECT to_regclass('weft.invocation_grants') IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Upgrade_from_the_0023_prefix_conserves_release_history_and_admits_grants_on_existing_targets()
    {
        await using var releases = await LocalReleasePersistenceTests.Context.CreateAsync(fixture, PrefixVersion);
        var test = releases.Case;
        Assert.Equal(Enumerable.Range(1, PrefixVersion), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        var first = await releases.Request(releases.First, null);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await releases.Apply(first));
        var before = await SnapshotAsync(test);

        await test.MigrateAsync();

        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(RegressionSchemaTestContract.MachineInvocationChecksum, await test.ScalarAsync<string>(
            "SELECT checksum FROM weft.schema_migrations WHERE version=24"));
        var after = await SnapshotAsync(test);
        Assert.Equal(before.Schema, after.Schema);
        Assert.Equal(before.Rows, after.Rows);

        // The existing release stores and pointer are unchanged; the new stores work on the upgraded target.
        Assert.Equal(first.ReleaseId, (await releases.Store.ReadTargetAsync(releases.Actor, releases.Target.Reference))!.ActiveReleaseId);
        await using var grants = new PostgresInvocationGrantStore(test.RegressionConnectionString("weft_invocation_grant_writer"));
        await using var ledger = new PostgresInvocationLedger(test.RegressionConnectionString("weft_invocation_writer"));
        var caller = new MachinePrincipal(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(releases.Actor, new(Guid.NewGuid(), releases.Target.Reference, caller)));
        var key = new MachineInvocationKey(releases.Target.Reference, caller, Guid.NewGuid());
        var application = Guid.NewGuid();
        var digest = Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("request"));
        var claim = await ledger.ClaimAsync(key, application, digest);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, claim.Status); Assert.Equal(releases.Actor.PrincipalId, claim.Publisher);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await ledger.FinishAsync(key, claim.ClaimId, application, MachineInvocationOutcome.Completed,
            MachineInvocationProvenance.From(first), "2026-10-22T12:00:00Z", [1]));
        Assert.Equal(new byte[] { 1 }, (await ledger.ClaimAsync(key, application, digest)).RetainedResponse);
        Assert.Equal(before.Rows, (await SnapshotAsync(test)).Rows);
    }

    private sealed record Snapshot(string Schema, string Rows);

    // Every pre-0024 relation, function, grant and row, excluding only the 0024 delta: its own objects, the
    // parent-side internal triggers of its foreign keys onto the release target, its schema USAGE grants and
    // its schema_migrations row.
    private static async Task<Snapshot> SnapshotAsync(PostgresCase test)
    {
        var schema = await test.ScalarAsync<string>("""
            WITH rels AS (SELECT c.oid,c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='weft' AND c.relkind IN ('r','p','v','m','S','i') AND NOT c.relname=ANY($1::text[])
                    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class t ON t.oid=x.indrelid WHERE x.indexrelid=c.oid AND t.relname=ANY($1::text[]))),
            new_roles AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname=ANY($3::text[]))
            SELECT jsonb_build_object(
                'relations',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relkind,c.relowner,c.relacl,c.relrowsecurity,c.relforcerowsecurity,c.relnatts,c.reloptions) ORDER BY c.relname)
                    FROM pg_catalog.pg_class c JOIN rels r ON r.oid=c.oid),
                'columns',(SELECT jsonb_agg(jsonb_build_array(r.relname,a.attnum,a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attacl,
                    pg_catalog.pg_get_expr(d.adbin,d.adrelid)) ORDER BY r.relname,a.attnum) FROM rels r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
                    LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum),
                'constraints',(SELECT jsonb_agg(jsonb_build_array(r.relname,k.conname,k.condeferrable,k.condeferred,pg_catalog.pg_get_constraintdef(k.oid)) ORDER BY r.relname,k.conname)
                    FROM rels r JOIN pg_catalog.pg_constraint k ON k.conrelid=r.oid),
                'indexes',(SELECT jsonb_agg(pg_catalog.pg_get_indexdef(i.indexrelid) ORDER BY 1) FROM rels r JOIN pg_catalog.pg_index i ON i.indrelid=r.oid),
                'triggers',(SELECT jsonb_agg(jsonb_build_array(r.relname,t.tgname,t.tgenabled,pg_catalog.pg_get_triggerdef(t.oid)) ORDER BY r.relname,t.tgname)
                    FROM rels r JOIN pg_catalog.pg_trigger t ON t.tgrelid=r.oid WHERE NOT t.tgisinternal),
                'internalTriggers',(SELECT jsonb_agg(jsonb_build_array(r.relname,t.tgname,t.tgenabled) ORDER BY r.relname,t.tgname)
                    FROM rels r JOIN pg_catalog.pg_trigger t ON t.tgrelid=r.oid WHERE t.tgisinternal AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_constraint k
                        JOIN pg_catalog.pg_class f ON f.oid=k.conrelid WHERE k.oid=t.tgconstraint AND f.relname=ANY($1::text[]))),
                'policies',(SELECT jsonb_agg(jsonb_build_array(r.relname,p.polname,p.polcmd,p.polroles,pg_catalog.pg_get_expr(p.polqual,p.polrelid)) ORDER BY r.relname,p.polname)
                    FROM rels r JOIN pg_catalog.pg_policy p ON p.polrelid=r.oid),
                'functions',(SELECT jsonb_agg(jsonb_build_array(p.proname,pg_catalog.pg_get_function_identity_arguments(p.oid),p.prosrc,p.prosecdef,p.provolatile,
                    p.proacl,p.proconfig,p.proowner) ORDER BY p.proname,2) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='weft' AND NOT p.proname=ANY($2::text[])),
                'roles',(SELECT jsonb_agg(to_jsonb(r)-'rolpassword' ORDER BY r.rolname) FROM pg_catalog.pg_roles r WHERE r.rolname LIKE 'weft_%'),
                'memberships',(SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY m.roleid,m.member),'[]'::jsonb) FROM pg_catalog.pg_auth_members m),
                'schemaAcl',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a.grantee,a.privilege_type) FROM pg_catalog.pg_namespace n
                    CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a WHERE n.nspname='weft' AND a.grantee NOT IN (SELECT oid FROM new_roles)),
                'databaseAcl',(SELECT datacl FROM pg_catalog.pg_database WHERE datname=current_database()),
                'history',(SELECT jsonb_agg(jsonb_build_array(version,checksum) ORDER BY version) FROM weft.schema_migrations WHERE version<=23))::text
            """, MachineInvocationRoleGuard.Tables, MachineInvocationRoleGuard.Names, new[] { "weft_invocation_grant_writer", "weft_invocation_writer" });
        var tables = await test.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='weft' AND c.relkind IN ('r','p') AND NOT c.relname=ANY($1::text[]) AND c.relname<>'schema_migrations'
            """, new object[] { MachineInvocationRoleGuard.Tables });
        var rows = new StringBuilder();
        foreach (var table in tables)
            rows.Append(table).Append('=').Append(await test.ScalarAsync<string>(
                $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text FROM ONLY weft.\"{table}\" t")).Append('\n');
        return new(schema, rows.ToString());
    }
}
