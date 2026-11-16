using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>
/// Actual restricted PostgreSQL and strict v2 files. The composed old fixture's
/// source revision is authored C1 metadata, not C2/codec evidence. No cleanup
/// disposition or positive report completion is seeded by this support.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class CommandTestSupport : IAsyncDisposable
{
    internal PostgresPrivateObjectMaintenanceTestSupport Facts { get; }
    internal PrivateStorageTestRoot Root { get; } = new();
    internal FileSystemV2PrivateObjectStore Physical { get; }
    internal CommandMetadata Metadata { get; }
    internal CommandRetirement Retirement { get; }
    internal Func<V2StorageObservation, CancellationToken, ValueTask>? Observation { get; set; }

    private CommandTestSupport(PostgresPrivateObjectMaintenanceTestSupport facts)
    {
        Facts = facts;
        var registry = new LazyPrivateObjectPublicationRegistry(() => facts.NativePublications, () => facts.ReportPublications);
        Physical = FileSystemV2PrivateObjectStore.Initialize(Root.Options, () => registry,
            new PrivateStorageV2Observer((observation, token) => Observation?.Invoke(observation, token) ?? ValueTask.CompletedTask));
        Metadata = new(facts.Maintenance);
        Retirement = new(Physical);
    }

    internal static async Task<CommandTestSupport> CreateAsync(PostgresFixture fixture)
    {
        var facts = await PostgresPrivateObjectMaintenanceTestSupport.CreateAsync(fixture);
        try { return new(facts); }
        catch { await facts.DisposeAsync(); throw; }
    }

    internal PrivateObjectMaintenanceService Service(IPrivateObjectRetirementStore? retirement = null,
        IPrivateObjectMaintenanceMetadata? metadata = null) =>
        new(metadata ?? Metadata, Facts.Maintenance, Facts.Maintenance, Physical, retirement ?? Retirement, Guid.NewGuid());

    internal async Task<ReservedIngestionFacts> PublishedNativeAsync(bool terminal = true, bool age = true)
    {
        var facts = await Facts.ReserveNativeAsync();
        var claim = await Facts.ClaimNativeAsync(facts);
        Assert.Equal(PrivateObjectPublishResult.Created, await Physical.PublishCreateOnlyAsync(
            PrivatePublicationOwner.ForIngestion(Facts.Actor, claim), facts.FutureRevision, facts.NativeObject,
            new MemoryStream(PostgresIngestionTestSupport.SyntheticBody)));
        if (terminal)
        {
            var completed = await Facts.Ingestions.CompleteAttemptAsync(Facts.Actor, claim,
                IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit));
            Assert.Equal(IngestionPhase.Rejected, completed.Receipt!.Phase);
        }
        // Explicit owner-authored 25h aging; actual deletion and acknowledgements
        // still run through the production filesystem and database ports.
        if (age) await Facts.SyntheticAgeNativeAsync(facts, TimeSpan.FromHours(25));
        return facts;
    }

    internal async Task<(RevisionReportClaim Claim, PrivateObjectReference Reference, byte[] Bytes)> PublishedReportAsync(bool failed)
    {
        var claim = await Facts.ReserveReportAsync(Facts.ReportKey(Guid.NewGuid().ToString("N")));
        var bytes = "Opaque failed/pending candidate, not a usable encoded report."u8.ToArray();
        var reference = new PrivateObjectReference(claim.CandidateObjectId, Sha256Digest.FromBytes(bytes), bytes.LongLength);
        Assert.Equal(PrivateObjectPublishResult.Created, await Physical.PublishCreateOnlyAsync(
            PrivatePublicationOwner.ForReport(Facts.Actor, claim), claim.Key.Scope, reference, new MemoryStream(bytes)));
        if (failed)
            Assert.Equal(RevisionReportStoreOutcome.Applied, (await Facts.Reports.FinishAsync(Facts.Actor, claim,
                RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.BackendFailure))).Outcome);
        await Facts.SyntheticAgeReportAsync(claim, TimeSpan.FromHours(25));
        return (claim, reference, bytes);
    }

    internal Task<string> OriginsAsync() => OriginsAsync(Facts.Case);

    internal static Task<string> OriginsAsync(PostgresCase test) => test.ScalarAsync<string>("""
        SELECT jsonb_build_object(
          'mappings',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY organization_id,project_id,id),'[]') FROM braid.mappings x),
          'revisions',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY organization_id,project_id,mapping_id,id),'[]') FROM braid.mapping_revisions x),
          'ingestions',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY operation_id),'[]') FROM braid.revision_ingestions x),
          'native_attempts',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY operation_id,generation),'[]') FROM braid.revision_ingestion_attempts x),
          'keys',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY cache_entry_id),'[]') FROM braid.revision_derivations x),
          'reports',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY derivation_id),'[]') FROM braid.revision_derivation_attempts x),
          'publications',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY publication_id),'[]') FROM braid.private_object_publications x),
          'memberships',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY organization_id,project_id,principal_id),'[]') FROM braid.project_memberships x))::text
        """);

    internal static async Task<int> AssertIdleAsync(PostgresPrivateObjectMaintenanceDatabase database, string name, int? expected = null)
    {
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString).MaxPoolSize);
        await using var connection = await database.DataSource.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await using var command = new NpgsqlCommand("""
            SELECT pg_backend_pid(),NULLIF(current_setting('braid.organization_id',true),''),
              NULLIF(current_setting('braid.project_id',true),''),
              (SELECT count(*) FROM pg_stat_activity WHERE datname=$1 AND usename=current_user
                AND pid<>pg_backend_pid() AND xact_start IS NOT NULL)
            """, connection);
        command.Parameters.AddWithValue(name);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var pid = rows.GetInt32(0);
        if (expected is not null) Assert.Equal(expected.Value, pid);
        Assert.True(rows.IsDBNull(1));
        Assert.True(rows.IsDBNull(2));
        Assert.Equal(0L, rows.GetInt64(3));
        Assert.False(await rows.ReadAsync());
        return pid;
    }

    internal static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    internal static byte[] ReadOwnedRawFile(string directory, byte[] name)
    {
        var parent = OpenRawDirectory(directory, 0x10000 | 0x20000 | 0x80000);
        if (parent < 0) throw new IOException("The owned raw-name parent could not be opened.");
        using var heldParent = new SafeFileHandle((IntPtr)parent, true);
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated, 0);
        var file = OpenRawFile(parent, terminated, 0x20000 | 0x80000);
        if (file < 0) throw new IOException("The owned raw-name fixture could not be read.");
        using var input = new FileStream(new SafeFileHandle((IntPtr)file, true), FileAccess.Read);
        Assert.InRange(input.Length, 0, 1024);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenRawDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenRawFile(int directory, [In] byte[] name, int flags);

    internal static async Task AuthoredAgeActualOriginsAsync(PostgresCase test, ProjectScope project)
    {
        // This is explicitly authored25h fixture time movement, not a claim that
        // the real C2/report work ran25h ago. Publication seals/timestamps are NOT
        // changed: tracked-temp eligibility is tested separately with its full chain.
        await using var owner = await ProjectCreationTestSupport.OpenOwnerAsync(test);
        await using var transaction = await owner.BeginTransactionAsync();
        var tables = new[] { "revision_ingestions", "revision_ingestion_attempts", "revision_derivation_attempts" };
        foreach (var table in tables)
            await new NpgsqlCommand($"ALTER TABLE braid.{table} DISABLE TRIGGER {table}_transition_guard", owner, transaction).ExecuteNonQueryAsync();
        foreach (var sql in new[]
        {
            "UPDATE braid.revision_ingestions SET created_at=created_at-$1,completed_at=completed_at-$1,lease_expires_at=lease_expires_at-$1,verified_stage_at=verified_stage_at-$1 WHERE organization_id=$2 AND project_id=$3",
            "UPDATE braid.revision_ingestion_attempts SET started_at=started_at-$1,finished_at=finished_at-$1,verified_stage_at=verified_stage_at-$1 WHERE organization_id=$2 AND project_id=$3",
            "UPDATE braid.revision_derivation_attempts SET started_at=started_at-$1,lease_expires_at=lease_expires_at-$1,finished_at=finished_at-$1 WHERE organization_id=$2 AND project_id=$3"
        })
        {
            await using var command = new NpgsqlCommand(sql, owner, transaction);
            command.Parameters.AddWithValue(TimeSpan.FromHours(25));
            command.Parameters.AddWithValue(project.OrganizationId);
            command.Parameters.AddWithValue(project.ProjectId);
            await command.ExecuteNonQueryAsync();
        }
        await new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", owner, transaction).ExecuteNonQueryAsync();
        foreach (var table in tables)
            await new NpgsqlCommand($"ALTER TABLE braid.{table} ENABLE TRIGGER {table}_transition_guard", owner, transaction).ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Physical.DisposeAsync();
        Root.Dispose();
        await Facts.DisposeAsync();
    }
}

