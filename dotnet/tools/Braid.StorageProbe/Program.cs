using System.Text.Json;
using System.Text.Json.Serialization;
using Braid.Application;
using Braid.Domain;
using Braid.Storage.FileSystem;

namespace Braid.StorageProbe;

internal static class Program
{
    private const int MaximumCommandBytes = 24 * 1024 * 1024;
    private const int MaximumSourceBytes = 24 * 1024 * 1024;
    private const int MaximumReturnedContentBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<int> Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        ProbeResponse response;
        int exitCode;
        try
        {
            var command = await ReadCommandAsync(timeout.Token);
            ValidateCommand(command);
            response = await ExecuteAsync(command, timeout.Token);
            exitCode = 0;
        }
        catch (PrivateObjectStorageException error)
        {
            response = new ProbeResponse("Error", Error: error.Error.ToString());
            exitCode = 2;
        }
        catch (Exception)
        {
            response = new ProbeResponse("Error", Error: "ProbeFailure");
            exitCode = 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return exitCode;
    }

    private static async Task<ProbeCommand> ReadCommandAsync(CancellationToken cancellationToken)
    {
        using var input = Console.OpenStandardInput();
        using var commandBytes = new MemoryStream(64 * 1024);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            if (commandBytes.Length + count > MaximumCommandBytes + 2L)
                throw new InvalidDataException();
            commandBytes.Write(buffer, 0, count);
        }

        var length = checked((int)commandBytes.Length);
        var bytes = commandBytes.GetBuffer();
        if (length > 0 && bytes[length - 1] == '\n')
        {
            length--;
            if (length > 0 && bytes[length - 1] == '\r')
                length--;
        }
        if (length == 0 || length > MaximumCommandBytes)
            throw new InvalidDataException();
        for (var index = 0; index < length; index++)
        {
            if (bytes[index] is (byte)'\r' or (byte)'\n')
                throw new InvalidDataException();
        }

