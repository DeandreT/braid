namespace Weft.Tests;

/// <summary>Pure-test monotonic timer source. It never supplies database lease or filesystem evidence.</summary>
internal sealed class MaintenanceServiceTestTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TestTimer> _timers = [];
    private readonly TaskCompletionSource timerScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _ticks;
    internal Task TimerScheduled => timerScheduled.Task;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        timerScheduled.TrySetResult();
        return timer;
    }

    internal void Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        long target;
        lock (_gate) target = checked(_ticks + duration.Ticks);
        while (true)
        {
            (TestTimer Timer, long Version)[] due;
            lock (_gate)
            {
                var next = _timers.Where(timer => !timer.Disposed && timer.Next is not null)
                    .Select(timer => timer.Next!.Value).DefaultIfEmpty(long.MaxValue).Min();
                if (next > target) { _ticks = target; return; }
                _ticks = next;
                due = _timers.Where(timer => !timer.Disposed && timer.Next == next)
                    .Select(timer => (timer, timer.Version)).ToArray();
                foreach (var item in due)
                    item.Timer.Next = item.Timer.Period is { } period ? checked(next + period) : null;
            }
            foreach (var item in due) item.Timer.Fire(item.Version);
        }
    }

    private sealed class TestTimer(MaintenanceServiceTestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed { get; private set; }
        internal long? Next { get; set; }
        internal long? Period { get; private set; }
        internal long Version { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(period));
            lock (owner._gate)
            {
                if (Disposed) return false;
                Version++;
                Next = dueTime == Timeout.InfiniteTimeSpan ? null : checked(owner._ticks + dueTime.Ticks);
                Period = period <= TimeSpan.Zero ? null : period.Ticks;
                return true;
            }
        }

        internal void Fire(long version)
        {
            lock (owner._gate) if (Disposed || Version != version) return;
            callback(state);
        }

        public void Dispose() { lock (owner._gate) { Disposed = true; Version++; Next = null; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
