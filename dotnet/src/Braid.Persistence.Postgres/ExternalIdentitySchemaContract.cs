using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Braid.Persistence.Postgres;

// Trusted build sources, never descriptors adopted from the database under admission.
// Source-only authorship: all PostgreSQL canonical forms still require pristine qualification.
internal static partial class ExternalIdentitySchemaContract
{
    internal const string MigrationName = "0008_external_identity_bindings";
    internal const string MigrationChecksum = "78f65b6f8b66a778a4513260c7199ca818f9c3fabcd1b67796851ebde20640f9";
    internal const string SearchPath = "search_path=pg_catalog, braid, pg_temp";
    internal const string BindingTable = "external_identity_bindings";
    internal const string KeyConstraint = "external_identity_bindings_key_key";
    internal static readonly string[] ReaderTables = [BindingTable, "principals"];
    internal static readonly string[] OwnerTables = [BindingTable, "principals", "organizations", "organization_memberships"];
    internal static readonly string[] BindingReadColumns =
        ["binding_id", "provider", "authority", "subject", "principal_id", "enabled", "version", "created_at", "disabled_at"];
    internal static readonly string[] PrincipalReadColumns = ["id", "active"];

    private sealed record Column(string Name, string Type, bool NotNull = true, string? Default = null, bool CCollation = false);
    private sealed record Constraint(string Name, char Kind, string[] Columns, string? ReferencedTable = null,
        string[]? ReferencedColumns = null, string? Definition = null);
    private sealed record Table(string Name, Column[] Columns, Constraint[] Constraints, bool Scoped);
    private sealed record Function(string Name, string[] ArgumentNames, string[] ArgumentTypes, string ReturnType,
        char Volatility, char Parallel, bool Strict, string Body = "");

    private static readonly IReadOnlyDictionary<string, Table> Tables = new Dictionary<string, Table>(StringComparer.Ordinal)
    {
        [BindingTable] = new(BindingTable,
            [new("binding_id", "uuid"), new("provider", "text", CCollation: true),
             new("authority", "text", CCollation: true), new("subject", "text", CCollation: true),
             new("principal_id", "uuid"), new("enabled", "boolean", Default: "true"),
             new("version", "bigint", Default: "1"), new("created_at", "timestamp with time zone", Default: "clock_timestamp()"),
             new("disabled_at", "timestamp with time zone", NotNull: false)],
            [new("external_identity_bindings_pkey", 'p', ["binding_id"]),
             new(KeyConstraint, 'u', ["provider", "authority", "subject"]),
             new("external_identity_bindings_principal_fkey", 'f', ["principal_id"], "principals", ["id"]),
             new("external_identity_bindings_id_check", 'c', [], Definition:
                 "CHECK ((binding_id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
             new("external_identity_bindings_key_check", 'c', [], Definition:
                 "CHECK (braid.external_identity_key_valid_v1(provider, authority, subject))"),
             new("external_identity_bindings_state_check", 'c', [], Definition:
                 "CHECK (braid.external_identity_binding_state_valid_v1(enabled, version, created_at, disabled_at))")], false),
        ["principals"] = new("principals",
            [new("id", "uuid"), new("active", "boolean", Default: "true"),
             new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
            [new("principals_pkey", 'p', ["id"]), new("principals_id_check", 'c', [], Definition:
                "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))")], false),
        ["organizations"] = new("organizations",
            [new("id", "uuid"), new("name", "text"), new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
            [new("organizations_pkey", 'p', ["id"]), new("organizations_id_check", 'c', [], Definition:
                 "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
             new("organizations_name_check", 'c', [], Definition:
                 "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))")], true),
        ["organization_memberships"] = new("organization_memberships",
            [new("organization_id", "uuid"), new("principal_id", "uuid"), new("active", "boolean", Default: "true")],
            [new("organization_memberships_pkey", 'p', ["organization_id", "principal_id"]),
             new("organization_memberships_organization_id_fkey", 'f', ["organization_id"], "organizations", ["id"]),
             new("organization_memberships_principal_id_fkey", 'f', ["principal_id"], "principals", ["id"])], true)
    };

    private static readonly Lazy<IReadOnlyDictionary<string, Function>> Functions = new(LoadFunctions);

    private static IReadOnlyDictionary<string, Function> LoadFunctions()
    {
        RequireSource("0001_project_ownership", "f0875300876276f876ff403ec0365fe8c245cf0bf4af10e1f2841d9c4ad14aed");
        var sql = RequireSource(MigrationName, MigrationChecksum);
        Function[] signatures =
        [
            new("external_identity_key_valid_v1", ["p_provider", "p_authority", "p_subject"],
                ["text", "text", "text"], "boolean", 'i', 's', true),
            new("external_identity_binding_state_valid_v1", ["p_enabled", "p_version", "p_created_at", "p_disabled_at"],
                ["boolean", "bigint", "timestamp with time zone", "timestamp with time zone"], "boolean", 'i', 's', false),
            new("guard_external_identity_bindings_transition", [], [], "trigger", 'v', 'u', false)
        ];
        var result = new Dictionary<string, Function>(StringComparer.Ordinal);
        foreach (var signature in signatures)
        {
            var opening = "AS $braid_" + signature.Name + "$";
            var closing = "$braid_" + signature.Name + "$;";
            var start = sql.IndexOf(opening, StringComparison.Ordinal);
            if (start < 0)
                throw Refusal("The trusted external identity function source is incomplete.");
            start += opening.Length;
            var end = sql.IndexOf(closing, start, StringComparison.Ordinal);
            if (end <= start)
                throw Refusal("The trusted external identity function source is incomplete.");
            result.Add(signature.Name, signature with { Body = sql[start..end] });
        }
        return result;
    }

    private static string RequireSource(string name, string checksum)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + name + ".sql") ??
            throw Refusal("The trusted external identity migration is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != checksum)
            throw Refusal("The external identity contract requires its exact trusted source migrations.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
