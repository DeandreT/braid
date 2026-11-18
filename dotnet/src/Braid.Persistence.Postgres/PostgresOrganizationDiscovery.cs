using System.Data;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;

namespace Braid.Persistence.Postgres;

/// <summary>
/// The restricted discovery login holds only schema usage and EXECUTE on one bounded function.
/// The adapter verifies its login attributes and the exact function contract before first use.
/// </summary>
internal static class OrganizationDiscoveryRoleGuard
{
    private static readonly Lazy<Match> Definition = new(() =>
        BuildRoleGuard.LoadFunctions("0057_organization_switching.sql", 4)["discover_organization_admissions"]);

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using (var command = new NpgsqlCommand("""
            SELECT current_user=session_user AND current_user='braid_organization_discovery' AND r.rolcanlogin AND NOT r.rolsuper
                AND NOT r.rolbypassrls AND NOT r.rolinherit AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND r.rolconfig IS NULL AND r.rolvaliduntil IS NULL AND current_setting('row_security')='on'
                AND current_setting('session_replication_role')='origin' AND current_setting('transaction_isolation')='read committed'
                AND current_setting('transaction_read_only')='on'
                AND coalesce(nullif(current_setting('braid.organization_id',true),''),'')=''
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_auth_members WHERE member=r.oid OR roleid=r.oid)
                AND NOT pg_catalog.has_database_privilege(r.oid,current_database(),'CREATE,TEMPORARY,CONNECT WITH GRANT OPTION')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_namespace n WHERE n.nspowner=r.oid OR pg_catalog.has_schema_privilege(r.oid,n.oid,'CREATE'))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND c.relkind IN ('r','p','v','m','f')
                    AND (c.relowner=r.oid OR pg_catalog.has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR pg_catalog.has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                        AND NOT(n.nspname='braid' AND p.proname='discover_organization_admissions'))
            FROM pg_catalog.pg_roles r WHERE r.rolname=current_user
            """, connection, transaction))
        {
            if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
        }
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, ["discover_organization_admissions"],
            new Dictionary<string, Match>(StringComparer.Ordinal) { ["discover_organization_admissions"] = Definition.Value }, token);
    }

    private static InvalidOperationException Invalid() => new("Organization discovery database contract unavailable.");
}

/// <summary>Bounded discovery reads through the dedicated login; results are re-validated, never trusted.</summary>
public sealed class PostgresOrganizationDiscovery(string connectionString) : IOrganizationDiscovery, IAsyncDisposable
{
    private readonly NpgsqlDataSource source = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(connectionString));

    public async Task<OrganizationDiscoveryPage?> DiscoverAsync(OrganizationDiscoveryIdentity identity, int limit,
        Guid? after, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (limit is < 1 or > 100 || after == Guid.Empty)
            throw new ArgumentException("The organization discovery page request is invalid.", nameof(limit));
        var connection = await source.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var settings = new NpgsqlCommand(
                "SET TRANSACTION READ ONLY; SET LOCAL search_path=pg_catalog; SET LOCAL statement_timeout='5s'; SET LOCAL lock_timeout='1s'", connection, transaction))
                await settings.ExecuteNonQueryAsync(cancellationToken);
            await OrganizationDiscoveryRoleGuard.ValidateAsync(connection, transaction, cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT organization_id, organization_name FROM braid.discover_organization_admissions(@binding,@version,@principal,@after,@fetch)",
                connection, transaction);
            command.Parameters.AddWithValue("binding", identity.BindingId);
            command.Parameters.AddWithValue("version", identity.BindingVersion);
            command.Parameters.AddWithValue("principal", identity.PrincipalId.Value);
            command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("fetch", limit + 1);
            var items = new List<OrganizationDiscoveryItem>(limit + 1);
            var last = Guid.Empty;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var name = reader.GetString(1);
                    if (id == Guid.Empty || id <= last || name.Length is 0 or > 200 || items.Count > limit)
                        throw new InvalidOperationException("Organization discovery returned inconsistent results.");
                    items.Add(new(id, name));
                    last = id;
                }
            }
            Guid? next = null;
            if (items.Count > limit)
            {
                items.RemoveAt(limit);
                next = items[^1].OrganizationId;
            }
            await transaction.CommitAsync(cancellationToken);
            return new(items, next);
        }
        catch (NpgsqlException)
        {
            throw new InvalidOperationException("Organization discovery persistence failed.");
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
