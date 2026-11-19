using Npgsql;

namespace Braid.Persistence.Postgres;

// Exact post-upgrade catalog, independent of row contents. Fail closed on a partial
// install or a changed routine, trigger, index, policy, constraint, owner or grant.
internal static class LifecycleCatalogAdmission
{
    internal static async Task<bool> InstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass('braid.resource_lifecycles') IS NOT NULL", connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    internal static async Task<bool> ValidateIfInstalledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await InstalledAsync(connection, transaction, token)) return false;
        await ValidateAsync(connection, transaction, token);
        return true;
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(CaptureSql, connection, transaction);
        var fingerprint = await command.ExecuteScalarAsync(token) as string;
        if (fingerprint is Fingerprint or NativeProfileFingerprint or RetentionHookFingerprint) return;
        // Migration 0026 omits presentation-only column grants when its optional login is absent.
        // A missing grant on a provisioned login is still drift, not an optional installation.
        if (fingerprint is WithoutDisplayRetentionHookFingerprint)
        {
            await using var role = new NpgsqlCommand("SELECT pg_catalog.to_regrole('braid_account_display') IS NULL", connection, transaction);
            if (await role.ExecuteScalarAsync(token) is true) return;
        }
        throw new InvalidOperationException("Lifecycle database contract unavailable.");
    }
    internal const string Fingerprint = "ee9c0db73af786f871e3360f24a99a88330c8274e5fdc550429bcb81b513f022:f5f76616e49cb92f20bcbfe76352fb1f6caa0a737238188fe1471fdf70953d8d";
    internal const string NativeProfileFingerprint = "30d4a7118a07043cf68a7957ed813edf32bfe0e6a3609ca1d5d44aaac45acc13:5d151cd13813fdbe63905e329c8beea5bb46c686beba6183761540b9a2f6900d";
    internal const string RetentionHookFingerprint = "30d4a7118a07043cf68a7957ed813edf32bfe0e6a3609ca1d5d44aaac45acc13:2fa9afd22e15bc2f6010227c1fff393ec49eb9770a974f16bc6a89e371244e16";
    private const string WithoutDisplayRetentionHookFingerprint = "08531c89c1d3c90505769ad9b8b71420ff217f7321d5e288f4a6402ad4f5ef8d:2fa9afd22e15bc2f6010227c1fff393ec49eb9770a974f16bc6a89e371244e16";
    internal const string CaptureSql = """
        SELECT (SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(
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
        FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class index_relation ON index_relation.oid=i.indexrelid
        WHERE i.indrelid=c.oid),
        'triggers', (SELECT jsonb_agg(jsonb_build_array(t.tgname,t.tgenabled::text,
        pg_catalog.pg_get_triggerdef(t.oid,false)) ORDER BY t.tgname)
        FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal),
        'internalTriggersEnabled', (SELECT coalesce(bool_and(t.tgenabled='O'),true)
        FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND t.tgisinternal),
        'policies', (SELECT jsonb_agg(jsonb_build_array(p.polname,p.polcmd::text,p.polpermissive,(SELECT jsonb_agg(CASE WHEN role_id=0 THEN 'PUBLIC' ELSE pg_get_userbyid(role_id)::text END ORDER BY CASE WHEN role_id=0 THEN 'PUBLIC' ELSE pg_get_userbyid(role_id)::text END) FROM unnest(p.polroles) role_id),
        pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false))
        ORDER BY p.polname) FROM pg_catalog.pg_policy p WHERE p.polrelid=c.oid),
        'inheritance', (SELECT count(*) FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
        ) ORDER BY c.relname),'[]'::jsonb)::text,'UTF8')),'hex')
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='braid' AND c.relkind IN ('r','p','v','m','f'))||':'||(SELECT encode(sha256(convert_to(coalesce(jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),pg_get_userbyid(p.proowner),p.proacl::text) ORDER BY p.oid::regprocedure::text COLLATE "C"),'[]'::jsonb)::text,'UTF8')),'hex') FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='braid')
        """;
}
