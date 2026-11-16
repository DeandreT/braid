using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Fresh binding and principal observations, with no grant inference or positive cache.</summary>
public sealed class PostgresExternalIdentityReader : IExternalIdentityReader
{
    private readonly PostgresExternalIdentityDatabase _database;

    public PostgresExternalIdentityReader(PostgresExternalIdentityDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ReadAsync(key, null, cancellationToken);
    }

    public Task<ExternalIdentityResolution> ResolveBindingAsync(Guid bindingId,
        CancellationToken cancellationToken = default)
    {
        if (bindingId == Guid.Empty)
            throw new ArgumentException("A binding identifier cannot be empty.", nameof(bindingId));
        return ReadAsync(null, bindingId, cancellationToken);
    }

    private async Task<ExternalIdentityResolution> ReadAsync(ExternalIdentityKey? key, Guid? bindingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await mode.ExecuteNonQueryAsync(cancellationToken);
            await using (var limits = new NpgsqlCommand(
                "SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'", connection, transaction))
                await limits.ExecuteNonQueryAsync(cancellationToken);
            await ExternalIdentityRoleGuard.ValidateReaderAsync(connection, transaction, cancellationToken);

            // One actual statement snapshot supplies both the binding and its current principal.
            // A missing principal is inconsistent persisted data, not an unknown external identity.
            var predicate = key is not null
                ? "b.provider=@provider AND b.authority=@authority AND b.subject=@subject"
                : "b.binding_id=@bindingId";
            await using var command = new NpgsqlCommand($"""
                SELECT {PostgresExternalIdentityFacts.BindingProjection}, p.id, p.active
                FROM ONLY braid.external_identity_bindings b
                LEFT JOIN ONLY braid.principals p ON p.id=b.principal_id
                WHERE {predicate}
                """, connection, transaction);
            if (key is not null) PostgresExternalIdentityFacts.AddKey(command, key);
            else PostgresExternalIdentityFacts.Add(command, "bindingId", NpgsqlDbType.Uuid, bindingId!.Value);
            ExternalIdentityResolution result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    result = ExternalIdentityResolution.FromCurrent(null, false);
                else
                {
                    var binding = PostgresExternalIdentityFacts.ReadBinding(reader, key, bindingId);
                    var active = PostgresExternalIdentityFacts.Read(() =>
                    {
                        if (reader.IsDBNull(11) || reader.IsDBNull(12) ||
                            reader.GetGuid(11) != binding.PrincipalId.Value)
                            throw PostgresExternalIdentityFacts.InvalidFacts();
                        return reader.GetBoolean(12);
                    });
                    result = ExternalIdentityResolution.FromCurrent(binding, active);
                    if (await reader.ReadAsync(cancellationToken))
                        throw PostgresExternalIdentityFacts.InvalidFacts();
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("External identity persistence failed."); }
    }
}

internal static class PostgresExternalIdentityFacts
{
    // The finite flags independently distinguish database infinities from Npgsql's CLR extrema.
    internal const string BindingProjection = """
        b.binding_id,b.provider,b.authority,b.subject,b.principal_id,b.version,b.enabled,
        b.created_at,b.disabled_at,pg_catalog.isfinite(b.created_at),
        (b.disabled_at IS NULL OR pg_catalog.isfinite(b.disabled_at))
        """;

    internal static ExternalIdentityBinding ReadBinding(NpgsqlDataReader reader,
        ExternalIdentityKey? expectedKey = null, Guid? expectedBindingId = null)
        => Read(() =>
        {
            if (!reader.GetBoolean(9) || !reader.GetBoolean(10)) throw InvalidFacts();
            var key = new ExternalIdentityKey(reader.GetString(1), reader.GetString(2), reader.GetString(3));
            var binding = new ExternalIdentityBinding(reader.GetGuid(0), key, new PrincipalId(reader.GetGuid(4)),
                reader.GetInt64(5), reader.GetBoolean(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
            if (expectedKey is not null && binding.Key != expectedKey ||
                expectedBindingId is { } id && binding.BindingId != id)
                throw InvalidFacts();
            return binding;
        });

    internal static void AddKey(NpgsqlCommand command, ExternalIdentityKey key)
    {
        Add(command, "provider", NpgsqlDbType.Text, key.Provider);
        Add(command, "authority", NpgsqlDbType.Text, key.Authority);
        Add(command, "subject", NpgsqlDbType.Text, key.Subject);
    }

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
        => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    internal static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException)
        { throw InvalidFacts(); }
    }

    internal static InvalidOperationException InvalidFacts()
        => new("The database contains inconsistent external identity facts.");
}
