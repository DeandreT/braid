using System.Reflection;
using System.Security.Cryptography;

namespace Weft.Persistence.Postgres;

// Expected source definitions are compiled from the accepted migrations, never
// adopted from the database being admitted. The reader needs no private-row rights.
internal static class CatalogSchemaContract
{
    internal sealed record Table(string Name, IngestionSchemaContract.Column[] Columns,
        IngestionSchemaContract.Constraint[] Constraints, string? FunctionBody);

    internal static readonly string[] FullReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions"];
    internal static readonly string[] ReservationReadColumns =
        ["organization_id", "project_id", "operation_id", "display_filename"];
    internal static readonly IReadOnlyDictionary<string, Table> Tables = Load();

    // Canonical PostgreSQL 16.15 representation of the trusted 0001 checks.
    // Source SHA256: 0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1
    internal static readonly IReadOnlyDictionary<string, string> LegacyChecks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["organizations_id_check"] = "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))",
        ["organizations_name_check"] = "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))",
        ["principals_id_check"] = "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))",
        ["projects_id_check"] = "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))",
        ["projects_name_check"] = "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))",
        ["projects_description_check"] = "CHECK ((char_length(description) <= 4000))",
        ["projects_version_check"] = "CHECK ((version > 0))",
        ["projects_check"] = "CHECK ((updated_at >= created_at))",
        ["project_memberships_role_check"] = "CHECK ((role = ANY (ARRAY['viewer'::text, 'runner'::text, 'publisher'::text, 'administrator'::text])))",
        ["project_memberships_version_check"] = "CHECK ((version > 0))"
    };

    internal static bool TryGetCheck(string name, out string? definition)
        => LegacyChecks.TryGetValue(name, out definition) || IngestionCheckDefinitions.Values.TryGetValue(name, out definition);

    private static IReadOnlyDictionary<string, Table> Load()
    {
        RequireMigration("0001_project_ownership", "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1");
        RequireMigration("0002_catalog_ingestion", "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5");
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal)
        {
            ["organizations"] = new("organizations",
                [Column("id", "uuid"), Column("name", "text"), Column("created_at", "timestamp with time zone", "CURRENT_TIMESTAMP")],
                [Key("organizations_pkey", "id"), Check("organizations_id_check"), Check("organizations_name_check")], null),
            ["principals"] = new("principals",
                [Column("id", "uuid"), Column("active", "boolean", "true"), Column("created_at", "timestamp with time zone", "CURRENT_TIMESTAMP")],
                [Key("principals_pkey", "id"), Check("principals_id_check")], null),
            ["organization_memberships"] = new("organization_memberships",
                [Column("organization_id", "uuid"), Column("principal_id", "uuid"), Column("active", "boolean", "true")],
                [Key("organization_memberships_pkey", "organization_id", "principal_id"),
                 ForeignKey("organization_memberships_organization_id_fkey", ["organization_id"], "organizations", ["id"]),
                 ForeignKey("organization_memberships_principal_id_fkey", ["principal_id"], "principals", ["id"])], null),
            ["projects"] = new("projects",
                [Column("organization_id", "uuid"), Column("id", "uuid"), Column("name", "text"), Column("description", "text", "''::text"),
                 Column("creator_principal_id", "uuid"), Column("created_at", "timestamp with time zone", "CURRENT_TIMESTAMP"),
                 Column("updated_at", "timestamp with time zone", "CURRENT_TIMESTAMP"), Column("version", "bigint", "1")],
                [Key("projects_pkey", "organization_id", "id"),
                 ForeignKey("projects_organization_id_fkey", ["organization_id"], "organizations", ["id"]),
                 ForeignKey("projects_organization_id_creator_principal_id_fkey", ["organization_id", "creator_principal_id"], "organization_memberships", ["organization_id", "principal_id"]),
                 Check("projects_id_check"), Check("projects_name_check"), Check("projects_description_check"), Check("projects_version_check"), Check("projects_check")], null),
            ["project_memberships"] = new("project_memberships",
                [Column("organization_id", "uuid"), Column("project_id", "uuid"), Column("principal_id", "uuid"),
                 Column("role", "text"), Column("active", "boolean", "true"), Column("version", "bigint", "1")],
                [Key("project_memberships_pkey", "organization_id", "project_id", "principal_id"),
                 ForeignKey("project_memberships_organization_id_project_id_fkey", ["organization_id", "project_id"], "projects", ["organization_id", "id"]),
                 ForeignKey("project_memberships_organization_id_principal_id_fkey", ["organization_id", "principal_id"], "organization_memberships", ["organization_id", "principal_id"]),
                 Check("project_memberships_role_check"), Check("project_memberships_version_check")], null)
        };
        foreach (var name in new[] { "mappings", "mapping_revisions", "revision_ingestions" })
        {
            var source = IngestionSchemaContract.Tables[name];
            tables.Add(name, new Table(name, source.Columns, source.Constraints, source.FunctionBody));
        }
        return tables;
    }

    private static IngestionSchemaContract.Column Column(string name, string type, string? defaultValue = null)
        => new(name, type, true, defaultValue);

    private static IngestionSchemaContract.Constraint Key(string name, params string[] columns)
        => new(name, 'p', columns, null, [], false);

    private static IngestionSchemaContract.Constraint Check(string name)
        => new(name, 'c', [], null, [], false);

    private static IngestionSchemaContract.Constraint ForeignKey(string name, string[] columns, string target, string[] referencedColumns)
        => new(name, 'f', columns, target, referencedColumns, false);

    private static void RequireMigration(string name, string expectedDigest)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations." + name + ".sql") ??
            throw new InvalidOperationException("The accepted catalog source migration is missing.");
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != expectedDigest)
            throw new InvalidOperationException("The catalog schema contract requires the accepted source migrations.");
    }
}