internal sealed class CommandMetadata(IPrivateObjectMaintenanceMetadata actual) : IPrivateObjectMaintenanceMetadata
{
    internal List<ObjectCleanupClaim> Claims { get; } = [];
    internal List<(ObjectCleanupClaim Claim, ObjectCleanupDisposition Disposition, ObjectCleanupFinishResult Result)> Finishes { get; } = [];
    internal int ClaimCalls { get; private set; }
    public async Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid instance,
        ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default)
    {
        ClaimCalls++;
        var batch = await actual.ClaimAsync(project, instance, after, cancellationToken);
        Claims.AddRange(batch.Claims);
        return batch;
    }
    public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("The command must use its shared settlement overload.");
    public async Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
        CancellationToken cancellationToken, CancellationToken settlementCancellationToken)
    {
        var result = await actual.FinishAsync(claim, disposition, cancellationToken, settlementCancellationToken);
        Finishes.Add((claim, disposition, result));
        return result;
    }
    public Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
        CancellationToken cancellationToken = default) => actual.GetAttemptAsync(project, retirementId, generation, cancellationToken);
}

internal sealed class CommandRetirement(IPrivateObjectRetirementStore actual) : IPrivateObjectRetirementStore
{
    internal Func<ObjectCleanupClaim, CancellationToken, Task>? Before { get; set; }
    internal Func<ObjectCleanupClaim, PrivateObjectRetirementOutcome, CancellationToken, Task>? After { get; set; }
    internal List<(ObjectCleanupClaim Claim, PrivateObjectRetirementOutcome Outcome)> Returns { get; } = [];
    public async Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim, CancellationToken cancellationToken = default)
    {
        if (Before is { } before) await before(claim, cancellationToken);
        var outcome = await actual.RetireAsync(claim, cancellationToken);
        Returns.Add((claim, outcome));
        if (After is { } after) await after(claim, outcome, cancellationToken);
        return outcome;
    }
}
