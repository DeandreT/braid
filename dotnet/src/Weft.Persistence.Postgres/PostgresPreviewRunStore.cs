using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Preview run transitions through the writer profile's owner-defined functions only.</summary>
public sealed class PostgresPreviewRunStore(PostgresPreviewDatabase database) : IPreviewRunStore
{
    private readonly PostgresPreviewDatabase _database = Writer(database);

    public Task<PreviewSubmitResult> SubmitAsync(ActorContext actor, PreviewRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(record);
        var submission = record.Submission;
        var inputs = submission.NamedInputs.Prepend(submission.Primary).ToArray();
        return _database.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT outcome, run_id FROM weft.preview_submit($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """, connection, transaction);
            var revision = submission.Revision;
            command.Parameters.AddWithValue(revision.OrganizationId);
            command.Parameters.AddWithValue(revision.ProjectId);
            command.Parameters.AddWithValue(revision.MappingId);
            command.Parameters.AddWithValue(revision.RevisionId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            command.Parameters.AddWithValue(submission.RequestKey);
            command.Parameters.AddWithValue(record.RequestDocument);
            command.Parameters.AddWithValue(record.CurrentDateTime);
            command.Parameters.AddWithValue(record.MaxAttempts);
            command.Parameters.AddWithValue(checked((int)record.Retention.TotalSeconds));
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = inputs.Select(input => input.Name).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = inputs.Select(input => input.CopyBytes()).ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw Inconsistent();
            var outcome = reader.GetString(0) switch
            {
                "created" => PreviewSubmitOutcome.Created,
                "replayed" => PreviewSubmitOutcome.Replayed,
                "conflict" => PreviewSubmitOutcome.Conflict,
                "notAuthorizedOrNotFound" => PreviewSubmitOutcome.NotAuthorizedOrNotFound,
                _ => throw Inconsistent()
            };
            Guid? run = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            if ((run is null) != (outcome is PreviewSubmitOutcome.Conflict or PreviewSubmitOutcome.NotAuthorizedOrNotFound)) throw Inconsistent();
            return new PreviewSubmitResult(outcome, run);
        }, cancellationToken);
    }

    public Task<PreviewClaimResult> ClaimAsync(Guid organizationId, Guid runId, Guid owner, TimeSpan lease,
        CancellationToken cancellationToken = default) => _database.RunAsync(true, async (connection, transaction) =>
    {
        await using var command = new NpgsqlCommand("""
            SELECT outcome, attempt, lease_token, lease_expires_at, project_id, mapping_id, revision_id, submitter_id,
                request_document, current_date_time, native_object_id, native_digest, native_byte_count
            FROM weft.preview_claim($1,$2,$3,$4)
            """, connection, transaction);
        command.Parameters.AddWithValue(organizationId);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(owner);
        command.Parameters.AddWithValue(checked((int)lease.TotalSeconds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Inconsistent();
        var outcome = reader.GetString(0) switch
        {
            "claimed" => PreviewClaimOutcome.Claimed,
            "busy" => PreviewClaimOutcome.Busy,
            "finished" => PreviewClaimOutcome.Finished,
            "missing" => PreviewClaimOutcome.Missing,
            _ => throw Inconsistent()
        };
        if (outcome != PreviewClaimOutcome.Claimed) return new PreviewClaimResult(outcome, null);
        var revision = new RevisionScope(organizationId, reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6));
        var native = new PrivateObjectReference(PrivateObjectId.Restore(revision, reader.GetGuid(10), PrivateObjectClass.NativeArtifact),
            Sha256Digest.Parse(reader.GetString(11)), reader.GetInt64(12));
        return new PreviewClaimResult(outcome, new PreviewRunClaim(organizationId, runId, reader.GetInt32(1), reader.GetGuid(2),
            reader.GetFieldValue<DateTimeOffset>(3), revision, new PrincipalId(reader.GetGuid(7)), reader.GetString(8),
            reader.GetString(9), native));
    }, cancellationToken);

    public Task<IReadOnlyList<PreviewStoredInput>> ReadInputsAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT ordinal, name, digest, content FROM weft.preview_inputs($1,$2,$3)",
                connection, transaction);
            command.Parameters.AddWithValue(claim.OrganizationId);
            command.Parameters.AddWithValue(claim.RunId);
            command.Parameters.AddWithValue(claim.LeaseToken);
            var inputs = new List<PreviewStoredInput>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                inputs.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), Sha256Digest.Parse(reader.GetString(2)),
                    reader.GetFieldValue<byte[]>(3)));
            return (IReadOnlyList<PreviewStoredInput>)inputs;
        }, cancellationToken);
    }

    public Task<PreviewClaimObservation> ObserveAsync(PreviewRunClaim claim, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return _database.RunAsync(false, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT claim_current, cancel_requested FROM weft.preview_observe($1,$2,$3)",
                connection, transaction);
            command.Parameters.AddWithValue(claim.OrganizationId);
            command.Parameters.AddWithValue(claim.RunId);
            command.Parameters.AddWithValue(claim.LeaseToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw Inconsistent();
            return new PreviewClaimObservation(reader.GetBoolean(0), reader.GetBoolean(1));
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(PreviewRunClaim claim, PreviewRunCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(completion);
        if ((completion.Outcome == PreviewRunOutcome.Succeeded) != (completion.Artifacts.Count > 0)
            || (completion.Outcome == PreviewRunOutcome.Succeeded && completion.Provenance is null))
            throw new ArgumentException("Only a succeeded completion publishes outputs, and it requires provenance.", nameof(completion));
        return _database.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT weft.preview_complete($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
                """, connection, transaction);
            var failure = completion.Failure;
            var provenance = completion.Provenance;
            var artifacts = completion.Artifacts;
            command.Parameters.AddWithValue(claim.OrganizationId);
            command.Parameters.AddWithValue(claim.RunId);
            command.Parameters.AddWithValue(claim.LeaseToken);
            command.Parameters.AddWithValue(OutcomeText(completion.Outcome));
            Nullable(command, NpgsqlDbType.Text, failure?.Code);
            Nullable(command, NpgsqlDbType.Text, failure?.Message);
            Nullable(command, NpgsqlDbType.Boolean, failure?.MessageTruncated);
            Nullable(command, NpgsqlDbType.Text, failure?.Subject);
            Nullable(command, NpgsqlDbType.Boolean, failure is null || failure.Subject is null ? null : failure.SubjectTruncated);
            Nullable(command, NpgsqlDbType.Boolean, provenance?.Confirmed);
            Nullable(command, NpgsqlDbType.Text, provenance?.EngineRevision);
            Nullable(command, NpgsqlDbType.Text, provenance?.BackendBuild);
            Nullable(command, NpgsqlDbType.Text, provenance?.AdapterBuild);
            Nullable(command, NpgsqlDbType.Text, provenance?.Profile);
            command.Parameters.AddWithValue(completion.RecordsWritten);
            Array(command, NpgsqlDbType.Text, artifacts.Select(artifact => artifact.BoundaryId).ToArray());
            Array(command, NpgsqlDbType.Text, artifacts.Select(artifact => artifact.TargetName).ToArray());
            Array(command, NpgsqlDbType.Boolean, artifacts.Select(artifact => artifact.TargetNameTruncated).ToArray());
            Array(command, NpgsqlDbType.Text, artifacts.Select(artifact => artifact.MediaType).ToArray());
            Array(command, NpgsqlDbType.Bigint, artifacts.Select(artifact => artifact.RecordsWritten).ToArray());
            Array(command, NpgsqlDbType.Bytea, artifacts.Select(artifact => artifact.CopyBytes()).ToArray());
            return await command.ExecuteScalarAsync(cancellationToken) switch
            {
                "completed" => true,
                "claimLost" => false,
                _ => throw Inconsistent()
            };
        }, cancellationToken);
    }

    public Task<bool> ReleaseAsync(PreviewRunClaim claim, TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return _database.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT weft.preview_release($1,$2,$3,$4)", connection, transaction);
            command.Parameters.AddWithValue(claim.OrganizationId);
            command.Parameters.AddWithValue(claim.RunId);
            command.Parameters.AddWithValue(claim.LeaseToken);
            command.Parameters.AddWithValue(checked((int)Math.Ceiling(retryDelay.TotalSeconds)));
            return await command.ExecuteScalarAsync(cancellationToken) switch
            {
                "released" => true,
                "claimLost" => false,
                _ => throw Inconsistent()
            };
        }, cancellationToken);
    }

    public Task<PreviewCancelOutcome> CancelAsync(ActorContext actor, Guid organizationId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _database.RunAsync(true, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT weft.preview_cancel($1,$2,$3)", connection, transaction);
            command.Parameters.AddWithValue(organizationId);
            command.Parameters.AddWithValue(runId);
            command.Parameters.AddWithValue(actor.PrincipalId.Value);
            return await command.ExecuteScalarAsync(cancellationToken) switch
            {
                "canceled" => PreviewCancelOutcome.Canceled,
                "requested" => PreviewCancelOutcome.Requested,
                "finished" => PreviewCancelOutcome.Finished,
                "notAuthorizedOrNotFound" => PreviewCancelOutcome.NotAuthorizedOrNotFound,
                _ => throw Inconsistent()
            };
        }, cancellationToken);
    }

    internal static string OutcomeText(PreviewRunOutcome outcome)
    {
        var name = outcome.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    internal static PreviewRunOutcome Outcome(string text) =>
        Enum.GetValues<PreviewRunOutcome>().Single(outcome => OutcomeText(outcome) == text);

    private static void Nullable(NpgsqlCommand command, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });

    private static void Array<T>(NpgsqlCommand command, NpgsqlDbType type, T[] values) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | type, Value = values });

    private static PostgresPreviewDatabase Writer(PostgresPreviewDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Profile != PreviewDatabaseProfile.Writer)
            throw new ArgumentException("The preview run store requires the writer profile.", nameof(database));
        return database;
    }

    internal static InvalidOperationException Inconsistent() => new("The preview store returned an inconsistent result.");
}
