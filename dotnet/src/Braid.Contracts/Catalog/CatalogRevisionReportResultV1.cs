using System.Text.Json.Serialization;

namespace Braid.Contracts.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogRevisionReportResultV1
{
    [JsonConstructor]
    public CatalogRevisionReportResultV1(string derivationId, string generation,
        DateTimeOffset startedAtUtc, DateTimeOffset finishedAtUtc)
    {
        DerivationId = CatalogValueV1.RequireId(derivationId, nameof(derivationId));
        Generation = CatalogValueV1.RequirePositiveDecimal(generation, nameof(generation));
        CatalogValueV1.RequireHistory(startedAtUtc, finishedAtUtc);
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        FinishedAtUtc = finishedAtUtc.ToUniversalTime();
    }

    public string DerivationId { get; }
    public string Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset FinishedAtUtc { get; }
}
