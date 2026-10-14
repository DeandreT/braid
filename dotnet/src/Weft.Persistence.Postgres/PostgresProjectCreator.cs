using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Invokes only the accepted creation routines; success is exposed after commit.</summary>
public sealed class PostgresProjectCreator : IProjectCreator, IOrganizationProjectCreationReader
{
    private readonly PostgresProjectCreationDatabase _database;

    public PostgresProjectCreator(PostgresProjectCreationDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<ProjectCreationResult> CreateAsync(ActorContext actor, Guid organizationId,
        Guid idempotencyKey, ProjectCreationMetadata metadata, CancellationToken cancellationToken = default)
    {
        Validate(actor, organizationId, idempotencyKey, metadata);
        return ExecuteAsync(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
            var result = await CreateInTransactionAsync(transaction, actor, organizationId, idempotencyKey, metadata, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    // Friend tests hold this same guarded production operation uncommitted to
    // observe actual uniqueness/authority locks; no raw SQL is publicly exposed.
    internal Task<ProjectCreationResult> CreateInTransactionAsync(PostgresTransaction transaction,
        ActorContext actor, Guid organizationId, Guid idempotencyKey, ProjectCreationMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Validate(actor, organizationId, idempotencyKey, metadata);
        return ExecuteAsync(() => CreateInTransactionCoreAsync(transaction, actor, organizationId, idempotencyKey, metadata, cancellationToken), cancellationToken);
    }

    private static async Task<ProjectCreationResult> CreateInTransactionCoreAsync(PostgresTransaction transaction,
        ActorContext actor, Guid organizationId, Guid idempotencyKey, ProjectCreationMetadata metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
        await using var command = transaction.CreateCommand(
            "SELECT * FROM weft.create_project_v1(@organizationId,@actorId,@key,@name,@description)");
        Add(command, "organizationId", NpgsqlDbType.Uuid, organizationId);
        Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
        Add(command, "key", NpgsqlDbType.Uuid, idempotencyKey);
        Add(command, "name", NpgsqlDbType.Text, metadata.Name);
        Add(command, "description", NpgsqlDbType.Text, metadata.Description);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        var result = Read(() => Materialize(reader, actor, organizationId, idempotencyKey, metadata));
        if (await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        return result;
    }

    public Task<OrganizationProjectCreationAdmission?> GetAdmissionAsync(ActorContext actor,
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireId(organizationId, nameof(organizationId));
        return ExecuteAsync<OrganizationProjectCreationAdmission?>(async () =>
        {
            await using var transaction = await _database.BeginTransactionAsync(organizationId, cancellationToken);
            await transaction.RequireOrganizationAsync(organizationId, cancellationToken);
            await using var command = transaction.CreateCommand(
                "SELECT * FROM weft.get_project_creation_admission_v1(@organizationId,@actorId)");
            Add(command, "organizationId", NpgsqlDbType.Uuid, organizationId);
            Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            OrganizationProjectCreationAdmission? result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
                result = Read<OrganizationProjectCreationAdmission?>(() =>
                {
                    var outcome = Value<string>(reader, "outcome");
                    if (outcome == "not_admitted")
                    {
                        if (!IsNull(reader, "organization_id") || !IsNull(reader, "principal_id") ||
                            !IsNull(reader, "can_create_projects")) throw InvalidFacts();
                        return null;
                    }
                    if (outcome is not ("admitted" or "capability_denied") ||
                        Value<Guid>(reader, "organization_id") != organizationId ||
                        Value<Guid>(reader, "principal_id") != actor.PrincipalId.Value)
                        throw InvalidFacts();
                    var allowed = Value<bool>(reader, "can_create_projects");
                    if (allowed != (outcome == "admitted")) throw InvalidFacts();
                    return new OrganizationProjectCreationAdmission(organizationId, actor.PrincipalId, allowed);
                });
                if (await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
    }

    private static ProjectCreationResult Materialize(NpgsqlDataReader reader, ActorContext actor,
        Guid organizationId, Guid key, ProjectCreationMetadata metadata)
    {
        var outcome = Value<string>(reader, "outcome") switch
        {
            "created" => ProjectCreationOutcome.Created,
            "replayed" => ProjectCreationOutcome.Replayed,
            "not_admitted" => ProjectCreationOutcome.NotAdmitted,
            "capability_denied" => ProjectCreationOutcome.CapabilityDenied,
            "idempotency_conflict" => ProjectCreationOutcome.IdempotencyConflict,
            _ => throw InvalidFacts()
        };
        var fields = new[] { "organization_id", "idempotency_key", "original_actor_id", "project_id",
            "fingerprint_version", "fingerprint", "name", "description", "created_at", "initial_project_version",
            "initial_membership_role", "initial_membership_active", "initial_membership_version" };
        if (outcome is not (ProjectCreationOutcome.Created or ProjectCreationOutcome.Replayed))
        {
            if (fields.Any(field => !IsNull(reader, field))) throw InvalidFacts();
            return ProjectCreationResult.WithoutReceipt(outcome);
        }
        if (fields.Any(field => IsNull(reader, field)) ||
            Value<Guid>(reader, "organization_id") != organizationId || Value<Guid>(reader, "idempotency_key") != key ||
            Value<Guid>(reader, "original_actor_id") != actor.PrincipalId.Value ||
            !string.Equals(Value<string>(reader, "name"), metadata.Name, StringComparison.Ordinal) ||
            !string.Equals(Value<string>(reader, "description"), metadata.Description, StringComparison.Ordinal) ||
            Value<string>(reader, "initial_membership_role") != "administrator") throw InvalidFacts();
        var digest = Value<string>(reader, "fingerprint");
        var fingerprint = ProjectCreationFingerprint.Restore(Value<int>(reader, "fingerprint_version"), Sha256Digest.Parse(digest));
        if (digest != fingerprint.Digest.Value) throw InvalidFacts();
        if (fingerprint != ProjectCreationFingerprint.Create(organizationId, actor.PrincipalId, key, metadata)) throw InvalidFacts();
        var receipt = new ProjectCreationReceipt(new ProjectScope(organizationId, Value<Guid>(reader, "project_id")),
            key, actor.PrincipalId, metadata, fingerprint, Value<DateTimeOffset>(reader, "created_at"),
            Value<long>(reader, "initial_project_version"), ProjectRole.Administrator,
            Value<bool>(reader, "initial_membership_active"), Value<long>(reader, "initial_membership_version"));
        return ProjectCreationResult.ForReceipt(outcome, receipt);
    }

    private static void Validate(ActorContext actor, Guid organizationId, Guid key, ProjectCreationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(metadata);
        RequireId(organizationId, nameof(organizationId));
        RequireId(key, nameof(key));
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty) throw new ArgumentException("A project creation identifier cannot be empty.", name);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
        => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
    private static T Value<T>(NpgsqlDataReader reader, string name) => reader.GetFieldValue<T>(reader.GetOrdinal(name));
    private static bool IsNull(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name));
    private static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
        { throw InvalidFacts(); }
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Project creation persistence failed."); }
    }

    private static InvalidOperationException InvalidFacts()
        => new("The database contains inconsistent project creation facts.");
}
