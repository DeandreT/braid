using System.Text.Json.Serialization;
using Braid.Contracts.Operations;

namespace Braid.Contracts.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogRevisionReportAttemptV1
{
    [JsonConstructor]
    public CatalogRevisionReportAttemptV1(string derivationId, string generation, DateTimeOffset startedAtUtc,
        DateTimeOffset? finishedAtUtc, OperationStatusV1? status, CatalogRevisionReportFailureV1? failure)
    {
        DerivationId = CatalogValueV1.RequireId(derivationId, nameof(derivationId));
        Generation = CatalogValueV1.RequirePositiveDecimal(generation, nameof(generation));
        CatalogRevisionReportValueV1.Known(status, nameof(status));
        CatalogRevisionReportValueV1.Known(failure, nameof(failure));
        if (finishedAtUtc is { } finished) CatalogValueV1.RequireHistory(startedAtUtc, finished);
        if (finishedAtUtc is null && (status is not null || failure is not null))
            throw new ArgumentException("An unfinished attempt has no terminal observations.");
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        FinishedAtUtc = finishedAtUtc?.ToUniversalTime();
        Status = status;
        Failure = failure;
    }

    public string DerivationId { get; }
    public string Generation { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public OperationStatusV1? Status { get; }
    public CatalogRevisionReportFailureV1? Failure { get; }
}
