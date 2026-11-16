using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;
using Braid.Server;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RegressionDeploymentProvisioningTests
{
    private static readonly string[] PasswordRoles =
    [
        "MIGRATOR", "RUNTIME", "INGEST", "CATALOG", "METADATA", "CREATOR", "REPORT", "DERIVATION",
        "MAINTENANCE", "IDENTITY", "PAYLOAD_PERMISSION", "SESSION_READER", "SESSION_WRITER", "CHAT_READER", "CHAT_WRITER",
        "CHAT_LIFECYCLE", "CHAT_APPROVER", "PREVIEW_READER", "PREVIEW_WRITER", "CASE_READER", "CASE_WRITER", "RUN_READER", "RUN_WRITER", "COMPARISON",
        "BUILD_READER", "BUILD_WRITER", "BUILD_RUN_READER", "BUILD_RUN_WRITER", "BUILD_DISPATCHER", "BUILD_METADATA",
        "QUALIFICATION_READER", "QUALIFICATION_WRITER", "RELEASE_READER", "RELEASE_WRITER", "INVOCATION_GRANT_WRITER", "INVOCATION_WRITER",
        "API_AUDIT_WRITER", "ADMINISTRATION", "DRAFT_READER", "DRAFT_WRITER", "SPEC_READER", "SPEC_WRITER", "SPEC_MAINTENANCE", "CREATION_READER", "CREATION_WRITER", "CREATION_APPROVER"
    ];

    [Theory]
    [InlineData("deploy/local/postgres-init.sh")]
    [InlineData("deploy/azure/provision-database.sh")]
    public void Native_fixture_supplies_every_declared_provisioning_password(string script)
    {
        var text = File.ReadAllText(Path.Combine(Repository(), script));
        Assert.NotEmpty(RequiredPasswordRoles(text));
        Assert.Empty(MissingPasswordRoles(text));
    }

    [Fact]
    public void An_unrecognized_provisioning_password_is_not_silently_ignored()
    {
        Assert.Equal(["NEW_READER"], MissingPasswordRoles("\\getenv new_password BRAID_LOCAL_NEW_READER_PASSWORD\n"));
        Assert.Equal(["NEW_READER"], MissingPasswordRoles("\\getenv new_password BRAID_NEW_READER_PASSWORD\n"));
    }

    [Fact]
    public void Every_local_init_consumer_is_in_the_credential_propagation_audit()
    {
        var root = Repository();
        var consumers = new[] { "deploy", "tools" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".sh" or ".mjs" or ".yml")
            .Where(path => File.ReadAllText(path).Contains("postgres-init.sh", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "deploy/compose/compose.yml", "deploy/local/dev-up.sh", "deploy/local/postgres.compose.yml",
            "tools/browser-fixture/runtime.mjs", "tools/preview-browser/run.mjs" }, consumers);
    }

    [Theory]
    [InlineData("deploy/local/postgres.compose.yml")]
    [InlineData("deploy/compose/compose.yml")]
    public void Both_compose_init_mounts_require_and_deliver_every_mandatory_password(string path)
    {
        var text = File.ReadAllText(Path.Combine(Repository(), path));
        var environment = ServiceEnvironment(text, "postgres");
        foreach (var role in MandatoryLocalPasswords())
        {
            var name = "BRAID_LOCAL_" + role + "_PASSWORD";
            Assert.True(environment.TryGetValue(name, out var delivery), $"Missing required database environment setting: {name}");
            Assert.StartsWith("${" + name + ":?", delivery, StringComparison.Ordinal);
        }
        var example = File.ReadAllText(Path.Combine(Repository(), "deploy/local/.env.example"));
        foreach (var role in MandatoryLocalPasswords())
            Assert.Matches(@"(?m)^BRAID_LOCAL_" + role + @"_PASSWORD=replace-[^\r\n]+", example);
    }

    [Theory]
    [InlineData("deploy/compose/smoke-test.sh")]
    [InlineData("deploy/compose/acceptance-test.sh")]
    public void Compose_generators_cover_all_init_requirements_not_only_chat(string path)
    {
        var text = File.ReadAllText(Path.Combine(Repository(), path));
        var loop = Regex.Match(text, @"for role in (?<roles>[^;]+); do\r?\n  export ""BRAID_LOCAL_\$\{role\}_PASSWORD=\$\(openssl rand -hex 24\)""",
            RegexOptions.CultureInvariant);
        Assert.True(loop.Success, "The independent per-role password generation loop must be explicit.");
        var roles = Regex.Matches(loop.Groups["roles"].Value, @"[A-Z][A-Z_]+", RegexOptions.CultureInvariant).Select(match => match.Value).ToArray();
        Assert.Equal(roles.Length, roles.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(MandatoryLocalPasswords().Except(roles, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("tools/browser-fixture/runtime.mjs")]
    [InlineData("tools/preview-browser/run.mjs")]
    public void Browser_init_consumers_generate_every_required_role_credential(string path)
    {
        var text = File.ReadAllText(Path.Combine(Repository(), path));
        var roles = Regex.Matches(text, "\\[\"braid_[a-z_]+\", \"(?<role>[A-Z_]+)\"\\]", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["role"].Value).ToArray();
        Assert.NotEmpty(roles);
        Assert.Empty(MandatoryLocalPasswords().Except(roles, StringComparer.Ordinal));
        Assert.Contains("[\"braid_chat_approver\", \"CHAT_APPROVER\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_dev_credential_discovery_and_server_delivery_include_approver()
    {
        var text = File.ReadAllText(Path.Combine(Repository(), "deploy/local/dev-up.sh"));
        Assert.Contains("mapfile -t roles < <(sed -n", text, StringComparison.Ordinal);
        Assert.Contains("\"$source_root/deploy/local/postgres-init.sh\")", text, StringComparison.Ordinal);
        Assert.Contains("BRAID_CHAT_APPROVER_CONNECTION_STRING=$(connection braid_chat_approver CHAT_APPROVER)", text, StringComparison.Ordinal);
        Assert.Contains("CHAT_APPROVER", MandatoryLocalPasswords());
    }

    [Fact]
    public void Session_compose_file_delivery_paths_roles_and_host_admission_agree()
    {
        var environment = ServiceEnvironment(File.ReadAllText(Path.Combine(Repository(), "deploy/compose/compose.features.yml")), "server");
        Assert.Equal("\"true\"", environment["BRAID_CHAT_ENABLED"]);
        var delivered = new Dictionary<string, string>();
        foreach (var role in new[] { "READER", "WRITER", "APPROVER" })
        {
            var name = "BRAID_CHAT_" + role + "_CONNECTION_STRING";
            Assert.False(environment.ContainsKey(name));
            var deliveryName = "BRAID_DELIVER_FILE_CHAT_" + role;
            // The entrypoint maps the generic delivery suffix to the protected file basename.
            var basename = deliveryName["BRAID_DELIVER_FILE_".Length..].ToLowerInvariant().Replace('_', '-');
            Assert.Equal("/run/braid/secrets/" + basename, environment[name + "_FILE"]);
            var value = environment[deliveryName];
            var placeholder = "${BRAID_LOCAL_CHAT_" + role + "_PASSWORD:?}";
            Assert.Contains(placeholder, value, StringComparison.Ordinal);
            var connection = value.Replace(placeholder, "synthetic-never-connected-" + role, StringComparison.Ordinal);
            Assert.DoesNotContain("${", connection, StringComparison.Ordinal);
            Assert.Equal("braid_chat_" + role.ToLowerInvariant(), new NpgsqlConnectionStringBuilder(connection).Username);
            delivered[role] = connection;
        }
        // Exercise the real admission routine against Compose's parsed connection values, without files or sockets.
        var admitted = ChatProposalHostConfiguration.Admit(delivered["READER"], delivered["WRITER"], delivered["APPROVER"]);
        Assert.Equal(delivered["APPROVER"], admitted.Approver);
        Assert.Throws<InvalidOperationException>(() => ChatProposalHostConfiguration.Admit(delivered["READER"], delivered["WRITER"], delivered["WRITER"]));
        var entrypoint = File.ReadAllText(Path.Combine(Repository(), "deploy/container/braid-entrypoint.sh"));
        Assert.Contains("file=${file,,}", entrypoint, StringComparison.Ordinal);
        Assert.Contains("file=${file//_/-}", entrypoint, StringComparison.Ordinal);
        Assert.Contains("write_private \"$file\" \"${!name}\"\n  unset \"$name\"", entrypoint.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static string[] MandatoryLocalPasswords()
    {
        var init = File.ReadAllText(Path.Combine(Repository(), "deploy/local/postgres-init.sh"));
        // Administration remains deliberately opt-in; the init script supplies its empty default itself.
        Assert.Contains("export BRAID_LOCAL_ADMINISTRATION_PASSWORD=${BRAID_LOCAL_ADMINISTRATION_PASSWORD:-}", init, StringComparison.Ordinal);
        string[] optional = ["ADMINISTRATION", "SPEC_READER", "SPEC_WRITER", "SPEC_MAINTENANCE", "CREATION_READER", "CREATION_WRITER", "CREATION_APPROVER"];
        foreach (var role in optional)
            Assert.Contains($"export BRAID_LOCAL_{role}_PASSWORD=${{BRAID_LOCAL_{role}_PASSWORD:-}}", init, StringComparison.Ordinal);
        return RequiredPasswordRoles(init).Except(optional, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Azure_provisioning_declares_every_current_migration_login()
    {
        var text = File.ReadAllText(Path.Combine(Repository(), "deploy/azure/provision-database.sh"));
        Assert.Empty(MissingPasswordRoles(text));
        foreach (var role in new[] { "reader", "writer", "maintenance" })
        {
            Assert.Contains($"CREATE ROLE braid_spec_{role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS", text, StringComparison.Ordinal);
            Assert.Contains($"export BRAID_SPEC_{role.ToUpperInvariant()}_PASSWORD=${{BRAID_SPEC_{role.ToUpperInvariant()}_PASSWORD:-}}", text, StringComparison.Ordinal);
        }
        Assert.Contains("CREATE ROLE braid_chat_approver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS", text, StringComparison.Ordinal);
        foreach (var role in new[] { "reader", "writer", "approver" })
        {
            Assert.Contains($"CREATE ROLE braid_creation_{role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS", text, StringComparison.Ordinal);
            Assert.Contains($"export BRAID_CREATION_{role.ToUpperInvariant()}_PASSWORD=${{BRAID_CREATION_{role.ToUpperInvariant()}_PASSWORD:-}}", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("deploy/local/postgres.compose.yml")]
    [InlineData("deploy/compose/compose.yml")]
    public void Disabled_intake_roles_accept_empty_credentials_but_compose_delivers_opt_in_values(string path)
    {
        var environment = ServiceEnvironment(File.ReadAllText(Path.Combine(Repository(), path)), "postgres");
        foreach (var role in new[] { "SPEC_READER", "SPEC_WRITER", "SPEC_MAINTENANCE", "CREATION_READER", "CREATION_WRITER", "CREATION_APPROVER" })
            Assert.Equal($"${{BRAID_LOCAL_{role}_PASSWORD:-}}", environment[$"BRAID_LOCAL_{role}_PASSWORD"]);
    }

    private static Dictionary<string, string> ServiceEnvironment(string yaml, string service)
    {
        // Only the shipped explicit scalar environment maps are accepted; no YAML evaluation or secret interpolation.
        var block = Regex.Match(yaml, @"^  " + Regex.Escape(service) + @":\r?\n(?<body>.*?)(?=^  [a-z][a-z0-9_-]*:|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(block.Success, "Expected an explicit Compose service.");
        return Regex.Matches(block.Groups["body"].Value, @"^      (?<key>[A-Z][A-Z0-9_]+): (?<value>[^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
    }

    [RegressionNativeTheory]
    [InlineData("deploy/local/postgres-init.sh", true)]
    [InlineData("deploy/local/postgres-init.sh", false)]
    [InlineData("deploy/azure/provision-database.sh", true)]
    [InlineData("deploy/azure/provision-database.sh", false)]
    public async Task Provisioning_SQL_creates_the_logins_required_by_the_full_migration_manifest(string script, bool administration)
    {
        var bin = Environment.GetEnvironmentVariable("BRAID_POSTGRES_BIN")!;
        var root = Environment.GetEnvironmentVariable("BRAID_POSTGRES_WORK_ROOT") ?? Path.GetTempPath();
        var user = "provision_" + Guid.NewGuid().ToString("N");
        var adminPassword = Guid.NewGuid().ToString("N");
        await using var host = new NativePostgresTestHost(bin, root, user, adminPassword);
        await host.InitializeAsync();
        var baseline = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = host.Port,
            Username = user,
            Password = adminPassword,
            Database = "postgres",
            Pooling = false,
            SslMode = SslMode.Disable
        }.ConnectionString;
        await using (var admin = new NpgsqlConnection(baseline))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE braid TEMPLATE template0 ENCODING 'UTF8'", admin);
            await create.ExecuteNonQueryAsync();
        }
        var passwords = new Dictionary<string, string>();
        foreach (var role in PasswordRoles)
            passwords[role] = Guid.NewGuid().ToString("N");
        var text = await File.ReadAllTextAsync(Path.Combine(Repository(), script));
        Assert.Empty(MissingPasswordRoles(text));
        var match = Regex.Match(text, "<<'SQL'\\r?\\n(.*?)\\r?\\nSQL", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "Provisioning SQL block must be explicit.");
        var start = new ProcessStartInfo(Path.Combine(bin, "psql.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("PG", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        start.ArgumentList.Add("--no-psqlrc"); start.ArgumentList.Add("--set=ON_ERROR_STOP=1"); start.ArgumentList.Add("--quiet");
        start.Environment["PGHOST"] = "127.0.0.1";
        start.Environment["PGPORT"] = host.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = user;
        start.Environment["PGPASSWORD"] = adminPassword;
        start.Environment["PGDATABASE"] = "braid";
        start.Environment["PGSSLMODE"] = "disable";
        start.Environment["PGGSSENCMODE"] = "disable";
        foreach (var (name, password) in passwords)
        {
            var value = name == "ADMINISTRATION" && !administration ? "" : password;
            start.Environment["BRAID_" + name + "_PASSWORD"] = value;
            start.Environment["BRAID_LOCAL_" + name + "_PASSWORD"] = value;
        }
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var output = DrainAsync(process.StandardOutput, deadline.Token);
            var error = DrainAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteAsync(match.Groups[1].Value);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            Assert.True(process.ExitCode == 0, "The native provisioning SQL must succeed without exposing process output.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        string Login(string name, string login) => new NpgsqlConnectionStringBuilder(baseline)
        { Database = "braid", Username = login, Password = passwords[name] }.ConnectionString;
        await using (var admin = new NpgsqlConnection(baseline))
        {
            await admin.OpenAsync();
            await using var optional = new NpgsqlCommand("SELECT rolpassword IS NULL FROM pg_catalog.pg_authid WHERE rolname='braid_administration'", admin);
            Assert.Equal(!administration, (bool)(await optional.ExecuteScalarAsync())!);
        }
        await using (var migrations = new MigrationRunner(Login("MIGRATOR", "braid_migrator"))) await migrations.MigrateAsync();
        await using var reader = new PostgresRegressionRunStore(Login("RUN_READER", "braid_run_reader"), writer: false);
        Assert.Null(await reader.ReadAsync(new ActorContext(new(Guid.NewGuid())), Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Deployment_inputs_supply_all_regression_passwords_without_enabling_runtime_features()
    {
        var root = Repository();
        foreach (var role in new[] { "CASE_READER", "CASE_WRITER", "RUN_READER", "RUN_WRITER", "COMPARISON", "BUILD_READER", "BUILD_WRITER", "BUILD_RUN_READER", "BUILD_RUN_WRITER", "BUILD_DISPATCHER", "BUILD_METADATA" })
        {
            foreach (var path in new[] { "deploy/local/.env.example", "deploy/local/postgres.compose.yml", "deploy/compose/compose.yml" })
                Assert.Contains("BRAID_LOCAL_" + role + "_PASSWORD", File.ReadAllText(Path.Combine(root, path)), StringComparison.Ordinal);
            var key = role.ToLowerInvariant().Replace('_', '-');
            var login = "braid_" + role.ToLowerInvariant() + (role == "BUILD_METADATA" ? "_reader" : role == "COMPARISON" ? "_settler" : "");
            Assert.Contains($"key: '{key}', env: '{role}'", File.ReadAllText(Path.Combine(root, "deploy/azure/modules/application.bicep")), StringComparison.Ordinal);
            Assert.Contains($"[{key}]={login}", File.ReadAllText(Path.Combine(root, "deploy/azure/initialize-secrets.sh")), StringComparison.Ordinal);
            Assert.Contains($"[\"{login}\", \"{role}\"]",
                File.ReadAllText(Path.Combine(root, "tools/browser-fixture/runtime.mjs")), StringComparison.Ordinal);
        }
        // The browser fixture admits its exact migration inventory from schema.mjs.
        var browserFixture = File.ReadAllText(Path.Combine(root, "tools/browser-fixture/schema.mjs"));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "migrations/postgres"), "*.sql"))
            Assert.Contains('"' + Path.GetFileName(path) + '"', browserFixture, StringComparison.Ordinal);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token) != 0) { }
    }

    private static string[] RequiredPasswordRoles(string text) => Regex.Matches(text,
            @"^\\getenv [a-z_]+ BRAID_(?:LOCAL_)?(?<role>[A-Z_]+)_PASSWORD\r?$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant)
        .Select(match => match.Groups["role"].Value).Distinct(StringComparer.Ordinal).ToArray();

    private static string[] MissingPasswordRoles(string text) => RequiredPasswordRoles(text)
        .Where(role => !PasswordRoles.Contains(role, StringComparer.Ordinal)).ToArray();

    private static string Repository()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "deploy")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository source is required for provisioning qualification.");
    }
}

public sealed class RegressionNativeTheoryAttribute : TheoryAttribute
{
    public RegressionNativeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("BRAID_POSTGRES_BIN") is null || !OperatingSystem.IsWindows())
            Skip = "Set BRAID_POSTGRES_BIN on Windows to qualify deployment SQL through native psql.";
    }
}
