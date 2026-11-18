using Npgsql;

namespace Braid.Persistence.Postgres;

internal static class RegressionCaseSchemaContract
{
    // A schema-only golden value; object OIDs, row data and statistics are deliberately excluded.
    internal const string Fingerprint = "4043a14d8115b79698b8851cead7b3b78ee874f94f7a2316c7b4a73d28baa28a";

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (await CaptureAsync(connection, transaction, token) != Fingerprint)
            throw new InvalidOperationException("Regression case schema does not match its qualified contract.");
    }

    internal static async Task<string> CaptureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => await CaptureAsync(connection, transaction, ["regression_cases", "regression_case_versions"], token);

    internal static async Task<string> CaptureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] tables, CancellationToken token,
        bool validateAutomation = true, bool validateAudit = true, bool validateUsage = true, bool validateQuota = true)
    {
        // This admits complete Automation or rejects any partial installation
        // before projecting its triggers away. The nonrecursive leaf capture is
        // used by that admission itself and must keep the same projection.
        if (validateAutomation) await AutomationSchemaAdmission.ValidateTriggersAsync(connection, transaction, token);
        if (validateAutomation) await OrganizationSwitchingSchemaAdmission.ValidateAsync(connection, transaction, token);
        if (validateAudit) await AuditLogSchemaAdmission.ValidateAsync(connection, transaction, token);
        if (validateUsage) await AiUsageSchemaAdmission.ValidateAsync(connection, transaction, token);
        if (validateQuota) await AiQuotaSchemaAdmission.ValidateAsync(connection, transaction, token);
        // Physical search indexes do not alter the logical source contracts. Omit
        // only this exact additive set, after its complete definitions and private
        // extension boundary have been admitted; every other index stays pinned.
        var searchIndexes = tables.Any(table => table is "projects" or "mappings" or "mapping_revisions" or "revision_ingestions") &&
            await CatalogSearchSchemaAdmission.ValidateAsync(connection, transaction, token)
            ? CatalogSearchSchemaAdmission.IndexNames : [];
        await using (var foreignKeys = new NpgsqlCommand("""
            SELECT NOT EXISTS (
                SELECT 1 FROM pg_catalog.pg_constraint c
                JOIN pg_catalog.pg_class r ON r.oid=c.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace
                WHERE n.nspname='braid' AND r.relname=ANY(@tables) AND c.contype='f'
                    AND (SELECT count(*)=4 AND bool_and(t.tgisinternal AND t.tgenabled='O'
                        AND t.tgrelid IN (c.conrelid,c.confrelid)
                        AND t.tgconstrrelid=CASE WHEN t.tgrelid=c.conrelid THEN c.confrelid ELSE c.conrelid END)
                        AND (c.conrelid=c.confrelid OR (count(*) FILTER(WHERE t.tgrelid=c.conrelid)=2
                            AND count(*) FILTER(WHERE t.tgrelid=c.confrelid)=2))
                        FROM pg_catalog.pg_trigger t WHERE t.tgconstraint=c.oid) IS NOT TRUE
            )
            """, connection, transaction))
        {
            foreignKeys.Parameters.AddWithValue("tables", tables);
            if (await foreignKeys.ExecuteScalarAsync(token) is not true)
                throw new InvalidOperationException("Regression foreign-key enforcement is incomplete.");
        }
        await using var command = new NpgsqlCommand(CaptureSql, connection, transaction);
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("automationTriggers", AutomationSchemaAdmission.AllTriggerNames.Concat(OrganizationSwitchingSchemaAdmission.AllTriggerNames).Concat(AuditLogSchemaAdmission.TriggerNames).Concat(AiUsageSchemaAdmission.TriggerNames).Concat(AiQuotaSchemaAdmission.TriggerNames).ToArray());
        command.Parameters.AddWithValue("searchIndexes", searchIndexes);
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
                    FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class index_relation ON index_relation.oid=i.indexrelid
                    WHERE i.indrelid=c.oid AND NOT (index_relation.relnamespace=c.relnamespace AND index_relation.relname=ANY(@searchIndexes))),
                'triggers', (SELECT jsonb_agg(jsonb_build_array(t.tgname,t.tgenabled::text,
                    pg_catalog.pg_get_triggerdef(t.oid,false)) ORDER BY t.tgname)
                    FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal AND NOT t.tgname=ANY(@automationTriggers)),
                'internalTriggersEnabled', (SELECT coalesce(bool_and(t.tgenabled='O'),true)
                    FROM pg_catalog.pg_trigger t WHERE t.tgrelid=c.oid AND t.tgisinternal),
                'policies', (SELECT jsonb_agg(jsonb_build_array(p.polname,p.polcmd::text,p.polpermissive,p.polroles,
                    pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false))
                    ORDER BY p.polname) FROM pg_catalog.pg_policy p WHERE p.polrelid=c.oid),
                'inheritance', (SELECT count(*) FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)
            ) ORDER BY c.relname),'[]'::jsonb)::text,'UTF8')),'hex')
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='braid' AND c.relname = ANY(@tables)
        """;
}
