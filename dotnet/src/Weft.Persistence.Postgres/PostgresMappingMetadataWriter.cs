using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Atomic fresh-authority metadata replacement, independent of revision-head admission.</summary>
public sealed class PostgresMappingMetadataWriter : IMappingMetadataWriter
{
    private readonly PostgresMappingMetadataDatabase _database;

    public PostgresMappingMetadataWriter(PostgresMappingMetadataDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<MappingMetadataWriteResult> ReplaceAsync(ActorContext actor, MappingScope scope,
        MappingMetadataVersion expectedVersion, MappingMetadataEdit edit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ReplaceCoreAsync(actor, scope, expectedVersion, edit, cancellationToken), cancellationToken);

    private const string ReplaceSql = """
        WITH authorized AS MATERIALIZED (
            SELECT project.organization_id, project.id AS project_id
            FROM weft.projects project
            JOIN weft.organizations organization ON organization.id = project.organization_id
            JOIN weft.principals principal ON principal.id = @actorId AND principal.active
            JOIN weft.organization_memberships admission
              ON admission.organization_id = project.organization_id AND admission.principal_id = principal.id AND admission.active
            JOIN weft.project_memberships membership
              ON membership.organization_id = project.organization_id AND membership.project_id = project.id
             AND membership.principal_id = principal.id AND membership.active
             AND membership.role IN ('publisher', 'administrator')
            WHERE project.organization_id = @organizationId AND project.id = @projectId
        ), locked AS MATERIALIZED (
            SELECT mapping.organization_id, mapping.project_id, mapping.id,
                mapping.name, mapping.description, mapping.version, mapping.updated_at
            FROM weft.mappings mapping JOIN authorized
              ON mapping.organization_id = authorized.organization_id AND mapping.project_id = authorized.project_id
            WHERE mapping.id = @mappingId
            FOR NO KEY UPDATE OF mapping
        ), decision AS MATERIALIZED (
            SELECT locked.*, CASE
                WHEN organization_id <> @expectedOrganizationId OR project_id <> @expectedProjectId
                  OR id <> @expectedMappingId OR version <> @expectedVersion THEN 'precondition_failed'
                WHEN name COLLATE "C" = @name::text COLLATE "C"
                  AND description COLLATE "C" = @description::text COLLATE "C" THEN 'unchanged'
                WHEN version = 9223372036854775807 THEN 'version_exhausted'
                ELSE 'updated' END AS outcome
            FROM locked
        ), changed AS (
            UPDATE weft.mappings mapping
            SET name = @name, description = @description,
                version = CASE WHEN decision.outcome = 'updated' AND decision.version < 9223372036854775807
                    THEN decision.version + 1 ELSE decision.version END,
                updated_at = greatest(decision.updated_at, clock_timestamp())
            FROM decision
            WHERE decision.outcome = 'updated'
              AND mapping.organization_id = decision.organization_id AND mapping.project_id = decision.project_id
              AND mapping.id = decision.id
            RETURNING mapping.organization_id, mapping.project_id, mapping.id, mapping.name, mapping.description, mapping.version
        ), reply AS MATERIALIZED (
            SELECT organization_id, project_id, id, name, description, version FROM changed
            UNION ALL
            SELECT organization_id, project_id, id, name, description, version FROM decision WHERE outcome = 'unchanged'
        )
        SELECT (SELECT count(*) FROM authorized) AS authorized_count,
            (SELECT count(*) FROM locked) AS locked_count,
            (SELECT count(*) FROM decision) AS decision_count,
            (SELECT count(*) FROM changed) AS changed_count,
            (SELECT count(*) FROM reply) AS reply_count,
            decision.outcome,
            decision.organization_id AS previous_organization_id, decision.project_id AS previous_project_id,
            decision.id AS previous_id, decision.name AS previous_name, decision.description AS previous_description,
            decision.version AS previous_version,
            reply.organization_id AS result_organization_id, reply.project_id AS result_project_id,
            reply.id AS result_id, reply.name AS result_name, reply.description AS result_description,
            reply.version AS result_version
        FROM (SELECT 1) observation LEFT JOIN decision ON true LEFT JOIN reply ON true
        """;

    private async Task<MappingMetadataWriteResult> ReplaceCoreAsync(ActorContext actor, MappingScope scope,
        MappingMetadataVersion expectedVersion, MappingMetadataEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await _database.BeginTransactionAsync(scope.OrganizationId, cancellationToken);
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(ReplaceSql);
        Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
        Add(command, "organizationId", NpgsqlDbType.Uuid, scope.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, scope.ProjectId);
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        Add(command, "expectedOrganizationId", NpgsqlDbType.Uuid, expectedVersion.Scope.OrganizationId);
        Add(command, "expectedProjectId", NpgsqlDbType.Uuid, expectedVersion.Scope.ProjectId);
        Add(command, "expectedMappingId", NpgsqlDbType.Uuid, expectedVersion.Scope.MappingId);
        Add(command, "expectedVersion", NpgsqlDbType.Bigint, expectedVersion.Version);
        Add(command, "name", NpgsqlDbType.Text, edit.Name);
        Add(command, "description", NpgsqlDbType.Text, edit.Description);
        Observation observation;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw InvalidMetadata();
            observation = Read(() => new Observation(Value<long>(reader, "authorized_count"),
                Value<long>(reader, "locked_count"), Value<long>(reader, "decision_count"),
                Value<long>(reader, "changed_count"), Value<long>(reader, "reply_count"),
                IsNull(reader, "outcome") ? null : Value<string>(reader, "outcome"),
                ReadMetadata(reader, "previous"), ReadMetadata(reader, "result")));
            if (await reader.ReadAsync(cancellationToken))
                throw InvalidMetadata();
        }
        var result = ValidateObservation(observation, scope, expectedVersion, edit);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static MappingMetadataWriteResult ValidateObservation(Observation observed, MappingScope scope,
        MappingMetadataVersion expected, MappingMetadataEdit edit)
    {
        if (observed.AuthorizedCount is < 0 or > 1 || observed.LockedCount is < 0 or > 1 ||
            observed.LockedCount > observed.AuthorizedCount || observed.DecisionCount != observed.LockedCount ||
            observed.ChangedCount is < 0 or > 1 || observed.ReplyCount is < 0 or > 1)
            throw InvalidMetadata();
        if (observed.LockedCount == 0)
        {
            if (observed.ChangedCount != 0 || observed.ReplyCount != 0 || observed.Outcome is not null ||
                observed.Previous is not null || observed.Metadata is not null)
                throw InvalidMetadata();
            return MappingMetadataWriteResult.WithoutMetadata(MappingMetadataWriteOutcome.Unavailable);
        }
        var previous = observed.Previous;
        if (previous is null || previous.Scope != scope)
            throw InvalidMetadata();
        var outcome = observed.Outcome switch
        {
            "precondition_failed" => MappingMetadataWriteOutcome.PreconditionFailed,
            "unchanged" => MappingMetadataWriteOutcome.Unchanged,
            "version_exhausted" => MappingMetadataWriteOutcome.VersionExhausted,
            "updated" => MappingMetadataWriteOutcome.Updated,
            _ => throw InvalidMetadata()
        };
        var expectedOutcome = expected.Scope != scope || expected.Version != previous.Version
            ? MappingMetadataWriteOutcome.PreconditionFailed
            : SameFields(previous, edit) ? MappingMetadataWriteOutcome.Unchanged
            : previous.Version == long.MaxValue ? MappingMetadataWriteOutcome.VersionExhausted
            : MappingMetadataWriteOutcome.Updated;
        if (outcome != expectedOutcome)
            throw InvalidMetadata();
        if (outcome is MappingMetadataWriteOutcome.Updated or MappingMetadataWriteOutcome.Unchanged)
        {
            var metadata = observed.Metadata;
            if (observed.ReplyCount != 1 || metadata is null || metadata.Scope != scope || !SameFields(metadata, edit))
                throw InvalidMetadata();
            if (outcome == MappingMetadataWriteOutcome.Updated)
            {
                if (observed.ChangedCount != 1 || previous.Version == long.MaxValue || metadata.Version != previous.Version + 1)
                    throw InvalidMetadata();
            }
            else if (observed.ChangedCount != 0 || metadata != previous)
                throw InvalidMetadata();
            return MappingMetadataWriteResult.ForMetadata(outcome, metadata);
        }
        if (observed.ChangedCount != 0 || observed.ReplyCount != 0 || observed.Metadata is not null)
            throw InvalidMetadata();
        return MappingMetadataWriteResult.WithoutMetadata(outcome);
    }

    private static bool SameFields(CatalogMappingMetadata metadata, MappingMetadataEdit edit) =>
        string.Equals(metadata.Name, edit.Name, StringComparison.Ordinal) &&
        string.Equals(metadata.Description, edit.Description, StringComparison.Ordinal);

    private static CatalogMappingMetadata? ReadMetadata(NpgsqlDataReader reader, string prefix)
    {
        var columns = new[] { "organization_id", "project_id", "id", "name", "description", "version" };
        var nulls = columns.Count(column => IsNull(reader, prefix + "_" + column));
        if (nulls == columns.Length)
            return null;
        if (nulls != 0)
            throw InvalidMetadata();
        return new CatalogMappingMetadata(new MappingScope(Value<Guid>(reader, prefix + "_organization_id"),
            Value<Guid>(reader, prefix + "_project_id"), Value<Guid>(reader, prefix + "_id")),
            Value<string>(reader, prefix + "_name"), Value<string>(reader, prefix + "_description"),
            Value<long>(reader, prefix + "_version"));
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    private static T Value<T>(NpgsqlDataReader reader, string name) => reader.GetFieldValue<T>(reader.GetOrdinal(name));
    private static bool IsNull(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name));

    private static T Read<T>(Func<T> materialize)
    {
        try { return materialize(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
        {
            throw InvalidMetadata();
        }
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (NpgsqlException)
        {
            throw new InvalidOperationException("Mapping metadata persistence failed.");
        }
    }

    private static InvalidOperationException InvalidMetadata() =>
        new("The database contains inconsistent mapping metadata.");

    private sealed record Observation(long AuthorizedCount, long LockedCount, long DecisionCount,
        long ChangedCount, long ReplyCount, string? Outcome, CatalogMappingMetadata? Previous,
        CatalogMappingMetadata? Metadata);
}
