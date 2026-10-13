using Weft.Domain;

namespace Weft.Application;

/// <summary>Bounds and owns exact caller bytes before any metadata reservation.</summary>
internal static class IngestionSourceAdmission
{
    private const int BufferBytes = 64 * 1024;

    internal static async Task<IngestionSourceRead> ReadAsync(Stream source, long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The native source must be readable.", nameof(source));
        if (maximumBytes is < 1 or > IngestionAdmissionPolicy.MaximumNativeByteCount)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The native source limit must fit the admission ceiling.");

        cancellationToken.ThrowIfCancellationRequested();
        using var bytes = new MemoryStream((int)Math.Min(maximumBytes, BufferBytes));
        var buffer = new byte[BufferBytes];
        while (true)
        {
            // One extra byte proves overflow without draining an unbounded source.
            var requested = (int)Math.Min(buffer.Length, maximumBytes - bytes.Length + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
                break;
            if (read < 0 || read > requested)
                throw new IOException("The native source returned an invalid byte count.");
            if (bytes.Length + read > maximumBytes)
                return new(null, null, RevisionIngestionRejection.NativeLimit);
            bytes.Write(buffer, 0, read);
        }

        if (bytes.Length == 0)
            return new(null, null, RevisionIngestionRejection.EmptyNative);
        var owned = bytes.ToArray();
        return new(owned, Sha256Digest.FromBytes(owned), null);
    }
}

internal sealed record IngestionSourceRead(byte[]? Bytes, Sha256Digest? Digest,
    RevisionIngestionRejection? Rejection);
