using System.Data;
using Npgsql;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Private immutable qualification proofs through independent, narrow database roles.</summary>
public sealed class PostgresBuildQualificationStore(string connectionString, bool writer) : IBuildQualificationStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<BuildQualificationWriteOutcome> SaveAsync(ActorContext actor, BuildQualification qualification, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(qualification);
        if (!writer || qualification.Actor != actor.PrincipalId) throw Invalid();
        var revision = qualification.Artifact.Intent.Revision;
        await using var tx = await BeginAsync(actor, revision.OrganizationId, true, token);
        await using var command = tx.CreateCommand("SELECT braid.write_qualification(@org,@project,@mapping,@id,@proof,@completion)");
        Scope(command, new(revision.OrganizationId, revision.ProjectId, revision.MappingId), qualification.Id);
        command.Parameters.AddWithValue("proof", qualification.CopyCanonicalBytes());
        command.Parameters.AddWithValue("completion", qualification.CopyResultBytes());
        object? value;
        try { value = await command.ExecuteScalarAsync(token); }
        catch (PostgresException error) when (error.SqlState == "23514" && error.ConstraintName == "automation_regression_gate")
        { return BuildQualificationWriteOutcome.RegressionRequired; }
        if (value is not int outcome || outcome is < 0 or > 3) throw Invalid();
        await tx.CommitAsync(token);
        return (BuildQualificationWriteOutcome)outcome;
    }

    public async Task<BuildQualification?> ReadAsync(ActorContext actor, MappingScope mapping, Guid qualificationId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (qualificationId == Guid.Empty) throw new ArgumentException("A qualification identifier is required.", nameof(qualificationId));
        await using var tx = await BeginAsync(actor, mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM braid.read_qualification(@org,@project,@mapping,@id)");
        Scope(command, mapping, qualificationId);
        BuildQualification? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                result = BuildQualificationRecord.Read(rows, 0, actor, mapping, qualificationId, token);
                if (await rows.ReadAsync(token)) throw Invalid();
            }
        }
        if (result is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.read_qualification(@org,@project,@mapping,@id))";
            if (await command.ExecuteScalarAsync(token) is not true) result = null;
        }
        await tx.CommitAsync(token);
        return result;
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext actor, Guid org, bool write, CancellationToken token)
    {
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var settings = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'; SET LOCAL statement_timeout='15s'; SET LOCAL lock_timeout='5s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(token);
            if (!write) await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(token);
            await QualificationRoleGuard.ValidateAsync(connection, transaction, writer, token);
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
    private static void Scope(NpgsqlCommand command, MappingScope mapping, Guid id)
    {
        command.Parameters.AddWithValue("org", mapping.OrganizationId); command.Parameters.AddWithValue("project", mapping.ProjectId);
        command.Parameters.AddWithValue("mapping", mapping.MappingId); command.Parameters.AddWithValue("id", id);
    }
    private static InvalidOperationException Invalid() => new("Qualification persistence refused.");
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
