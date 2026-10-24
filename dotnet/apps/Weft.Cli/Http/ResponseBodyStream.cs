namespace Weft.Cli.Http;

/// <summary>A body stream cannot outlive either its request deadline or its consumer's cancellation scope.</summary>
internal sealed class ResponseBodyStream(Stream inner, CancellationToken deadline, CancellationToken caller) : Stream
{
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline, caller);
    private int disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try { return await inner.ReadAsync(buffer, read.Token); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ApiResponse.BodyTimeout();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            caller.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested)
                throw ApiResponse.BodyTimeout();
            throw;
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try { lifetime.Cancel(); }
            finally
            {
                try { inner.Dispose(); }
                finally { lifetime.Dispose(); }
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try { lifetime.Cancel(); }
            finally
            {
                try { await inner.DisposeAsync(); }
                finally { lifetime.Dispose(); }
            }
        }
        GC.SuppressFinalize(this);
    }
}
