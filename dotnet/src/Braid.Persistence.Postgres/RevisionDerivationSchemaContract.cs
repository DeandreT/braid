using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Braid.Persistence.Postgres;

internal static class RevisionDerivationSchemaContract
{
    internal const string MigrationName = "0006_revision_derivations";
    // Frozen source and pristine PostgreSQL 16.15 body capture; never adopted from a target database.
    internal const string MigrationChecksum = "b3e1dd0f648694e0eab2c8e188cac176dafa9798ac239c8f555c42ffbc55d21e";
    internal static readonly IReadOnlyDictionary<string, string> BodyChecksums =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["guard_revision_derivation_attempts_transition"] = "fe0e86a92756ee771f8fe8042e9e573f51fd45b79f068639a248f598bb0f7476",
            ["guard_revision_derivations_transition"] = "94945540936962a059e091a855ddc333cbb14c74cda0ad9b505668536a4c8cf0",
        };

    internal static readonly string[] SourceTables =
        ["organizations", "principals", "organization_memberships", "projects", "project_memberships", "mappings", "mapping_revisions"];
    internal static readonly string[] NewTables = ["revision_derivations", "revision_derivation_attempts"];
    internal static readonly string[] ReadTables = [.. SourceTables, .. NewTables];
    internal static readonly string[] SourceIndexColumns =
        ["organization_id", "project_id", "mapping_id", "id", "native_object_id", "native_class", "native_digest", "native_byte_count", "logical_entry_document", "dependency_manifest_id", "backend_id"];

    internal static readonly string[] KeyInsertColumns =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "operation_kind",
        "native_object_id", "native_class", "native_digest", "native_byte_count", "logical_entry_document",
        "dependency_manifest_id", "backend_id", "engine_revision", "backend_build", "adapter_build",
        "schema_version", "algorithm_version", "codec_version", "policy_schema_version", "policy_json",
        "policy_fingerprint", "native_byte_limit", "worker_response_byte_limit", "item_limit",
        "response_item_limit", "json_depth_limit", "detail_depth_limit", "diagnostic_limit", "validation_issue_limit",
        "text_byte_limit", "header_byte_limit", "stored_object_byte_limit", "response_byte_limit", "key_fingerprint"
    ];
    internal static readonly string[] AttemptInsertColumns =
        ["organization_id", "project_id", "mapping_id", "revision_id", "cache_entry_id", "generation", "actor_principal_id", "claimant_instance_id", "repair_target_derivation_id"];
    internal static readonly string[] KeyUpdateColumns = ["current_generation", "current_attempt_id", "selected_derivation_id"];
    internal static readonly string[] AttemptUpdateColumns =
    [
        "completion_kind", "failure_code", "observation_present", "backend_status", "provenance_confirmed",
        "provenance_organization_id", "provenance_project_id", "provenance_mapping_id", "provenance_revision_id",
        "provenance_native_digest", "provenance_backend_id", "provenance_engine_revision", "provenance_backend_build",
        "provenance_adapter_build", "projection_coverage", "document_item_count", "document_diagnostic_count",
        "coverage_gap_count", "operation_diagnostic_count", "is_valid", "total_issue_count", "emitted_issue_count",
        "issues_truncated", "validation_diagnostic_count", "derived_digest", "derived_byte_count"
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, CatalogSchemaContract.Table>> AcceptedTables = new(Load);
    internal static IReadOnlyDictionary<string, CatalogSchemaContract.Table> Tables => AcceptedTables.Value;

    internal static bool TryGetCheck(string name, out string? definition) =>
        RevisionDerivationCheckDefinitions.Values.TryGetValue(name, out definition) ||
        CatalogSchemaContract.TryGetCheck(name, out definition);

    private static IReadOnlyDictionary<string, CatalogSchemaContract.Table> Load()
    {
        RevisionDerivationCheckDefinitions.RequireCaptured();
        if (MigrationChecksum.Length != 64 || BodyChecksums.Count != NewTables.Length)
            throw new InvalidOperationException("The trusted revision derivation source capture is incomplete.");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + MigrationName + ".sql") ??
            throw new InvalidOperationException("The accepted revision derivation migration is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var contents = bytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(contents)) != MigrationChecksum)
            throw new InvalidOperationException("The revision derivation contract requires the accepted source migration.");
        var sql = new UTF8Encoding(false, true).GetString(contents);

        // Explicitly reuse only the seven needed source descriptors. The catalog
        // map also contains private reservations that these pools never admit.
        var tables = SourceTables.ToDictionary(name => name, name => CatalogSchemaContract.Tables[name], StringComparer.Ordinal);
        var declarations = Regex.Matches(sql,
            @"^CREATE TABLE braid\.(?<name>revision_derivations|revision_derivation_attempts) \(\n(?<body>.*?)\n\);",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (declarations.Count != NewTables.Length)
            throw new InvalidOperationException("The accepted revision derivation table contract is incomplete.");
        foreach (Match declaration in declarations)
        {
            var name = declaration.Groups["name"].Value;
            var body = declaration.Groups["body"].Value;
            var columns = new List<IngestionSchemaContract.Column>();
            foreach (Match column in Regex.Matches(body,
                         @"^    (?<name>[a-z_]+) (?<type>timestamp with time zone|uuid|text|integer|bigint|boolean)(?<tail>[^\n]*)$",
                         RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                var tail = column.Groups["tail"].Value.TrimEnd(',');
                var type = column.Groups["type"].Value;
                var defaultMatch = Regex.Match(tail, @" DEFAULT (?<value>.*)$", RegexOptions.CultureInvariant);
                string? defaultValue = defaultMatch.Success ? defaultMatch.Groups["value"].Value : null;
                if (type == "text" && defaultValue is not null && !defaultValue.EndsWith("::text", StringComparison.Ordinal))
                    defaultValue += "::text";
                columns.Add(new(column.Groups["name"].Value, type, tail.Contains("NOT NULL", StringComparison.Ordinal), defaultValue));
            }
            var constraints = new List<IngestionSchemaContract.Constraint>();
            foreach (Match constraint in Regex.Matches(body,
                         @"CONSTRAINT (?<name>[a-z_]+) (?<kind>PRIMARY KEY|UNIQUE|FOREIGN KEY|CHECK)(?<definition>.*?)(?=,\n    CONSTRAINT|\z)",
                         RegexOptions.Singleline | RegexOptions.CultureInvariant))
                constraints.Add(ParseConstraint(constraint));
            foreach (Match constraint in Regex.Matches(sql,
                         @"ALTER TABLE braid\." + Regex.Escape(name) + @" ADD CONSTRAINT (?<name>[a-z_]+)\s+(?<kind>FOREIGN KEY)(?<definition>.*?);",
                         RegexOptions.Singleline | RegexOptions.CultureInvariant))
                constraints.Add(ParseConstraint(constraint));
            var functionName = "guard_" + name + "_transition";
            var functions = Regex.Matches(sql,
                @"^CREATE FUNCTION braid\." + Regex.Escape(functionName) +
                @"\(\)\s+RETURNS\s+trigger\s+LANGUAGE\s+plpgsql\s+SECURITY\s+INVOKER\s+SET\s+search_path(?:\s+TO|\s*=)\s*pg_catalog,\s*braid,\s*pg_temp\s+AS\s+\$braid_" +
                Regex.Escape(functionName) + @"\$(?<body>.*?)\$braid_" + Regex.Escape(functionName) + @"\$;",
                RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (columns.Count == 0 || constraints.Count == 0 || functions.Count != 1 ||
                functions[0].Groups["body"].Value.Length == 0 || !BodyChecksums.TryGetValue(functionName, out var expectedBodyChecksum) ||
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(functions[0].Groups["body"].Value))) != expectedBodyChecksum)
                throw new InvalidOperationException("The accepted revision derivation transition contract is incomplete.");
            var checkNames = constraints.Where(value => value.Kind == 'c').Select(value => value.Name).Order(StringComparer.Ordinal);
            if (!checkNames.SequenceEqual(RevisionDerivationCheckDefinitions.Names.Where(value =>
                    value.StartsWith(name + "_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("The accepted revision derivation checks do not match this build.");
            tables.Add(name, new(name, columns.ToArray(), constraints.ToArray(), functions[0].Groups["body"].Value));
        }
        return tables;
    }

    private static IngestionSchemaContract.Constraint ParseConstraint(Match match)
    {
        var kind = match.Groups["kind"].Value switch { "PRIMARY KEY" => 'p', "UNIQUE" => 'u', "FOREIGN KEY" => 'f', _ => 'c' };
        var definition = match.Groups["definition"].Value;
        string[] columns = kind == 'c' ? [] : SplitColumns(Regex.Match(definition, @"^\s*\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant).Groups["columns"].Value);
        var target = Regex.Match(definition, @"REFERENCES braid\.(?<table>[a-z_]+)\s*\((?<columns>[^)]+)\)", RegexOptions.CultureInvariant);
        if (kind != 'c' && columns.Length == 0 || kind == 'f' && !target.Success)
            throw new InvalidOperationException("The accepted revision derivation key contract is incomplete.");
        return new(match.Groups["name"].Value, kind, columns,
            target.Success ? target.Groups["table"].Value : null,
            target.Success ? SplitColumns(target.Groups["columns"].Value) : [],
            definition.Contains("DEFERRABLE INITIALLY DEFERRED", StringComparison.Ordinal));
    }

    private static string[] SplitColumns(string columns) => columns.Split(',').Select(value => value.Trim()).ToArray();
}
