using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Ingestion;
using Braid.MappingBackends.Workers;
using Braid.Persistence.Postgres;
using Braid.Storage.FileSystem;

namespace Braid.IngestionProbe;

/// <summary>Test-only composition; private server configuration never enters a worker frame.</summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    private const int MaximumCommandBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<int> Main()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        ProbeResponse response;
        int exitCode;
        try
        {
            var command = await ReadCommandAsync(deadline.Token);
            Validate(command);
            response = await ExecuteAsync(command, deadline.Token);
            exitCode = 0;
        }
        catch (Exception error)
        {
            // Never print exception messages: they can contain private pool/path configuration.
            response = new("ProbeFailure", Error: error is PrivateObjectStorageException storage
                ? storage.Error.ToString() : error.GetType().Name);
            exitCode = 2;
        }
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return exitCode;
    }

    private static async Task<ProbeCommand> ReadCommandAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            if (bytes.Length + count > MaximumCommandBytes)
                throw new InvalidDataException();
            bytes.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<ProbeCommand>(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), JsonOptions)
            ?? throw new InvalidDataException();
    }

    private static void Validate(ProbeCommand command)
    {
        if (command.Operation is not ("create" or "resume") ||
            string.IsNullOrWhiteSpace(command.IngestionConnectionString) ||
            string.IsNullOrWhiteSpace(command.ReaderConnectionString) ||
            string.IsNullOrWhiteSpace(command.RootPath) ||
            string.IsNullOrWhiteSpace(command.ApplicationContentRoot) ||
            string.IsNullOrWhiteSpace(command.WorkerImage) ||
            !Path.IsPathFullyQualified(command.WorkerStateDirectory) || !Directory.Exists(command.WorkerStateDirectory) ||
            command.OrganizationId == Guid.Empty || command.ProjectId == Guid.Empty || command.ActorId == Guid.Empty)
            throw new InvalidDataException();
        if (command.Operation == "create" && (command.IdempotencyKey == Guid.Empty || command.Base64Content is null) ||
            command.Operation == "resume" && (command.OperationId == Guid.Empty || command.Base64Content is not null))
            throw new InvalidDataException();
        if (command.GateStage is not (null or "beforeObject" or "afterObject" or "afterValidation" or "afterReady" or "afterCommit") ||
            (command.GateDirectory is null) != (command.GateStage is null))
            throw new InvalidDataException();
        if (command.GateDirectory is not null &&
            (!Path.IsPathFullyQualified(command.GateDirectory) || !Directory.Exists(command.GateDirectory)))
            throw new InvalidDataException();
    }

    private static async Task<ProbeResponse> ExecuteAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        var actor = new ActorContext(new PrincipalId(command.ActorId));
        var project = new ProjectScope(command.OrganizationId, command.ProjectId);
        await using var ingestionDatabase = new PostgresIngestionDatabase(command.IngestionConnectionString);
        await using var readerDatabase = new PostgresDatabase(command.ReaderConnectionString);
        await using var physicalStore = FileSystemPrivateObjectStore.Open(new(command.RootPath, command.ApplicationContentRoot));
        var actualMetadata = new PostgresRevisionIngestionStore(ingestionDatabase);
        var gate = new ProbeGate(command.GateDirectory, command.GateStage, command.OperationId);
        var store = new GatedPrivateStore(physicalStore, gate);
        var runner = new CountedRunner(new DockerInspectionWorkerRunner(
            new(command.DockerExecutable, command.WorkerImage), new OwnedWorkerLauncher(command.WorkerStateDirectory)), gate);
        var qualified = FerruleIngestionAdmissionBackend.CreateRegistration(runner);
        var registration = new IngestionBackendRegistration(qualified.Profile, qualified.NativeByteLimit,
            new GatedBackend(qualified.Backend, gate));
        var service = new RevisionIngestionService(new GatedMetadata(actualMetadata, gate), actualMetadata,
            new PostgresProjectReader(readerDatabase), new(store, IngestionAdmissionPolicy.MaximumNativeByteCount),
            new IngestionBackendRegistry(command.EmptyRegistry ? [] : [registration]));

        RevisionIngestionResult result;
        if (command.Operation == "create")
        {
            var body = Convert.FromBase64String(command.Base64Content!);
            if (body.LongLength > IngestionAdmissionPolicy.MaximumNativeByteCount)
                throw new InvalidDataException();
            using var source = new MemoryStream(body, writable: false);
            result = await service.CreateAsync(actor, project, command.IdempotencyKey,
                new(new(command.Name, command.Description), "ferrule", command.OriginalFilename), source, cancellationToken);
        }
        else
        {
            result = await service.ResumeAsync(actor, project, command.OperationId, cancellationToken);
        }
        var receipt = gate.OperationId == Guid.Empty ? null
            : await actualMetadata.GetReceiptAsync(actor, project, gate.OperationId, cancellationToken);
        return new(result.Outcome.ToString(), receipt?.Phase.ToString(), receipt?.Facts.OperationId,
            receipt?.ClaimCount, gate.WorkerCalls, gate.PublishCalls, gate.ReadCalls);
    }

    private sealed record ProbeCommand(string Operation, string IngestionConnectionString,
        string ReaderConnectionString, string RootPath, string ApplicationContentRoot,
        string WorkerImage, string WorkerStateDirectory, Guid OrganizationId, Guid ProjectId, Guid ActorId,
        Guid IdempotencyKey = default, Guid OperationId = default, string? Base64Content = null,
        string Name = "Actual ingestion process mapping", string Description = "Actual qualified process recovery",
        string? OriginalFilename = null, string DockerExecutable = "/usr/bin/docker",
        string? GateDirectory = null, string? GateStage = null, bool EmptyRegistry = false);

    private sealed record ProbeResponse(string Outcome, string? Phase = null, Guid? OperationId = null,
        int? ClaimCount = null, int WorkerCalls = 0, int PublishCalls = 0, int ReadCalls = 0, string? Error = null);

    private sealed class ProbeGate(string? directory, string? selectedStage, Guid operationId)
    {
        internal Guid OperationId { get; set; } = operationId;
        internal int WorkerCalls { get; set; }
        internal int PublishCalls { get; set; }
        internal int ReadCalls { get; set; }

        internal async Task WaitAsync(string stage, CancellationToken cancellationToken)
        {
            if (stage != selectedStage || directory is null)
                return;
            var temporary = Path.Combine(directory, "ready.tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new
            {
                Stage = stage,
                OperationId,
                WorkerCalls,
                PublishCalls,
                ReadCalls
            }, JsonOptions), cancellationToken);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, Path.Combine(directory, "ready"));
            while (!File.Exists(Path.Combine(directory, "release")))
                await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class CountedRunner(IInspectionWorkerRunner actual, ProbeGate gate) : IInspectionWorkerRunner
    {
        public string BuildIdentity => actual.BuildIdentity;
        public Task<WorkerProcessResult> RunAsync(ReadOnlyMemory<byte> requestFrame, CancellationToken cancellationToken)
        {
            gate.WorkerCalls++;
            return actual.RunAsync(requestFrame, cancellationToken);
        }
    }

    private sealed class OwnedWorkerLauncher(string directory) : IWorkerProcessLauncher
    {
        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToArray();
            var nameIndex = Array.IndexOf(arguments, "--name");
            if (nameIndex >= 0)
            {
                var name = arguments[nameIndex + 1];
                if (!Braid.WorkerProbeSupport.InspectionWorkerName.IsExact(name))
                    throw new InvalidOperationException("The owned worker name was invalid.");
                var record = Path.Combine(directory, name);
                File.WriteAllText(record, "owned");
                File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The owned worker process did not start.");
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

    private sealed class GatedBackend(IIngestionAdmissionBackend actual, ProbeGate gate) : IIngestionAdmissionBackend
    {
        public async Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact,
            CancellationToken cancellationToken = default)
        {
            var result = await actual.ValidateAsync(artifact, cancellationToken);
            // The real runner has completed its owned container cleanup before this gate.
            await gate.WaitAsync("afterValidation", cancellationToken);
            return result;
        }
    }

    private sealed class GatedPrivateStore(IPrivateObjectStore actual, ProbeGate gate) : IPrivateObjectStore
    {
        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync("beforeObject", cancellationToken);
            gate.PublishCalls++;
            var result = await actual.PublishCreateOnlyAsync(expectedScope, reference, source, cancellationToken);
            await gate.WaitAsync("afterObject", cancellationToken);
            return result;
        }

        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            gate.ReadCalls++;
            return actual.ReadVerifiedAsync(expectedScope, reference, cancellationToken);
        }
    }

    private sealed class GatedMetadata(IIngestionMetadataStore actual, ProbeGate gate) : IIngestionMetadataStore
    {
        public async Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project,
            Guid idempotencyKey, IngestionIntent intent, IngestionBackendProfile proposedProfile,
            IngestionAdmissionPolicy proposedPolicy, CancellationToken cancellationToken = default)
        {
            var result = await actual.ReserveAsync(actor, project, idempotencyKey, intent, proposedProfile,
                proposedPolicy, cancellationToken);
            if (result.Receipt is { } receipt)
                gate.OperationId = receipt.Facts.OperationId;
            return result;
        }

        public Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operationId,
            Guid serverInstanceId, CancellationToken cancellationToken = default) =>
            actual.ClaimAsync(actor, project, operationId, serverInstanceId, cancellationToken);

        public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim,
            PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default) =>
            actual.MarkStagedAsync(actor, claim, verifiedNativeObject, cancellationToken);

        public async Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim,
            IngestionAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            var result = await actual.CompleteAttemptAsync(actor, claim, completion, cancellationToken);
            if (result.Outcome == IngestionStoreOutcome.Applied && result.Receipt?.Phase == IngestionPhase.Ready)
                await gate.WaitAsync("afterReady", cancellationToken);
            return result;
        }

        public async Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim,
            CancellationToken cancellationToken = default)
        {
            var result = await actual.FinalizeAsync(actor, claim, cancellationToken);
            if (result.Receipt?.Phase == IngestionPhase.Committed)
                await gate.WaitAsync("afterCommit", cancellationToken);
            return result;
        }

        public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId,
            CancellationToken cancellationToken = default) => actual.GetReceiptAsync(actor, project, operationId, cancellationToken);
    }
}
