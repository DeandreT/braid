using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Scoped ingestion operations and private, bounded import snapshots; no external I/O.</summary>
public sealed partial class PostgresRevisionIngestionStore : IIngestionMetadataStore
{
    private readonly PostgresIngestionDatabase _database;

    public PostgresRevisionIngestionStore(PostgresIngestionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project,
        Guid idempotencyKey, IngestionIntent intent, IngestionBackendProfile proposedProfile,
        IngestionAdmissionPolicy proposedPolicy, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ReserveCoreAsync(actor, project, idempotencyKey, intent, proposedProfile,
            proposedPolicy, cancellationToken), cancellationToken);

    public Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project,
        Guid operationId, Guid serverInstanceId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => ClaimCoreAsync(actor, project, operationId, serverInstanceId, cancellationToken), cancellationToken);

    public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim,
        PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => MarkStagedCoreAsync(actor, claim, verifiedNativeObject, cancellationToken), cancellationToken);

    public Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim,
        IngestionAttemptCompletion completion, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => CompleteAttemptCoreAsync(actor, claim, completion, cancellationToken), cancellationToken);

    public Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => FinalizeCoreAsync(actor, claim, cancellationToken), cancellationToken);

    public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project,
        Guid operationId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => GetReceiptCoreAsync(actor, project, operationId, cancellationToken), cancellationToken);

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException($"Ingestion metadata persistence failed ({exception.SqlState}).");
        }
        catch (NpgsqlException)
        {
            // PostgreSQL row DETAIL can contain derived diagnostic text. Keep failure reporting categorical.
            throw new InvalidOperationException("Ingestion metadata persistence failed.");
        }
    }

    private const string AuthorizedSql = """
        WITH authorized AS MATERIALIZED (
            SELECT project.organization_id, project.id AS project_id
            FROM weft.projects project
            JOIN weft.principals principal ON principal.id = @actorId AND principal.active
            JOIN weft.organization_memberships admission
              ON admission.organization_id = project.organization_id
             AND admission.principal_id = principal.id AND admission.active
            JOIN weft.project_memberships membership
              ON membership.organization_id = project.organization_id
             AND membership.project_id = project.id
             AND membership.principal_id = principal.id AND membership.active
             AND membership.role IN ('publisher', 'administrator')
            WHERE project.organization_id = @organizationId AND project.id = @projectId
        )
        """;

    private const string LockedSql = AuthorizedSql + """
        , locked AS MATERIALIZED (
            SELECT ingestion.* FROM weft.revision_ingestions ingestion
            JOIN authorized USING (organization_id, project_id)
            WHERE ingestion.operation_id = @operationId AND ingestion.actor_principal_id = @actorId
            FOR UPDATE OF ingestion
        )
        """;

    private const string FencedSql = LockedSql + """
        , fenced AS MATERIALIZED (
            SELECT locked.*, clock_timestamp() AS observed_at FROM locked
            WHERE locked.phase IN ('reserved', 'staged', 'ready')
              AND locked.lease_owner_id = @instanceId AND locked.lease_token = @leaseToken
              AND locked.lease_generation = @generation AND locked.actor_principal_id = @originalActorId
              AND locked.lease_expires_at > clock_timestamp()
        )
        """;

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static void AddScope(NpgsqlCommand command, ActorContext actor, ProjectScope project)
    {
        Add(command, "organizationId", NpgsqlDbType.Uuid, project.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, project.ProjectId);
        Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
    }

    private static void AddClaim(NpgsqlCommand command, ActorContext actor, IngestionClaim claim)
    {
        AddScope(command, actor, claim.Project);
        Add(command, "operationId", NpgsqlDbType.Uuid, claim.OperationId);
        Add(command, "instanceId", NpgsqlDbType.Uuid, claim.ServerInstanceId);
        Add(command, "leaseToken", NpgsqlDbType.Uuid, claim.LeaseToken);
        Add(command, "generation", NpgsqlDbType.Bigint, claim.Generation);
        Add(command, "originalActorId", NpgsqlDbType.Uuid, claim.OriginalActor.Value);
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("An ingestion identifier cannot be empty.", name);
    }

    private static void RequireClaim(ActorContext actor, IngestionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(claim);
    }

    private static bool IsTerminal(IngestionPhase phase) => phase is IngestionPhase.Committed or
        IngestionPhase.Rejected or IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted;

    private static InvalidOperationException InvalidMetadata() =>
        new("The database contains inconsistent ingestion metadata.");
}
