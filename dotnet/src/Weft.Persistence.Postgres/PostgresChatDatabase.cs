using System.Data;
using Npgsql;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

public enum ChatDatabaseProfile { Reader, Writer, Lifecycle, Approver }

/// <summary>Owns a separate, resetting, lazily opened chat-only credential pool.</summary>
public sealed class PostgresChatDatabase : IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _source;
    private readonly object _lifetime = new();
    private bool _disposed;

    public PostgresChatDatabase(string connectionString, ChatDatabaseProfile profile)
    {
        if (profile is not (ChatDatabaseProfile.Reader or ChatDatabaseProfile.Writer or ChatDatabaseProfile.Approver)) throw new ArgumentOutOfRangeException(nameof(profile));
        Profile = profile;
        var normalized = PostgresConfiguration.Normalize(connectionString);
        _source = new(() => NpgsqlDataSource.Create(normalized));
    }
    internal ChatDatabaseProfile Profile { get; }
    private NpgsqlDataSource Source
    {
        get { lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); return _source.Value; } }
    }
    internal NpgsqlDataSource DataSource => Source;

    internal Task<PostgresTransaction> BeginAsync(ActorContext actor, ChatConversationKey key, bool write, CancellationToken ct) =>
        BeginAsync(actor, key.OrganizationId, write, ct);

    internal async Task<PostgresTransaction> BeginAsync(ActorContext actor, Guid organizationId, bool write, CancellationToken ct)
    {
        if (write && Profile is not (ChatDatabaseProfile.Writer or ChatDatabaseProfile.Approver)) throw new InvalidOperationException("Chat mutation requires its separate mutation profile.");
        var connection = await Source.OpenConnectionAsync(ct);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            // Fix resolution before the first function/operator-based inspection;
            // connection startup search paths are not trusted schema admission.
            await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, transaction))
                await path.ExecuteNonQueryAsync(ct);
            if (!write)
            {
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
                await readOnly.ExecuteNonQueryAsync(ct);
            }
            await using (var inspect = new NpgsqlCommand("""
                SELECT current_user=session_user AND current_user=@profile AND NOT r.rolsuper AND NOT r.rolbypassrls
                    AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                    AND current_setting('transaction_isolation')='read committed'
                    AND current_setting('session_replication_role')='origin' AND current_setting('row_security')='on'
                    AND current_setting('server_encoding')='UTF8' AND current_setting('client_encoding')='UTF8'
                    AND current_setting('standard_conforming_strings')='on'
                    AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member=r.oid OR m.roleid=r.oid)
                    AND coalesce(nullif(current_setting('weft.organization_id',true),''),'')=''
                    AND coalesce(nullif(current_setting('weft.chat_principal_id',true),''),'')=''
                FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
                """, connection, transaction))
            {
                inspect.Parameters.AddWithValue("profile", ChatRoleGuard.RoleName(Profile));
                if (await inspect.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Chat database profile is invalid.");
            }
            await using (var settings = new NpgsqlCommand("""
                SELECT set_config('statement_timeout','15000',true),set_config('lock_timeout','5000',true),
                    set_config('search_path','pg_catalog,weft,pg_temp',true),set_config('TimeZone','UTC',true),
                    set_config('weft.chat_principal_id',@creator,true)
                """, connection, transaction))
            {
                settings.Parameters.AddWithValue("creator", actor.PrincipalId.Value.ToString("D"));
                await settings.ExecuteNonQueryAsync(ct);
            }
            var result = new PostgresTransaction(connection, transaction, organizationId);
            await result.BindOrganizationAsync(organizationId, ct);
            await ChatRoleGuard.ValidateAsync(connection, transaction, Profile, !write, ct);
            return result;
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlDataSource? source;
        lock (_lifetime) { if (_disposed) return; _disposed = true; source = _source.IsValueCreated ? _source.Value : null; }
        if (source is not null) await source.DisposeAsync();
    }
}
