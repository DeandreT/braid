using Weft.Domain;

namespace Weft.Application;

/// <summary>One explicit bounded maintenance invocation; no scheduler, retained port ownership or background work.</summary>
public sealed partial class PrivateObjectMaintenanceService
{
    private readonly IPrivateObjectMaintenanceMetadata _metadata;
    private readonly IPrivateObjectMaintenanceStatusReader _statuses;
    private readonly IPrivateObjectPublicationObservationReader _publications;
    private readonly IPrivateObjectStorageObserver _storageObservations;
    private readonly IPrivateObjectRetirementStore _retirementStore;
    private readonly Guid _serverInstanceId;
    private readonly TimeProvider _timeProvider;

    public PrivateObjectMaintenanceService(IPrivateObjectMaintenanceMetadata metadata,
        IPrivateObjectMaintenanceStatusReader statuses, IPrivateObjectPublicationObservationReader publications,
        IPrivateObjectStorageObserver storageObservations, IPrivateObjectRetirementStore retirementStore,
        Guid serverInstanceId) : this(metadata, statuses, publications, storageObservations, retirementStore,
            serverInstanceId, TimeProvider.System)
    { }

    internal PrivateObjectMaintenanceService(IPrivateObjectMaintenanceMetadata metadata,
        IPrivateObjectMaintenanceStatusReader statuses, IPrivateObjectPublicationObservationReader publications,
        IPrivateObjectStorageObserver storageObservations, IPrivateObjectRetirementStore retirementStore,
        Guid serverInstanceId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(storageObservations);
        ArgumentNullException.ThrowIfNull(retirementStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        MaintenanceValue.Id(serverInstanceId, nameof(serverInstanceId));
        _metadata = metadata;
        _statuses = statuses;
        _publications = publications;
        _storageObservations = storageObservations;
        _retirementStore = retirementStore;
        _serverInstanceId = serverInstanceId;
        _timeProvider = timeProvider;
    }

    public async Task<PrivateObjectMaintenanceRunResult> RunOnceAsync(ProjectScope project,
        ObjectCleanupScanCursor? cleanupAfter = null, ObjectCleanupStatusCursor? statusAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if ((cleanupAfter is not null && cleanupAfter.Project != project) ||
            (statusAfter is not null && statusAfter.Project != project))
            throw new ArgumentException("Maintenance cursors must belong to the requested project.");
        using var deadline = new RunDeadline(_timeProvider, cancellationToken);
        var state = new RunState(project);
        try
        {
            if (deadline.CanStart)
            {
                var statuses = await _statuses.GetStatusesAsync(project, statusAfter, deadline.WorkToken).ConfigureAwait(false);
                if (statuses is null || statuses.Project != project || (statusAfter is not null &&
                    statuses.Headers.Any(header => MaintenanceObservationValue.CompareIds(header.RetirementId, statusAfter.AfterRetirementId) <= 0)))
                    throw new InvalidRunEvidence();
                state.Statuses = statuses;
                if (deadline.CanStart)
                {
                    state.MetadataPhase = false;
                    var storage = await _storageObservations.ObserveAsync(project, deadline.WorkToken).ConfigureAwait(false);
                    if (storage is null || storage.Project != project) throw new InvalidRunEvidence();
                    state.Storage = storage;
                    if (deadline.CanStart)
                    {
                        var request = new PublicationObservationRequest(project,
                            storage.Entries.Where(entry => entry.Kind == PrivateStorageEntryKind.CandidateSlot).Select(entry => entry.Slot!), deadline.WorkToken);
                        state.MetadataPhase = true;
                        var matches = await _publications.MatchAsync(request, deadline.WorkToken).ConfigureAwait(false);
                        if (matches is null || matches.Request.Project != project ||
                            !matches.Request.Slots.SequenceEqual(request.Slots)) throw new InvalidRunEvidence();
                        state.Publications = matches;
                        if (deadline.CanStart)
                        {
                            state.ClaimAttempted = true;
                            var batch = await _metadata.ClaimAsync(project, _serverInstanceId, cleanupAfter, deadline.WorkToken).ConfigureAwait(false);
                            if (batch is null || batch.Project != project || batch.Claims.Any(claim => claim.ServerInstanceId != _serverInstanceId))
                                throw new InvalidRunEvidence();
                            state.Batch = batch;
                            for (var index = 0; index < batch.Claims.Count; index++)
                                state.Targets.Add(new(index, batch.Claims[index], PrivateObjectMaintenancePhysicalObservation.NotStarted,
                                    PrivateObjectMaintenanceAcknowledgement.NotAttempted));
                            for (var index = 0; index < batch.Claims.Count && deadline.CanStart && state.Failure is null; index++)
                                await ProcessTargetAsync(state, index, deadline).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (InvalidRunEvidence) { state.Failure = PrivateObjectMaintenanceRunFailure.InvalidEvidence; }
        catch (PrivateStorageObservationChangedException) { state.Failure = PrivateObjectMaintenanceRunFailure.ObservationChanged; }
        catch (OperationCanceledException) when (deadline.Stopped) { }
        catch (Exception error)
        {
            state.Failure = state.MetadataPhase ? PrivateObjectMaintenanceRunFailure.MetadataUnavailable : StorageFailure(error);
        }
        var outcome = deadline.CallerCanceled ? PrivateObjectMaintenanceRunOutcome.Canceled :
            deadline.WorkExpired ? PrivateObjectMaintenanceRunOutcome.Deadline :
            state.Failure is not null ? PrivateObjectMaintenanceRunOutcome.Failed : PrivateObjectMaintenanceRunOutcome.Finished;
        return new(project, cleanupAfter, statusAfter, outcome, state.Failure,
            new(state.Statuses, state.Storage, state.Publications), state.ClaimAttempted, state.Batch, state.Targets);
    }

    private static PrivateObjectMaintenanceRunFailure StorageFailure(Exception error) => error is PrivateObjectStorageException storage
        ? storage.Error switch
        {
            PrivateObjectStorageError.UnsafeStorage or PrivateObjectStorageError.Integrity or PrivateObjectStorageError.ScopeMismatch =>
                PrivateObjectMaintenanceRunFailure.UnsafeStorage,
            PrivateObjectStorageError.Unsupported => PrivateObjectMaintenanceRunFailure.UnsupportedStorage,
            _ => PrivateObjectMaintenanceRunFailure.StorageUnavailable
        } : PrivateObjectMaintenanceRunFailure.StorageUnavailable;

    private sealed class InvalidRunEvidence : Exception { }

    private sealed class RunState(ProjectScope project)
    {
        internal ProjectScope Project { get; } = project;
        internal bool MetadataPhase { get; set; } = true;
        internal ObjectCleanupStatusPage? Statuses { get; set; }
        internal PrivateStorageObservationPage? Storage { get; set; }
        internal PrivatePublicationObservationBatch? Publications { get; set; }
        internal bool ClaimAttempted { get; set; }
        internal ObjectCleanupClaimBatch? Batch { get; set; }
        internal List<PrivateObjectMaintenanceTargetResult> Targets { get; } = [];
        internal PrivateObjectMaintenanceRunFailure? Failure { get; set; }
    }
}
