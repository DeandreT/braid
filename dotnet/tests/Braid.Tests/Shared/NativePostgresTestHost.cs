using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;

namespace Braid.Tests;

/// <summary>Optional Windows test transport. Owns one child process and a uniquely marked disposable cluster.</summary>
internal sealed class NativePostgresTestHost : IAsyncDisposable
{
    internal const string RequiredVersion = "16.15";
    private readonly string binaryDirectory;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly string username;
    private readonly string password;
    private readonly string root;
    private readonly string data;
    private Process? server;
    private Task? output;
    private Task? error;
    private bool created;

    internal NativePostgresTestHost(string binaryDirectory, string workRoot, string username, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("The native PostgreSQL fixture currently supports Windows only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        if (!Path.IsPathFullyQualified(binaryDirectory) || !Path.IsPathFullyQualified(workRoot))
            throw new ArgumentException("Native PostgreSQL test paths must be explicit absolute paths.");
        if (!Directory.Exists(binaryDirectory) || !Directory.Exists(workRoot))
            throw new ArgumentException("Native PostgreSQL test directories must already exist.");
        this.binaryDirectory = Path.GetFullPath(binaryDirectory);
        this.username = username;
        this.password = password;
        root = Path.Combine(Path.GetFullPath(workRoot), $"braid-native-pg-{owner}");
        data = Path.Combine(root, "data");
    }

    internal string DirectoryPath => root;
    internal string OwnerMarkerPath => Path.Combine(root, ".braid-owner");
    internal int Port { get; private set; }
    internal int? ProcessId => server is { HasExited: false } ? server.Id : null;

    internal async Task InitializeAsync()
    {
        if (created) throw new InvalidOperationException("A native cluster cannot be initialized twice.");
        foreach (var tool in new[] { "postgres", "initdb", "pg_ctl" })
        {
            var version = await CommandAsync(tool, ["--version"]);
            if (version.Trim() != $"{tool} (PostgreSQL) {RequiredVersion}")
                throw new InvalidOperationException("The native PostgreSQL tools must exactly match the qualified version.");
        }
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("A native cluster must use a fresh owned directory.");
        Directory.CreateDirectory(root);
        created = true;
        await using (var marker = new FileStream(OwnerMarkerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await marker.WriteAsync(Encoding.ASCII.GetBytes(owner));
        var passwordFile = Path.Combine(root, ".init-password");
        try
        {
            await File.WriteAllTextAsync(passwordFile, password, new UTF8Encoding(false));
            await CommandAsync("initdb", ["-D", data, "--locale=C", "--encoding=UTF8", "--auth=scram-sha-256",
                "--username", username, "--pwfile", passwordFile], timeoutSeconds: 60);
        }
        finally { File.Delete(passwordFile); }
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        finally { reservation.Stop(); }
        await StartAsync();
    }

    internal async Task RestartAsync()
    {
        RequireOwnedDirectory();
        await StopAsync();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        RequireOwnedDirectory();
        if (server is not null) throw new InvalidOperationException("The owned native server has not stopped.");
        var start = StartInfo("postgres", ["-D", data, "-h", "127.0.0.1", "-p", Port.ToString(CultureInfo.InvariantCulture),
            "-c", "unix_socket_directories=", "-c", "logging_collector=off", "-c", "log_statement=none",
            "-c", "log_min_error_statement=panic", "-c", "log_error_verbosity=terse",
            "-c", "shared_buffers=32MB"]);
        server = Process.Start(start) ?? throw new InvalidOperationException("The native PostgreSQL child did not start.");
        output = DrainAsync(server.StandardOutput);
        error = DrainAsync(server.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = Port,
                Database = "postgres",
                Username = username,
                Password = password,
                Pooling = false,
                Timeout = 1,
                SslMode = SslMode.Disable,
                IncludeErrorDetail = false
            }.ConnectionString;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (server.HasExited) throw new InvalidOperationException("The owned native PostgreSQL server exited before admission.");
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(deadline.Token);
                    await using var check = new NpgsqlCommand("SELECT current_setting('server_version'), current_setting('listen_addresses'), current_setting('data_directory')", connection);
                    await using var reader = await check.ExecuteReaderAsync(deadline.Token);
                    if (!await reader.ReadAsync(deadline.Token) || reader.GetString(0) != RequiredVersion ||
                        reader.GetString(1) != "127.0.0.1" || !SamePath(reader.GetString(2), data))
                        throw new InvalidOperationException("The native PostgreSQL endpoint did not match its owned cluster.");
                    RequireOwnedProcess();
                    return;
                }
                catch (NpgsqlException) { await Task.Delay(100, deadline.Token); }
            }
        }
        catch
        {
            await KillOwnedProcessAsync();
            throw;
        }
    }

