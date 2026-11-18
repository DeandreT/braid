using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class LifecycleTransactionGate
{
    // Use PostgreSQL's lock capability directly. This gives restricted adapters
    // no lifecycle-table access or owner-function execution privilege.
    // Only lifecycle changes are exclusive; ordinary reads and writes share the gate.
    internal static async Task AcquireAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organizationId, CancellationToken token, bool exclusive = false)
    {
        await using var command = new NpgsqlCommand(exclusive
            ? "SELECT pg_advisory_xact_lock(1464158804,hashtext('braid.lifecycle:'||$1::text))"
            : "SELECT pg_advisory_xact_lock_shared(1464158804,hashtext('braid.lifecycle:'||$1::text))", connection, transaction);
        command.Parameters.AddWithValue(organizationId);
        await command.ExecuteNonQueryAsync(token);
    }
}
