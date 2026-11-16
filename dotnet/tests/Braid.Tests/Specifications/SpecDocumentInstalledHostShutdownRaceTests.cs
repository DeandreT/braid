using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Workers;
using Braid.Server;
using Xunit.Abstractions;

namespace Braid.Tests;

// Separate source-only review unit. Do not promote until independently reviewed.
// Uses the reviewed hostile file's installation/census helpers, not its controlled host/held-owner composition.
[Collection("Installed document qualification")]
[SupportedOSPlatform("linux")]
public sealed class SpecDocumentInstalledHostShutdownRaceTests(ITestOutputHelper output)
{
    private const int MaximumAttempts = 8;
    private static readonly TimeSpan WitnessWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(20);

    [SpecDocumentInstalledHostileTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Default_host_shutdown_overlaps_a_real_pending_qualified_extraction(bool office, bool dispose)
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var text = new string('x', 7 * 1024 * 1024); // Valid XML/PDF literal text; no escaping-dependent xref lengths.
        var source = InstalledDocumentHostileSupport.Source(office ? SpecSourceFormat.Docx : SpecSourceFormat.Pdf,
            office ? SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word($"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"),
                CompressionLevel.NoCompression) : SpecPdfProcessTests.Pdf([text]));
        Assert.InRange(source.ByteCount, 7 * 1024 * 1024, SpecPdfWorkerResponse.MaximumSourceBytes - 1);
        Assert.True(source.ByteCount < SpecCapturedSource.MaximumBytes);
        // The valid DOCX may exceed the default 4MiB entry budget; the valid PDF may exceed native text limits.
        // Those are legitimate completed LimitExceeded results, not malformed-source evidence or interrupted overlap.
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (await AttemptAsync(settings, processes, source, office, dispose, attempt, deadline.Token)) return;
        }
        Assert.Fail($"Inconclusive: no witnessed interrupted qualified {(office ? "Office" : "PDF")} call across {MaximumAttempts} fully settled default-host {(dispose ? "Dispose" : "Stop")} attempts.");
    }

    private async Task<bool> AttemptAsync(SpecDocumentHostSettings settings, InstalledDocumentProcessCensus processes,
        SpecCapturedSource source, bool office, bool dispose, int attempt, CancellationToken deadline)
    {
        var host = new SpecDocumentHostResources(settings); // DEFAULT production factory only, every attempt.
        Task<SpecOfficeExtractionResult>? officeCall = null;
        Task<SpecPdfExtractionResult>? pdfCall = null;
        Task? extraction = null, shutdown = null;
        Task<Terminal>? observed = null;
        ChildWitness? witness = null;
        var pendingAtShutdown = false;
        var shutdownInvoked = false;
        var interrupted = false;
        var failures = new List<Exception>();
        void Retain(Exception error) { if (!failures.Any(old => ReferenceEquals(old, error))) failures.Add(error); }
        try
        {
            await host.StartAsync(deadline);
            Assert.Equal("admitted", host.Status);
            var admission = host.Bind(_ => Task.FromResult(true)); // Database evidence is independently qualified.
            var retainedOffice = Assert.IsType<QualifiedOfficeProcessWorker>(admission.Office);
            var retainedPdf = Assert.IsType<QualifiedPdfProcessWorker>(admission.Pdf);
            Assert.Equal(settings.OfficeRuntime, retainedOffice.RuntimeIdentity);
            Assert.Equal(settings.PdfProfile.Digest, retainedPdf.ProfileDigest);
            Assert.Equal(6, (await admission.FormatsAsync(deadline)).Count);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);

            // No caller-cancellation stimulus, extra runner, EOF option or injected factory. Only host shutdown intervenes.
            if (office) extraction = officeCall = retainedOffice.ExtractAsync(source, settings.OfficeLimits, CancellationToken.None);
            else extraction = pdfCall = retainedPdf.ExtractAsync(source, CancellationToken.None);
            observed = ObserveAsync();
            witness = await WitnessAsync(extraction, office, deadline);
            // No await/sleep between this last pending sample and actual Stop/Dispose dispatch. Completion can still win.
            Volatile.Write(ref pendingAtShutdown, !extraction.IsCompleted);
            Volatile.Write(ref shutdownInvoked, true);
            shutdown = dispose ? host.DisposeAsync().AsTask() : host.StopAsync(CancellationToken.None);
            await shutdown.WaitAsync(deadline);
            var terminal = await observed.WaitAsync(deadline);
            Assert.True(extraction.IsCompleted); // Join the qualified continuation too, not only the host's native owners.
            interrupted = witness is not null && pendingAtShutdown && (terminal.Kind is "unavailable" or "canceled");
            output.WriteLine($"{(office ? "Office" : "PDF")}/{(dispose ? "Dispose" : "Stop")} attempt {attempt}: witness={witness?.ToString() ?? "none"}; pending-at-shutdown={pendingAtShutdown}; terminal={terminal.Kind}/{terminal.Outcome}/{terminal.Code}; witnessed-interrupted-overlap={interrupted}");

            Assert.Equal("stopped", host.Status);
            Assert.False(retainedOffice.Available); Assert.False(retainedPdf.Available);
            Assert.Equal(3, (await admission.FormatsAsync(deadline)).Count);
            await Assert.ThrowsAsync<SpecOfficeWorkerUnavailableException>(() => retainedOffice.ExtractAsync(
                InstalledDocumentHostileSupport.ReadableOffice(), settings.OfficeLimits, CancellationToken.None));
            await Assert.ThrowsAsync<SpecPdfWorkerUnavailableException>(() => retainedPdf.ExtractAsync(
                InstalledDocumentHostileSupport.ReadablePdf(), CancellationToken.None));
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
        catch (Exception error) when (Ordinary(error)) { Retain(error); }
        finally
        {
            // Every attempt (including a successful extraction that won the race) fully settles before another host opens.
            // Unknown errors and cleanup faults are retained/propagated, not turned into exploratory retries.
            using var teardown = new CancellationTokenSource(TeardownBound); // One aggregate diagnostic teardown bound.
            try
            {
                if (shutdown is null)
                {
                    Volatile.Write(ref pendingAtShutdown, extraction is not null && !extraction.IsCompleted);
                    Volatile.Write(ref shutdownInvoked, true);
                    shutdown = host.StopAsync(CancellationToken.None);
                }
                await shutdown.WaitAsync(teardown.Token);
            }
            catch (Exception error) when (Ordinary(error)) { Retain(error); }
            try
            {
                if (observed is not null) await observed.WaitAsync(teardown.Token);
                else if (extraction is not null) await extraction.WaitAsync(teardown.Token);
            }
            catch (Exception error) when (Ordinary(error)) { Retain(error); }
            try { await host.DisposeAsync().AsTask().WaitAsync(teardown.Token); }
            catch (Exception error) when (Ordinary(error)) { Retain(error); }
            try { InstalledDocumentHostileSupport.AssertSettled(settings, processes); }
            catch (Exception error) when (Ordinary(error)) { Retain(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Qualified extraction/default-host race or owned settlement failed.", failures);
        return interrupted;

        async Task<Terminal> ObserveAsync()
        {
            try
            {
                if (office)
                {
                    var result = await officeCall!;
                    AssertIdentity(result.Identity, result.CopyEvidenceBytes(), settings.OfficeLimits.Digest);
                    Assert.True(result.Outcome is SpecExtractionOutcome.Partial or SpecExtractionOutcome.LimitExceeded);
                    if (result.Outcome == SpecExtractionOutcome.LimitExceeded) Assert.Empty(result.Chunks);
                    return new("completed", result.Outcome, result.Code);
                }
                else
                {
                    var result = await pdfCall!;
                    AssertIdentity(result.Identity, result.CopyEvidenceBytes(), settings.PdfProfile.Digest);
                    Assert.True(result.Outcome is SpecExtractionOutcome.Partial or SpecExtractionOutcome.LimitExceeded);
                    if (result.Outcome == SpecExtractionOutcome.LimitExceeded) Assert.Empty(result.Chunks);
                    return new("completed", result.Outcome, result.Code);
                }
            }
            catch (SpecOfficeWorkerUnavailableException) when (office && ShutdownPending())
            { return new("unavailable", null, null); }
            catch (SpecPdfWorkerUnavailableException) when (!office && ShutdownPending())
            { return new("unavailable", null, null); }
            catch (OperationCanceledException) when (ShutdownPending())
            { return new("canceled", null, null); }
            // No broad exception catch: protocol/assertion/cleanup faults and pre-shutdown failures propagate.
        }

        bool ShutdownPending() => Volatile.Read(ref shutdownInvoked) && Volatile.Read(ref pendingAtShutdown) && !deadline.IsCancellationRequested;

        void AssertIdentity(SpecExtractionIdentity identity, byte[] evidence, Sha256Digest profile)
        {
            Assert.Equal(source.Identity, identity.Source);
            Assert.Equal(source.Digest, identity.SourceDigest);
            Assert.Equal(source.Format, identity.Format);
            Assert.Equal(profile, identity.ProfileDigest);
            Assert.Equal(Sha256Digest.FromBytes(evidence), identity.EvidenceDigest);
        }
    }

    private sealed record Terminal(string Kind, SpecExtractionOutcome? Outcome, string? Code);
    private sealed record ChildIdentity(int Pid, ulong StartTicks);
    private sealed record ChildWitness(ChildIdentity Identity, long ObservedAt)
    {
        public override string ToString() => $"{Identity.Pid}@{Identity.StartTicks},observed={ObservedAt}";
    }

    private static async Task<ChildWitness?> WitnessAsync(Task extraction, bool office, CancellationToken deadline)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        poll.CancelAfter(WitnessWindow);
        var expectedFilters = ChildSandboxVerification.InheritedFilters() + (office ? ProcessCompileOptions.LauncherFilters : 3);
        var limits = FixedLimits(office);
        try
        {
            while (!extraction.IsCompleted)
            {
                poll.Token.ThrowIfCancellationRequested();
                foreach (var pid in DirectChildren())
                {
                    var first = LiveIdentity(pid);
                    if (first is null) continue;
                    // Read-only full fixed-profile procfs checks, not a replacement for the real runner's own admission.
                    if (ChildSandboxVerification.Verify(pid, expectedFilters, limits, requireUnprivilegedIdentity: true) != ChildAdmissionFailure.None) continue;
                    if (first == LiveIdentity(pid) && DirectChildren().Contains(pid) && !extraction.IsCompleted)
                        return new(first, Stopwatch.GetTimestamp());
                }
                await Task.Delay(1, poll.Token); // Requested interval, not a hard real-time scheduling claim.
            }
            return null;
        }
        catch (OperationCanceledException) when (poll.IsCancellationRequested && !deadline.IsCancellationRequested)
        { return null; } // Only the bounded observation window expired; this attempt still has to settle.
    }

    private static IReadOnlyList<(string Name, long Value)> FixedLimits(bool office)
    {
        var profile = office ? ProcessCompiledRuntimeOptions.Profile : DockerWorkerProfile.Pdf;
        var values = new List<(string, long)>
        {
            ("Max cpu time", profile.CpuSeconds), ("Max file size", office ? ProcessCompiledRuntimeOptions.FileBytes : 0),
            ("Max core file size", 0), ("Max open files", profile.FileLimit),
            ("Max address space", office ? ProcessCompiledRuntimeOptions.AddressSpaceBytes : profile.MemoryBytes),
            ("Max locked memory", 0), ("Max file locks", 0), ("Max msgqueue size", 0), ("Max realtime priority", 0)
        };
        if (!office) values.Add(("Max processes", 1));
        return values;
    }

    private static ChildIdentity? LiveIdentity(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')'); Assert.True(close > 0);
            var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(fields.Length > 19);
            if (fields[0] == "Z" || int.Parse(fields[1], CultureInfo.InvariantCulture) != Environment.ProcessId) return null;
            Assert.Equal(pid, int.Parse(stat.AsSpan(0, stat.IndexOf(' ')), CultureInfo.InvariantCulture));
            return new(pid, ulong.Parse(fields[19], CultureInfo.InvariantCulture));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static HashSet<int> DirectChildren()
    {
        var ids = new HashSet<int>();
        foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(task, "children")); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var value in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                ids.Add(int.Parse(value, CultureInfo.InvariantCulture));
        }
        return ids;
    }

    private static bool Ordinary(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
