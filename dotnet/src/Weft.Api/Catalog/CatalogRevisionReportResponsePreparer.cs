using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Api.Catalog;

internal static class CatalogRevisionReportResponsePreparer
{
    internal const int MaximumPreparationJsonDepth = 65;
    internal const int MaximumErrorBytes = 1024;

    internal static CatalogRevisionReportPreparedResponse Prepare(CatalogRevisionReportMappedResponse mapped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        cancellationToken.ThrowIfCancellationRequested();
        if (mapped.Error is not null) return Error(mapped, cancellationToken);
        var limits = mapped.Limits ?? throw Inconsistent();
        if (limits.ByteLimit is < 1 or > RevisionReportPolicy.MaximumResponseByteLimit ||
            limits.ValueLimit is < 1 or > RevisionReportPolicy.MaximumResponseItemLimit ||
            limits.ContainerDepthLimit is < 1 or > RevisionReportPolicy.MaximumJsonDepthLimit) throw Inconsistent();
        try { return Admit(mapped, limits, cancellationToken); }
        catch (ResponseLimitException)
        {
            var replacement = mapped.WithoutPayloadForResourceLimit(cancellationToken);
            try { return Admit(replacement, limits, cancellationToken); }
            catch (ResponseLimitException)
            {
                return Error(CatalogRevisionReportMappedResponse.ForError(503, CatalogErrorCodeV1.RevisionReportResourceLimit), cancellationToken);
            }
        }
    }

    private static CatalogRevisionReportPreparedResponse Admit(CatalogRevisionReportMappedResponse mapped,
        CatalogRevisionReportResponseLimits limits, CancellationToken token)
    {
        var bytes = Serialize(mapped, limits.ByteLimit, token);
        Values(bytes, limits, token);
        return CatalogRevisionReportPreparedResponse.Create(mapped.StatusCode, mapped.Location, mapped.RetryAfterSeconds, bytes, token);
    }

    private static CatalogRevisionReportPreparedResponse Error(CatalogRevisionReportMappedResponse mapped, CancellationToken token)
    {
        if (mapped.Error is null) throw Inconsistent();
        var bytes = Serialize(mapped, MaximumErrorBytes, token);
        return CatalogRevisionReportPreparedResponse.Create(mapped.StatusCode, null, null, bytes, token);
    }

    private static byte[] Serialize(CatalogRevisionReportMappedResponse mapped, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var options = ProjectionJson.CreateOptions();
        options.MaxDepth = MaximumPreparationJsonDepth;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        using var stream = new BoundedResponseStream(maximum, token);
        try
        {
            if (mapped.Projection is { } projection) JsonSerializer.Serialize(stream, projection, options);
            else if (mapped.Validation is { } validation) JsonSerializer.Serialize(stream, validation, options);
            else if (mapped.Error is { } error) JsonSerializer.Serialize(stream, error, options);
            else throw Inconsistent();
            token.ThrowIfCancellationRequested();
            return stream.CopyBytes();
        }
        catch (JsonException) { throw Inconsistent(); }
        catch (NotSupportedException) { throw Inconsistent(); }
    }

    private static void Values(byte[] bytes, CatalogRevisionReportResponseLimits limits, CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumPreparationJsonDepth });
        var count = 0;
        try
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray) continue;
                var depth = reader.CurrentDepth + (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray ? 1 : 0);
                if (++count > limits.ValueLimit || depth > limits.ContainerDepthLimit) throw new ResponseLimitException();
            }
            token.ThrowIfCancellationRequested();
        }
        catch (JsonException) { throw Inconsistent(); }
    }

    private static InvalidOperationException Inconsistent() => new("The prepared report response is inconsistent.");
    private sealed class ResponseLimitException : Exception { }

    private sealed class BoundedResponseStream(int maximum, CancellationToken token) : Stream
    {
        private readonly MemoryStream _buffer = new(Math.Min(maximum, 4096));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

        internal byte[] CopyBytes()
        {
            token.ThrowIfCancellationRequested();
            var bytes = new byte[checked((int)_buffer.Length)];
            var source = _buffer.GetBuffer();
            for (var offset = 0; offset < bytes.Length; offset += 4096)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(4096, bytes.Length - offset);
                source.AsSpan(offset, count).CopyTo(bytes.AsSpan(offset, count));
            }
            token.ThrowIfCancellationRequested();
            return bytes;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.Length > maximum - _buffer.Length) throw new ResponseLimitException();
            _buffer.Write(buffer);
            token.ThrowIfCancellationRequested();
        }
        public override void WriteByte(byte value)
        {
            Span<byte> one = stackalloc byte[1];
            one[0] = value;
            Write(one);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _buffer.Dispose();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
