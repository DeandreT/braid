using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Weft.Storage.Blob;

namespace Weft.Tests;

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Owned Azurite blob emulator";
}

public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!AzuriteFixture.IsConfigured)
            Skip = "Set WEFT_AZURITE_IMAGE to execute actual blob emulator gates.";
    }
}

public sealed class AzuriteTheoryAttribute : TheoryAttribute
{
    public AzuriteTheoryAttribute()
    {
        if (!AzuriteFixture.IsConfigured)
            Skip = "Set WEFT_AZURITE_IMAGE to execute actual blob emulator gates.";
    }
}

/// <summary>
/// One uniquely owned in-memory Azurite container on a loopback port. Tests create uniquely named
/// blob containers through it and remove only those containers and this emulator.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string QualifiedImage = "mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5";
    internal const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string OwnerLabel = "weft.azurite-test-owner";
    internal static bool IsConfigured => Environment.GetEnvironmentVariable("WEFT_AZURITE_IMAGE") is not null;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly string docker = Environment.GetEnvironmentVariable("WEFT_DOCKER_EXECUTABLE") ?? "docker";
    private readonly ConcurrentDictionary<string, byte> containers = new(StringComparer.Ordinal);
    private bool containerAttempted;
    private Uri? serviceUri;

    private string Container => $"weft-azurite-{owner}";

    internal Uri ServiceUri => serviceUri ?? throw new InvalidOperationException("The emulator is not running.");

    internal BlobServiceClient Service => new(ServiceUri, new StorageSharedKeyCredential("devstoreaccount1", EmulatorKey));

    public async Task InitializeAsync()
    {
        if (!IsConfigured) return;
        if (Environment.GetEnvironmentVariable("WEFT_AZURITE_IMAGE") != QualifiedImage)
            throw new InvalidOperationException("WEFT_AZURITE_IMAGE must exactly equal the qualified immutable emulator image.");
        await DockerAsync(["pull", "--platform", "linux/amd64", QualifiedImage], 180);
        containerAttempted = true;
        await DockerAsync(["run", "--detach", "--platform", "linux/amd64", "--name", Container, "--label", $"{OwnerLabel}={owner}",
            "--publish", "127.0.0.1::10000", "--memory", "512m", "--cpus", "1", "--pids-limit", "128", QualifiedImage,
            "azurite-blob", "--blobHost", "0.0.0.0", "--blobPort", "10000", "--inMemoryPersistence", "--skipApiVersionCheck", "--loose"]);
        var binding = (await DockerAsync(["container", "port", Container, "10000/tcp"])).Trim().Split('\n')[0].Split(':');
        if (binding.Length != 2 || binding[0] != "127.0.0.1" ||
            !int.TryParse(binding[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw new InvalidOperationException("The emulator must publish only a loopback port.");
        serviceUri = new Uri($"http://127.0.0.1:{port}/devstoreaccount1");
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await Service.GetPropertiesAsync();
                return;
            }
            catch (Exception) when (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(250);
            }
        }
    }

    /// <summary>A new private container owned by this fixture; Initialize never creates one itself.</summary>
    internal async Task<BlobContainerClient> CreateContainerAsync(PublicAccessType access = PublicAccessType.None)
    {
        var name = "weft-" + Guid.NewGuid().ToString("N");
        containers.TryAdd(name, 0);
        var container = Service.GetBlobContainerClient(name);
        await container.CreateAsync(access);
        return container;
    }

    internal BlobStorageOptions Options(BlobContainerClient container, int maximumConcurrency = 4) =>
        new(ServiceUri, container.Name, BlobStorageCredentialKind.Emulator, maximumConcurrency: maximumConcurrency);

    internal BlobContainerClient Client(BlobContainerClient container) => BlobPrivateObjectStore.CreateContainerClient(Options(container));

    public async Task DisposeAsync()
    {
        if (serviceUri is not null)
            foreach (var name in containers.Keys)
            {
                try { await Service.GetBlobContainerClient(name).DeleteIfExistsAsync(); }
                catch (RequestFailedException) { }
            }
        if (containerAttempted)
            await DockerAsync(["rm", "--force", "--volumes", Container]);
    }

    private async Task<string> DockerAsync(IReadOnlyList<string> arguments, int timeoutSeconds = 60)
    {
        var start = new ProcessStartInfo(docker)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("A Docker command for the owned emulator failed: " + (await error).Trim());
        return await output;
    }
}
