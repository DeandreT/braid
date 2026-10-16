using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresExternalIdentitySecurityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task DedicatedReaderHasExactColumnRightsAndNeitherReaderNorOlderProfilesCanMutateIdentity()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        await using (var connection = PostgresExternalIdentityTestSupport.UnpooledConnection(test.Case.IdentityConnectionString))
        {
            await connection.OpenAsync();
            await using var rights = new NpgsqlCommand("""
                SELECT current_user='weft_identity_reader' AND session_user=current_user
                    AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND NOT rolreplication AND NOT rolinherit
                    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname<>current_user
                        AND pg_catalog.pg_has_role(current_user,r.oid,'MEMBER'))
                    AND NOT pg_catalog.has_table_privilege(current_user,'weft.external_identity_bindings','SELECT')
                    AND NOT pg_catalog.has_table_privilege(current_user,'weft.principals','SELECT')
                    AND pg_catalog.has_column_privilege(current_user,'weft.external_identity_bindings','subject','SELECT')
                    AND pg_catalog.has_column_privilege(current_user,'weft.principals','active','SELECT')
                FROM pg_catalog.pg_roles WHERE rolname=current_user
                """, connection);
            Assert.True(Assert.IsType<bool>(await rights.ExecuteScalarAsync()));
            await using var actual = new NpgsqlCommand("SELECT b.binding_id,p.id,p.active FROM weft.external_identity_bindings b JOIN weft.principals p ON p.id=b.principal_id", connection);
            await using var rows = await actual.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(principal.Value, rows.GetGuid(1));
            Assert.True(rows.GetBoolean(2));
            Assert.False(await rows.ReadAsync());
        }
        foreach (var sql in new[]
        {
            "UPDATE weft.external_identity_bindings SET enabled=false,version=version+1,disabled_at=clock_timestamp()",
            "DELETE FROM weft.external_identity_bindings",
            "TRUNCATE weft.external_identity_bindings",
            "INSERT INTO weft.principals(id,active) VALUES (gen_random_uuid(),true)",
            "SELECT created_at FROM weft.principals",
            "SELECT id FROM weft.organizations",
            "SELECT id FROM weft.projects",
            "SELECT name FROM weft.mappings",
            "SELECT version FROM weft.schema_migrations",
            "SELECT weft.external_identity_key_valid_v1('provider','authority','subject')"
        }) await PostgresExternalIdentityTestSupport.DeniedAsync(test.Case.IdentityConnectionString, sql);
        foreach (var connectionString in new[]
        {
            test.Case.RuntimeConnectionString, test.Case.IngestionConnectionString, test.Case.CatalogConnectionString,
            test.Case.MetadataConnectionString, test.Case.ProjectCreationConnectionString,
            test.Case.ReportConnectionString, test.Case.DerivationConnectionString, test.Case.MaintenanceConnectionString
        }) await PostgresExternalIdentityTestSupport.DeniedAsync(connectionString, "SELECT binding_id FROM weft.external_identity_bindings");
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task ReaderRefusesUnsafeAttributesMembershipAndExtraOrMissingPrivilegesAndRecoversAfterRestore()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ResolveAsync(key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ResolveBindingAsync(binding.BindingId));
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER ROLE weft_identity_reader INHERIT", "ALTER ROLE weft_identity_reader NOINHERIT"),
            ("ALTER ROLE weft_identity_reader BYPASSRLS", "ALTER ROLE weft_identity_reader NOBYPASSRLS"),
            ("GRANT pg_read_all_data TO weft_identity_reader", "REVOKE pg_read_all_data FROM weft_identity_reader")
        })
        {
            await test.DriftAsync(change, restore, Refused, bootstrap: true);
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        }
        foreach (var (change, restore) in new[]
        {
            ("GRANT SELECT ON weft.external_identity_bindings TO weft_identity_reader", "REVOKE SELECT ON weft.external_identity_bindings FROM weft_identity_reader; GRANT SELECT(binding_id,provider,authority,subject,principal_id,enabled,version,created_at,disabled_at) ON weft.external_identity_bindings TO weft_identity_reader"),
            ("GRANT SELECT(created_at) ON weft.principals TO weft_identity_reader", "REVOKE SELECT(created_at) ON weft.principals FROM weft_identity_reader"),
            ("GRANT UPDATE(active) ON weft.principals TO PUBLIC", "REVOKE UPDATE(active) ON weft.principals FROM PUBLIC"),
            ("REVOKE SELECT(subject) ON weft.external_identity_bindings FROM weft_identity_reader", "GRANT SELECT(subject) ON weft.external_identity_bindings TO weft_identity_reader")
        })
        {
            await test.DriftAsync(change, restore, Refused);
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        }
    }

    [PostgresFact]
    public async Task SchemaDriftRefusesBothPortsBeforeMutationAndTrustedRestorationMakesThemUsableAgain()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        async Task Refused()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Reader.ResolveAsync(key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Owner.DisableAsync(key, principal, 1));
            Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        }
        foreach (var (change, restore) in new[]
        {
            ("ALTER TABLE weft.principals RENAME COLUMN created_at TO identity_unknown_probe", "ALTER TABLE weft.principals RENAME COLUMN identity_unknown_probe TO created_at"),
            ("ALTER TABLE weft.external_identity_bindings DISABLE TRIGGER external_identity_bindings_transition_guard", "ALTER TABLE weft.external_identity_bindings ENABLE TRIGGER external_identity_bindings_transition_guard"),
            ("ALTER TABLE weft.external_identity_bindings DROP CONSTRAINT external_identity_bindings_key_check; ALTER TABLE weft.external_identity_bindings ADD CONSTRAINT external_identity_bindings_key_check CHECK(true)",
             "ALTER TABLE weft.external_identity_bindings DROP CONSTRAINT external_identity_bindings_key_check; ALTER TABLE weft.external_identity_bindings ADD CONSTRAINT external_identity_bindings_key_check CHECK(weft.external_identity_key_valid_v1(provider,authority,subject))")
        })
        {
            await test.DriftAsync(change, restore, Refused);
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        }
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, (await test.Owner.DisableAsync(key, principal, 1)).Outcome);
        // ADD then DROP leaves a physical dropped attribute. Keep that negative
        // probe in its own case instead of treating DROP as a pristine restore.
        await using var extraColumnCase = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var extraPrincipal = await extraColumnCase.PrincipalAsync();
        var extraKey = PostgresExternalIdentityTestSupport.Key("extra-column");
        await extraColumnCase.BindAsync(extraKey, extraPrincipal);
        var extraBefore = await extraColumnCase.BindingsAsync();
        await extraColumnCase.Case.ExecuteAsync("ALTER TABLE weft.external_identity_bindings ADD COLUMN identity_unknown_probe integer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => extraColumnCase.Reader.ResolveAsync(extraKey));
        await Assert.ThrowsAsync<InvalidOperationException>(() => extraColumnCase.Owner.DisableAsync(extraKey, extraPrincipal, 1));
        Assert.True(string.Equals(extraBefore, await extraColumnCase.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task RestrictedReaderConnectionCannotBeUsedAsAnOwnerProvisioner()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        await test.BindAsync(key, principal);
        var before = await test.CountsAsync();
        var bindings = await test.BindingsAsync();
        await using var swapped = new ExternalIdentityBindingProvisioner(test.Case.IdentityConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => swapped.EnsurePrincipalAsync(new PrincipalId(Guid.NewGuid())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => swapped.EnsureOrganizationAdmissionAsync(Guid.NewGuid(), principal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => swapped.BindAsync(PostgresExternalIdentityTestSupport.Key("new"), principal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => swapped.DisableAsync(key, principal, 1));
        Assert.Equal(before, await test.CountsAsync());
        Assert.True(string.Equals(bindings, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task CallerCancellationBeforeAdmissionAndAtAnObservedOwnerLockStaysCancellationWithoutWrites()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var call in new Func<Task>[]
        {
            () => test.Reader.ResolveAsync(key, canceled.Token),
            () => test.Reader.ResolveBindingAsync(binding.BindingId, canceled.Token),
            () => test.Owner.BindAsync(key, principal, canceled.Token),
            () => test.Owner.DisableAsync(key, principal, 1, canceled.Token),
            () => test.Owner.EnsurePrincipalAsync(principal, canceled.Token),
            () => test.Owner.EnsureOrganizationAdmissionAsync(Guid.NewGuid(), principal, canceled.Token)
        })
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
            Assert.Equal(canceled.Token, error.CancellationToken);
        }
        await using var blocker = PostgresExternalIdentityTestSupport.UnpooledConnection(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var gate = new NpgsqlCommand("SELECT binding_id FROM weft.external_identity_bindings WHERE binding_id=$1 FOR UPDATE", blocker, transaction))
        {
            gate.Parameters.AddWithValue(binding.BindingId);
            Assert.Equal(binding.BindingId, Assert.IsType<Guid>(await gate.ExecuteScalarAsync()));
        }
        var applicationName = $"weft-identity-cancel-{Guid.NewGuid():N}";
        var route = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var owner = new ExternalIdentityBindingProvisioner(route);
        await using var observer = PostgresExternalIdentityTestSupport.UnpooledConnection(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresExternalIdentityTestSupport.PrepareBlockedAsync(observer, applicationName, blocker.ProcessID);
        using var work = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var operation = owner.DisableAsync(key, principal, 1, work.Token);
        try
        {
            await PostgresExternalIdentityTestSupport.WaitBlockedAsync(prepared, operation);
            work.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(work.Token, error.CancellationToken);
            // A fresh reader while the blocker remains held proves no update escaped.
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        }
        finally
        {
            work.Cancel();
            await transaction.RollbackAsync();
            try { await operation; } catch (OperationCanceledException) { }
        }
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, (await test.Owner.DisableAsync(key, principal, 1)).Outcome);
    }

    [PostgresFact]
    public async Task ActualConnectionFailureIsRedactedExceptionRatherThanUnknownIdentityOrOwnerSuccess()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var secret = Guid.NewGuid().ToString("N");
        var key = PostgresExternalIdentityTestSupport.Key($"private-subject-{Guid.NewGuid():N}");
        var invalidReader = new NpgsqlConnectionStringBuilder(test.Case.IdentityConnectionString) { Password = secret, Pooling = false }.ConnectionString;
        var invalidOwner = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Password = secret, Pooling = false }.ConnectionString;
        await using var database = new PostgresExternalIdentityDatabase(invalidReader);
        var reader = new PostgresExternalIdentityReader(database);
        await using var owner = new ExternalIdentityBindingProvisioner(invalidOwner);
        foreach (var call in new Func<Task>[]
        {
            () => reader.ResolveAsync(key), () => reader.ResolveBindingAsync(Guid.NewGuid()),
            () => owner.BindAsync(key, principal), () => owner.EnsurePrincipalAsync(principal)
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Null(error.InnerException);
            Assert.True(error.Message is "External identity persistence failed." or "External identity owner operation failed.");
            Assert.False(error.ToString().Contains(secret, StringComparison.Ordinal), "An identity failure exposed a private credential.");
            Assert.False(error.ToString().Contains(key.Subject, StringComparison.Ordinal), "An identity failure exposed a private subject.");
            Assert.False(error.ToString().Contains(key.Authority, StringComparison.Ordinal), "An identity failure exposed a private authority.");
        }
        Assert.Equal(new long[] { 0, 1, 0, 0, 0, 0, 0 }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task OrdinaryOwnerSqlCannotReassignReactivateRewriteOrEraseBindingHistory()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var other = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key();
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        foreach (var sql in new[]
        {
            "UPDATE weft.external_identity_bindings SET principal_id=$1 WHERE binding_id=$2",
            "UPDATE weft.external_identity_bindings SET binding_id=$1 WHERE binding_id=$2"
        }) await PostgresExternalIdentityTestSupport.CheckViolationAsync(test.Case, sql, other.Value, binding.BindingId);
        foreach (var sql in new[]
        {
            "UPDATE weft.external_identity_bindings SET subject=subject||'x'",
            "UPDATE weft.external_identity_bindings SET created_at=created_at+interval '1 second'",
            "UPDATE weft.external_identity_bindings SET enabled=false,version=version+2,disabled_at=clock_timestamp()",
            "DELETE FROM weft.external_identity_bindings",
            "TRUNCATE weft.external_identity_bindings"
        }) await PostgresExternalIdentityTestSupport.CheckViolationAsync(test.Case, sql);
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, (await test.Owner.DisableAsync(key, principal, 1)).Outcome);
        var disabled = await test.BindingsAsync();
        foreach (var sql in new[]
        {
            "UPDATE weft.external_identity_bindings SET enabled=true,disabled_at=NULL",
            "UPDATE weft.external_identity_bindings SET disabled_at=disabled_at+interval '1 second'",
            "DELETE FROM weft.external_identity_bindings",
            "TRUNCATE weft.external_identity_bindings"
        }) await PostgresExternalIdentityTestSupport.CheckViolationAsync(test.Case, sql);
        Assert.True(string.Equals(disabled, await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot changed.");
    }

    [PostgresFact]
    public async Task DirectSqlIndependentlyRejectsInvalidKeyBytesAndNonfiniteOrInvalidInitialState()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        const string insert = """
            INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id)
            VALUES($1,$2,$3,$4,$5)
            """;
        foreach (var (provider, authority, subject) in new[]
        {
            ("Provider", "authority", "subject"),
            (new string('a', 65), "authority", "subject"),
            ("provider\n", "authority", "subject"),
            ("provider", "", "subject"),
            ("provider", new string('\u00e9', 513), "subject"),
            ("provider", "authority", new string('\u00e9', 257)),
            ("provider", "authority", "subject\u0085"),
            ("provider", "authority\u007f", "subject")
        }) await PostgresExternalIdentityTestSupport.CheckViolationAsync(test.Case, insert, Guid.NewGuid(), provider, authority, subject, principal.Value);
        foreach (var suffix in new[]
        {
            "false,2,clock_timestamp(),clock_timestamp()",
            "true,2,clock_timestamp(),NULL",
            "true,0,clock_timestamp(),NULL",
            "true,1,'infinity'::timestamptz,NULL",
            "true,1,'-infinity'::timestamptz,NULL",
            "true,1,clock_timestamp(),clock_timestamp()"
        }) await PostgresExternalIdentityTestSupport.CheckViolationAsync(test.Case, $"""
            INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id,enabled,version,created_at,disabled_at)
            VALUES($1,'provider','authority','subject',$2,{suffix})
            """, Guid.NewGuid(), principal.Value);
        Assert.True(string.Equals("[]", await test.BindingsAsync(), StringComparison.Ordinal), "The private binding snapshot was not empty.");
    }
}
