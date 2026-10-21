using System.Data;
using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Application.Regression;
using Weft.Deployment;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

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
        await using var command = tx.CreateCommand("SELECT weft.write_qualification(@org,@project,@mapping,@id,@proof,@completion)");
        Scope(command, new(revision.OrganizationId, revision.ProjectId, revision.MappingId), qualification.Id);
        command.Parameters.AddWithValue("proof", qualification.CopyCanonicalBytes());
        command.Parameters.AddWithValue("completion", qualification.CopyResultBytes());
        if (await command.ExecuteScalarAsync(token) is not int outcome || outcome is < 0 or > 3) throw Invalid();
        await tx.CommitAsync(token);
        return (BuildQualificationWriteOutcome)outcome;
    }

    public async Task<BuildQualification?> ReadAsync(ActorContext actor, MappingScope mapping, Guid qualificationId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (qualificationId == Guid.Empty) throw new ArgumentException("A qualification identifier is required.", nameof(qualificationId));
        await using var tx = await BeginAsync(actor, mapping.OrganizationId, false, token);
        await using var command = tx.CreateCommand("SELECT * FROM weft.read_qualification(@org,@project,@mapping,@id)");
        Scope(command, mapping, qualificationId);
        BuildQualification? result = null;
        await using (var rows = await command.ExecuteReaderAsync(token))
        {
            if (await rows.ReadAsync(token))
            {
                var proof = rows.GetFieldValue<byte[]>(0); var completion = rows.GetFieldValue<byte[]>(1);
                var intentBytes = rows.GetFieldValue<byte[]>(2); var caseBytes = rows.GetFieldValue<byte[]>(10);
                var evidenceBytes = rows.GetFieldValue<byte[]>(13);
                if (proof.Length is < 1 or > 16384 || completion.Length is < 1 or > RegressionRunResultCodec.MaximumBytes ||
                    intentBytes.Length is < 1 or > 1048576 || caseBytes.Length is < 1 or > RegressionCaseEnvelopeCodec.MaximumEnvelopeBytes ||
                    evidenceBytes.Length is < 1 or > RegressionRunResultCodec.MaximumBytes) throw Invalid();
                using var document = JsonDocument.Parse(proof);
                var metadata = document.RootElement;
                var intent = BuildIntentCodec.Decode(intentBytes);
                if (intent.Revision.OrganizationId != mapping.OrganizationId || intent.Revision.ProjectId != mapping.ProjectId ||
                    intent.Revision.MappingId != mapping.MappingId || metadata.GetProperty("qualificationId").GetGuid() != qualificationId ||
                    metadata.GetProperty("actorId").GetGuid() != actor.PrincipalId.Value) throw Invalid();
                var artifact = BuildArtifactReference.Restore(intent, Sha256Digest.Parse(rows.GetString(3)), rows.GetInt32(4));
                var build = new BuildRunSnapshot(actor.PrincipalId, intent, BuildRunState.Succeeded, rows.GetInt32(5),
                    new(rows.GetDateTime(6)), (BuildExecutionOutcome)rows.GetInt32(7), artifact);
                var reference = new RegressionCaseReference(metadata.GetProperty("caseId").GetGuid(), metadata.GetProperty("caseVersion").GetInt64(),
                    new(rows.GetGuid(8)), intent.Revision, intent.NativeDigest, Sha256Digest.Parse(metadata.GetProperty("caseDigest").GetString()!), rows.GetInt32(9));
                var sourceCase = new RegressionCaseEnvelopeCodec().Decode(reference, caseBytes, token);
                var evidence = new RegressionRunSnapshot(intent.RegressionEvidenceId, actor.PrincipalId, reference, RegressionRunState.Completed,
                    rows.GetInt32(11), new(rows.GetDateTime(12)), evidenceBytes, Sha256Digest.FromBytes(evidenceBytes));
                result = BuildQualification.Restore(qualificationId, actor.PrincipalId, build, evidence, sourceCase,
                    metadata.GetProperty("runtimeImage").GetString()!, proof, completion, token);
                if (await rows.ReadAsync(token)) throw Invalid();
            }
        }
        if (result is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM weft.read_qualification(@org,@project,@mapping,@id))";
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
            await using (var identity = new NpgsqlCommand("SELECT set_config('weft.build_principal_id',@actor,true)", connection, transaction))
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
