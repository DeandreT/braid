using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.MaintenanceProbe;

/// <summary>
/// Test-only external process. Gates wrap public production ports after actual
/// operations; they cannot manufacture claims, publication records or deletion.
/// Credentials are read only from the child's environment.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const int MaximumCommandBytes = 6 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<int> Main()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var command = await ReadCommandAsync(lifetime.Token);
            var project = new ProjectScope(command.OrganizationId, command.ProjectId);
            var options = new FileSystemStorageOptions(command.RootPath, command.ApplicationContentRoot);
            var gate = new Gate(command.GateDirectory, command.GateStage);
            Response response;
            if (command.Operation == "run")
            {
                await using var database = new PostgresPrivateObjectMaintenanceDatabase(Credential("BRAID_MAINTENANCE_PROBE_CONNECTION"));
                var metadata = new PostgresPrivateObjectMaintenanceStore(database);
                // Opening an existing v2 root is explicit. No initialization,
                // activation, writer-registry resolution or worker is possible.
                await using var storage = FileSystemV2PrivateObjectStore.Open(options);
                var gatedMetadata = new GatedMetadata(metadata, gate);
                var gatedRetirement = new GatedRetirement(storage, gate);
                var service = new PrivateObjectMaintenanceService(gatedMetadata, metadata, metadata,
                    storage, gatedRetirement, command.ServerInstanceId);
                var result = await service.RunOnceAsync(project, cancellationToken: lifetime.Token);
                response = new("Run", Outcome: result.Outcome.ToString(), Failure: result.Failure?.ToString(),
                    ClaimedCount: result.ClaimedCount, PhysicalDeleted: result.PhysicalDeleted,
                    PhysicalMissing: result.PhysicalMissing, AcknowledgedDeleted: result.AcknowledgedDeleted,
                    AcknowledgedMissing: result.AcknowledgedMissing,
                    Generations: result.Targets.Select(target => target.Generation).ToArray(),
                    Physical: result.Targets.Select(target => target.Physical.ToString()).ToArray(),
                    Acknowledgements: result.Targets.Select(target => target.Acknowledgement.ToString()).ToArray());
            }
            else if (command.Operation == "publish")
            {
                var actor = new ActorContext(new PrincipalId(command.ActorId));
                await using var database = new PostgresIngestionDatabase(Credential("BRAID_MAINTENANCE_PROBE_INGESTION_CONNECTION"));
                var metadata = new PostgresRevisionIngestionStore(database);
                var receipt = await metadata.GetReceiptAsync(actor, project, command.OperationId, lifetime.Token)
                    ?? throw new InvalidDataException();
                var claimed = await metadata.ClaimAsync(actor, project, command.OperationId, command.ServerInstanceId, lifetime.Token);
                var claim = claimed.Claim ?? throw new InvalidDataException();
                var owner = PrivatePublicationOwner.ForIngestion(actor, claim);
                var registry = new PostgresIngestionPublicationRegistry(database);
                await using var storage = FileSystemV2PrivateObjectStore.Open(options, () => registry);
                var bytes = Convert.FromBase64String(command.Base64Content ?? throw new InvalidDataException());
                if (bytes.Length > FileSystemStorageOptions.NativeHardMaximum ||
                    bytes.LongLength != receipt.Facts.NativeObject.ByteCount ||
                    Sha256Digest.FromBytes(bytes) != receipt.Facts.NativeObject.Digest)
                    throw new InvalidDataException();
                using var source = new GatedSource(bytes, gate);
                var published = await storage.PublishCreateOnlyAsync(owner, receipt.Facts.FutureRevision,
                    receipt.Facts.NativeObject, source, lifetime.Token);
                response = new("Publish", Outcome: published.ToString());
            }
            else throw new InvalidDataException();
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return 0;
        }
        catch (Exception)
        {
            // Exception, connection and filesystem text must never reach IPC logs.
            Console.WriteLine(JsonSerializer.Serialize(new Response("Error", Error: "ProbeFailure"), JsonOptions));
            return 3;
        }
    }

    private static string Credential(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidDataException();

    private static async Task<Command> ReadCommandAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (bytes.Length + count > MaximumCommandBytes) throw new InvalidDataException();
            bytes.Write(buffer, 0, count);
        }
        var command = JsonSerializer.Deserialize<Command>(bytes.ToArray(), JsonOptions) ?? throw new InvalidDataException();
        if (command.ServerInstanceId == Guid.Empty || command.OrganizationId == Guid.Empty || command.ProjectId == Guid.Empty ||
            command.Operation is not ("run" or "publish") ||
            (command.GateDirectory is null) != (command.GateStage is null) ||
            command.GateStage is not (null or "afterClaim" or "afterRetire" or "duringPublication") ||
            (command.Operation == "run" && (command.GateStage == "duringPublication" || command.Base64Content is not null)) ||
            (command.Operation == "publish" && (command.ActorId == Guid.Empty || command.OperationId == Guid.Empty ||
                command.GateStage is not (null or "duringPublication"))))
            throw new InvalidDataException();
        return command;
    }

    private sealed class Gate(string? directory, string? selected)
    {
        private int entered;

        internal async Task HoldAsync(string stage, int claimCount = 0,
            PrivateObjectRetirementOutcome? physical = null, CancellationToken cancellationToken = default)
        {
            if (selected != stage) return;
            if (directory is null || Interlocked.Exchange(ref entered, 1) != 0 || !Directory.Exists(directory) ||
                File.GetUnixFileMode(directory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                throw new InvalidDataException();
            var temporary = Path.Combine(directory, "ready.pending");
            var ready = Path.Combine(directory, "ready");
            var payload = JsonSerializer.Serialize(new Signal(stage, claimCount, physical?.ToString()), JsonOptions);
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(payload), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, ready);
            var release = Path.Combine(directory, "release");
            while (!File.Exists(release)) await Task.Delay(10, cancellationToken);
            if (await File.ReadAllTextAsync(release, cancellationToken) != "release") throw new InvalidDataException();
        }
    }

    private sealed class GatedMetadata(IPrivateObjectMaintenanceMetadata actual, Gate gate) : IPrivateObjectMaintenanceMetadata
    {
        public async Task<ObjectCleanupClaimBatch> ClaimAsync(ProjectScope project, Guid instance,
            ObjectCleanupScanCursor? after = null, CancellationToken cancellationToken = default)
        {
            var committed = await actual.ClaimAsync(project, instance, after, cancellationToken);
            await gate.HoldAsync("afterClaim", committed.Claims.Count, cancellationToken: cancellationToken);
            return committed;
        }

        public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
            CancellationToken cancellationToken = default) => actual.FinishAsync(claim, disposition, cancellationToken);

        public Task<ObjectCleanupFinishResult> FinishAsync(ObjectCleanupClaim claim, ObjectCleanupDisposition disposition,
            CancellationToken cancellationToken, CancellationToken settlementCancellationToken) =>
            actual.FinishAsync(claim, disposition, cancellationToken, settlementCancellationToken);

        public Task<ObjectCleanupAttempt?> GetAttemptAsync(ProjectScope project, Guid retirementId, long generation,
            CancellationToken cancellationToken = default) => actual.GetAttemptAsync(project, retirementId, generation, cancellationToken);
    }

    private sealed class GatedRetirement(IPrivateObjectRetirementStore actual, Gate gate) : IPrivateObjectRetirementStore
    {
        public async Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim,
            CancellationToken cancellationToken = default)
        {
            var physical = await actual.RetireAsync(claim, cancellationToken);
            await gate.HoldAsync("afterRetire", physical: physical, cancellationToken: cancellationToken);
            return physical;
        }
    }

    private sealed class GatedSource(byte[] bytes, Gate gate) : Stream
    {
        private readonly MemoryStream input = new(bytes, writable: false);
        private int entered;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref entered, 1) == 0)
                await gate.HoldAsync("duringPublication", cancellationToken: cancellationToken);
            return await input.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record Command(string Operation, string RootPath, string ApplicationContentRoot,
        Guid OrganizationId, Guid ProjectId, Guid ServerInstanceId, string? GateDirectory = null,
        string? GateStage = null, Guid ActorId = default, Guid OperationId = default, string? Base64Content = null);
    private sealed record Signal(string Stage, int ClaimCount, string? Physical);
    private sealed record Response(string Kind, string? Outcome = null, string? Failure = null, string? Error = null,
        int? ClaimedCount = null, int PhysicalDeleted = 0, int PhysicalMissing = 0, int AcknowledgedDeleted = 0,
        int AcknowledgedMissing = 0, long[]? Generations = null, string[]? Physical = null, string[]? Acknowledgements = null);
}
