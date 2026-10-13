using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Weft.Persistence.Postgres;

// The effective mapping transition originates only from the pinned embedded
// migration. Loading it is independent of catalog/ingestion table initialization.
internal static class MappingMetadataSchemaContract
{
    internal const string MigrationChecksum = "7335f18b8c5686b9e3ba3a2b9719d52e51fb22f5c07071e201bb7a239a48b113";
    internal static readonly string[] FullReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings"];
    internal static readonly string[] UpdateColumns = ["name", "description", "version", "updated_at"];
    internal static readonly string MappingTransitionBody = LoadMappingTransitionBody();

    // Do not initialize this map while the effective function body is loading:
    // the catalog's trusted shapes themselves consume the ingestion contract.
    internal static IReadOnlyDictionary<string, CatalogSchemaContract.Table> Tables =>
        FullReadTables.ToDictionary(name => name, name => CatalogSchemaContract.Tables[name], StringComparer.Ordinal);

    private static string LoadMappingTransitionBody()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations.0004_mapping_metadata.sql") ??
            throw new InvalidOperationException("The accepted mapping metadata migration is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var contents = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(contents)) != MigrationChecksum)
            throw new InvalidOperationException("The mapping metadata contract requires the accepted source migration.");
        var sql = new UTF8Encoding(false, true).GetString(contents);
        var functions = Regex.Matches(sql,
            @"CREATE OR REPLACE FUNCTION weft\.guard_mappings_transition\(\) RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS \$guard\$(?<body>.*?)\$guard\$;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (functions.Count != 1 || functions[0].Groups["body"].Value.Length == 0)
            throw new InvalidOperationException("The accepted mapping metadata transition contract is incomplete.");
        return functions[0].Groups["body"].Value;
    }
}
