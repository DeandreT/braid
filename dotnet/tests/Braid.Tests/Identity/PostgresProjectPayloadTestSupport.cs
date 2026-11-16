using System.Diagnostics;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Owned actual PostgreSQL cases; no payload bytes, HTTP or identity activation.</summary>
internal sealed class PostgresProjectPayloadTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal ActorContext Actor { get; }
    internal ProjectScope Scope { get; }
    internal ProjectPayloadGrantProvisioner Owner { get; }
    internal PostgresPayloadPermissionDatabase Database { get; }
    internal PostgresProjectPayloadPermissionReader Reader { get; }

    private PostgresProjectPayloadTestSupport(PostgresCase test, PostgresSeedData seed)
    {
        Case = test;
        Seed = seed;
        Actor = new(new PrincipalId(seed.Alice));
        Scope = new(seed.OrganizationA, seed.ProjectA);
        Owner = new(test.MigrationConnectionString);
        Database = new(test.PayloadPermissionConnectionString);
        Reader = new(Database);
    }

    internal static async Task<PostgresProjectPayloadTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var test = await fixture.CreateCaseAsync();
        try { return new(test, await PostgresSeedData.CreateAsync(test)); }
        catch { await test.DisposeAsync(); throw; }
    }

    internal Task<ProjectPayloadGrantOwnerResult> SetAsync(bool active = true, long expectedVersion = 0,
        CancellationToken cancellationToken = default) =>
        Owner.SetAsync(Scope, Actor.PrincipalId, active, expectedVersion, cancellationToken);

    internal Task<ProjectPayloadGrant?> ObserveAsync(CancellationToken cancellationToken = default) =>
        Reader.ObserveAsync(Actor, Scope, cancellationToken);

    internal static ProjectPayloadGrant Grant(ProjectPayloadGrantOwnerResult result,
        ProjectPayloadGrantOwnerOutcome outcome, ProjectScope scope, PrincipalId principal, bool active, long version)
    {
        Assert.Equal(outcome, result.Outcome);
        var grant = Assert.IsType<ProjectPayloadGrant>(result.Grant);
        Exact(grant, scope, principal, active, version);
        return grant;
    }

    internal static void Exact(ProjectPayloadGrant? result, ProjectScope scope, PrincipalId principal,
        bool active = true, long version = 1)
    {
        var grant = Assert.IsType<ProjectPayloadGrant>(result);
        Assert.True(grant.Scope == scope && grant.PrincipalId == principal,
            "The private payload grant tuple did not match its intended owner.");
        Assert.Equal(active, grant.Active);
        Assert.Equal(version, grant.Version);
    }

    internal static void Refused(ProjectPayloadGrantOwnerResult result, ProjectPayloadGrantOwnerOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Grant);
    }

    internal Task<string> GrantsAsync() => Case.ScalarAsync<string>("""
        SELECT COALESCE(jsonb_agg(to_jsonb(g) ORDER BY to_jsonb(g)::text),'[]'::jsonb)::text
        FROM (SELECT organization_id,project_id,principal_id,active,version FROM braid.project_payload_read_grants) g
        """);

    internal async Task<string> AuthorityAsync()
    {
        var snapshots = new List<string>();
        foreach (var table in new[] { "organizations", "principals", "organization_memberships", "projects",
            "project_memberships", "organization_project_creation_capabilities", "project_creations", "external_identity_bindings" })
            snapshots.Add(await Case.ScalarAsync<string>($"""
                SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text
                FROM braid.{table} r
                """));
        return string.Join('\n', snapshots);
    }

    internal static void SamePrivate(string expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), "A private PostgreSQL snapshot changed.");

    internal static NpgsqlConnection Unpooled(string connectionString) =>
        new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    internal static async Task DeniedAsync(string connectionString, string sql)
    {
        await using var connection = Unpooled(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    internal static async Task CheckViolationAsync(PostgresCase test, string sql, params object[] values)
    {
        await using var command = test.Owner.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    internal async Task DriftAsync(string change, string restore, Func<Task> probe, bool bootstrap = false)
    {
        if (bootstrap) await Case.BootstrapAsync(change);
        else await Case.ExecuteAsync(change);
        try { await probe(); }
        finally
        {
            if (bootstrap) await Case.BootstrapAsync(restore);
            else await Case.ExecuteAsync(restore);
        }
    }

    internal static async Task<NpgsqlCommand> PrepareBlockedAsync(NpgsqlConnection observer,
        string applicationName, int blocker)
    {
        var command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity a
                WHERE a.datname=current_database() AND a.application_name=$1
                  AND $2=ANY(pg_catalog.pg_blocking_pids(a.pid)))
            """, observer);
        command.Parameters.AddWithValue(applicationName);
        command.Parameters.AddWithValue(blocker);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await command.PrepareAsync(deadline.Token);
            Assert.False(Assert.IsType<bool>(await command.ExecuteScalarAsync(deadline.Token)));
            return command;
        }
        catch { await command.DisposeAsync(); throw; }
    }

    internal static async Task WaitBlockedAsync(NpgsqlCommand prepared, Task operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (operation.IsCompleted)
                throw new InvalidOperationException("The payload permission operation settled before its blocking edge was observed.");
            var blocked = Assert.IsType<bool>(await prepared.ExecuteScalarAsync(deadline.Token));
            if (operation.IsCompleted)
                throw new InvalidOperationException("The payload permission operation settled during blocking observation.");
            if (blocked) return;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("The payload permission operation did not expose its required blocking edge.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    internal static async Task DrainAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Database.DisposeAsync(); }
        finally
        {
            try { await Owner.DisposeAsync(); }
            finally { await Case.DisposeAsync(); }
        }
    }
}
