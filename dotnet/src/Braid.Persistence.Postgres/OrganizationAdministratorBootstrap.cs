using System.Data;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>Offline bootstrap only: the initial administrator and fixed invitation tenant, never a project grant.</summary>
public static class OrganizationAdministratorBootstrap
{
    public static async Task BootstrapAsync(string ownerConnectionString, Guid organizationId, Guid principalId, Guid tenantId,
        CancellationToken token = default)
    {
        if (organizationId == Guid.Empty || principalId == Guid.Empty || tenantId == Guid.Empty ||
            tenantId == Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad")) throw new ArgumentException("Nonempty organizational identifiers required.");
        await using var source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(ownerConnectionString));
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using var command = new NpgsqlCommand("""
            DO $bootstrap$
            BEGIN
                IF current_user<>'braid_migrator' OR session_user<>current_user THEN
                    RAISE EXCEPTION 'Bootstrap requires the migration owner.' USING ERRCODE='42501';
                END IF;
            END;
            $bootstrap$;
            SELECT set_config('braid.organization_id',@org::text,true);
            SELECT id FROM ONLY braid.organizations WHERE id=@org FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("org", organizationId);
        await command.ExecuteNonQueryAsync(token);
        await using var bootstrap = new NpgsqlCommand("""
            INSERT INTO braid.administration_state(organization_id) VALUES(@org) ON CONFLICT DO NOTHING;
            SELECT tenant_id FROM ONLY braid.administration_state WHERE organization_id=@org FOR UPDATE;
            """, connection, transaction);
        bootstrap.Parameters.AddWithValue("org", organizationId);
        var existingTenant = await bootstrap.ExecuteScalarAsync(token);
        if (existingTenant is Guid existing && existing != tenantId) throw new InvalidOperationException("The organization invitation tenant is immutable.");
        await using (var authority = new NpgsqlCommand("""
            SELECT id FROM ONLY braid.principals WHERE id=@principal FOR SHARE;
            SELECT principal_id FROM ONLY braid.organization_memberships WHERE organization_id=@org AND principal_id=@principal FOR SHARE;
            """, connection, transaction))
        {
            authority.Parameters.AddWithValue("org", organizationId);
            authority.Parameters.AddWithValue("principal", principalId);
            await authority.ExecuteNonQueryAsync(token);
        }
        await using var eligible = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM ONLY braid.organization_memberships o JOIN ONLY braid.principals p ON p.id=o.principal_id AND p.active
                WHERE o.organization_id=@org AND o.principal_id=@principal AND o.active)
                AND NOT EXISTS(SELECT 1 FROM ONLY braid.organization_administration_roles WHERE organization_id=@org)
            """, connection, transaction);
        eligible.Parameters.AddWithValue("org", organizationId);
        eligible.Parameters.AddWithValue("principal", principalId);
        if (await eligible.ExecuteScalarAsync(token) is not true) throw new InvalidOperationException("Bootstrap needs an admitted active principal and no existing administrative roles.");
        await using var write = new NpgsqlCommand("""
            INSERT INTO braid.organization_administration_roles(organization_id,principal_id,role) VALUES(@org,@principal,'administrator');
            UPDATE ONLY braid.administration_state SET tenant_id=@tenant,version=version+1 WHERE organization_id=@org;
            INSERT INTO braid.administration_audit(organization_id,actor_id,operation,target_id,after_state)
                VALUES(@org,@principal,'bootstrapAdministrator',@principal,jsonb_build_object('role','administrator','tenantId',@tenant));
            """, connection, transaction);
        write.Parameters.AddWithValue("org", organizationId);
        write.Parameters.AddWithValue("principal", principalId);
        write.Parameters.AddWithValue("tenant", tenantId);
        await write.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }
}
