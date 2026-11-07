using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

/// <summary>Real C2 source, PostgreSQL report metadata, private objects and Ferrule; no synthetic positive evidence.</summary>
[SupportedOSPlatform("linux")]
internal sealed class RevisionReportTestSupport : IAsyncDisposable
{
    internal RevisionIngestionTestSupport Ingestion { get; }
    internal PostgresCase Case => Ingestion.Case;
    internal PostgresSeedData Seed => Ingestion.Seed;
    internal ActorContext Actor => Ingestion.Actor;
    internal ProjectScope Project => Ingestion.Project;
    internal PrivateStorageTestRoot PhysicalRoot => Ingestion.PhysicalRoot;
    internal FileSystemPrivateObjectStore PhysicalStore => Ingestion.PhysicalStore;
    internal IngestionReceipt Receipt { get; }
    internal RevisionScope Revision => Receipt.Facts.FutureRevision;
    internal RevisionReportSource Source { get; }
    internal byte[] NativeBytes { get; }
    internal PostgresRevisionReportDatabase ReportDatabase { get; }
    internal PostgresRevisionDerivationDatabase DerivationDatabase { get; }
    internal PostgresRevisionReportMetadataReader Reader { get; }
    internal PostgresRevisionDerivationStore Store { get; }
    internal RevisionReportMetadataPort MetadataPort { get; }
    internal RevisionReportStoragePort StoragePort { get; }
    internal RevisionReportBackendPort BackendPort { get; }
    internal RevisionReportBackendProfile Profile { get; }
    internal IRevisionReportProfileRegistry Registry { get; }
    internal RevisionReportService Service { get; }

    private RevisionReportTestSupport(RevisionIngestionTestSupport ingestion, IngestionReceipt receipt,
        RevisionReportSource source, byte[] nativeBytes, PostgresRevisionReportDatabase reads,
        PostgresRevisionDerivationDatabase writes)
    {
        Ingestion = ingestion;
        Receipt = receipt;
        Source = source;
        NativeBytes = nativeBytes.ToArray();
        ReportDatabase = reads;
        DerivationDatabase = writes;
        Reader = new(reads);
        Store = new(writes);
        MetadataPort = new(Reader, Store);
        StoragePort = new(ingestion.PhysicalStore);
        Profile = FerruleRevisionReportBackendFactory.CreateProfile(ingestion.Runner.BuildIdentity);
        var real = FerruleRevisionReportBackendFactory.CreateRegistry(Profile, new(), () => ingestion.Runner);
        BackendPort = new(real);
        Registry = NewRegistry();
        Service = NewService();
    }

    internal static async Task<RevisionReportTestSupport> CreateAsync(PostgresFixture fixture,
        string nativeFixture = "versioned-exact", IIngestionBackendRegistry? ingestionRegistry = null)
    {
        var ingestion = await RevisionIngestionTestSupport.CreateAsync(fixture);
        PostgresRevisionReportDatabase? reads = null;
        PostgresRevisionDerivationDatabase? writes = null;
        try
        {
            var bytes = RevisionIngestionTestSupport.Fixture(nativeFixture);
            var created = ingestionRegistry is null ? await ingestion.CreateAsync(bytes)
                : await ingestion.NewService(ingestionRegistry).CreateAsync(ingestion.Actor, ingestion.Project, Guid.NewGuid(),
                    RevisionIngestionTestSupport.Request(), new MemoryStream(bytes));
            Assert.True(created.Outcome is RevisionIngestionOutcome.CommittedValid or RevisionIngestionOutcome.CommittedDecodedInvalid,
                $"Actual native fixture {nativeFixture}: ingestion outcome {created.Outcome}, failure {created.Failure}.");
            var receipt = Assert.IsType<IngestionReceipt>(await ingestion.PrivateReceiptAsync(created.Receipt!.OperationId));
            reads = new(ingestion.Case.ReportConnectionString);
            writes = new(ingestion.Case.DerivationConnectionString);
            var source = Assert.IsType<AuthorizedRevisionReportSource>(await new PostgresRevisionReportMetadataReader(reads)
                .GetSourceAsync(ingestion.Actor, receipt.Facts.FutureRevision));
            Assert.Equal(receipt.Facts.NativeObject, source.Source.NativeObject);
            Assert.Equal(bytes, await ingestion.PhysicalStore.ReadVerifiedAsync(receipt.Facts.FutureRevision, source.Source.NativeObject));
            return new(ingestion, receipt, source.Source, bytes, reads, writes);
        }
        catch
        {
            if (reads is not null) await reads.DisposeAsync();
            if (writes is not null) await writes.DisposeAsync();
            await ingestion.DisposeAsync();
            throw;
        }
    }

