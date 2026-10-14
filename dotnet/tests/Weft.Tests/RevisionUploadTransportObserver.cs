using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Weft.Tests;

/// <summary>Counts actual encoded output without retaining bytes; HTTP connection-pipeline completion bounds the observation.</summary>
internal sealed class RevisionUploadTransportObserver
{
    private const int MaximumConnections = 8;
    private readonly object synchronization = new();
    private readonly Dictionary<int, TaskCompletionSource<ObservedConnection>> connections = new();

    internal void Configure(ListenOptions listener) => listener.Use(next => async connection =>
    {
        if (connection.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address))
            throw new InvalidOperationException("Transport observation requires the actual loopback peer.");
        var selected = Slot(peer.Port);
        var observed = new ObservedConnection();
        var actual = connection.Transport;
        connection.Transport = new CountedTransport(actual, observed);
        try
        {
            if (!selected.TrySetResult(observed))
                throw new InvalidOperationException("The observed connection peer must be unique.");
            await next(connection);
        }
        finally
        {
            connection.Transport = actual;
            observed.PipelineSettled.TrySetResult();
        }
    });

    internal Task<ObservedConnection> SelectAsync(int peerPort, CancellationToken cancellationToken) =>
        Slot(peerPort).Task.WaitAsync(cancellationToken);

    private TaskCompletionSource<ObservedConnection> Slot(int peerPort)
    {
        if (peerPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(peerPort));
        lock (synchronization)
        {
            if (connections.TryGetValue(peerPort, out var existing)) return existing;
            if (connections.Count >= MaximumConnections)
                throw new InvalidOperationException("Transport connection observation exceeded its bound.");
            var created = new TaskCompletionSource<ObservedConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            connections.Add(peerPort, created);
            return created;
        }
    }

    internal sealed class ObservedConnection
    {
        private long encodedBytes;
        internal long EncodedBytes => Interlocked.Read(ref encodedBytes);
        internal TaskCompletionSource PipelineSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void RecordAdvance(int count) => Interlocked.Add(ref encodedBytes, count);
    }

    private sealed class CountedTransport : IDuplexPipe
    {
        public PipeReader Input { get; }
        public PipeWriter Output { get; }
        internal CountedTransport(IDuplexPipe actual, ObservedConnection observation)
        {
            Input = actual.Input;
            Output = new CountedOutput(actual.Output, observation);
        }
    }

    // Forward the actual pipe directly. A successful Advance counts encoded
    // application output; it does not claim that TCP delivered those bytes.
    // There is no input interception, payload copy, substituted token or delay.
    private sealed class CountedOutput(PipeWriter actual, ObservedConnection observation) : PipeWriter
    {
        public override bool CanGetUnflushedBytes => actual.CanGetUnflushedBytes;
        public override long UnflushedBytes => actual.UnflushedBytes;
        public override void Advance(int bytes)
        {
            actual.Advance(bytes);
            observation.RecordAdvance(bytes);
        }
        public override Memory<byte> GetMemory(int sizeHint = 0) => actual.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => actual.GetSpan(sizeHint);
        public override void CancelPendingFlush() => actual.CancelPendingFlush();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => actual.FlushAsync(cancellationToken);
        public override void Complete(Exception? exception = null) => actual.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => actual.CompleteAsync(exception);
    }
}
