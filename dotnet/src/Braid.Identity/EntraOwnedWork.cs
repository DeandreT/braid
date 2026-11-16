namespace Braid.Identity;

// Counts whole owned operations, not just the caller's cancellable wait. Closing prevents
// even a framework refresh queued before shutdown from entering owned HTTP afterwards.
internal sealed class EntraOwnedWork
{
    private readonly object sync = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closed;
    private int count;

    internal IDisposable Enter()
    {
        lock (sync)
        {
            if (closed)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            count++;
            return new Lease(this);
        }
    }

    internal Task CloseAndDrain()
    {
        lock (sync)
        {
            closed = true;
            if (count == 0)
                drained.TrySetResult();
            return drained.Task;
        }
    }

    private void Exit()
    {
        lock (sync)
        {
            count--;
            if (closed && count == 0)
                drained.TrySetResult();
        }
    }

    private sealed class Lease(EntraOwnedWork owner) : IDisposable
    {
        private EntraOwnedWork? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Exit();
    }
}
