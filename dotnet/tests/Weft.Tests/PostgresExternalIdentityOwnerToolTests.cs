using System.Text;
using Npgsql;
using Weft.Application;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresExternalIdentityOwnerToolTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ActualOwnerCommandsPrepareOnlyExplicitPrincipalAndAdmissionAndNeverReactivateOrGrant()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var seed = await PostgresSeedData.CreateAsync(test.Case);
        var principal = Guid.NewGuid();
        var unrelated = await UnrelatedRowsAsync(test.Case, principal, seed.OrganizationA, excludeCreatedRows: true);
        var arguments = new[] { "ensure-principal", principal.ToString("D") };
        Category(await RunAsync(test, arguments), 0, "created");
        var principalRow = await test.Case.ScalarAsync<string>("SELECT to_jsonb(p)::text FROM weft.principals p WHERE id=$1", principal);
        Category(await RunAsync(test, arguments), 0, "existing");
        Assert.True(string.Equals(principalRow, await test.Case.ScalarAsync<string>("SELECT to_jsonb(p)::text FROM weft.principals p WHERE id=$1", principal), StringComparison.Ordinal), "The explicit principal replay changed private persisted facts.");
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_memberships WHERE principal_id=$1", principal));
        Category(await RunAsync(test, ["ensure-organization-admission", Guid.NewGuid().ToString("D"), principal.ToString("D")]), 3, "organizationMissing");
        Category(await RunAsync(test, ["ensure-organization-admission", seed.OrganizationA.ToString("D"), Guid.NewGuid().ToString("D")]), 3, "principalMissing");
        var admissionArgs = new[] { "ensure-organization-admission", seed.OrganizationA.ToString("D"), principal.ToString("D") };
        Category(await RunAsync(test, admissionArgs), 0, "created");
        var admission = await test.Case.ScalarAsync<string>("SELECT to_jsonb(m)::text FROM weft.organization_memberships m WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, principal);
        Category(await RunAsync(test, admissionArgs), 0, "existing");
        Assert.True(string.Equals(admission, await test.Case.ScalarAsync<string>("SELECT to_jsonb(m)::text FROM weft.organization_memberships m WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, principal), StringComparison.Ordinal), "The explicit admission replay changed private persisted facts.");
        await test.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, principal);
        Category(await RunAsync(test, admissionArgs), 3, "admissionInactive");
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT active FROM weft.organization_memberships WHERE organization_id=$1 AND principal_id=$2", seed.OrganizationA, principal));
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", principal);
        Category(await RunAsync(test, arguments), 3, "inactive");
        Category(await RunAsync(test, admissionArgs), 3, "principalInactive");
        Assert.False(await test.Case.ScalarAsync<bool>("SELECT active FROM weft.principals WHERE id=$1", principal));
        Assert.True(string.Equals(unrelated, await UnrelatedRowsAsync(test.Case, principal, seed.OrganizationA, excludeCreatedRows: true), StringComparison.Ordinal), "Owner preparation changed unrelated private authority rows.");
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
        Assert.Equal(5L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.principals"));
        Assert.Equal(6L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_memberships"));
    }

    [PostgresFact]
    public async Task ExactRawBoundaryAndEscapedKeyRoundTripThroughBindDisableConflictAndDisabledReplayCommands()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        await PostgresSeedData.CreateAsync(test.Case);
        var principal = await test.PrincipalAsync();
        var other = await test.PrincipalAsync();
        var unrelated = await UnrelatedRowsAsync(test.Case);
        var key = new ExternalIdentityKey("tool-provider", " Literal Authority ", "subject-\U0001f600");
        // Both the decoded property name and the surrogate pair are valid JSON.
        // Padding reaches exactly the raw byte limit without changing key values.
        var encoded = Encoding.UTF8.GetBytes("""{"pro\u0076ider":"tool-provider","authority":" Literal Authority ","subject":"subject-\uD83D\uDE00"}""");
        var bytes = encoded.Concat(Enumerable.Repeat((byte)' ', 8192 - encoded.Length)).ToArray();
        Assert.Equal(8192, bytes.Length);
        Category(await RunAsync(test, ["bind-external-identity", principal.Value.ToString("D")], bytes, key), 0, "created");
        var binding = PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key));
        Assert.Equal(principal, binding.PrincipalId);
        Assert.True(binding.Key == key);
        var original = await test.BindingsAsync();
        Category(await RunAsync(test, ["bind-external-identity", principal.Value.ToString("D")], bytes, key), 0, "existing");
        Category(await RunAsync(test, ["bind-external-identity", other.Value.ToString("D")], bytes, key), 3, "bindingConflict");
        Category(await RunAsync(test, ["disable-external-identity", other.Value.ToString("D"), "1"], bytes, key), 3, "ownerConflict");
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "2"], bytes, key), 3, "versionConflict");
        Assert.True(string.Equals(original, await test.BindingsAsync(), StringComparison.Ordinal), "A refusal or replay changed the private binding snapshot.");
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "1"], bytes, key), 0, "disabled");
        var disabled = await test.BindingsAsync();
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "1"], bytes, key), 3, "versionConflict");
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "2"], bytes, key), 0, "alreadyDisabled");
        Category(await RunAsync(test, ["bind-external-identity", principal.Value.ToString("D")], bytes, key), 0, "existing");
        Assert.True(string.Equals(disabled, await test.BindingsAsync(), StringComparison.Ordinal), "The disabled private binding changed on command replay.");
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await test.Reader.ResolveBindingAsync(binding.BindingId));
        var unknown = PostgresExternalIdentityTestSupport.Key("cli-unknown");
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "1"], ExternalIdentityOwnerToolTestSupport.KeyBytes(unknown), unknown), 3, "unknownBinding");
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "9223372036854775807"], ExternalIdentityOwnerToolTestSupport.KeyBytes(unknown), unknown), 3, "unknownBinding");
        Category(await RunAsync(test, ["bind-external-identity", Guid.NewGuid().ToString("D")], ExternalIdentityOwnerToolTestSupport.KeyBytes(unknown), unknown), 3, "principalMissing");
        Assert.True(string.Equals(unrelated, await UnrelatedRowsAsync(test.Case), StringComparison.Ordinal), "Binding commands changed private authority rows before the explicit fixture deactivation.");
        await test.Case.ExecuteAsync("UPDATE weft.principals SET active=false WHERE id=$1", other.Value);
        var afterDeactivation = await UnrelatedRowsAsync(test.Case);
        Category(await RunAsync(test, ["bind-external-identity", other.Value.ToString("D")], ExternalIdentityOwnerToolTestSupport.KeyBytes(unknown), unknown), 3, "principalInactive");
        Assert.True(string.Equals(disabled, await test.BindingsAsync(), StringComparison.Ordinal), "A rejected key operation changed private binding history.");
        Assert.True(string.Equals(afterDeactivation, await UnrelatedRowsAsync(test.Case), StringComparison.Ordinal), "Binding commands changed unrelated private authority rows.");
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_memberships WHERE principal_id IN ($1,$2)", principal.Value, other.Value));
    }

    [PostgresFact]
    public async Task EntraUserCommandsBindTheTenantStableKeyBesideARetiredSubjectBindingWithTheSameSemantics()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        await PostgresSeedData.CreateAsync(test.Case);
        var principal = await test.PrincipalAsync();
        var other = await test.PrincipalAsync();
        var tenant = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        // An installation from before the tenant-stable key: the browser bound the pairwise sub.
        var retired = new ExternalIdentityKey(EntraUserIdentity.RetiredSubjectProvider,
            "https://login.microsoftonline.com/" + tenant.ToString("D") + "/v2.0", "pairwise-private-subject");
        var old = await test.BindAsync(retired, principal);
        var key = EntraUserIdentity.Key(tenant, objectId);
        Assert.Equal(("entra.oid", tenant.ToString("D"), objectId.ToString("D")), (key.Provider, key.Authority, key.Subject));
        var input = Encoding.UTF8.GetBytes("{\"objectId\":\"" + objectId.ToString("D") + "\",\"tenantId\":\"" + tenant.ToString("D") + "\"}");
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Unknown, await test.Reader.ResolveAsync(key));
        Category(await RunAsync(test, ["bind-entra-user", principal.Value.ToString("D")], input, key), 0, "created");
        var binding = PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key));
        Assert.Equal(principal, binding.PrincipalId);
        Assert.NotEqual(old.BindingId, binding.BindingId);
        // The new key is exactly the one browser sign-in and bearer access construct, and it never equals the retired one.
        Assert.True(binding.Key == key && binding.Key != retired);
        Category(await RunAsync(test, ["bind-entra-user", principal.Value.ToString("D")], input, key), 0, "existing");
        Category(await RunAsync(test, ["bind-entra-user", other.Value.ToString("D")], input, key), 3, "bindingConflict");
        Category(await RunAsync(test, ["disable-entra-user", other.Value.ToString("D"), "1"], input, key), 3, "ownerConflict");
        // Upgrade step two: the owner disables the retired binding with the existing generic command.
        Category(await RunAsync(test, ["disable-external-identity", principal.Value.ToString("D"), "1"],
            ExternalIdentityOwnerToolTestSupport.KeyBytes(retired), retired), 0, "disabled");
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await test.Reader.ResolveAsync(retired));
        PostgresExternalIdentityTestSupport.SameBinding(binding, PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        Category(await RunAsync(test, ["disable-entra-user", principal.Value.ToString("D"), "1"], input, key), 0, "disabled");
        Category(await RunAsync(test, ["disable-entra-user", principal.Value.ToString("D"), "2"], input, key), 0, "alreadyDisabled");
        Category(await RunAsync(test, ["bind-entra-user", principal.Value.ToString("D")], input, key), 0, "existing");
        PostgresExternalIdentityTestSupport.Hidden(ExternalIdentityResolutionOutcome.Disabled, await test.Reader.ResolveAsync(key));
        var unknown = Encoding.UTF8.GetBytes("{\"tenantId\":\"" + tenant.ToString("D") + "\",\"objectId\":\"" + Guid.NewGuid().ToString("D") + "\"}");
        Category(await RunAsync(test, ["disable-entra-user", principal.Value.ToString("D"), "1"], unknown, key), 3, "unknownBinding");
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_memberships WHERE principal_id IN ($1,$2)", principal.Value, other.Value));
    }

    [PostgresFact]
    public async Task RestrictedOrInvalidOwnerCredentialsFailCategoricallyWithoutChangingPrivateRows()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key("private-tool-subject");
        await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        var counts = await test.CountsAsync();
        foreach (var arguments in new[]
        {
            new[] { "ensure-principal", Guid.NewGuid().ToString("D") },
            new[] { "ensure-organization-admission", Guid.NewGuid().ToString("D"), principal.Value.ToString("D") },
            new[] { "bind-external-identity", principal.Value.ToString("D") },
            new[] { "disable-external-identity", principal.Value.ToString("D"), "1" }
        })
        {
            ReadOnlyMemory<byte> input = arguments[0] is "bind-external-identity" or "disable-external-identity"
                ? ExternalIdentityOwnerToolTestSupport.KeyBytes(key) : ReadOnlyMemory<byte>.Empty;
            var result = await ExternalIdentityOwnerToolTestSupport.RunAsync(test.Case.IdentityConnectionString, arguments,
                input, [key.Provider, key.Authority, key.Subject]);
            ExternalIdentityOwnerToolTestSupport.Category(result, 1, "error", "persistenceFailure");
        }
        var password = "weft-private-tool-password-" + Guid.NewGuid().ToString("N");
        var wrong = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Password = password, Pooling = false }.ConnectionString;
        var denied = await ExternalIdentityOwnerToolTestSupport.RunAsync(wrong,
            ["bind-external-identity", principal.Value.ToString("D")], ExternalIdentityOwnerToolTestSupport.KeyBytes(key),
            [password, key.Provider, key.Authority, key.Subject]);
        ExternalIdentityOwnerToolTestSupport.Category(denied, 1, "error", "persistenceFailure");
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "A rejected owner credential changed private binding rows.");
        Assert.Equal(counts, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task SigintAfterActualBlockedChildSqlProducesNaturalCanceledExitAndNoDisable()
    {
        await using var test = await PostgresExternalIdentityTestSupport.CreateAsync(fixture);
        var principal = await test.PrincipalAsync();
        var key = PostgresExternalIdentityTestSupport.Key("cancel-tool-subject");
        var binding = await test.BindAsync(key, principal);
        var before = await test.BindingsAsync();
        await using var blocker = PostgresExternalIdentityTestSupport.UnpooledConnection(test.Case.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT binding_id FROM weft.external_identity_bindings WHERE binding_id=$1 FOR UPDATE", blocker, transaction))
        {
            command.Parameters.AddWithValue(binding.BindingId);
            Assert.Equal(binding.BindingId, Assert.IsType<Guid>(await command.ExecuteScalarAsync()));
        }
        var applicationName = "weft-identity-tool-cancel-" + Guid.NewGuid().ToString("N");
        var route = new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var observer = PostgresExternalIdentityTestSupport.UnpooledConnection(test.Case.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresExternalIdentityTestSupport.PrepareBlockedAsync(observer, applicationName, blocker.ProcessID);
        await using var child = await ExternalIdentityOwnerToolTestSupport.StartAsync(route,
            ["disable-external-identity", principal.Value.ToString("D"), "1"], ExternalIdentityOwnerToolTestSupport.KeyBytes(key),
            [key.Provider, key.Authority, key.Subject]);
        var completion = child.FinishAsync();
        try
        {
            // The tool installs CancelKeyPress before reading stdin or opening this
            // owner connection. Its real blocking edge establishes both facts.
            await PostgresExternalIdentityTestSupport.WaitBlockedAsync(prepared, completion);
            Assert.False(child.HasExited, "The identity command exited before the observed cancellation point.");
            await child.InterruptAsync();
            ExternalIdentityOwnerToolTestSupport.Category(await completion, 130, "error", "canceled");
            PostgresExternalIdentityTestSupport.SameBinding(binding,
                PostgresExternalIdentityTestSupport.Resolved(await test.Reader.ResolveAsync(key)));
        }
        finally
        {
            await transaction.RollbackAsync();
            // Failure cleanup can kill/reap the child, but never manufactures130.
            await child.DisposeAsync();
            await completion;
        }
        Assert.True(string.Equals(before, await test.BindingsAsync(), StringComparison.Ordinal), "The canceled command changed the private binding snapshot.");
    }

    private static Task<ExternalIdentityOwnerToolTestSupport.Result> RunAsync(PostgresExternalIdentityTestSupport test,
        string[] arguments, ReadOnlyMemory<byte> input = default, ExternalIdentityKey? key = null) =>
        ExternalIdentityOwnerToolTestSupport.RunAsync(test.Case.MigrationConnectionString, arguments, input,
            key is null ? [] : [key.Provider, key.Authority, key.Subject]);

    private static void Category(ExternalIdentityOwnerToolTestSupport.Result result, int exit, string outcome) =>
        ExternalIdentityOwnerToolTestSupport.Category(result, exit, "outcome", outcome);

    private static Task<string> UnrelatedRowsAsync(PostgresCase test) =>
        UnrelatedRowsAsync(test, Guid.Empty, Guid.Empty, excludeCreatedRows: false);

    private static Task<string> UnrelatedRowsAsync(PostgresCase test, Guid createdPrincipal,
        Guid createdAdmissionOrganization, bool excludeCreatedRows) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
            'organizations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM weft.organizations r),
            'principals',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM weft.principals r
                WHERE NOT ($1 AND r.id=$2)),
            'admissions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM weft.organization_memberships r
                WHERE NOT ($1 AND r.principal_id=$2 AND r.organization_id=$3)),
            'projects',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,id) FROM weft.projects r),
            'roles',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,project_id,principal_id) FROM weft.project_memberships r),
            'capabilities',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM weft.organization_project_creation_capabilities r),
            'creations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,idempotency_key) FROM weft.project_creations r)
        )::text
        """, excludeCreatedRows, createdPrincipal, createdAdmissionOrganization);
}
