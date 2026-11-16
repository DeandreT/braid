using System.Diagnostics;
using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectCreationCapabilityToolTests(PostgresFixture fixture)
{
    private const string Command = "set-project-creation-capability";
    private const string Usage = "Usage: Braid.DatabaseTool migrate | provision-development-identity | set-project-creation-capability ORG PRINCIPAL true|false | bootstrap-organization-administrator ORG PRINCIPAL TENANT";
    private const string InvalidCapability = "Project creation capability configuration requires canonical nonempty organization and principal IDs and an explicit true|false value.";
    private const string MissingOwnerConfiguration = "Set BRAID_MIGRATION_CONNECTION_STRING for the dedicated migration login.";
    private const string ProviderFailure = "Creation capability provisioning failed.";
    private const string WrongOwner = "Creation capability provisioning requires the separate migration owner.";
    private const string InactiveIdentity = "Creation capability cannot be granted to an inactive identity or admission.";
    private const string SyntheticMarker = "braid-owned-capability-tool-private-marker";

    [PostgresFact]
    public async Task MigrationAndExplicitIdentityProvisioningNeverSeedProjectCreationCapability()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        var organization = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var environment = new Dictionary<string, string>
        {
            ["BRAID_DEV_ORGANIZATION_ID"] = organization.ToString("D"),
            ["BRAID_DEV_PRINCIPAL_ID"] = principal.ToString("D"),
            ["BRAID_DEV_ORGANIZATION_NAME"] = SyntheticMarker
        };
        var migration = await RunAsync(test.MigrationConnectionString, ["migrate"], environment);
        AssertSuccess(migration, "The Braid database migrations are current.");
        foreach (var relation in new[] { "organizations", "principals", "organization_memberships", "projects", "project_memberships",
            "organization_project_creation_capabilities", "project_creations" })
            Assert.Equal(0L, await CountAsync(test, relation));

        var identity = await RunAsync(test.MigrationConnectionString, ["provision-development-identity"], environment);
        AssertSuccess(identity, "The explicitly configured local identity and requested admission are current.");
        Assert.Equal(1L, await CountAsync(test, "organizations"));
        Assert.Equal(1L, await CountAsync(test, "principals"));
        Assert.Equal(1L, await CountAsync(test, "organization_memberships"));
        Assert.Equal(0L, await CountAsync(test, "organization_project_creation_capabilities"));
        await AssertNoCreationWritesAsync(test);
        await AssertAdmissionAsync(test, organization, principal, expected: false);

        // Exercise the actual database default separately from the absence rule.
        await test.ExecuteAsync("""
            INSERT INTO braid.organization_project_creation_capabilities(organization_id,principal_id)
            VALUES ($1,$2)
            """, organization, principal);
        Assert.False(await CapabilityAsync(test, organization, principal));
        await AssertAdmissionAsync(test, organization, principal, expected: false);
        var before = await SnapshotAsync(test);
        AssertSuccess(await RunAsync(test.MigrationConnectionString, ["provision-development-identity"], environment),
            "The explicitly configured local identity and requested admission are current.");
        Assert.Equal(before, await SnapshotAsync(test));
        await AssertNoCreationWritesAsync(test);
    }

    [PostgresFact]
    public async Task OwnerGrantAndRevokeChangeOnlyTheExactCapabilityAndPreserveExistingProjectRoles()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await SetFixtureCapabilityAsync(test, seed.OrganizationB, seed.Alice, true);
        await SetFixtureCapabilityAsync(test, seed.OrganizationA, seed.OtherProjectAdmin, false);
        var ownership = await SnapshotAsync(test, includeCapabilities: false);
        foreach (var enabled in new[] { true, true, false, false })
        {
            var result = await CapabilityCommandAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, enabled);
            AssertSuccess(result, enabled ? "Project creation capability granted." : "Project creation capability revoked.");
            Assert.Equal(enabled, await CapabilityAsync(test, seed.OrganizationA, seed.Alice));
            await AssertAdmissionAsync(test, seed.OrganizationA, seed.Alice, enabled);
            Assert.True(await CapabilityAsync(test, seed.OrganizationB, seed.Alice));
            Assert.False(await CapabilityAsync(test, seed.OrganizationA, seed.OtherProjectAdmin));
            Assert.Equal(3L, await CountAsync(test, "organization_project_creation_capabilities"));
            Assert.Equal(ownership, await SnapshotAsync(test, includeCapabilities: false));
        }
        Assert.Equal("viewer", await test.ScalarAsync<string>("""
            SELECT role FROM braid.project_memberships
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
            """, seed.OrganizationA, seed.ProjectA, seed.Alice));
        Assert.Equal(0L, await CountAsync(test, "project_creations"));
    }

    [PostgresFact]
    public async Task GrantRefusesMissingOrInactiveIdentityWithoutCreatingOrReactivatingAdmission()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await SetFixtureCapabilityAsync(test, seed.OrganizationA, seed.Alice, false);
        foreach (var pair in new[]
        {
            (Organization: Guid.NewGuid(), Principal: seed.Alice, Error: "The intended organization does not exist."),
            (Organization: seed.OrganizationA, Principal: Guid.NewGuid(), Error: "The intended principal does not exist."),
            (Organization: seed.OrganizationA, Principal: seed.Bob, Error: "The intended organization admission does not exist.")
        })
        {
            foreach (var enabled in new[] { true, false })
            {
                var before = await SnapshotAsync(test);
                AssertOperationFailure(await CapabilityCommandAsync(test.MigrationConnectionString, pair.Organization, pair.Principal, enabled), pair.Error);
                Assert.Equal(before, await SnapshotAsync(test));
            }
        }

        await test.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", seed.Alice);
        var inactivePrincipal = await SnapshotAsync(test);
        AssertOperationFailure(await CapabilityCommandAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, true), InactiveIdentity);
        Assert.Equal(inactivePrincipal, await SnapshotAsync(test));
        Assert.False(await CapabilityAsync(test, seed.OrganizationA, seed.Alice));
        await test.ExecuteAsync("UPDATE braid.principals SET active=true WHERE id=$1", seed.Alice);
        await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
            seed.OrganizationA, seed.Alice);
        var inactiveAdmission = await SnapshotAsync(test);
        AssertOperationFailure(await CapabilityCommandAsync(test.MigrationConnectionString, seed.OrganizationA, seed.Alice, true), InactiveIdentity);
        Assert.Equal(inactiveAdmission, await SnapshotAsync(test));
        Assert.False(await CapabilityAsync(test, seed.OrganizationA, seed.Alice));
    }

    [PostgresFact]
    public async Task ExistingExactCapabilityCanBeRevokedAfterPrincipalAdmissionOrBothBecomeInactive()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        foreach (var (principal, deactivatePrincipal, deactivateAdmission) in new[]
        {
            (seed.Alice, true, false), (seed.Admitted, false, true), (seed.OtherProjectAdmin, true, true)
        })
        {
            AssertSuccess(await CapabilityCommandAsync(test.MigrationConnectionString, seed.OrganizationA, principal, true),
                "Project creation capability granted.");
            if (deactivatePrincipal)
                await test.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", principal);
            if (deactivateAdmission)
                await test.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                    seed.OrganizationA, principal);
            var ownership = await SnapshotAsync(test, includeCapabilities: false);
            foreach (var repeat in new[] { false, false })
            {
                AssertSuccess(await CapabilityCommandAsync(test.MigrationConnectionString, seed.OrganizationA, principal, repeat),
                    "Project creation capability revoked.");
                Assert.False(await CapabilityAsync(test, seed.OrganizationA, principal));
                Assert.Equal(ownership, await SnapshotAsync(test, includeCapabilities: false));
            }
        }
        Assert.Equal(0L, await CountAsync(test, "project_creations"));
    }

    [PostgresFact]
    public async Task RuntimeAndCreatorLoginsCannotGrantOrRevokeAndProviderFailuresRemainCategorical()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await SetFixtureCapabilityAsync(test, seed.OrganizationA, seed.Alice, true);
        var before = await SnapshotAsync(test);
        foreach (var connection in new[] { test.RuntimeConnectionString, test.ProjectCreationConnectionString })
            foreach (var enabled in new[] { true, false })
            {
                AssertOperationFailure(await CapabilityCommandAsync(connection, seed.OrganizationA, seed.Alice, enabled), WrongOwner);
                Assert.Equal(before, await SnapshotAsync(test));
            }

        var invalidLogin = new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        {
            Password = SyntheticMarker + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var refused = await CapabilityCommandAsync(invalidLogin, seed.OrganizationA, seed.Alice, false);
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("", refused.Output);
        Assert.Equal(ProviderFailure, refused.Error.TrimEnd('\r', '\n'));
        Assert.Equal(before, await SnapshotAsync(test));
    }

    [PostgresFact]
    public async Task StrictArgumentsAndMissingOwnerConfigurationFailWithoutAnyDatabaseMutation()
    {
        await using var test = await fixture.CreateCaseAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await SetFixtureCapabilityAsync(test, seed.OrganizationA, seed.Alice, true);
        var before = await SnapshotAsync(test);
        var organization = seed.OrganizationA.ToString("D");
        var principal = seed.Alice.ToString("D");
        var invalidArguments = new List<(string[] Arguments, string Error)>
        {
            ([], Usage), ([Command], Usage), ([Command, organization, principal], Usage),
            ([Command, organization, principal, "false", SyntheticMarker], Usage),
            (["SET-PROJECT-CREATION-CAPABILITY", organization, principal, "true"], Usage)
        };
        foreach (var value in new[] { "", "TRUE", "True", "False", "1", "yes", " true", "false ", SyntheticMarker })
            invalidArguments.Add(([Command, organization, principal, value], Usage));
        foreach (var value in new[]
        {
            "", Guid.Empty.ToString("D"), " " + organization, organization + " ", "\t" + principal,
            Guid.Parse(organization).ToString("N"), Guid.Parse(principal).ToString("B"),
            "ABCDEF01-2345-6789-ABCD-ABCDEF012345", SyntheticMarker
        })
        {
            invalidArguments.Add(([Command, value, principal, "true"], InvalidCapability));
            invalidArguments.Add(([Command, organization, value, "false"], InvalidCapability));
        }
        foreach (var (arguments, expectedError) in invalidArguments)
        {
            var result = await RunAsync(test.MigrationConnectionString, arguments);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("", result.Output);
            Assert.Equal(expectedError, result.Error.TrimEnd('\r', '\n'));
            Assert.Equal(before, await SnapshotAsync(test));
        }
        foreach (var missingConnection in new string?[] { null, "", " \t" })
        {
            var result = await RunAsync(missingConnection, [Command, organization, principal, "false"]);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("", result.Output);
            Assert.Equal(MissingOwnerConfiguration, result.Error.TrimEnd('\r', '\n'));
            Assert.Equal(before, await SnapshotAsync(test));
        }
    }

    private static Task SetFixtureCapabilityAsync(PostgresCase test, Guid organization, Guid principal, bool enabled) =>
        test.ExecuteAsync("""
            INSERT INTO braid.organization_project_creation_capabilities(organization_id,principal_id,can_create_projects)
            VALUES ($1,$2,$3)
            """, organization, principal, enabled);

    private static Task<bool> CapabilityAsync(PostgresCase test, Guid organization, Guid principal) =>
        test.ScalarAsync<bool>("""
            SELECT can_create_projects FROM braid.organization_project_creation_capabilities
            WHERE organization_id=$1 AND principal_id=$2
            """, organization, principal);

    private static async Task AssertAdmissionAsync(PostgresCase test, Guid organization, Guid principal, bool expected)
    {
        await using var database = new PostgresProjectCreationDatabase(test.ProjectCreationConnectionString);
        var reader = new PostgresProjectCreator(database);
        var admission = await reader.GetAdmissionAsync(new ActorContext(new PrincipalId(principal)), organization);
        Assert.NotNull(admission);
        Assert.Equal(organization, admission.OrganizationId);
        Assert.Equal(new PrincipalId(principal), admission.PrincipalId);
        Assert.Equal(expected, admission.CanCreateProjects);
    }

    private static async Task AssertNoCreationWritesAsync(PostgresCase test)
    {
        Assert.Equal(0L, await CountAsync(test, "projects"));
        Assert.Equal(0L, await CountAsync(test, "project_memberships"));
        Assert.Equal(0L, await CountAsync(test, "project_creations"));
    }

    private static Task<long> CountAsync(PostgresCase test, string relation) => relation switch
    {
        "organizations" => test.ScalarAsync<long>("SELECT count(*) FROM braid.organizations"),
        "principals" => test.ScalarAsync<long>("SELECT count(*) FROM braid.principals"),
        "organization_memberships" => test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_memberships"),
        "projects" => test.ScalarAsync<long>("SELECT count(*) FROM braid.projects"),
        "project_memberships" => test.ScalarAsync<long>("SELECT count(*) FROM braid.project_memberships"),
        "organization_project_creation_capabilities" => test.ScalarAsync<long>("SELECT count(*) FROM braid.organization_project_creation_capabilities"),
        "project_creations" => test.ScalarAsync<long>("SELECT count(*) FROM braid.project_creations"),
        _ => throw new ArgumentException("Unknown owned fixture relation.", nameof(relation))
    };

    private static Task<string> SnapshotAsync(PostgresCase test, bool includeCapabilities = true) => test.ScalarAsync<string>("""
        SELECT (jsonb_build_object(
            'organizations',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.id),'[]'::jsonb) FROM braid.organizations r),
            'principals',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.id),'[]'::jsonb) FROM braid.principals r),
            'admissions',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.organization_id,r.principal_id),'[]'::jsonb) FROM braid.organization_memberships r),
            'projects',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.organization_id,r.id),'[]'::jsonb) FROM braid.projects r),
            'memberships',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.organization_id,r.project_id,r.principal_id),'[]'::jsonb) FROM braid.project_memberships r),
            'creations',(SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.organization_id,r.idempotency_key),'[]'::jsonb) FROM braid.project_creations r)
        ) || CASE WHEN $1 THEN jsonb_build_object('capabilities',
            (SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY r.organization_id,r.principal_id),'[]'::jsonb)
             FROM braid.organization_project_creation_capabilities r)) ELSE '{}'::jsonb END)::text
        """, includeCapabilities);

    private static Task<ToolResult> CapabilityCommandAsync(string connection, Guid organization, Guid principal, bool enabled) =>
        RunAsync(connection, [Command, organization.ToString("D"), principal.ToString("D"), enabled ? "true" : "false"]);

    private static void AssertSuccess(ToolResult result, string message)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(message, result.Output.TrimEnd('\r', '\n'));
        Assert.Equal("", result.Error);
    }

    private static void AssertOperationFailure(ToolResult result, string expectedError)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal(expectedError, result.Error.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\n', result.Error.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('\r', result.Error.TrimEnd('\r', '\n'));
    }

    private static async Task<ToolResult> RunAsync(string? connection, string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "DatabaseTool", "Braid.DatabaseTool.dll");
        Assert.True(File.Exists(dll), "The owner database tool and dependencies must be copied into the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
            host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var variable in new[] { "BRAID_MIGRATION_CONNECTION_STRING", "BRAID_DEV_ORGANIZATION_ID", "BRAID_DEV_PRINCIPAL_ID",
            "BRAID_DEV_ORGANIZATION_NAME", "BRAID_DEV_PROJECT_ID", "BRAID_DEV_PROJECT_ROLE" })
            start.Environment.Remove(variable);
        if (connection is not null)
            start.Environment["BRAID_MIGRATION_CONNECTION_STRING"] = connection;
        if (environment is not null)
            foreach (var item in environment)
                start.Environment[item.Key] = item.Value;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The owned capability tool did not start.");
        var output = ReadBoundedAsync(process.StandardOutput, deadline);
        var error = ReadBoundedAsync(process.StandardError, deadline);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            var result = new ToolResult(process.ExitCode, await output, await error);
            AssertSafeOutput(result, connection, arguments, environment);
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var text = new StringBuilder();
        var buffer = new char[256];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        {
            if (text.Length + count > 1024)
            {
                deadline.Cancel();
                throw new InvalidOperationException("The owned capability tool exceeded bounded categorical output.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static void AssertSafeOutput(ToolResult result, string? connection, string[] arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var combined = result.Output + result.Error;
        var forbidden = new List<string> { SyntheticMarker, "Npgsql", "PostgresException", "SQLSTATE", "Host=", "Password=", "Username=",
            "SocketException", "TimeoutException", "password authentication failed", "permission denied", "connection refused" };
        if (!string.IsNullOrWhiteSpace(connection))
        {
            forbidden.Add(connection);
            var parsed = new NpgsqlConnectionStringBuilder(connection);
            foreach (var value in new[] { parsed.Password, parsed.Username, parsed.Database, parsed.Host,
                parsed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                if (!string.IsNullOrWhiteSpace(value))
                    forbidden.Add(value);
        }
        foreach (var value in arguments.Concat(environment?.Values ?? []))
            if (Guid.TryParse(value, out _))
                forbidden.Add(value.Trim());
        foreach (var value in forbidden)
            Assert.True(!combined.Contains(value, StringComparison.OrdinalIgnoreCase),
                "The owner tool output must not disclose configuration, identifiers, synthetic secrets or provider details.");
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);
}
