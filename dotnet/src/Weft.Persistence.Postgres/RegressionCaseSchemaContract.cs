using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class RegressionCaseSchemaContract
{
    // A schema-only golden value; object OIDs, row data and statistics are deliberately excluded.
    internal const string Fingerprint = "c054c65a63a2b31d8d4064d16227407d169b2aefa742eae8683019a1c1eb8d1c";

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (await CaptureAsync(connection, transaction, token) != Fingerprint)
            throw new InvalidOperationException("Regression case schema does not match its qualified contract.");
    }

    internal static async Task<string> CaptureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => await CaptureAsync(connection, transaction, ["regression_cases", "regression_case_versions"], token);

    internal static async Task<string> CaptureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] tables, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(CaptureSql, connection, transaction);
        command.Parameters.AddWithValue("tables", tables);
        return await command.ExecuteScalarAsync(token) as string ?? throw new InvalidOperationException("Regression case schema is unavailable.");
    }

    private const string CaptureSql = """
        SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
            coalesce(jsonb_agg(jsonb_build_object(
                'name', c.relname, 'owner', pg_catalog.pg_get_userbyid(c.relowner),
                'kind', c.relkind::text, 'persistence', c.relpersistence::text,
                'rowSecurity', c.relrowsecurity, 'forceRowSecurity', c.relforcerowsecurity,
                'partition', c.relispartition, 'rules', c.relhasrules, 'options', c.reloptions,
                'grants', (SELECT jsonb_agg(jsonb_build_array(
                    CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                    pg_catalog.pg_get_userbyid(a.grantor)::text,a.privilege_type,a.is_grantable)
                    ORDER BY a.grantee=0,pg_catalog.pg_get_userbyid(a.grantee)::text,a.privilege_type,a.is_grantable)
                    FROM pg_catalog.aclexplode(coalesce(c.relacl,pg_catalog.acldefault('r',c.relowner))) a),
                'columns', (SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,
                    pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attisdropped,
                    a.attidentity::text,a.attgenerated::text,cn.nspname,cl.collname,
                    pg_catalog.pg_get_expr(d.adbin,d.adrelid,false),
                    (SELECT jsonb_agg(jsonb_build_array(
                        CASE WHEN ca.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(ca.grantee)::text END,
                        pg_catalog.pg_get_userbyid(ca.grantor)::text,ca.privilege_type,ca.is_grantable)
                        ORDER BY ca.grantee=0,pg_catalog.pg_get_userbyid(ca.grantee)::text,ca.privilege_type,ca.is_grantable)
                        FROM pg_catalog.aclexplode(a.attacl) ca)) ORDER BY a.attnum)
                    FROM pg_catalog.pg_attribute a
                    LEFT JOIN pg_catalog.pg_collation cl ON cl.oid=a.attcollation
                    LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid=cl.collnamespace
                    LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                    WHERE a.attrelid=c.oid AND a.attnum>0),
                'constraints', (SELECT jsonb_agg(jsonb_build_array(k.conname,k.contype::text,k.convalidated,
                    k.condeferrable,k.condeferred,k.connoinherit,pg_catalog.pg_get_constraintdef(k.oid,false))
                    ORDER BY k.conname) FROM pg_catalog.pg_constraint k WHERE k.conrelid=c.oid),
                'indexes', (SELECT jsonb_agg(jsonb_build_array(pg_catalog.pg_get_indexdef(i.indexrelid),
                    i.indisvalid,i.indisready,i.indislive) ORDER BY pg_catalog.pg_get_indexdef(i.indexrelid))
                    FROM pg_catalog.pg_index i WHERE i.indrelid=c.oid),
                'triggers', (SELECT jsonb_agg(jsonb_build_array(t.tgname,t.tgenabled::text,
                    pg_catalog.pg_get_triggerdef(t.oid,false)) ORDER BY t.tgname)
                    FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal),
                'internalTriggersEnabled', (SELECT coalesce(bool_and(t.tgenabled='O'),true)
                    FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND t.tgisinternal),
                'policies', (SELECT jsonb_agg(jsonb_build_array(p.polname,p.polcmd::text,p.polpermissive,p.polroles,
                    pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false))
                    ORDER BY p.polname) FROM pg_catalog.pg_policy p WHERE p.polrelid=c.oid),
                'inheritance', (SELECT count(*) FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            ) ORDER BY c.relname),'[]'::jsonb)::text,'UTF8')),'hex')
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='weft' AND c.relname = ANY(@tables)
        """;
}
