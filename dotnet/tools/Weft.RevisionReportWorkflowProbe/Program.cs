using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;
using Weft.MappingBackends.Workers;
using Weft.Persistence.Postgres;
using Weft.Storage.FileSystem;

namespace Weft.RevisionReportWorkflowProbe;

/// <summary>Test-only independent composition; credentials never enter requests, output or worker frames.</summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const int MaximumInputBytes = 32768;
    private const int MaximumOutputBytes = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<int> Main(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (args.Length != 0)
                throw new InvalidDataException();
            var command = await ReadCommandAsync(deadline.Token);
            Validate(command);
            return Output(await ExecuteAsync(command, deadline.Token), 0);
        }
        catch (Exception error)
        {
            // Exception text can contain private paths, pool configuration or input.
            return Output(new("ProbeFailure", Error: error is PrivateObjectStorageException storage
                ? storage.Error.ToString() : error.GetType().Name), 2);
        }
    }

    private static int Output(ProbeResponse response, int exitCode)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumOutputBytes)
            return 3;
        Console.WriteLine(json);
        return exitCode;
    }

    private static async Task<ProbeCommand> ReadCommandAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        var bytes = new byte[MaximumInputBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0)
                break;
            count += read;
        }
        if (count is 0 or > MaximumInputBytes)
            throw new InvalidDataException();
        _ = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        if (bytes.AsSpan(0, count).StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new InvalidDataException();
        using (var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 }))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    throw new InvalidDataException();
        }
        return JsonSerializer.Deserialize<ProbeCommand>(bytes.AsSpan(0, count), JsonOptions)
            ?? throw new InvalidDataException();
    }

    private static void Validate(ProbeCommand command)
    {
        if (command.Mode is not ("ensure" or "current" or "retained") ||
            command.Operation is not ("projection" or "validation") ||
            command.OrganizationId == Guid.Empty || command.ProjectId == Guid.Empty ||
            command.MappingId == Guid.Empty || command.RevisionId == Guid.Empty || command.ActorId == Guid.Empty ||
            !Path.IsPathFullyQualified(command.RootPath) || !Path.IsPathFullyQualified(command.ApplicationContentRoot) ||
            string.IsNullOrWhiteSpace(command.WorkerImage))
            throw new InvalidDataException();
        if ((command.Mode == "retained") != (command.DerivationId != Guid.Empty) ||
            command.GateStage is not (null or "beforeFinish" or "afterFinish") ||
            (command.GateDirectory is null) != (command.GateStage is null) ||
            command.Mode != "ensure" && (command.GateStage is not null || command.WorkerStateDirectory is not null) ||
            command.Mode == "ensure" && (command.WorkerStateDirectory is null ||
                !Path.IsPathFullyQualified(command.WorkerStateDirectory) || !Directory.Exists(command.WorkerStateDirectory)))
            throw new InvalidDataException();
        if (command.GateDirectory is not null && (!Path.IsPathFullyQualified(command.GateDirectory) ||
            !Directory.Exists(command.GateDirectory)))
            throw new InvalidDataException();
    }

    private static async Task<ProbeResponse> ExecuteAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        var connection = Environment.GetEnvironmentVariable("WEFT_REPORT_WORKFLOW_READER_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidDataException();
        var writable = command.Mode == "ensure";
        var writeConnection = Environment.GetEnvironmentVariable("WEFT_REPORT_WORKFLOW_DERIVER_CONNECTION_STRING");
        if (writable ? string.IsNullOrWhiteSpace(writeConnection) : writeConnection is not null)
            throw new InvalidDataException();
        var actor = new ActorContext(new PrincipalId(command.ActorId));
        var scope = new RevisionScope(command.OrganizationId, command.ProjectId, command.MappingId, command.RevisionId);
        var operation = command.Operation == "projection" ? RevisionReportOperation.Projection : RevisionReportOperation.Validation;
        await using var readerDatabase = new PostgresRevisionReportDatabase(connection);
        await using var derivationDatabase = writable ? new PostgresRevisionDerivationDatabase(writeConnection!) : null;
        await using var physical = FileSystemPrivateObjectStore.Open(new(command.RootPath, command.ApplicationContentRoot));
        var reader = new PostgresRevisionReportMetadataReader(readerDatabase);
        var gate = new ProbeGate(command.GateDirectory, command.GateStage);
        var store = new ObservedPrivateStore(physical, gate, writable);
        var profile = FerruleRevisionReportBackendFactory.CreateProfile(command.WorkerImage);
        var actualRegistry = FerruleRevisionReportBackendFactory.CreateRegistry(profile, new(), () =>
        {
            gate.RunnerResolutions++;
            if (!writable)
                throw new InvalidOperationException();
            return new ObservedRunner(new DockerInspectionWorkerRunner(new(command.DockerExecutable, command.WorkerImage),
                new OwnedWorkerLauncher(command.WorkerStateDirectory!)), gate);
        });
        var current = new List<RevisionReportRegistration>();
        var stored = new List<RevisionReportStoredFormatRegistration>();
        foreach (var reportOperation in new[] { RevisionReportOperation.Projection, RevisionReportOperation.Validation })
        {
            var registration = actualRegistry.FindCurrent("ferrule", reportOperation) ?? throw new InvalidOperationException();
            stored.Add(new(registration.Format, actualRegistry.FindStored(registration.Format) ?? throw new InvalidOperationException()));
            if (command.Mode != "retained")
                current.Add(new(registration.Profile, registration.Format, registration.Policy, () =>
                {
                    gate.BackendResolutions++;
                    if (!writable)
                        throw new InvalidOperationException();
                    return registration.GetBackend();
                }));
        }
        var registry = new RevisionReportProfileRegistry(current, stored);
        IRevisionReportMetadataStore metadata = writable
            ? new GatedMetadata(new PostgresRevisionDerivationStore(derivationDatabase!), gate)
            : new ReadOnlyMetadata(gate);
        var service = new RevisionReportService(reader, metadata, registry,
            new(RevisionReportPolicy.MaximumNativeByteLimit, RevisionReportPolicy.MaximumStoredObjectByteLimit,
                RevisionReportPolicy.MaximumStoredObjectByteLimit, () => store), Guid.NewGuid());
        RevisionReportReadResult? read;
        string outcome;
        if (writable)
        {
            var ensured = await service.EnsureAsync(actor, scope, operation, cancellationToken: cancellationToken);
            outcome = ensured.Outcome.ToString();
            read = ensured.Read;
        }
        else
        {
            read = command.Mode == "current"
                ? await service.GetCurrentAsync(actor, scope, operation, cancellationToken: cancellationToken)
                : await service.GetRetainedAsync(actor, scope, operation, command.DerivationId, cancellationToken: cancellationToken);
            outcome = read.Outcome.ToString();
        }
        return gate.Response(outcome, read);
    }

    private sealed record ProbeCommand(string Mode, string Operation, string RootPath, string ApplicationContentRoot,
        string WorkerImage, Guid OrganizationId, Guid ProjectId, Guid MappingId, Guid RevisionId, Guid ActorId,
        Guid DerivationId = default, string? GateDirectory = null, string? GateStage = null,
        string? WorkerStateDirectory = null, string DockerExecutable = "/usr/bin/docker");

    private sealed record ProbeResponse(string Outcome, string? State = null, bool HasVerifiedReport = false,
        Guid? DerivationId = null, Guid? CacheEntryId = null, long? Generation = null, string? KeyFingerprint = null,
        Guid? DerivedObjectId = null, string? DerivedDigest = null, long? DerivedByteCount = null,
        string? BackendStatus = null, bool? ProvenanceConfirmed = null, int WorkerCalls = 0, int WorkerCompletions = 0,
        int RunnerResolutions = 0, int BackendResolutions = 0, int NativeReads = 0, int DerivedReads = 0, int PublishCalls = 0,
        int CreatedPublications = 0, int MetadataWrites = 0, string? LastWorkerFailure = null, string? Error = null);

    private sealed class ProbeGate(string? directory, string? selectedStage)
    {
        internal int WorkerCalls { get; set; }
        internal int WorkerCompletions { get; set; }
        internal int RunnerResolutions { get; set; }
        internal int BackendResolutions { get; set; }
        internal int NativeReads { get; set; }
        internal int DerivedReads { get; set; }
        internal int PublishCalls { get; set; }
        internal int CreatedPublications { get; set; }
        internal int MetadataWrites { get; set; }
        internal string? LastWorkerFailure { get; set; }
        internal PrivateObjectReference? Published { get; set; }
        internal PrivateObjectReference? VerifiedReadback { get; set; }

        internal ProbeResponse Response(string outcome, RevisionReportReadResult? read)
        {
            var result = read?.Report?.Metadata;
            return new(outcome, read?.State?.ToString(), read?.Report is not null,
                result?.DerivationId, result?.CacheEntryId, result?.Generation, result?.Key.Fingerprint.Value,
                result?.DerivedObject.Id.ObjectId, result?.DerivedObject.Digest.Value, result?.DerivedObject.ByteCount,
                result?.Observation.Status.ToString(), result?.Observation.Provenance?.Confirmed,
                WorkerCalls, WorkerCompletions, RunnerResolutions, BackendResolutions, NativeReads, DerivedReads,
                PublishCalls, CreatedPublications, MetadataWrites, LastWorkerFailure);
        }

        internal async Task WaitAsync(string stage, RevisionReportClaim claim, RevisionReportCompletion completion,
            CancellationToken cancellationToken)
        {
            if (stage != selectedStage || directory is null)
                return;
            if (!completion.IsUsable || completion.Key != claim.Key || completion.DerivedObject is not { } candidate ||
                candidate.Id != claim.CandidateObjectId ||
                candidate != Published || candidate != VerifiedReadback || CreatedPublications != 1 ||
                WorkerCalls != 1 || WorkerCompletions != 1 || LastWorkerFailure != "None")
                throw new InvalidOperationException();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Stage = stage,
                claim.DerivationId,
                claim.CacheEntryId,
                claim.Generation,
                claim.StartedAtUtc,
                claim.LeaseExpiresAtUtc,
                KeyFingerprint = claim.Key.Fingerprint.Value,
                DerivedObjectId = candidate.Id.ObjectId,
                DerivedDigest = candidate.Digest.Value,
                DerivedByteCount = candidate.ByteCount,
                BackendStatus = completion.Observation?.Status.ToString(),
                ProvenanceConfirmed = completion.Observation?.Provenance?.Confirmed,
                WorkerCalls,
                WorkerCompletions,
                NativeReads,
                DerivedReads,
                PublishCalls,
                CreatedPublications,
                MetadataWrites
            }, JsonOptions);
            if (bytes.Length > MaximumOutputBytes)
                throw new InvalidOperationException();
            var temporary = Path.Combine(directory, $".ready-{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            // Closed staging file, same directory, no replacement of an existing checkpoint.
            File.Move(temporary, Path.Combine(directory, "ready"), overwrite: false);
            while (!File.Exists(Path.Combine(directory, "release")))
                await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class ObservedPrivateStore(IPrivateObjectStore actual, ProbeGate gate, bool writable) : IPrivateObjectStore
    {
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope scope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            gate.PublishCalls++;
            if (!writable || reference.Id.Class != PrivateObjectClass.DerivedArtifact)
                throw new InvalidOperationException();
            var result = await actual.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
            if (result == PrivateObjectPublishResult.Created)
            {
                gate.CreatedPublications++;
                gate.Published = reference;
            }
            return result;
        }

        public async Task<byte[]> ReadVerifiedAsync(RevisionScope scope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            if (reference.Id.Class == PrivateObjectClass.NativeArtifact)
            {
                gate.NativeReads++;
                if (!writable)
                    throw new InvalidOperationException();
            }
            else
                gate.DerivedReads++;
            var result = await actual.ReadVerifiedAsync(scope, reference, cancellationToken);
            if (reference.Id.Class == PrivateObjectClass.DerivedArtifact)
                gate.VerifiedReadback = reference;
            return result;
        }
    }

    private sealed class GatedMetadata(IRevisionReportMetadataStore actual, ProbeGate gate) : IRevisionReportMetadataStore
    {
        public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
            Guid instance, CancellationToken cancellationToken = default)
        {
            gate.MetadataWrites++;
            return actual.TryClaimAsync(actor, key, instance, cancellationToken);
        }
        public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
            Guid instance, CancellationToken cancellationToken = default)
        {
            gate.MetadataWrites++;
            return actual.RepairAsync(actor, target, instance, cancellationToken);
        }
        public async Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
            RevisionReportCompletion completion, CancellationToken cancellationToken = default)
        {
            // Service has independently verified the real readback before it reaches this public port.
            await gate.WaitAsync("beforeFinish", claim, completion, cancellationToken);
            gate.MetadataWrites++;
            var result = await actual.FinishAsync(actor, claim, completion, cancellationToken);
            if (result.Outcome == RevisionReportStoreOutcome.Applied && result.Snapshot?.SelectedResult is { } selected &&
                selected.DerivationId == claim.DerivationId && selected.DerivedObject == completion.DerivedObject)
                await gate.WaitAsync("afterFinish", claim, completion, cancellationToken);
            return result;
        }
    }

    private sealed class ReadOnlyMetadata(ProbeGate gate) : IRevisionReportMetadataStore
    {
        private Task<RevisionReportStoreResult> Refuse()
        {
            gate.MetadataWrites++;
            throw new InvalidOperationException();
        }
        public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
            Guid instance, CancellationToken cancellationToken = default) => Refuse();
        public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
            Guid instance, CancellationToken cancellationToken = default) => Refuse();
        public Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
            RevisionReportCompletion completion, CancellationToken cancellationToken = default) => Refuse();
    }

    private sealed class ObservedRunner(IInspectionWorkerRunner actual, ProbeGate gate) : IInspectionWorkerRunner
    {
        public string BuildIdentity => actual.BuildIdentity;
        public async Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            gate.WorkerCalls++;
            var result = await actual.RunAsync(frame, cancellationToken);
            // RunAsync has settled all actual owned cleanup before any process-death checkpoint.
            gate.WorkerCompletions++;
            gate.LastWorkerFailure = result.Failure.ToString();
            return result;
        }
    }

    private sealed class OwnedWorkerLauncher(string directory) : IWorkerProcessLauncher
    {
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var index = Array.IndexOf(arguments, "--name");
            if (index >= 0)
            {
                var name = arguments[index + 1];
                if (name.Length != 45 || !name.StartsWith("weft-inspect-", StringComparison.Ordinal) ||
                    name.AsSpan(13).ContainsAnyExcept("0123456789abcdef"))
                    throw new InvalidOperationException();
                var path = Path.Combine(directory, name);
                using var record = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                record.Write("owned"u8);
            }
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException();
                return new OwnedWorkerProcess(process);
            }
            catch { process.Dispose(); throw; }
        }
    }

    private sealed class OwnedWorkerProcess(Process process) : IWorkerProcess
    {
        private int inputClosed;
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public void CloseStandardInput()
        {
            if (Interlocked.Exchange(ref inputClosed, 1) == 0)
                process.StandardInput.Dispose();
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() { if (!process.HasExited) process.Kill(); }
        public void Dispose()
        {
            try { CloseStandardInput(); }
            finally { process.StandardOutput.Dispose(); process.StandardError.Dispose(); process.Dispose(); }
        }
    }
}
