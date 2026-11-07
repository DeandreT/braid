using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Ingestion;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.Tests;

public sealed class RevisionIngestionFactAttribute : FactAttribute
{
    public RevisionIngestionFactAttribute()
    {
        if (!PostgresFixture.IsConfigured && Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE") is null)
            Skip = "Configure the actual PostgreSQL and qualified Ferrule image to execute complete ingestion integration gates.";
    }
}

[SupportedOSPlatform("linux")]
internal sealed class RevisionIngestionTestSupport : IAsyncDisposable
{
    internal PostgresCase Case { get; }
    internal PostgresSeedData Seed { get; }
    internal ProjectScope Project { get; }
    internal ActorContext Actor { get; }
    internal PrivateStorageTestRoot PhysicalRoot { get; }
    internal FileSystemPrivateObjectStore PhysicalStore { get; }
    internal PostgresIngestionDatabase Database { get; }
    internal PostgresDatabase ReaderDatabase { get; }
    internal PostgresRevisionIngestionStore Metadata { get; }
    internal RevisionIngestionMetadataPort MetadataPort { get; }
    internal RevisionIngestionStorePort StorePort { get; }
    internal RevisionIngestionWorkerLauncher Launcher { get; }
    internal RevisionIngestionWorkerRunner Runner { get; }
    internal RevisionIngestionBackendPort BackendPort { get; }
    internal IngestionBackendRegistration Registration { get; }
    internal RevisionIngestionService Service { get; }

