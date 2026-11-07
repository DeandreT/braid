using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Weft.Api.Chat;
using Weft.Contracts.Chat;

namespace Weft.Tests;

public sealed class ChatEmptyBodyTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(null, "chunked")]
    public async Task Empty_decoded_bodies_are_accepted_without_rewriting_framing(long? length, string? transfer)
    {
        var context = Context();
        context.Request.ContentLength = length;
        if (transfer is not null) context.Request.Headers.TransferEncoding = transfer;
        await ChatRequestParser.RequireNoBodyAsync(context.Request, default);
        Assert.Equal(length, context.Request.ContentLength);
        Assert.Equal(transfer ?? "", context.Request.Headers.TransferEncoding.ToString());
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
    }

    [Theory]
    [InlineData("Content-Length", "1")]
    [InlineData("Content-Type", "application/json")]
    [InlineData("Content-Encoding", "gzip")]
    [InlineData("Content-Encoding", "\u00a0identity")]
    [InlineData("Transfer-Encoding", "\u00a0chunked")]
    [InlineData("Transfer-Encoding", "gzip, chunked")]
    [InlineData("Transfer-Encoding", "chunked, chunked")]
    [InlineData("Trailer", "X-Extra")]
    [InlineData("X-Content-Length", "0")]
    public async Task Unsupported_headers_refuse_before_body_reads(string name, string value)
    {
        var context = Context();
        context.Request.Headers[name] = value;
        context.Request.Body = new UnreadableStream();
        await InvalidAsync(context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task The_decoder_is_checked_even_when_the_header_claims_zero(long? length)
    {
        var context = Context();
        context.Request.ContentLength = length;
        context.Request.Body = new MemoryStream([42, 43]);
        await InvalidAsync(context);
        Assert.Equal(1, context.Request.Body.Position);
    }

    [Fact]
    public async Task Only_HTTP_optional_whitespace_surrounds_identity_coding()
    {
        var context = Context();
        context.Request.Headers.ContentEncoding = " \tIdEnTiTy\t ";
        await ChatRequestParser.RequireNoBodyAsync(context.Request, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Trailers_must_be_available_and_empty_after_EOF(bool available)
    {
        var context = Context();
        var trailers = new TrailerFeature { Available = available };
        if (available) trailers.Trailers["X-Undeclared"] = "refused";
        context.Features.Set<IHttpRequestTrailersFeature>(trailers);
        await InvalidAsync(context);
    }

    [Fact]
    public async Task A_supported_empty_trailer_collection_is_accepted()
    {
        var context = Context();
        context.Features.Set<IHttpRequestTrailersFeature>(new TrailerFeature { Available = true });
        await ChatRequestParser.RequireNoBodyAsync(context.Request, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_classified_bodyless_messages_still_require_actual_EOF(bool nonempty)
    {
        var context = Context();
        context.Request.ContentLength = 0;
        context.Features.Set<IHttpRequestTrailersFeature>(new TrailerFeature { Available = false });
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature(false));
        if (nonempty)
        {
            context.Request.Body = new MemoryStream([1]);
            await InvalidAsync(context);
        }
        else await ChatRequestParser.RequireNoBodyAsync(context.Request, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Decoder_or_connection_failures_cannot_become_empty_requests(bool badFrame)
    {
        var context = Context();
        context.Request.Body = new FailingStream(badFrame);
        await InvalidAsync(context);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_relabelled_as_a_successful_empty_body()
    {
        var context = Context();
        context.Request.Body = new WaitingStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatRequestParser.RequireNoBodyAsync(context.Request, cancellation.Token));
    }

    [Fact]
    public async Task A_stalled_decoded_body_has_a_fixed_deadline()
    {
        var context = Context();
        context.Request.Body = new WaitingStream();
        var pending = InvalidAsync(context);
        await pending.WaitAsync(ChatRequestParser.EmptyBodyReadTimeout + TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Invalid_HTTP2_streams_do_not_add_connection_specific_headers()
    {
        var context = Context();
        context.Request.Protocol = "HTTP/2";
        context.Request.Body = new MemoryStream([1]);
        var error = await Assert.ThrowsAsync<ChatRequestException>(() => ChatRequestParser.RequireNoBodyAsync(context.Request, default));
        Assert.Equal(400, error.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream();
        return context;
    }

    private static async Task InvalidAsync(DefaultHttpContext context)
    {
        var error = await Assert.ThrowsAsync<ChatRequestException>(() => ChatRequestParser.RequireNoBodyAsync(context.Request, default));
        Assert.Equal(ChatErrorCodeV1.InvalidRequest, error.Code);
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("close", context.Response.Headers.Connection.ToString());
    }

    private sealed class TrailerFeature : IHttpRequestTrailersFeature
    {
        public bool Available { get; init; }
        public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
    }

    private sealed class BodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Unexpected body read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected body read.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream(bool badFrame) : UnreadableStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(badFrame ? new BadHttpRequestException("Synthetic bad framing.", 400) : new IOException("Synthetic connection failure."));
    }

    private sealed class WaitingStream : UnreadableStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite wait unexpectedly completed.");
        }
    }
}