        return JsonSerializer.Deserialize<ProbeCommand>(bytes.AsSpan(0, length), JsonOptions)
            ?? throw new InvalidDataException();
    }

    private static void ValidateCommand(ProbeCommand command)
    {
        if (command.Operation is not ("publish" or "read") ||
            string.IsNullOrWhiteSpace(command.RootPath) ||
            string.IsNullOrWhiteSpace(command.ApplicationContentRoot) ||
            command.ObjectClass is not ("NativeArtifact" or "DerivedArtifact") ||
            command.Sha256 is null)
            throw new InvalidDataException();
        if (command.GateStage is not (null or "beforeBodyEof" or "afterPublication") ||
            (command.GateDirectory is null) != (command.GateStage is null))
            throw new InvalidDataException();
        if (command.GateDirectory is not null &&
            (!Path.IsPathFullyQualified(command.GateDirectory) || !Directory.Exists(command.GateDirectory)))
            throw new InvalidDataException();

        if (command.Operation == "publish")
        {
            if ((command.Base64Content is null) == (command.PatternLength is null) ||
                command.PatternLength is < 0 or > MaximumSourceBytes)
                throw new InvalidDataException();
        }
        else if (command.Base64Content is not null || command.PatternLength is not null || command.GateStage is not null)
        {
            throw new InvalidDataException();
        }
    }

    private static async Task<ProbeResponse> ExecuteAsync(ProbeCommand command, CancellationToken cancellationToken)
    {
        var scope = new RevisionScope(command.OrganizationId, command.ProjectId, command.MappingId, command.RevisionId);
        var objectClass = command.ObjectClass == "NativeArtifact"
            ? PrivateObjectClass.NativeArtifact
            : PrivateObjectClass.DerivedArtifact;
        var id = PrivateObjectId.Restore(scope, command.ObjectId, objectClass);
        var digest = Sha256Digest.Parse(command.Sha256);
        if (digest.Value != command.Sha256)
            throw new InvalidDataException();
        var reference = new PrivateObjectReference(id, digest, command.ByteCount);
        var options = new FileSystemStorageOptions(command.RootPath, command.ApplicationContentRoot, command.RepositoryRoot);

        using var source = command.Operation == "publish" ? CreateSource(command, cancellationToken) : null;
        using var store = FileSystemPrivateObjectStore.Open(options);
        if (source is not null)
        {
            var result = await store.PublishCreateOnlyAsync(scope, reference, source, cancellationToken);
            var status = result switch
            {
                PrivateObjectPublishResult.Created => "Created",
                PrivateObjectPublishResult.AlreadyPresent => "AlreadyPresent",
                _ => throw new InvalidDataException()
            };
            if (command.GateStage == "afterPublication")
                await WaitForGateAsync(command.GateDirectory!, cancellationToken);
            return new ProbeResponse(status, ByteCount: reference.ByteCount, Sha256: reference.Digest.Value);
        }

        var content = await store.ReadVerifiedAsync(scope, reference, cancellationToken);
        return new ProbeResponse("Read", ByteCount: content.LongLength, Sha256: Sha256Digest.FromBytes(content).Value,
            Base64Content: content.Length <= MaximumReturnedContentBytes ? Convert.ToBase64String(content) : null);
    }

    private static ProbeContentStream CreateSource(ProbeCommand command, CancellationToken cancellationToken)
    {
        var content = command.Base64Content is not null ? Convert.FromBase64String(command.Base64Content) : null;
        if (content is not null && content.Length > MaximumSourceBytes)
            throw new InvalidDataException();
        return new ProbeContentStream(content, content?.Length ?? command.PatternLength!.Value,
            command.GateStage == "beforeBodyEof" ? command.GateDirectory : null, cancellationToken);
    }

    private static async Task WaitForGateAsync(string gateDirectory, CancellationToken cancellationToken)
    {
        using var gateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateTimeout.CancelAfter(TimeSpan.FromSeconds(120));
        var readyPath = Path.Combine(gateDirectory, "ready");
        var stagingPath = Path.Combine(gateDirectory, ".ready-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stagingOwned = false;
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                stagingOwned = true;
                await staging.WriteAsync("ready"u8.ToArray(), gateTimeout.Token);
            }
            gateTimeout.Token.ThrowIfCancellationRequested();
            // The consumer sees only the completed marker in its uniquely owned gate.
            File.Move(stagingPath, readyPath, overwrite: false);
            stagingOwned = false;
        }
        finally
        {
            if (stagingOwned)
                File.Delete(stagingPath);
        }
        var releasePath = Path.Combine(gateDirectory, "release");
        while (!File.Exists(releasePath))
            await Task.Delay(TimeSpan.FromMilliseconds(20), gateTimeout.Token);
        gateTimeout.Token.ThrowIfCancellationRequested();
    }

    internal sealed record ProbeCommand
    {
        public ProbeCommand() { }

        public required string Operation { get; init; }
        public required string RootPath { get; init; }
        public required string ApplicationContentRoot { get; init; }
        public string? RepositoryRoot { get; init; }
        public required Guid OrganizationId { get; init; }
        public required Guid ProjectId { get; init; }
        public required Guid MappingId { get; init; }
        public required Guid RevisionId { get; init; }
        public required Guid ObjectId { get; init; }
        public required string ObjectClass { get; init; }
        public required string Sha256 { get; init; }
        public required long ByteCount { get; init; }
        public string? Base64Content { get; init; }
        public int? PatternLength { get; init; }
        public string? GateDirectory { get; init; }
        public string? GateStage { get; init; }
    }

    internal sealed record ProbeResponse(string Status, string? Error = null, long? ByteCount = null,
        string? Sha256 = null, string? Base64Content = null);

    private sealed class ProbeContentStream(byte[]? content, int contentLength, string? eofGateDirectory,
        CancellationToken lifetimeCancellationToken) : Stream
    {
        private int _position;
        private bool _eofGateReached;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            lifetimeCancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _position < contentLength)
                return CopyNext(buffer);
            WaitAtEndAsync(lifetimeCancellationToken).GetAwaiter().GetResult();
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeCancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty || _position < contentLength)
                return CopyNext(buffer.Span);
            await WaitAtEndAsync(cancellationToken);
            return 0;
        }

        private int CopyNext(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, contentLength - _position);
            if (content is not null)
            {
                content.AsSpan(_position, count).CopyTo(buffer);
            }
            else
            {
                for (var index = 0; index < count; index++)
                    buffer[index] = unchecked((byte)((_position + index) * 31 + 7));
            }
            _position += count;
            return count;
        }

        private async Task WaitAtEndAsync(CancellationToken cancellationToken)
        {
            if (eofGateDirectory is null || _eofGateReached)
                return;
            _eofGateReached = true;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellationToken, cancellationToken);
            await WaitForGateAsync(eofGateDirectory, linkedCancellation.Token);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
