using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Administration;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>All authority, CAS, last-administrator checks and audit writes share the database transaction.</summary>
public sealed partial class PostgresAdministrationStore(string connectionString) : IAdministrationStore, IOrganizationInvitationRedeemer, IAuditLogReader, IAiUsageReader, IAiQuotaStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public Task<OrganizationAdministrationV1?> ReadOrganizationAsync(ActorContext actor, Guid organizationId, CancellationToken token) =>
        ReadAsync<OrganizationAdministrationV1>(actor, organizationId, null, token);
    public Task<ProjectAdministrationV1?> ReadProjectAsync(ActorContext actor, ProjectScope project, CancellationToken token) =>
        ReadAsync<ProjectAdministrationV1>(actor, project.OrganizationId, project.ProjectId, token);

    private async Task<T?> ReadAsync<T>(ActorContext actor, Guid org, Guid? project, CancellationToken token)
    {
        await using var tx = await BeginAsync(org, token);
        await using var command = tx.CreateCommand("SELECT braid.read_administration(@org,@actor,@project)");
        Scope(command, org, actor.PrincipalId.Value, project);
        var text = await command.ExecuteScalarAsync(token);
        var result = text is string json ? JsonSerializer.Deserialize<T>(json, ProjectionJson.CreateOptions()) : default;
        await tx.CommitAsync(token);
        return result;
    }

    public async Task<string> WriteAsync(ActorContext actor, Guid organizationId, Guid? projectId, long expectedVersion,
        AdministrationOperation operation, Guid targetId, JsonElement data, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        if (!Enum.IsDefined(operation) || targetId == Guid.Empty) throw new ArgumentException("Invalid administration command.");
        await using var tx = await BeginAsync(organizationId, token);
        await using var command = tx.CreateCommand("SELECT braid.write_administration(@org,@actor,@version,@op,@project,@target,@data)");
        Scope(command, organizationId, actor.PrincipalId.Value, projectId);
        var name = operation.ToString();
        command.Parameters.AddWithValue("op", char.ToLowerInvariant(name[0]) + name[1..]);
        command.Parameters.AddWithValue("version", expectedVersion);
        command.Parameters.AddWithValue("target", targetId);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, data.GetRawText());
        var result = await command.ExecuteScalarAsync(token) as string;
        if (result is not ("updated" or "unchanged" or "refused" or "preconditionFailed" or "conflict" or "lastAdministrator" or "invalidRequest"))
            throw new InvalidOperationException("Administration persistence refused.");
        // A refusal never commits partially prepared facts.
        if (result is "updated" or "unchanged") await tx.CommitAsync(token);
        return result;
    }

    public async Task<bool> RedeemAsync(Guid organizationId, string secret, ExternalIdentityKey verifiedIdentity, CancellationToken token)
    {
        if (!OrganizationInvitationSecret.IsValid(secret) || verifiedIdentity.Provider != "entra.oid" ||
            !Guid.TryParseExact(verifiedIdentity.Authority, "D", out var tenant) || !Guid.TryParseExact(verifiedIdentity.Subject, "D", out var oid)) return false;
        await using var tx = await BeginAsync(organizationId, token);
        await using var command = tx.CreateCommand("SELECT braid.redeem_organization_invitation(@org,@hash,@tenant,@oid)");
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("hash", OrganizationInvitationSecret.Hash(secret));
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("oid", oid);
        var result = await command.ExecuteScalarAsync(token) is true;
        if (result) await tx.CommitAsync(token);
        return result;
    }

    private async Task<PostgresTransaction> BeginAsync(Guid org, CancellationToken token, bool readOnly = false, bool exclusiveLifecycle = false)
    {
        if (org == Guid.Empty) throw new ArgumentException("Organization required.", nameof(org));
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            if (readOnly)
            {
                await using var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await mode.ExecuteNonQueryAsync(token);
            }
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'; SET LOCAL TimeZone='UTC'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            await AdministrationRoleGuard.ValidateAsync(connection, transaction, token);
            var tx = new PostgresTransaction(connection, transaction, org);
            await tx.BindOrganizationAsync(org, token);
            if (exclusiveLifecycle) await LifecycleTransactionGate.AcquireAsync(connection, transaction, org, token, exclusive: true);
            return tx;
        }
        catch { if (transaction is not null) await transaction.DisposeAsync(); await connection.DisposeAsync(); throw; }
    }

    private static void Scope(NpgsqlCommand command, Guid org, Guid actor, Guid? project)
    {
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Uuid, (object?)project ?? DBNull.Value);
    }
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
