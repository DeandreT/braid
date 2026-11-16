using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.Tests;

/// <summary>Authored claims for physical protocol tests only; none establish persisted ownership or eligibility.</summary>
[SupportedOSPlatform("linux")]
internal static class PrivateStorageV2Data
{
    internal static PrivatePublicationOwner Owner(PrivateObjectReference reference)
    {
        var actor = new ActorContext(new PrincipalId(Guid.NewGuid()));
        var now = DateTimeOffset.UtcNow;
        if (reference.Id.Class == PrivateObjectClass.NativeArtifact)
            return PrivatePublicationOwner.ForIngestion(actor, new IngestionClaim(
                new(reference.Id.Scope.OrganizationId, reference.Id.Scope.ProjectId), Guid.NewGuid(), actor.PrincipalId,
                Guid.NewGuid(), Guid.NewGuid(), 1, now.AddSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds)));
        var native = new PrivateObjectReference(PrivateObjectId.Create(reference.Id.Scope, PrivateObjectClass.NativeArtifact),
            Sha256Digest.FromBytes("native fixture"u8), 14);
        var key = new ProjectionCacheKey(new(native.Id.Scope, native, "entry", "dependencies", "physical-fixture"),
            new("physical-fixture", "engine", "worker", "adapter"), "projection.v1", "algorithm.v1", "codec.v1", new());
        return PrivatePublicationOwner.ForReport(actor, new RevisionReportClaim(Guid.NewGuid(), Guid.NewGuid(), key,
            actor.PrincipalId, Guid.NewGuid(), Guid.NewGuid(), 1, now, now.AddSeconds(60), reference.Id));
    }

    internal static ObjectCleanupClaim FinalClaim(PrivateObjectReference reference, PrivatePublicationOwner owner)
    {
        var retired = DateTimeOffset.UtcNow;
        var retirement = new ObjectCleanupRetirement(Guid.NewGuid(), new(reference.Id, CleanupTargetKind.FinalObject),
            owner.OriginFor(reference.Id), reference.Id.Class == PrivateObjectClass.NativeArtifact
                ? ObjectCleanupReason.TerminalNativeStage : ObjectCleanupReason.FailedDerivedCandidate,
            retired.AddHours(-25), retired);
        return Claim(retirement);
    }

    internal static ObjectCleanupClaim TemporaryClaim(PrivatePublicationRecord publication)
    {
        var reservation = publication.Reservation;
        var eligible = reservation.Owner.LeaseExpiresAtUtc;
        var retired = eligible.AddHours(25);
        return Claim(new ObjectCleanupRetirement(Guid.NewGuid(), new(reservation.Key.Object,
                CleanupTargetKind.TrackedTemporary, reservation.PublicationId), reservation.Origin,
            ObjectCleanupReason.AbandonedTrackedTemporary, eligible, retired, publication));
    }

    private static ObjectCleanupClaim Claim(ObjectCleanupRetirement retirement) => new(retirement, Guid.NewGuid(), 1,
        Guid.NewGuid(), Guid.NewGuid(), retirement.RetiredAtUtc, retirement.RetiredAtUtc.AddSeconds(60));

    internal static (Guid Root, Guid Control) Marker(PrivateStorageTestRoot root)
    {
        var lines = File.ReadAllText(root.MarkerPath).Split('\n');
        Assert.Equal(new[] { "braid-private-storage-v2", lines[1], lines[2], "" }, lines);
        return (Guid.ParseExact(lines[1], "N"), Guid.ParseExact(lines[2], "N"));
    }

    internal static string ControlRoot(PrivateStorageTestRoot root) => Path.Combine(root.RootPath,
        ".braid-private-control", Marker(root).Control.ToString("N"));
    internal static string ObjectLeaf(PrivateStorageTestRoot root, PrivateObjectId id) => Path.Combine(ControlRoot(root),
        "objects", id.Class == PrivateObjectClass.NativeArtifact ? "native" : "derived",
        id.Scope.OrganizationId.ToString("N"), id.Scope.ProjectId.ToString("N"),
        id.Scope.MappingId.ToString("N"), id.Scope.RevisionId.ToString("N"), id.ObjectId.ToString("N"));
    internal static string TempLeaf(PrivateStorageTestRoot root, PrivatePublicationKey key) =>
        Path.Combine(ObjectLeaf(root, key.Object), "publications", key.TempId.ToString("N"));
    internal static string TempPath(PrivateStorageTestRoot root, PrivatePublicationKey key) =>
        Path.Combine(Path.GetDirectoryName(root.FinalPath(new(key.Object, Sha256Digest.FromBytes([]), 0)))!,
            $".attempt-{key.TempId:N}.tmp");

    internal static Dictionary<string, byte[]> Files(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.Ordinal);

    // Reconstitutes explicitly synthetic metadata from complete seals authored by
    // an owned synthetic-registry child. This is not recovery of PostgreSQL facts.
    internal static PrivatePublicationRecord SyntheticRecordFromSeals(PrivateStorageTestRoot root,
        PrivateObjectReference reference, bool bound)
    {
        var publications = Path.Combine(ObjectLeaf(root, reference.Id), "publications");
        var leaf = Assert.Single(Directory.GetDirectories(publications));
        var reservation = Assert.IsType<V2ReservationRecord>(V2PublicationRecordCodec.Decode(
            File.ReadAllBytes(Path.Combine(leaf, "reservation"))));
        var context = reservation.Context;
        var row = new PrivatePublicationReservation(reservation.PublicationId,
            new(context.RootId, reference.Id, context.TempId), PrivatePublicationStoreKind.FileSystem, context.Owner,
            reservation.AbsenceRecordDigest, reservation.RegisteredAtUtc);
        if (!bound) return new(row);
        var bytes = File.ReadAllBytes(Path.Combine(leaf, "bound"));
        var localBound = Assert.IsType<V2BoundRecord>(V2PublicationRecordCodec.Decode(bytes));
        var preparedPath = Path.Combine(leaf, "prepared");
        var bindingTime = File.Exists(preparedPath)
            ? Assert.IsType<V2PreparedRecord>(V2PublicationRecordCodec.Decode(File.ReadAllBytes(preparedPath))).BoundAtUtc
            : row.RegisteredAtUtc;
        return new(row, new(context.CreateFileIdentity(localBound.DataFileIdentity), Sha256Digest.FromBytes(bytes), bindingTime));
    }
}

