using System.Globalization;
using Braid.Contracts.Administration;
using Braid.Domain;

namespace Braid.Application;

public sealed record AiQuotaPolicy
{
    public AiQuotaPolicy(Guid? principalId, AiQuotaPeriod period, long? maximumTurns, long? maximumTokens)
    {
        if (principalId == Guid.Empty || !Enum.IsDefined(period) || maximumTurns is < 1 || maximumTokens is < 1)
            throw new ArgumentException("Invalid AI quota policy.");
        PrincipalId = principalId; Period = period; MaximumTurns = maximumTurns; MaximumTokens = maximumTokens;
    }
    public Guid? PrincipalId { get; }
    public AiQuotaPeriod Period { get; }
    public long? MaximumTurns { get; }
    public long? MaximumTokens { get; }
    public bool Disabled => MaximumTurns is null && MaximumTokens is null;

    public static long? ParseLimit(string? text) => text is null ? null :
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 &&
        text == value.ToString(CultureInfo.InvariantCulture) ? value : throw new ArgumentException("Invalid AI quota limit.");
    public static AiQuotaPeriod ParsePeriod(string period) => period switch
    {
        "hour" => AiQuotaPeriod.Hour,
        "day" => AiQuotaPeriod.Day,
        "month" => AiQuotaPeriod.Month,
        _ => throw new ArgumentException("Invalid AI quota period.")
    };
}

public interface IAiQuotaStore
{
    Task<AiQuotaSnapshotV1?> ReadQuotasAsync(ActorContext actor, Guid organizationId, Guid? principalId, CancellationToken token);
    Task<string> WriteQuotaAsync(ActorContext actor, Guid organizationId, long expectedVersion, AiQuotaPolicy policy, CancellationToken token);
}
