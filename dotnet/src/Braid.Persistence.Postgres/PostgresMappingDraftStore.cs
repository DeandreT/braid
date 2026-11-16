using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Projections;
using Braid.Contracts.Validation;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Draft ownership, authority, optimistic concurrency and recovery are enforced in database functions.</summary>
public sealed partial class PostgresMappingDraftStore : IMappingDraftStore
{
    private readonly PostgresMappingDraftDatabase _reader;
    private readonly PostgresMappingDraftDatabase _writer;
    private static readonly JsonSerializerOptions Json = ProjectionJson.CreateOptions();

    public PostgresMappingDraftStore(PostgresMappingDraftDatabase reader, PostgresMappingDraftDatabase writer)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        if (reader.Profile != MappingDraftDatabaseProfile.Reader || writer.Profile != MappingDraftDatabaseProfile.Writer)
            throw new ArgumentException("Draft reads and writes require distinct restricted profiles.");
    }

    public Task<MappingDraftBase?> SelectBaseAsync(ActorContext actor, MappingScope mapping, Guid revisionId, bool requireHead, CancellationToken cancellationToken = default) =>
        _reader.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT * FROM braid.draft_base($1,$2,$3,$4,$5,$6)", connection, transaction);
            Scope(command, actor, mapping, revisionId); command.Parameters.AddWithValue(requireHead);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var revision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, revisionId);
            return new MappingDraftBase(revision, reader.GetInt64(reader.GetOrdinal("sequence")), new(
                PrivateObjectId.Restore(revision, reader.GetGuid(reader.GetOrdinal("native_object_id")), PrivateObjectClass.NativeArtifact),
                Sha256Digest.Parse(reader.GetString(reader.GetOrdinal("native_digest"))), reader.GetInt64(reader.GetOrdinal("native_byte_count"))));
        }, cancellationToken);

    public Task<IReadOnlyList<MappingDraftSnapshot>?> ListAsync(ActorContext actor, MappingScope mapping, CancellationToken cancellationToken = default) =>
        _reader.RunAsync<IReadOnlyList<MappingDraftSnapshot>?>(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT * FROM braid.draft_list_admitted($1,$2,$3,$4)", connection, transaction);
            command.Parameters.AddWithValue(mapping.OrganizationId); command.Parameters.AddWithValue(mapping.ProjectId);
            command.Parameters.AddWithValue(mapping.MappingId); command.Parameters.AddWithValue(actor.PrincipalId.Value);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true) return null;
            var snapshots = await ReadCoreAsync(connection, transaction, actor, mapping, null, cancellationToken);
            return snapshots.Where(draft => draft.State is MappingDraftState.Open or MappingDraftState.Saving).ToArray();
        }, cancellationToken);

    public Task<MappingDraftSnapshot?> ReadAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default) =>
        _reader.RunAsync(false, async (connection, transaction) => (await ReadCoreAsync(connection, transaction, actor, mapping, id, cancellationToken)).SingleOrDefault(), cancellationToken);

    public Task<MappingEditScript?> ReadOperationsAsync(ActorContext actor, MappingScope mapping, Guid id, CancellationToken cancellationToken = default) =>
        _reader.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT operations,operations_digest,operation_count FROM braid.draft_read($1,$2,$3,$4,$5) WHERE state IN ('open','saving')", connection, transaction);
            Scope(command, actor, mapping, id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var operations = MappingEditScript.Parse(reader.GetFieldValue<byte[]>(0));
            if (operations.Digest.Value != reader.GetString(1) || operations.Count != reader.GetInt32(2)) throw Inconsistent();
            return operations;
        }, cancellationToken);

    public Task<MappingEditCheckpoint?> ReadCheckpointAsync(ActorContext actor, MappingScope mapping, Guid id, long version, CancellationToken cancellationToken = default) =>
        _reader.RunAsync(false, async (connection, transaction) =>
        {
            var draft = (await ReadCoreAsync(connection, transaction, actor, mapping, id, cancellationToken)).SingleOrDefault();
            if (draft is null || draft.Version != version) return null;
            await using var command = new NpgsqlCommand("SELECT * FROM braid.draft_checkpoint_read($1,$2,$3,$4,$5,$6)", connection, transaction);
            Scope(command, actor, mapping, id); command.Parameters.AddWithValue(version);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var native = NativeArtifact.Admit(draft.Base.Revision, reader.GetFieldValue<byte[]>(reader.GetOrdinal("native")), MappingEditPolicy.MaximumNativeBytes);
            if (native.Digest.Value != reader.GetString(reader.GetOrdinal("native_digest"))) throw Inconsistent();
            var validation = JsonSerializer.Deserialize<ValidationReportV1>(reader.GetFieldValue<byte[]>(reader.GetOrdinal("validation_report")), Json) ?? throw Inconsistent();
            var projection = JsonSerializer.Deserialize<ProjectionReportV1>(reader.GetFieldValue<byte[]>(reader.GetOrdinal("projection_report")), Json) ?? throw Inconsistent();
            var valid = reader.GetBoolean(reader.GetOrdinal("is_valid"));
            var profile = new IngestionBackendProfile("ferrule", reader.GetString(reader.GetOrdinal("engine_revision")), reader.GetString(reader.GetOrdinal("worker_build")), reader.GetString(reader.GetOrdinal("adapter_build")));
            var facts = new IngestionValidationFacts(valid ? IngestionBackendStatus.Succeeded : IngestionBackendStatus.InvalidMapping,
                new(native.Scope, native.Digest, profile, true), valid, reader.GetInt32(reader.GetOrdinal("total_issue_count")), validation.EmittedIssueCount, validation.Truncated);
            var evidence = IngestionEvidenceDocument.Restore(facts, reader.GetFieldValue<byte[]>(reader.GetOrdinal("validation_evidence")));
            if (validation.Provenance?.NativeArtifactDigest.Value != native.Digest.Value || projection.Document?.Provenance.NativeArtifactDigest.Value != native.Digest.Value) throw Inconsistent();
            return new MappingEditCheckpoint(native, evidence, validation, projection);
        }, cancellationToken);

    private static async Task<IReadOnlyList<MappingDraftSnapshot>> ReadCoreAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ActorContext actor, MappingScope mapping, Guid? id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT d.*,c.version AS checkpoint_version,c.native_digest AS checkpoint_digest,c.native_byte_count AS checkpoint_bytes,
                c.is_valid AS checkpoint_valid,c.total_issue_count AS checkpoint_issues
            FROM braid.draft_read($1,$2,$3,$4,$5) d
            LEFT JOIN LATERAL braid.draft_checkpoint_summary($1,$2,$3,d.id,$5) c ON true
            """, connection, transaction);
        Scope(command, actor, mapping, id);
        var snapshots = new List<MappingDraftSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            int At(string name) => reader.GetOrdinal(name);
            Guid? OptionalId(string name) => reader.IsDBNull(At(name)) ? null : reader.GetGuid(At(name));
            string? OptionalText(string name) => reader.IsDBNull(At(name)) ? null : reader.GetString(At(name));
            var revision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(At("base_revision_id")));
            var immutableBase = new MappingDraftBase(revision, reader.GetInt64(At("base_sequence")), new(
                PrivateObjectId.Restore(revision, reader.GetGuid(At("base_native_object_id")), PrivateObjectClass.NativeArtifact),
                Sha256Digest.Parse(reader.GetString(At("base_digest"))), reader.GetInt64(At("base_native_byte_count"))));
            var checkpoint = reader.IsDBNull(At("checkpoint_version")) ? null : new MappingDraftCheckpointSummary(reader.GetInt64(At("checkpoint_version")),
                Sha256Digest.Parse(reader.GetString(At("checkpoint_digest"))), reader.GetInt32(At("checkpoint_bytes")), reader.GetBoolean(At("checkpoint_valid")), reader.GetInt32(At("checkpoint_issues")));
            snapshots.Add(new(reader.GetGuid(At("id")), mapping, new(reader.GetGuid(At("author_principal_id"))), immutableBase,
                reader.GetString(At("state")) switch { "open" => MappingDraftState.Open, "saving" => MappingDraftState.Saving, "saved" => MappingDraftState.Saved, "discarded" => MappingDraftState.Discarded, "expired" => MappingDraftState.Expired, _ => throw Inconsistent() },
                reader.GetInt64(At("version")), reader.GetInt32(At("operation_count")), OptionalText("operations_digest") is { } digest ? Sha256Digest.Parse(digest) : null,
                reader.GetFieldValue<DateTimeOffset>(At("created_at")), reader.GetFieldValue<DateTimeOffset>(At("changed_at")), reader.GetFieldValue<DateTimeOffset>(At("expires_at")),
                checkpoint, OptionalId("saving_key"), OptionalText("saving_filename"), OptionalId("saving_operation_id"), OptionalId("saved_revision_id")));
        }
        return snapshots;
    }

    private static void Scope(NpgsqlCommand command, ActorContext actor, MappingScope mapping, Guid? id)
    {
        command.Parameters.AddWithValue(mapping.OrganizationId); command.Parameters.AddWithValue(mapping.ProjectId); command.Parameters.AddWithValue(mapping.MappingId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id is { } value ? value : DBNull.Value });
        command.Parameters.AddWithValue(actor.PrincipalId.Value);
    }
    private static InvalidOperationException Inconsistent() => new("The stored draft facts are inconsistent.");
}
