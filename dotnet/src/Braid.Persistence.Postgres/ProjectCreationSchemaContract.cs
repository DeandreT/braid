using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Braid.Persistence.Postgres;

// The embedded migration is the trusted source. A live routine body or migration
// journal entry is never used to construct this build's expected definitions.
internal static class ProjectCreationSchemaContract
{
    internal const string MigrationName = "0005_project_creation";
    internal const string SearchPath = "search_path=pg_catalog, braid, pg_temp";

    internal sealed record Function(string Name, string[] InputNames, string[] InputTypes,
        string[] OutputNames, string[] OutputTypes, string ReturnType, bool ReturnsSet,
        bool SecurityDefiner, char Volatility, char Parallel, bool Strict, string Body)
    {
        internal string BodyChecksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body)));
    }

    private sealed record Source(string Checksum, IReadOnlyDictionary<string, Function> Functions);
    private static readonly Source AcceptedSource = Load();

    internal static string MigrationChecksum => AcceptedSource.Checksum;
    internal static IReadOnlyDictionary<string, Function> Functions => AcceptedSource.Functions;
    private static readonly Lazy<string> SwitchingAssertion = new(LoadSwitchingAssertion);
    /// <summary>Organization switching replaces only the contract assertion, to admit its exact membership triggers.</summary>
    internal static string SwitchingAssertionBody => SwitchingAssertion.Value;

    private static string LoadSwitchingAssertion()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + OrganizationSwitchingSchemaAdmission.Migration) ??
            throw new InvalidOperationException("The accepted organization switching migration is missing.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var matches = Regex.Matches(reader.ReadToEnd(),
            @"^CREATE OR REPLACE FUNCTION braid\.assert_project_creation_contract_v1\(\) RETURNS void\s.*?\bAS \$braid_assert_project_creation_contract_v1\$(?<body>.*?)\$braid_assert_project_creation_contract_v1\$;",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (matches.Count != 1 || matches[0].Groups["body"].Length == 0)
            throw new InvalidOperationException("The accepted project creation function contract is incomplete.");
        return matches[0].Groups["body"].Value;
    }

    private static Source Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Braid.Persistence.Postgres.Migrations." + MigrationName + ".sql") ??
            throw new InvalidOperationException("The accepted project creation migration is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var contents = bytes.ToArray();
        var sql = new UTF8Encoding(false, true).GetString(contents);
        var signatures = new Dictionary<string, Function>(StringComparer.Ordinal)
        {
            ["get_project_creation_admission_v1"] = new("get_project_creation_admission_v1",
                ["p_organization_id", "p_actor_id"], ["uuid", "uuid"],
                ["outcome", "organization_id", "principal_id", "can_create_projects"],
                ["text", "uuid", "uuid", "boolean"], "record", true, true, 'v', 'u', false, ""),
            ["create_project_v1"] = new("create_project_v1",
                ["p_organization_id", "p_actor_id", "p_idempotency_key", "p_name", "p_description"],
                ["uuid", "uuid", "uuid", "text", "text"],
                ["outcome", "organization_id", "idempotency_key", "original_actor_id", "project_id",
                 "fingerprint_version", "fingerprint", "name", "description", "created_at",
                 "initial_project_version", "initial_membership_role", "initial_membership_active", "initial_membership_version"],
                ["text", "uuid", "uuid", "uuid", "uuid", "integer", "text", "text", "text",
                 "timestamp with time zone", "bigint", "text", "boolean", "bigint"],
                "record", true, true, 'v', 'u', false, ""),
            ["assert_project_creation_contract_v1"] = new("assert_project_creation_contract_v1",
                [], [], [], [], "void", false, false, 'v', 'u', false, ""),
            ["project_creation_fingerprint_v1"] = new("project_creation_fingerprint_v1",
                ["p_organization_id", "p_actor_id", "p_idempotency_key", "p_name", "p_description"],
                ["uuid", "uuid", "uuid", "text", "text"], [], [], "text", false, false, 'i', 's', true, ""),
            ["guard_project_creations_transition"] = new("guard_project_creations_transition",
                [], [], [], [], "trigger", false, false, 'v', 'u', false, "")
        };

        var declarations = Regex.Matches(sql,
            @"^CREATE(?: OR REPLACE)? FUNCTION braid\.(?<name>[a-z_][a-z0-9_]*)\((?<arguments>.*?)\)\s*(?<declaration>.*?)\bAS \$braid_\k<name>\$(?<body>.*?)\$braid_\k<name>\$;",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (declarations.Count != signatures.Count || Regex.Matches(sql,
                @"^CREATE(?: OR REPLACE)? FUNCTION braid\.", RegexOptions.Multiline | RegexOptions.CultureInvariant).Count != signatures.Count)
            throw new InvalidOperationException("The accepted project creation function contract is incomplete.");

        var functions = new Dictionary<string, Function>(StringComparer.Ordinal);
        foreach (Match declaration in declarations)
        {
            var name = declaration.Groups["name"].Value;
            if (!signatures.Remove(name, out var signature) || declaration.Groups["body"].Value.Length == 0 ||
                !ParseArguments(declaration.Groups["arguments"].Value).SequenceEqual(
                    signature.InputNames.Zip(signature.InputTypes, (argument, type) => argument + " " + type)))
                throw new InvalidOperationException("The accepted project creation function signature does not match this build.");
            var result = declaration.Groups["declaration"].Value;
            if (signature.OutputNames.Length != 0)
            {
                var outputs = Regex.Match(result, @"\ARETURNS\s+TABLE\s*\((?<columns>.*?)\)\s+LANGUAGE\b",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant);
                if (!outputs.Success || !ParseArguments(outputs.Groups["columns"].Value).SequenceEqual(
                        signature.OutputNames.Zip(signature.OutputTypes, (argument, type) => argument + " " + type)))
                    throw new InvalidOperationException("The accepted project creation result signature does not match this build.");
            }
            else if (!Regex.IsMatch(result, @"\ARETURNS\s+" + Regex.Escape(signature.ReturnType) + @"\s+LANGUAGE\b",
                         RegexOptions.CultureInvariant))
                throw new InvalidOperationException("The accepted project creation return type does not match this build.");
            functions.Add(name, signature with { Body = declaration.Groups["body"].Value });
        }
        if (signatures.Count != 0)
            throw new InvalidOperationException("The accepted project creation function contract is incomplete.");
        return new Source(Convert.ToHexStringLower(SHA256.HashData(contents)), functions);
    }

    private static string[] ParseArguments(string arguments) => string.IsNullOrWhiteSpace(arguments)
        ? []
        : arguments.Split(',').Select(argument => Regex.Replace(argument.Trim(), @"\s+", " ", RegexOptions.CultureInvariant)).ToArray();
}
