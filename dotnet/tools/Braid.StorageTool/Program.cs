using Braid.Application;
using Braid.Storage.FileSystem;
using Braid.Storage.Hosting;

var command = args.Length == 0 ? null : args[0];
if (command == "initialize-blob")
    return await InitializeBlobAsync(args);
var activation = command == "activate-v2";
if (activation && (args.Length < 2 || args[1] != "--offline"))
{
    Console.Error.WriteLine("OfflineAcknowledgementRequired");
    return 2;
}
if (activation ? args.Length is < 4 or > 5 :
    command is not ("initialize" or "initialize-v2") || args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("Usage: Braid.StorageTool initialize ROOT APPLICATION_ROOT [REPOSITORY_ROOT]");
    return 2;
}

try
{
    var offset = activation ? 2 : 1;
    var options = new FileSystemStorageOptions(args[offset], args[offset + 1],
        args.Length == offset + 3 ? args[offset + 2] : null);
    if (activation)
    {
        var result = await FileSystemV2PrivateObjectStore.ActivateAsync(options, offlineAcknowledged: true);
        if (result.Outcome == PrivateStorageV2ActivationOutcome.Busy)
        {
            Console.Error.WriteLine("Busy");
            return 1;
        }
        Console.WriteLine(result.Outcome.ToString());
    }
    else if (command == "initialize-v2")
    {
        using var store = FileSystemV2PrivateObjectStore.Initialize(options);
        Console.WriteLine("InitializedV2");
    }
    else
    {
        using var store = FileSystemPrivateObjectStore.Initialize(options);
        Console.WriteLine("Initialized");
    }
    return 0;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("InvalidConfiguration");
    return 2;
}
catch (PrivateObjectStorageException error)
{
    // The closed category never contains submitted paths or provider messages.
    Console.Error.WriteLine(error.Error.ToString());
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine(activation ? "ActivationFailure" : "InitializationFailure");
    return 1;
}

// Provisions a fresh claim-aware store in the explicitly selected, existing private container.
static async Task<int> InitializeBlobAsync(string[] args)
{
    if (args.Length != 1)
    {
        Console.Error.WriteLine("Usage: Braid.StorageTool initialize-blob (with BRAID_PRIVATE_STORAGE_KIND=azure-blob)");
        return 2;
    }
    PrivateStorageConfiguration configuration;
    try
    {
        configuration = PrivateStorageConfiguration.ForBlob(PrivateStorageSettings.ReadBlob(Environment.GetEnvironmentVariable).Blob!);
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine("InvalidConfiguration");
        return 2;
    }
    try
    {
        await configuration.InitializeAsync();
        Console.WriteLine("InitializedBlob");
        return 0;
    }
    catch (PrivateObjectStorageException error)
    {
        // The closed category never contains endpoints, container names or provider messages.
        Console.Error.WriteLine(error.Error.ToString());
        return 1;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("InitializationFailure");
        return 1;
    }
}
