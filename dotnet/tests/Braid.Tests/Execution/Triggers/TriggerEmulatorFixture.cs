using System.Diagnostics;
using System.Globalization;
using Azure.Messaging.ServiceBus;

namespace Braid.Tests;

[CollectionDefinition(Name)]
public sealed class TriggerEmulatorCollection : ICollectionFixture<TriggerEmulatorFixture>
{
    public const string Name = "Owned trigger emulators";
}

public sealed class TriggerEmulatorFactAttribute : FactAttribute
{
    public TriggerEmulatorFactAttribute()
    {
        if (!TriggerEmulatorFixture.IsConfigured)
            Skip = "Set BRAID_TRIGGER_EMULATORS=compose and BRAID_TRIGGERS_ACCEPT_EULA=Y to execute actual Service Bus emulator gates.";
    }
}

/// <summary>
/// One uniquely named project of deploy/compose/compose.triggers.yml on loopback ephemeral ports. It removes only its own
/// containers, network and volumes.
/// </summary>
public sealed class TriggerEmulatorFixture : IAsyncLifetime
{
    internal static bool IsConfigured => Environment.GetEnvironmentVariable("BRAID_TRIGGER_EMULATORS") == "compose" &&
        Environment.GetEnvironmentVariable("BRAID_TRIGGERS_ACCEPT_EULA") == "Y";
    private readonly string project = "braid-triggers-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string password = "Wt1!" + Guid.NewGuid().ToString("N");
    private readonly string docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "docker";
    private readonly SemaphoreSlim sftpStart = new(1, 1);
    private bool started;
    private ServiceBusClient? client;
    private (int Port, string HostKey)? sftp;

    internal ServiceBusClient Client => client ?? throw new InvalidOperationException("The emulators are not running.");
    internal string ConnectionString { get; private set; } = "";
    internal string SftpPassword { get; } = "S" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Starts the SFTP service once and pins its host key out of band from the container's public key file, never on first use.
    /// </summary>
    internal async Task<(int Port, string HostKey)> SftpAsync()
    {
        await sftpStart.WaitAsync();
        try
        {
            if (sftp is { } running) return running;
            await ComposeAsync(["up", "--detach", "--quiet-pull", "sftp"], 300);
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var binding = (await ComposeAsync(["port", "sftp", "22"])).Trim().Split('\n')[0].Split(':');
                    if (binding.Length != 2 || binding[0] != "127.0.0.1" || !int.TryParse(binding[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                        throw new InvalidOperationException("The SFTP server must publish only a loopback port.");
                    var line = (await ComposeAsync(["exec", "-T", "sftp", "cat", "/etc/ssh/ssh_host_ed25519_key.pub"])).Trim().Split(' ');
                    if (line.Length < 2 || line[0] != "ssh-ed25519") throw new InvalidOperationException("The SFTP host key is not ready.");
                    var key = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(line[1]))).TrimEnd('=');
                    using (var probe = new System.Net.Sockets.TcpClient()) await probe.ConnectAsync(System.Net.IPAddress.Loopback, port);
                    sftp = (port, key);
                    return (port, key);
                }
                catch (Exception) when (deadline.Elapsed < TimeSpan.FromMinutes(3)) { await Task.Delay(1000); }
            }
        }
        finally { sftpStart.Release(); }
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured) return;
        started = true;
        await ComposeAsync(["up", "--detach", "--quiet-pull", "servicebus-sql", "servicebus"], 600);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // A restarted emulator container can publish a new ephemeral port.
                var binding = (await ComposeAsync(["port", "servicebus", "5672"])).Trim().Split('\n')[0].Split(':');
                if (binding.Length != 2 || binding[0] != "127.0.0.1" || !int.TryParse(binding[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    throw new InvalidOperationException("The emulator must publish only a loopback port.");
                var connection = $"Endpoint=sb://127.0.0.1:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
                if (connection != ConnectionString)
                {
                    if (client is not null) await client.DisposeAsync();
                    ConnectionString = connection;
                    client = Braid.Triggers.Emulators.ServiceBusQueueTrigger.CreateEmulatorClient(connection);
                }
                await using var receiver = client!.CreateReceiver("braid-orders-1");
                await receiver.PeekMessageAsync();
                return;
            }
            catch (Exception) when (deadline.Elapsed < TimeSpan.FromMinutes(8)) { await Task.Delay(2000); }
            catch (Exception error)
            {
                var logs = await ComposeAsync(["logs", "--no-color", "--tail", "40", "servicebus"]);
                throw new InvalidOperationException("The Service Bus emulator did not become ready:\n" + logs, error);
            }
        }
    }

    public async Task DisposeAsync()
    {
        try { if (client is not null) await client.DisposeAsync(); }
        finally { if (started) await ComposeAsync(["down", "--volumes", "--timeout", "5"], 180); }
    }

    private async Task<string> ComposeAsync(IReadOnlyList<string> arguments, int timeoutSeconds = 60)
    {
        var start = new ProcessStartInfo(docker) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "compose", "--project-name", project, "--file", ComposeFile() }.Concat(arguments)) start.ArgumentList.Add(argument);
        start.Environment["BRAID_TRIGGERS_SQL_PASSWORD"] = password;
        start.Environment["BRAID_TRIGGERS_SFTP_PASSWORD"] = SftpPassword;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException("A compose command for the owned emulators failed: " + (await error).Trim());
        return await output;
    }

    private static string ComposeFile()
    {
        return Path.Combine(AppContext.BaseDirectory, "BrokerFixture", "compose.triggers.yml");
    }
}
