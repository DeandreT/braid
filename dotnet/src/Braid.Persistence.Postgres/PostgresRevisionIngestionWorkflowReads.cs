using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class PostgresRevisionIngestionStore : IIngestionWorkflowReader
{
    public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project,
        IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => FindOwnReceiptCoreAsync(actor, project, kind, target, idempotencyKey,
            cancellationToken), cancellationToken);

    public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetAppendTargetCoreAsync(actor, target, cancellationToken), cancellationToken);

    private async Task<IngestionReceipt?> FindOwnReceiptCoreAsync(ActorContext actor, ProjectScope project,
        IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        RequireId(idempotencyKey, nameof(idempotencyKey));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "The ingestion operation kind must be known.");
        if ((kind == IngestionOperationKind.Create && target is not null) ||
            (kind == IngestionOperationKind.Append && target is null))
            throw new ArgumentException("The receipt key must include a target exactly for append.", nameof(target));
        if (target is not null && (target.OrganizationId != project.OrganizationId || target.ProjectId != project.ProjectId))
            throw new ArgumentException("The append target must belong to the complete project scope.", nameof(target));

        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        var receipt = await ReadReceiptQueryAsync(transaction, actor, project, """
            AND ingestion.idempotency_key = @key AND ingestion.operation_kind = @kind
            AND ingestion.target_mapping_id IS NOT DISTINCT FROM @target
            """, command =>
        {
            Add(command, "key", NpgsqlDbType.Uuid, idempotencyKey);
            Add(command, "kind", NpgsqlDbType.Text, kind == IngestionOperationKind.Create ? "create" : "append");
            Add(command, "target", NpgsqlDbType.Uuid, target?.MappingId);
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private async Task<IngestionAppendTarget?> GetAppendTargetCoreAsync(ActorContext actor, MappingScope target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        var project = new ProjectScope(target.OrganizationId, target.ProjectId);
        await using var transaction = await _database.BeginTransactionAsync(project.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(project.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedSql + """
            SELECT mapping.organization_id, mapping.project_id, mapping.id, mapping.backend_id
            FROM braid.mappings mapping JOIN authorized USING (organization_id, project_id)
            WHERE mapping.id = @mappingId
            """);
        AddScope(command, actor, project);
        Add(command, "mappingId", NpgsqlDbType.Uuid, target.MappingId);
        IngestionAppendTarget? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    var scope = new MappingScope(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2));
                    if (scope != target)
                        throw InvalidMetadata();
                    result = new IngestionAppendTarget(scope, reader.GetString(3));
                }
                catch (ArgumentException)
                {
                    throw InvalidMetadata();
                }
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
