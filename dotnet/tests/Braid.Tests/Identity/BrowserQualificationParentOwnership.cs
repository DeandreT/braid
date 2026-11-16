using Braid.BrowserQualification;

namespace Braid.Tests;

// The parent uses this same first-task owner with actual QualificationChild objects.
// Tests supply inert children/delegates; they do not simulate network qualification.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class QualificationProbeLedger<TChild> where TChild : class
{
    private readonly object sync = new();
    private readonly List<Launch> launches = [];
    internal Launch? Current { get; private set; }
    internal int AllocatedGeneration { get; private set; }
    internal int AdmittedGeneration => launches.LastOrDefault(run => run.Admitted)?.Generation ?? 0;
    internal IEnumerable<TChild> Children => launches.Select(run => run.Child).OfType<TChild>();
    internal bool AllClosed => launches.All(run => run.StopConfirmed);
    internal int AdmissionRefusalsTotal => launches.Aggregate(0, (total, run) => checked(total + (run.Report?.AdmissionRefusals ?? 0)));
    internal Launch Allocate()
    {
        lock (sync)
        {
            QualificationWire.Require(Current is null || Current.StopConfirmed);
            var run = new Launch(checked(++AllocatedGeneration)); launches.Add(run); Current = run; return run;
        }
    }
    internal sealed class Launch
    {
        private readonly object sync = new();
        private Task? start;
        private Task<bool>? startObserved;
        private Task<QualificationReply?>? stop;
        internal int Generation { get; }
        internal TChild? Child { get; private set; }
        internal bool Admitted { get; private set; }
        internal bool StartupFailed { get; private set; }
        internal bool StopConfirmed { get; private set; }
        internal QualificationReply? Report { get; private set; }
        internal Launch(int generation) { Generation = generation; }
        internal Task StartAsync(Func<TChild> launch, Func<TChild, Task> ready, Func<int, Task> counts)
        {
            lock (sync)
            {
                QualificationWire.Require(start is null && stop is null);
                start = StartCoreAsync(launch, ready, counts); startObserved = ObserveStartAsync(start); return start;
            }
        }
        private async Task StartCoreAsync(Func<TChild> launch, Func<TChild, Task> ready, Func<int, Task> counts)
        {
            // No await between invoking the original factory and binding its returned
            // child. Generation is allocated BEFORE launch, not after counts success.
            Child = launch(); await ready(Child); await counts(Generation); Admitted = true;
        }
        private async Task<bool> ObserveStartAsync(Task original)
        {
            try { await original; return true; }
            catch { StartupFailed = true; return false; } // categorical, original remains faulted
        }
        internal Task<QualificationReply?> StopAsync(Func<TChild, Task> stopChild, Func<TChild, int, QualificationReply> readReport)
        {
            lock (sync) return stop ??= StopCoreAsync(stopChild, readReport);
        }
        private async Task<QualificationReply?> StopCoreAsync(Func<TChild, Task> stopChild, Func<TChild, int, QualificationReply> readReport)
        {
            if (Child is { } child)
            {
                // stopChild owns the actual process/exit/stdout/stderr settlement.
                // Neither an admitted counter nor a previous report chooses the file.
                await stopChild(child);
                if (startObserved is not null) await startObserved;
                var report = readReport(child, Generation);
                QualificationWire.Require(report.Version == 1 && report.Outcome == "ok" && report.ClosedObservation
                    && report.ProcessGeneration == Generation && report.NetworkActive == 0 && report.ProofPending == 0 && report.AdmissionRefusals >= 0);
                Report = report;
            }
            else
            {
                // No returned child: never fabricate a process/report/admission.
                // A throwing factory alone is NOT positive OS non-start evidence;
                // retain conservatively rather than certifying an unowned process.
                if (startObserved is not null) await startObserved;
                QualificationWire.Require(start is null || start.IsCompleted);
                if (start is not null) return null;
            }
            StopConfirmed = true; return Report; // memoized: read/archive once only
        }
    }
}

internal sealed record QualificationParentStopResult(bool ProxyFailed, bool ProbeFailed, bool KnownSettled,
    bool CounterValidation, QualificationReply? Report);
internal sealed record QualificationParentOutcome(bool PrimaryFailed, bool CounterFailed, bool ControlFailed, bool CleanupFailed)
{
    internal bool Failed => PrimaryFailed || CounterFailed || ControlFailed || CleanupFailed;
    internal string CategoryMessage => "Owned HTTPS browser failed: primary=" + PrimaryFailed + "; counter=" + CounterFailed + "; control=" + ControlFailed + "; cleanup=" + CleanupFailed + ".";
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed partial class QualificationParentStopOwner
{
    private readonly object sync = new();
    private Task<QualificationParentStopResult>? stop;
    private Task? release, releaseOriginal;
    private Task? proxyOriginal;
    private Task<QualificationReply?>? probeOriginal;
    internal bool KnownSettled { get; private set; }
    internal QualificationParentStopResult? Result { get; private set; }
    internal Task<QualificationParentStopResult> StopAsync(Func<Task> closeProxy, Func<bool> proxySettled,
        Func<Task<QualificationReply?>> closeProbe, Func<bool> probesSettled, Action validateCounters)
    { lock (sync) return stop ??= StopCoreAsync(closeProxy, proxySettled, closeProbe, probesSettled, validateCounters); }
    private static Task<T> Start<T>(Func<Task<T>> operation) { try { return operation(); } catch { return Task.FromException<T>(new QualificationFailure()); } }
    private static Task Start(Func<Task> operation) { try { return operation(); } catch { return Task.FromException(new QualificationFailure()); } }
    private static async Task<bool> Observe(Task original) { try { await original; return true; } catch { return false; } }
    private async Task<QualificationParentStopResult> StopCoreAsync(Func<Task> closeProxy, Func<bool> proxySettled,
        Func<Task<QualificationReply?>> closeProbe, Func<bool> probesSettled, Action validateCounters)
    {
        proxyOriginal = Start(closeProxy); Task<bool> proxyObserved = Observe(proxyOriginal);
        probeOriginal = Start(closeProbe); Task<bool> probeObserved = Observe(probeOriginal);
        bool proxyOk = await proxyObserved, probeOk = await probeObserved;
        KnownSettled = proxyOk && probeOk && proxySettled() && probesSettled();
        if (!KnownSettled) return Result = new(!proxyOk, !probeOk, false, false, null);
        // Positive original-settlement evidence is published BEFORE qualification
        // policy. A bad finite budget cannot turn known closure into unknown closure.
        bool counterValid = false;
        try { validateCounters(); counterValid = true; }
        catch (QualificationFailure) { counterValid = false; } // retained independent failure category
        return Result = new(false, false, true, counterValid, await probeOriginal);
    }
    internal Task ReleaseAsync(Func<Task> confirmOuterOwners, Func<Task> releaseDependencies)
    { lock (sync) return release ??= ReleaseCoreAsync(confirmOuterOwners, releaseDependencies); }
    private async Task ReleaseCoreAsync(Func<Task> confirmOuterOwners, Func<Task> releaseDependencies)
    {
        await confirmOuterOwners();
        QualificationWire.Require(KnownSettled && Result is { KnownSettled: true });
        releaseOriginal = Start(releaseDependencies); await releaseOriginal;
    }
    internal QualificationParentOutcome Outcome(bool primaryFailed, bool cleanupFailed)
        => new(primaryFailed, Result is { KnownSettled: true, CounterValidation: false }, ControlFailed, cleanupFailed);
}
