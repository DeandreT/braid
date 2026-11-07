namespace Weft.Tests;

public sealed class MaintenanceServiceTestTimeProviderTests
{
    [Fact]
    public async Task Scheduled_barrier_follows_registration_so_one_advance_completes_the_wait()
    {
        var clock = new MaintenanceServiceTestTimeProvider();
        Assert.False(clock.TimerScheduled.IsCompleted);
        var waiting = Task.Delay(TimeSpan.FromMinutes(1), clock);
        await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_advance_before_registration_cannot_complete_a_later_timer()
    {
        var clock = new MaintenanceServiceTestTimeProvider();
        clock.Advance(TimeSpan.FromMinutes(1));
        var waiting = Task.Delay(TimeSpan.FromMinutes(1), clock);
        await clock.TimerScheduled.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