    private async Task StopAsync()
    {
        if (server is null) return;
        if (!server.HasExited)
        {
            RequireOwnedProcess();
            await CommandAsync("pg_ctl", ["-D", data, "stop", "-m", "fast", "-w", "-t", "30"], timeoutSeconds: 40);
        }
        await ReapAsync();
    }

    private async Task KillOwnedProcessAsync()
    {
        // This retained Process handle identifies the child we started, not a PID read from an untrusted file.
        if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
        await ReapAsync();
    }

    private async Task ReapAsync()
    {
        if (server is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.WaitForExitAsync(deadline.Token);
        await Task.WhenAll(output ?? Task.CompletedTask, error ?? Task.CompletedTask).WaitAsync(deadline.Token);
        server.Dispose();
        server = null;
        output = null;
        error = null;
    }

    private void RequireOwnedDirectory()
    {
        if (!created || !Directory.Exists(root) || IsLink(root) || !File.Exists(OwnerMarkerPath) || IsLink(OwnerMarkerPath) ||
            new FileInfo(OwnerMarkerPath).Length != owner.Length ||
            File.ReadAllText(OwnerMarkerPath, Encoding.ASCII) != owner)
            throw new InvalidOperationException("Refusing a native PostgreSQL operation without its exact directory ownership marker.");
        if (Directory.Exists(data) && IsLink(data)) throw new InvalidOperationException("The owned database directory cannot be a link.");
    }

    private void RequireOwnedProcess()
    {
        RequireOwnedDirectory();
        if (server is null || server.HasExited) throw new InvalidOperationException("The owned native PostgreSQL child is unavailable.");
        var pidPath = Path.Combine(data, "postmaster.pid");
        if (!File.Exists(pidPath) || IsLink(pidPath) || new FileInfo(pidPath).Length > 4096)
            throw new InvalidOperationException("The native PostgreSQL process identity is unavailable.");
        var lines = File.ReadAllLines(pidPath);
        if (lines.Length < 2 || lines[0] != server.Id.ToString(CultureInfo.InvariantCulture) || !SamePath(lines[1], data))
            throw new InvalidOperationException("Refusing a native PostgreSQL operation against a foreign process identity.");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private ProcessStartInfo StartInfo(string tool, IReadOnlyList<string> arguments)
    {
        var executable = Path.Combine(binaryDirectory, tool + ".exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("A required native PostgreSQL executable is absent.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = binaryDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var variable in new[] { "PGDATA", "PGHOST", "PGHOSTADDR", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD",
            "PGPASSFILE", "PGSERVICE", "PGSERVICEFILE", "PGOPTIONS" }) start.Environment.Remove(variable);
        start.Environment["LC_ALL"] = "C";
        return start;
    }

    private async Task<string> CommandAsync(string tool, IReadOnlyList<string> arguments, int timeoutSeconds = 15)
    {
        using var process = Process.Start(StartInfo(tool, arguments)) ??
            throw new InvalidOperationException("A native PostgreSQL tool did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Native PostgreSQL {tool} failed with exit code {process.ExitCode}.");
            return await stdout;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(reap.Token);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > 64 * 1024) throw new InvalidOperationException("Native PostgreSQL tool output exceeded its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) != 0) { }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        try { await StopAsync(); }
        catch (Exception failure)
        {
            failures.Add(failure);
            try { await KillOwnedProcessAsync(); }
            catch (Exception reap) { failures.Add(reap); }
        }
        try
        {
            if (created)
            {
                RequireOwnedDirectory();
                if (server is not null) throw new InvalidOperationException("The native PostgreSQL process has not been reaped.");
                Directory.Delete(root, recursive: true);
                created = false;
            }
        }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count > 0) throw new AggregateException("Owned native PostgreSQL cleanup failed.", failures);
    }
}
