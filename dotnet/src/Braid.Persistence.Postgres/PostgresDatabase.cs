using System.Data;
using Npgsql;

namespace Braid.Persistence.Postgres;

/// <summary>A restricted runtime pool that only leases organization-bound read transactions.</summary>
public sealed class PostgresDatabase : IAsyncDisposable
{
    private bool _disposed;

    public PostgresDatabase(string runtimeConnectionString)
    {
        DataSource = NpgsqlDataSource.Create(PostgresConfiguration.Normalize(runtimeConnectionString));
    }

    // The friend test assembly uses this only to prove reset on the very same physical pool.
    internal NpgsqlDataSource DataSource { get; }

    public async Task<PostgresTransaction> BeginTransactionAsync(Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));

        var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                await ValidateRuntimeAsync(connection, transaction, cancellationToken);
                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                    await readOnly.ExecuteNonQueryAsync(cancellationToken);
                var scope = new PostgresTransaction(connection, transaction, organizationId);
                await scope.BindOrganizationAsync(organizationId, cancellationToken);
                return scope;
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DataSource.DisposeAsync();
    }

    private static async Task ValidateRuntimeAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH reachable_roles AS (
                SELECT role.* FROM pg_catalog.pg_roles role
                WHERE role.rolname = session_user
                   OR pg_catalog.pg_has_role(session_user, role.oid, 'MEMBER')
            ), read_tables AS (
                SELECT relation.oid, relation.relname, relation.relrowsecurity
                FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                WHERE namespace.nspname = 'braid'
                  AND relation.relkind = 'r'
                  AND relation.relname IN ('organizations', 'principals', 'organization_memberships',
                                           'projects', 'project_memberships')
            )
            SELECT current_user = session_user
              AND current_setting('session_replication_role') = 'origin'
              AND current_setting('row_security') = 'on'
              AND NOT pg_catalog.has_schema_privilege('public', 'braid', 'USAGE,CREATE')
              AND (SELECT count(*) = 5 FROM read_tables)
              AND NOT EXISTS (
                  SELECT 1 FROM read_tables
                  WHERE NOT pg_catalog.has_table_privilege(oid, 'SELECT')
                     OR (relname <> 'principals' AND NOT relrowsecurity)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM reachable_roles role
                  WHERE role.rolsuper OR role.rolbypassrls OR role.rolcreaterole
                     OR role.rolcreatedb OR role.rolreplication
                     OR role.rolname IN ('braid_migrator', 'pg_read_all_data', 'pg_write_all_data',
                                         'pg_read_server_files', 'pg_write_server_files',
                                         'pg_execute_server_program', 'pg_signal_backend', 'pg_maintain')
                     OR pg_catalog.has_database_privilege(role.oid, current_database(), 'CREATE,CONNECT WITH GRANT OPTION,TEMPORARY WITH GRANT OPTION')
                     OR pg_catalog.has_parameter_privilege(role.oid, 'session_replication_role', 'SET,ALTER SYSTEM')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_namespace namespace
                  CROSS JOIN reachable_roles role
                  WHERE pg_catalog.left(namespace.nspname,8)<>'pg_temp_'
                    AND pg_catalog.left(namespace.nspname,14)<>'pg_toast_temp_'
                    AND (namespace.nspowner = role.oid
                         OR pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'CREATE')
                         OR (namespace.nspname = 'braid' AND pg_catalog.has_schema_privilege(role.oid, namespace.oid, 'USAGE WITH GRANT OPTION')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class relation
                  JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                  CROSS JOIN reachable_roles role
                  WHERE namespace.nspname = 'braid'
                    AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                    AND (relation.relowner = role.oid
                         OR pg_catalog.has_table_privilege(role.oid, relation.oid,
                             'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER,SELECT WITH GRANT OPTION')
                         OR pg_catalog.has_any_column_privilege(role.oid, relation.oid,
                             'INSERT,UPDATE,REFERENCES,SELECT WITH GRANT OPTION')
                         OR (relation.relname NOT IN ('organizations', 'principals',
                                                      'organization_memberships', 'projects', 'project_memberships')
                             AND pg_catalog.has_any_column_privilege(role.oid, relation.oid, 'SELECT')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class relation
                  JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
                  WHERE namespace.nspname = 'braid' AND relation.relkind IN ('r','p','v','m','f')
                    AND (pg_catalog.has_table_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                         OR pg_catalog.has_any_column_privilege('public', relation.oid, 'SELECT,INSERT,UPDATE,REFERENCES'))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_proc function
                  JOIN pg_catalog.pg_namespace namespace ON namespace.oid = function.pronamespace
                  WHERE namespace.nspname = 'braid'
                    AND (pg_catalog.has_function_privilege('public', function.oid, 'EXECUTE')
                         OR EXISTS (SELECT 1 FROM reachable_roles role WHERE pg_catalog.has_function_privilege(role.oid, function.oid, 'EXECUTE')))
              )
              AND NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_default_acl defaults
                  CROSS JOIN LATERAL pg_catalog.aclexplode(defaults.defaclacl) grant_entry
                  WHERE defaults.defaclnamespace IN (0, (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'braid'))
                    AND (grant_entry.grantee = 0 OR grant_entry.grantee IN (SELECT oid FROM reachable_roles))
              )
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException(
                "Runtime access requires installed ownership tables and a separate read-only role without ownership, privileged memberships, RLS bypass, or schema/database creation rights.");

        await ValidateOrganizationPoliciesAsync(connection, transaction, cancellationToken);
    }

    private static async Task ValidateOrganizationPoliciesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Keep policy DDL from racing the admission check and subsequent project read.
        // ACCESS SHARE permits ordinary membership/metadata changes and needs only SELECT.
        await using (var protect = new NpgsqlCommand("""
            LOCK TABLE ONLY braid.organizations, ONLY braid.organization_memberships,
                ONLY braid.projects, ONLY braid.project_memberships IN ACCESS SHARE MODE
            """, connection, transaction))
            await protect.ExecuteNonQueryAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            WITH expected(table_name, policy_name, expression) AS (
                VALUES ('organizations', 'organizations_organization_scope', $1::text),
                       ('organization_memberships', 'organization_memberships_organization_scope', $2::text),
                       ('projects', 'projects_organization_scope', $2::text),
                       ('project_memberships', 'project_memberships_organization_scope', $2::text)
            )
            SELECT NOT EXISTS (
                SELECT 1 FROM expected
                LEFT JOIN pg_catalog.pg_class relation
                    ON relation.relname = expected.table_name
                   AND relation.relnamespace = (
                       SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = 'braid')
                LEFT JOIN pg_catalog.pg_policy policy ON policy.polrelid = relation.oid
                WHERE relation.oid IS NULL OR relation.relkind <> 'r' OR NOT relation.relrowsecurity
                   OR policy.oid IS NULL OR policy.polname <> expected.policy_name
                   OR policy.polcmd <> '*' OR NOT policy.polpermissive
                   OR policy.polroles <> ARRAY[0::oid]
                   OR (SELECT count(*) FROM pg_catalog.pg_policy other
                       WHERE other.polrelid = relation.oid) <> 1
                   OR pg_catalog.pg_get_expr(policy.polqual, policy.polrelid, false)
                        IS DISTINCT FROM expected.expression
                   OR pg_catalog.pg_get_expr(policy.polwithcheck, policy.polrelid, false)
                        IS DISTINCT FROM expected.expression
            )
            """, connection, transaction);
        // Match the qualified PostgreSQL 16 representation exactly. Broad whitespace or
        // token normalization could accidentally also rewrite a security-sensitive literal.
        const string right = " = (NULLIF(current_setting('braid.organization_id'::text, true), ''::text))::uuid)";
        command.Parameters.AddWithValue("(id" + right);
        command.Parameters.AddWithValue("(organization_id" + right);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("The installed organization row-security policies do not match the accepted ownership guard.");
    }
}
