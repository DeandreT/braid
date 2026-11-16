namespace Braid.Application;

public sealed partial class PrivateObjectMaintenanceService
{
    private sealed class RunDeadline : IDisposable
    {
        private static readonly TimeSpan WorkLimit = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan SettlementLimit = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CallerGrace = TimeSpan.FromSeconds(5);
        private readonly TimeProvider _time;
        private readonly long _started;
        private readonly CancellationToken _caller;
        private readonly CancellationTokenSource _workDeadline;
        private readonly CancellationTokenSource _settlement;
        private readonly CancellationTokenSource _work;
        private readonly ITimer _callerGrace;
        private readonly CancellationTokenRegistration _callerRegistration;
        private long _callerStoppedAt;
        private int _callerStopped;

        internal RunDeadline(TimeProvider time, CancellationToken caller)
        {
            _time = time;
            _caller = caller;
            _started = time.GetTimestamp();
            _workDeadline = new(WorkLimit, time);
            _settlement = new(SettlementLimit, time);
            _work = CancellationTokenSource.CreateLinkedTokenSource(caller, _workDeadline.Token);
            _callerGrace = time.CreateTimer(static state => Cancel((CancellationTokenSource)state!), _settlement,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _callerRegistration = caller.UnsafeRegister(static state => ((RunDeadline)state!).CallerStopped(), this);
        }

        internal CancellationToken WorkToken => _work.Token;
        internal CancellationToken SettlementToken => _settlement.Token;
        internal bool CallerCanceled => _caller.IsCancellationRequested;
        internal bool WorkExpired => _time.GetElapsedTime(_started) >= WorkLimit || _workDeadline.IsCancellationRequested;
        internal bool Stopped => CallerCanceled || WorkExpired;
        internal bool CanStart => !Stopped && !_work.IsCancellationRequested;

        internal bool CanRecover
        {
            get
            {
                if (_time.GetElapsedTime(_started) >= SettlementLimit ||
                    (Volatile.Read(ref _callerStopped) == 1 && _time.GetElapsedTime(Volatile.Read(ref _callerStoppedAt)) >= CallerGrace))
                    Cancel(_settlement);
                return !_settlement.IsCancellationRequested;
            }
        }

        private void CallerStopped()
        {
            Volatile.Write(ref _callerStoppedAt, _time.GetTimestamp());
            if (Interlocked.Exchange(ref _callerStopped, 1) == 0)
                _callerGrace.Change(CallerGrace, Timeout.InfiniteTimeSpan);
        }

        private static void Cancel(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _callerRegistration.Dispose();
            _callerGrace.Dispose();
            _work.Dispose();
            _workDeadline.Dispose();
            _settlement.Dispose();
        }
    }
}