    internal IRevisionReportProfileRegistry NewRegistry(RevisionReportPolicy? policy = null,
        bool includeCurrent = true, Func<IRevisionReportBackend>? backendFactory = null)
    {
        policy ??= new();
        var projection = new RevisionReportFormat("ferrule", RevisionReportOperation.Projection,
            "weft.mapping-projection.v1", FerruleInspectionBackend.ProjectionVersion, FerruleRevisionReportBackendFactory.CodecVersion);
        var validation = new RevisionReportFormat("ferrule", RevisionReportOperation.Validation,
            "weft.mapping-validation.v1", FerruleRevisionReportBackendFactory.ValidationAlgorithm, FerruleRevisionReportBackendFactory.CodecVersion);
        Func<IRevisionReportBackend> factory = backendFactory ?? (() => BackendPort);
        RevisionReportRegistration[] current = includeCurrent
            ? [new(Profile, projection, policy, factory), new(Profile, validation, policy, factory)] : [];
        var validator = new FerruleRevisionReportFormatValidator();
        return new RevisionReportProfileRegistry(current, [new(projection, validator), new(validation, validator)]);
    }

    internal RevisionReportService NewService(IRevisionReportProfileRegistry? registry = null,
        IPrivateObjectStore? store = null, IRevisionReportMetadataReader? reader = null,
        IRevisionReportMetadataStore? metadata = null, RevisionReportPolicy? activeReadCaps = null,
        Guid? serverInstanceId = null) => new(reader ?? MetadataPort, metadata ?? MetadataPort,
            registry ?? Registry, new(PrivateStorageTestRoot.NativeLimit, PrivateStorageTestRoot.DerivedLimit,
                PrivateStorageTestRoot.DerivedLimit, () => store ?? StoragePort), serverInstanceId ?? Guid.NewGuid(), activeReadCaps);

