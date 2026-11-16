using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Braid.Persistence.Postgres;

// The effective mapping transition originates only from the pinned embedded
// migration. Loading it is independent of catalog/ingestion table initialization.
internal static class MappingMetadataSchemaContract
{
    internal const string MigrationChecksum = "08e63461c49e7dabd64a951f0b0d0c4501f55e669cfec5b1fefb105732b807bd";
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
            "Braid.Persistence.Postgres.Migrations.0004_mapping_metadata.sql") ??
            throw new InvalidOperationException("The accepted mapping metadata migration is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var contents = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(contents)) != MigrationChecksum)
            throw new InvalidOperationException("The mapping metadata contract requires the accepted source migration.");
        var sql = new UTF8Encoding(false, true).GetString(contents);
        var functions = Regex.Matches(sql,
            @"CREATE OR REPLACE FUNCTION braid\.guard_mappings_transition\(\) RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, braid AS \$guard\$(?<body>.*?)\$guard\$;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (functions.Count != 1 || functions[0].Groups["body"].Value.Length == 0)
            throw new InvalidOperationException("The accepted mapping metadata transition contract is incomplete.");
        return functions[0].Groups["body"].Value;
    }
}
