using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

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
    internal const string MigrationChecksum = "efe069090ebf95ac5708178f3b96492e27ade72fe995c0b3a145f7d495576247";

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
        SELECT ARRAY[(SELECT count(*) FROM braid.projects),
                     (SELECT count(*) FROM braid.project_memberships),
                     (SELECT count(*) FROM braid.project_creations)]
        """);

    internal Task<string> CreationSnapshotAsync(Guid key) => Case.ScalarAsync<string>("""
        SELECT to_jsonb(fact)::text FROM braid.project_creations fact
        WHERE organization_id=$1 AND idempotency_key=$2
        """, OrganizationId, key);

    internal static async Task<NpgsqlConnection> OpenOwnerAsync(PostgresCase test, CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.MigrationConnectionString)
        { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static async Task<int> BackendAsync(NpgsqlDataSource pool)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(pool.ConnectionString).MaxPoolSize);
        await using var connection = await pool.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<BlockingObserver> OpenBlockingObserverAsync(PostgresCase test,
        int contenderPid, int blockerPid, CancellationToken cancellationToken = default)
    {
        // Prepare independent observation before dispatching the contender, so
        // connection/authentication/command setup cannot consume its lock wait.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var connection = await OpenOwnerAsync(test, deadline.Token);
        NpgsqlCommand? command = null;
        try
        {
            command = new NpgsqlCommand("SELECT $2=ANY(pg_blocking_pids($1))", connection);
            command.Parameters.AddWithValue(contenderPid);
            command.Parameters.AddWithValue(blockerPid);
            await command.PrepareAsync(deadline.Token);
            if (await command.ExecuteScalarAsync(deadline.Token) is not false)
                throw new InvalidOperationException("The owned contender was already blocked before dispatch.");
            return new BlockingObserver(connection, command, contenderPid, blockerPid);
        }
        catch
        {
            try { if (command is not null) await command.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
            throw;
        }
    }

    internal sealed class BlockingObserver(NpgsqlConnection connection, NpgsqlCommand command,
        int contenderPid, int blockerPid) : IAsyncDisposable
    {
        internal async Task WaitForBlockedAsync(Task operation, CancellationToken cancellationToken = default)
        {
            // Admission/guard work precedes the actual row-lock request. This
            // fixture budget does not alter the product's 1s lock/5s SQL limits.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var elapsed = Stopwatch.StartNew();
            var polls = 0;
            try
            {
                while (true)
                {
                    await RequirePendingAsync(operation);
                    var blocked = await command.ExecuteScalarAsync(deadline.Token);
                    polls++;
                    await RequirePendingAsync(operation);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (blocked is true) return;
                    await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                await RequirePendingAsync(operation);
                throw new TimeoutException($"The exact blocking edge for owned contender {contenderPid} and blocker {blockerPid} was not observed after {polls} samples in {elapsed.ElapsedMilliseconds} ms; contender status {operation.Status}.");
            }
        }

        private static async Task RequirePendingAsync(Task operation)
        {
            if (!operation.IsCompleted) return;
            await operation;
            throw new InvalidOperationException("The creator completed before its intended actual PostgreSQL wait was observed.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await command.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }

    internal static string IndependentFingerprint(Guid organization, Guid actor, Guid key, string name, string description)
    {
        var utf8 = new UTF8Encoding(false, true);
        var bytes = new List<byte>();
        AddText("braid.project-creation-intent");
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
