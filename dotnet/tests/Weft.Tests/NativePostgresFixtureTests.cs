using Npgsql;

namespace Weft.Tests;

public sealed class NativePostgresFactAttribute : FactAttribute
{
    public NativePostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN") is null)
            Skip = "Set WEFT_POSTGRES_BIN to exercise the native Windows PostgreSQL fixture.";
    }
}

[Collection(PostgresCollection.Name)]
public sealed class NativePostgresFixtureTests
{
    [NativePostgresFact]
    public async Task Native_cluster_is_loopback_only_survives_graceful_restart_and_removes_its_owned_data()
    {
        var username = $"probe_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        var host = Host(username, password);
        var directory = host.DirectoryPath;
        try
        {
            await host.InitializeAsync();
            Assert.True(Directory.Exists(directory));
            Assert.False(File.Exists(Path.Combine(directory, ".init-password")));
            var process = host.ProcessId;
            var connectionString = ConnectionString(host, username, password);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand("CREATE TABLE probe(value integer NOT NULL); INSERT INTO probe VALUES (42)", connection);
                await create.ExecuteNonQueryAsync();
            }
            await host.RestartAsync();
            Assert.NotEqual(process, host.ProcessId);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var read = new NpgsqlCommand("SELECT value FROM probe", connection);
                Assert.Equal(42, await read.ExecuteScalarAsync());
                await using var listener = new NpgsqlCommand("SHOW listen_addresses", connection);
                Assert.Equal("127.0.0.1", await listener.ExecuteScalarAsync());
            }
        }
        finally { await host.DisposeAsync(); }
        Assert.False(Directory.Exists(directory));
        Assert.Null(host.ProcessId);
        await host.DisposeAsync();
    }

    [NativePostgresFact]
    public async Task Missing_ownership_marker_stops_only_the_owned_process_and_refuses_directory_removal()
    {
        var host = Host($"probe_{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"));
        string? marker = null;
        try
        {
            await host.InitializeAsync();
            marker = await File.ReadAllTextAsync(host.OwnerMarkerPath);
            File.Delete(host.OwnerMarkerPath);
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
            Assert.Null(host.ProcessId);
            Assert.True(Directory.Exists(host.DirectoryPath));
        }
        finally
        {
            if (marker is not null) await File.WriteAllTextAsync(host.OwnerMarkerPath, marker);
            await host.DisposeAsync();
        }
        Assert.False(Directory.Exists(host.DirectoryPath));
    }

    [NativePostgresFact]
    public async Task Foreign_postmaster_pid_cannot_redirect_shutdown_at_an_unrelated_process()
    {
        var host = Host($"probe_{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"));
        try
        {
            await host.InitializeAsync();
            var pidFile = Path.Combine(host.DirectoryPath, "data", "postmaster.pid");
            var lines = await File.ReadAllLinesAsync(pidFile);
            lines[0] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllLinesAsync(pidFile, lines);
            await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask());
            Assert.Null(host.ProcessId);
            Assert.False(Directory.Exists(host.DirectoryPath));
        }
        finally { await host.DisposeAsync(); }
    }

    [NativePostgresFact]
    public void Native_mode_rejects_relative_or_missing_directories()
    {
        var binaries = Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN")!;
        var work = Environment.GetEnvironmentVariable("WEFT_POSTGRES_WORK_ROOT") ?? Path.GetTempPath();
        Assert.Throws<ArgumentException>(() => new NativePostgresTestHost("relative", work, "user", "test"));
        Assert.Throws<ArgumentException>(() => new NativePostgresTestHost(binaries, "relative", "user", "test"));
        Assert.Throws<ArgumentException>(() => new NativePostgresTestHost(Path.Combine(work, Guid.NewGuid().ToString("N")), work, "user", "test"));
    }

    [NativePostgresFact]
    public async Task A_preexisting_directory_is_not_adopted_or_removed()
    {
        var host = Host($"probe_{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"));
        var sentinel = Path.Combine(host.DirectoryPath, "test-owned-sentinel");
        Directory.CreateDirectory(host.DirectoryPath);
        await File.WriteAllTextAsync(sentinel, "preserve");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.InitializeAsync());
            await host.DisposeAsync();
            Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            // This test created the collision directory itself; the fixture is forbidden from adopting it.
            File.Delete(sentinel);
            Directory.Delete(host.DirectoryPath);
        }
    }

    [NativePostgresFact]
    public async Task Mixed_native_and_Docker_configuration_is_refused_before_startup()
    {
        var previous = Environment.GetEnvironmentVariable("WEFT_POSTGRES_IMAGE");
        Environment.SetEnvironmentVariable("WEFT_POSTGRES_IMAGE", PostgresFixture.QualifiedImage);
        try
        {
            var fixture = new PostgresFixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InitializeAsync());
            await fixture.DisposeAsync();
        }
        finally { Environment.SetEnvironmentVariable("WEFT_POSTGRES_IMAGE", previous); }
    }

    private static NativePostgresTestHost Host(string username, string password) => new(
        Environment.GetEnvironmentVariable("WEFT_POSTGRES_BIN")!,
        Environment.GetEnvironmentVariable("WEFT_POSTGRES_WORK_ROOT") ?? Path.GetTempPath(), username, password);

    private static string ConnectionString(NativePostgresTestHost host, string username, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = host.Port,
        Database = "postgres",
        Username = username,
        Password = password,
        Pooling = false,
        SslMode = SslMode.Disable,
        IncludeErrorDetail = false
    }.ConnectionString;
}
