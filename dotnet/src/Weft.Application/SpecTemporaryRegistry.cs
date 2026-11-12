using Weft.Domain;

namespace Weft.Application;

public sealed record SpecTemporarySnapshot(SpecPublicationClaim Publication, Guid TemporaryId, SpecTemporaryPhase Phase,
    SpecTemporaryEvidence Evidence, Sha256Digest SealDigest, Sha256Digest? PreviousDigest, Guid? RetirementId, bool Retired);

/// <summary>Durable temporary ownership/evidence only. Physical seal installation and native recovery remain unavailable.</summary>
public interface ISpecTemporaryRegistry
{
    Task<SpecTemporarySnapshot> AdvanceTemporaryAsync(SpecPublicationClaim claim, Guid temporaryId, SpecTemporaryPhase phase,
        SpecTemporaryEvidence evidence, Sha256Digest? previousDigest, CancellationToken token = default);
    Task<IReadOnlyList<SpecTemporarySnapshot>> RecoverTemporariesAsync(SpecPublicationClaim claim, CancellationToken token = default);
    Task<IReadOnlyList<SpecTemporarySnapshot>> RetiringTemporariesAsync(SpecRetirementClaim claim, CancellationToken token = default);
    Task ExecuteTemporaryRetirementAsync(SpecRetirementClaim claim, SpecTemporarySnapshot temporary,
        Func<CancellationToken, Task> retire, CancellationToken token = default);
}
