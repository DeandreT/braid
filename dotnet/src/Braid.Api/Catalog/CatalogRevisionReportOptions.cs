using Braid.Domain;

namespace Braid.Api.Catalog;

/// <summary>Independent, default-off report reads and explicit derivation.</summary>
public sealed record CatalogRevisionReportOptions
{
    public CatalogRevisionReportOptions(bool enabled = false, bool derivationEnabled = false,
        RevisionReportPolicy? activeReadCaps = null)
    {
        if (derivationEnabled && !enabled)
            throw new ArgumentException("Report derivation requires enabled report reads.", nameof(derivationEnabled));
        Enabled = enabled;
        DerivationEnabled = derivationEnabled;
        ActiveReadCaps = activeReadCaps ?? new RevisionReportPolicy();
    }

    public bool Enabled { get; }
    public bool DerivationEnabled { get; }
    public RevisionReportPolicy ActiveReadCaps { get; }
}
