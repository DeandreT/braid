using Weft.Application;
using Weft.Storage.FileSystem;

if (args.Length is < 3 or > 4 || args[0] != "initialize")
{
    Console.Error.WriteLine("Usage: Weft.StorageTool initialize ROOT APPLICATION_ROOT [REPOSITORY_ROOT]");
    return 2;
}

try
{
    var options = new FileSystemStorageOptions(args[1], args[2], args.Length == 4 ? args[3] : null);
    using var store = FileSystemPrivateObjectStore.Initialize(options);
    Console.WriteLine("Initialized");
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
    Console.Error.WriteLine("InitializationFailure");
    return 1;
}
