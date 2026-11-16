using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public sealed partial class ExternalIdentityBindingProvisioner
{
    public Task<ExternalIdentityBindingOwnerResult> BindAsync(ExternalIdentityKey key,
        PrincipalId existingPrincipalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(existingPrincipalId);
        return ExecuteAsync(async (connection, transaction, token) =>
        {
            var existing = await FindBindingAsync(connection, transaction, key, token);
            // Preserve collision precedence without admitting or disclosing the intended other principal.
            if (existing is not null && existing.PrincipalId != existingPrincipalId)
                return ExternalIdentityBindingOwnerResult.WithoutBinding(
                    ExternalIdentityBindingPolicy.ClassifyBind(key, existingPrincipalId, existing, null));
            var active = await PrincipalActiveAsync(connection, transaction, existingPrincipalId, token);
            var outcome = ExternalIdentityBindingPolicy.ClassifyBind(key, existingPrincipalId, existing, active);
            if (outcome != ExternalIdentityBindingOwnerOutcome.Created)
                return outcome == ExternalIdentityBindingOwnerOutcome.Existing
                    ? ExternalIdentityBindingOwnerResult.ForBinding(outcome, existing!)
                    : ExternalIdentityBindingOwnerResult.WithoutBinding(outcome);

            // Unique-key arbitration also covers an initially absent key. A loser does not reuse the
            // INSERT statement's snapshot: the following statement observes and locks the actual winner.
            var candidateBindingId = Guid.NewGuid();
            int inserted;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO braid.external_identity_bindings
                    (binding_id,provider,authority,subject,principal_id,enabled,version,created_at,disabled_at)
                VALUES(@bindingId,@provider,@authority,@subject,@principalId,true,1,pg_catalog.clock_timestamp(),NULL)
                ON CONFLICT ON CONSTRAINT external_identity_bindings_key_key DO NOTHING
                """, connection, transaction))
            {
                PostgresExternalIdentityFacts.AddKey(insert, key);
                PostgresExternalIdentityFacts.Add(insert, "bindingId", NpgsqlDbType.Uuid, candidateBindingId);
                PostgresExternalIdentityFacts.Add(insert, "principalId", NpgsqlDbType.Uuid, existingPrincipalId.Value);
                inserted = await insert.ExecuteNonQueryAsync(token);
                RequireChangedCount(inserted);
            }
            var current = await FindBindingAsync(connection, transaction, key, token)
                ?? throw PostgresExternalIdentityFacts.InvalidFacts();
            outcome = ExternalIdentityBindingPolicy.ClassifyBind(key, existingPrincipalId, current, active);
            if (inserted == 1)
            {
                if (outcome != ExternalIdentityBindingOwnerOutcome.Existing || current.BindingId != candidateBindingId ||
                    !current.Enabled || current.Version != 1)
                    throw PostgresExternalIdentityFacts.InvalidFacts();
                return ExternalIdentityBindingOwnerResult.ForBinding(ExternalIdentityBindingOwnerOutcome.Created, current);
            }
            return outcome == ExternalIdentityBindingOwnerOutcome.Existing
                ? ExternalIdentityBindingOwnerResult.ForBinding(outcome, current)
                : ExternalIdentityBindingOwnerResult.WithoutBinding(outcome);
        }, cancellationToken);
    }

    public Task<ExternalIdentityBindingOwnerResult> DisableAsync(ExternalIdentityKey key,
        PrincipalId expectedPrincipalId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedPrincipalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        return ExecuteAsync(async (connection, transaction, token) =>
        {
            var existing = await FindBindingAsync(connection, transaction, key, token);
            var outcome = ExternalIdentityBindingPolicy.ClassifyDisable(key, expectedPrincipalId, expectedVersion, existing);
            if (outcome != ExternalIdentityBindingOwnerOutcome.Disabled)
                return outcome == ExternalIdentityBindingOwnerOutcome.AlreadyDisabled
                    ? ExternalIdentityBindingOwnerResult.ForBinding(outcome, existing!)
                    : ExternalIdentityBindingOwnerResult.WithoutBinding(outcome);

            await using var disable = new NpgsqlCommand($"""
                UPDATE ONLY braid.external_identity_bindings b
                SET enabled=false,version=b.version+1,disabled_at=pg_catalog.clock_timestamp()
                WHERE b.binding_id=@bindingId AND b.principal_id=@principalId
                  AND b.version=@version AND b.enabled
                RETURNING {PostgresExternalIdentityFacts.BindingProjection}
                """, connection, transaction);
            PostgresExternalIdentityFacts.Add(disable, "bindingId", NpgsqlDbType.Uuid, existing!.BindingId);
            PostgresExternalIdentityFacts.Add(disable, "principalId", NpgsqlDbType.Uuid, expectedPrincipalId.Value);
            PostgresExternalIdentityFacts.Add(disable, "version", NpgsqlDbType.Bigint, expectedVersion);
            ExternalIdentityBinding current;
            await using (var reader = await disable.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) throw PostgresExternalIdentityFacts.InvalidFacts();
                current = PostgresExternalIdentityFacts.ReadBinding(reader, key, existing.BindingId);
                if (await reader.ReadAsync(token)) throw PostgresExternalIdentityFacts.InvalidFacts();
            }
            if (current.PrincipalId != existing.PrincipalId || current.CreatedAtUtc != existing.CreatedAtUtc ||
                current.Version != expectedVersion + 1 || current.Enabled || current.DisabledAtUtc is null)
                throw PostgresExternalIdentityFacts.InvalidFacts();
            return ExternalIdentityBindingOwnerResult.ForBinding(ExternalIdentityBindingOwnerOutcome.Disabled, current);
        }, cancellationToken);
    }
}
