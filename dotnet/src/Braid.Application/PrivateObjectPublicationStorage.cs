using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// Publishes with the workflow's actual original claim. The owner is evidence to
/// recheck through the registry, not an authorization grant or a replacement claim.
/// </summary>
public interface IPrivateObjectPublicationStore : IPrivateObjectStore
{
    Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(PrivatePublicationOwner owner,
        RevisionScope expectedScope, PrivateObjectReference reference, Stream source,
        CancellationToken cancellationToken = default);
}

/// <summary>Physical observations, distinct from database acknowledgement dispositions.</summary>
public enum PrivateObjectRetirementOutcome
{
    Deleted = 1,
    Missing = 2,
    Busy = 3,
    Unsupported = 4,
    UnsafeStorage = 5,
    Io = 6
}

/// <summary>
/// Fences and retires only the exact target of an already issued immutable
/// retirement. A temporary target retains its captured original publication;
/// this operation neither establishes authority nor acknowledges a cleanup claim.
/// </summary>
public interface IPrivateObjectRetirementStore
{
    Task<PrivateObjectRetirementOutcome> RetireAsync(ObjectCleanupClaim claim,
        CancellationToken cancellationToken = default);
}
