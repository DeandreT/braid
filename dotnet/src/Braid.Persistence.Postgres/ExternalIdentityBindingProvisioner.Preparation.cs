using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class ExternalIdentityBindingProvisioner
{
    public Task<ExternalIdentityPrincipalPreparationOutcome> EnsurePrincipalAsync(PrincipalId principalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        return ExecuteAsync(async (connection, transaction, token) =>
        {
            int inserted;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO braid.principals(id,active) VALUES(@principalId,true)
                ON CONFLICT(id) DO NOTHING
                """, connection, transaction))
            {
                PostgresExternalIdentityFacts.Add(insert, "principalId", NpgsqlDbType.Uuid, principalId.Value);
                inserted = await insert.ExecuteNonQueryAsync(token);
                RequireChangedCount(inserted);
            }
            var active = await PrincipalActiveAsync(connection, transaction, principalId, token)
                ?? throw PostgresExternalIdentityFacts.InvalidFacts();
            var outcome = ExternalIdentityOwnerPreparationPolicy.ClassifyPrincipal(principalId, active);
            if (inserted == 1)
            {
                if (outcome != ExternalIdentityPrincipalPreparationOutcome.Existing)
                    throw PostgresExternalIdentityFacts.InvalidFacts();
                return ExternalIdentityPrincipalPreparationOutcome.Created;
            }
            return outcome;
        }, cancellationToken);
    }

    public Task<ExternalIdentityOrganizationAdmissionPreparationOutcome> EnsureOrganizationAdmissionAsync(
        Guid existingOrganizationId, PrincipalId principalId, CancellationToken cancellationToken = default)
    {
        if (existingOrganizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(existingOrganizationId));
        ArgumentNullException.ThrowIfNull(principalId);
        return ExecuteAsync(async (connection, transaction, token) =>
        {
            await using (var organization = new NpgsqlCommand(
                "SELECT o.id FROM ONLY braid.organizations o WHERE o.id=@organizationId FOR KEY SHARE OF o", connection, transaction))
            {
                PostgresExternalIdentityFacts.Add(organization, "organizationId", NpgsqlDbType.Uuid, existingOrganizationId);
                await using var reader = await organization.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                    return ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing;
                if (PostgresExternalIdentityFacts.Read(() => reader.GetGuid(0)) != existingOrganizationId ||
                    await reader.ReadAsync(token))
                    throw PostgresExternalIdentityFacts.InvalidFacts();
            }
            var active = await PrincipalActiveAsync(connection, transaction, principalId, token);
            if (active is not true)
                return ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
                    existingOrganizationId, principalId, true, active, null);

            int inserted;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO braid.organization_memberships(organization_id,principal_id,active)
                VALUES(@organizationId,@principalId,true)
                ON CONFLICT(organization_id,principal_id) DO NOTHING
                """, connection, transaction))
            {
                AddAdmissionParameters(insert, existingOrganizationId, principalId);
                inserted = await insert.ExecuteNonQueryAsync(token);
                RequireChangedCount(inserted);
            }
            bool membershipActive;
            await using (var membership = new NpgsqlCommand("""
                SELECT m.organization_id,m.principal_id,m.active FROM ONLY braid.organization_memberships m
                WHERE m.organization_id=@organizationId AND m.principal_id=@principalId
                FOR SHARE OF m
                """, connection, transaction))
            {
                AddAdmissionParameters(membership, existingOrganizationId, principalId);
                await using var reader = await membership.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) throw PostgresExternalIdentityFacts.InvalidFacts();
                membershipActive = PostgresExternalIdentityFacts.Read(() =>
                {
                    if (reader.GetGuid(0) != existingOrganizationId || reader.GetGuid(1) != principalId.Value)
                        throw PostgresExternalIdentityFacts.InvalidFacts();
                    return reader.GetBoolean(2);
                });
                if (await reader.ReadAsync(token)) throw PostgresExternalIdentityFacts.InvalidFacts();
            }
            var outcome = ExternalIdentityOwnerPreparationPolicy.ClassifyOrganizationAdmission(
                existingOrganizationId, principalId, true, active, membershipActive);
            if (inserted == 1)
            {
                if (outcome != ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing)
                    throw PostgresExternalIdentityFacts.InvalidFacts();
                return ExternalIdentityOrganizationAdmissionPreparationOutcome.Created;
            }
            return outcome;
        }, cancellationToken);
    }

    private static void AddAdmissionParameters(NpgsqlCommand command, Guid organizationId, PrincipalId principalId)
    {
        PostgresExternalIdentityFacts.Add(command, "organizationId", NpgsqlDbType.Uuid, organizationId);
        PostgresExternalIdentityFacts.Add(command, "principalId", NpgsqlDbType.Uuid, principalId.Value);
    }
}
