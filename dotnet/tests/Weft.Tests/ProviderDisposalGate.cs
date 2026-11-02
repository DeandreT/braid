namespace Weft.Tests;

/// <summary>Forces provider teardown to yield before it reaches an earlier singleton.</summary>
internal sealed class ProviderDisposalGate : IAsyncDisposable
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Entered => entered.Task;
    internal void Release() => released.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        entered.TrySetResult();
        await released.Task;
    }
}
