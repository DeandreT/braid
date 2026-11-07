namespace Weft.Api.Catalog;

/// <summary>No environment switch registers a conversion backend. Hosts must explicitly register a real adapter and services.</summary>
public sealed record CatalogInterchangeOptions(bool Enabled = false);

internal sealed class CatalogInterchangeGate : IDisposable
{
    private readonly SemaphoreSlim _slots = new(2);
    internal bool TryEnter() => _slots.Wait(0);
    internal void Exit() => _slots.Release();
    public void Dispose() => _slots.Dispose();
}
