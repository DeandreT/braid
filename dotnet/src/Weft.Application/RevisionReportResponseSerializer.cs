using System.Text.Json;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Serializes a trusted response with exact contract converters and a bounded owned buffer.</summary>
public static class RevisionReportResponseSerializer
{
    public static byte[] Serialize<T>(T trustedResponse, int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedResponse);
        if (maximumBytes is < 1 or > RevisionReportPolicy.MaximumResponseByteLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new BoundedWriteStream(maximumBytes, cancellationToken);
        try
        {
            JsonSerializer.Serialize(stream, trustedResponse, CreateOptions());
            cancellationToken.ThrowIfCancellationRequested();
            return stream.CopyBytes();
        }
        catch (JsonException)
        {
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        }
        catch (NotSupportedException)
        {
            throw new RevisionReportValidationException(RevisionReportValidationFailure.Integrity);
        }
    }

    internal static JsonSerializerOptions CreateOptions(int maximumDepth = RevisionReportPolicy.MaximumJsonDepthLimit)
    {
        var options = ProjectionJson.CreateOptions();
        options.MaxDepth = maximumDepth;
        return options;
    }

    internal sealed class BoundedWriteStream(int maximumBytes, CancellationToken cancellationToken) : Stream
    {
        private readonly MemoryStream buffer = new(Math.Min(maximumBytes, 4096));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => buffer.Length;
        public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }

        internal byte[] CopyBytes()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = new byte[checked((int)buffer.Length)];
            var source = buffer.GetBuffer();
            for (var offset = 0; offset < owned.Length; offset += 4096)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(4096, owned.Length - offset);
                source.AsSpan(offset, count).CopyTo(owned.AsSpan(offset, count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return owned;
        }

        public override void Write(byte[] source, int offset, int count) => Write(source.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length > maximumBytes - buffer.Length)
                throw new RevisionReportValidationException(RevisionReportValidationFailure.ResourceLimit);
            buffer.Write(source);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public override void WriteByte(byte value)
        {
            Span<byte> one = stackalloc byte[1];
            one[0] = value;
            Write(one);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Write(source.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] source, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Write(source, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() => cancellationToken.ThrowIfCancellationRequested();
        public override Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) buffer.Dispose();
            base.Dispose(disposing);
        }

        public override int Read(byte[] source, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
