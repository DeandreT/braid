using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore : IExportReviewStore
{
    private const string ExportAuthoritySql = """
        WITH authorized AS MATERIALIZED (
            SELECT r.organization_id, r.project_id, r.mapping_id, r.id AS revision_id
            FROM braid.mapping_revisions r
            JOIN braid.principals p ON p.id=@actorId AND p.active
            JOIN braid.organization_memberships a ON a.organization_id=r.organization_id AND a.principal_id=p.id AND a.active
            JOIN braid.project_memberships m ON m.organization_id=r.organization_id AND m.project_id=r.project_id
                AND m.principal_id=p.id AND m.active AND m.role IN ('viewer','runner','publisher','administrator')
            WHERE r.organization_id=@organizationId AND r.project_id=@projectId
                AND r.mapping_id=@mappingId AND r.id=@revisionId
        )
        """;

    public async Task SaveAsync(ActorContext actor, ExportReview review, CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(async () =>
        {
            if (review.Actor != actor.PrincipalId) throw new ArgumentException("Export review actor mismatch.");
            var bytes = ExportReviewCodec.Encode(review);
            await using var transaction = await _database.BeginTransactionAsync(review.Scope.OrganizationId, cancellationToken);
            await using var command = transaction.CreateCommand(ExportAuthoritySql + """
                INSERT INTO braid.export_reviews
                  (organization_id,project_id,mapping_id,revision_id,id,actor_principal_id,expires_at,review_digest,payload)
                SELECT organization_id,project_id,mapping_id,revision_id,@id,@actorId,@expires,@digest,@payload FROM authorized
                RETURNING id
                """);
            AddExportScope(command, actor, review.Scope);
            Add(command, "id", NpgsqlDbType.Uuid, review.Id);
            Add(command, "expires", NpgsqlDbType.TimestampTz, review.ExpiresAt);
            Add(command, "digest", NpgsqlDbType.Text, review.Digest.Value);
            Add(command, "payload", NpgsqlDbType.Bytea, bytes);
            if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
                throw new UnauthorizedAccessException("Export review is unavailable.");
            await transaction.CommitAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<ExportReview?> FindExportAsync(ActorContext actor, RevisionScope scope, Guid id,
        CancellationToken cancellationToken = default) => ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
            await using var command = transaction.CreateCommand(ExportAuthoritySql + """
                SELECT review.payload,review.review_digest,review.expires_at FROM braid.export_reviews review
                JOIN authorized USING (organization_id,project_id,mapping_id,revision_id)
                WHERE review.id=@id AND review.actor_principal_id=@actorId
                """);
            AddExportScope(command, actor, scope);
            Add(command, "id", NpgsqlDbType.Uuid, id);
            ExportReview? result = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken))
                {
                    result = ExportReviewCodec.Decode(reader.GetFieldValue<byte[]>(0));
                    if (result.Id != id || result.Actor != actor.PrincipalId || result.Scope != scope ||
                        result.Digest != Sha256Digest.Parse(reader.GetString(1)) || result.ExpiresAt != reader.GetFieldValue<DateTimeOffset>(2))
                        throw InvalidMetadata();
                }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);

    private static void AddExportScope(Npgsql.NpgsqlCommand command, ActorContext actor, RevisionScope scope)
    {
        AddScope(command, actor, new(scope.OrganizationId, scope.ProjectId));
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        Add(command, "revisionId", NpgsqlDbType.Uuid, scope.RevisionId);
    }
}
