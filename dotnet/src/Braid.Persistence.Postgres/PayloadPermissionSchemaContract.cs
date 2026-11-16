using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Braid.Persistence.Postgres;

// Admission uses exact embedded migration bytes and source-owned schema definitions.
internal static partial class PayloadPermissionSchemaContract
{
    internal const string MigrationName = "0009_project_payload_read_grants";
    internal const string MigrationChecksum = "5476583339ee653d61858efba72eb05e270a0e7c26f60962bc2c1ece97a426fe";
    internal const string SearchPath = "search_path=pg_catalog, braid, pg_temp";
    internal const string GrantTable = "project_payload_read_grants";
    internal static readonly string[] ReaderTables =
        ["principals", "organization_memberships", "projects", "project_memberships", GrantTable];
    // Revocation requires no active parents, but still validates their trusted definitions.
    internal static readonly string[] OwnerTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", GrantTable];

    private sealed record Column(string Name, string Type, bool NotNull = true, string? Default = null, bool CCollation = false);
    private sealed record Constraint(string Name, char Kind, string[] Columns, string? ReferencedTable = null,
        string[]? ReferencedColumns = null, string? Definition = null);
    private sealed record Table(string Name, Column[] Columns, Constraint[] Constraints, bool Scoped);
    private sealed record Function(string Name, string[] ArgumentNames, string[] ArgumentTypes, string ReturnType,
        char Volatility, char Parallel, bool Strict, string Body = "");

    private static Constraint Key(string name, params string[] columns) => new(name, 'p', columns);
    private static Constraint Check(string name, string definition) => new(name, 'c', [], Definition: definition);
    private static Constraint Foreign(string name, string[] columns, string target, string[] referenced) =>
        new(name, 'f', columns, target, referenced);

    private static readonly IReadOnlyDictionary<string, Table> Tables = new Dictionary<string, Table>(StringComparer.Ordinal)
    {
        ["organizations"] = new("organizations",
            [new("id", "uuid"), new("name", "text"), new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
            [Key("organizations_pkey", "id"),
             Check("organizations_id_check", "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
             Check("organizations_name_check", "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))")], true),
        ["principals"] = new("principals",
            [new("id", "uuid"), new("active", "boolean", Default: "true"), new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
            [Key("principals_pkey", "id"),
             Check("principals_id_check", "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))")], false),
        ["organization_memberships"] = new("organization_memberships",
            [new("organization_id", "uuid"), new("principal_id", "uuid"), new("active", "boolean", Default: "true")],
            [Key("organization_memberships_pkey", "organization_id", "principal_id"),
             Foreign("organization_memberships_organization_id_fkey", ["organization_id"], "organizations", ["id"]),
             Foreign("organization_memberships_principal_id_fkey", ["principal_id"], "principals", ["id"])], true),
        ["projects"] = new("projects",
            [new("organization_id", "uuid"), new("id", "uuid"), new("name", "text"), new("description", "text", Default: "''::text"),
             new("creator_principal_id", "uuid"), new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP"),
             new("updated_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP"), new("version", "bigint", Default: "1")],
            [Key("projects_pkey", "organization_id", "id"),
             Foreign("projects_organization_id_fkey", ["organization_id"], "organizations", ["id"]),
             Foreign("projects_organization_id_creator_principal_id_fkey", ["organization_id", "creator_principal_id"], "organization_memberships", ["organization_id", "principal_id"]),
             Check("projects_id_check", "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
             Check("projects_name_check", "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))"),
             Check("projects_description_check", "CHECK ((char_length(description) <= 4000))"),
             Check("projects_version_check", "CHECK ((version > 0))"),
             Check("projects_check", "CHECK ((updated_at >= created_at))")], true),
        ["project_memberships"] = new("project_memberships",
            [new("organization_id", "uuid"), new("project_id", "uuid"), new("principal_id", "uuid"), new("role", "text"),
             new("active", "boolean", Default: "true"), new("version", "bigint", Default: "1")],
            [Key("project_memberships_pkey", "organization_id", "project_id", "principal_id"),
             Foreign("project_memberships_organization_id_project_id_fkey", ["organization_id", "project_id"], "projects", ["organization_id", "id"]),
             Foreign("project_memberships_organization_id_principal_id_fkey", ["organization_id", "principal_id"], "organization_memberships", ["organization_id", "principal_id"]),
             Check("project_memberships_role_check", "CHECK ((role = ANY (ARRAY['viewer'::text, 'runner'::text, 'publisher'::text, 'administrator'::text])))"),
             Check("project_memberships_version_check", "CHECK ((version > 0))")], true),
        [GrantTable] = new(GrantTable,
            [new("organization_id", "uuid"), new("project_id", "uuid"), new("principal_id", "uuid"),
             new("active", "boolean", Default: "true"), new("version", "bigint", Default: "1")],
            [Key("project_payload_read_grants_pkey", "organization_id", "project_id", "principal_id"),
             Foreign("project_payload_read_grants_project_fkey", ["organization_id", "project_id"], "projects", ["organization_id", "id"]),
             Foreign("project_payload_read_grants_membership_fkey", ["organization_id", "project_id", "principal_id"], "project_memberships", ["organization_id", "project_id", "principal_id"]),
             Check("project_payload_read_grants_ids_check", "CHECK (((organization_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (project_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (principal_id <> '00000000-0000-0000-0000-000000000000'::uuid)))"),
             Check("project_payload_read_grants_version_check", "CHECK ((version > 0))")], true)
    };

    private static readonly Lazy<IReadOnlyDictionary<string, Function>> Functions = new(LoadFunctions);

    private static IReadOnlyDictionary<string, Function> LoadFunctions()
    {
        RequireSource("0001_project_ownership", "f0875300876276f876ff403ec0365fe8c245cf0bf4af10e1f2841d9c4ad14aed");
        var sql = RequireSource(MigrationName, MigrationChecksum);
        const string name = "guard_project_payload_read_grants_transition";
        var opening = "AS $braid_" + name + "$";
        var closing = "$braid_" + name + "$;";
        var start = sql.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0 || sql.IndexOf(opening, start + opening.Length, StringComparison.Ordinal) >= 0)
            throw Refusal("The trusted payload permission transition source is incomplete.");
        start += opening.Length;
        var end = sql.IndexOf(closing, start, StringComparison.Ordinal);
        if (end <= start)
            throw Refusal("The trusted payload permission transition source is incomplete.");
        return new Dictionary<string, Function>(StringComparer.Ordinal)
        {
            [name] = new(name, [], [], "trigger", 'v', 'u', false, sql[start..end])
        };
    }

    private static string RequireSource(string name, string checksum)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + name + ".sql") ??
            throw Refusal("The trusted payload permission migration is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != checksum)
            throw Refusal("The payload permission contract requires its exact trusted source migrations.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