    private RevisionIngestionTestSupport(PostgresCase test, PostgresSeedData seed, PrivateStorageTestRoot root, IInspectionWorkerRunner? serviceRunner = null)
    {
        Case = test;
        Seed = seed;
        PhysicalRoot = root;
        PhysicalStore = root.Initialize();
        Project = new(seed.OrganizationA, seed.ProjectA);
        Actor = new(new PrincipalId(seed.Alice));
        Database = new(test.IngestionConnectionString);
        ReaderDatabase = new(test.RuntimeConnectionString);
        Metadata = new(Database);
        MetadataPort = new(Metadata);
        StorePort = new(PhysicalStore);
        Launcher = new();
        var docker = Environment.GetEnvironmentVariable("WEFT_DOCKER_EXECUTABLE") ?? "/usr/bin/docker";
        Runner = new(serviceRunner ?? new DockerInspectionWorkerRunner(new(docker, Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")!), Launcher));
        var real = FerruleIngestionAdmissionBackend.CreateRegistration(Runner);
        BackendPort = new(real.Backend);
        Registration = new(real.Profile, real.NativeByteLimit, BackendPort);
        Service = NewService();
    }

    internal static async Task<RevisionIngestionTestSupport> CreateAsync(PostgresFixture fixture, IInspectionWorkerRunner? serviceRunner = null)
    {
        if (!PostgresFixture.IsConfigured || serviceRunner is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEFT_FERRULE_WORKER_IMAGE")))
            throw new InvalidOperationException("Complete integration requires both explicitly configured actual dependencies.");
        var test = await fixture.CreateCaseAsync();
        var root = new PrivateStorageTestRoot();
        try
        {
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
            await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Admitted, "publisher");
            return new(test, seed, root, serviceRunner);
        }
        catch { root.Dispose(); await test.DisposeAsync(); throw; }
    }

    internal RevisionIngestionService NewService(IIngestionBackendRegistry? registry = null, RevisionIngestionOptions? options = null,
        IPrivateObjectStore? store = null, long storeLimit = 4 * 1024 * 1024) => new(MetadataPort, Metadata,
        new PostgresProjectReader(ReaderDatabase), new(store ?? StorePort, storeLimit), registry ?? new IngestionBackendRegistry([Registration]), options);

    internal static CreateRevisionRequest Request(string name = "Actual native integration", string description = "Actual PostgreSQL/private store/Ferrule", string? filename = null) =>
        new(new(name, description), "ferrule", filename);

    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FerruleFixtures", name + ".json"));

    internal Task<RevisionIngestionResult> CreateAsync(byte[] bytes, Guid? key = null, CreateRevisionRequest? request = null, CancellationToken cancellationToken = default) =>
        Service.CreateAsync(Actor, Project, key ?? Guid.NewGuid(), request ?? Request(), new MemoryStream(bytes), cancellationToken);

    internal Task<IngestionReceipt?> PrivateReceiptAsync(Guid operationId) => Metadata.GetReceiptAsync(Actor, Project, operationId);

    internal async Task AssertNoHeldMetadataAsync(Guid unrelatedOperation, ProjectScope unrelatedProject)
    {
        var receipt = await Service.GetOwnReceiptAsync(Actor, unrelatedProject, unrelatedOperation).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(receipt);
        await using (var otherScope = await Database.BeginTransactionAsync(unrelatedProject.OrganizationId))
        {
            await using var rows = otherScope.CreateCommand("SELECT count(*) FROM weft.projects");
            Assert.Equal(1L, await rows.ExecuteScalarAsync());
        }
        await AssertNoHeldTransactionsAsync();
        await using var connection = await Case.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var probe = new Npgsql.NpgsqlCommand("SELECT operation_id FROM weft.revision_ingestions FOR UPDATE NOWAIT", connection, transaction);
        await probe.ExecuteNonQueryAsync();
    }

    internal async Task AssertNoHeldTransactionsAsync()
    {
        // Each restricted login can see its own sessions' activity. The nonsuperuser
        // migration owner cannot inspect other roles' xact_start values reliably.
        foreach (var pool in new[] { Database.DataSource, ReaderDatabase.DataSource })
        {
            await using var observer = await pool.OpenConnectionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await using var activity = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=$1 AND usename=current_user AND pid<>pg_backend_pid() AND xact_start IS NOT NULL", observer);
            activity.Parameters.AddWithValue(Case.Name);
            Assert.Equal(0L, await activity.ExecuteScalarAsync());
        }
    }

    public async ValueTask DisposeAsync()
    {
        Launcher.ReleaseAll();
        await Database.DisposeAsync();
        await ReaderDatabase.DisposeAsync();
        await PhysicalStore.DisposeAsync();
        await Case.DisposeAsync();
        PhysicalRoot.Dispose();
    }
}

internal sealed class RevisionIngestionGate
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

internal sealed class RevisionIngestionSource(Stream inner, RevisionIngestionGate gate) : Stream
{
    private int held;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref held, 1) == 0)
            await gate.HoldAsync(cancellationToken);
        return await inner.ReadAsync(buffer, cancellationToken);
    }
}

internal sealed class RevisionIngestionStorePort(IPrivateObjectStore inner) : IPrivateObjectStore
{
    private int publications;
    private int reads;
    private int failAfterPublish;
    internal int PublishCalls => Volatile.Read(ref publications);
    internal int ReadCalls => Volatile.Read(ref reads);
    internal RevisionIngestionGate? PublishReadGate { get; set; }
    internal RevisionIngestionGate? AfterReadGate { get; set; }
    internal void FailAfterNextRealPublication() => Interlocked.Exchange(ref failAfterPublish, 1);
    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope, PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref publications);
        var result = await inner.PublishCreateOnlyAsync(scope, reference,
            PublishReadGate is { } gate ? new RevisionIngestionSource(source, gate) : source, cancellationToken);
        if (Interlocked.Exchange(ref failAfterPublish, 0) == 1)
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Io);
        return result;
    }
    public async Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref reads);
        var bytes = await inner.ReadVerifiedAsync(scope, reference, cancellationToken);
        if (AfterReadGate is { } gate)
            await gate.HoldAsync(cancellationToken);
        return bytes;
    }
}

internal sealed class RevisionIngestionBackendPort(IIngestionAdmissionBackend inner) : IIngestionAdmissionBackend
{
    private int calls;
    private readonly object sync = new();
    private readonly List<IngestionAttemptCompletion> completions = [];
    internal int Calls => Volatile.Read(ref calls);
    internal IReadOnlyList<IngestionAttemptCompletion> Completions { get { lock (sync) return completions.ToArray(); } }
    internal Func<int, RevisionIngestionGate?>? AfterValidationGate { get; set; }
    public async Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref calls);
        var actual = await inner.ValidateAsync(artifact, cancellationToken);
        lock (sync) completions.Add(actual);
        if (AfterValidationGate?.Invoke(call) is { } gate)
            await gate.HoldAsync(cancellationToken);
        return actual;
    }
}

