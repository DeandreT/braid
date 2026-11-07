using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore : IImportReviewStore
{
    public async Task SaveAsync(ActorContext actor, ImportReview review, CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(async () =>
        {
            await SaveReviewCoreAsync(actor, review, cancellationToken);
            return true;
        }, cancellationToken);

    private async Task SaveReviewCoreAsync(ActorContext actor, ImportReview review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(review);
        if (review.Actor != actor.PrincipalId)
            throw new ArgumentException("The review must belong to the calling actor.", nameof(review));
        var project = review.Destination.Project;
        var payload = ImportReviewCodec.Encode(review);
        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedSql + """
            INSERT INTO weft.import_reviews
                (organization_id, project_id, id, actor_principal_id, expires_at, review_digest, payload)
            SELECT organization_id, project_id, @id, @actorId, @expires, @digest, @payload FROM authorized
            RETURNING id
            """);
        AddScope(command, actor, project);
        Add(command, "id", NpgsqlDbType.Uuid, review.Id);
        Add(command, "expires", NpgsqlDbType.TimestampTz, review.ExpiresAt);
        Add(command, "digest", NpgsqlDbType.Text, review.Digest.Value);
        Add(command, "payload", NpgsqlDbType.Bytea, payload);
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw new UnauthorizedAccessException("The review is unavailable.");
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<ImportReview?> FindAsync(ActorContext actor, ProjectScope project, Guid reviewId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => FindReviewCoreAsync(actor, project, reviewId, cancellationToken), cancellationToken);

    private async Task<ImportReview?> FindReviewCoreAsync(ActorContext actor, ProjectScope project, Guid reviewId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        RequireId(reviewId, nameof(reviewId));
        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedSql + """
            SELECT review.payload, review.review_digest, review.expires_at
            FROM weft.import_reviews review JOIN authorized USING (organization_id, project_id)
            WHERE review.id = @id AND review.actor_principal_id = @actorId
            """);
        AddScope(command, actor, project);
        Add(command, "id", NpgsqlDbType.Uuid, reviewId);
        ImportReview? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = ImportReviewCodec.Decode(reader.GetFieldValue<byte[]>(0));
                if (result.Id != reviewId || result.Actor != actor.PrincipalId || result.Destination.Project != project ||
                    result.Digest != Sha256Digest.Parse(reader.GetString(1)) || result.ExpiresAt != reader.GetFieldValue<DateTimeOffset>(2))
                    throw InvalidMetadata();
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
