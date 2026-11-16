using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Braid.Storage.Blob;

namespace Braid.Tests;

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Owned Azurite blob emulator";
}

public sealed class AzuriteEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1")]
    public void Accepts_only_explicit_test_loopback(string endpoint) =>
        Assert.Equal("127.0.0.1", AzuriteFixture.ValidateLoopbackEndpoint(endpoint).Host);

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:10000/devstoreaccount1")]
    [InlineData("http://127.1:10000/devstoreaccount1")]
    [InlineData("http://[::1]:10000/devstoreaccount1")]
    [InlineData("https://127.0.0.1:10000/devstoreaccount1")]
    [InlineData("http://user@127.0.0.1:10000/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1?")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1#")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1/")]
    [InlineData("http://127.0.0.1:10000/a/../devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/%64evstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1\n")]
    [InlineData("http://127.0.0.1:0/devstoreaccount1")]
    [InlineData("http://127.0.0.1:65536/devstoreaccount1")]
    public void Rejects_other_hosts_paths_or_url_components(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => AzuriteFixture.ValidateLoopbackEndpoint(endpoint));

    [Fact]
    public void Cleanup_accepts_exact_container_owner() =>
        AzuriteFixture.RequireContainerOwnership(new Dictionary<string, string>
        {
            [AzuriteFixture.ContainerOwnerMetadata] = "owned-by-this-fixture"
        }, "owned-by-this-fixture");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("another-fixture")]
    [InlineData("OWNED-BY-THIS-FIXTURE")]
    public void Cleanup_rejects_missing_or_different_container_owner(string? recordedOwner)
    {
        var metadata = new Dictionary<string, string>();
        if (recordedOwner is not null) metadata[AzuriteFixture.ContainerOwnerMetadata] = recordedOwner;
        Assert.Throws<InvalidOperationException>(() =>
            AzuriteFixture.RequireContainerOwnership(metadata, "owned-by-this-fixture"));
    }
}

public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!AzuriteFixture.IsConfigured)
            Skip = "Set BRAID_AZURITE_IMAGE or BRAID_AZURITE_LOOPBACK_ENDPOINT to execute actual blob emulator gates.";
    }
}

public sealed class AzuriteTheoryAttribute : TheoryAttribute
{
    public AzuriteTheoryAttribute()
    {
        if (!AzuriteFixture.IsConfigured)
            Skip = "Set BRAID_AZURITE_IMAGE or BRAID_AZURITE_LOOPBACK_ENDPOINT to execute actual blob emulator gates.";
    }
}

/// <summary>
/// Test-only loopback Azurite: an owned immutable Docker image, or an explicitly supplied external
/// native emulator. Removes only unique blob containers with verified ownership metadata; never stops the external process.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string QualifiedImage = "mcr.microsoft.com/azure-storage/azurite@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5";
    internal const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string OwnerLabel = "braid.azurite-test-owner";
    internal const string ContainerOwnerMetadata = "braid_test_owner";
    internal static bool IsConfigured => Environment.GetEnvironmentVariable("BRAID_AZURITE_IMAGE") is not null ||
        Environment.GetEnvironmentVariable("BRAID_AZURITE_LOOPBACK_ENDPOINT") is not null;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly string docker = Environment.GetEnvironmentVariable("BRAID_DOCKER_EXECUTABLE") ?? "docker";
    private readonly ConcurrentDictionary<string, byte> containers = new(StringComparer.Ordinal);
    private bool containerAttempted;
    private Uri? serviceUri;

    private string Container => $"braid-azurite-{owner}";

    internal Uri ServiceUri => serviceUri ?? throw new InvalidOperationException("The emulator is not running.");

    internal BlobServiceClient Service => new(ServiceUri, new StorageSharedKeyCredential("devstoreaccount1", EmulatorKey));

    public async Task InitializeAsync()
    {
        if (!IsConfigured) return;
        var endpoint = Environment.GetEnvironmentVariable("BRAID_AZURITE_LOOPBACK_ENDPOINT");
        if (endpoint is not null)
        {
            if (Environment.GetEnvironmentVariable("BRAID_AZURITE_IMAGE") is not null)
                throw new InvalidOperationException("External and Docker Azurite fixture modes cannot be combined.");
            serviceUri = ValidateLoopbackEndpoint(endpoint);
            await Service.GetPropertiesAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("BRAID_AZURITE_IMAGE") != QualifiedImage)
            throw new InvalidOperationException("BRAID_AZURITE_IMAGE must exactly equal the qualified immutable emulator image.");
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

    internal static Uri ValidateLoopbackEndpoint(string endpoint)
    {
        // Check the original spelling too: Uri normalization must not admit aliases, escapes or dot segments.
        if (!System.Text.RegularExpressions.Regex.IsMatch(endpoint,
                @"\Ahttp://127\.0\.0\.1(?::[0-9]{1,5})?/devstoreaccount1\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            throw new InvalidOperationException("BRAID_AZURITE_LOOPBACK_ENDPOINT must be http://127.0.0.1[:port]/devstoreaccount1 without credentials, query or fragment.");
        return uri;
    }

    /// <summary>A new private container owned by this fixture; Initialize never creates one itself.</summary>
    internal async Task<BlobContainerClient> CreateContainerAsync(PublicAccessType access = PublicAccessType.None)
    {
        var name = "braid-" + Guid.NewGuid().ToString("N");
        var container = Service.GetBlobContainerClient(name);
        // Track before sending: creation can succeed remotely even when its acknowledgement is lost.
        containers.TryAdd(name, 0);
        await container.CreateAsync(access, metadata: new Dictionary<string, string> { [ContainerOwnerMetadata] = owner });
        return container;
    }

    internal static void RequireContainerOwnership(IDictionary<string, string> metadata, string expectedOwner)
    {
        if (!metadata.TryGetValue(ContainerOwnerMetadata, out var recordedOwner) || recordedOwner != expectedOwner)
            throw new InvalidOperationException("Refusing removal of a blob container without this fixture's exact ownership metadata.");
    }

    internal BlobStorageOptions Options(BlobContainerClient container, int maximumConcurrency = 4) =>
        new(ServiceUri, container.Name, BlobStorageCredentialKind.Emulator, maximumConcurrency: maximumConcurrency);

    internal BlobContainerClient Client(BlobContainerClient container) => BlobPrivateObjectStore.CreateContainerClient(Options(container));

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        if (serviceUri is not null)
            foreach (var name in containers.Keys)
            {
                try
                {
                    var container = Service.GetBlobContainerClient(name);
                    var properties = (await container.GetPropertiesAsync()).Value;
                    RequireContainerOwnership(properties.Metadata, owner);
                    await container.DeleteIfExistsAsync();
                    containers.TryRemove(name, out _);
                }
                catch (RequestFailedException error) when (error.Status == 404) { containers.TryRemove(name, out _); }
                catch (Exception error) { failures.Add(error); }
            }
        if (containerAttempted)
        {
            try { await DockerAsync(["rm", "--force", "--volumes", Container]); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0)
            throw new AggregateException("Cleanup of owned Azurite test resources failed.", failures);
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
