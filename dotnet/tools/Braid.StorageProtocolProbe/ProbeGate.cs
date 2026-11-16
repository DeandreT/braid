using System.Runtime.Versioning;
using System.Text;
using Braid.Storage.FileSystem;

namespace Braid.StorageProtocolProbe;

[SupportedOSPlatform("linux")]
internal sealed class ProbeGate : IV2StorageObserver, IV2ActivationObserver
{
    private readonly string? _directory;
    private readonly string? _stage;
    private readonly V2RecordKind? _kind;
    private int _reached;
    private int _sourceMarked;

    internal ProbeGate(ProbeCommand command)
    {
        _directory = command.GateDirectory;
        _stage = command.GateStage;
        if ((_directory is null) != (_stage is null))
            throw new InvalidDataException();
        if (_stage is null)
        {
            if (command.GateRecordKind is not null)
                throw new InvalidDataException();
            return;
        }
        if (command.Operation == "activate")
        {
            if (command.GateRecordKind is not null || _stage != "afterActivationReturned" &&
                (!Enum.TryParse<V2ActivationPhase>(_stage, out var activationPhase) ||
                 !Enum.IsDefined(activationPhase) || activationPhase.ToString() != _stage))
                throw new InvalidDataException();
        }
        else if (command.Operation is "initialize" or "open" ||
            (_stage is "afterRename" or "beforeSecondSourceRead" ? command.Operation != "publish" :
                !Enum.TryParse<V2StoragePhase>(_stage, out var phase) || !Enum.IsDefined(phase) || phase.ToString() != _stage))
            throw new InvalidDataException();
        if (command.GateRecordKind is not null)
        {
            if (_stage is not ("SealStageWritten" or "SealInstalled") ||
                !Enum.TryParse<V2RecordKind>(command.GateRecordKind, out var kind) ||
                !Enum.IsDefined(kind) || kind.ToString() != command.GateRecordKind)
                throw new InvalidDataException();
            _kind = kind;
        }
        if (_directory is null || !_directory.StartsWith('/') || _directory.EndsWith('/') ||
            _directory.Contains('\0') || _directory.Contains('\\') ||
            _directory.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or "..") ||
            Encoding.UTF8.GetByteCount(_directory) > 4095 || !Directory.Exists(_directory) ||
            Directory.EnumerateFileSystemEntries(_directory).Any())
            throw new InvalidDataException();
    }

    internal bool Reached => Volatile.Read(ref _reached) != 0;

    public async ValueTask ObserveAsync(V2StorageObservation observation, CancellationToken cancellationToken)
    {
        if (observation.Phase.ToString() != _stage || (_kind is { } kind && observation.RecordKind != kind) ||
            Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            return;
        await WaitAsync(cancellationToken);
    }

    public async ValueTask ObserveAsync(V2ActivationObservation observation, CancellationToken cancellationToken)
    {
        if (observation.Phase.ToString() != _stage || Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            return;
        await WaitAsync(cancellationToken);
    }

    internal async Task AfterActivationReturnedAsync(CancellationToken token)
    {
        if (_stage != "afterActivationReturned" || Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            throw new InvalidDataException();
        await WaitAsync(token);
    }

    internal async Task AfterRenameAsync(CancellationToken token)
    {
        if (_stage != "afterRename" || Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            throw new InvalidDataException();
        await WaitAsync(token);
    }

    internal async ValueTask BeforeSecondSourceReadAsync(CancellationToken token)
    {
        if (_stage != "beforeSecondSourceRead" || Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            return;
        // The caller's first read returned, and the component awaited its first
        // output write before asking for this read. This is a source boundary,
        // not a physical flush or a completed-syscall observation.
        await WaitAsync(token);
    }

    internal void SourceReadCompleted(CancellationToken token)
    {
        if (_directory is not null && Interlocked.CompareExchange(ref _sourceMarked, 1, 0) == 0)
            WriteMarker(_directory, "source-read", "read"u8, token);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        WriteMarker(_directory!, "ready", "ready"u8, deadline.Token);
        var releasePath = Path.Combine(_directory!, "release");
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (File.Exists(releasePath))
            {
                // The parent may also use a closed stage. A momentarily empty
                // newly created marker is not accepted as a completed release.
                using var release = new FileStream(releasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (release.Length == 7)
                {
                    var bytes = new byte[7];
                    release.ReadExactly(bytes);
                    if (!bytes.AsSpan().SequenceEqual("release"u8) || release.ReadByte() != -1)
                        throw new InvalidDataException();
                    return;
                }
                if (release.Length != 0)
                    throw new InvalidDataException();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private static void WriteMarker(string directory, string name, ReadOnlySpan<byte> contents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stagePath = Path.Combine(directory, "." + name + "-" + Guid.NewGuid().ToString("N") + ".stage");
        var owned = false;
        try
        {
            using (var stage = new FileStream(stagePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                owned = true;
                stage.Write(contents);
                stage.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            // Only the completed, closed marker becomes observable. File.Move
            // without overwrite refuses an already visible destination.
            File.Move(stagePath, Path.Combine(directory, name), overwrite: false);
            owned = false;
        }
        finally
        {
            if (owned)
                File.Delete(stagePath);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class ProbeContentStream(byte[]? content, int? patternLength, ProbeState state,
    ProbeGate gate, CancellationToken lifetime) : Stream
{
    private readonly int _length = content?.Length ?? patternLength ?? throw new InvalidDataException();
    private int _position;
    private int _nonemptyReadEntries;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lifetime.ThrowIfCancellationRequested();
        if (!buffer.IsEmpty && ++_nonemptyReadEntries == 2)
            gate.BeforeSecondSourceReadAsync(lifetime).GetAwaiter().GetResult();
        return CopyNext(buffer);
    }

    private int CopyNext(Span<byte> buffer)
    {
        lifetime.ThrowIfCancellationRequested();
        var count = Math.Min(buffer.Length, _length - _position);
        if (content is not null)
            content.AsSpan(_position, count).CopyTo(buffer);
        else
            for (var index = 0; index < count; index++)
                buffer[index] = unchecked((byte)((_position + index) * 31 + 7));
        _position += count;
        state.SourceReadCalls++;
        state.SourceBytes += count;
        gate.SourceReadCompleted(lifetime);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        if (!buffer.IsEmpty && ++_nonemptyReadEntries == 2)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            await gate.BeforeSecondSourceReadAsync(linked.Token);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return CopyNext(buffer.Span);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
