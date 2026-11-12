using System.Text.RegularExpressions;
using Npgsql;

namespace Weft.Persistence.Postgres;

internal static class DocumentJsonSchemaAdmission
{
    internal const string MigrationChecksum = "464d284a1c33be37fd0458b79362f947f816f351541174c7fc8ae6ade4ccf31e";
    internal const string FunctionName = "document_json_unique";
    private static readonly Lazy<Dictionary<string, Match>> Definitions = new(() =>
    {
        if (MigrationRunner.EmbeddedMigrations.Single(script => script.Version == 47).Checksum != MigrationChecksum)
            throw Invalid();
        return BuildRoleGuard.LoadFunctions("0047_document_json_contract.sql", 1);
    });

    internal static async Task ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await BuildRoleGuard.ValidateFunctionsAsync(connection, transaction, [FunctionName], Definitions.Value, token);
        // This recursive helper executes only as the owner through the existing
        // restricted XML entry points. It grants no new caller capability.
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=1 AND coalesce(bool_and(a.grantee=p.proowner AND a.grantor=p.proowner
                AND a.privilege_type='EXECUTE' AND NOT a.is_grantable
                AND p.provariadic=0 AND p.prosupport=0 AND NOT p.proretset
                AND p.proallargtypes IS NULL AND p.proargmodes IS NULL),false)
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(coalesce(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
            WHERE n.nspname='weft' AND p.proname='document_json_unique'
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(token) is not true) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Document JSON schema is unavailable.");
}
