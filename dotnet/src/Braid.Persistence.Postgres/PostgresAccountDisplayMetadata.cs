using System.Data;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Contracts.Auth;

namespace Braid.Persistence.Postgres;

/// <summary>Dedicated resetting pool; this credential cannot edit principals or identity bindings.</summary>
public sealed class PostgresAccountDisplayMetadata : IAccountDisplayMetadata, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    public PostgresAccountDisplayMetadata(string connectionString) =>
        source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<string?> ReadAsync(Guid organizationId, Guid bindingId, CancellationToken cancellationToken)
    {
        RequireKey(organizationId, bindingId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RequireRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT display_name FROM braid.account_display_metadata WHERE organization_id=$1 AND binding_id=$2
            """, connection, transaction);
        command.Parameters.AddWithValue(organizationId);
        command.Parameters.AddWithValue(bindingId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value is string text ? AccountDisplayName.Optional(text) : null;
    }

    public async Task ObserveAsync(Guid organizationId, Guid bindingId, string? displayName,
        DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        RequireKey(organizationId, bindingId);
        if (displayName is not null && !AccountDisplayName.IsValid(displayName) ||
            observedAtUtc.Offset != TimeSpan.Zero || observedAtUtc == DateTimeOffset.MinValue ||
            observedAtUtc == DateTimeOffset.MaxValue)
            throw new ArgumentException("The display observation is invalid.");
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RequireRoleAsync(connection, transaction, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO braid.account_display_metadata(organization_id,binding_id,display_name,observed_at)
            VALUES ($1,$2,$3,$4)
            ON CONFLICT (organization_id,binding_id) DO UPDATE
            SET display_name=EXCLUDED.display_name,observed_at=EXCLUDED.observed_at
            WHERE braid.account_display_metadata.observed_at < EXCLUDED.observed_at
            """, connection, transaction);
        command.Parameters.AddWithValue(organizationId);
        command.Parameters.AddWithValue(bindingId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)displayName ?? DBNull.Value });
        command.Parameters.AddWithValue(observedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void RequireKey(Guid organization, Guid binding)
    {
        if (organization == Guid.Empty || binding == Guid.Empty)
            throw new ArgumentException("The display metadata key is invalid.");
    }

    private static async Task RequireRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using (var path = new NpgsqlCommand("SET LOCAL search_path=pg_catalog", connection, transaction))
            await path.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='braid_account_display'
                AND rolcanlogin AND NOT rolinherit AND NOT rolsuper AND NOT rolbypassrls
                AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication
                AND current_setting('session_replication_role')='origin'
                AND current_setting('row_security')='on'
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT pg_catalog.has_schema_privilege(r.oid,'braid','CREATE')
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n
                    WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='braid' AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR (c.relname<>'account_display_metadata' AND pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES'))))
                AND NOT pg_catalog.has_column_privilege(r.oid,'braid.account_display_metadata','organization_id','UPDATE,REFERENCES')
                AND NOT pg_catalog.has_column_privilege(r.oid,'braid.account_display_metadata','binding_id','UPDATE,REFERENCES')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                    WHERE a.attrelid='braid.account_display_metadata'::regclass AND a.attnum>0 AND NOT a.attisdropped
                    AND (pg_catalog.has_column_privilege(r.oid,a.attrelid,a.attnum,'REFERENCES,SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION')
                        OR (a.attname NOT IN ('organization_id','binding_id','display_name','observed_at')
                            AND pg_catalog.has_column_privilege(r.oid,a.attrelid,a.attnum,'SELECT,INSERT,UPDATE'))))
                AND NOT pg_catalog.has_parameter_privilege(r.oid,'session_replication_role','SET,ALTER SYSTEM')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='braid' AND (p.proowner=r.oid OR pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')))
            FROM pg_catalog.pg_roles r WHERE rolname=current_user
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new InvalidOperationException("Account display metadata requires its dedicated restricted role.");
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
    public override string ToString() => nameof(PostgresAccountDisplayMetadata);
}
