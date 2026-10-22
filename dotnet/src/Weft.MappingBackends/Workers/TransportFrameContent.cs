using System.Net;

namespace Weft.MappingBackends.Workers;

/// <summary>One exact frame as request content that records whether any frame byte may have reached the peer.</summary>
internal sealed class TransportFrameContent(ReadOnlyMemory<byte> frame) : HttpContent
{
    private int started;
    internal bool Started => Volatile.Read(ref started) != 0;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref started, 1);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = frame.Length;
        return true;
    }
}