internal sealed class RevisionIngestionMetadataPort(IIngestionMetadataStore inner) : IIngestionMetadataStore
{
    internal RevisionIngestionGate? BeforeValidationCompletion { get; set; }
    internal RevisionIngestionGate? BeforeFinalize { get; set; }
    public Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project, Guid key, IngestionIntent intent, IngestionBackendProfile profile, IngestionAdmissionPolicy policy, CancellationToken cancellationToken = default) => inner.ReserveAsync(actor, project, key, intent, profile, policy, cancellationToken);
    public Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operation, Guid instance, CancellationToken cancellationToken = default) => inner.ClaimAsync(actor, project, operation, instance, cancellationToken);
    public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim, PrivateObjectReference reference, CancellationToken cancellationToken = default) => inner.MarkStagedAsync(actor, claim, reference, cancellationToken);
    public async Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim, IngestionAttemptCompletion completion, CancellationToken cancellationToken = default)
    {
        if (completion.Kind == IngestionAttemptCompletionKind.Validation && BeforeValidationCompletion is { } gate)
            await gate.HoldAsync(cancellationToken);
        return await inner.CompleteAttemptAsync(actor, claim, completion, cancellationToken);
    }
    public async Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim, CancellationToken cancellationToken = default)
    {
        if (BeforeFinalize is { } gate)
            await gate.HoldAsync(cancellationToken);
        return await inner.FinalizeAsync(actor, claim, cancellationToken);
    }
    public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operation, CancellationToken cancellationToken = default) => inner.GetReceiptAsync(actor, project, operation, cancellationToken);
}

internal sealed class RevisionIngestionWorkerRunner(IInspectionWorkerRunner inner) : IInspectionWorkerRunner
{
    private readonly object sync = new();
    private readonly List<byte[]> nativeBodies = [];
    private readonly List<string> headers = [];
    public string BuildIdentity => inner.BuildIdentity;
    internal IReadOnlyList<byte[]> NativeBodies { get { lock (sync) return nativeBodies.Select(bytes => bytes.ToArray()).ToArray(); } }
    internal IReadOnlyList<string> Headers { get { lock (sync) return headers.ToArray(); } }
    public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.Span));
        var nativeLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.Span[(4 + headerLength)..]));
        Assert.Equal(frame.Length, 8 + headerLength + nativeLength);
        lock (sync)
        {
            headers.Add(System.Text.Encoding.UTF8.GetString(frame.Span.Slice(4, headerLength)));
            nativeBodies.Add(frame.Slice(8 + headerLength, nativeLength).ToArray());
        }
        return await inner.RunAsync(frame, cancellationToken);
    }
}

internal sealed class RevisionIngestionWorkerLauncher : IWorkerProcessLauncher
{
    private readonly object sync = new();
    private readonly List<ProcessStartInfo> commands = [];
    private readonly List<StreamWriter> heldWriters = [];
    internal RevisionIngestionGate? NextProtocolGate { get; set; }
    internal IReadOnlyList<ProcessStartInfo> Commands { get { lock (sync) return commands.ToArray(); } }
    public IWorkerProcess Start(ProcessStartInfo start)
    {
        lock (sync) commands.Add(start);
        var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("The actual owned worker CLI did not start.");
        var gate = start.ArgumentList.Contains("--attach", StringComparer.Ordinal) ? NextProtocolGate : null;
        if (gate is not null)
        {
            NextProtocolGate = null;
            lock (sync) heldWriters.Add(process.StandardInput);
        }
        return new RealProcess(process, gate);
    }
    internal void ReleaseAll()
    {
        lock (sync)
            foreach (var writer in heldWriters)
                try { writer.Dispose(); } catch (ObjectDisposedException) { }
    }
    private sealed class RealProcess(Process process, RevisionIngestionGate? gate) : IWorkerProcess
    {
        private readonly StreamWriter writer = process.StandardInput;
        private readonly Stream input = gate is null ? process.StandardInput.BaseStream : new SignaledInput(process.StandardInput.BaseStream, gate);
        public Stream StandardInput => input;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput()
        {
            if (gate is null) writer.Dispose();
            else _ = CloseAfterReleaseAsync();
        }
        private async Task CloseAfterReleaseAsync()
        {
            await gate!.HoldAsync(CancellationToken.None);
            try { writer.Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { }
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose()
        {
            gate?.Release();
            writer.Dispose();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            process.Dispose();
        }
    }
    private sealed class SignaledInput(Stream inner, RevisionIngestionGate gate) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); gate.Entered.TrySetResult(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(buffer, cancellationToken); gate.Entered.TrySetResult(); }
    }
}
