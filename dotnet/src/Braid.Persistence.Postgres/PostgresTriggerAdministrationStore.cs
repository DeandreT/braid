using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Deployment;
using Braid.Deployment.Triggers;

namespace Braid.Persistence.Postgres;

/// <summary>Administrator trigger configuration through the release-writer login; each call re-checks current project authority.</summary>
public sealed class PostgresTriggerAdministrationStore(string connectionString) : ITriggerAdministrationStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<TriggerSnapshot?> ReadAsync(ActorContext administrator, ReleaseTargetReference target, Guid triggerId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_trigger(@org,@project,@mapping,@environment,@version,@target,@trigger)");
        PostgresTriggerWorkStore.Scope(command, target);
        command.Parameters.AddWithValue("trigger", triggerId);
        TriggerSnapshot? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var configuration = TriggerConfiguration.Parse(rows.GetFieldValue<byte[]>(1));
                if (configuration.TriggerId != triggerId || configuration.Target != target) throw Invalid();
                result = new(configuration, rows.GetInt64(0), rows.GetBoolean(2), rows.GetBoolean(3), rows.GetInt64(4));
            }
            if (await rows.ReadAsync(token)) throw Invalid();
        }
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<TriggerOperationOutcome> OperateAsync(ActorContext administrator, ReleaseTargetReference target, TriggerOperation operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(operation);
        if (operation.Configuration is { } configuration && configuration.Target != target) throw new ArgumentException("The configuration names another target.");
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT braid.operate_trigger(@org,@project,@mapping,@environment,@version,@target,@trigger,@operation,@action,@expected,@body)");
        PostgresTriggerWorkStore.Scope(command, target);
        Arguments(command, operation);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)operation.Configuration?.CopyCanonicalBytes() ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(token) is not int result || result is < 0 or > 5) throw Invalid();
        await tx.CommitAsync(token);
        return (TriggerOperationOutcome)result;
    }

    public async Task<TriggerOperationOutcome?> RecoverAsync(ActorContext administrator, ReleaseTargetReference target, TriggerOperation operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(operation);
        await using var tx = await BeginAsync(administrator, target.Mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT braid.read_trigger_operation(@org,@project,@mapping,@environment,@version,@target,@trigger,@operation,@action,@expected,@digest)");
        PostgresTriggerWorkStore.Scope(command, target);
        Arguments(command, operation);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Text, (object?)operation.Configuration?.Digest.Value ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(token);
        await tx.CommitAsync(token);
        if (result is null or DBNull) return null;
        if (result is not int number || number is < 0 or > 5) throw Invalid();
        return (TriggerOperationOutcome)number;
    }

    private static void Arguments(NpgsqlCommand command, TriggerOperation operation)
    {
        command.Parameters.AddWithValue("trigger", operation.TriggerId);
        command.Parameters.AddWithValue("operation", operation.OperationId);
        command.Parameters.AddWithValue("action", operation.Action switch
        {
            TriggerAction.Configure => "configure",
            TriggerAction.Enable => "enable",
            TriggerAction.Disable => "disable",
            _ => "retire"
        });
        command.Parameters.AddWithValue("expected", operation.ExpectedGeneration);
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext actor, Guid org, bool write, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await LocalReleaseRoleGuard.ValidateAsync(connection, transaction, writer: true, token);
            if (!await TriggerRoleGuard.InstalledAsync(connection, transaction, token)) throw Invalid();
            await using (var identity = new NpgsqlCommand("SELECT set_config('braid.build_principal_id',@actor,true)", connection, transaction))
            {
                identity.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await identity.ExecuteNonQueryAsync(token);
            }
            var result = new PostgresTransaction(connection, transaction, org);
            await result.BindOrganizationAsync(org, token);
            return result;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static InvalidOperationException Invalid() => new("Trigger administration persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
