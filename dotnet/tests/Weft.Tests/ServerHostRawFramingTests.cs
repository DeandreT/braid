using System.Text;

namespace Weft.Tests;

public sealed class ServerHostRawFramingTests
{
    private const string EmptyRefusal = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nCache-Control: no-store\r\n\r\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1024)]
    public async Task CompleteEmptyRefusalStopsAtItsFrameWithoutReadingEof(int fragmentSize)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\ncOnTeNt-LeNgTh: \t0\t\r\nCache-Control: no-store\r\nX-Owned: first\r\nX-Owned: second\r\n\r\n");
        using var stream = new FragmentedResponse(bytes, fragmentSize, failAtEnd: true);
        var reply = await ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None);
        ServerHostCheck.That(reply.Status == 403 && reply.Bytes.Length == 0 && stream.Position == bytes.Length
            && reply.Headers["Content-Length"] is ["0"] && reply.Headers["Cache-Control"] is ["no-store"]
            && reply.Headers["X-Owned"] is ["first", "second"],
            "The fragmented fixed refusal lost actual headers, consumed beyond its frame, or changed its empty body.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r")]
    public async Task EofBeforeTheHeaderBoundaryIsNotACompleteRefusal(string wire)
    {
        using var stream = new FragmentedResponse(Encoding.ASCII.GetBytes(wire), 1);
        await Assert.ThrowsAsync<ServerHostCaseFailure>(() => ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFailureBeforeTheHeaderBoundaryIsNotReinterpretedAsEofOrARefusal()
    {
        using var stream = new FragmentedResponse(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n"), 3, failAtEnd: true);
        await Assert.ThrowsAsync<IOException>(() => ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.0 403 Forbidden\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 403\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\ncOnTeNt-LeNgTh: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: +0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 00\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0, 0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length:\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 9999999999999999999999\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\ntRaNsFeR-EnCoDiNg: chunked\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nTransfer-Encoding:\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length : 0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n: empty-name\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nMissing-Colon\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n Folded: value\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: value\nOther: value\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: value\rOther: value\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: \0\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: \u007f\r\n\r\n")]
    [InlineData("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: \u00e9\r\n\r\n")]
    public async Task MalformedAmbiguousOrNonapplicationResponsesCannotSatisfyTheRefusalOracle(string wire)
    {
        using var stream = new FragmentedResponse(Encoding.UTF8.GetBytes(wire), 3, failAtEnd: true);
        await Assert.ThrowsAsync<ServerHostCaseFailure>(() => ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\r\n")]
    [InlineData(EmptyRefusal)]
    public async Task AlreadyBufferedBytesBeyondTheEmptyFrameAreRejected(string suffix)
    {
        using var stream = new FragmentedResponse(Encoding.ASCII.GetBytes(EmptyRefusal + suffix), 1024, failAtEnd: true);
        await Assert.ThrowsAsync<ServerHostCaseFailure>(() => ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(65_536)]
    [InlineData(65_537)]
    public async Task TheExistingResponseByteBoundIncludesTheEntireHeaderBoundary(int length)
    {
        const string prefix = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nX-Owned: ";
        const string suffix = "\r\n\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(prefix + new string('x', length - prefix.Length - suffix.Length) + suffix);
        using var stream = new FragmentedResponse(bytes, 1024, failAtEnd: true);
        if (length > 65_536)
            await Assert.ThrowsAsync<ServerHostCaseFailure>(() => ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None));
        else
        {
            var reply = await ServerHostRaw.ReadRefusalAsync(stream, CancellationToken.None);
            ServerHostCheck.That(reply.Status == 403 && reply.Bytes.Length == 0 && stream.Position == length,
                "The exact existing raw-response bound did not retain a complete empty refusal.");
        }
    }

    private sealed class FragmentedResponse(byte[] bytes, int fragmentSize, bool failAtEnd = false) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position == Length && failAtEnd)
                return ValueTask.FromException<int>(new IOException("The owned fragmented response forbids a read past its supplied bytes."));
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
        }
    }
}
