using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Braid.Application;

namespace Braid.Storage.Blob;

/// <summary>
/// An exclusive gate: a finite lease on one exact blob, renewed while held.
/// A lost renewal or a stale holder cannot authorize a later critical step, and process
/// death releases the gate when its lease expires. A held lease is not a shared lock.
/// </summary>
internal sealed class BlobGate : IAsyncDisposable
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(10);
    private readonly BlobLeaseClient _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _state = new();
    private long _validUntil;
    private bool _lost;
    private Task? _renewal;

    private BlobGate(BlobLeaseClient lease, long validUntil)
    {
        _lease = lease;
        _validUntil = validUntil;
    }

    internal string LeaseId => _lease.LeaseId;

    /// <summary>Returns null when another holder's lease is active; a missing gate is unsafe storage.</summary>
    internal static async Task<BlobGate?> TryAcquireAsync(BlobClient gate, CancellationToken cancellationToken)
    {
        var lease = gate.GetBlobLeaseClient(Guid.NewGuid().ToString("D"));
        var started = Stopwatch.GetTimestamp();
        try
        {
            await lease.AcquireAsync(LeaseDuration, conditions: null, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException error) when (error.Status == 409) { return null; }
        catch (RequestFailedException error) when (error.Status == 404)
        { throw new PrivateObjectStorageException(PrivateObjectStorageError.UnsafeStorage); }
        var result = new BlobGate(lease, started + Ticks(LeaseDuration));
        result._renewal = result.RenewLoopAsync();
        return result;
    }

    /// <summary>Requires a lease that was confirmed recently enough to outlast the next bounded step.</summary>
    internal void Validate()
    {
        lock (_state)
            if (_lost || Stopwatch.GetTimestamp() >= _validUntil - Ticks(SafetyMargin))
                throw new PrivateObjectStorageException(PrivateObjectStorageError.Io);
    }

    /// <summary>Renews now, proving the same lease is still held on the same gate blob.</summary>
    internal async Task RenewAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _lease.RenewAsync(conditions: null, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            lock (_state) _lost = true;
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Io);
        }
        lock (_state)
            if (!_lost) _validUntil = Math.Max(_validUntil, started + Ticks(LeaseDuration));
        Validate();
    }

    private async Task RenewLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewalInterval, _stop.Token).ConfigureAwait(false);
                try { await RenewAsync(_stop.Token).ConfigureAwait(false); }
                catch (PrivateObjectStorageException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lock (_state) _lost = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_renewal is not null)
            await _renewal.ConfigureAwait(false);
        bool lost;
        lock (_state) lost = _lost;
        if (!lost)
        {
            // Releasing is an optimization; an unreleased lease expires on its own.
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _lease.ReleaseAsync(conditions: null, release.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is RequestFailedException or OperationCanceledException) { }
        }
        _stop.Dispose();
    }

    private static long Ticks(TimeSpan value) => (long)(value.TotalSeconds * Stopwatch.Frequency);
}
