using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual execute-only creator pool and separately configured owner provisioning over an owned PostgreSQL case.</summary>
internal sealed class ProjectCreationTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal Guid OrganizationId => Seed.OrganizationA;
    internal ActorContext Actor { get; }
    internal PostgresProjectCreationDatabase Database { get; }
    internal PostgresProjectCreator Creator { get; }
    internal static ProjectCreationMetadata DefaultMetadata { get; } = new("Created project", "Exact description");
    // Pinned from trusted migration bytes; no live database definition supplies
    // this expectation.
    internal const string MigrationChecksum = "2ae8603df7c7ecd56e587ae65d32e1225b8569702e98ff24e5d0fc1694fae5c1";

    private ProjectCreationTestSupport(PostgresCase test, PostgresSeedData seed)
    {
        Case = test;
        Seed = seed;
        Actor = new(new PrincipalId(seed.Alice));
        Database = new(test.ProjectCreationConnectionString);
        Creator = new(Database);
    }

    internal static async Task<ProjectCreationTestSupport> CreateAsync(PostgresFixture fixture, bool grantCapability = true)
    {
        var test = await fixture.CreateCaseAsync();
        ProjectCreationTestSupport? support = null;
        try
        {
            support = new(test, await PostgresSeedData.CreateAsync(test));
            if (grantCapability) await support.SetCapabilityAsync(true);
            return support;
        }
        catch
        {
            if (support is not null) await support.DisposeAsync();
            else await test.DisposeAsync();
            throw;
        }
    }

    internal Task<ProjectCreationResult> CreateAsync(Guid? key = null, ProjectCreationMetadata? metadata = null,
        CancellationToken cancellationToken = default) => Creator.CreateAsync(Actor, OrganizationId,
        key ?? Guid.NewGuid(), metadata ?? DefaultMetadata, cancellationToken);

    internal async Task SetCapabilityAsync(bool allowed, Guid? principal = null, Guid? organization = null)
    {
        await using var provisioner = new OrganizationProjectCreationCapabilityProvisioner(Case.MigrationConnectionString);
        await provisioner.SetAsync(organization ?? OrganizationId, new PrincipalId(principal ?? Seed.Alice), allowed);
    }

    internal Task<long[]> CountsAsync() => Case.ScalarAsync<long[]>("""
        SELECT ARRAY[(SELECT count(*) FROM weft.projects),
                     (SELECT count(*) FROM weft.project_memberships),
                     (SELECT count(*) FROM weft.project_creations)]
        """);

    internal Task<string> CreationSnapshotAsync(Guid key) => Case.ScalarAsync<string>("""
        SELECT to_jsonb(fact)::text FROM weft.project_creations fact
        WHERE organization_id=$1 AND idempotency_key=$2
        """, OrganizationId, key);

    internal static async Task<NpgsqlConnection> OpenOwnerAsync(PostgresCase test)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static async Task<int> BackendAsync(NpgsqlDataSource pool)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(pool.ConnectionString).MaxPoolSize);
        await using var connection = await pool.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task WaitForBlockedAsync(PostgresCase test, int contenderPid, int blockerPid,
        Task operation, CancellationToken cancellationToken = default)
    {
        // Observe a real backend graph before the unchanged one-second product lock deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        await using var observer = await OpenOwnerAsync(test);
        while (true)
        {
            if (operation.IsCompleted)
            {
                await operation;
                throw new InvalidOperationException("The creator completed before its intended actual PostgreSQL wait.");
            }
            await using var command = new NpgsqlCommand("SELECT $2=ANY(pg_blocking_pids($1))", observer);
            command.Parameters.AddWithValue(contenderPid);
            command.Parameters.AddWithValue(blockerPid);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }
    }

    internal static string IndependentFingerprint(Guid organization, Guid actor, Guid key, string name, string description)
    {
        var utf8 = new UTF8Encoding(false, true);
        var bytes = new List<byte>();
        AddText("weft.project-creation-intent");
        AddLength(1);
        foreach (var id in new[] { organization, actor, key })
            bytes.AddRange(Convert.FromHexString(id.ToString("N")));
        AddText(name);
        AddText(description);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();

        void AddLength(int length) => bytes.AddRange(new[]
        {
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
        });
        void AddText(string text)
        {
            var encoded = utf8.GetBytes(text);
            AddLength(encoded.Length);
            bytes.AddRange(encoded);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Database.DisposeAsync(); }
        finally { await Case.DisposeAsync(); }
    }
}
