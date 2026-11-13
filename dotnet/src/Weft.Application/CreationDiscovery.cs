using Weft.Domain;

namespace Weft.Application;

public sealed record CreationSelectionReference(SpecBundleScope Bundle, Guid ExtractionId);
public sealed record CreationProposalReference(ProjectScope Project, Guid ProposalId, Sha256Digest Fingerprint);

/// <summary>Bounded durable discovery, independent of chat turns and acknowledgements. Implementations require current actor/project content
/// authority; proposals are creator-private. Cursors grant no authority. Return at most limit items in ascending UUID cursor order.</summary>
public interface ICreationDiscovery
{
    Task<IReadOnlyList<CreationSelectionReference>> SelectionsAsync(ActorContext actor, ProjectScope project,
        Guid? afterBundle, Guid? afterObject, int limit, CancellationToken ct);
    Task<IReadOnlyList<CreationProposalReference>> ProposalsAsync(ActorContext actor, ProjectScope project,
        Guid? afterProposal, int limit, CancellationToken ct);
}

public sealed record CreationSelection(CreationSelectionReference Reference, SpecExtractionIdentity Identity, int EvidenceBytes);

/// <summary>Discovery IDs are re-observed through the authoritative content ports before any identity or review content leaves the boundary.</summary>
public sealed class CreationDiscoveryService(ICreationDiscovery discovery, ISpecBundleServiceFactory specifications, IMappingCreationProposalStore proposals)
{
    public async Task<IReadOnlyList<CreationSelection>> SelectionsAsync(ActorContext actor, ProjectScope project, Guid? afterBundle,
        Guid? afterObject, int limit, CancellationToken ct, Func<CancellationToken, Task>? authorize = null)
    {
        Bounds(limit); if ((afterBundle is null) != (afterObject is null)) throw new ArgumentException("A complete cursor is required.");
        if (afterBundle == Guid.Empty || afterObject == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        if (authorize is not null) await authorize(ct).ConfigureAwait(false);
        var references = await discovery.SelectionsAsync(actor, project, afterBundle, afterObject, limit, ct).ConfigureAwait(false);
        if (authorize is not null) await authorize(ct).ConfigureAwait(false);
        if (references.Count > limit || references.Any(r => r.Bundle.Project != project || r.ExtractionId == Guid.Empty) || references.Distinct().Count() != references.Count)
            throw new InvalidOperationException("Invalid specification discovery result.");
        var reader = await specifications.OpenAsync(actor, false, ct).ConfigureAwait(false);
        var result = new List<CreationSelection>();
        var previous = afterBundle is null ? null : $"{afterBundle:D}/{afterObject:D}";
        foreach (var reference in references)
        {
            var key = $"{reference.Bundle.BundleId:D}/{reference.ExtractionId:D}";
            if (previous is not null && string.CompareOrdinal(key, previous) <= 0) throw new InvalidOperationException("Invalid discovery order.");
            previous = key;
            if (authorize is not null) await authorize(ct).ConfigureAwait(false);
            var inventory = await reader.InventoryAsync(reference.Bundle, ct).ConfigureAwait(false);
            if (authorize is not null) await authorize(ct).ConfigureAwait(false);
            if (inventory.Bundle.Scope != reference.Bundle || inventory.Bundle.Tombstoned) throw new SpecStorageException(SpecStorageError.NotAdmitted);
            var item = inventory.Objects.SingleOrDefault(i => i.Reference.Id.ObjectId == reference.ExtractionId);
            if (item is not { State: "published", Extraction: { } pin } || item.Reference.Id.Kind != SpecObjectKind.Extraction ||
                pin.Source.Bundle != reference.Bundle || item.Reference.Id.Source != pin.Source || item.Reference.Digest != pin.EvidenceDigest ||
                item.Format != pin.Format || item.Outcome != pin.Outcome) throw new SpecStorageException(SpecStorageError.NotAdmitted);
            result.Add(new(reference, pin, item.Reference.ByteCount));
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<MappingCreationState>> ProposalsAsync(ActorContext actor, ProjectScope project, Guid? afterProposal,
        int limit, CancellationToken ct, Func<CancellationToken, Task>? authorize = null)
    {
        Bounds(limit);
        if (afterProposal == Guid.Empty) throw new ArgumentException("Invalid cursor.");
        if (authorize is not null) await authorize(ct).ConfigureAwait(false);
        var references = await discovery.ProposalsAsync(actor, project, afterProposal, limit, ct).ConfigureAwait(false);
        if (authorize is not null) await authorize(ct).ConfigureAwait(false);
        if (references.Count > limit || references.Any(r => r.Project != project || r.ProposalId == Guid.Empty || r.Fingerprint is null) ||
            references.Select(r => r.ProposalId).Distinct().Count() != references.Count) throw new InvalidOperationException("Invalid proposal discovery result.");
        var result = new List<MappingCreationState>();
        var previous = afterProposal?.ToString("D");
        foreach (var reference in references)
        {
            var key = reference.ProposalId.ToString("D");
            if (previous is not null && string.CompareOrdinal(key, previous) <= 0) throw new InvalidOperationException("Invalid discovery order.");
            previous = key;
            if (authorize is not null) await authorize(ct).ConfigureAwait(false);
            var state = await proposals.ReadAsync(actor, project, reference.ProposalId, ct).ConfigureAwait(false);
            if (authorize is not null) await authorize(ct).ConfigureAwait(false);
            if (state is null || state.Proposal.Author != actor.PrincipalId || state.Proposal.Project != project ||
                state.Proposal.Id != reference.ProposalId || state.Proposal.Fingerprint != reference.Fingerprint)
                throw new SpecStorageException(SpecStorageError.NotAdmitted);
            result.Add(state);
        }
        return result.AsReadOnly();
    }
    private static void Bounds(int limit) { if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit)); }
}
