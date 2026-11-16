using System.Runtime.Versioning;
using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Server;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>
/// Real C2 services, ordinary restricted pools, pinned worker and a fresh v2 root.
/// The reused runner fixture's legacy root is unused; no positive workflow is seeded.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PostgresClaimAwareStorageIntegrationSupport : IAsyncDisposable
{
    internal RevisionIngestionTestSupport Native { get; }
    internal PostgresCase Case => Native.Case;
    internal ActorContext Actor => Native.Actor;
    internal ProjectScope Project => Native.Project;
    internal PrivateStorageTestRoot Root { get; }
    internal FileSystemV2PrivateObjectStore Physical { get; }
    internal ClaimAwareServiceStore Storage { get; }
    internal ClaimAwareNativeMetadata Metadata { get; }
    internal RevisionIngestionService Ingestion { get; }
    internal PostgresRevisionReportDatabase ReportDatabase { get; }
    internal PostgresRevisionDerivationDatabase DerivationDatabase { get; }
    internal PostgresRevisionReportMetadataReader ReportReader { get; }
    internal PostgresRevisionDerivationStore ReportWriter { get; }
    internal RevisionReportMetadataPort ReportMetadata { get; }
    internal RevisionReportBackendPort ReportBackend { get; }
    internal IRevisionReportProfileRegistry ReportRegistry { get; }
    internal RevisionReportService Reports { get; }
    internal ClaimAwareServiceRegistry NativeRegistry { get; }
    internal ClaimAwareServiceRegistry ReportPublicationRegistry { get; }
    internal int RegistryFactoryResolutions { get; private set; }
    internal int NativeRegistryResolutions { get; private set; }
    internal int ReportRegistryResolutions { get; private set; }
    internal Func<V2StorageObservation, CancellationToken, ValueTask>? ObserveAsync { get; set; }

    private PostgresClaimAwareStorageIntegrationSupport(RevisionIngestionTestSupport native, PrivateStorageTestRoot root, bool activated)
    {
        Native = native;
        Root = root;
        ReportDatabase = new(Case.ReportConnectionString);
        DerivationDatabase = new(Case.DerivationConnectionString);
        ReportReader = new(ReportDatabase);
        ReportWriter = new(DerivationDatabase);
        ReportMetadata = new(ReportReader, ReportWriter);
        NativeRegistry = new(new PostgresIngestionPublicationRegistry(native.Database));
        ReportPublicationRegistry = new(new PostgresRevisionReportPublicationRegistry(DerivationDatabase));
        var router = new LazyPrivateObjectPublicationRegistry(
            () => { NativeRegistryResolutions++; return NativeRegistry; },
            () => { ReportRegistryResolutions++; return ReportPublicationRegistry; });
        IPrivateObjectPublicationRegistry RegistryFactory() { RegistryFactoryResolutions++; return router; }
        var observer = new PrivateStorageV2Observer((observation, token) => ObserveAsync?.Invoke(observation, token) ?? ValueTask.CompletedTask);
        Physical = activated ? FileSystemV2PrivateObjectStore.Open(root.Options, RegistryFactory, observer)
            : FileSystemV2PrivateObjectStore.Initialize(root.Options, RegistryFactory, observer);
        Storage = new(Physical);
        Metadata = new(native.Metadata);
        Ingestion = NewIngestion();
        var profile = FerruleRevisionReportBackendFactory.CreateProfile(native.Runner.BuildIdentity);
        var actual = FerruleRevisionReportBackendFactory.CreateRegistry(profile, new(), () => native.Runner);
        ReportBackend = new(actual);
        var registrations = Enum.GetValues<RevisionReportOperation>().Select(operation =>
            actual.FindCurrent("ferrule", operation) ?? throw new InvalidOperationException("Missing actual report registration.")).ToArray();
        ReportRegistry = new RevisionReportProfileRegistry(
            registrations.Select(registration => new RevisionReportRegistration(registration.Profile, registration.Format,
                registration.Policy, () => ReportBackend)),
            registrations.Select(registration => new RevisionReportStoredFormatRegistration(registration.Format,
                actual.FindStored(registration.Format) ?? throw new InvalidOperationException("Missing actual report format."))));
        Reports = NewReports();
    }

    internal static async Task<PostgresClaimAwareStorageIntegrationSupport> CreateAsync(PostgresFixture fixture, bool activateLegacy = false, IInspectionWorkerRunner? serviceRunner = null)
    {
        var native = await RevisionIngestionTestSupport.CreateAsync(fixture, serviceRunner);
        var root = new PrivateStorageTestRoot();
        try
        {
            if (activateLegacy)
            {
                // Only this test-owned empty v1 root is quiesced and converted.
                // The v1 store is closed before the explicit offline acknowledgement.
                using (FileSystemPrivateObjectStore.Initialize(root.Options)) { }
                var originalMarker = File.ReadAllText(root.MarkerPath);
                var activation = await FileSystemV2PrivateObjectStore.ActivateAsync(root.Options, offlineAcknowledged: true);
                Assert.Equal(PrivateStorageV2ActivationOutcome.Activated, activation.Outcome);
                Assert.NotNull(activation.Receipt);
                Assert.Contains(activation.Receipt.RootId.ToString("N"), originalMarker, StringComparison.Ordinal);
                Assert.Equal(activation.Receipt.RootId, PrivateStorageV2Data.Marker(root).Root);
            }
            return new(native, root, activateLegacy);
        }
        catch { root.Dispose(); await native.DisposeAsync(); throw; }
    }

    internal RevisionIngestionService NewIngestion(IPrivateObjectStore? storage = null) => new(Metadata, Native.Metadata,
        new PostgresProjectReader(Native.ReaderDatabase), new(storage ?? Storage, FileSystemStorageOptions.NativeHardMaximum),
        new IngestionBackendRegistry([Native.Registration]));

    internal RevisionReportService NewReports(IPrivateObjectStore? storage = null,
        IRevisionReportMetadataStore? metadata = null, IRevisionReportProfileRegistry? registry = null) => new(
        ReportMetadata, metadata ?? ReportMetadata, registry ?? ReportRegistry,
        new(checked((int)FileSystemStorageOptions.NativeHardMaximum), checked((int)FileSystemStorageOptions.DerivedHardMaximum),
            checked((int)FileSystemStorageOptions.DerivedHardMaximum), () => storage ?? Storage), Guid.NewGuid());

    internal Task<RevisionIngestionResult> CreateNativeAsync(byte[] bytes, Guid? key = null, CancellationToken cancellationToken = default) =>
        Ingestion.CreateAsync(Actor, Project, key ?? Guid.NewGuid(), RevisionIngestionTestSupport.Request(), new MemoryStream(bytes), cancellationToken);

    internal async Task<IngestionReceipt> ReceiptAsync(RevisionIngestionResult result) =>
        Assert.IsType<IngestionReceipt>(await Native.Metadata.GetReceiptAsync(Actor, Project, result.Receipt!.OperationId));

    internal async Task<int[]> AssertPoolsUnboundAsync(int[]? expected = null)
    {
        var pids = new List<int>();
        foreach (var pool in new[] { Native.Database.DataSource, Native.ReaderDatabase.DataSource,
            ReportDatabase.DataSource, DerivationDatabase.DataSource })
        {
            Assert.Equal(1, new NpgsqlConnectionStringBuilder(pool.ConnectionString).MaxPoolSize);
            await using var connection = await pool.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await using var command = new NpgsqlCommand("""
                SELECT pg_backend_pid(),NULLIF(current_setting('braid.organization_id',true),''),
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
        var current = pids.ToArray();
        if (expected is not null) Assert.Equal(expected, current);
        return current;
    }

    public async ValueTask DisposeAsync()
    {
        Native.Launcher.ReleaseAll();
        await Physical.DisposeAsync();
        await ReportDatabase.DisposeAsync();
        await DerivationDatabase.DisposeAsync();
        await Native.DisposeAsync();
        Root.Dispose();
    }
}

internal sealed class ClaimAwareNativeMetadata(IIngestionMetadataStore inner) : IIngestionMetadataStore
{
    internal List<IngestionClaim> Claims { get; } = [];
    public Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project, Guid key, IngestionIntent intent,
        IngestionBackendProfile profile, IngestionAdmissionPolicy policy, CancellationToken cancellationToken = default) =>
        inner.ReserveAsync(actor, project, key, intent, profile, policy, cancellationToken);
    public async Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operation, Guid instance,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.ClaimAsync(actor, project, operation, instance, cancellationToken);
        if (result.Claim is { } claim) Claims.Add(claim);
        return result;
    }
    public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim, PrivateObjectReference reference,
        CancellationToken cancellationToken = default) => inner.MarkStagedAsync(actor, claim, reference, cancellationToken);
    public Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim, IngestionAttemptCompletion completion,
        CancellationToken cancellationToken = default) => inner.CompleteAttemptAsync(actor, claim, completion, cancellationToken);
    public Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim, CancellationToken cancellationToken = default) =>
        inner.FinalizeAsync(actor, claim, cancellationToken);
    public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operation,
        CancellationToken cancellationToken = default) => inner.GetReceiptAsync(actor, project, operation, cancellationToken);
}

internal sealed class ClaimAwareServiceRegistry(IPrivateObjectPublicationRegistry actual) : IPrivateObjectPublicationRegistry
{
    internal IPrivateObjectPublicationRegistry Actual => actual;
    internal IPrivateObjectPublicationRegistry? Override { get; set; }
    internal List<PrivatePublicationSnapshot> Reservations { get; } = [];
    internal List<PrivatePublicationSnapshot> Bindings { get; } = [];
    internal List<PrivatePublicationKey> Recoveries { get; } = [];
    internal Func<PrivatePublicationReservation, CancellationToken, Task>? BeforeBindAsync { get; set; }
    public async Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
    {
        var result = await (Override ?? actual).ReserveAsync(owner, storeKind, rootId, objectId, tempId, absenceRecordDigest, cancellationToken);
        if (result.Snapshot is { } snapshot) Reservations.Add(snapshot);
        return result;
    }
    public async Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
    {
        if (BeforeBindAsync is { } before) await before(reservation, cancellationToken);
        var result = await (Override ?? actual).BindAsync(reservation, actualIdentity, boundRecordDigest, cancellationToken);
        if (result.Snapshot is { } snapshot) Bindings.Add(snapshot);
        return result;
    }
    public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
    {
        Recoveries.Add(new(rootId, objectId, tempId));
        return (Override ?? actual).GetAsync(owner, storeKind, rootId, objectId, tempId, cancellationToken);
    }
}

internal sealed class ClaimAwareServicePublication(PrivatePublicationOwner owner, PrivateObjectReference reference)
{
    internal PrivatePublicationOwner Owner { get; } = owner;
    internal PrivateObjectReference Reference { get; } = reference;
    internal int ReadCalls;
    internal long BytesRead;
    internal PrivateObjectPublishResult? Result { get; set; }
}

internal sealed class ClaimAwareServiceStore(IPrivateObjectPublicationStore actual) : IPrivateObjectPublicationStore
{
    internal List<ClaimAwareServicePublication> Publications { get; } = [];
    internal int PlainCalls { get; private set; }
    internal int NativeReads { get; private set; }
    internal int DerivedReads { get; private set; }
    internal Func<ClaimAwareServicePublication, CancellationToken, Task>? BeforeFirstPayloadReadAsync { get; set; }
    public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference,
        Stream source, CancellationToken cancellationToken = default)
    {
        PlainCalls++;
        return actual.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
    }
    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner, RevisionScope scope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        var publication = new ClaimAwareServicePublication(owner, reference);
        Publications.Add(publication);
        using var input = new PayloadSource(source, publication, BeforeFirstPayloadReadAsync);
        var result = await actual.PublishCreateOnlyAsync(owner, scope, reference, input, cancellationToken);
        publication.Result = result;
        return result;
    }
    public Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Id.Class == PrivateObjectClass.NativeArtifact) NativeReads++; else DerivedReads++;
        return actual.ReadVerifiedAsync(scope, reference, cancellationToken);
    }

    private sealed class PayloadSource(Stream source, ClaimAwareServicePublication publication,
        Func<ClaimAwareServicePublication, CancellationToken, Task>? before) : Stream
    {
        private bool entered;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!entered)
            {
                entered = true;
                if (before is not null) await before(publication, cancellationToken);
            }
            var count = await source.ReadAsync(buffer, cancellationToken);
            publication.ReadCalls++;
            publication.BytesRead += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
