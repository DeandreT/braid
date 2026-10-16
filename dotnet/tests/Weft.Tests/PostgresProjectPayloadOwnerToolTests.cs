using System.Globalization;
using Npgsql;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectPayloadOwnerToolTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ActualCommandCommitsGrantRevokeReplayStaleConflictAndExplicitRegrantForOnlyItsExactTuple()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        var unrelated = await AuthorityRowsAsync(test);
        Category(await RunAsync(test, seed, 0, false), 0, "unchanged");
        Assert.Equal(0L, await GrantCountAsync(test));
        Category(await RunAsync(test, seed, 0, true), 0, "created");
        await GrantAsync(test, seed, true, 1);
        var first = await GrantRowsAsync(test);
        Category(await RunAsync(test, seed, 1, true), 0, "unchanged");
        Category(await RunAsync(test, seed, 0, true), 3, "versionConflict");
        Same(first, await GrantRowsAsync(test), "Grant replay or stale expectation changed private grant facts.");
        Category(await RunAsync(test, seed, 1, false), 0, "updated");
        await GrantAsync(test, seed, false, 2);
        var revoked = await GrantRowsAsync(test);
        Category(await RunAsync(test, seed, 1, false), 3, "versionConflict");
        Category(await RunAsync(test, seed, 2, false), 0, "unchanged");
        Same(revoked, await GrantRowsAsync(test), "Revoke replay or stale expectation changed private grant facts.");
        Category(await RunAsync(test, seed, 2, true), 0, "updated");
        await GrantAsync(test, seed, true, 3);
        var regranted = await GrantRowsAsync(test);
        foreach (var tuple in new[]
        {
            (seed.OrganizationA, seed.HiddenProjectA, seed.Alice),
            (seed.OrganizationA, seed.ProjectA, seed.Admitted),
            (seed.OrganizationB, seed.ProjectA, seed.Alice),
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        })
        {
            var refused = await ExternalIdentityOwnerToolTestSupport.RunAsync(test.MigrationConnectionString,
                Arguments(tuple.Item1, tuple.Item2, tuple.Item3, 0, true));
            Category(refused, 3, "authorityUnavailable");
        }
        Same(regranted, await GrantRowsAsync(test), "A foreign or missing authority request changed private grants.");
        Assert.Equal(1L, await GrantCountAsync(test));
        Same(unrelated, await AuthorityRowsAsync(test), "The payload owner command changed unrelated private authority rows.");
    }

    [PostgresFact]
    public async Task ActualCommandRefusesEachInactiveAuthorityButRevokesWithoutReactivationAndNeedsExplicitRegrant()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        Category(await RunAsync(test, seed, 0, true), 0, "created");
        long version = 1;
        foreach (var table in new[] { "principals", "organization_memberships", "project_memberships" })
        {
            var predicate = table switch
            {
                "principals" => "id=$1",
                "organization_memberships" => "principal_id=$1 AND organization_id=$2",
                _ => "principal_id=$1 AND organization_id=$2 AND project_id=$3"
            };
            object[] values = table switch
            {
                "principals" => new object[] { seed.Alice },
                "organization_memberships" => [seed.Alice, seed.OrganizationA],
                _ => [seed.Alice, seed.OrganizationA, seed.ProjectA]
            };
            await test.ExecuteAsync($"UPDATE weft.{table} SET active=false WHERE {predicate}", values);
            var authority = await AuthorityRowsAsync(test);
            var before = await GrantRowsAsync(test);
            Category(await RunAsync(test, seed, version, true), 3, "authorityUnavailable");
            Same(before, await GrantRowsAsync(test), "Inactive authority changed an existing private grant.");
            Category(await RunAsync(test, seed, version, false), 0, "updated");
            version++;
            await GrantAsync(test, seed, false, version);
            Category(await RunAsync(test, seed, version, false), 0, "unchanged");
            Category(await RunAsync(test, seed, version, true), 3, "authorityUnavailable");
            await GrantAsync(test, seed, false, version);
            Same(authority, await AuthorityRowsAsync(test), "A payload command reactivated or altered private authority.");
            await test.ExecuteAsync($"UPDATE weft.{table} SET active=true WHERE {predicate}", values);
            await GrantAsync(test, seed, false, version);
            Category(await RunAsync(test, seed, version, true), 0, "updated");
            version++;
            await GrantAsync(test, seed, true, version);
        }
    }

    [PostgresFact]
    public async Task ActualCommandAllowsMaximumVersionReplayButRefusesIncrementAndStaleExpectationWithoutMutation()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        Category(await RunAsync(test, seed, 0, true), 0, "created");
        // Seed an otherwise unreachable boundary only in this owned fixture. Both the state
        // setup and exact trigger restoration commit together, before the ordinary tool runs.
        await test.BootstrapAsync($"""
            BEGIN;
            ALTER TABLE weft.project_payload_read_grants DISABLE TRIGGER project_payload_read_grants_transition_guard;
            UPDATE weft.project_payload_read_grants SET version=9223372036854775807
            WHERE organization_id='{seed.OrganizationA:D}'::uuid AND project_id='{seed.ProjectA:D}'::uuid
                AND principal_id='{seed.Alice:D}'::uuid;
            ALTER TABLE weft.project_payload_read_grants ENABLE TRIGGER project_payload_read_grants_transition_guard;
            COMMIT;
            """);
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT count(*)=1 AND bool_and(tgenabled='O') FROM pg_catalog.pg_trigger
            WHERE tgrelid='weft.project_payload_read_grants'::regclass
                AND tgname='project_payload_read_grants_transition_guard'
            """), "The owned maximum-version fixture did not restore its exact transition trigger.");
        await GrantAsync(test, seed, true, long.MaxValue);
        var before = await GrantRowsAsync(test);
        var authority = await AuthorityRowsAsync(test);
        Category(await RunAsync(test, seed, long.MaxValue, true), 0, "unchanged");
        Category(await RunAsync(test, seed, long.MaxValue, false), 3, "versionExhausted");
        Category(await RunAsync(test, seed, long.MaxValue - 1, true), 3, "versionConflict");
        Same(before, await GrantRowsAsync(test), "Maximum version refusal or replay changed private grant facts.");
        Same(authority, await AuthorityRowsAsync(test), "Maximum version handling changed private authority facts.");
    }

    [PostgresFact]
    public async Task ActualCommandRejectsRestrictedAndInvalidCredentialsCategoricallyWithoutChangingPrivateRows()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        Category(await RunAsync(test, seed, 0, true), 0, "created");
        var before = await GrantRowsAsync(test);
        var authority = await AuthorityRowsAsync(test);
        foreach (var connection in new[]
        {
            test.RuntimeConnectionString, test.CatalogConnectionString, test.IdentityConnectionString,
            test.PayloadPermissionConnectionString
        })
        {
            foreach (var active in new[] { true, false })
            {
                var denied = await ExternalIdentityOwnerToolTestSupport.RunAsync(connection,
                    Arguments(seed.OrganizationA, seed.ProjectA, seed.Alice, 1, active));
                ExternalIdentityOwnerToolTestSupport.Category(denied, 1, "error", "persistenceFailure");
            }
        }
        var password = "weft-private-payload-owner-password-" + Guid.NewGuid().ToString("N");
        var wrong = new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        {
            Password = password,
            Pooling = false
        }.ConnectionString;
        var unavailable = new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
            Pooling = false,
            Timeout = 1,
            CommandTimeout = 1
        }.ConnectionString;
        foreach (var connection in new[] { wrong, unavailable })
        {
            var denied = await ExternalIdentityOwnerToolTestSupport.RunAsync(connection,
                Arguments(seed.OrganizationA, seed.ProjectA, seed.Alice, 1, false), privateValues: [password]);
            ExternalIdentityOwnerToolTestSupport.Category(denied, 1, "error", "persistenceFailure");
        }
        Same(before, await GrantRowsAsync(test), "A rejected owner connection changed private grant rows.");
        Same(authority, await AuthorityRowsAsync(test), "A rejected owner connection changed private authority rows.");
    }

    [PostgresFact]
    public async Task SigintAfterObservedBlockedChildSqlReturnsNaturalCanceledExitWithoutRevokingGrant()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        Category(await RunAsync(test, seed, 0, true), 0, "created");
        var before = await GrantRowsAsync(test);
        var authority = await AuthorityRowsAsync(test);
        await using var blocker = PostgresExternalIdentityTestSupport.UnpooledConnection(test.MigrationConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT version FROM weft.project_payload_read_grants
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3 FOR UPDATE
            """, blocker, transaction))
        {
            command.Parameters.AddWithValue(seed.OrganizationA);
            command.Parameters.AddWithValue(seed.ProjectA);
            command.Parameters.AddWithValue(seed.Alice);
            Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
        }
        var applicationName = "weft-payload-tool-cancel-" + Guid.NewGuid().ToString("N");
        var route = new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        await using var observer = PostgresExternalIdentityTestSupport.UnpooledConnection(test.MigrationConnectionString);
        await observer.OpenAsync();
        await using var prepared = await PostgresExternalIdentityTestSupport.PrepareBlockedAsync(observer,
            applicationName, blocker.ProcessID);
        await using var child = await ExternalIdentityOwnerToolTestSupport.StartAsync(route,
            Arguments(seed.OrganizationA, seed.ProjectA, seed.Alice, 1, false));
        var completion = child.FinishAsync();
        try
        {
            await PostgresExternalIdentityTestSupport.WaitBlockedAsync(prepared, completion);
            Assert.False(child.HasExited, "The payload command ended before the actual cancellation point.");
            await child.InterruptAsync();
            ExternalIdentityOwnerToolTestSupport.Category(await completion, 130, "error", "canceled");
        }
        finally
        {
            await transaction.RollbackAsync();
            // Only failure cleanup may kill/reap the exact owned child; it cannot manufacture exit 130.
            await child.DisposeAsync();
            await completion;
        }
        Same(before, await GrantRowsAsync(test), "The canceled command changed private grant facts.");
        Same(authority, await AuthorityRowsAsync(test), "The canceled command changed private authority facts.");
    }

    private static string[] Arguments(Guid organization, Guid project, Guid principal, long version, bool active) =>
        ["set-project-payload-read", organization.ToString("D"), project.ToString("D"), principal.ToString("D"),
            version.ToString(CultureInfo.InvariantCulture), active ? "true" : "false"];

    private static Task<ExternalIdentityOwnerToolTestSupport.Result> RunAsync(PostgresCase test,
        PostgresSeedData seed, long version, bool active) => ExternalIdentityOwnerToolTestSupport.RunAsync(
        test.MigrationConnectionString, Arguments(seed.OrganizationA, seed.ProjectA, seed.Alice, version, active));

    private static void Category(ExternalIdentityOwnerToolTestSupport.Result result, int exitCode, string outcome) =>
        ExternalIdentityOwnerToolTestSupport.Category(result, exitCode, "outcome", outcome);

    private static void Same(string expected, string actual, string failure) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), failure);

    private static Task<long> GrantCountAsync(PostgresCase test) =>
        test.ScalarAsync<long>("SELECT count(*) FROM weft.project_payload_read_grants");

    private static async Task GrantAsync(PostgresCase test, PostgresSeedData seed, bool active, long version)
    {
        Assert.Equal(1L, await test.ScalarAsync<long>("""
            SELECT count(*) FROM weft.project_payload_read_grants
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3 AND active=$4 AND version=$5
            """, seed.OrganizationA, seed.ProjectA, seed.Alice, active, version));
    }

    private static Task<string> GrantRowsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY organization_id,project_id,principal_id),'[]'::jsonb)::text
        FROM weft.project_payload_read_grants r
        """);

    private static Task<string> AuthorityRowsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
            'organizations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM weft.organizations r),
            'principals',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM weft.principals r),
            'admissions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM weft.organization_memberships r),
            'projects',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,id) FROM weft.projects r),
            'roles',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,project_id,principal_id) FROM weft.project_memberships r),
            'capabilities',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,principal_id) FROM weft.organization_project_creation_capabilities r),
            'creations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY organization_id,idempotency_key) FROM weft.project_creations r),
            'bindings',(SELECT jsonb_agg(to_jsonb(r) ORDER BY binding_id) FROM weft.external_identity_bindings r)
        )::text
        """);
}
