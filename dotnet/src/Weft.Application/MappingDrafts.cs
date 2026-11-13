using Weft.Domain;

namespace Weft.Application;

public enum MappingDraftState { Open, Saving, Saved, Discarded, Expired }
public enum MappingDraftOutcome { Succeeded, Created, Replayed, DraftExists, NotAuthorizedOrNotFound, PreconditionFailed, Conflict, Busy, EmptyOperations, CheckpointRequired, ReplayFailed }

public sealed record MappingDraftBase(RevisionScope Revision, long Sequence, PrivateObjectReference NativeObject);
public sealed record MappingDraftCheckpointSummary(long Version, Sha256Digest NativeDigest, int NativeByteCount, bool IsValid, int TotalIssueCount);
public sealed record MappingDraftSnapshot(Guid Id, MappingScope Mapping, PrincipalId Author, MappingDraftBase Base,
    MappingDraftState State, long Version, int OperationCount, Sha256Digest? OperationsDigest, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ChangedAtUtc, DateTimeOffset ExpiresAtUtc, MappingDraftCheckpointSummary? Checkpoint,
    Guid? SavingKey, string? SavingFilename, Guid? SavingOperationId, Guid? SavedRevisionId);
public sealed record MappingDraftResult(MappingDraftOutcome Outcome, MappingDraftSnapshot? Draft = null,
    MappingEditFailure? Failure = null, RevisionIngestionResult? Ingestion = null);
public sealed record MappingDraftContent(MappingDraftSnapshot Draft, MappingEditScript Operations);

/// <summary>Author-private database functions re-check fresh EditMapping authority on every call.</summary>
public interface IMappingDraftStore
{
    Task<MappingDraftResult?> FindCreationAsync(ActorContext actor, MappingScope mapping, Guid key, Guid baseRevisionId,
        CancellationToken cancellationToken = default);
    Task<MappingDraftBase?> SelectBaseAsync(ActorContext actor, MappingScope mapping, Guid revisionId,
        bool requireHead, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> CreateAsync(ActorContext actor, MappingScope mapping, Guid key, MappingDraftBase immutableBase,
        MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MappingDraftSnapshot>?> ListAsync(ActorContext actor, MappingScope mapping, CancellationToken cancellationToken = default);
    Task<MappingDraftSnapshot?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default);
    Task<MappingEditScript?> ReadOperationsAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default);
    Task<MappingEditCheckpoint?> ReadCheckpointAsync(ActorContext actor, MappingScope mapping, Guid id, long version, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> PutOperationsAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingEditScript operations, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> PutCheckpointAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> RebaseAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingDraftBase newBase, MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> BeginSaveAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        Guid key, string filename, CancellationToken cancellationToken = default);
    Task ReconcileSaveAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default);
    Task<MappingDraftResult> DiscardAsync(ActorContext actor, MappingScope mapping, Guid id, long version, CancellationToken cancellationToken = default);
    Task<int> ExpireAsync(int limit, CancellationToken cancellationToken = default);
}
