using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

/// <summary>Fresh private session metadata, never authentication, an authority cache or a grant.</summary>
public sealed class PostgresServerSessionReader : IServerSessionReader
{
    private readonly PostgresServerSessionDatabase _database;

    public PostgresServerSessionReader(PostgresServerSessionDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Profile != ServerSessionDatabaseProfile.Reader)
            throw new ArgumentException("Session observation requires the separate reader profile.", nameof(database));
        _database = database;
    }

    public async Task<CurrentServerSession?> ObserveAsync(ServerSessionKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await _database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await PostgresServerSessionFacts.PrepareAsync(connection, transaction, key.OrganizationId,
                _database.Profile, cancellationToken);
            await using var command = new NpgsqlCommand($"""
                WITH clock AS MATERIALIZED ({PostgresServerSessionFacts.Clock})
                SELECT clock.observed_at,{PostgresServerSessionFacts.ValidInstant("clock.observed_at")} AS clock_valid,
                    {PostgresServerSessionFacts.Projection("session_row", "stored_")},
                    {PostgresServerSessionFacts.IdentityProjection}
                FROM clock
                LEFT JOIN ONLY braid.server_sessions session_row
                    ON session_row.organization_id=@organizationId AND session_row.handle_digest=@digest
                LEFT JOIN ONLY braid.external_identity_bindings binding ON binding.binding_id=session_row.binding_id
                LEFT JOIN ONLY braid.principals principal ON principal.id=binding.principal_id
                """, connection, transaction);
            PostgresServerSessionFacts.AddKey(command, key);
            CurrentServerSession? result;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw PostgresServerSessionFacts.InvalidFacts();
                var sample = PostgresServerSessionFacts.ReadClock(reader);
                var stored = PostgresServerSessionFacts.ReadFacts(reader, "stored_", key);
                var identity = PostgresServerSessionFacts.ReadIdentity(reader, stored?.BindingId);
                result = ServerSessionPolicy.ObserveCurrent(key, sample, stored, identity);
                if (await reader.ReadAsync(cancellationToken)) throw PostgresServerSessionFacts.InvalidFacts();
            }
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (NpgsqlException)
        { throw new InvalidOperationException("Server session observation failed."); }
    }
}

internal static class PostgresServerSessionFacts
{
    internal const string Clock = "SELECT pg_catalog.statement_timestamp() AS observed_at";
    internal const string MaximumBeforeIdleAddition = "TIMESTAMPTZ '9999-12-31 23:39:59.999999+00'";
    internal const string MaximumBeforeAbsoluteAddition = "TIMESTAMPTZ '9999-12-31 22:59:59.999999+00'";
    internal const string IdentityProjection = """
        binding.binding_id AS identity_binding_id,binding.version AS identity_binding_version,
        binding.principal_id AS identity_owner_id,binding.enabled AS identity_enabled,
        principal.id AS identity_principal_id,principal.active AS identity_active
        """;
    internal const string ProjectedIdentity = """
        identity_binding_id,identity_binding_version,identity_owner_id,identity_enabled,
        identity_principal_id,identity_active
        """;
    private static readonly string[] Columns =
        ["handle_digest", "organization_id", "binding_id", "binding_version", "login_epoch", "issued_at",
         "idle_expires_at", "absolute_expires_at", "version", "revoked_at", "policy_version", "codec_version", "protected_ticket"];

    // These fragments accept only source-owned alias/prefix literals from this DAL, never request text.
    internal static string Projection(string alias, string prefix) =>
        string.Join(",", Columns.Select(column => alias + "." + column + " AS " + prefix + column)) + "," +
        string.Join(",", new[] { "issued_at", "idle_expires_at", "absolute_expires_at" }
            .Select(column => ValidInstant(alias + "." + column) + " AS " + prefix + column + "_valid")) + "," +
        "(" + alias + ".revoked_at IS NULL OR (" + ValidInstant(alias + ".revoked_at") + ")) AS " + prefix + "revoked_at_valid";

