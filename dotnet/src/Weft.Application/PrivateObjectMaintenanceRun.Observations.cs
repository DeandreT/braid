using Weft.Domain;

namespace Weft.Application;

/// <summary>Nullable evidence is unavailable, not zero. Completeness applies only to the selected page.</summary>
public sealed class PrivateObjectMaintenanceObservationSummary
{
    private readonly HashSet<Guid>? _exhaustedIds;

    internal PrivateObjectMaintenanceObservationSummary(ObjectCleanupStatusPage? statuses,
        PrivateStorageObservationPage? storage, PrivatePublicationObservationBatch? publications)
    {
        if ((storage is not null && statuses is null) || (publications is not null && storage is null) ||
            (storage is not null && storage.Project != statuses!.Project) ||
            (publications is not null && (publications.Request.Project != storage!.Project ||
                !publications.Request.Slots.SequenceEqual(storage.Entries.Where(entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot)
                    .Select(entry => entry.Slot!)))))
            throw new ArgumentException("Maintenance evidence must retain its admitted observation order.");
        ObservedProject = statuses?.Project;
        ReadyToClaim = publications is not null;
        if (statuses is not null)
        {
            MetadataRetirementsExamined = statuses.ExaminedCount;
            MetadataObservedAtUtc = statuses.ObservedAtUtc;
            MetadataStatusPageComplete = statuses.IsComplete;
            NextStatusCursor = statuses.Next;
            _exhaustedIds = [];
            foreach (var header in statuses.Headers)
                if (header.State == ObjectCleanupObservedState.Exhausted) _exhaustedIds.Add(header.RetirementId);
            ExhaustedRetirementsObserved = _exhaustedIds.Count;
        }
        if (storage is not null)
        {
            FilesystemEntriesExamined = storage.ExaminedCount;
            FilesystemTraversalComplete = storage.TraversalComplete;
        }
        if (publications is not null)
        {
            PublicationSlotsExamined = publications.Results.Count;
            PublicationObservedAtUtc = publications.ObservedAtUtc;
            var unproved = storage!.Entries.Count(entry => entry.Kind == PrivateStorageEntryKind.Unproved);
            var matched = 0;
            foreach (var result in publications.Results)
            {
                if (result.Outcome == PublicationObservationOutcome.ExactlyOne) matched++;
                else unproved++;
            }
            BoundPublicationMatchesObserved = matched;
            OwnershipUnprovedEntriesObserved = unproved;
        }
    }

    public int? MetadataRetirementsExamined { get; }
    public int? ExhaustedRetirementsObserved { get; }
    public DateTimeOffset? MetadataObservedAtUtc { get; }
    public bool? MetadataStatusPageComplete { get; }
    public ObjectCleanupStatusCursor? NextStatusCursor { get; }
    public int? FilesystemEntriesExamined { get; }
    public bool? FilesystemTraversalComplete { get; }
    public int? PublicationSlotsExamined { get; }
    public int? BoundPublicationMatchesObserved { get; }
    public DateTimeOffset? PublicationObservedAtUtc { get; }
    public int? OwnershipUnprovedEntriesObserved { get; }

    internal ProjectScope? ObservedProject { get; }
    internal bool ReadyToClaim { get; }
    internal HashSet<Guid>? CopyExhaustedIds() => _exhaustedIds is null ? null : new(_exhaustedIds);
}