/// <summary>In-memory physical-test registry. Live-owner booleans here are synthetic, never a database oracle.</summary>
internal sealed class PrivateStorageV2PhysicalRegistry : IPrivateObjectPublicationRegistry
{
    private readonly Dictionary<PrivatePublicationKey, PrivatePublicationRecord> records = [];
    internal int ReserveCalls { get; private set; }
    internal int BindCalls { get; private set; }
    internal int GetCalls { get; private set; }
    internal PrivatePublicationKey? LastReserveKey { get; private set; }
    internal bool ThrowAfterReserve { get; init; }
    internal bool ThrowAfterBind { get; init; }
    internal bool RefuseOwner { get; set; }
    internal bool RefuseEveryCall { get; set; }
    internal Action? BeforeReserve { get; init; }
    internal Action? BeforeBind { get; init; }
    internal PrivatePublicationRecord Single => Assert.Single(records.Values);

    public Task<PrivatePublicationReserveResult> ReserveAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, Sha256Digest absenceRecordDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReserveCalls++;
        if (RefuseEveryCall) throw new InvalidOperationException("Unexpected synthetic registry call.");
        LastReserveKey = new(rootId, objectId, tempId);
        BeforeReserve?.Invoke();
        if (RefuseOwner) return Task.FromResult(PrivatePublicationReserveResult.WithoutSnapshot(PrivatePublicationReserveOutcome.OwnerUnavailable));
        var record = new PrivatePublicationRecord(new(Guid.NewGuid(), new(rootId, objectId, tempId), storeKind, owner,
            absenceRecordDigest, DateTimeOffset.UtcNow));
        records.Add(record.Reservation.Key, record);
        if (ThrowAfterReserve) throw new IOException("Synthetic loss after registration.");
        return Task.FromResult(PrivatePublicationReserveResult.ForSnapshot(PrivatePublicationReserveOutcome.Reserved, Snapshot(record)));
    }

    public Task<PrivatePublicationBindResult> BindAsync(PrivatePublicationReservation reservation,
        PrivatePublicationFileIdentity actualIdentity, Sha256Digest boundRecordDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindCalls++;
        if (RefuseEveryCall) throw new InvalidOperationException("Unexpected synthetic registry call.");
        BeforeBind?.Invoke();
        if (RefuseOwner) return Task.FromResult(PrivatePublicationBindResult.WithoutSnapshot(PrivatePublicationBindOutcome.OwnerUnavailable));
        Assert.Equal(reservation, records[reservation.Key].Reservation);
        var record = new PrivatePublicationRecord(reservation, new(actualIdentity, boundRecordDigest, DateTimeOffset.UtcNow));
        records[reservation.Key] = record;
        if (ThrowAfterBind) throw new IOException("Synthetic loss after binding.");
        return Task.FromResult(PrivatePublicationBindResult.ForSnapshot(PrivatePublicationBindOutcome.Bound, Snapshot(record)));
    }

    public Task<PrivatePublicationSnapshot?> GetAsync(PrivatePublicationOwner owner, PrivatePublicationStoreKind storeKind,
        Guid rootId, PrivateObjectId objectId, Guid tempId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCalls++;
        if (RefuseEveryCall) throw new InvalidOperationException("Unexpected synthetic registry call.");
        var record = records[new(rootId, objectId, tempId)];
        Assert.Equal(owner, record.Reservation.Owner);
        Assert.Equal(storeKind, record.Reservation.StoreKind);
        return Task.FromResult<PrivatePublicationSnapshot?>(Snapshot(record));
    }

    private PrivatePublicationSnapshot Snapshot(PrivatePublicationRecord record) =>
        new(record, DateTimeOffset.UtcNow, !RefuseOwner, false, false);
}

