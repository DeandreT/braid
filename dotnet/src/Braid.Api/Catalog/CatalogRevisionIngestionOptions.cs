namespace Braid.Api.Catalog;

/// <summary>Explicit raw revision ingestion capability with a bounded immediate work admission policy.</summary>
public sealed record CatalogRevisionIngestionOptions
{
    public CatalogRevisionIngestionOptions(bool enabled = false, int maximumConcurrentOperations = 2)
    {
        if (maximumConcurrentOperations is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentOperations));
        Enabled = enabled;
        MaximumConcurrentOperations = maximumConcurrentOperations;
    }

    public bool Enabled { get; }
    public int MaximumConcurrentOperations { get; }
}

internal sealed class CatalogRevisionIngestionGate(CatalogRevisionIngestionOptions options) : IDisposable
{
    private readonly SemaphoreSlim _permits = new(options.MaximumConcurrentOperations, options.MaximumConcurrentOperations);

    internal IDisposable? TryEnter() => _permits.Wait(0) ? new Permit(_permits) : null;

    public void Dispose() => _permits.Dispose();

    private sealed class Permit(SemaphoreSlim permits) : IDisposable
    {
        private SemaphoreSlim? _permits = permits;
        public void Dispose() => Interlocked.Exchange(ref _permits, null)?.Release();
    }
}