    internal RevisionReportKey Key(RevisionReportOperation operation, RevisionReportPolicy? policy = null) => operation switch
    {
        RevisionReportOperation.Projection => new ProjectionCacheKey(Source, Profile, "weft.mapping-projection.v1",
            FerruleInspectionBackend.ProjectionVersion, FerruleRevisionReportBackendFactory.CodecVersion, policy ?? new()),
        RevisionReportOperation.Validation => new ValidationCacheKey(Source, Profile, "weft.mapping-validation.v1",
            FerruleRevisionReportBackendFactory.ValidationAlgorithm, FerruleRevisionReportBackendFactory.CodecVersion, policy ?? new()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal Task<long[]> CountsAsync(NpgsqlConnection? owner = null) => ScalarAsync<long[]>(owner, """
        SELECT ARRAY[(SELECT count(*) FROM weft.revision_derivations),
                     (SELECT count(*) FROM weft.revision_derivation_attempts),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable),
                     (SELECT count(*) FROM weft.revision_derivation_attempts WHERE completion_kind='failed')]
        """);

    internal Task<string> SourceSnapshotAsync(NpgsqlConnection? owner = null) => ScalarAsync<string>(owner, """
        SELECT jsonb_build_object('mapping',(SELECT to_jsonb(m) FROM weft.mappings m
            WHERE organization_id=$1 AND project_id=$2 AND id=$3),
            'revisions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY sequence) FROM weft.mapping_revisions r
            WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3),
            'ingestion',(SELECT to_jsonb(i) FROM weft.revision_ingestions i
            WHERE organization_id=$1 AND project_id=$2 AND operation_id=$4))::text
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Receipt.Facts.OperationId);

    internal Task<string> AttemptSnapshotAsync(Guid derivationId, NpgsqlConnection? owner = null) => ScalarAsync<string>(owner, """
        SELECT to_jsonb(a)::text FROM weft.revision_derivation_attempts a
        WHERE organization_id=$1 AND project_id=$2 AND mapping_id=$3 AND revision_id=$4 AND derivation_id=$5
        """, Revision.OrganizationId, Revision.ProjectId, Revision.MappingId, Revision.RevisionId, derivationId);

    private async Task<T> ScalarAsync<T>(NpgsqlConnection? owner, string sql, params object[] values)
    {
        if (owner is null) return await Case.ScalarAsync<T>(sql, values);
        await using var command = new NpgsqlCommand(sql, owner);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected owned test scalar."));
    }

    internal async Task<int[]> AssertPoolsUnboundAsync(int[]? expectedPids = null)
    {
        var pids = new List<int>();
        foreach (var pool in new[] { ReportDatabase.DataSource, DerivationDatabase.DataSource })
        {
            await using var connection = await pool.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await using var command = new NpgsqlCommand("""
                SELECT pg_backend_pid(), NULLIF(current_setting('weft.organization_id',true),''),
                    (SELECT count(*) FROM pg_stat_activity WHERE datname=$1 AND usename=current_user
                     AND pid<>pg_backend_pid() AND xact_start IS NOT NULL)
                """, connection);
            command.Parameters.AddWithValue(Case.Name);
            await using var rows = await command.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            pids.Add(rows.GetInt32(0));
            Assert.True(rows.IsDBNull(1));
            Assert.Equal(0L, rows.GetInt64(2));
        }
        var result = pids.ToArray();
        if (expectedPids is not null) Assert.Equal(expectedPids, result);
        return result;
    }

    internal static VerifiedRevisionReport Available(RevisionReportEnsureResult result)
    {
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        return Available(Assert.IsType<RevisionReportReadResult>(result.Read));
    }

    internal static VerifiedRevisionReport Available(RevisionReportReadResult result)
    {
        Assert.Equal(RevisionReportReadOutcome.Observed, result.Outcome);
        Assert.Equal(RevisionReportState.Available, result.State);
        return Assert.IsAssignableFrom<VerifiedRevisionReport>(result.Report);
    }

    public async ValueTask DisposeAsync()
    {
        await ReportDatabase.DisposeAsync();
        await DerivationDatabase.DisposeAsync();
        await Ingestion.DisposeAsync();
    }
}

internal sealed class RevisionReportBoundaryGate
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Release() => release.TrySetResult();
    internal async Task HoldAsync(CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
    }
}

internal enum RevisionReportIoBoundary { BeforeRead, AfterRead, BeforePublish, AfterPublish }
internal sealed record RevisionReportIoObservation(RevisionReportIoBoundary Boundary, RevisionScope Scope,
    PrivateObjectReference Reference, PrivateObjectPublishResult? Publication, long? ActualByteCount, Sha256Digest? ActualDigest);

internal sealed class RevisionReportStoragePort(IPrivateObjectStore inner) : IPrivateObjectStore
{
    private readonly object sync = new();
    private readonly List<RevisionReportIoObservation> observations = [];
    internal Func<RevisionReportIoObservation, CancellationToken, Task>? ObserveAsync { get; set; }
    /// <summary>Explicit synthetic dishonest-port fault, applied only after an actual verified read.</summary>
    internal Func<PrivateObjectReference, byte[], byte[]>? ReadSubstitution { get; set; }
    internal IReadOnlyList<RevisionReportIoObservation> Observations { get { lock (sync) return observations.ToArray(); } }
    internal int NativeReads => Observations.Count(value => value.Boundary == RevisionReportIoBoundary.BeforeRead && value.Reference.Id.Class == PrivateObjectClass.NativeArtifact);
    internal int DerivedReads => Observations.Count(value => value.Boundary == RevisionReportIoBoundary.BeforeRead && value.Reference.Id.Class == PrivateObjectClass.DerivedArtifact);
    internal int PublishCalls => Observations.Count(value => value.Boundary == RevisionReportIoBoundary.BeforePublish);

    private async Task ObserveAsyncCore(RevisionReportIoObservation observation, CancellationToken cancellationToken)
    {
        lock (sync) observations.Add(observation);
        if (ObserveAsync is { } observe) await observe(observation, cancellationToken);
    }

    public async Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        await ObserveAsyncCore(new(RevisionReportIoBoundary.BeforeRead, scope, reference, null, null, null), cancellationToken);
        var bytes = await inner.ReadVerifiedAsync(scope, reference, cancellationToken);
        await ObserveAsyncCore(new(RevisionReportIoBoundary.AfterRead, scope, reference, null, bytes.LongLength, Sha256Digest.FromBytes(bytes)), cancellationToken);
        return ReadSubstitution?.Invoke(reference, bytes) ?? bytes;
    }

    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
        Stream source, CancellationToken cancellationToken = default)
    {
        await ObserveAsyncCore(new(RevisionReportIoBoundary.BeforePublish, scope, reference, null, null, null), cancellationToken);
        var published = await inner.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
        await ObserveAsyncCore(new(RevisionReportIoBoundary.AfterPublish, scope, reference, published, null, null), cancellationToken);
        return published;
    }
}

internal sealed class RevisionReportBackendPort(IRevisionReportProfileRegistry actualRegistry) : IRevisionReportBackend
{
    private readonly object sync = new();
    private readonly List<RevisionReportBackendResult> completions = [];
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    internal IReadOnlyList<RevisionReportBackendResult> Completions { get { lock (sync) return completions.ToArray(); } }
    internal Func<RevisionReportKey, RevisionReportBackendResult, CancellationToken, Task>? AfterDeriveAsync { get; set; }
    public async Task<RevisionReportBackendResult> DeriveAsync(RevisionReportKey key, NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        var registration = actualRegistry.FindCurrent(key.BackendId, key.Operation)
            ?? throw new InvalidOperationException("Missing real test backend.");
        var result = await registration.GetBackend().DeriveAsync(key, artifact, cancellationToken);
        lock (sync) completions.Add(result);
        if (AfterDeriveAsync is { } observe) await observe(key, result, cancellationToken);
        return result;
    }
}

internal sealed record RevisionReportFinishObservation(RevisionReportClaim Claim, RevisionReportCompletion Completion, RevisionReportStoreResult Result);

internal sealed class RevisionReportMetadataPort(IRevisionReportMetadataReader reader, IRevisionReportMetadataStore store)
    : IRevisionReportMetadataReader, IRevisionReportMetadataStore
{
    private readonly object sync = new();
    private readonly List<RevisionReportClaim> claims = [];
    private readonly List<RevisionReportFinishObservation> finishes = [];
    private int sourceReads, currentReads, retainedReads, claimCalls, finishCalls, repairCalls;
    internal int SourceReads => Volatile.Read(ref sourceReads);
    internal int CurrentReads => Volatile.Read(ref currentReads);
    internal int RetainedReads => Volatile.Read(ref retainedReads);
    internal int ClaimCalls => Volatile.Read(ref claimCalls);
    internal int FinishCalls => Volatile.Read(ref finishCalls);
    internal int RepairCalls => Volatile.Read(ref repairCalls);
    internal IReadOnlyList<RevisionReportClaim> Claims { get { lock (sync) return claims.ToArray(); } }
    internal IReadOnlyList<RevisionReportFinishObservation> Finishes { get { lock (sync) return finishes.ToArray(); } }
    internal Func<RevisionReportReadSnapshot?, CancellationToken, Task>? AfterCurrentAsync { get; set; }
    internal Func<RevisionReportClaim, CancellationToken, Task>? AfterClaimAsync { get; set; }
    internal Func<RevisionReportClaim, RevisionReportCompletion, CancellationToken, Task>? BeforeFinishAsync { get; set; }
    internal Func<RevisionReportFinishObservation, CancellationToken, Task>? AfterFinishAsync { get; set; }
    internal Func<RevisionReportRepairTarget, CancellationToken, Task>? BeforeRepairAsync { get; set; }

    public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref sourceReads);
        return reader.GetSourceAsync(actor, scope, cancellationToken);
    }
    public async Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref currentReads);
        var result = await reader.GetCurrentAsync(actor, key, cancellationToken);
        if (AfterCurrentAsync is { } observe) await observe(result, cancellationToken);
        return result;
    }
    public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref retainedReads);
        return reader.GetRetainedAsync(actor, scope, operation, derivationId, cancellationToken);
    }
    private async Task<RevisionReportStoreResult> ObserveClaimAsync(RevisionReportStoreResult result, CancellationToken cancellationToken)
    {
        if (result.Claim is { } claim)
        {
            lock (sync) claims.Add(claim);
            if (AfterClaimAsync is { } observe) await observe(claim, cancellationToken);
        }
        return result;
    }
    public async Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key, Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref claimCalls);
        return await ObserveClaimAsync(await store.TryClaimAsync(actor, key, serverInstanceId, cancellationToken), cancellationToken);
    }
    public async Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
        RevisionReportCompletion completion, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref finishCalls);
        if (BeforeFinishAsync is { } before) await before(claim, completion, cancellationToken);
        var result = await store.FinishAsync(actor, claim, completion, cancellationToken);
        var observation = new RevisionReportFinishObservation(claim, completion, result);
        lock (sync) finishes.Add(observation);
        if (AfterFinishAsync is { } after) await after(observation, cancellationToken);
        return result;
    }
    public async Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
        Guid serverInstanceId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref repairCalls);
        if (BeforeRepairAsync is { } before) await before(target, cancellationToken);
        return await ObserveClaimAsync(await store.RepairAsync(actor, target, serverInstanceId, cancellationToken), cancellationToken);
    }
}
