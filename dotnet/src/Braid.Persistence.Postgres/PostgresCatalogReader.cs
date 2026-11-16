using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Specific fresh-authority catalog reads on the dedicated read-only pool.</summary>
public sealed partial class PostgresCatalogReader : IProjectReader, ICatalogReader
{
    private readonly PostgresCatalogDatabase _database;

    public PostgresCatalogReader(PostgresCatalogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    private const string AuthorizedOrganizationSql = """
        WITH admitted AS MATERIALIZED (
            SELECT organization.id AS organization_id, organization.name AS organization_name,
                principal.id AS principal_id
            FROM braid.organizations organization
            JOIN braid.principals principal ON principal.id = @actorId AND principal.active
            JOIN braid.organization_memberships admission
              ON admission.organization_id = organization.id AND admission.principal_id = principal.id AND admission.active
            WHERE organization.id = @organizationId
        )
        """;

    private const string AuthorizedProjectSql = """
        WITH authorized AS MATERIALIZED (
            SELECT project.organization_id, project.id AS project_id, @actorId::uuid AS principal_id,
                membership.role
            FROM braid.projects project
            JOIN braid.principals principal ON principal.id = @actorId AND principal.active
            JOIN braid.organization_memberships admission
              ON admission.organization_id = project.organization_id AND admission.principal_id = principal.id AND admission.active
            JOIN braid.project_memberships membership
              ON membership.organization_id = project.organization_id AND membership.project_id = project.id
             AND membership.principal_id = principal.id AND membership.active
             AND membership.role IN ('viewer', 'runner', 'publisher', 'administrator')
            WHERE project.organization_id = @organizationId AND project.id = @projectId
        )
        """;

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static void AddOrganization(NpgsqlCommand command, ActorContext actor, Guid organizationId)
    {
        Add(command, "organizationId", NpgsqlDbType.Uuid, organizationId);
        Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
    }

    private static void AddProject(NpgsqlCommand command, ActorContext actor, ProjectScope scope)
    {
        AddOrganization(command, actor, scope.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, scope.ProjectId);
    }

    private static ProjectScope ProjectOf(MappingScope scope) => new(scope.OrganizationId, scope.ProjectId);
    private static MappingScope MappingOf(RevisionScope scope) => new(scope.OrganizationId, scope.ProjectId, scope.MappingId);

    private static void RequireOrganization(ActorContext actor, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("A catalog organization identifier cannot be empty.", nameof(organizationId));
    }

    private static void RequireLimit(int limit)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Catalog pages must contain at most one hundred rows.");
    }

    private static InvalidOperationException InvalidMetadata() => new("The database contains inconsistent catalog metadata.");

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try { return await operation(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (NpgsqlException)
        {
            throw new InvalidOperationException("Catalog metadata persistence failed.");
        }
    }

    private static T Read<T>(Func<T> materialize)
    {
        try { return materialize(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException)
        {
            throw InvalidMetadata();
        }
    }

    private static T Value<T>(NpgsqlDataReader reader, string name) => reader.GetFieldValue<T>(reader.GetOrdinal(name));
    private static bool IsNull(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name));

    private static ProjectRole Role(string value) => value switch
    {
        "viewer" => ProjectRole.Viewer,
        "runner" => ProjectRole.Runner,
        "publisher" => ProjectRole.Publisher,
        "administrator" => ProjectRole.Administrator,
        _ => throw InvalidMetadata()
    };

    private static IngestionAdmissionClassification Admission(string value) => value switch
    {
        "eligible_valid" => IngestionAdmissionClassification.EligibleValid,
        "eligible_decoded_invalid" => IngestionAdmissionClassification.EligibleDecodedInvalid,
        _ => throw InvalidMetadata()
    };

    private static ProjectAccess ReadAccess(NpgsqlDataReader reader, ActorContext actor, ProjectScope scope) => Read(() =>
    {
        var observed = new ProjectScope(Value<Guid>(reader, "a_organization_id"), Value<Guid>(reader, "a_project_id"));
        var principal = new PrincipalId(Value<Guid>(reader, "a_principal_id"));
        if (observed != scope || principal != actor.PrincipalId)
            throw InvalidMetadata();
        return new ProjectAccess(observed, principal, Role(Value<string>(reader, "a_role")));
    });
}
