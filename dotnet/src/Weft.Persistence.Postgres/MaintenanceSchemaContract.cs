using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Weft.Persistence.Postgres;

internal static class MaintenanceSchemaContract
{
    internal const string MigrationName = "0007_private_object_maintenance";
    internal const string MigrationChecksum = "a7de73d263b5a8ae7c0ca4a14d0608d4e50222ffdd68c2dab6bec9ac77e0d259";
    internal sealed record Function(string Name, string IdentityArguments, string ResultType, string Body,
        bool SecurityDefiner = true, char Volatility = 'v', char Parallel = 'u', string SearchPath = "pg_catalog, weft, pg_temp");
    internal sealed record Trigger(string Name, string Table, string Function, short Events);
    internal sealed record Index(string Name, string Table, string[] Columns, string? Predicate, bool Unique,
        string? Expressions = null, string[]? KeyTypes = null);
    internal sealed record Policy(string Name, string Table, char Command, uint[] Roles, string Expression, string CheckExpression);

    internal static readonly string[] NewTables =
        ["private_object_publications", "private_object_retirements", "private_object_cleanup_attempts"];
    internal static readonly string[] ReadTables = [.. NewTables];
    internal static readonly string[] EntryFunctionNames =
        ["claim_private_object_cleanup_v1", "finish_private_object_cleanup_v1", "get_private_object_cleanup_attempt_v1"];
    internal static readonly Policy[] Policies =
    [
        new("private_object_publications_organization_scope", "private_object_publications", '*', [0], "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND ((CURRENT_USER <> 'weft_maintenance'::name) OR (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid)))", "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND ((CURRENT_USER <> 'weft_maintenance'::name) OR (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid)))"),
        new("private_object_retirements_organization_scope", "private_object_retirements", '*', [0], "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND ((CURRENT_USER <> 'weft_maintenance'::name) OR (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid)))", "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND ((CURRENT_USER <> 'weft_maintenance'::name) OR (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid)))"),
        new("private_object_cleanup_attempts_organization_scope", "private_object_cleanup_attempts", '*', [0], "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid))", "((organization_id = (NULLIF(current_setting('weft.organization_id'::text, true), ''::text))::uuid) AND (project_id = (NULLIF(current_setting('weft.project_id'::text, true), ''::text))::uuid))")
    ];
    internal static readonly Trigger[] Triggers =
    [
        new("private_object_publications_transition_guard", "private_object_publications", "guard_private_object_publications_v1", 31),
        new("private_object_retirements_transition_guard", "private_object_retirements", "guard_private_object_retirements_v1", 31),
        new("private_object_cleanup_attempts_transition_guard", "private_object_cleanup_attempts", "guard_private_object_cleanup_attempts_v1", 31),
        new("revision_ingestions_z_retirement_guard", "revision_ingestions", "guard_native_retirement_attachment_v1", 23),
        new("revision_ingestion_attempts_z_retirement_guard", "revision_ingestion_attempts", "guard_native_retirement_attachment_v1", 23),
        new("mapping_revisions_z_retirement_guard", "mapping_revisions", "guard_native_retirement_attachment_v1", 7),
        new("revision_derivation_attempts_z_retirement_guard", "revision_derivation_attempts", "guard_derived_retirement_attachment_v1", 23),
        new("revision_derivations_z_retirement_guard", "revision_derivations", "guard_derived_retirement_attachment_v1", 19)
    ];
    // Explicit indexes only. Constraint-backed indexes are described by Tables.
    // Exact predicates and expression keys from pristine PostgreSQL 16.15 source calibration.
    // Capture SHA256 aaf6daf1b7ce638e33372a5965baf77c02c8b936d199d18daaff756e12e58b64.
    internal static readonly Index[] Indexes =
    [
        new("private_object_publications_temporary_scan", "private_object_publications", ["organization_id", "project_id", "", "mapping_id", "revision_id", "object_class", "object_id", "publication_id"], null, false, "GREATEST(registered_at, bound_at, owner_lease_expires_at)", ["uuid", "uuid", "timestamp with time zone", "uuid", "uuid", "text", "uuid", "uuid"]),
        new("private_object_retirements_final_key", "private_object_retirements", ["organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id"], "(target_kind = 'final_object'::text)", true),
        new("private_object_retirements_temporary_key", "private_object_retirements", ["organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id", "publication_id"], "(target_kind = 'tracked_temporary'::text)", true),
        new("private_object_retirements_due_scan", "private_object_retirements", ["organization_id", "project_id", "retired_at", "mapping_id", "revision_id", "object_class", "object_id", "publication_id"], "((completed_attempt_id IS NULL) AND (current_generation < 8))", false),
        new("revision_ingestions_cleanup_scan", "revision_ingestions", ["organization_id", "project_id", "completed_at", "future_mapping_id", "future_revision_id", "native_object_id"], "(phase = ANY (ARRAY['rejected'::text, 'head_conflict'::text, 'attempt_exhausted'::text]))", false),
        new("revision_derivation_attempts_cleanup_scan", "revision_derivation_attempts", ["organization_id", "project_id", "", "mapping_id", "revision_id", "candidate_object_id"], "(NOT result_usable)", false, "COALESCE(finished_at, lease_expires_at)", ["uuid", "uuid", "timestamp with time zone", "uuid", "uuid", "uuid"])
    ];
    private static readonly Lazy<(IReadOnlyDictionary<string, CatalogSchemaContract.Table> Tables,
        IReadOnlyDictionary<string, Function> Functions)> Accepted = new(Load);
    internal static IReadOnlyDictionary<string, CatalogSchemaContract.Table> Tables => Accepted.Value.Tables;
    internal static IReadOnlyDictionary<string, Function> Functions => Accepted.Value.Functions;
    internal static bool TryGetCheck(string name, out string? definition) => MaintenanceCheckDefinitions.Values.TryGetValue(name, out definition);

