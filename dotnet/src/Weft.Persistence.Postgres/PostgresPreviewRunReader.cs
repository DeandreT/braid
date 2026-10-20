using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Submitter-only preview reads through the reader profile's owner-defined functions.</summary>
public sealed class PostgresPreviewRunReader(PostgresPreviewDatabase database) : IPreviewRunReader
{
    private readonly PostgresPreviewDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<PreviewRunSnapshot?> ReadAsync(ActorContext actor, Guid organizationId, Guid runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            PreviewRunSnapshot? snapshot;
            await using (var command = new NpgsqlCommand("""
                SELECT project_id, mapping_id, revision_id, request_key, request_fingerprint, current_date_time, state, attempt,
                    max_attempts, cancel_requested, outcome, failure_code, failure_message, failure_message_truncated,
                    failure_subject, failure_subject_truncated, provenance_confirmed, engine_revision, backend_build,
                    adapter_build, profile, records_written, created_at, started_at, finished_at, expires_at
                FROM weft.preview_read($1,$2,$3)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(organizationId);
                command.Parameters.AddWithValue(runId);
                command.Parameters.AddWithValue(actor.PrincipalId.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return null;
                var state = reader.GetInt32(6) switch
                {
                    1 => PreviewRunState.Queued,
                    2 => PreviewRunState.Running,
                    3 => PreviewRunState.Completed,
                    4 => PreviewRunState.Canceled,
                    _ => throw PostgresPreviewRunStore.Inconsistent()
                };
                var failure = reader.IsDBNull(11) ? null : new MappingPreviewFailure(reader.GetString(11), reader.GetString(12),
                    reader.GetBoolean(13), reader.IsDBNull(14) ? null : reader.GetString(14), !reader.IsDBNull(15) && reader.GetBoolean(15));
                var provenance = reader.IsDBNull(16) ? null : new PreviewRunProvenance(reader.GetBoolean(16), reader.GetString(17),
                    reader.GetString(18), reader.GetString(19), reader.GetString(20));
                snapshot = new PreviewRunSnapshot(runId, new RevisionScope(organizationId, reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)),
                    reader.GetGuid(3), reader.GetString(4), reader.GetString(5), state, reader.GetInt32(7), reader.GetInt32(8),
                    reader.GetBoolean(9), reader.IsDBNull(10) ? null : PostgresPreviewRunStore.Outcome(reader.GetString(10)), failure,
                    provenance, reader.IsDBNull(21) ? null : reader.GetInt64(21), [], reader.GetFieldValue<DateTimeOffset>(22),
                    reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23),
                    reader.IsDBNull(24) ? null : reader.GetFieldValue<DateTimeOffset>(24), reader.GetFieldValue<DateTimeOffset>(25));
            }
            var outputs = new List<PreviewOutputDescriptor>();
            await using (var command = new NpgsqlCommand("""
                SELECT ordinal, boundary_id, target_name, target_name_truncated, media_type, records_written, digest, byte_count
                FROM weft.preview_outputs($1,$2,$3)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(organizationId);
                command.Parameters.AddWithValue(runId);
                command.Parameters.AddWithValue(actor.PrincipalId.Value);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    outputs.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4),
                        reader.GetInt64(5), Sha256Digest.Parse(reader.GetString(6)), reader.GetInt32(7)));
            }
            if (outputs.Select((output, index) => output.Ordinal != index).Any(mismatch => mismatch)
                || (snapshot.Outcome == PreviewRunOutcome.Succeeded) != (outputs.Count > 0))
                throw PostgresPreviewRunStore.Inconsistent();
            return snapshot with { Outputs = outputs };
        }, cancellationToken);
    }

    public Task<PreviewOutputContent?> ReadOutputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT digest, content FROM weft.preview_output($1,$2,$3,$4)", connection, transaction);
            command.Parameters.AddWithValue(organizationId);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            command.Parameters.AddWithValue(ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var digest = Sha256Digest.Parse(reader.GetString(0));
            var content = reader.GetFieldValue<byte[]>(1);
            if (Sha256Digest.FromBytes(content) != digest) throw PostgresPreviewRunStore.Inconsistent();
            return (PreviewOutputContent?)new PreviewOutputContent(digest, content);
        }, cancellationToken);
    }
}
