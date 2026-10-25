using Weft.Domain;

namespace Weft.Application;

/// <summary>Server authority for exact-base replay and conditional ingestion. No public method accepts native bytes.</summary>
public sealed class MappingDraftService(IMappingDraftStore store, IPrivateObjectStore objects,
    IMappingEditBackend backend, IRevisionIngestionService ingestion) : IDisposable
{
    private readonly IMappingDraftStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPrivateObjectStore _objects = objects ?? throw new ArgumentNullException(nameof(objects));
    private readonly IMappingEditBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    private readonly IRevisionIngestionService _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
    private readonly SemaphoreSlim _gate = new(2, 2);

    public async Task<MappingDraftResult> CreateAsync(ActorContext actor, MappingScope mapping, Guid key, Guid baseRevisionId,
        CancellationToken cancellationToken = default)
    {
        RequireId(key); RequireId(baseRevisionId);
        if (await _store.FindCreationAsync(actor, mapping, key, baseRevisionId, cancellationToken).ConfigureAwait(false) is { } existing) return existing;
        var immutableBase = await _store.SelectBaseAsync(actor, mapping, baseRevisionId, false, cancellationToken).ConfigureAwait(false);
        if (immutableBase is null) return Hidden();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDraftOutcome.Busy);
        try
        {
            var result = await ReplayAsync(immutableBase, MappingEditScript.Empty, cancellationToken).ConfigureAwait(false);
            if (result.Checkpoint is not { } checkpoint) return new(MappingDraftOutcome.ReplayFailed, Failure: result.Failure);
            return await _store.CreateAsync(actor, mapping, key, immutableBase, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<MappingDraftSnapshot>?> ListAsync(ActorContext actor, MappingScope mapping,
        CancellationToken cancellationToken = default) => _store.ListAsync(actor, mapping, cancellationToken);

    public async Task<MappingDraftSnapshot?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id,
        CancellationToken cancellationToken = default)
    {
        await _store.ReconcileSaveAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeArtifact?> ReadBaseAsync(ActorContext actor, MappingScope mapping, Guid id,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.State is not (MappingDraftState.Open or MappingDraftState.Saving)) return null;
        var artifact = await ReadBaseArtifactAsync(snapshot.Base, cancellationToken).ConfigureAwait(false);
        // A base read must not escape after a concurrent rebase, discard or demotion.
        var fresh = await _store.ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        return fresh is not null && fresh.Base == snapshot.Base && fresh.State is MappingDraftState.Open or MappingDraftState.Saving ? artifact : null;
    }

    public async Task<MappingDraftContent?> ReadOperationsAsync(ActorContext actor, MappingScope mapping, Guid id,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        var operations = await _store.ReadOperationsAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (operations is null || operations.Digest != snapshot.OperationsDigest || operations.Count != snapshot.OperationCount) return null;
        return new(snapshot, operations);
    }

    public Task<MappingDraftResult> PutOperationsAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingEditScript operations, CancellationToken cancellationToken = default) =>
        _store.PutOperationsAsync(actor, mapping, id, version, operations, cancellationToken);

    public async Task<MappingDraftResult> CheckpointAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        CancellationToken cancellationToken = default)
    {
        var content = await ReadOperationsAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (content is null) return Hidden();
        if (content.Draft.Version != version) return new(MappingDraftOutcome.PreconditionFailed, content.Draft);
        if (content.Draft.State != MappingDraftState.Open) return new(MappingDraftOutcome.Conflict, content.Draft);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDraftOutcome.Busy);
        try
        {
            var result = await ReplayAsync(content.Draft.Base, content.Operations, cancellationToken).ConfigureAwait(false);
            if (result.Checkpoint is not { } checkpoint) return new(MappingDraftOutcome.ReplayFailed, content.Draft, result.Failure);
            return await _store.PutCheckpointAsync(actor, mapping, id, version, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MappingEditCheckpoint?> ReadCheckpointAsync(ActorContext actor, MappingScope mapping, Guid id,
        CancellationToken cancellationToken = default)
    {
        var draft = await ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (draft?.Checkpoint?.Version != draft?.Version || draft is null) return null;
        return await _store.ReadCheckpointAsync(actor, mapping, id, draft.Version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MappingDraftResult> RebaseAsync(ActorContext actor, MappingScope mapping, Guid id, long version, Guid targetRevisionId,
        CancellationToken cancellationToken = default)
    {
        RequireId(targetRevisionId);
        var content = await ReadOperationsAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (content is null) return Hidden();
        if (content.Draft.Version != version) return new(MappingDraftOutcome.PreconditionFailed, content.Draft);
        if (content.Draft.State != MappingDraftState.Open) return new(MappingDraftOutcome.Conflict, content.Draft);
        var target = await _store.SelectBaseAsync(actor, mapping, targetRevisionId, true, cancellationToken).ConfigureAwait(false);
        if (target is null) return new(MappingDraftOutcome.Conflict, content.Draft);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDraftOutcome.Busy);
        try
        {
            var result = await ReplayAsync(target, content.Operations, cancellationToken).ConfigureAwait(false);
            if (result.Checkpoint is not { } checkpoint) return new(MappingDraftOutcome.ReplayFailed, content.Draft, result.Failure);
            // The function checks head, author and version again after the worker returns.
            return await _store.RebaseAsync(actor, mapping, id, version, target, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<MappingDraftResult> SaveAsync(ActorContext actor, MappingScope mapping, Guid id, long version, Guid key,
        string? originalFilename = null, CancellationToken cancellationToken = default)
    {
        RequireId(key);
        var filename = new AppendRevisionRequest(new AnyHead(), originalFilename).OriginalFilename;
        await _store.ReconcileSaveAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        var snapshot = await _store.ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return Hidden();
        if (snapshot.Version != version) return new(MappingDraftOutcome.PreconditionFailed, snapshot);
        if (snapshot.State == MappingDraftState.Saved)
            return await SavedResultAsync(actor, mapping, snapshot, key, filename, cancellationToken).ConfigureAwait(false);
        if (snapshot.OperationCount == 0) return new(MappingDraftOutcome.EmptyOperations, snapshot);
        if (snapshot.Checkpoint?.Version != version) return new(MappingDraftOutcome.CheckpointRequired, snapshot);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDraftOutcome.Busy);
        try
        {
            var beginning = await _store.BeginSaveAsync(actor, mapping, id, version, key, filename, cancellationToken).ConfigureAwait(false);
            if (beginning.Draft?.State == MappingDraftState.Saved)
                return await SavedResultAsync(actor, mapping, beginning.Draft, key, filename, cancellationToken).ConfigureAwait(false);
            if (beginning.Outcome is not (MappingDraftOutcome.Succeeded or MappingDraftOutcome.Replayed)) return beginning;
            snapshot = beginning.Draft ?? throw new InvalidOperationException("Saving requires a stored draft snapshot.");
            var checkpoint = await _store.ReadCheckpointAsync(actor, mapping, id, version, cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                // A concurrent retry may have committed and reconciled this same save.
                var completed = await _store.ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
                return completed?.State == MappingDraftState.Saved
                    ? await SavedResultAsync(actor, mapping, completed, key, filename, cancellationToken).ConfigureAwait(false)
                    : Hidden();
            }
            // The stored state freezes the exact log, base, checkpoint, key and filename.
            // Repeating this call after a crash recovers ingestion's original receipt.
            using var source = new MemoryStream(checkpoint.Artifact.CopyBytes(), false);
            var result = await _ingestion.AppendAsync(actor, mapping, key,
                new AppendRevisionRequest(new EqualHead(snapshot.Base.Revision.RevisionId), filename), source, cancellationToken).ConfigureAwait(false);
            await _store.ReconcileSaveAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
            var final = await _store.ReadAsync(actor, mapping, id, cancellationToken).ConfigureAwait(false);
            if (final is null) return Hidden();
            return new(MappingDraftOutcome.Succeeded, final, Ingestion: result);
        }
        finally { _gate.Release(); }
    }

    public Task<MappingDraftResult> DiscardAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        CancellationToken cancellationToken = default) => _store.DiscardAsync(actor, mapping, id, version, cancellationToken);

    private async Task<MappingDraftResult> SavedResultAsync(ActorContext actor, MappingScope mapping, MappingDraftSnapshot snapshot,
        Guid key, string filename, CancellationToken cancellationToken)
    {
        if (snapshot.SavingKey != key || snapshot.SavingFilename != filename || snapshot.SavingOperationId is not { } operation)
            return new(MappingDraftOutcome.Conflict, snapshot);
        var receipt = await _ingestion.GetOwnReceiptAsync(actor, new(mapping.OrganizationId, mapping.ProjectId), operation, cancellationToken).ConfigureAwait(false);
        if (receipt is null) return Hidden();
        return new(MappingDraftOutcome.Replayed, snapshot, Ingestion: RevisionIngestionResult.ForReceipt(
            receipt.CommittedAdmission == IngestionAdmissionClassification.EligibleValid ? RevisionIngestionOutcome.CommittedValid : RevisionIngestionOutcome.CommittedDecodedInvalid, receipt));
    }

    private async Task<MappingEditResult> ReplayAsync(MappingDraftBase immutableBase, MappingEditScript operations, CancellationToken token)
    {
        var native = await ReadBaseArtifactAsync(immutableBase, token).ConfigureAwait(false);
        var result = await _backend.ReplayAsync(native, operations, token).ConfigureAwait(false);
        if ((result.Checkpoint is null) == (result.Failure is null)) throw new InvalidOperationException("Edit replay requires exactly one checkpoint or failure.");
        if (result.Checkpoint is { } checkpoint && (checkpoint.Artifact.Scope != immutableBase.Revision
            || checkpoint.Artifact.ByteCount > MappingEditPolicy.MaximumNativeBytes
            || checkpoint.Evidence.Facts.Provenance.NativeDigest != checkpoint.Artifact.Digest))
            throw new InvalidOperationException("The edit worker returned inconsistent checkpoint provenance.");
        return result;
    }

    private async Task<NativeArtifact> ReadBaseArtifactAsync(MappingDraftBase immutableBase, CancellationToken token)
    {
        var bytes = await _objects.ReadVerifiedAsync(immutableBase.Revision, immutableBase.NativeObject, token).ConfigureAwait(false);
        return NativeArtifact.Admit(immutableBase.Revision, bytes, MappingEditPolicy.MaximumNativeBytes);
    }

    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A nonempty request identity is required.", nameof(id)); }
    private static MappingDraftResult Hidden() => new(MappingDraftOutcome.NotAuthorizedOrNotFound);
    public void Dispose() => _gate.Dispose();
}
