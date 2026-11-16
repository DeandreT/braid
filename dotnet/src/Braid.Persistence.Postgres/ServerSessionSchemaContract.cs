using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Braid.Persistence.Postgres;

// Trusted build sources only. Root binds the two migration constants after the qualified parents.
// PostgreSQL canonical forms below require pristine qualification; live definitions are never adopted.
internal static partial class ServerSessionSchemaContract
{
    internal const string MigrationName = "0010_server_sessions";
    internal const string MigrationChecksum = "01d42240d33c86e6f5516a19369bfcf1576bfeb407517803bfb92d7441ba3966";
    internal const string SessionTable = "server_sessions";
    internal const string SearchPath = "search_path=pg_catalog, braid, pg_temp";
    internal static readonly string[] SessionColumns =
        ["handle_digest", "organization_id", "binding_id", "binding_version", "login_epoch", "issued_at",
         "idle_expires_at", "absolute_expires_at", "version", "revoked_at", "policy_version", "codec_version", "protected_ticket"];

    private sealed record Column(string Name, string Type, bool NotNull = true, string? Default = null);
    private sealed record Constraint(string Name, char Kind, string[] Columns, string? ReferencedTable = null,
        string[]? ReferencedColumns = null, string? Definition = null);
    private sealed record Table(string Name, Column[] Columns, Constraint[] Constraints);

    private static Constraint Check(string name, string definition) => new(name, 'c', [], Definition: definition);
    private static Constraint Foreign(string name, string[] columns, string target, string[] referenced) =>
        new(name, 'f', columns, target, referenced);

    private static readonly Table Organizations = new("organizations",
        [new("id", "uuid"), new("name", "text"), new("created_at", "timestamp with time zone", Default: "CURRENT_TIMESTAMP")],
        [new("organizations_pkey", 'p', ["id"]),
         Check("organizations_id_check", "CHECK ((id <> '00000000-0000-0000-0000-000000000000'::uuid))"),
         Check("organizations_name_check", "CHECK ((((char_length(name) >= 1) AND (char_length(name) <= 200)) AND (char_length(btrim(name)) > 0)))")]);

    private static readonly Table Sessions = new(SessionTable,
        [new("handle_digest", "bytea"), new("organization_id", "uuid"), new("binding_id", "uuid"),
         new("binding_version", "bigint"), new("login_epoch", "uuid"), new("issued_at", "timestamp with time zone"),
         new("idle_expires_at", "timestamp with time zone"), new("absolute_expires_at", "timestamp with time zone"),
         new("version", "bigint"), new("revoked_at", "timestamp with time zone", NotNull: false),
         new("policy_version", "integer"), new("codec_version", "integer"), new("protected_ticket", "bytea")],
        [new("server_sessions_pkey", 'p', ["handle_digest"]),
         Foreign("server_sessions_organization_fkey", ["organization_id"], "organizations", ["id"]),
         Foreign("server_sessions_binding_fkey", ["binding_id"], "external_identity_bindings", ["binding_id"]),
         Check("server_sessions_digest_check", "CHECK ((octet_length(handle_digest) = 32))"),
         Check("server_sessions_ids_check", "CHECK (((organization_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (binding_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (login_epoch <> '00000000-0000-0000-0000-000000000000'::uuid)))"),
         Check("server_sessions_versions_check", "CHECK (((binding_version > 0) AND (version > 0) AND (policy_version = 1) AND (codec_version = 1)))"),
         Check("server_sessions_instants_check", "CHECK ((isfinite(issued_at) AND (issued_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (issued_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone) AND isfinite(idle_expires_at) AND (idle_expires_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (idle_expires_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone) AND isfinite(absolute_expires_at) AND (absolute_expires_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (absolute_expires_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone)))"),
         Check("server_sessions_lifetime_check", "CHECK (((issued_at < idle_expires_at) AND (idle_expires_at <= absolute_expires_at) AND ((absolute_expires_at - issued_at) <= '01:00:00'::interval)))"),
         Check("server_sessions_revocation_check", "CHECK (((revoked_at IS NULL) OR (isfinite(revoked_at) AND (revoked_at > '0001-01-01 00:00:00+00'::timestamp with time zone) AND (revoked_at <= '9999-12-31 23:59:59.999999+00'::timestamp with time zone) AND (revoked_at >= issued_at) AND (version >= 2))))"),
         Check("server_sessions_ticket_check", "CHECK (((octet_length(protected_ticket) >= 1) AND (octet_length(protected_ticket) <= 16384)))")]);

    private static readonly Lazy<string> TransitionBody = new(LoadTransitionBody);

    private static string LoadTransitionBody()
    {
        RequireSource("0001_project_ownership", "f0875300876276f876ff403ec0365fe8c245cf0bf4af10e1f2841d9c4ad14aed");
        RequireSource("0008_external_identity_bindings", "78f65b6f8b66a778a4513260c7199ca818f9c3fabcd1b67796851ebde20640f9");
        var sql = RequireSource(MigrationName, MigrationChecksum);
        const string opening = "AS $braid_guard_server_sessions_transition$";
        const string closing = "$braid_guard_server_sessions_transition$;";
        var start = sql.IndexOf(opening, StringComparison.Ordinal);
        if (start < 0 || sql.IndexOf(opening, start + opening.Length, StringComparison.Ordinal) >= 0)
            throw Refusal("The trusted session transition source is incomplete.");
        start += opening.Length;
        var end = sql.IndexOf(closing, start, StringComparison.Ordinal);
        if (end <= start || sql.IndexOf(closing, end + closing.Length, StringComparison.Ordinal) >= 0)
            throw Refusal("The trusted session transition source is incomplete.");
        return sql[start..end];
    }

    private static string RequireSource(string name, string checksum)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + name + ".sql") ??
            throw Refusal("The trusted session migration is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != checksum)
            throw Refusal("The session contract requires its exact trusted source migrations.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static InvalidOperationException Refusal(string message) => new(message);
}
