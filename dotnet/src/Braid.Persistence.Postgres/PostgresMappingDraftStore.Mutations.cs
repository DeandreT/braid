using Npgsql;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresMappingDraftStore
{
    public Task<MappingDraftResult?> FindCreationAsync(ActorContext actor, MappingScope mapping, Guid key, Guid baseRevisionId,
        CancellationToken cancellationToken = default) =>
        _reader.RunAsync<MappingDraftResult?>(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT outcome,draft_id FROM braid.draft_observe_creation($1,$2,$3,$4,$5,$6)", connection, transaction);
            Scope(command, actor, mapping, baseRevisionId); command.Parameters.AddWithValue(key);
            MappingDraftOutcome outcome; Guid? id;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return null;
                outcome = Outcome(reader.GetString(0)); id = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
            var snapshot = id is null ? null : (await ReadCoreAsync(connection, transaction, actor, mapping, id, cancellationToken)).SingleOrDefault();
            return new MappingDraftResult(snapshot is null && outcome is MappingDraftOutcome.Replayed or MappingDraftOutcome.DraftExists ? MappingDraftOutcome.NotAuthorizedOrNotFound : outcome, snapshot);
        }, cancellationToken);

    public Task<MappingDraftResult> CreateAsync(ActorContext actor, MappingScope mapping, Guid key, MappingDraftBase immutableBase,
        MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        _writer.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT outcome,draft_id FROM braid.draft_create($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)", connection, transaction);
            Scope(command, actor, mapping, immutableBase.Revision.RevisionId); command.Parameters.AddWithValue(key);
            Checkpoint(command, checkpoint);
            MappingDraftOutcome outcome; Guid? id;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw Inconsistent();
                outcome = Outcome(reader.GetString(0)); id = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }
            var snapshot = id is null ? null : (await ReadCoreAsync(connection, transaction, actor, mapping, id, cancellationToken)).SingleOrDefault();
            return new MappingDraftResult(outcome, snapshot);
        }, cancellationToken);

    public Task<MappingDraftResult> PutOperationsAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingEditScript operations, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, mapping, id, "SELECT braid.draft_put_operations($1,$2,$3,$4,$5,$6,$7,$8)", command =>
        {
            command.Parameters.AddWithValue(version); command.Parameters.AddWithValue(operations.CopyUtf8()); command.Parameters.AddWithValue(operations.Count);
        }, cancellationToken);

    public Task<MappingDraftResult> PutCheckpointAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, mapping, id, "SELECT braid.draft_put_checkpoint($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)", command =>
        {
            command.Parameters.AddWithValue(version); Checkpoint(command, checkpoint);
        }, cancellationToken);

    public Task<MappingDraftResult> RebaseAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        MappingDraftBase newBase, MappingEditCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, mapping, id, "SELECT braid.draft_rebase($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)", command =>
        {
            command.Parameters.AddWithValue(version); command.Parameters.AddWithValue(newBase.Revision.RevisionId); Checkpoint(command, checkpoint);
        }, cancellationToken);

    public Task<MappingDraftResult> BeginSaveAsync(ActorContext actor, MappingScope mapping, Guid id, long version,
        Guid key, string filename, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, mapping, id, "SELECT braid.draft_begin_save($1,$2,$3,$4,$5,$6,$7,$8)", command =>
        {
            command.Parameters.AddWithValue(version); command.Parameters.AddWithValue(key); command.Parameters.AddWithValue(filename);
        }, cancellationToken);

    public async Task ReconcileSaveAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default) =>
        _ = await _writer.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT braid.draft_reconcile($1,$2,$3,$4,$5)", connection, transaction);
            Scope(command, actor, mapping, id); return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken).ConfigureAwait(false);

    public Task<MappingDraftResult> DiscardAsync(ActorContext actor, MappingScope mapping, Guid id, long version, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, mapping, id, "SELECT braid.draft_discard($1,$2,$3,$4,$5,$6)", command => command.Parameters.AddWithValue(version), cancellationToken);

    public Task<int> ExpireAsync(int limit, CancellationToken cancellationToken = default) =>
        _writer.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT braid.draft_expire($1)", connection, transaction);
            command.Parameters.AddWithValue(limit); return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? throw Inconsistent());
        }, cancellationToken);

    private Task<MappingDraftResult> MutateAsync(ActorContext actor, MappingScope mapping, Guid id, string sql,
        Action<NpgsqlCommand> parameters, CancellationToken token) =>
        _writer.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            Scope(command, actor, mapping, id); parameters(command);
            var outcome = Outcome((string)(await command.ExecuteScalarAsync(token) ?? throw Inconsistent()));
            var snapshot = outcome == MappingDraftOutcome.NotAuthorizedOrNotFound ? null
                : (await ReadCoreAsync(connection, transaction, actor, mapping, id, token)).SingleOrDefault();
            return new MappingDraftResult(outcome, snapshot);
        }, token);

    private static void Checkpoint(NpgsqlCommand command, MappingEditCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var provenance = checkpoint.Evidence.Facts.Provenance;
        if (!provenance.Confirmed || provenance.NativeDigest != checkpoint.Artifact.Digest || checkpoint.Validation.IsValid is not { } valid
            || checkpoint.Validation.TotalIssueCount != checkpoint.Evidence.Facts.TotalIssueCount
            || checkpoint.Projection.Document?.Provenance.NativeArtifactDigest.Value != checkpoint.Artifact.Digest.Value) throw Inconsistent();
        var validation = RevisionReportResponseSerializer.Serialize(checkpoint.Validation, IngestionEvidenceDocument.MaximumUtf8Bytes);
        var projection = RevisionReportResponseSerializer.Serialize(checkpoint.Projection, MappingEditPolicy.MaximumReportBytes);
        command.Parameters.AddWithValue(checkpoint.Artifact.CopyBytes()); command.Parameters.AddWithValue(checkpoint.Evidence.CopyUtf8());
        command.Parameters.AddWithValue(validation); command.Parameters.AddWithValue(projection); command.Parameters.AddWithValue(valid);
        command.Parameters.AddWithValue(checkpoint.Validation.TotalIssueCount); command.Parameters.AddWithValue(provenance.BackendProfile.EngineRevision);
        command.Parameters.AddWithValue(provenance.BackendProfile.BackendBuild); command.Parameters.AddWithValue(provenance.BackendProfile.AdapterBuild);
    }

    private static MappingDraftOutcome Outcome(string outcome) => outcome switch
    {
        "succeeded" => MappingDraftOutcome.Succeeded,
        "created" => MappingDraftOutcome.Created,
        "replayed" => MappingDraftOutcome.Replayed,
        "draftExists" => MappingDraftOutcome.DraftExists,
        "notAuthorizedOrNotFound" => MappingDraftOutcome.NotAuthorizedOrNotFound,
        "preconditionFailed" => MappingDraftOutcome.PreconditionFailed,
        "conflict" => MappingDraftOutcome.Conflict,
        "emptyOperations" => MappingDraftOutcome.EmptyOperations,
        "checkpointRequired" => MappingDraftOutcome.CheckpointRequired,
        _ => throw Inconsistent()
    };
}
