using Microsoft.AspNetCore.Http;

namespace Braid.Api.Catalog;

/// <summary>Nonowning body-read boundary; service, persistence and storage failures never become request-body errors.</summary>
internal sealed class CatalogRevisionRequestStream(Stream source, CancellationToken requestAborted) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            requestAborted.ThrowIfCancellationRequested();
            var read = source.Read(buffer, offset, count);
            requestAborted.ThrowIfCancellationRequested();
            return read;
        }
        catch (IOException exception) { throw Normalize(exception); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            requestAborted.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, cancellationToken);
            requestAborted.ThrowIfCancellationRequested();
            return read;
        }
        catch (IOException exception) { throw Normalize(exception); }
    }

    private CatalogRevisionRequestException Normalize(IOException exception)
    {
        requestAborted.ThrowIfCancellationRequested();
        return exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
            ? CatalogRevisionRequestException.BodyLimit() : CatalogRevisionRequestException.Invalid();
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    // The request owns its body stream. Disposing this wrapper must not close it.
    protected override void Dispose(bool disposing) { }
}
