using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Owned actual PostgreSQL";
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresFixture.IsConfigured)
            Skip = "Set WEFT_POSTGRES_IMAGE or WEFT_POSTGRES_BIN to execute actual database gates.";
    }
}

/// <summary>A uniquely owned cluster; the optional isolated client is only attached and detached.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string QualifiedImage = "docker.io/library/postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc";
    internal static bool IsConfigured => new[] { "WEFT_POSTGRES_IMAGE", "WEFT_TEST_CLIENT_CONTAINER", "WEFT_DOCKER_EXECUTABLE", "WEFT_POSTGRES_BIN" }
        .Any(name => Environment.GetEnvironmentVariable(name) is not null);
    private const string OwnerLabel = "weft.postgres-test-owner";
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly string bootstrapPassword = Guid.NewGuid().ToString("N");
    private readonly string migratorPassword = Guid.NewGuid().ToString("N");
    private readonly string runtimePassword = Guid.NewGuid().ToString("N");
    private readonly string ingestionPassword = Guid.NewGuid().ToString("N");
    private readonly string catalogPassword = Guid.NewGuid().ToString("N");
    private readonly string metadataPassword = Guid.NewGuid().ToString("N");
    private readonly string projectCreationPassword = Guid.NewGuid().ToString("N");
    private readonly string reportPassword = Guid.NewGuid().ToString("N");
    private readonly string derivationPassword = Guid.NewGuid().ToString("N");
    private readonly string maintenancePassword = Guid.NewGuid().ToString("N");
    private readonly string identityPassword = Guid.NewGuid().ToString("N");
    private readonly string payloadPermissionPassword = Guid.NewGuid().ToString("N");
    private readonly string sessionReaderPassword = Guid.NewGuid().ToString("N");
    private readonly string sessionWriterPassword = Guid.NewGuid().ToString("N");
    private readonly string chatReaderPassword = Guid.NewGuid().ToString("N");
    private readonly string chatWriterPassword = Guid.NewGuid().ToString("N");
    private readonly string chatLifecyclePassword = Guid.NewGuid().ToString("N");
    private readonly string previewReaderPassword = Guid.NewGuid().ToString("N");
    private readonly string previewWriterPassword = Guid.NewGuid().ToString("N");
    private readonly bool provisionMetadataEditor;
    private readonly bool provisionProjectCreator;
    private readonly bool provisionReportReader;
    private readonly bool provisionDeriver;
    private readonly bool provisionMaintenance;
    private readonly bool provisionIdentityReader;
    private readonly bool provisionPayloadPermissionReader;
    private readonly bool provisionSessionReader;
    private readonly bool provisionSessionWriter;
    private readonly bool provisionChatReader;
    private readonly bool provisionChatWriter;
    private readonly bool provisionChatLifecycle;
    private readonly bool provisionPreviewProfiles;
    private readonly ConcurrentDictionary<string, byte> caseDatabases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim caseRemoval = new(1, 1);
    private readonly string? client = Environment.GetEnvironmentVariable("WEFT_TEST_CLIENT_CONTAINER");
    private readonly string docker = Environment.GetEnvironmentVariable("WEFT_DOCKER_EXECUTABLE") ?? "docker";
    private string Host => client is null ? "127.0.0.1" : Container;
    private string Container => $"weft-pg-{owner}";
    private string Network => $"weft-pg-net-{owner}";
    private string Volume => $"weft-pg-data-{owner}";
    private string BootstrapUser => $"weft_bootstrap_{owner}";
    private int port = 5432;
    private bool networkAttempted;
    private bool volumeAttempted;
    private bool containerAttempted;
    private bool clientAttached;
    private NativePostgresTestHost? native;

    public PostgresFixture() : this(provisionMetadataEditor: true, provisionProjectCreator: true) { }

    internal PostgresFixture(bool provisionMetadataEditor, bool provisionProjectCreator = true,
        bool provisionReportReader = true, bool provisionDeriver = true, bool provisionMaintenance = true,
        bool provisionIdentityReader = true, bool provisionPayloadPermissionReader = true,
        bool provisionSessionReader = true, bool provisionSessionWriter = true,
        bool provisionChatReader = true, bool provisionChatWriter = true, bool provisionChatLifecycle = true,
        bool provisionPreviewProfiles = true)
    {
        this.provisionMetadataEditor = provisionMetadataEditor;
        this.provisionProjectCreator = provisionProjectCreator;
        this.provisionReportReader = provisionReportReader;
        this.provisionDeriver = provisionDeriver;
        this.provisionMaintenance = provisionMaintenance;
        this.provisionIdentityReader = provisionIdentityReader;
        this.provisionPayloadPermissionReader = provisionPayloadPermissionReader;
        this.provisionSessionReader = provisionSessionReader;
        this.provisionSessionWriter = provisionSessionWriter;
        this.provisionChatReader = provisionChatReader;
        this.provisionChatWriter = provisionChatWriter;
        this.provisionChatLifecycle = provisionChatLifecycle;
        this.provisionPreviewProfiles = provisionPreviewProfiles;
    }

    public async Task InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("WEFT_POSTGRES_IMAGE");
        if (!IsConfigured)
            return;
        var nativeBin = Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN");
        if (nativeBin is not null && new[] { image, client, Environment.GetEnvironmentVariable("WEFT_DOCKER_EXECUTABLE") }.Any(value => value is not null))
            throw new InvalidOperationException("Native and Docker PostgreSQL fixture modes cannot be combined.");
        if (nativeBin is null && image != QualifiedImage)
            throw new InvalidOperationException("WEFT_POSTGRES_IMAGE must exactly equal the qualified immutable linux/amd64 manifest.");
        if (client is not null && (string.IsNullOrWhiteSpace(client) || client.StartsWith('-')))
            throw new InvalidOperationException("WEFT_TEST_CLIENT_CONTAINER must name the explicitly owned, running qualification client.");
        try
        {
            if (nativeBin is not null)
            {
                native = new NativePostgresTestHost(nativeBin,
                    Environment.GetEnvironmentVariable("WEFT_POSTGRES_WORK_ROOT") ?? Path.GetTempPath(), BootstrapUser, bootstrapPassword);
                await native.InitializeAsync();
                port = native.Port;
            }
            else
            {
                await DockerAsync(["pull", "--platform", "linux/amd64", image!], timeoutSeconds: 180);
                var platform = await DockerAsync(["image", "inspect", "--format", "{{.Os}}/{{.Architecture}}", image!]);
                if (platform.Trim() != "linux/amd64")
                    throw new InvalidOperationException("The qualified image did not resolve to linux/amd64.");
                networkAttempted = true;
                var networkArguments = new List<string> { "network", "create", "--driver", "bridge" };
                if (client is not null)
                    networkArguments.Add("--internal");
                networkArguments.AddRange(["--label", $"{OwnerLabel}={owner}", Network]);
                await DockerAsync(networkArguments);
                volumeAttempted = true;
                await DockerAsync(["volume", "create", "--label", $"{OwnerLabel}={owner}", Volume]);
                if (client is not null)
                {
                    var running = await DockerAsync(["container", "inspect", "--format", "{{.State.Running}}", client]);
                    if (running.Trim() != "true")
                        throw new InvalidOperationException("The configured isolated client is not running.");
                    await DockerAsync(["network", "connect", Network, client]);
                    clientAttached = true;
                }
                var arguments = new List<string>
            {
                "run", "--detach", "--platform", "linux/amd64", "--name", Container,
                "--label", $"{OwnerLabel}={owner}", "--network", Network,
                "--network-alias", Container, "--mount", $"type=volume,source={Volume},target=/var/lib/postgresql/data",
                "--memory", "512m", "--cpus", "1", "--pids-limit", "128",
                "--env", "POSTGRES_PASSWORD", "--env", $"POSTGRES_USER={BootstrapUser}",
                "--env", "POSTGRES_DB=postgres"
            };
                containerAttempted = true;
                await StartContainerAsync(arguments, image!);
                if (client is null)
                {
                    var binding = await DockerAsync(["container", "port", Container, "5432/tcp"]);
                    var address = binding.Trim().Split(':');
                    if (address.Length != 2 || address[0] != "127.0.0.1"
                        || !int.TryParse(address[1], CultureInfo.InvariantCulture, out var actualPort) || actualPort != port)
                        throw new InvalidOperationException("The disposable cluster must publish only its selected stable loopback port.");
                }
            }
            await WaitForServerAsync();
            await using var bootstrap = await OpenBootstrapAsync();
            await using var version = new NpgsqlCommand("SHOW server_version", bootstrap);
            if (((string?)await version.ExecuteScalarAsync())?.Split(' ')[0] != "16.15")
                throw new InvalidOperationException("Actual PostgreSQL server version is not the qualified 16.15 baseline.");
            await using var roles = new NpgsqlCommand($"""
                CREATE ROLE weft_migrator LOGIN PASSWORD '{migratorPassword}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                CREATE ROLE weft_runtime LOGIN PASSWORD '{runtimePassword}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                CREATE ROLE weft_ingest LOGIN PASSWORD '{ingestionPassword}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                CREATE ROLE weft_catalog_reader LOGIN PASSWORD '{catalogPassword}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT SET ON PARAMETER weft.organization_id, weft.chat_principal_id TO weft_migrator;
                """, bootstrap);
            await roles.ExecuteNonQueryAsync();
            if (provisionMetadataEditor)
            {
                await using var metadataRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_catalog_editor LOGIN PASSWORD '{metadataPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await metadataRole.ExecuteNonQueryAsync();
            }
            if (provisionProjectCreator)
            {
                await using var creatorRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_project_creator LOGIN PASSWORD '{projectCreationPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await creatorRole.ExecuteNonQueryAsync();
            }
            if (provisionReportReader)
            {
                await using var reportRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_report_reader LOGIN PASSWORD '{reportPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await reportRole.ExecuteNonQueryAsync();
            }
            if (provisionDeriver)
            {
                await using var derivationRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_deriver LOGIN PASSWORD '{derivationPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await derivationRole.ExecuteNonQueryAsync();
            }
            if (provisionMaintenance)
            {
                await using var maintenanceRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_maintenance LOGIN PASSWORD '{maintenancePassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await maintenanceRole.ExecuteNonQueryAsync();
            }
            if (provisionIdentityReader)
            {
                await using var identityRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_identity_reader LOGIN PASSWORD '{identityPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await identityRole.ExecuteNonQueryAsync();
            }
            if (provisionPayloadPermissionReader)
            {
                await using var payloadPermissionRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_payload_permission_reader LOGIN PASSWORD '{payloadPermissionPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await payloadPermissionRole.ExecuteNonQueryAsync();
            }
            if (provisionSessionReader)
            {
                await using var sessionReaderRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_session_reader LOGIN PASSWORD '{sessionReaderPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await sessionReaderRole.ExecuteNonQueryAsync();
            }
            if (provisionSessionWriter)
            {
                await using var sessionWriterRole = new NpgsqlCommand($"""
                    CREATE ROLE weft_session_writer LOGIN PASSWORD '{sessionWriterPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await sessionWriterRole.ExecuteNonQueryAsync();
            }
            if (provisionChatReader)
            {
                await using var role = new NpgsqlCommand($"""
                    CREATE ROLE weft_chat_reader LOGIN PASSWORD '{chatReaderPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await role.ExecuteNonQueryAsync();
            }
            if (provisionChatWriter)
            {
                await using var role = new NpgsqlCommand($"""
                    CREATE ROLE weft_chat_writer LOGIN PASSWORD '{chatWriterPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await role.ExecuteNonQueryAsync();
            }
            if (provisionChatLifecycle)
            {
                await using var role = new NpgsqlCommand($"""
                    CREATE ROLE weft_chat_lifecycle LOGIN PASSWORD '{chatLifecyclePassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await role.ExecuteNonQueryAsync();
            }
            if (provisionPreviewProfiles)
            {
                await using var role = new NpgsqlCommand($"""
                    CREATE ROLE weft_preview_reader LOGIN PASSWORD '{previewReaderPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    CREATE ROLE weft_preview_writer LOGIN PASSWORD '{previewWriterPassword}'
                      NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                    """, bootstrap);
                await role.ExecuteNonQueryAsync();
            }
        }
        catch (Exception initialization)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Owned PostgreSQL setup and cleanup failed.", initialization, cleanup); }
            throw;
        }
    }

    public async Task<PostgresCase> CreateCaseAsync(bool migrate = true)
    {
        var database = $"weft_case_{Guid.NewGuid():N}";
        await using var bootstrap = await OpenBootstrapAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database} OWNER weft_migrator", bootstrap))
            await create.ExecuteNonQueryAsync();
        if (!caseDatabases.TryAdd(database, 0))
            throw new InvalidOperationException("The newly created PostgreSQL case database was already registered.");
        PostgresCase? test = null;
        try
        {
            var connectRoles = new List<string> { "weft_runtime", "weft_ingest", "weft_catalog_reader" };
            if (provisionMetadataEditor) connectRoles.Add("weft_catalog_editor");
            if (provisionProjectCreator) connectRoles.Add("weft_project_creator");
            if (provisionReportReader) connectRoles.Add("weft_report_reader");
            if (provisionDeriver) connectRoles.Add("weft_deriver");
            if (provisionMaintenance) connectRoles.Add("weft_maintenance");
            if (provisionIdentityReader) connectRoles.Add("weft_identity_reader");
            if (provisionPayloadPermissionReader) connectRoles.Add("weft_payload_permission_reader");
            if (provisionSessionReader) connectRoles.Add("weft_session_reader");
            if (provisionSessionWriter) connectRoles.Add("weft_session_writer");
            if (provisionChatReader) connectRoles.Add("weft_chat_reader");
            if (provisionChatWriter) connectRoles.Add("weft_chat_writer");
            if (provisionChatLifecycle) connectRoles.Add("weft_chat_lifecycle");
            if (provisionPreviewProfiles) connectRoles.AddRange(["weft_preview_reader", "weft_preview_writer"]);
            await using (var grants = new NpgsqlCommand($"REVOKE ALL ON DATABASE {database} FROM PUBLIC; GRANT CONNECT ON DATABASE {database} TO {string.Join(", ", connectRoles)}", bootstrap))
                await grants.ExecuteNonQueryAsync();
            test = new PostgresCase(this, database, ConnectionString(database, "weft_migrator", migratorPassword),
                ConnectionString(database, "weft_runtime", runtimePassword), ConnectionString(database, "weft_ingest", ingestionPassword),
                ConnectionString(database, "weft_catalog_reader", catalogPassword),
                ConnectionString(database, "weft_catalog_editor", metadataPassword),
                ConnectionString(database, "weft_project_creator", projectCreationPassword),
                ConnectionString(database, "weft_report_reader", reportPassword),
                ConnectionString(database, "weft_deriver", derivationPassword),
                ConnectionString(database, "weft_maintenance", maintenancePassword),
                ConnectionString(database, "weft_identity_reader", identityPassword),
                ConnectionString(database, "weft_payload_permission_reader", payloadPermissionPassword),
                ConnectionString(database, "weft_session_reader", sessionReaderPassword),
                ConnectionString(database, "weft_session_writer", sessionWriterPassword),
                ConnectionString(database, "weft_chat_reader", chatReaderPassword),
                ConnectionString(database, "weft_chat_writer", chatWriterPassword),
                ConnectionString(database, "weft_chat_lifecycle", chatLifecyclePassword),
                ConnectionString(database, "weft_preview_reader", previewReaderPassword),
                ConnectionString(database, "weft_preview_writer", previewWriterPassword));
            await using var ownerConnection = await test.Owner.OpenConnectionAsync();
            await using var publicSchema = new NpgsqlCommand("REVOKE ALL ON SCHEMA public FROM PUBLIC", ownerConnection);
            await publicSchema.ExecuteNonQueryAsync();
            if (migrate)
                await test.MigrateAsync();
            return test;
        }
        catch (Exception initialization)
        {
            try
            {
                if (test is null)
                    await DropCaseDatabaseAsync(database);
                else
                    await test.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Owned PostgreSQL case setup and cleanup failed.", initialization, cleanup);
            }
            throw;
        }
    }

    internal async Task DropCaseDatabaseAsync(string name)
    {
        if (!IsGeneratedCaseName(name) || !caseDatabases.ContainsKey(name))
            throw new InvalidOperationException("Refusing removal of a PostgreSQL database not registered by this fixture.");
        await caseRemoval.WaitAsync();
        try
        {
            if (!caseDatabases.ContainsKey(name))
                throw new InvalidOperationException("Refusing removal of a PostgreSQL database not registered by this fixture.");
            await using var bootstrap = await OpenBootstrapAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", bootstrap);
            await drop.ExecuteNonQueryAsync();
            caseDatabases.TryRemove(name, out _);
        }
        finally { caseRemoval.Release(); }
    }

    internal async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var bootstrap = await OpenBootstrapAsync();
        await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = $1)", bootstrap);
        exists.Parameters.Add(new NpgsqlParameter { Value = name });
        return (bool)(await exists.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected database existence result."));
    }

    private static bool IsGeneratedCaseName(string name)
    {
        const string prefix = "weft_case_";
        if (name is null || name.Length != prefix.Length + 32 || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var character in name.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    public async Task RestartAsync()
    {
        if (native is not null) await native.RestartAsync();
        else
        {
            await DockerAsync(["container", "stop", "--time", "15", Container]);
            await DockerAsync(["container", "start", Container]);
        }
        await WaitForServerAsync();
    }

    private async Task StartContainerAsync(IReadOnlyList<string> arguments, string image)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var start = new List<string>(arguments);
            if (client is null)
            {
                // Reserve an unused loopback port before selecting the immutable binding. Docker
                // needs the socket released to bind it; the narrow release/create race is retried.
                var reservation = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    reservation.Start();
                    port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                }
                finally { reservation.Stop(); }
                start.AddRange(["--publish", $"127.0.0.1:{port}:5432"]);
            }
            start.Add(image);
            var result = await RunDockerAsync(start, bootstrapPassword);
            if (result.ExitCode == 0)
                return;
            var collision = client is null && (result.Error.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("address already in use", StringComparison.OrdinalIgnoreCase));
            if (!collision || attempt == 2)
                throw new InvalidOperationException($"Owned PostgreSQL container start failed with exit code {result.ExitCode}.{SafeDockerError(result.Error)}");
            await RemoveOwnedAsync("container", Container, ["container", "rm", "--force", Container]);
        }
        throw new InvalidOperationException("Could not allocate the disposable cluster's loopback endpoint.");
    }

    internal async Task ExecuteBootstrapAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(database, BootstrapUser, bootstrapPassword, pooling: false));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private string ConnectionString(string database, string username, string password, bool pooling = true) => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = port,
        Database = database,
        Username = username,
        Password = password,
        Pooling = pooling,
        MinPoolSize = 0,
        MaxPoolSize = 1,
        // Windows creates a backend process for each connection; keep this setup
        // allowance separate from query/lock deadlines and the Docker transport.
        Timeout = native is null ? 3 : 15,
        CommandTimeout = 15,
        Enlist = false,
        Multiplexing = false,
        NoResetOnClose = false,
        IncludeErrorDetail = false,
        ApplicationName = "weft-owned-postgres-tests",
        SslMode = SslMode.Disable
    }.ConnectionString;

    private async Task<NpgsqlConnection> OpenBootstrapAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString("postgres", BootstrapUser, bootstrapPassword, pooling: false));
        try { await connection.OpenAsync(); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task WaitForServerAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Exception? last = null;
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString("postgres", BootstrapUser, bootstrapPassword, pooling: false));
                await connection.OpenAsync(deadline.Token);
                return;
            }
            catch (NpgsqlException exception) { last = exception; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
        }
        if (native is not null) throw new InvalidOperationException("The owned native PostgreSQL endpoint did not become ready.");
        var state = await RunDockerAsync(["container", "inspect", "--format", "{{.State.Status}} exit={{.State.ExitCode}} {{json .NetworkSettings.Ports}}", Container], timeoutSeconds: 5);
        var evidence = state.ExitCode == 0 ? SafeDockerError(state.Output) : SafeDockerError(state.Error);
        throw new InvalidOperationException($"Owned PostgreSQL did not become ready within 60 seconds at {Host}:{port} ({last?.GetType().Name}).{evidence}");
    }

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        async Task Clean(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (native is not null)
            await Clean(async () => await native.DisposeAsync());
        if (clientAttached)
            await Clean(async () => { await DockerAsync(["network", "disconnect", Network, client!]); clientAttached = false; });
        if (containerAttempted)
            await Clean(() => RemoveOwnedAsync("container", Container, ["container", "rm", "--force", Container]));
        if (volumeAttempted)
            await Clean(() => RemoveOwnedAsync("volume", Volume, ["volume", "rm", Volume]));
        if (networkAttempted)
            await Clean(() => RemoveOwnedAsync("network", Network, ["network", "rm", Network]));
        if (failures.Count != 0)
            throw new AggregateException("Cleanup of explicitly owned PostgreSQL resources failed.", failures);
    }

    private async Task RemoveOwnedAsync(string kind, string name, IReadOnlyList<string> removal)
    {
        var format = kind == "container" ? $"{{{{ index .Config.Labels \"{OwnerLabel}\" }}}}" : $"{{{{ index .Labels \"{OwnerLabel}\" }}}}";
        var inspected = await RunDockerAsync([kind, "inspect", "--format", format, name]);
        if (inspected.ExitCode != 0)
        {
            if (inspected.Error.Contains("No such", StringComparison.OrdinalIgnoreCase)
                || inspected.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException($"Could not confirm ownership/absence of disposable {kind}.");
        }
        if (inspected.Output.Trim() != owner)
            throw new InvalidOperationException($"Refusing removal of {kind} without this fixture's exact ownership label.");
        await DockerAsync(removal);
    }

    private async Task<string> DockerAsync(IReadOnlyList<string> arguments, string? password = null, int timeoutSeconds = 45)
    {
        var result = await RunDockerAsync(arguments, password, timeoutSeconds);
        if (result.ExitCode != 0)
        {
            var evidence = arguments[0] == "network" || arguments is ["container", "port", ..]
                ? SafeDockerError(result.Error)
                : string.Empty;
            throw new InvalidOperationException($"Owned PostgreSQL Docker operation {arguments[0]} failed with exit code {result.ExitCode}.{evidence}");
        }
        return result.Output;
    }

    private string SafeDockerError(string error)
    {
        var safe = error.Replace(bootstrapPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(migratorPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(runtimePassword, "[redacted]", StringComparison.Ordinal)
            .Replace(ingestionPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(catalogPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(metadataPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(projectCreationPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(reportPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(derivationPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(maintenancePassword, "[redacted]", StringComparison.Ordinal)
            .Replace(identityPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(payloadPermissionPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(sessionReaderPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(sessionWriterPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(chatReaderPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(chatWriterPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(chatLifecyclePassword, "[redacted]", StringComparison.Ordinal)
            .Replace(previewReaderPassword, "[redacted]", StringComparison.Ordinal)
            .Replace(previewWriterPassword, "[redacted]", StringComparison.Ordinal).Trim();
        return safe.Length == 0 ? string.Empty : $" Docker reported: {safe[..Math.Min(safe.Length, 2000)]}";
    }

    private async Task<(int ExitCode, string Output, string Error)> RunDockerAsync(IReadOnlyList<string> arguments, string? password = null, int timeoutSeconds = 45)
    {
        var start = new ProcessStartInfo(docker) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (password is not null)
            start.Environment["POSTGRES_PASSWORD"] = password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The explicitly configured Docker executable did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(reap.Token);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + count > 64 * 1024)
                throw new InvalidOperationException("Disposable PostgreSQL process output exceeded its capture limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

public sealed class PostgresCase : IAsyncDisposable
{
    private readonly PostgresFixture fixture;
    private readonly SemaphoreSlim lifetime = new(1, 1);
    private bool ownerPoolClosed;
    private bool disposed;
    public string Name { get; }
    public string MigrationConnectionString { get; }
    public string RuntimeConnectionString { get; }
    public string IngestionConnectionString { get; }
    public string CatalogConnectionString { get; }
    public string MetadataConnectionString { get; }
    public string ProjectCreationConnectionString { get; }
    public string ReportConnectionString { get; }
    public string DerivationConnectionString { get; }
    public string MaintenanceConnectionString { get; }
    public string IdentityConnectionString { get; }
    public string PayloadPermissionConnectionString { get; }
    public string SessionReaderConnectionString { get; }
    public string SessionWriterConnectionString { get; }
    public string ChatReaderConnectionString { get; }
    public string ChatWriterConnectionString { get; }
    public string ChatLifecycleConnectionString { get; }
    public string PreviewReaderConnectionString { get; }
    public string PreviewWriterConnectionString { get; }
    public NpgsqlDataSource Owner { get; }

    internal PostgresCase(PostgresFixture fixture, string name, string migrationConnectionString, string runtimeConnectionString,
        string ingestionConnectionString, string catalogConnectionString, string metadataConnectionString,
        string projectCreationConnectionString, string reportConnectionString, string derivationConnectionString,
        string maintenanceConnectionString, string identityConnectionString, string payloadPermissionConnectionString,
        string sessionReaderConnectionString, string sessionWriterConnectionString,
        string chatReaderConnectionString, string chatWriterConnectionString, string chatLifecycleConnectionString,
        string previewReaderConnectionString, string previewWriterConnectionString)
    {
        this.fixture = fixture;
        Name = name;
        MigrationConnectionString = migrationConnectionString;
        RuntimeConnectionString = runtimeConnectionString;
        IngestionConnectionString = ingestionConnectionString;
        CatalogConnectionString = catalogConnectionString;
        MetadataConnectionString = metadataConnectionString;
        ProjectCreationConnectionString = projectCreationConnectionString;
        ReportConnectionString = reportConnectionString;
        DerivationConnectionString = derivationConnectionString;
        MaintenanceConnectionString = maintenanceConnectionString;
        IdentityConnectionString = identityConnectionString;
        PayloadPermissionConnectionString = payloadPermissionConnectionString;
        SessionReaderConnectionString = sessionReaderConnectionString;
        SessionWriterConnectionString = sessionWriterConnectionString;
        ChatReaderConnectionString = chatReaderConnectionString;
        ChatWriterConnectionString = chatWriterConnectionString;
        ChatLifecycleConnectionString = chatLifecycleConnectionString;
        PreviewReaderConnectionString = previewReaderConnectionString;
        PreviewWriterConnectionString = previewWriterConnectionString;
        Owner = NpgsqlDataSource.Create(migrationConnectionString);
    }

    public async Task MigrateAsync()
    {
        await using var runner = new MigrationRunner(MigrationConnectionString);
        await runner.MigrateAsync();
    }

    public Task BootstrapAsync(string sql) => fixture.ExecuteBootstrapAsync(Name, sql);

    public async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var command = Owner.CreateCommand(sql);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var command = Owner.CreateCommand(sql);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected fixture scalar."));
    }

    internal async ValueTask CloseOwnerPoolAsync()
    {
        await lifetime.WaitAsync();
        try { await CloseOwnerPoolCoreAsync(); }
        finally { lifetime.Release(); }
    }

    private async ValueTask CloseOwnerPoolCoreAsync()
    {
        if (ownerPoolClosed)
            return;
        await Owner.DisposeAsync();
        ownerPoolClosed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.WaitAsync();
        try
        {
            if (disposed)
                return;
            await CloseOwnerPoolCoreAsync();
            await fixture.DropCaseDatabaseAsync(Name);
            disposed = true;
        }
        finally { lifetime.Release(); }
    }
}
