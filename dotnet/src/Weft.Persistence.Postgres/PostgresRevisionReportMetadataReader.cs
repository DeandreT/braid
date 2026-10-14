using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using static Weft.Persistence.Postgres.RevisionReportPersistence;

namespace Weft.Persistence.Postgres;

/// <summary>Fresh full-scope report metadata snapshots through the dedicated read-only profile.</summary>
public sealed class PostgresRevisionReportMetadataReader : IRevisionReportMetadataReader
{
    private readonly PostgresRevisionReportDatabase _database;

    public PostgresRevisionReportMetadataReader(PostgresRevisionReportDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        return ExecuteAsync<AuthorizedRevisionReportSource?>(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
            var result = await ReadSourceAsync(transaction, actor, scope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    public Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteAsync<RevisionReportReadSnapshot?>(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(key.Scope.OrganizationId, cancellationToken);
            var row = await ReadCurrentAsync(transaction, actor, key.Scope, key.Operation, key.Fingerprint.Value, null, cancellationToken);
            var result = row is null || row.Source.Source != key.Source ? null : Read(() => row.Snapshot(key));
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    public Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
        RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        _ = Operation(operation);
        if (derivationId == Guid.Empty) throw new ArgumentException("A derivation identifier cannot be empty.", nameof(derivationId));
        return ExecuteAsync<AuthorizedRevisionReportAttempt?>(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
            await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
            await using var command = transaction.CreateCommand(AuthorizedSourceSql + """
                SELECT observed.observed_at,to_jsonb(source)::text AS source_row,
                    to_jsonb(entry)::text AS entry_row,to_jsonb(attempt)::text AS attempt_row
                FROM authorized_source source CROSS JOIN observed
                JOIN weft.revision_derivation_attempts attempt ON attempt.organization_id=source.organization_id
                  AND attempt.project_id=source.project_id AND attempt.mapping_id=source.mapping_id
                  AND attempt.revision_id=source.id AND attempt.derivation_id=@derivationId
                JOIN weft.revision_derivations entry ON entry.organization_id=attempt.organization_id
                  AND entry.project_id=attempt.project_id AND entry.mapping_id=attempt.mapping_id
                  AND entry.revision_id=attempt.revision_id AND entry.cache_entry_id=attempt.cache_entry_id
                  AND entry.operation_kind=@operation
                """);
            AddScope(command, actor, scope);
            Add(command, "derivationId", NpgsqlDbType.Uuid, derivationId);
            Add(command, "operation", NpgsqlDbType.Text, Operation(operation));
            AuthorizedRevisionReportAttempt? result = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    result = Read(() =>
                    {
                        var authorized = new AuthorizedRevisionReportSource(Source(Parse(reader, "source_row"), "id"),
                            actor.PrincipalId, reader.GetFieldValue<DateTimeOffset>(0));
                        var entry = Entry(Parse(reader, "entry_row"));
                        var attempt = Attempt(Parse(reader, "attempt_row"), entry);
                        if (authorized.Source.Scope != scope || entry.Key.Source != authorized.Source || entry.Key.Operation != operation ||
                            attempt.Claim.DerivationId != derivationId) throw InvalidFacts();
                        return new AuthorizedRevisionReportAttempt(authorized, entry.Key, entry.Id, attempt.Summary, attempt.Result);
                    });
                    if (await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }
}
