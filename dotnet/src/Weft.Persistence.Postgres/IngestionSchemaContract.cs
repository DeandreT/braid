using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Weft.Persistence.Postgres;

// Expected definitions originate only from the build's trusted embedded migration.
// The live catalog can be compared against them, never used to manufacture them.
internal static class IngestionSchemaContract
{
    internal sealed record Column(string Name, string Type, bool NotNull, string? Default);
    internal sealed record Constraint(string Name, char Kind, string[] Columns, string? ReferencedTable,
        string[] ReferencedColumns, bool Deferred);
    internal sealed record Table(string Name, Column[] Columns, Constraint[] Constraints, string FunctionBody);

    internal static readonly IReadOnlyDictionary<string, Table> Tables = Load();
    internal static readonly string[] LegacyReadTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships"];
    internal static readonly IReadOnlyDictionary<string, string[]> MutableColumns = new Dictionary<string, string[]>
    {
        ["mappings"] = ["head_revision_id", "head_sequence", "updated_at"],
        ["revision_ingestions"] = ["phase", "lease_owner_id", "lease_token", "lease_generation", "lease_expires_at", "claim_count", "verified_stage_generation", "verified_stage_at", "eligible_attempt_generation", "eligible_admission", "last_error", "completed_at", "committed_revision_id"],
        ["revision_ingestion_attempts"] = ["verified_stage_at", "finished_at", "completion_kind", "backend_status", "admission_classification", "provenance_confirmed", "provenance_organization_id", "provenance_project_id", "provenance_mapping_id", "provenance_revision_id", "provenance_native_digest", "provenance_backend_id", "provenance_engine_revision", "provenance_backend_build", "provenance_adapter_build", "is_valid", "total_issue_count", "emitted_issue_count", "issues_truncated", "evidence_version", "evidence", "reused_attempt_generation", "reused_admission", "last_error"],
        ["mapping_revisions"] = []
    };

    private static IReadOnlyDictionary<string, Table> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Weft.Persistence.Postgres.Migrations.0002_catalog_ingestion.sql") ??
            throw new InvalidOperationException("The accepted ingestion migration is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var sql = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(sql, @"CREATE TABLE weft\.(?<name>[a-z_]+) \(\n(?<body>.*?)\n\);", RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var name = match.Groups["name"].Value;
            var body = match.Groups["body"].Value;
            var columns = new List<Column>();
            foreach (Match column in Regex.Matches(body, @"^    (?<name>[a-z_]+) (?<type>timestamp with time zone|uuid|text|integer|bigint|boolean|jsonb)(?<tail>[^\n]*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                var type = column.Groups["type"].Value;
                var tail = column.Groups["tail"].Value.TrimEnd(',');
                var defaultMatch = Regex.Match(tail, @" DEFAULT (?<value>.*)$", RegexOptions.CultureInvariant);
                string? defaultValue = defaultMatch.Success ? defaultMatch.Groups["value"].Value : null;
                if (type == "text" && defaultValue is not null)
                    defaultValue += "::text";
                columns.Add(new Column(column.Groups["name"].Value, type, tail.Contains("NOT NULL", StringComparison.Ordinal), defaultValue));
            }
            var constraints = new List<Constraint>();
            foreach (Match constraint in Regex.Matches(body, @"CONSTRAINT (?<name>[a-z_]+) (?<kind>PRIMARY KEY|UNIQUE|FOREIGN KEY|CHECK)(?<definition>.*?)(?=,\n    CONSTRAINT|\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant))
                constraints.Add(ParseConstraint(constraint));
            foreach (Match constraint in Regex.Matches(sql, @"ALTER TABLE weft\." + Regex.Escape(name) + @" ADD CONSTRAINT (?<name>[a-z_]+) (?<kind>FOREIGN KEY)(?<definition>.*?);", RegexOptions.Singleline | RegexOptions.CultureInvariant))
                constraints.Add(ParseConstraint(constraint));
            var function = Regex.Match(sql, @"CREATE FUNCTION weft\.guard_" + Regex.Escape(name) + @"_transition\(\).*? AS \$guard\$(?<body>.*?)\$guard\$;", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (columns.Count == 0 || constraints.Count == 0 || !function.Success)
                throw new InvalidOperationException("The accepted ingestion schema contract is incomplete.");
            tables.Add(name, new Table(name, columns.ToArray(), constraints.ToArray(), function.Groups["body"].Value));
        }
        if (tables.Count != 4)
            throw new InvalidOperationException("The accepted ingestion schema requires exactly four workflow tables.");
        return tables;
    }

    private static Constraint ParseConstraint(Match match)
    {
        var kind = match.Groups["kind"].Value switch { "PRIMARY KEY" => 'p', "UNIQUE" => 'u', "FOREIGN KEY" => 'f', _ => 'c' };
        var definition = match.Groups["definition"].Value;
        string[] columns = kind == 'c' ? [] : SplitColumns(Regex.Match(definition, @"^\s*\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant).Groups["columns"].Value);
        var target = Regex.Match(definition, @"REFERENCES weft\.(?<table>[a-z_]+)\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant);
        return new Constraint(match.Groups["name"].Value, kind, columns,
            target.Success ? target.Groups["table"].Value : null,
            target.Success ? SplitColumns(target.Groups["columns"].Value) : [],
            definition.Contains("DEFERRABLE INITIALLY DEFERRED", StringComparison.Ordinal));
    }

    private static string[] SplitColumns(string columns) => columns.Split(',').Select(value => value.Trim()).ToArray();
}