    internal static string ColumnProjection(string alias) => string.Join(",", Columns.Select(column => alias + "." + column));

    internal static string ValidInstant(string expression) =>
        "(pg_catalog.isfinite(" + expression + ") AND " + expression +
        " > TIMESTAMPTZ '0001-01-01 00:00:00+00' AND " + expression +
        " <= TIMESTAMPTZ '9999-12-31 23:59:59.999999+00')";

    internal static string ValidShape(string alias) => $"""
        (pg_catalog.octet_length({alias}.handle_digest)=32
            AND {alias}.organization_id<>'00000000-0000-0000-0000-000000000000'::uuid
            AND {alias}.binding_id<>'00000000-0000-0000-0000-000000000000'::uuid
            AND {alias}.login_epoch<>'00000000-0000-0000-0000-000000000000'::uuid
            AND {alias}.binding_version>0 AND {alias}.version>0
            AND {alias}.policy_version=1 AND {alias}.codec_version=1
            AND pg_catalog.octet_length({alias}.protected_ticket) BETWEEN 1 AND 16384
            AND {ValidInstant(alias + ".issued_at")} AND {ValidInstant(alias + ".idle_expires_at")}
            AND {ValidInstant(alias + ".absolute_expires_at")}
            AND {alias}.issued_at<{alias}.idle_expires_at AND {alias}.idle_expires_at<={alias}.absolute_expires_at
            AND {alias}.absolute_expires_at<={alias}.issued_at+INTERVAL '60 minutes'
            AND ({alias}.revoked_at IS NULL OR ({ValidInstant(alias + ".revoked_at")}
                AND {alias}.revoked_at>={alias}.issued_at AND {alias}.version>=2)))
        """;