internal sealed class PrivateStorageV2Observer(Func<V2StorageObservation, CancellationToken, ValueTask> observe) : IV2StorageObserver
{
    public ValueTask ObserveAsync(V2StorageObservation observation, CancellationToken cancellationToken) => observe(observation, cancellationToken);
}

internal sealed class PrivateStorageV2Boundary(V2StoragePhase phase) : IV2StorageObserver
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<V2StorageObservation> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Release() => release.TrySetResult();
    public async ValueTask ObserveAsync(V2StorageObservation observation, CancellationToken cancellationToken)
    {
        if (observation.Phase != phase) return;
        Entered.TrySetResult(observation);
        await release.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>A separately opened descriptor; no production locking helper participates in this assertion.</summary>
[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageV2IndependentGate : IDisposable
{
    private readonly SafeFileHandle descriptor;
    internal PrivateStorageV2IndependentGate(string path)
    {
        descriptor = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (Flock(descriptor, 2 | 4) != 0)
        {
            descriptor.Dispose();
            throw new IOException("The independent test descriptor could not take its exclusive gate.");
        }
    }
    public void Dispose() => descriptor.Dispose();
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle descriptor, int operation);
}

[SupportedOSPlatform("linux")]
internal static class PrivateStorageV2Identity
{
    internal static PrivatePublicationInodeIdentity Read(string path)
    {
        if (Statx(-100, path, 0x100, 0x17ff, out var value) != 0)
            throw new IOException("Could not inspect an owned physical test identity.");
        return new(value.Inode, value.DeviceMajor, value.DeviceMinor, value.MountId);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
        [FieldOffset(144)] internal ulong MountId;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxData value);
}

[SupportedOSPlatform("linux")]
internal sealed class PrivateStorageV2Probe : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(180));
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly string privateRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private PrivateStorageV2Probe(Process process, string privateRoot)
    {
        this.process = process;
        this.privateRoot = privateRoot;
        output = CaptureAsync(process.StandardOutput, 4096, deadline.Token);
        error = CaptureAsync(process.StandardError, 4096, deadline.Token);
    }

    internal static async Task<PrivateStorageV2Probe> StartAsync(PrivateStorageTestRoot root,
        PrivateObjectReference? reference, string operation, PrivatePublicationOwner? owner = null,
        byte[]? bytes = null, string? gateDirectory = null, string? gateStage = null, string? gateRecordKind = null,
        ObjectCleanupClaim? cleanup = null, string? connectionString = null, bool denyFlockAfterOpen = false)
    {
        var dll = Environment.GetEnvironmentVariable("BRAID_STORAGE_PROTOCOL_PROBE_PATH") ??
            Path.Combine(AppContext.BaseDirectory, "StorageProtocolProbe", "Braid.StorageProtocolProbe.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException("The protocol probe must be copied to the test output.");
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host)) host = Environment.ProcessPath is { } current && Path.GetFileName(current) == "dotnet" ? current : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned protocol probe did not start.");
        var probe = new PrivateStorageV2Probe(process, root.RootPath);
        try
        {
            var command = new
            {
                Operation = operation,
                RootPath = root.RootPath,
                ApplicationContentRoot = root.ApplicationRoot,
                RegistryMode = connectionString is null ? "syntheticPhysical" : "postgres",
                ConnectionString = connectionString,
                Reference = reference is null ? null : new
                {
                    reference.Id.Scope.OrganizationId,
                    reference.Id.Scope.ProjectId,
                    reference.Id.Scope.MappingId,
                    reference.Id.Scope.RevisionId,
                    reference.Id.ObjectId,
                    ObjectClass = reference.Id.Class.ToString(),
                    Sha256 = reference.Digest.Value,
                    reference.ByteCount
                },
                Owner = owner is null ? null : OwnerWire(owner),
                Cleanup = cleanup is null ? null : CleanupWire(cleanup),
                Base64Content = bytes is null ? null : Convert.ToBase64String(bytes),
                GateDirectory = gateDirectory,
                GateStage = gateStage,
                GateRecordKind = gateRecordKind,
                DenyFlockAfterOpen = denyFlockAfterOpen
            };
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(command, JsonOptions).AsMemory(), probe.deadline.Token);
            process.StandardInput.Close();
            return probe;
        }
        catch { await probe.DisposeAsync(); throw; }
    }

    private static object OwnerWire(PrivatePublicationOwner owner) => new
    {
        Kind = owner.IngestionClaim is not null ? "ingestion" : "report",
        ActorId = owner.OriginalActor.Value,
        owner.ServerInstanceId,
        owner.LeaseToken,
        owner.Generation,
        owner.LeaseExpiresAtUtc,
        Project = owner.IngestionClaim?.Project,
        OperationId = owner.IngestionClaim?.OperationId,
        CacheEntryId = owner.ReportClaim?.CacheEntryId,
        DerivationId = owner.ReportClaim?.DerivationId,
        CandidateObjectId = owner.ReportClaim?.CandidateObjectId.ObjectId,
        StartedAtUtc = owner.ReportClaim?.StartedAtUtc,
        RepairTargetDerivationId = owner.ReportClaim?.RepairTargetDerivationId,
        CanonicalKeyBase64 = owner.ReportClaim is { } report ? Convert.ToBase64String(report.Key.CopyCanonicalUtf8()) : null
    };

    private static object CleanupWire(ObjectCleanupClaim claim)
    {
        var retirement = claim.Retirement;
        var origin = retirement.Origin;
        var publication = retirement.Publication;
        return new
        {
            retirement.RetirementId,
            TargetKind = retirement.Target.Kind.ToString(),
            retirement.Target.PublicationAttemptId,
            Origin = new
            {
                Kind = origin.Kind == PrivateObjectOwnerKind.NativeIngestion ? "ingestion" : "report",
                OperationId = origin.IngestionOperationId,
                origin.CacheEntryId,
                origin.DerivationId,
                origin.Generation
            },
            Reason = retirement.Reason.ToString(),
            retirement.EligibilityOriginAtUtc,
            retirement.RetiredAtUtc,
            Publication = publication is null ? null : new
            {
                publication.Reservation.PublicationId,
                publication.Reservation.Key.RootId,
                publication.Reservation.Key.TempId,
                Owner = OwnerWire(publication.Reservation.Owner),
                AbsenceRecordDigest = publication.Reservation.AbsenceRecordDigest.Value,
                publication.Reservation.RegisteredAtUtc,
                Binding = publication.Binding is null ? null : new
                {
                    publication.Binding.ActualIdentity,
                    BoundRecordDigest = publication.Binding.BoundRecordDigest.Value,
                    publication.Binding.BoundAtUtc
                }
            },
            claim.AttemptId,
            claim.Generation,
            claim.ServerInstanceId,
            claim.LeaseToken,
            claim.StartedAtUtc,
            claim.LeaseExpiresAtUtc
        };
    }

    internal async Task WaitReadyAsync(string directory)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = Path.Combine(directory, "ready");
        while (!File.Exists(ready))
        {
            if (process.HasExited)
            {
                var response = await CompleteAsync();
                throw new InvalidOperationException($"The owned protocol probe exited before readiness ({response.Status}/{response.Error}).");
            }
            await Task.Delay(20, wait.Token);
        }
        Assert.Equal("ready", await File.ReadAllTextAsync(ready, wait.Token));
        Assert.False(process.HasExited);
    }

    internal static async Task ReleaseAsync(string directory)
    {
        var stage = Path.Combine(directory, $"release-{Guid.NewGuid():N}.stage");
        await File.WriteAllTextAsync(stage, "release");
        File.Move(stage, Path.Combine(directory, "release"), overwrite: false);
    }

    internal async Task<PrivateStorageV2ProbeResult> CompleteAsync()
    {
        await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
        var stdout = await output;
        Assert.DoesNotContain(privateRoot, stdout, StringComparison.Ordinal);
        Assert.Empty(await error);
        using var json = JsonDocument.Parse(stdout);
        var value = json.RootElement;
        return new(process.ExitCode, value.GetProperty("status").GetString()!,
            value.TryGetProperty("error", out var failure) ? failure.GetString() : null,
            value.GetProperty("sourceReadCalls").GetInt64(), value.GetProperty("sourceBytes").GetInt64(),
            value.GetProperty("registryFactoryResolutions").GetInt32(), value.GetProperty("reserveCalls").GetInt32(),
            value.GetProperty("bindCalls").GetInt32(), value.GetProperty("getCalls").GetInt32(),
            value.GetProperty("kernelFlockDenied").GetBoolean());
    }

    internal async Task KillAsync()
    {
        Assert.False(process.HasExited);
        process.Kill(entireProcessTree: true);
        using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(reap.Token);
        Assert.NotEqual(0, process.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { deadline.Dispose(); process.Dispose(); }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > maximum) throw new InvalidOperationException("The protocol probe exceeded its categorical output bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

internal sealed record PrivateStorageV2ProbeResult(int ExitCode, string Status, string? Error,
    long SourceReadCalls, long SourceBytes, int RegistryFactoryResolutions, int ReserveCalls, int BindCalls, int GetCalls,
    bool KernelFlockDenied);
