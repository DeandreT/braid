using Braid.Deployment.Sandbox;

namespace Braid.Tests;

public sealed class FileSandboxHistoryConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "braid-sandbox-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("completed", "inProgress")]
    [InlineData("completed", "conflict")]
    [InlineData("completed", "failed")]
    [InlineData("failed", "completed")]
    [InlineData("expired", "unavailable")]
    public async Task A_late_duplicate_cannot_replace_the_terminal_receipt(string terminalStatus, string lateStatus)
    {
        var history = new FileSandboxHistory(root);
        var original = Message(terminalStatus);
        var late = original with
        {
            Status = lateStatus,
            SentAt = original.SentAt.AddSeconds(1),
            Inputs = [new(null, "application/json", "{\"different\":true}"u8.ToArray())],
            Outputs = []
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicate = Task.Run(async () =>
        {
            ready.SetResult();
            await release.Task;
            await history.DeliverAsync(late);
        });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await history.DeliverAsync(original);
        }
        finally { release.TrySetResult(); }
        await duplicate.WaitAsync(TimeSpan.FromSeconds(10));

        var reopened = new FileSandboxHistory(root);
        var stored = Assert.IsType<SandboxMessage>(await reopened.ReadAsync(original.TargetId, original.MessageId));
        Assert.Equal(original.Status, stored.Status);
        Assert.Equal(original.SentAt, stored.SentAt);
        Assert.Equal(original.Inputs.Single().Content, stored.Inputs.Single().Content);
        Assert.Equal(original.Outputs.Single().Content, stored.Outputs.Single().Content);
        Assert.Equal(original.BuildId, stored.BuildId);
        Assert.Equal(original.ReleaseId, stored.ReleaseId);
        Assert.Single(await reopened.ListAsync(original.TargetId, 10));
        Assert.Single(Directory.GetFiles(Path.Combine(root, original.TargetId.ToString("D"))));
    }

    [Fact]
    public async Task Retryable_history_can_finish_and_terminal_replays_do_not_extend_retention()
    {
        var history = new FileSandboxHistory(root);
        var original = Message("completed");
        await history.DeliverAsync(original with { Status = "unavailable", Outputs = [] });
        await history.DeliverAsync(original);
        await new FileSandboxHistory(root).DeliverAsync(original with { SentAt = original.SentAt.AddDays(1), Replayed = true });
        var stored = Assert.IsType<SandboxMessage>(await history.ReadAsync(original.TargetId, original.MessageId));
        Assert.Equal("completed", stored.Status);
        Assert.Equal(original.SentAt, stored.SentAt);
        Assert.False(stored.Replayed);
        Assert.Equal(original.Outputs.Single().Content, stored.Outputs.Single().Content);
    }

    private static SandboxMessage Message(string status) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UnixEpoch.AddYears(56), 10, status, null, false, Guid.NewGuid(), Guid.NewGuid(), "inline", null,
        [new(null, "application/json", "{\"input\":1}"u8.ToArray())], [], [new(null, "application/json", "{\"output\":2}"u8.ToArray())]);
}