    internal static async Task PrepareAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organizationId, ServerSessionDatabaseProfile profile, CancellationToken cancellationToken)
    {
        await using (var mode = new NpgsqlCommand(profile == ServerSessionDatabaseProfile.Reader
            ? "SET TRANSACTION READ ONLY" : "SET TRANSACTION READ WRITE", connection, transaction))
            await mode.ExecuteNonQueryAsync(cancellationToken);
        await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'",
            connection, transaction))
            await limits.ExecuteNonQueryAsync(cancellationToken);
        await using (var inspect = new NpgsqlCommand(
            "SELECT NULLIF(pg_catalog.current_setting($1,true),'')", connection, transaction))
        {
            inspect.Parameters.AddWithValue(PostgresTransaction.OrganizationSetting);
            if (await inspect.ExecuteScalarAsync(cancellationToken) is string)
                throw new InvalidOperationException("A leased connection contains a preset organization context.");
        }
        await using (var bind = new NpgsqlCommand("SELECT pg_catalog.set_config($1,$2,true)", connection, transaction))
        {
            bind.Parameters.AddWithValue(PostgresTransaction.OrganizationSetting);
            bind.Parameters.AddWithValue(organizationId.ToString("D"));
            if (await bind.ExecuteScalarAsync(cancellationToken) is not string current ||
                !string.Equals(current, organizationId.ToString("D"), StringComparison.Ordinal))
                throw new InvalidOperationException("The session transaction could not bind its organization.");
        }
        await ServerSessionRoleGuard.ValidateAsync(connection, transaction, profile, cancellationToken);
    }

    internal static DateTimeOffset ReadClock(NpgsqlDataReader reader) => Read(() =>
    {
        if (!reader.GetBoolean(reader.GetOrdinal("clock_valid"))) throw InvalidFacts();
        var sample = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("observed_at"));
        if (sample.Offset != TimeSpan.Zero || sample.Ticks % 10 != 0 ||
            sample == DateTimeOffset.MinValue || sample == DateTimeOffset.MaxValue) throw InvalidFacts();
        return sample;
    });

    internal static ServerSessionFacts? ReadFacts(NpgsqlDataReader reader, string prefix, ServerSessionKey expectedKey)
        => Read<ServerSessionFacts?>(() =>
        {
            if (reader.IsDBNull(reader.GetOrdinal(prefix + "handle_digest")))
            {
                if (Columns.Any(column => !reader.IsDBNull(reader.GetOrdinal(prefix + column)))) throw InvalidFacts();
                return null;
            }
            foreach (var column in new[] { "issued_at", "idle_expires_at", "absolute_expires_at", "revoked_at" })
                if (!reader.GetBoolean(reader.GetOrdinal(prefix + column + "_valid"))) throw InvalidFacts();
            var digest = reader.GetFieldValue<byte[]>(reader.GetOrdinal(prefix + "handle_digest"));
            if (digest.Length != 32) throw InvalidFacts();
            var key = new ServerSessionKey(reader.GetGuid(reader.GetOrdinal(prefix + "organization_id")),
                Sha256Digest.Parse(Convert.ToHexStringLower(digest)));
            if (key != expectedKey) throw InvalidFacts();
            return new ServerSessionFacts(key, reader.GetGuid(reader.GetOrdinal(prefix + "binding_id")),
                reader.GetInt64(reader.GetOrdinal(prefix + "binding_version")), reader.GetGuid(reader.GetOrdinal(prefix + "login_epoch")),
                new ServerSessionLifetime(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(prefix + "issued_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(prefix + "idle_expires_at")),
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(prefix + "absolute_expires_at"))),
                reader.GetInt64(reader.GetOrdinal(prefix + "version")),
                reader.GetFieldValue<byte[]>(reader.GetOrdinal(prefix + "protected_ticket")),
                reader.IsDBNull(reader.GetOrdinal(prefix + "revoked_at")) ? null :
                    reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(prefix + "revoked_at")),
                reader.GetInt32(reader.GetOrdinal(prefix + "policy_version")), reader.GetInt32(reader.GetOrdinal(prefix + "codec_version")));
        });

    internal static ServerSessionIdentityObservation? ReadIdentity(NpgsqlDataReader reader,
        Guid? expectedBindingId, PrincipalId? expectedOwner = null) => Read<ServerSessionIdentityObservation?>(() =>
    {
        if (reader.IsDBNull(reader.GetOrdinal("identity_binding_id")))
        {
            foreach (var name in new[] { "identity_binding_version", "identity_owner_id", "identity_enabled",
                "identity_principal_id", "identity_active" })
                if (!reader.IsDBNull(reader.GetOrdinal(name))) throw InvalidFacts();
            if (expectedBindingId is not null) throw InvalidFacts();
            return null;
        }
        var bindingId = reader.GetGuid(reader.GetOrdinal("identity_binding_id"));
        var owner = new PrincipalId(reader.GetGuid(reader.GetOrdinal("identity_owner_id")));
        if (reader.IsDBNull(reader.GetOrdinal("identity_principal_id")) ||
            reader.GetGuid(reader.GetOrdinal("identity_principal_id")) != owner.Value ||
            expectedBindingId is { } binding && bindingId != binding || expectedOwner is not null && owner != expectedOwner)
            throw InvalidFacts();
        return new ServerSessionIdentityObservation(bindingId, reader.GetInt64(reader.GetOrdinal("identity_binding_version")),
            owner, reader.GetBoolean(reader.GetOrdinal("identity_enabled")), reader.GetBoolean(reader.GetOrdinal("identity_active")));
    });

    internal static void AddKey(NpgsqlCommand command, ServerSessionKey key)
    {
        Add(command, "organizationId", NpgsqlDbType.Uuid, key.OrganizationId);
        Add(command, "digest", NpgsqlDbType.Bytea, Convert.FromHexString(key.HandleDigest.Value));
    }

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
        => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

    internal static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException or IndexOutOfRangeException)
        { throw InvalidFacts(); }
    }

    internal static InvalidOperationException InvalidFacts()
        => new("The database contains inconsistent server session facts.");
}
