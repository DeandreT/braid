using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using Weft.Application;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RegressionDeploymentProvisioningTests
{
    private static readonly string[] PasswordRoles =
    [
        "MIGRATOR", "RUNTIME", "INGEST", "CATALOG", "METADATA", "CREATOR", "REPORT", "DERIVATION",
        "MAINTENANCE", "IDENTITY", "PAYLOAD_PERMISSION", "SESSION_READER", "SESSION_WRITER", "CHAT_READER", "CHAT_WRITER",
        "CHAT_LIFECYCLE", "PREVIEW_READER", "PREVIEW_WRITER", "CASE_READER", "CASE_WRITER", "RUN_READER", "RUN_WRITER",
        "BUILD_READER", "BUILD_WRITER", "BUILD_RUN_READER", "BUILD_RUN_WRITER", "BUILD_DISPATCHER", "BUILD_METADATA",
        "QUALIFICATION_READER", "QUALIFICATION_WRITER", "RELEASE_READER", "RELEASE_WRITER", "INVOCATION_GRANT_WRITER", "INVOCATION_WRITER"
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
        Assert.Equal(["NEW_READER"], MissingPasswordRoles("\\getenv new_password WEFT_LOCAL_NEW_READER_PASSWORD\n"));
        Assert.Equal(["NEW_READER"], MissingPasswordRoles("\\getenv new_password WEFT_NEW_READER_PASSWORD\n"));
    }

    [RegressionNativeTheory]
    [InlineData("deploy/local/postgres-init.sh")]
    [InlineData("deploy/azure/provision-database.sh")]
    public async Task Provisioning_SQL_creates_the_logins_required_by_the_full_migration_manifest(string script)
    {
        var bin = Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN")!;
        var root = Environment.GetEnvironmentVariable("WEFT_POSTGRES_WORK_ROOT") ?? Path.GetTempPath();
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
            await using var create = new NpgsqlCommand("CREATE DATABASE weft TEMPLATE template0 ENCODING 'UTF8'", admin);
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
        start.Environment["PGDATABASE"] = "weft";
        start.Environment["PGSSLMODE"] = "disable";
        start.Environment["PGGSSENCMODE"] = "disable";
        foreach (var (name, password) in passwords)
        {
            start.Environment["WEFT_" + name + "_PASSWORD"] = password;
            start.Environment["WEFT_LOCAL_" + name + "_PASSWORD"] = password;
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
        { Database = "weft", Username = login, Password = passwords[name] }.ConnectionString;
        await using (var migrations = new MigrationRunner(Login("MIGRATOR", "weft_migrator"))) await migrations.MigrateAsync();
        await using var reader = new PostgresRegressionRunStore(Login("RUN_READER", "weft_run_reader"), writer: false);
        Assert.Null(await reader.ReadAsync(new ActorContext(new(Guid.NewGuid())), Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Deployment_inputs_supply_all_regression_passwords_without_enabling_runtime_features()
    {
        var root = Repository();
        foreach (var role in new[] { "CASE_READER", "CASE_WRITER", "RUN_READER", "RUN_WRITER", "BUILD_READER", "BUILD_WRITER", "BUILD_RUN_READER", "BUILD_RUN_WRITER", "BUILD_DISPATCHER", "BUILD_METADATA" })
        {
            foreach (var path in new[] { "deploy/local/.env.example", "deploy/local/postgres.compose.yml", "deploy/compose/compose.yml" })
                Assert.Contains("WEFT_LOCAL_" + role + "_PASSWORD", File.ReadAllText(Path.Combine(root, path)), StringComparison.Ordinal);
            var key = role.ToLowerInvariant().Replace('_', '-');
            var login = "weft_" + role.ToLowerInvariant() + (role == "BUILD_METADATA" ? "_reader" : "");
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
            @"^\\getenv [a-z_]+ WEFT_(?:LOCAL_)?(?<role>[A-Z_]+)_PASSWORD\r?$",
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
        if (Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN") is null || !OperatingSystem.IsWindows())
            Skip = "Set WEFT_POSTGRES_BIN on Windows to qualify deployment SQL through native psql.";
    }
}
