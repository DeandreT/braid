using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

public enum RegressionCaseDatabaseProfile { Reader, Writer }

/// <summary>Dedicated resetting pool for explicit saved samples, never production payloads.</summary>
public sealed class PostgresRegressionCaseStore : IRegressionCaseReader, IRegressionCaseMetadataReader, IRegressionCaseWriter, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly RegressionCaseDatabaseProfile profile;
    private readonly RegressionCaseEnvelopeCodec codec = new();

    public PostgresRegressionCaseStore(string connectionString, RegressionCaseDatabaseProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));
        this.profile = profile;
    }

    public async Task<RegressionCaseSaveResult> SaveAsync(ActorContext actor, RegressionCaseVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(version);
        if (profile != RegressionCaseDatabaseProfile.Writer) throw new InvalidOperationException("Regression writes require the writer profile.");
        var envelope = codec.Encode(version, cancellationToken);
        var scope = new MappingScope(version.Revision.OrganizationId, version.Revision.ProjectId, version.Revision.MappingId);
        await using var transaction = await BeginAsync(actor, scope, write: true, cancellationToken);
        await using var command = transaction.CreateCommand("SELECT braid.save_regression_case(@org,@project,@mapping,@id,@version,@creator,@revision,@native,@name,@digest,@length,@body)");
        AddScope(command, scope);
        Add(command, "id", NpgsqlDbType.Uuid, version.CaseId);
        Add(command, "version", NpgsqlDbType.Bigint, version.Version);
        Add(command, "creator", NpgsqlDbType.Uuid, version.Owner.Value);
        Add(command, "revision", NpgsqlDbType.Uuid, version.Revision.RevisionId);
        Add(command, "native", NpgsqlDbType.Text, version.NativeDigest.Value);
        Add(command, "name", NpgsqlDbType.Text, version.Name);
        Add(command, "digest", NpgsqlDbType.Text, envelope.Reference.ContentDigest.Value);
        Add(command, "length", NpgsqlDbType.Integer, envelope.Reference.ByteCount);
        Add(command, "body", NpgsqlDbType.Bytea, envelope.CopyBytes());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not int outcome || outcome is < 0 or > 4) throw Integrity();
        await transaction.CommitAsync(cancellationToken);
        return (RegressionCaseSaveOutcome)outcome switch
        {
            RegressionCaseSaveOutcome.Created or RegressionCaseSaveOutcome.Appended or RegressionCaseSaveOutcome.Replayed =>
                RegressionCaseSaveResult.Saved((RegressionCaseSaveOutcome)outcome, envelope.Reference),
            _ => RegressionCaseSaveResult.Refused((RegressionCaseSaveOutcome)outcome)
        };
    }

    public async Task<RegressionCaseSummary?> ReadMetadataAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        if (caseId == Guid.Empty || version < 1) throw new ArgumentException("A regression case and positive version are required.");
        await using var transaction = await BeginAsync(actor, mapping, write: false, cancellationToken);
        await using var command = transaction.CreateCommand("SELECT * FROM braid.read_regression_case_metadata(@org,@project,@mapping,@id,@version)");
        AddScope(command, mapping);
        Add(command, "id", NpgsqlDbType.Uuid, caseId);
        Add(command, "version", NpgsqlDbType.Bigint, version);
        RegressionCaseSummary? result = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new(reader.GetGuid(0), reader.GetInt64(1),
                    new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(3)),
                    new(reader.GetGuid(2)), reader.GetString(4), new(reader.GetDateTime(6)), new(reader.GetGuid(5)));
                if (result.CaseId != caseId || result.Version != version || await reader.ReadAsync(cancellationToken)) throw Integrity();
            }
        }
        if (result is not null)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.read_regression_case_metadata(@org,@project,@mapping,@id,@version))";
            if (await command.ExecuteScalarAsync(cancellationToken) is not true) result = null;
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        if (caseId == Guid.Empty || version < 1) throw new ArgumentException("A regression case and positive version are required.");
        await using var transaction = await BeginAsync(actor, mapping, write: false, cancellationToken);
        await using var command = transaction.CreateCommand("SELECT * FROM braid.read_regression_case(@org,@project,@mapping,@id,@version)");
        AddScope(command, mapping);
        Add(command, "id", NpgsqlDbType.Uuid, caseId);
        Add(command, "version", NpgsqlDbType.Bigint, version);
        RegressionCaseVersion? result = null;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                var owner = new PrincipalId(reader.GetGuid(0));
                var revision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(1));
                var native = CanonicalDigest(reader.GetString(2));
                var digest = CanonicalDigest(reader.GetString(3));
                var length = reader.GetInt32(4);
                if (length < 1 || length > RegressionCaseEnvelopeCodec.MaximumEnvelopeBytes) throw Integrity();
                await using var stream = reader.GetStream(5);
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                if (await stream.ReadAsync(new byte[1], cancellationToken) != 0) throw Integrity();
                result = codec.Decode(new(caseId, version, owner, revision, native, digest, length), bytes, cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) throw Integrity();
            }
        }
        if (result is not null)
        {
            // A second fresh statement drops data if access was revoked while bytes were being verified.
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.read_regression_case(@org,@project,@mapping,@id,@version))";
            if (await command.ExecuteScalarAsync(cancellationToken) is not true) result = null;
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit,
        Guid? afterCaseId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapping);
        if (limit is < 1 or > 100 || afterCaseId == Guid.Empty) throw new ArgumentException("The case page request is invalid.");
        await using var transaction = await BeginAsync(actor, mapping, write: false, cancellationToken);
        await using var command = transaction.CreateCommand("SELECT * FROM braid.list_regression_cases(@org,@project,@mapping,@after,@limit)");
        AddScope(command, mapping);
        Add(command, "after", NpgsqlDbType.Uuid, afterCaseId is null ? DBNull.Value : afterCaseId.Value);
        Add(command, "limit", NpgsqlDbType.Integer, limit + 1);
        var cases = new List<RegressionCaseSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (cases.Count > limit) throw Integrity();
                var revision = new RevisionScope(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, reader.GetGuid(3));
                cases.Add(new(reader.GetGuid(0), reader.GetInt64(1), revision, new(reader.GetGuid(2)), reader.GetString(4),
                    new DateTimeOffset(reader.GetDateTime(6)), new(reader.GetGuid(5))));
            }
        }
        if (cases.Count != 0)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM braid.list_regression_cases(@org,@project,@mapping,@after,@limit))";
            if (await command.ExecuteScalarAsync(cancellationToken) is not true) cases.Clear();
        }
        Guid? next = null;
        if (cases.Count > limit)
        {
            cases.RemoveAt(limit);
            next = cases[^1].CaseId;
        }
        await transaction.CommitAsync(cancellationToken);
        return new(cases, next);
    }

    private async Task<PostgresTransaction> BeginAsync(ActorContext actor, MappingScope mapping, bool write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var connection = await source.OpenConnectionAsync(token);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog; SET LOCAL TimeZone='UTC'; SET LOCAL DateStyle='ISO, MDY'", connection, transaction))
                await path.ExecuteNonQueryAsync(token);
            if (!write)
            {
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await readOnly.ExecuteNonQueryAsync(token);
            }
            await RegressionCaseRoleGuard.ValidateAsync(connection, transaction, profile, token);
            await using (var settings = new NpgsqlCommand("""
                SELECT set_config('statement_timeout','15000',true),set_config('lock_timeout','5000',true),
                    set_config('TimeZone','UTC',true),set_config('braid.case_principal_id',@actor,true)
                """, connection, transaction))
            {
                settings.Parameters.AddWithValue("actor", actor.PrincipalId.Value.ToString("D"));
                await settings.ExecuteNonQueryAsync(token);
            }
            var scope = new PostgresTransaction(connection, transaction, mapping.OrganizationId);
            await scope.BindOrganizationAsync(mapping.OrganizationId, token);
            return scope;
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void AddScope(NpgsqlCommand command, MappingScope scope)
    {
        Add(command, "org", NpgsqlDbType.Uuid, scope.OrganizationId);
        Add(command, "project", NpgsqlDbType.Uuid, scope.ProjectId);
        Add(command, "mapping", NpgsqlDbType.Uuid, scope.MappingId);
    }
    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value) => command.Parameters.Add(new(name, type) { Value = value });
    private static Sha256Digest CanonicalDigest(string value)
    {
        var result = Sha256Digest.Parse(value);
        return result.Value == value ? result : throw Integrity();
    }
    private static RegressionCaseException Integrity() => new(RegressionCaseFailure.StorageIntegrity);
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
