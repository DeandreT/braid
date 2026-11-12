using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Submitter-only preview reads through the reader profile's owner-defined functions.</summary>
public sealed class PostgresPreviewRunReader(PostgresPreviewDatabase database) : IPreviewRunReader
{
    private readonly PostgresPreviewDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<DraftPreviewSubject?> ReadDraftSubjectAsync(ActorContext actor, MappingScope mapping, Guid requestKey,
        CancellationToken cancellationToken = default) => _database.RunAsync<DraftPreviewSubject?>(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT draft_id,draft_version,base_revision_id,native_digest,native_byte_count FROM weft.preview_draft_subject_by_key($1,$2,$3,$4,$5)", connection, transaction);
            command.Parameters.AddWithValue(mapping.OrganizationId); command.Parameters.AddWithValue(mapping.ProjectId); command.Parameters.AddWithValue(mapping.MappingId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value); command.Parameters.AddWithValue(requestKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? new(new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(2)),
                reader.GetGuid(0), reader.GetInt64(1), Sha256Digest.Parse(reader.GetString(3)), reader.GetInt32(4)) : null;
        }, cancellationToken);

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
                var project = reader.GetGuid(0); var mapping = reader.GetGuid(1);
                // The subject query is needed only for a draft, so revision-only deployments
                // retain their existing read path while the staged migration is integrated.
                var revision = reader.IsDBNull(2) ? null : new RevisionScope(organizationId, project, mapping, reader.GetGuid(2));
                var key = reader.GetGuid(3); var fingerprint = reader.GetString(4); var clock = reader.GetString(5);
                var attempt = reader.GetInt32(7); var maximum = reader.GetInt32(8); var cancel = reader.GetBoolean(9);
                var outcome = reader.IsDBNull(10) ? (PreviewRunOutcome?)null : PostgresPreviewRunStore.Outcome(reader.GetString(10));
                var records = reader.IsDBNull(21) ? (long?)null : reader.GetInt64(21);
                var created = reader.GetFieldValue<DateTimeOffset>(22);
                var started = reader.IsDBNull(23) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(23);
                var finished = reader.IsDBNull(24) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(24);
                var expires = reader.GetFieldValue<DateTimeOffset>(25);
                PreviewSubject subject;
                if (revision is not null) subject = new RevisionPreviewSubject(revision);
                else
                {
                    await reader.DisposeAsync();
                    await using var subjectCommand = new NpgsqlCommand("SELECT draft_id,draft_version,base_revision_id,native_digest,native_byte_count FROM weft.preview_draft_subject($1,$2,$3)", connection, transaction);
                    subjectCommand.Parameters.AddWithValue(organizationId); subjectCommand.Parameters.AddWithValue(runId); subjectCommand.Parameters.AddWithValue(actor.PrincipalId.Value);
                    await using var draft = await subjectCommand.ExecuteReaderAsync(cancellationToken);
                    if (!await draft.ReadAsync(cancellationToken)) throw PostgresPreviewRunStore.Inconsistent();
                    subject = new DraftPreviewSubject(new(organizationId, project, mapping, draft.GetGuid(2)), draft.GetGuid(0), draft.GetInt64(1),
                        Sha256Digest.Parse(draft.GetString(3)), draft.GetInt32(4));
                }
                snapshot = new PreviewRunSnapshot(runId, subject,
                    key, fingerprint, clock, state, attempt, maximum, cancel, outcome, failure, provenance, records, [], created, started, finished, expires);
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
            for (var index = 0; index < outputs.Count; index++)
                if (CsvDocumentPolicy.AdmitsMediaType(outputs[index].MediaType))
                    outputs[index] = outputs[index] with
                    {
                        CsvDialect = await ReadCsvDialectAsync(connection, transaction,
                        actor, organizationId, runId, outputs[index].Ordinal, outputs[index].MediaType, cancellationToken)
                    };
            if (outputs.Select((output, index) => output.Ordinal != index).Any(mismatch => mismatch)
                || (snapshot.Outcome == PreviewRunOutcome.Succeeded) != (outputs.Count > 0))
                throw PostgresPreviewRunStore.Inconsistent();
            return snapshot with { Outputs = outputs };
        }, cancellationToken);
    }

    public Task<PreviewOutputContent?> ReadOutputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default) => ReadDocumentAsync(actor, organizationId, runId, ordinal, "preview_output", cancellationToken);

    public Task<PreviewOutputContent?> ReadInputAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        CancellationToken cancellationToken = default) => ReadDocumentAsync(actor, organizationId, runId, ordinal, "preview_input", cancellationToken);

    public Task<PreviewRunPage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterRunId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT run_id, revision_id, state, outcome, created_at, finished_at, expires_at
                FROM weft.preview_history($1,$2,$3,$4,$5,$6)
                """, connection, transaction);
            command.Parameters.AddWithValue(mapping.OrganizationId);
            command.Parameters.AddWithValue(mapping.ProjectId);
            command.Parameters.AddWithValue(mapping.MappingId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = afterRunId is { } after ? after : DBNull.Value });
            command.Parameters.AddWithValue(limit + 1);
            var runs = new List<PreviewRunSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                runs.Add(new(reader.GetGuid(0), new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(1)),
                    State(reader.GetInt32(2)), reader.IsDBNull(3) ? null : PostgresPreviewRunStore.Outcome(reader.GetString(3)),
                    reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            Guid? next = null;
            if (runs.Count > limit) { runs.RemoveAt(limit); next = runs[^1].RunId; }
            return new PreviewRunPage(runs.AsReadOnly(), next);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PreviewInputDescriptor>?> ReadInputDescriptorsAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT ordinal, name, digest, byte_count FROM weft.preview_input_documents($1,$2,$3)",
                connection, transaction);
            command.Parameters.AddWithValue(organizationId);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            var inputs = new List<PreviewInputDescriptor>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                inputs.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), Sha256Digest.Parse(reader.GetString(2)),
                    reader.GetInt32(3)));
            if (inputs.Count == 0) return null;
            if (inputs.Select((input, index) => input.Ordinal != index || (index == 0) != (input.Name is null)).Any(mismatch => mismatch))
                throw PostgresPreviewRunStore.Inconsistent();
            return (IReadOnlyList<PreviewInputDescriptor>?)inputs.AsReadOnly();
        }, cancellationToken);
    }

    private static PreviewRunState State(int value) => value switch
    {
        1 => PreviewRunState.Queued,
        2 => PreviewRunState.Running,
        3 => PreviewRunState.Completed,
        4 => PreviewRunState.Canceled,
        _ => throw PostgresPreviewRunStore.Inconsistent()
    };

    private Task<PreviewOutputContent?> ReadDocumentAsync(ActorContext actor, Guid organizationId, Guid runId, int ordinal,
        string function, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            var sql = function == "preview_output"
                ? "SELECT b.digest,b.content,m.media_type FROM weft.preview_output($1,$2,$3,$4) b JOIN weft.preview_outputs($1,$2,$3) m ON m.ordinal=$4"
                : "SELECT digest,content,'application/json'::text FROM weft.preview_input($1,$2,$3,$4)";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(organizationId);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            command.Parameters.AddWithValue(ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var digest = Sha256Digest.Parse(reader.GetString(0));
            var content = reader.GetFieldValue<byte[]>(1);
            if (Sha256Digest.FromBytes(content) != digest) throw PostgresPreviewRunStore.Inconsistent();
            var mediaType = reader.GetString(2);
            await reader.DisposeAsync();
            CsvDocumentDialect? dialect = null;
            if (function == "preview_output" && CsvDocumentPolicy.AdmitsMediaType(mediaType))
                dialect = await ReadCsvDialectAsync(connection, transaction, actor, organizationId, runId, ordinal, mediaType, cancellationToken);
            else if (function == "preview_input")
            {
                // Prefix-33 input reads predate CSV metadata. Admit only this exact additive
                // reader when installed; a missing or faulty CSV output reader never falls back.
                await using var installed = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM pg_catalog.pg_attribute
                            WHERE attrelid=to_regclass('weft.preview_documents') AND attnum>0 AND NOT attisdropped
                            AND ((attname='csv_input' AND atttypid='boolean'::regtype AND attnotnull)
                                OR (attname='csv_dialect' AND atttypid='jsonb'::regtype AND NOT attnotnull))),
                        to_regprocedure('weft.preview_csv_input(uuid,uuid,uuid,integer)') IS NOT NULL
                    """, connection, transaction);
                bool csvInstalled;
                await using (var admission = await installed.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await admission.ReadAsync(cancellationToken)) throw PostgresPreviewRunStore.Inconsistent();
                    var columns = admission.GetInt64(0);
                    csvInstalled = admission.GetBoolean(1);
                    if (columns != (csvInstalled ? 2 : 0))
                        throw PostgresPreviewRunStore.Inconsistent();
                }
                if (csvInstalled)
                {
                    await using var metadata = new NpgsqlCommand("SELECT media_type,csv_dialect::text FROM weft.preview_csv_input($1,$2,$3,$4)", connection, transaction);
                    metadata.Parameters.AddWithValue(organizationId); metadata.Parameters.AddWithValue(runId);
                    metadata.Parameters.AddWithValue(actor.PrincipalId.Value); metadata.Parameters.AddWithValue(ordinal);
                    await using var values = await metadata.ExecuteReaderAsync(cancellationToken);
                    if (!await values.ReadAsync(cancellationToken)) throw PostgresPreviewRunStore.Inconsistent();
                    mediaType = values.GetString(0);
                    dialect = values.IsDBNull(1) ? null : CsvDialectJson.Decode(values.GetString(1));
                    if (await values.ReadAsync(cancellationToken)) throw PostgresPreviewRunStore.Inconsistent();
                }
            }
            if (CsvDocumentPolicy.AdmitsMediaType(mediaType) ? !CsvDocumentPolicy.Admits(dialect, mediaType)
                : !DocumentPreviewPolicy.AdmitsMediaType(mediaType) || dialect is not null) throw PostgresPreviewRunStore.Inconsistent();
            return (PreviewOutputContent?)new PreviewOutputContent(digest, content) { MediaType = mediaType, CsvDialect = dialect };
        }, cancellationToken);
    }

    private static async Task<CsvDocumentDialect> ReadCsvDialectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ActorContext actor, Guid organizationId, Guid runId, int ordinal, string mediaType, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT csv_dialect::text FROM weft.preview_csv_outputs($1,$2,$3) WHERE ordinal=$4", connection, transaction);
        command.Parameters.AddWithValue(organizationId); command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(actor.PrincipalId.Value); command.Parameters.AddWithValue(ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw PostgresPreviewRunStore.Inconsistent();
        var dialect = CsvDialectJson.Decode(reader.GetString(0));
        if (!CsvDocumentPolicy.Admits(dialect, mediaType) || await reader.ReadAsync(token)) throw PostgresPreviewRunStore.Inconsistent();
        return dialect;
    }
}
