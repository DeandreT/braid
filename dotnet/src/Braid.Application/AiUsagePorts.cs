using Braid.Contracts.Administration;
using Braid.Domain;

namespace Braid.Application;

public interface IAiUsageReader
{
    /// <summary>Reads only metadata currently visible under the actor's administrator authority.</summary>
    Task<AiUsagePageV1?> ReadUsageAsync(ActorContext actor, AiUsageQuery query, CancellationToken token);
}
