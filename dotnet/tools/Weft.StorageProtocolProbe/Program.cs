using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Storage.FileSystem;

namespace Weft.StorageProtocolProbe;

/// <summary>Independent-process qualification only; private inputs never become diagnostics.</summary>
[SupportedOSPlatform("linux")]
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var state = new ProbeState();
        string status;
        string? error = null;
        long? byteCount = null;
        var exitCode = 0;
        try
        {
            if (args.Length != 0)
                throw new InvalidDataException();
            var command = await ReadCommandAsync(deadline.Token);
            byteCount = await ExecuteAsync(command, state, deadline.Token);
            status = state.Status;
        }
        catch (PrivateObjectStorageException failure)
        {
            status = "Error";
            error = failure.Error.ToString();
            exitCode = 2;
        }
        catch (OperationCanceledException)
        {
            status = "Error";
            error = "Canceled";
            exitCode = 3;
        }
        catch (Exception)
        {
            // Exception messages, connection strings, paths, leases and seal
            // bytes are deliberately excluded from every response.
            status = "Error";
            error = "ProbeFailure";
            exitCode = 3;
        }
        Console.WriteLine(JsonSerializer.Serialize(new ProbeResponse(status, error, byteCount,
            state.SourceReadCalls, state.SourceBytes, state.RegistryFactoryResolutions,
            state.ReserveCalls, state.BindCalls, state.GetCalls, state.KernelFlockDenied), Protocol.JsonOptions));
        return exitCode;
    }

    private static async Task<ProbeCommand> ReadCommandAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        using var bytes = new MemoryStream(64 * 1024);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            if (bytes.Length + count > Protocol.MaximumCommandBytes)
                throw new InvalidDataException();
            bytes.Write(buffer, 0, count);
        }
        var length = checked((int)bytes.Length);
        if (length == 0)
            throw new InvalidDataException();
        var value = bytes.GetBuffer().AsMemory(0, length);
        _ = new UTF8Encoding(false, true).GetCharCount(value.Span);
        if (value.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new InvalidDataException();
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException();
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<ProbeCommand>(value.Span, Protocol.JsonOptions) ?? throw new InvalidDataException();
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            // No command field accepts an array, including private identities.
            throw new InvalidDataException();
        }
    }

    private static async Task<long?> ExecuteAsync(ProbeCommand command, ProbeState state, CancellationToken token)
    {
        if (command.Operation is not ("initialize" or "open" or "publish" or "read" or "retire" or "activate") ||
            command.RegistryMode is not ("syntheticPhysical" or "postgres") ||
            command.DenyFlockAfterOpen && command.Operation is not ("publish" or "read" or "retire"))
            throw new InvalidDataException();
        var activation = command.Operation == "activate";
        if (activation != command.OfflineAcknowledged || activation && command.RegistryMode != "syntheticPhysical")
            throw new InvalidDataException();
        var publication = command.Operation == "publish";
        var retirement = command.Operation == "retire";
        var referenced = command.Operation is "publish" or "read" or "retire";
        if (referenced != (command.Reference is not null) || publication != (command.Owner is not null) ||
            retirement != (command.Cleanup is not null) ||
            !publication && (command.Base64Content is not null || command.PatternLength is not null) ||
            publication && (command.Base64Content is null) == (command.PatternLength is null) ||
            command.PatternLength is < 0 or > Protocol.MaximumSourceBytes)
            throw new InvalidDataException();
        if (command.RegistryMode == "postgres" && publication
            ? string.IsNullOrWhiteSpace(command.ConnectionString) || command.ConnectionString.Length > 16384
            : command.ConnectionString is not null)
            throw new InvalidDataException();

        // Restore every supplied private fact and bound source before opening
        // the filesystem or resolving any database factory.
        var options = new FileSystemStorageOptions(command.RootPath, command.ApplicationContentRoot, command.RepositoryRoot);
        var reference = command.Reference?.Restore();
        var owner = command.Owner?.Restore();
        var cleanup = command.Cleanup?.Restore(reference!.Id);
        var gate = new ProbeGate(command);
        if (activation)
        {
            var result = await FileSystemV2PrivateObjectStore.ActivateAsync(options, command.OfflineAcknowledged, gate, token);
            state.Status = result.Outcome switch
            {
                PrivateStorageV2ActivationOutcome.Activated => "Activated",
                PrivateStorageV2ActivationOutcome.AlreadyActive => "AlreadyActive",
                PrivateStorageV2ActivationOutcome.Busy => "Busy",
                _ => throw new InvalidDataException()
            };
            // The activation component performs no fallible observation after
            // its marker exchange. Lost replies are gated only after it returns.
            if ((result.Outcome is PrivateStorageV2ActivationOutcome.Activated or PrivateStorageV2ActivationOutcome.AlreadyActive) &&
                command.GateStage == "afterActivationReturned")
                await gate.AfterActivationReturnedAsync(token);
            if (command.GateStage is not null && !gate.Reached)
                throw new InvalidDataException();
            return null;
        }
        using var source = publication ? new ProbeContentStream(command.Base64Content is { } encoded
            ? Protocol.Base64(encoded, Protocol.MaximumSourceBytes) : null, command.PatternLength, state, gate, token) : null;
        await using var registry = new ProbeRegistry(command, owner, state);
        await using var store = command.Operation == "initialize"
            ? FileSystemV2PrivateObjectStore.Initialize(options, registry.Resolve, gate)
            : FileSystemV2PrivateObjectStore.Open(options, registry.Resolve, gate);
        if (command.DenyFlockAfterOpen)
            state.KernelFlockDenied = ProbeKernelFaults.DenyFlock();

        long? count = null;
        switch (command.Operation)
        {
            case "initialize":
                state.Status = "Initialized";
                break;
            case "open":
                state.Status = "Opened";
                break;
            case "publish":
                var result = await store.PublishCreateOnlyAsync(owner!, reference!.Id.Scope, reference, source!, token);
                state.Status = result switch
                {
                    PrivateObjectPublishResult.Created => "Created",
                    PrivateObjectPublishResult.AlreadyPresent => "AlreadyPresent",
                    _ => throw new InvalidDataException()
                };
                // This is intentionally outside the component: its final rename
                // is followed by no fallible observation inside Publish.
                if (result == PrivateObjectPublishResult.Created && command.GateStage == "afterRename")
                    await gate.AfterRenameAsync(token);
                count = reference.ByteCount;
                break;
            case "read":
                var bytes = await store.ReadVerifiedAsync(reference!.Id.Scope, reference, token);
                state.Status = "Read";
                count = bytes.LongLength;
                break;
            case "retire":
                var outcome = await store.RetireAsync(cleanup!, token);
                state.Status = outcome switch
                {
                    PrivateObjectRetirementOutcome.Deleted => "Deleted",
                    PrivateObjectRetirementOutcome.Missing => "Missing",
                    PrivateObjectRetirementOutcome.Busy => "Busy",
                    PrivateObjectRetirementOutcome.Unsupported => "Unsupported",
                    PrivateObjectRetirementOutcome.UnsafeStorage => "UnsafeStorage",
                    PrivateObjectRetirementOutcome.Io => "Io",
                    _ => throw new InvalidDataException()
                };
                break;
        }
        if (command.GateStage is not null && !gate.Reached)
            throw new InvalidDataException();
        return count;
    }
}

internal sealed class ProbeState
{
    internal string Status { get; set; } = "Error";
    internal long SourceReadCalls;
    internal long SourceBytes;
    internal int RegistryFactoryResolutions;
    internal int ReserveCalls;
    internal int BindCalls;
    internal int GetCalls;
    internal bool KernelFlockDenied;
}

internal sealed record ProbeResponse(string Status, string? Error, long? ByteCount,
    long SourceReadCalls, long SourceBytes, int RegistryFactoryResolutions, int ReserveCalls, int BindCalls, int GetCalls,
    bool KernelFlockDenied);
