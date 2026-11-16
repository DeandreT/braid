using Braid.BrowserQualification;

namespace Braid.Tests;

public sealed partial class AuthBrowserHttpsQualificationTests
{
    // Inert delegates to the SAME owners used by the actual parent/control path.
    // No child process, network, DB, NSS or private root is created by these bodies.
    private sealed class InertParentChild { internal bool Settled; }
    private static QualificationReply InertClosedReport(int generation, int refusals = 0)
        => QualificationReply.Ok() with { ClosedObservation = true, ProcessGeneration = generation, AdmissionRefusals = refusals };

    [Theory]
    [InlineData("first-counts")]
    [InlineData("restart-counts")]
    [InlineData("first-cancellation")]
    public async Task LaunchedOriginalKeepsGenerationWhenAdmissionFails(string category)
    {
        var ledger = new QualificationProbeLedger<InertParentChild>(); int reads = 0, stopped = 0;
        if (category == "restart-counts")
        {
            var old = ledger.Allocate(); var oldChild = new InertParentChild();
            await old.StartAsync(() => oldChild, _ => Task.CompletedTask, _ => Task.CompletedTask);
            await old.StopAsync(child => { Assert.Same(oldChild, child); child.Settled = true; stopped++; return Task.CompletedTask; },
                (child, generation) => { Assert.Same(oldChild, child); Assert.Equal(1, generation); reads++; return InertClosedReport(generation); });
        }
        var run = ledger.Allocate(); var actual = new InertParentChild(); int launched = 0, admitted = 0;
        Task startup = run.StartAsync(() => { launched++; return actual; }, _ => Task.CompletedTask,
            generation =>
            {
                admitted++; Assert.Equal(run.Generation, generation); return category == "first-cancellation"
                ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new QualificationFailure());
            });
        await Assert.ThrowsAnyAsync<Exception>(() => startup);
        int expected = category == "restart-counts" ? 2 : 1;
        Task<QualificationReply?> firstStop = run.StopAsync(child => { Assert.Same(actual, child); child.Settled = true; stopped++; return Task.CompletedTask; },
            (child, generation) => { Assert.Same(actual, child); Assert.Equal(expected, generation); reads++; return InertClosedReport(generation, 1); });
        var report = await firstStop;
        Task<QualificationReply?> sameStop = run.StopAsync(_ => throw new QualificationFailure(), (_, _) => throw new QualificationFailure());
        Assert.Same(firstStop, sameStop); Assert.Same(report, await sameStop);
        Assert.True(launched == 1 && admitted == 1 && run.StartupFailed && !run.Admitted && run.StopConfirmed && actual.Settled);
        Assert.True(report!.ProcessGeneration == expected && ledger.AllocatedGeneration == expected && ledger.AdmittedGeneration == expected - 1
            && ledger.AdmissionRefusalsTotal == 1 && reads == expected && stopped == expected && ledger.AllClosed);
    }

    [Fact]
    public async Task FailedOriginalLaunchHasNoProcessOrFabricatedReport()
    {
        var ledger = new QualificationProbeLedger<InertParentChild>(); var run = ledger.Allocate(); int stopped = 0, reads = 0;
        Task start = run.StartAsync(() => throw new QualificationFailure(), _ => Task.CompletedTask, _ => Task.CompletedTask);
        await Assert.ThrowsAsync<QualificationFailure>(() => start);
        var report = await run.StopAsync(_ => { stopped++; return Task.CompletedTask; }, (_, generation) => { reads++; return InertClosedReport(generation); });
        Assert.True(report is null && run.Child is null && !run.StopConfirmed && run.StartupFailed && !run.Admitted
            && !ledger.AllClosed && ledger.AllocatedGeneration == 1 && ledger.AdmittedGeneration == 0 && !ledger.Children.Any() && stopped == 0 && reads == 0);
    }

    [Fact]
    public async Task WrongGenerationClosedReportCannotBeArchivedOrRetried()
    {
        var ledger = new QualificationProbeLedger<InertParentChild>(); var run = ledger.Allocate(); int reads = 0;
        var child = new InertParentChild(); await run.StartAsync(() => child, _ => Task.CompletedTask, _ => Task.CompletedTask);
        Task<QualificationReply?> original = run.StopAsync(value => { value.Settled = true; return Task.CompletedTask; },
            (_, _) => { reads++; return InertClosedReport(0); });
        await Assert.ThrowsAsync<QualificationFailure>(() => original);
        Assert.Same(original, run.StopAsync(_ => Task.CompletedTask, (_, _) => InertClosedReport(1)));
        Assert.True(reads == 1 && !run.StopConfirmed && run.Report is null && ledger.AdmissionRefusalsTotal == 0 && !ledger.AllClosed);
    }

    [Fact]
    public async Task KnownClosedBadBudgetReleasesOnceButNeverPasses()
    {
        var owner = new QualificationParentStopOwner(); var report = InertClosedReport(1, 1); int proxy = 0, probe = 0, released = 0;
        var original = owner.StopAsync(() => { proxy++; return Task.CompletedTask; }, () => true,
            () => { probe++; return Task.FromResult<QualificationReply?>(report); }, () => true,
            () => { Assert.True(owner.KnownSettled); QualificationBackstopPolicy.Validate(null, 1, 0); });
        var result = await original;
        Assert.Same(original, owner.StopAsync(() => throw new QualificationFailure(), () => false, () => throw new QualificationFailure(), () => false, () => { }));
        Task release = owner.ReleaseAsync(() => Task.CompletedTask, () => { released++; return Task.CompletedTask; }); await release;
        Assert.Same(release, owner.ReleaseAsync(() => throw new QualificationFailure(), () => throw new QualificationFailure()));
        var outcome = owner.Outcome(false, false);
        Assert.True(result.KnownSettled && !result.CounterValidation && ReferenceEquals(report, result.Report) && report.AdmissionRefusals == 1
            && outcome.CounterFailed && outcome.Failed && proxy == 1 && probe == 1 && released == 1);
    }

    [Fact]
    public async Task UnconfirmedOriginalStopRetainsDependentOwners()
    {
        var owner = new QualificationParentStopOwner(); int proxy = 0, probe = 0, validated = 0, released = 0;
        var result = await owner.StopAsync(() => { proxy++; return Task.FromException(new QualificationFailure()); }, () => false,
            () => { probe++; return Task.FromResult<QualificationReply?>(InertClosedReport(1)); }, () => true, () => { validated++; });
        await Assert.ThrowsAsync<QualificationFailure>(() => owner.ReleaseAsync(() => Task.CompletedTask, () => { released++; return Task.CompletedTask; }));
        Assert.True(!result.KnownSettled && result.ProxyFailed && !result.CounterValidation && !owner.KnownSettled
            && proxy == 1 && probe == 1 && validated == 0 && released == 0);
    }

    [Fact]
    public async Task PrimaryCounterAndSecondaryCleanupFailuresRemainSeparate()
    {
        var owner = new QualificationParentStopOwner(); int releases = 0;
        await owner.StopAsync(() => Task.CompletedTask, () => true, () => Task.FromResult<QualificationReply?>(InertClosedReport(1, 1)), () => true,
            () => QualificationBackstopPolicy.Validate(null, 1, 0));
        Task original = owner.ReleaseAsync(() => Task.CompletedTask, () => { releases++; return Task.FromException(new QualificationFailure()); });
        await Assert.ThrowsAsync<QualificationFailure>(() => original);
        Assert.Same(original, owner.ReleaseAsync(() => Task.CompletedTask, () => { releases++; return Task.CompletedTask; }));
        var outcome = owner.Outcome(true, true);
        Assert.True(outcome.PrimaryFailed && outcome.CounterFailed && outcome.CleanupFailed && outcome.Failed && releases == 1);
    }

    [Fact]
    public async Task FailedFinalReceiptDoesNotUndoPositiveClosureEvidence()
    {
        var owner = new QualificationParentStopOwner(); int released = 0; bool receiptFailed = false;
        var result = await owner.StopAsync(() => Task.CompletedTask, () => true, () => Task.FromResult<QualificationReply?>(InertClosedReport(1, 1)), () => true,
            () => QualificationBackstopPolicy.Validate(null, 1, 0));
        try { QualificationWire.Require(result.CounterValidation); } catch (QualificationFailure) { receiptFailed = true; }
        await owner.ReleaseAsync(() => { QualificationWire.Require(receiptFailed && owner.KnownSettled); return Task.CompletedTask; },
            () => { released++; return Task.CompletedTask; });
        var outcome = owner.Outcome(receiptFailed, false);
        Assert.True(released == 1 && outcome.PrimaryFailed && outcome.CounterFailed && !outcome.CleanupFailed && outcome.Failed);
    }
}
