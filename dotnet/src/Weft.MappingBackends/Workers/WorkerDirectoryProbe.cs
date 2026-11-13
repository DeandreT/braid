namespace Weft.MappingBackends.Workers;

/// <summary>A startup filesystem probe whose unconfirmed cleanup survives failure before runner registration.</summary>
internal static class WorkerDirectoryProbe
{
    internal sealed class CleanupFailure : Exception, IAsyncDisposable
    {
        internal CleanupFailure(Exception cause) : base("Worker startup directory cleanup was not confirmed.", cause) { }
        // Terminal evidence, like a runner's memoized shutdown fault. Never turn a failed join into a clean stop.
        public ValueTask DisposeAsync() => ValueTask.FromException(this);
    }

    internal static void Run(string root, string prefix, Action<string>? delete = null)
    {
        var path = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        delete ??= Directory.Delete;
        // Establish cleanup responsibility before creation, which itself may fail after making the directory.
        try
        {
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(path);
        }
        catch
        {
            try { delete(path); }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) { throw new CleanupFailure(error); }
            throw;
        }
        try { delete(path); }
        catch (Exception error) { throw new CleanupFailure(error); }
    }
}
