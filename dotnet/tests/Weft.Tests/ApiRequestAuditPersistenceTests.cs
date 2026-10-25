using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Deployment;
using Weft.Domain;
using Weft.Persistence.Postgres;
using Xunit.Abstractions;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiRequestAuditPersistenceTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int PrefixVersion = 24;
    private const string Login = "weft_api_audit_writer";

    [PostgresFact]
    public async Task The_audit_schema_is_exact_and_its_login_can_only_append_through_the_writer()
    {
        await using var identities = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var test = identities.Case;
        await using (var connection = await test.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, ApiRequestAuditRoleGuard.Names,
                BuildRoleGuard.LoadFunctions("0025_api_request_audit.sql", ApiRequestAuditRoleGuard.Names.Length), default);
            var fingerprint = await RegressionCaseSchemaContract.CaptureAsync(connection, transaction, ApiRequestAuditRoleGuard.Tables, default);
            output.WriteLine("ApiRequestAuditFingerprint=" + fingerprint);
            Assert.Equal(ApiRequestAuditRoleGuard.Fingerprint, fingerprint);
            output.WriteLine("ApiRequestAuditChecksum=" + MigrationRunner.EmbeddedMigrations[PrefixVersion].Checksum);
            Assert.Equal(RegressionSchemaTestContract.ApiRequestAuditChecksum, MigrationRunner.EmbeddedMigrations[PrefixVersion].Checksum);
            await using var command = new NpgsqlCommand("""
                SELECT array_agg(p.proname::text ORDER BY p.proname) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='weft' AND pg_catalog.has_function_privilege(@role,p.oid,'EXECUTE')
                """, connection, transaction);
            command.Parameters.AddWithValue("role", Login);
            Assert.Equal(ApiRequestAuditRoleGuard.WriterFunctions, (string[])(await command.ExecuteScalarAsync())!);
        }
        // The login reads and writes no table directly, and nobody can change or remove an audit row.
        foreach (var sql in new[] { "SELECT count(*) FROM weft.api_request_audit", "DELETE FROM weft.api_request_audit",
            "INSERT INTO weft.api_request_audit(organization_id,audit_id,principal_id,binding_id,client_application_id,operation,method,resource_names,resource_values) VALUES (gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'Op','POST','{}','{}')" })
            await PostgresExternalIdentityTestSupport.DeniedAsync(test.RegressionConnectionString(Login), sql);
        await using var audit = await AuditFixture.CreateAsync(identities);
        Assert.Equal(ApiRequestAuditOutcome.Recorded, await audit.Writer.RecordAsync(audit.Entry(), default));
        foreach (var sql in new[] { "UPDATE weft.api_request_audit SET operation='Other'", "DELETE FROM weft.api_request_audit",
            "TRUNCATE weft.api_request_audit" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => test.ExecuteAsync(sql));
            Assert.Equal("42501", error.SqlState);
        }
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.api_request_audit"));
    }

    [PostgresFact]
    public async Task The_writer_records_exact_redacted_facts_and_refuses_an_identity_that_is_no_longer_current()
    {
        await using var identities = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var test = identities.Case;
        await using var audit = await AuditFixture.CreateAsync(identities);
        var entry = audit.Entry();
        Assert.Equal(ApiRequestAuditOutcome.Recorded, await audit.Writer.RecordAsync(entry, default));
        // A replayed audit ID conflicts instead of overwriting or duplicating its row.
        await Assert.ThrowsAsync<PostgresException>(() => audit.Writer.RecordAsync(entry, default));
        var row = await test.ScalarAsync<string>("""
            SELECT jsonb_build_object('org',organization_id,'principal',principal_id,'binding',binding_id,'client',client_application_id,
                'operation',operation,'method',method,'names',resource_names,'values',resource_values,'now',recorded_at<=clock_timestamp())::text
            FROM weft.api_request_audit WHERE audit_id=$1
            """, entry.AuditId);
        using (var document = System.Text.Json.JsonDocument.Parse(row))
        {
            var value = document.RootElement;
            Assert.True(value.GetProperty("now").GetBoolean());
            Assert.Equal(entry.OrganizationId.ToString("D"), value.GetProperty("org").GetString());
            Assert.Equal(audit.Principal.Value.ToString("D"), value.GetProperty("principal").GetString());
            Assert.Equal(audit.Binding.BindingId.ToString("D"), value.GetProperty("binding").GetString());
            Assert.Equal(entry.ClientApplicationId.ToString("D"), value.GetProperty("client").GetString());
            Assert.Equal("PatchCatalogMappingMetadata", value.GetProperty("operation").GetString());
            Assert.Equal("PATCH", value.GetProperty("method").GetString());
            Assert.Equal(["mappingId", "version"], value.GetProperty("names").EnumerateArray().Select(item => item.GetString()!));
            Assert.Equal([audit.Mapping.ToString("D"), "7"], value.GetProperty("values").EnumerateArray().Select(item => item.GetString()!));
            Assert.Equal(9, value.EnumerateObject().Count());
        }
        // A binding owned by another principal, a disabled binding and an inactive principal never record.
        var other = await audit.Identities.PrincipalAsync();
        Assert.Equal(ApiRequestAuditOutcome.IdentityChanged, await audit.Writer.RecordAsync(audit.Entry(principal: other), default));
        await test.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", audit.Principal.Value);
        Assert.Equal(ApiRequestAuditOutcome.IdentityChanged, await audit.Writer.RecordAsync(audit.Entry(), default));
        await test.ExecuteAsync("UPDATE weft.principals SET active=true WHERE id=$1", audit.Principal.Value);
        Assert.Equal(ApiRequestAuditOutcome.Recorded, await audit.Writer.RecordAsync(audit.Entry(), default));
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled,
            (await audit.Identities.Owner.DisableAsync(audit.Binding.Key, audit.Principal, audit.Binding.Version)).Outcome);
        Assert.Equal(ApiRequestAuditOutcome.IdentityChanged, await audit.Writer.RecordAsync(audit.Entry(), default));
        Assert.Equal(2L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.api_request_audit"));
    }

    [PostgresFact]
    public async Task A_missing_audit_login_preserves_schema24()
    {
        await using var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionApiAudit: false);
        await isolated.InitializeAsync();
        await using var test = await isolated.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray()))
            await prefix.MigrateAsync();
        await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
        Assert.Equal((long)PrefixVersion, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.False(await test.ScalarAsync<bool>("SELECT to_regclass('weft.api_request_audit') IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Upgrade_from_the_0024_prefix_conserves_every_earlier_object_and_row_and_audits_existing_bindings()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray()))
            await prefix.MigrateAsync();
        Assert.Equal(Enumerable.Range(1, PrefixVersion), await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        // An existing installation: organizations, principals, projects and a browser binding from before 0025.
        var seed = await PostgresSeedData.CreateAsync(test);
        // The owner commands admit only the complete history, so the earlier binding is written as 0024 stored it.
        var binding = Guid.NewGuid();
        var key = EntraUserIdentity.Key(Guid.NewGuid(), Guid.NewGuid());
        await test.ExecuteAsync("INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES ($1,$2,$3,$4,$5)",
            binding, key.Provider, key.Authority, key.Subject, seed.Alice);
        var before = await SnapshotAsync(test);

        await test.MigrateAsync();

        Assert.Equal(Enumerable.Range(1, MigrationRunner.EmbeddedMigrations.Count), await test.ScalarAsync<int[]>(
            "SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"));
        Assert.Equal(RegressionSchemaTestContract.ApiRequestAuditChecksum, await test.ScalarAsync<string>(
            "SELECT checksum FROM weft.schema_migrations WHERE version=25"));
        var after = await SnapshotAsync(test);
        Assert.Equal(before.Schema, after.Schema);
        Assert.Equal(before.Rows, after.Rows);
        await using var writer = new PostgresApiRequestAuditWriter(test.RegressionConnectionString(Login));
        var entry = new ApiRequestAudit(Guid.NewGuid(), seed.OrganizationA, new PrincipalId(seed.Alice), binding,
            UserTokenValidationTests.Cli, "PatchCatalogMappingMetadata", "PATCH", [new("projectId", seed.ProjectA.ToString("D"))]);
        Assert.Equal(ApiRequestAuditOutcome.Recorded, await writer.RecordAsync(entry, default));
        Assert.Equal(before.Rows, (await SnapshotAsync(test)).Rows);
    }

    private sealed record Snapshot(string Schema, string Rows);

    // Every pre-0025 relation, function, grant and row, excluding only the 0025 delta: its own objects, the
    // parent-side internal triggers of its foreign keys, its schema USAGE grant and its schema_migrations row.
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
                'history',(SELECT jsonb_agg(jsonb_build_array(version,checksum) ORDER BY version) FROM weft.schema_migrations WHERE version<=24))::text
            """, ApiRequestAuditRoleGuard.Tables.Concat(AdministrationRoleGuard.Objects).ToArray(), ApiRequestAuditRoleGuard.Names.Concat(AdministrationRoleGuard.Names).ToArray(), new[] { Login, "weft_administration" });
        var tables = await test.ScalarAsync<string[]>("""
            SELECT array_agg(c.relname::text ORDER BY c.relname) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='weft' AND c.relkind IN ('r','p') AND NOT c.relname=ANY($1::text[]) AND c.relname<>'schema_migrations'
            """, new object[] { ApiRequestAuditRoleGuard.Tables.Concat(AdministrationRoleGuard.Objects).ToArray() });
        var rows = new StringBuilder();
        foreach (var table in tables)
            rows.Append(table).Append('=').Append(await test.ScalarAsync<string>(
                $"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text FROM ONLY weft.\"{table}\" t")).Append('\n');
        return new(schema, rows.ToString());
    }

    /// <summary>One seeded organization, an active principal with an enabled binding, and the restricted audit writer.</summary>
    private sealed class AuditFixture : IAsyncDisposable
    {
        internal PostgresExternalIdentityTestSupport Identities { get; private init; } = null!;
        internal PostgresApiRequestAuditWriter Writer { get; private init; } = null!;
        internal Guid Organization { get; private init; }
        internal PrincipalId Principal { get; private init; } = null!;
        internal ExternalIdentityBinding Binding { get; private init; } = null!;
        internal Guid Mapping { get; } = Guid.NewGuid();

        internal static async Task<AuditFixture> CreateAsync(PostgresExternalIdentityTestSupport identities)
        {
            var test = identities.Case;
            var seed = await PostgresSeedData.CreateAsync(test);
            var principal = await identities.PrincipalAsync();
            var binding = await identities.BindAsync(EntraUserIdentity.Key(Guid.NewGuid(), Guid.NewGuid()), principal);
            return new()
            {
                Identities = identities,
                Writer = new PostgresApiRequestAuditWriter(test.RegressionConnectionString(Login)),
                Organization = seed.OrganizationA,
                Principal = principal,
                Binding = binding
            };
        }

        internal ApiRequestAudit Entry(PrincipalId? principal = null) => new(Guid.NewGuid(), Organization, principal ?? Principal,
            Binding.BindingId, UserTokenValidationTests.Cli, "PatchCatalogMappingMetadata", "PATCH",
            [new("mappingId", Mapping.ToString("D")), new("version", "7")]);

        public ValueTask DisposeAsync() => Writer.DisposeAsync();
    }
}