    private static (IReadOnlyDictionary<string, CatalogSchemaContract.Table>, IReadOnlyDictionary<string, Function>) Load()
    {
        MaintenanceCheckDefinitions.RequireCaptured();
        if (MigrationChecksum.Length != 64 || Indexes.Any(index => index.Predicate is { Length: 0 } || index.Expressions is { Length: 0 })
            || Policies.Any(policy => policy.Expression.Length == 0 || policy.CheckExpression.Length == 0))
            throw new InvalidOperationException("The trusted maintenance source calibration is incomplete.");
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations." + MigrationName + ".sql") ??
            throw new InvalidOperationException("The trusted maintenance migration is missing.");
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != MigrationChecksum)
            throw new InvalidOperationException("The maintenance migration does not match this build.");
        var sql = new UTF8Encoding(false, true).GetString(bytes);
        var functions = new Dictionary<string, Function>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(sql,
                     @"^CREATE FUNCTION weft\.(?<name>[a-z_0-9]+)\((?<args>.*?)\) RETURNS (?<result>jsonb|boolean|void|trigger)\s+LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER\s+SET search_path = pg_catalog, weft, pg_temp AS \$weft_\k<name>\$(?<body>.*?)\$weft_\k<name>\$;",
                     RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var name = match.Groups["name"].Value;
            var arguments = Regex.Replace(match.Groups["args"].Value.Trim(), @"\s+", " ");
            arguments = Regex.Replace(arguments, @",\s*", ", ");
            functions.Add(name, new(name, arguments, match.Groups["result"].Value, match.Groups["body"].Value));
        }
        if (functions.Count == 0 || EntryFunctionNames.Any(name => !functions.ContainsKey(name)))
            throw new InvalidOperationException("The trusted maintenance routine declarations are incomplete.");
        var tables = new Dictionary<string, CatalogSchemaContract.Table>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(sql,
                     @"^CREATE TABLE weft\.(?<name>private_object_publications|private_object_retirements|private_object_cleanup_attempts) \(\n(?<body>.*?)\n\);",
                     RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var name = match.Groups["name"].Value;
            var body = match.Groups["body"].Value;
            var columns = new List<IngestionSchemaContract.Column>();
            foreach (Match column in Regex.Matches(body,
                         @"^    (?<name>[a-z_0-9]+) (?<type>timestamp with time zone|numeric\(20,0\)|uuid|text|integer|bigint|boolean)(?<tail>[^\n]*)$",
                         RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                var tail = column.Groups["tail"].Value.TrimEnd(',');
                var type = column.Groups["type"].Value;
                var value = Regex.Match(tail, @" DEFAULT (?<value>.*)$", RegexOptions.CultureInvariant);
                var defaultSql = value.Success ? value.Groups["value"].Value : null;
                if (type == "text" && defaultSql is not null && !defaultSql.EndsWith("::text", StringComparison.Ordinal)) defaultSql += "::text";
                columns.Add(new(column.Groups["name"].Value, type == "numeric(20,0)" ? "numeric(20,0)" : type,
                    tail.Contains("NOT NULL", StringComparison.Ordinal), defaultSql));
            }
            var constraints = new List<IngestionSchemaContract.Constraint>();
            foreach (Match constraint in Regex.Matches(body,
                         @"CONSTRAINT (?<name>[a-z_0-9]+) (?<kind>PRIMARY KEY|UNIQUE|FOREIGN KEY|CHECK)(?<definition>.*?)(?=,\n    CONSTRAINT|\z)",
                         RegexOptions.Singleline | RegexOptions.CultureInvariant)) constraints.Add(ParseConstraint(constraint));
            foreach (Match constraint in Regex.Matches(sql,
                         @"ALTER TABLE weft\." + Regex.Escape(name) + @" ADD CONSTRAINT (?<name>[a-z_0-9]+)\s+(?<kind>FOREIGN KEY)(?<definition>.*?);",
                         RegexOptions.Singleline | RegexOptions.CultureInvariant)) constraints.Add(ParseConstraint(constraint));
            var trigger = Triggers.Single(value => value.Table == name);
            tables.Add(name, new(name, columns.ToArray(), constraints.ToArray(), functions[trigger.Function].Body));
        }
        if (tables.Count != NewTables.Length || !tables.Values.SelectMany(table => table.Constraints)
                .Where(constraint => constraint.Kind == 'c').Select(constraint => constraint.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(MaintenanceCheckDefinitions.Names.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("The trusted maintenance relation declarations are incomplete.");
        return (tables, functions);
    }

    private static IngestionSchemaContract.Constraint ParseConstraint(Match match)
    {
        var definition = match.Groups["definition"].Value;
        var kind = match.Groups["kind"].Value switch { "PRIMARY KEY" => 'p', "UNIQUE" => 'u', "FOREIGN KEY" => 'f', _ => 'c' };
        var columns = Regex.Match(definition, @"^\s*\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant);
        var target = Regex.Match(definition, @"REFERENCES weft\.(?<table>[a-z_]+)\s*\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant);
        static string[] Split(string value) => value.Split(',').Select(part => part.Trim()).ToArray();
        if (kind != 'c' && !columns.Success || kind == 'f' && !target.Success)
            throw new InvalidOperationException("The trusted maintenance key declaration is incomplete.");
        return new(match.Groups["name"].Value, kind, kind == 'c' ? [] : Split(columns.Groups["columns"].Value),
            target.Success ? target.Groups["table"].Value : null,
            target.Success ? Split(target.Groups["columns"].Value) : [],
            definition.Contains("DEFERRABLE INITIALLY DEFERRED", StringComparison.Ordinal));
    }
}
