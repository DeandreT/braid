using Braid.Domain;

namespace Braid.Application;

public sealed class PublicationObservationRequest
{
    public PublicationObservationRequest(ProjectScope project, IEnumerable<PrivateStorageSlotObservation> slots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        var owned = MaintenanceObservationValue.Snapshot(slots, MaintenanceObservationValue.MaximumEntries, cancellationToken);
        var ordinals = new HashSet<int>();
        foreach (var slot in owned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MaintenanceObservationValue.InProject(slot.Scope, project) || !ordinals.Add(slot.Ordinal))
                throw new ArgumentException("Observed slots must retain their project and distinct input ordinals.", nameof(slots));
        }
        Project = project;
        Slots = owned;
    }

    public ProjectScope Project { get; }
    public IReadOnlyList<PrivateStorageSlotObservation> Slots { get; }
}

/// <summary>Only flat retained registration facts. A witness confers no permission or deletion authority.</summary>
public sealed record ObservedPublicationWitness
{
    public ObservedPublicationWitness(Guid publicationId, PrivatePublicationKey key,
        PrivatePublicationFileIdentity actualIdentity, ObjectCleanupOrigin origin)
    {
        PublicationId = MaintenanceValue.Id(publicationId, nameof(publicationId));
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(actualIdentity);
        ArgumentNullException.ThrowIfNull(origin);
        if (key.RootId != actualIdentity.RootId || key.Scope != origin.Scope ||
            (key.Object.Class == PrivateObjectClass.NativeArtifact) != (origin.Kind == PrivateObjectOwnerKind.NativeIngestion))
            throw new ArgumentException("An observed witness must retain its complete storage identity and origin.");
        Key = key;
        ActualIdentity = actualIdentity;
        Origin = origin;
    }

    public Guid PublicationId { get; }
    public PrivatePublicationKey Key { get; }
    public PrivatePublicationFileIdentity ActualIdentity { get; }
    public ObjectCleanupOrigin Origin { get; }
}

public enum PublicationObservationOutcome { NoMatch = 1, ExactlyOne = 2, Ambiguous = 3 }

public sealed class PublicationSlotObservation
{
    public PublicationSlotObservation(int ordinal, PublicationObservationOutcome outcome,
        IEnumerable<ObservedPublicationWitness> witnesses, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MaintenanceObservationValue.Ordinal(ordinal);
        MaintenanceValue.Known(outcome, nameof(outcome));
        var owned = MaintenanceObservationValue.Snapshot(witnesses, 2, cancellationToken);
        var expected = outcome switch
        {
            PublicationObservationOutcome.NoMatch => 0,
            PublicationObservationOutcome.ExactlyOne => 1,
            _ => 2
        };
        if (owned.Count != expected || (owned.Count == 2 &&
            (owned[0].PublicationId == owned[1].PublicationId || owned[0].Key == owned[1].Key)))
            throw new ArgumentException("An observed outcome requires the exact number of distinct witnesses.", nameof(witnesses));
        Ordinal = ordinal;
        Outcome = outcome;
        Witnesses = owned;
    }

    public int Ordinal { get; }
    public PublicationObservationOutcome Outcome { get; }
    public IReadOnlyList<ObservedPublicationWitness> Witnesses { get; }
}

/// <summary>Later database matching of earlier physical observations, not a current or atomic census.</summary>
public sealed class PrivatePublicationObservationBatch
{
    public PrivatePublicationObservationBatch(PublicationObservationRequest request, DateTimeOffset observedAtUtc,
        IEnumerable<PublicationSlotObservation> results, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        MaintenanceValue.Utc(observedAtUtc, nameof(observedAtUtc));
        var owned = MaintenanceObservationValue.Snapshot(results, MaintenanceObservationValue.MaximumEntries, cancellationToken);
        if (owned.Count != request.Slots.Count)
            throw new ArgumentException("Publication observations require exactly one result per input slot.", nameof(results));
        for (var index = 0; index < owned.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = owned[index];
            var slot = request.Slots[index];
            if (result.Ordinal != slot.Ordinal)
                throw new ArgumentException("Publication results must retain the input slot order and ordinal.", nameof(results));
            ObservedPublicationWitness? previous = null;
            foreach (var witness in result.Witnesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (witness.ActualIdentity != slot.ActualIdentity || witness.Key.Scope != slot.Scope ||
                    witness.Key.Object.Class != slot.ObjectClass || witness.Key.RootId != slot.ActualIdentity.RootId ||
                    (slot.Kind == CleanupTargetKind.FinalObject
                        ? witness.Key.Object != slot.ObjectId : witness.Key.TempId != slot.TempId))
                    throw new ArgumentException("A publication witness must match its exact observed slot and full identity.", nameof(results));
                if (previous is not null)
                {
                    var order = slot.Kind == CleanupTargetKind.FinalObject ? 0 :
                        MaintenanceObservationValue.CompareIds(previous.Key.Object.ObjectId, witness.Key.Object.ObjectId);
                    if (order == 0) order = MaintenanceObservationValue.CompareIds(previous.Key.TempId, witness.Key.TempId);
                    if (order >= 0)
                        throw new ArgumentException("Publication witnesses must retain their database UUID order.", nameof(results));
                }
                previous = witness;
            }
        }
        Request = request;
        ObservedAtUtc = observedAtUtc;
        Results = owned;
    }

    public PublicationObservationRequest Request { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public IReadOnlyList<PublicationSlotObservation> Results { get; }
}
