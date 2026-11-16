using System.Diagnostics;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Owned actual PostgreSQL fixtures only; no sign-in, provider, session or private file work.</summary>
internal sealed class PostgresExternalIdentityTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal ExternalIdentityBindingProvisioner Owner { get; }
    internal PostgresExternalIdentityDatabase Database { get; }
    internal PostgresExternalIdentityReader Reader { get; }

    private PostgresExternalIdentityTestSupport(PostgresCase test)
    {
        Case = test;
        Owner = new(test.MigrationConnectionString);
        Database = new(test.IdentityConnectionString);
        Reader = new(Database);
    }

    internal static async Task<PostgresExternalIdentityTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var test = await fixture.CreateCaseAsync();
        try { return new(test); }
        catch { await test.DisposeAsync(); throw; }
    }

    internal static ExternalIdentityKey Key(string subject = "literal subject") =>
        new("test-provider", "https://identity.example/Exact Tenant", subject);

    internal async Task<PrincipalId> PrincipalAsync(bool active = true)
    {
        var principal = new PrincipalId(Guid.NewGuid());
        await Case.ExecuteAsync("INSERT INTO braid.principals(id,active) VALUES ($1,$2)", principal.Value, active);
        return principal;
    }

    internal async Task<ExternalIdentityBinding> BindAsync(ExternalIdentityKey key, PrincipalId principal)
    {
        var result = await Owner.BindAsync(key, principal);
        Assert.Equal(ExternalIdentityBindingOwnerOutcome.Created, result.Outcome);
        return Assert.IsType<ExternalIdentityBinding>(result.Binding);
    }

    internal Task<string> BindingsAsync() => Case.ScalarAsync<string>("""
        SELECT COALESCE(jsonb_agg(to_jsonb(b) ORDER BY to_jsonb(b)::text),'[]'::jsonb)::text
        FROM (SELECT binding_id,provider,authority,subject,principal_id,enabled,version,created_at,disabled_at
            FROM braid.external_identity_bindings) b
        """);

    internal Task<long[]> CountsAsync() => Case.ScalarAsync<long[]>("""
        SELECT ARRAY[(SELECT count(*) FROM braid.organizations),
            (SELECT count(*) FROM braid.principals),
            (SELECT count(*) FROM braid.organization_memberships),
            (SELECT count(*) FROM braid.projects),
            (SELECT count(*) FROM braid.project_memberships),
            (SELECT count(*) FROM braid.organization_project_creation_capabilities),
            (SELECT count(*) FROM braid.external_identity_bindings)]
        """);

    internal static void SameBinding(ExternalIdentityBinding expected, ExternalIdentityBinding actual)
    {
        Assert.Equal(expected.BindingId, actual.BindingId);
        Assert.True(expected.Key == actual.Key);
        Assert.Equal(expected.PrincipalId, actual.PrincipalId);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.DisabledAtUtc, actual.DisabledAtUtc);
    }

    internal static ExternalIdentityBinding Resolved(ExternalIdentityResolution result)
    {
        Assert.Equal(ExternalIdentityResolutionOutcome.Resolved, result.Outcome);
        return Assert.IsType<ExternalIdentityBinding>(result.Binding);
    }

    internal static void Hidden(ExternalIdentityResolutionOutcome outcome, ExternalIdentityResolution result)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Binding);
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

    internal static NpgsqlConnection UnpooledConnection(string connectionString)
    {
        var configuration = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        return new NpgsqlConnection(configuration.ConnectionString);
    }

    internal static async Task DeniedAsync(string connectionString, string sql)
    {
        await using var connection = UnpooledConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    internal static async Task<string> TableRowsAsync(NpgsqlConnection connection, string name)
    {
        if (name.Length == 0 || name.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new InvalidOperationException("The owned snapshot table name is invalid.");
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text
            FROM braid.{name} r
            """, connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    // Preparation happens before dispatch. Only the real blocking edge is proof;
    // the five-second outer budget includes admission, not a product lock SLA.
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
                throw new InvalidOperationException("The identity operation settled before an actual blocking edge was observed.");
            var blocked = Assert.IsType<bool>(await prepared.ExecuteScalarAsync(deadline.Token));
            if (operation.IsCompleted)
                throw new InvalidOperationException("The identity operation settled during blocking observation.");
            if (blocked) return;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                throw new TimeoutException("The identity operation did not expose the required blocking edge.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    internal static async Task CheckViolationAsync(PostgresCase test, string sql, params object[] values)
    {
        await using var command = test.Owner.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
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
