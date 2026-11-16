using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;
using Braid.MappingBackends.Workers;
using Braid.Server;
using Xunit.Abstractions;

namespace Braid.Tests;

// STAGED SOURCE ONLY. Add to Braid.Tests only after the frozen installed-image receipt.
// These cases share the existing nonparallel collection and unchanged installation admission.
public sealed class SpecDocumentInstalledHostileTheoryAttribute : TheoryAttribute
{
    public SpecDocumentInstalledHostileTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_HOST_LINUX") != "1")
            Skip = "Requires Linux and explicit opt-in to an independently pinned, protected document installation.";
    }
}

[Collection("Installed document qualification")]
[SupportedOSPlatform("linux")]
public sealed class SpecDocumentInstalledHostileTests(ITestOutputHelper output)
{
    [SpecDocumentInstalledHostileTheory]
    [InlineData(SpecSourceFormat.Docx)]
    [InlineData(SpecSourceFormat.Xlsx)]
    public async Task Installed_office_refuses_representative_hostile_packages_and_extraction_budgets(SpecSourceFormat format)
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var host = new SpecDocumentHostResources(settings);
        try
        {
            await host.StartAsync(deadline.Token);
            Assert.Equal("admitted", host.Status);
            var admission = host.Bind(_ => Task.FromResult(true)); // Database admission has its separate actual-PG fact.
            var worker = Assert.IsType<QualifiedOfficeProcessWorker>(admission.Office);
            Assert.Equal(settings.OfficeRuntime, worker.RuntimeIdentity);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);

            foreach (var fixture in OfficeCases(format))
            {
                var source = InstalledDocumentHostileSupport.Source(format, fixture.Bytes);
                // Fixture/reference agreement is checked separately; it never replaces the installed child invocation.
                var reference = SpecOfficeParser.Extract(source, fixture.Limits);
                Assert.Equal(fixture.Outcome, reference.Outcome);
                Assert.Equal(fixture.Code, reference.Code);
                Assert.Empty(reference.Chunks); // Empty Word paragraphs and blank spreadsheet cells still consume chunk budget.
                var result = await worker.ExtractAsync(source, fixture.Limits, deadline.Token);
                output.WriteLine($"{format}/{fixture.Name}: {result.Outcome}/{result.Code}");
                Assert.Equal(fixture.Outcome, result.Outcome);
                Assert.Equal(fixture.Code, result.Code);
                Assert.Empty(result.Chunks);
                Assert.Equal(source.Identity, result.Identity.Source);
                Assert.Equal(source.Digest, result.Identity.SourceDigest);
                Assert.Equal(fixture.Limits.Digest, result.Identity.ProfileDigest);
                Assert.Equal(Sha256Digest.FromBytes(result.CopyEvidenceBytes()), result.Identity.EvidenceDigest);
                Assert.True(result.CopyEvidenceBytes().Length <= fixture.Limits.MaximumEvidenceBytes);
                using var json = JsonDocument.Parse(result.CopyEvidenceBytes());
                Assert.Equal(2, json.RootElement.GetArrayLength());
                InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            }

            // A representative refusal must not poison subsequent real, normally pinned extraction.
            var readable = InstalledDocumentHostileSupport.Source(format, SpecOfficeExtractionTests.Zip(Parts(format)));
            var recovered = await worker.ExtractAsync(readable, settings.OfficeLimits, deadline.Token);
            Assert.Equal(SpecExtractionOutcome.Partial, recovered.Outcome);
            Assert.Equal(SpecOfficeParser.Extract(readable, settings.OfficeLimits).CopyEvidenceBytes(), recovered.CopyEvidenceBytes());
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }

    [SpecDocumentInstalledHostileTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Installed_real_runner_caller_cancellation_or_default_wall_deadline_settles_then_recovers(bool office, bool callerCancellation)
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        // Run the unchanged production host startup before using the separately owned real runner.
        await using var host = new SpecDocumentHostResources(settings);
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await host.StartAsync(startup.Token);
            Assert.Equal("admitted", host.Status);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            using var caller = new CancellationTokenSource();
            var captured = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var officeRunner = office
                ? InstalledDocumentHostileSupport.OfficeRunner(settings, true, owner => captured.TrySetResult(owner)) : null;
            await using var pdfRunner = office ? null : InstalledDocumentHostileSupport.PdfRunner(settings, true);
            IInspectionWorkerRunner runner = (IInspectionWorkerRunner?)officeRunner ?? pdfRunner!;
            var frame = InstalledDocumentHostileSupport.Frame(office);
            var running = runner.RunAsync(frame, caller.Token);
            try
            {
                if (office)
                {
                    var owner = await captured.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
                    await owner.FrameFlushed.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
                    Assert.False(running.IsCompleted);
                }
                var pids = await InstalledDocumentHostileSupport.WitnessPendingChildrenAsync(1, running);
                if (callerCancellation) await caller.CancelAsync();
                // Default 30s Office / 10s PDF wall deadlines; no native resource/profile values are changed.
                var result = await running.WaitAsync(TimeSpan.FromMinutes(1));
                output.WriteLine($"{(office ? "Office" : "PDF")}/{(callerCancellation ? "caller" : "default-wall")}: {result.Failure}");
                Assert.Equal(callerCancellation ? WorkerProcessFailure.Canceled : WorkerProcessFailure.TimedOut, result.Failure);
                Assert.Empty(result.Output);
                Assert.True(officeRunner?.Available ?? pdfRunner!.Available);
                Assert.All(pids, pid => Assert.False(Directory.Exists($"/proc/{pid}")));
                if (office) await InstalledDocumentHostileSupport.AssertOfficeOwnerSettledAsync(await captured.Task);
                InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            }
            finally
            {
                // Separate teardown, not an extension of an invocation's product cleanup budget.
                await ((IAsyncDisposable?)officeRunner ?? pdfRunner!).DisposeAsync().AsTask()
                    .WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
                await running.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            }

            // EOF withholding is immutable on an options object. Recovery uses a fresh ordinary runner.
            await using var nextOffice = office ? InstalledDocumentHostileSupport.OfficeRunner(settings, false) : null;
            await using var nextPdf = office ? null : InstalledDocumentHostileSupport.PdfRunner(settings, false);
            IInspectionWorkerRunner next = (IInspectionWorkerRunner?)nextOffice ?? nextPdf!;
            var recovered = await next.RunAsync(frame, startup.Token);
            Assert.Equal(WorkerProcessFailure.None, recovered.Failure);
            if (office)
                Assert.Equal(SpecExtractionOutcome.Partial, SpecOfficeProcessProtocol.DecodeResponse(recovered.Output,
                    InstalledDocumentHostileSupport.ReadableOffice(), settings.OfficeLimits).Outcome);
            else
                Assert.Equal(SpecExtractionOutcome.Partial, SpecPdfExtractionResult.Decode(recovered.Output, 0,
                    InstalledDocumentHostileSupport.ReadablePdf(), settings.PdfProfile).Outcome);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }

    [SpecDocumentInstalledHostileTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controlled_host_Stop_or_Dispose_drains_registered_real_Office_and_Pdf_runners(bool dispose)
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // First prove unmodified production composition. The second host below is explicitly fixture composition.
        await using (var production = new SpecDocumentHostResources(settings))
        {
            await production.StartAsync(deadline.Token);
            Assert.Equal("admitted", production.Status);
            await production.StopAsync(deadline.Token);
        }
        InstalledDocumentHostileSupport.AssertSettled(settings, processes);

        ProcessCompiledRuntimeRunner? heldOffice = null;
        ProcessWorkerRunner? heldPdf = null;
        Task<WorkerProcessResult>? officeCall = null, pdfCall = null;
        var captured = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Existing controlled ownership port: never used to return fabricated availability or skip installation/pin/probe checks.
        await using var host = new SpecDocumentHostResources(settings, async (s, own, token) =>
        {
            SpecDocumentInstallation.Validate(s, token);
            var office = await QualifiedOfficeProcessWorker.CreateOwnedAsync(s.OfficeAssembly, s.OfficeBuild, s.OfficeRuntime,
                s.OfficeLauncher, s.OfficeWorkRoot, s.DotnetRoot, own, token);
            Assert.True(office.Available);
            var pdf = await QualifiedPdfProcessWorker.CreateForQualificationAsync(s.PdfExecutable, s.PdfWorkRoot,
                s.PdfProfile, s.PdfProfile.Digest, token, own);
            Assert.True(pdf.Available);
            // The wrappers' private runners cannot receive EOF options. Register extra real, pinned owners instead.
            // This proves host ownership/drain, NOT an active Qualified*.ExtractAsync shutdown race.
            heldOffice = InstalledDocumentHostileSupport.OfficeRunner(s, true, owner => captured.TrySetResult(owner));
            own(heldOffice);
            heldPdf = InstalledDocumentHostileSupport.PdfRunner(s, true);
            own(heldPdf);
            return new SpecDocumentHostResources.Workers(office, pdf,
                () => office.Available && pdf.Available && heldOffice.Available && heldPdf.Available,
                () => ValueTask.CompletedTask);
        });
        try
        {
            await host.StartAsync(deadline.Token);
            Assert.Equal("admitted", host.Status);
            var admission = host.Bind(_ => Task.FromResult(true));
            var office = Assert.IsType<QualifiedOfficeProcessWorker>(admission.Office);
            var pdf = Assert.IsType<QualifiedPdfProcessWorker>(admission.Pdf);
            Assert.Equal(6, (await admission.FormatsAsync(deadline.Token)).Count);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            Assert.NotNull(heldOffice); Assert.NotNull(heldPdf);
            officeCall = heldOffice.RunAsync(InstalledDocumentHostileSupport.Frame(true), deadline.Token);
            pdfCall = heldPdf.RunAsync(InstalledDocumentHostileSupport.Frame(false), deadline.Token);
            var owner = await captured.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
            await owner.FrameFlushed.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
            var pids = await InstalledDocumentHostileSupport.WitnessPendingChildrenAsync(2, officeCall, pdfCall);
            var stopping = dispose ? host.DisposeAsync().AsTask() : host.StopAsync(deadline.Token);
            // These are overlapping operations, witnessed live before shutdown; not a timed sleep after completion.
            Assert.Equal(3, (await admission.FormatsAsync(deadline.Token)).Count);
            await stopping.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            foreach (var call in new[] { officeCall, pdfCall })
            {
                Assert.True(call.IsCompleted);
                var result = await call;
                Assert.Equal(WorkerProcessFailure.Canceled, result.Failure);
                Assert.Empty(result.Output);
            }
            Assert.All(pids, pid => Assert.False(Directory.Exists($"/proc/{pid}")));
            await InstalledDocumentHostileSupport.AssertOfficeOwnerSettledAsync(owner);
            Assert.False(heldOffice.Available); Assert.False(heldPdf.Available);
            await Assert.ThrowsAsync<SpecOfficeWorkerUnavailableException>(() => office.ExtractAsync(
                InstalledDocumentHostileSupport.ReadableOffice(), settings.OfficeLimits, default));
            await Assert.ThrowsAsync<SpecPdfWorkerUnavailableException>(() => pdf.ExtractAsync(
                InstalledDocumentHostileSupport.ReadablePdf(), default));
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
                (await heldOffice.RunAsync(InstalledDocumentHostileSupport.Frame(true), default)).Failure);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
                (await heldPdf.RunAsync(InstalledDocumentHostileSupport.Frame(false), default)).Failure);
            await host.StopAsync(default);
            await host.DisposeAsync();
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            output.WriteLine($"Controlled host {(dispose ? "Dispose" : "Stop")}: both real held owners canceled, joined and admission closed.");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            if (officeCall is not null) await officeCall.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            if (pdfCall is not null) await pdfCall.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }

    [SpecDocumentInstalledQualificationFact]
    public async Task Installed_Office_directory_cleanup_fault_quarantines_retains_and_repeats_terminal_failure()
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var host = new SpecDocumentHostResources(settings);
        await host.StartAsync(deadline.Token);
        Assert.Equal("admitted", host.Status);
        InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        var injected = new IOException("Owned synthetic installed-Office directory-removal failure.");
        var captured = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessCompiledRuntimeRunner.OwnedOperation? owner = null;
        // Exact installed parser, launcher, runtime and native profile. Only the existing parent cleanup hook differs.
        var runner = new ProcessCompiledRuntimeRunner(new(settings.OfficeBuild, settings.OfficeAssembly,
            settings.OfficeLauncher, settings.OfficeWorkRoot, settings.DotnetRoot)
        {
            Operation = CompiledRuntimeOperation.OfficeExtraction,
            WithholdEndOfInput = true,
            ObserveOperation = operation => captured.TrySetResult(operation),
            BeforeCleanupStage = stage => stage == CompiledRuntimeCleanupStage.Directory
                ? Task.FromException(injected) : Task.CompletedTask
        });
        using var caller = new CancellationTokenSource();
        Task<WorkerProcessResult>? running = null;
        try
        {
            Assert.Equal(settings.OfficeRuntime, runner.RuntimeIdentity);
            running = runner.RunAsync(InstalledDocumentHostileSupport.Frame(true), caller.Token);
            owner = await captured.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
            await owner.FrameFlushed.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
            var pids = await InstalledDocumentHostileSupport.WitnessPendingChildrenAsync(1, running);
            await caller.CancelAsync();
            var result = await running.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            Assert.Equal(WorkerProcessFailure.CleanupFailed, result.Failure);
            Assert.Empty(result.Output);
            Assert.True(owner.Work?.IsCompletedSuccessfully == true);
            Assert.False(await owner.Work!);
            Assert.Same(injected, owner.Failure);
            Assert.True(owner.NativeExit?.IsCompletedSuccessfully == true && owner.Reaped?.IsCompletedSuccessfully == true);
            Assert.True(owner.Cancellation?.IsCompletedSuccessfully == true && owner.HandlesDisposed);
            Assert.All(owner.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
            Assert.All(pids, pid => Assert.False(Directory.Exists($"/proc/{pid}")));
            processes.AssertSettled();
            Assert.False(owner.DirectoryRemoved);
            Assert.True(Directory.Exists(owner.DirectoryPath));
            Assert.False(runner.Available);
            Assert.Equal(WorkerProcessFailure.SandboxUnavailable,
                (await runner.RunAsync(InstalledDocumentHostileSupport.Frame(true), default)).Failure);
            var stopping = runner.StopAsync();
            var first = await Assert.ThrowsAsync<InvalidOperationException>(() => stopping);
            Assert.Same(stopping, runner.StopAsync());
            Assert.Same(first, await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StopAsync()));
            Assert.Same(first, await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DisposeAsync().AsTask()));
            Assert.True(Directory.Exists(owner.DirectoryPath));
            output.WriteLine("Installed Office cleanup injection: actual child/I/O settled; exact directory retained; admission quarantined; repeated Stop/Dispose retained terminal fault.");
        }
        finally
        {
            await caller.CancelAsync();
            // Join the actual retained owner, not a replacement cleanup operation. Observe the known terminal fault.
            var stopFailure = await Record.ExceptionAsync(() => runner.DisposeAsync().AsTask()
                .WaitAsync(InstalledDocumentHostileSupport.JoinTimeout));
            await runner.JoinRetainedCleanupAsync().WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            if (running is not null) await running.WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            if (owner is null && captured.Task.IsCompletedSuccessfully) owner = await captured.Task;
            if (stopFailure is not null)
            {
                Assert.IsType<InvalidOperationException>(stopFailure);
                Assert.NotNull(owner);
                Assert.Same(injected, owner.Failure); // Unknown cleanup failures are never swallowed as expected injection.
            }
            if (owner is not null && ReferenceEquals(owner.Failure, injected))
            {
                Assert.True(owner.Work?.IsCompleted == true && owner.HandlesDisposed);
                Assert.All(owner.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
                Assert.True(owner.ChildPid == 0 || owner.NativeExit?.IsCompletedSuccessfully == true && owner.Reaped?.IsCompletedSuccessfully == true);
                Assert.Equal(settings.OfficeWorkRoot, Path.GetDirectoryName(owner.DirectoryPath));
                Assert.StartsWith("braid-execute-", Path.GetFileName(owner.DirectoryPath), StringComparison.Ordinal);
                Assert.Empty(Directory.EnumerateFileSystemEntries(owner.DirectoryPath));
                Directory.Delete(owner.DirectoryPath); // Exact, empty, joined fixture directory only; never a recursive work-root purge.
                Assert.False(runner.Available);
                await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StopAsync());
            }
            await host.StopAsync(CancellationToken.None).WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }

    private sealed record OfficeCase(string Name, byte[] Bytes, SpecOfficeExtractionLimits Limits, SpecExtractionOutcome Outcome, string Code);
    private static Dictionary<string, string> Parts(SpecSourceFormat format) => format == SpecSourceFormat.Docx
        ? SpecOfficeExtractionTests.Word() : SpecOfficeExtractionTests.Excel();

    private static IEnumerable<OfficeCase> OfficeCases(SpecSourceFormat format)
    {
        var defaults = new SpecOfficeExtractionLimits();
        var parts = Parts(format); parts["../outside.xml"] = "<x/>";
        yield return new("traversal", SpecOfficeExtractionTests.Zip(parts), defaults, SpecExtractionOutcome.Malformed, "unsafe-package-path");
        foreach (var target in new[] { "https://not-fetched.invalid/document", "file:///private" })
        {
            parts = Parts(format);
            parts["_rels/.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
                $"<Relationship Id='main' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='{target}' TargetMode='External'/></Relationships>";
            yield return new("external-relationship", SpecOfficeExtractionTests.Zip(parts), defaults, SpecExtractionOutcome.Unsupported, "external-relationship");
        }
        foreach (var entity in new[] { "SYSTEM 'file:///private'", "SYSTEM 'https://not-fetched.invalid/entity'", "'expansion'" })
        {
            parts = Parts(format);
            var main = format == SpecSourceFormat.Docx ? "word/document.xml" : "xl/workbook.xml";
            parts[main] = $"<!DOCTYPE document [<!ENTITY x {entity}>]>" + parts[main];
            yield return new("DTD-entity", SpecOfficeExtractionTests.Zip(parts), defaults, SpecExtractionOutcome.Malformed, "invalid-office-xml");
        }
        var valid = SpecOfficeExtractionTests.Zip(Parts(format));
        yield return new("truncated-ZIP", valid[..^12], defaults, SpecExtractionOutcome.Malformed, "invalid-zip-directory");
        var crc = (byte[])valid.Clone();
        var central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(crc.AsSpan(crc.Length - 6, 4)));
        crc[central + 16] ^= 1; crc[14] ^= 1;
        yield return new("damaged-CRC", crc, defaults, SpecExtractionOutcome.Malformed, "zip-content-mismatch");
        parts = format == SpecSourceFormat.Docx
            ? SpecOfficeExtractionTests.Word($"<w:p><w:r><w:t>{new string('x', 20_000)}</w:t></w:r></w:p>")
            : SpecOfficeExtractionTests.Excel(shared: $"<si><t>{new string('x', 20_000)}</t></si>");
        yield return new("compression-ratio", SpecOfficeExtractionTests.Zip(parts, CompressionLevel.SmallestSize), defaults, SpecExtractionOutcome.LimitExceeded, "zip-ratio");
        parts = Parts(format);
        parts[format == SpecSourceFormat.Docx ? "word/document.xml" : "xl/workbook.xml"] =
            string.Concat(Enumerable.Repeat("<x>", 66)) + string.Concat(Enumerable.Repeat("</x>", 66));
        yield return new("deep-XML", SpecOfficeExtractionTests.Zip(parts), defaults, SpecExtractionOutcome.LimitExceeded, "xml-depth");

        // Request-level extraction budgets are supported protocol inputs. They do not change native process limits.
        foreach (var (name, limits, code) in new (string, SpecOfficeExtractionLimits, string)[]
        {
            ("source-budget", new(maximumSourceBytes: 10), "source-bytes"),
            ("entry-count-budget", new(maximumEntries: 1), "zip-entries"),
            ("entry-size-budget", new(maximumEntryBytes: 10), "zip-entry-bytes"),
            ("expanded-budget", new(maximumExpandedBytes: 10), "zip-expanded-bytes"),
            ("XML-node-budget", new(maximumXmlNodes: 2), "xml-nodes")
        }) yield return new(name, valid, limits, SpecExtractionOutcome.LimitExceeded, code);
        parts = format == SpecSourceFormat.Docx
            ? SpecOfficeExtractionTests.Word("<w:p/><w:p/>")
            : SpecOfficeExtractionTests.Excel("<c r='A1'/><c r='B1'/>");
        yield return new("chunk-budget", SpecOfficeExtractionTests.Zip(parts), new(maximumChunks: 1), SpecExtractionOutcome.LimitExceeded, "office-chunks");
        parts = format == SpecSourceFormat.Docx
            ? SpecOfficeExtractionTests.Word($"<w:p><w:r><w:t>{new string('é', 80)}</w:t></w:r></w:p>")
            : SpecOfficeExtractionTests.Excel(shared: $"<si><t>{new string('é', 80)}</t></si>");
        yield return new("canonical-serialization-budget", SpecOfficeExtractionTests.Zip(parts), new(maximumEvidenceBytes: 256),
            SpecExtractionOutcome.LimitExceeded, "serialized-evidence-bytes");
        var encrypted = Convert.FromBase64String(format == SpecSourceFormat.Docx ? EncryptedDocx : EncryptedXlsx);
        Assert.Equal(format == SpecSourceFormat.Docx ? 10_752 : 11_776, encrypted.Length);
        Assert.Equal(format == SpecSourceFormat.Docx
            ? "6d7544a08970faaf4329ccdd694cc3c49b9ab5323ee8fdbe252d983a8155e992"
            : "4212bc2682d54c7f67f6d609c7726a6fa639d3ad70fef9783981b34ea8700902", Sha256Digest.FromBytes(encrypted).Value);
        yield return new("genuine-password-encrypted-Office", encrypted, defaults, SpecExtractionOutcome.Unsupported, "legacy-or-encrypted-office");
    }

    // FINAL public synthetic msoffcrypto-tool 6.1.0 ECMA-376 Agile OLE fixtures, public password braid-fixture-only.
    // Parent manifest records independent AES password-verifier/HMAC decryption and byte-identical OOXML recovery,
    // plus fresh msoffcrypto verification and wrong-password/cipher-mutation refusals. No desktop Office acceptance claimed.
    // Final plaintext ZIPs have a 4096-byte ZIP comment; the rejected tiny DOCX draft is deliberately not embedded.
    private const string EncryptedDocx = """
0M8R4KGxGuEAAAAAAAAAAAAAAAAAAAAAPgADAP7/CQAGAAAAAAAAAAAAAAABAAAAAgAAAAAAAAAAEAAAAQAAAAEAAAD+////AAAAAAAAAAD/////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////////9/////v///wMAAAAEAAAA/v///wYAAAAHAAAA
CAAAAP7///8KAAAACwAAAAwAAAANAAAADgAAAA8AAAAQAAAAEQAAABIAAAATAAAA/v//////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////wEAAAD+////AwAAAP7////+////BgAAAAcAAAAIAAAA/v///woAAAALAAAADAAAAA0AAAAOAAAA
DwAAABAAAAARAAAAEgAAABMAAAAUAAAAFQAAABYAAAAXAAAAGAAAABkAAAAaAAAAGwAAABwAAAAdAAAAHgAAAB8AAAD+////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////UgBvAG8AdAAgAEUAbgB0AHIAeQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABYABQD//////////woAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAIAMAjC+ct0BgAwCML5y3QEFAAAAAAgAAAAAAABFAG4AYwByAHkAcAB0AGUAZABQAGEAYwBrAGEAZwBlAAAAAAAAAAAAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAAAAIgACAP///////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAgAwCML5y3QGADAIwvnLdAQkAAAA4FAAAAAAAAAYARABhAHQA
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
AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAEAEAAAAA8P3htbCB2ZXJzaW9uPSIxLjAiIGVuY29kaW5nPSJVVEYt
OCIgc3RhbmRhbG9uZT0ieWVzIj8+CjxlbmNyeXB0aW9uIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL29mZmljZS8yMDA2L2VuY3J5cHRp
b24iIHhtbG5zOnA9Imh0dHA6Ly9zY2hlbWFzLm1pY3Jvc29mdC5jb20vb2ZmaWNlLzIwMDYva2V5RW5jcnlwdG9yL3Bhc3N3b3JkIiB4bWxuczpjPSJodHRw
Oi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL29mZmljZS8yMDA2L2tleUVuY3J5cHRvci9jZXJ0aWZpY2F0ZSI+CiAgICA8a2V5RGF0YSBzYWx0U2l6ZT0iMTYi
IGJsb2NrU2l6ZT0iMTYiIGtleUJpdHM9IjI1NiIgaGFzaFNpemU9IjY0IgogICAgICAgICAgICAgY2lwaGVyQWxnb3JpdGhtPSJBRVMiIGNpcGhlckNoYWlu
aW5nPSJDaGFpbmluZ01vZGVDQkMiIGhhc2hBbGdvcml0aG09IlNIQTUxMiIgc2FsdFZhbHVlPSJ5RmNxN1BSeHdYdFBxV00yVnJOa2d3PT0iIC8+CiAgICA8
ZGF0YUludGVncml0eSBlbmNyeXB0ZWRIbWFjS2V5PSJRRW01T0lUUEtZQ1pGQzRGbDYvcklyNS9wTjhWc1lWME9uK0xiVjU0K05VQkNqNUlGMTRHWCtGMWxI
TFE5c0RQNkRtcTRhZGdkQ0JEcU5sVzhNZVRCZz09IiBlbmNyeXB0ZWRIbWFjVmFsdWU9Ildyb3oxM3o1TDRCTEdwVWllRVlWYysvaW1jOHVnZEs5ZHF3amNS
R05aTnorN0FRaVFPaDVzaUljaUlheVNOV2JNV3Z3TlRTZUR2SjRJbnV3OUdRNWpnPT0iIC8+CiAgICA8a2V5RW5jcnlwdG9ycz4KICAgICAgICA8a2V5RW5j
cnlwdG9yIHVyaT0iaHR0cDovL3NjaGVtYXMubWljcm9zb2Z0LmNvbS9vZmZpY2UvMjAwNi9rZXlFbmNyeXB0b3IvcGFzc3dvcmQiPgogICAgICAgICAgICA8
cDplbmNyeXB0ZWRLZXkgc3BpbkNvdW50PSIxMDAwMDAiIHNhbHRTaXplPSIxNiIgYmxvY2tTaXplPSIxNiIga2V5Qml0cz0iMjU2IgogICAgICAgICAgICAg
ICAgICAgICAgICAgICAgaGFzaFNpemU9IjY0IiBjaXBoZXJBbGdvcml0aG09IkFFUyIgY2lwaGVyQ2hhaW5pbmc9IkNoYWluaW5nTW9kZUNCQyIgaGFzaEFs
Z29yaXRobT0iU0hBNTEyIgogICAgICAgICAgICAgICAgICAgICAgICAgICAgc2FsdFZhbHVlPSJYenhRTkNFMEJOakRxSm1ZS2F1bGNRPT0iIGVuY3J5cHRl
ZFZlcmlmaWVySGFzaElucHV0PSJBb0lwa0QzOTdIRkQ3M0RvT2JTM1lRPT0iCiAgICAgICAgICAgICAgICAgICAgICAgICAgICBlbmNyeXB0ZWRWZXJpZmll
ckhhc2hWYWx1ZT0iS3RGVmtiY0lSSjNaLzVlL1ZJODBsRUd5Tk1tQlR2UGVHS0k0d1h3S3AxS1lrNFdzcHRCSk94LzZDUWFjK2NDNm9xd0ROcjVRYWFaNDMz
RjRhaVppNGc9PSIgZW5jcnlwdGVkS2V5VmFsdWU9Ii8zSlVqUWlVb29sTyt3aVRZc05TTTdUZzZ2M2FFdUJyTVNQald5KzdIdDQ9IiAvPgogICAgICAgIDwv
a2V5RW5jcnlwdG9yPgogICAgPC9rZXlFbmNyeXB0b3JzPgo8L2VuY3J5cHRpb24+CgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAmFAAAAAAAAIlN
FHkRj+FCkOhYPbCadd9ydEY1mAT7htf8g7CtiWmse6GkdqeiQLS16Fh+QgtWEuhveLstOQlu/cG62xPRLaoGIyikTeUsFd936OGM1W+xklGQZht7ZNX7v2p6
6BfIjR2Qeo6WvodJHI6bWvoD5rFH89wnBPNS6a0YfLvV8XSFy23p9XVPtSW3DzNVpB7mNTr4LLr9GGVszi29bEOQ11WvtVCJoZsL5vhcbIKZFdelaIO5nf2h
4NM73siXlivLiy4Hd1y0UcvaMa2HRxZdL0N9A/LuOevmfXgDw+Los6VIXXM1VxHXDGkGCbCnEw9eQKrw4FqnAXQJAfmHO00bU9YM5TgaZchbnKuwUI9mQuwl
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
""";

    private const string EncryptedXlsx = """
0M8R4KGxGuEAAAAAAAAAAAAAAAAAAAAAPgADAP7/CQAGAAAAAAAAAAAAAAABAAAAAgAAAAAAAAAAEAAAAQAAAAEAAAD+////AAAAAAAAAAD/////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////////9/////v///wMAAAAEAAAA/v///wYAAAAHAAAA
CAAAAP7///8KAAAACwAAAAwAAAANAAAADgAAAA8AAAAQAAAAEQAAABIAAAATAAAAFAAAABUAAAD+////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////wEAAAD+////AwAAAP7////+////BgAAAAcAAAAIAAAA/v///woAAAALAAAADAAAAA0AAAAOAAAA
DwAAABAAAAARAAAAEgAAABMAAAAUAAAAFQAAABYAAAAXAAAAGAAAABkAAAAaAAAAGwAAABwAAAAdAAAAHgAAAB8AAAD+////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
////////UgBvAG8AdAAgAEUAbgB0AHIAeQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABYABQD//////////woAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAJB2OTC+ct0BkHY5ML5y3QEFAAAAAAgAAAAAAABFAG4AYwByAHkAcAB0AGUAZABQAGEAYwBrAGEAZwBlAAAAAAAAAAAAAAAAAAAA
AAAAAAAAAAAAAAAAAAAAAAAAIgACAP///////////////wAAAAAAAAAAAAAAAAAAAAAAAAAAkHY5ML5y3QGQdjkwvnLdAQkAAACoGQAAAAAAAAYARABhAHQA
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
AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAEAEAAAAA8P3htbCB2ZXJzaW9uPSIxLjAiIGVuY29kaW5nPSJVVEYt
OCIgc3RhbmRhbG9uZT0ieWVzIj8+CjxlbmNyeXB0aW9uIHhtbG5zPSJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL29mZmljZS8yMDA2L2VuY3J5cHRp
b24iIHhtbG5zOnA9Imh0dHA6Ly9zY2hlbWFzLm1pY3Jvc29mdC5jb20vb2ZmaWNlLzIwMDYva2V5RW5jcnlwdG9yL3Bhc3N3b3JkIiB4bWxuczpjPSJodHRw
Oi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL29mZmljZS8yMDA2L2tleUVuY3J5cHRvci9jZXJ0aWZpY2F0ZSI+CiAgICA8a2V5RGF0YSBzYWx0U2l6ZT0iMTYi
IGJsb2NrU2l6ZT0iMTYiIGtleUJpdHM9IjI1NiIgaGFzaFNpemU9IjY0IgogICAgICAgICAgICAgY2lwaGVyQWxnb3JpdGhtPSJBRVMiIGNpcGhlckNoYWlu
aW5nPSJDaGFpbmluZ01vZGVDQkMiIGhhc2hBbGdvcml0aG09IlNIQTUxMiIgc2FsdFZhbHVlPSJKUEFGMW1BOHhyY3hPeDVLVmhxdmpBPT0iIC8+CiAgICA8
ZGF0YUludGVncml0eSBlbmNyeXB0ZWRIbWFjS2V5PSJVMWR1eS9uTEVTam9qUFZiNDdXaEdpUm5KMzJJYVZZMUhRVndmMGl0LzlOU2wxb2hIZllwNk52SXg5
Z2pITXJ6MW8wUHBSbUFuZ1lrdVFkcjJpN2xUZz09IiBlbmNyeXB0ZWRIbWFjVmFsdWU9InBUdUZuVHpXRXdZT3BxVXdjSWgwQ3ZuMkJYM0c2OWZKTHpQbzlY
a0VmaHExRitQaHM4UGdUZnhZNFhoMEw2NVkzSEpueFh6YS9xaTB0ZHc2OFVwQnBRPT0iIC8+CiAgICA8a2V5RW5jcnlwdG9ycz4KICAgICAgICA8a2V5RW5j
cnlwdG9yIHVyaT0iaHR0cDovL3NjaGVtYXMubWljcm9zb2Z0LmNvbS9vZmZpY2UvMjAwNi9rZXlFbmNyeXB0b3IvcGFzc3dvcmQiPgogICAgICAgICAgICA8
cDplbmNyeXB0ZWRLZXkgc3BpbkNvdW50PSIxMDAwMDAiIHNhbHRTaXplPSIxNiIgYmxvY2tTaXplPSIxNiIga2V5Qml0cz0iMjU2IgogICAgICAgICAgICAg
ICAgICAgICAgICAgICAgaGFzaFNpemU9IjY0IiBjaXBoZXJBbGdvcml0aG09IkFFUyIgY2lwaGVyQ2hhaW5pbmc9IkNoYWluaW5nTW9kZUNCQyIgaGFzaEFs
Z29yaXRobT0iU0hBNTEyIgogICAgICAgICAgICAgICAgICAgICAgICAgICAgc2FsdFZhbHVlPSJzQVJVejRRdGtDTWo4S1podU0yZjRRPT0iIGVuY3J5cHRl
ZFZlcmlmaWVySGFzaElucHV0PSJtY1lQTlBSanErN0hMRnNKc1MrTWxnPT0iCiAgICAgICAgICAgICAgICAgICAgICAgICAgICBlbmNyeXB0ZWRWZXJpZmll
ckhhc2hWYWx1ZT0iUVo0bzJINjMvYjRyalpUSWRYaEFDMHE1ekJHNTlUQy9JRm96ODVZRjZQcmdER3hSQ1BqS0dhdkF5YWJ4SnJ4Y2hRaWIvWWhKanpGYnRR
eUhvc1VjUmc9PSIgZW5jcnlwdGVkS2V5VmFsdWU9Ik1rYWZZVnN4MFdYcUI3anlnTzVxWjA4UW9wYzdoMWE2WDFETWdjdUMyVzA9IiAvPgogICAgICAgIDwv
a2V5RW5jcnlwdG9yPgogICAgPC9rZXlFbmNyeXB0b3JzPgo8L2VuY3J5cHRpb24+CgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACbGQAAAAAAANI6
oX+wGKMNYe/FBqyMrI8TP4cNeJPdkkppOFvdotnzloczteJ2tjCY4+/BNY0SpwkO5Kine3A3efoPigj69x3PCX+7u07pA+8l1UwXw5lbP5F1n9Qj7FLgPLwn
OvQkSlP/wj/73ywBfK66cCoBWmu3nsnNxOFtuZphv7OBlJy7YyDrVlYrZFC8qKRa/LsWqrJfw09oepfmNpMHG4U8NlQOQ9kXLSVebTmwFfx9wiuemA7X75dl
p1f9k9HfbYgCautVtu7rl7GZSLSRadXj9T81gkkqvZczx6dBzQauhRi1yLY3dnzk9kux1DERVMzavNZQ8f8jbKymwkj+CeKuiKPSQNgrTpapMV2MeniNetn6
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
""";
}

// Select separately. This source-built, separately approved assembly is NOT the Office parser executable.
[Collection("Installed document qualification")]
[SupportedOSPlatform("linux")]
public sealed class SpecDocumentInstalledSharedProfileProbeTests(ITestOutputHelper output)
{
    [SpecDocumentInstalledQualificationFact]
    public async Task Separately_pinned_readonly_probe_observes_shared_Office_runtime_profile_denials()
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        var assembly = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_PROFILE_PROBE_ASSEMBLY");
        Assert.True(assembly is not null && Path.IsPathFullyQualified(assembly) && File.Exists(assembly));
        Assert.Equal("Braid.CompiledSandboxProbe.dll", Path.GetFileName(assembly));
        string Pin(string suffix)
        {
            var raw = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_PROFILE_PROBE_" + suffix);
            Assert.NotNull(raw);
            Assert.Equal(raw, Sha256Digest.Parse(raw).Value);
            return "sha256:" + raw;
        }
        var probe = settings with { OfficeAssembly = assembly!, OfficeBuild = Pin("BUILD_SHA256"), OfficeRuntime = Pin("RUNTIME_SHA256") };
        // Apply the unchanged installation gate to this additional tree, not only to the ordinary parser tree.
        SpecDocumentInstallation.Validate(probe, default);
        var marker = Path.Combine(settings.PdfWorkRoot, "shared-profile-outside-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("BRAID_RUNTIME_PROBE_SECRET");
        const string content = "synthetic local qualification marker";
        var mode = File.GetUnixFileMode(probe.OfficeAssembly);
        try
        {
            File.WriteAllText(marker, content);
            Assert.Equal(content, File.ReadAllText(marker)); // A nonexistent path cannot satisfy outside-read denial.
            Environment.SetEnvironmentVariable("BRAID_RUNTIME_PROBE_SECRET", "synthetic-must-not-be-inherited");
            await using var runner = InstalledDocumentHostileSupport.OfficeRunner(probe, false);
            var result = await runner.RunAsync(Encoding.UTF8.GetBytes(marker), default).WaitAsync(TimeSpan.FromMinutes(1));
            Assert.Equal(WorkerProcessFailure.None, result.Failure);
            Assert.Equal("outside-read=denied\nprivate-write=denied\nmetadata=denied\nnetwork=denied\nprocess=denied\nsignal-owner=denied\nenvironment=clean\nprivacy=sealed\n",
                Encoding.UTF8.GetString(result.Output).Replace("\r", "", StringComparison.Ordinal));
            Assert.Equal(mode, File.GetUnixFileMode(probe.OfficeAssembly));
            Assert.Equal(content, File.ReadAllText(marker));
            output.WriteLine("Shared Office runtime profile probe passed; this is not a denial probe inside the Office parser assembly.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAID_RUNTIME_PROBE_SECRET", previous);
            if (File.Exists(marker)) File.Delete(marker); // Only this fixture's exact marker, never a recursive work-root deletion.
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }
}

[Collection("Installed document qualification")]
[SupportedOSPlatform("linux")]
public sealed class SpecDocumentInstalledResourceProbeTests(ITestOutputHelper output)
{
    [SpecDocumentInstalledHostileTheory]
    [InlineData("memory")]
    [InlineData("overflow")]
    public async Task Separately_pinned_probe_exercises_fixed_Office_address_space_or_parent_stdout_bound(string action)
    {
        var settings = InstalledDocumentHostileSupport.Settings();
        using var processes = new InstalledDocumentProcessCensus();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var host = new SpecDocumentHostResources(settings);
        try
        {
            await host.StartAsync(deadline.Token);
            Assert.Equal("admitted", host.Status);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            var assembly = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_RESOURCE_PROBE_ASSEMBLY");
            Assert.True(assembly is not null && Path.IsPathFullyQualified(assembly) && File.Exists(assembly));
            Assert.Equal("Braid.InstalledResourceProbe.dll", Path.GetFileName(assembly));
            string Pin(string suffix)
            {
                var raw = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_RESOURCE_PROBE_" + suffix);
                Assert.NotNull(raw); Assert.Equal(raw, Sha256Digest.Parse(raw).Value);
                return "sha256:" + raw;
            }
            var probe = settings with { OfficeAssembly = assembly!, OfficeBuild = Pin("BUILD_SHA256"), OfficeRuntime = Pin("RUNTIME_SHA256") };
            SpecDocumentInstallation.Validate(probe, deadline.Token);
            var captured = new TaskCompletionSource<ProcessCompiledRuntimeRunner.OwnedOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var runner = InstalledDocumentHostileSupport.OfficeRunner(probe, false, owner => captured.TrySetResult(owner));
            // The new probe's closed flood command uses this exact fixed transport cap, not an adjustable option.
            Assert.Equal(10 * 1024 * 1024 + 2048, SpecOfficeProcessProtocol.MaximumResponseBytes);
            var result = await runner.RunAsync(Encoding.UTF8.GetBytes(action), deadline.Token).WaitAsync(TimeSpan.FromMinutes(1));
            var owner = await captured.Task.WaitAsync(InstalledDocumentHostileSupport.WitnessTimeout);
            await InstalledDocumentHostileSupport.AssertOfficeOwnerSettledAsync(owner);
            Assert.True(runner.Available);
            if (action == "memory")
            {
                Assert.Equal(WorkerProcessFailure.None, result.Failure);
                Assert.Equal("small=ok large=ENOMEM\n", Encoding.UTF8.GetString(result.Output));
            }
            else
            {
                Assert.Equal("overflow", action);
                Assert.Equal(WorkerProcessFailure.OutputLimitExceeded, result.Failure);
                Assert.Empty(result.Output);
                InstalledDocumentHostileSupport.AssertSettled(settings, processes);
                var recovered = await runner.RunAsync("memory"u8.ToArray(), deadline.Token);
                Assert.Equal(WorkerProcessFailure.None, recovered.Failure);
                Assert.Equal("small=ok large=ENOMEM\n", Encoding.UTF8.GetString(recovered.Output));
            }
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
            output.WriteLine($"Shared Office fixed-profile resource probe/{action}: {result.Failure}; not parser workload or OS RSS/OOM evidence.");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).WaitAsync(InstalledDocumentHostileSupport.JoinTimeout);
            InstalledDocumentHostileSupport.AssertSettled(settings, processes);
        }
    }
}

[SupportedOSPlatform("linux")]
internal static class InstalledDocumentHostileSupport
{
    internal static readonly TimeSpan WitnessTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20); // Test/teardown bound, not a product cleanup budget.
    private static readonly SpecSourceIdentity Identity = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    // ZIP builders stamp entry timestamps. Reuse captured bytes, rather than rebuild a differently hashed source after a 30s wait.
    private static readonly byte[] OfficeBytes = SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word());
    private static readonly byte[] PdfBytes = SpecPdfProcessTests.Pdf(["Held native PDF fixture"]);
    internal static SpecCapturedSource Source(SpecSourceFormat format, byte[] bytes) => new(Identity, format, bytes);
    internal static SpecCapturedSource ReadableOffice() => Source(SpecSourceFormat.Docx, OfficeBytes);
    internal static SpecCapturedSource ReadablePdf() => Source(SpecSourceFormat.Pdf, PdfBytes);
    internal static byte[] Frame(bool office) => office
        ? SpecOfficeProcessProtocol.EncodeRequest(ReadableOffice(), new()) : ReadablePdf().CopyBytes();

    internal static SpecDocumentHostSettings Settings()
    {
        Assert.True(OperatingSystem.IsLinux());
        Assert.Equal("1", Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_HOST_LINUX"));
        var settings = SpecDocumentHostSettings.Read(Environment.GetEnvironmentVariable);
        Assert.NotNull(settings);
        // Direct runner tests must fail too when root, capabilities or writable installation aliases are present.
        SpecDocumentInstallation.Validate(settings, default);
        SpecDocumentInstallation.RequireReadOnlyTree(Path.GetDirectoryName(typeof(SpecDocumentInstalledHostileTests).Assembly.Location)!,
            SpecDocumentInstallation.ParseMounts(File.ReadAllLines("/proc/self/mountinfo")));
        AssertSettled(settings);
        return settings;
    }

    internal static ProcessCompiledRuntimeRunner OfficeRunner(SpecDocumentHostSettings settings, bool withhold,
        Action<ProcessCompiledRuntimeRunner.OwnedOperation>? observe = null)
    {
        Assert.Equal(settings.OfficeBuild, "sha256:" + Sha256Digest.FromBytes(File.ReadAllBytes(settings.OfficeAssembly)).Value);
        var runner = new ProcessCompiledRuntimeRunner(new(settings.OfficeBuild, settings.OfficeAssembly,
            settings.OfficeLauncher, settings.OfficeWorkRoot, settings.DotnetRoot)
        { Operation = CompiledRuntimeOperation.OfficeExtraction, WithholdEndOfInput = withhold, ObserveOperation = observe });
        Assert.Equal(settings.OfficeRuntime, runner.RuntimeIdentity);
        return runner;
    }

    internal static ProcessWorkerRunner PdfRunner(SpecDocumentHostSettings settings, bool withhold)
    {
        var runner = new ProcessWorkerRunner(new(settings.PdfExecutable, DockerWorkerProfile.Pdf, settings.PdfWorkRoot)
        { WithholdEndOfInput = withhold });
        Assert.Equal("sha256:" + settings.PdfProfile.ExecutableDigest.Value, runner.BuildIdentity);
        return runner;
    }

    internal static async Task AssertOfficeOwnerSettledAsync(ProcessCompiledRuntimeRunner.OwnedOperation owner)
    {
        Assert.True(owner.Work?.IsCompletedSuccessfully == true);
        Assert.True(await owner.Work!);
        Assert.True(owner.NativeExit?.IsCompletedSuccessfully == true);
        Assert.True(owner.Reaped?.IsCompletedSuccessfully == true);
        Assert.True(owner.Cancellation?.IsCompletedSuccessfully == true);
        Assert.True(owner.HandlesDisposed && owner.DirectoryRemoved);
        Assert.Null(owner.Failure);
        Assert.All(owner.Tasks, task => { Assert.True(task.Original.IsCompleted); Assert.True(task.Owned.IsCompleted); });
        Assert.All(owner.IoTasks, task => Assert.True(task.IsCompleted));
        Assert.True(owner.CompletedAt > 0 && owner.CompletedAt <= owner.Deadline);
    }

    internal static async Task<int[]> WitnessPendingChildrenAsync(int count, params Task<WorkerProcessResult>[] pending)
    {
        using var bound = new CancellationTokenSource(WitnessTimeout);
        while (true)
        {
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            var ids = ChildIds().ToArray();
            if (ids.Length == count && ids.All(IsLiveAndHardened)) return ids;
            await Task.Delay(10, bound.Token);
        }
    }

    private static bool IsLiveAndHardened(int pid)
    {
        try
        {
            var lines = File.ReadAllLines($"/proc/{pid}/status");
            return lines.Contains("NoNewPrivs:\t1", StringComparer.Ordinal) && lines.Contains("Seccomp:\t2", StringComparer.Ordinal)
                && lines.Any(line => line.StartsWith("State:", StringComparison.Ordinal) && !line.Contains("Z (zombie)", StringComparison.Ordinal));
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void AssertSettled(SpecDocumentHostSettings settings, InstalledDocumentProcessCensus? processes = null)
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.OfficeWorkRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.PdfWorkRoot));
        Assert.Empty(ChildIds());
        processes?.AssertSettled();
    }

    private static HashSet<int> ChildIds()
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
        return ids; // Additional all-visible PID/start-time accounting is performed by the mandatory process census.
    }
}

// A bounded census for a dedicated container PID namespace, not a host-wide process monitor or cleanup framework.
// The pinned, read-only record must come from the external installation owner; no bool asserts containment.
[SupportedOSPlatform("linux")]
internal sealed class InstalledDocumentProcessCensus : IDisposable
{
    private sealed record ProcessIdentity(int Pid, ulong StartTicks);
    private sealed record ProcessEntry(ProcessIdentity Identity, int Parent);
    private sealed record NamespaceRecord(string Format, string PidNamespace, string ParentPidNamespace,
        ProcessIdentity Controller, ProcessIdentity[] Baseline);
    private readonly HashSet<ProcessIdentity> before;
    private readonly string pidNamespace;

    internal InstalledDocumentProcessCensus()
    {
        var path = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD");
        var expected = Environment.GetEnvironmentVariable("BRAID_TEST_DOCUMENT_PID_NAMESPACE_RECORD_SHA256");
        Assert.True(path is not null && Path.IsPathFullyQualified(path) && File.Exists(path),
            "Supply the external owner's protected dedicated-PID-namespace record, not a qualification boolean.");
        Assert.NotNull(expected);
        Assert.Equal(expected, Sha256Digest.Parse(expected).Value);
        ValidateEvidenceRecord(path!); // Nonexecutable controller evidence, not the production executable-tree policy.
        for (var current = path; current is not null && current != "/"; current = Path.GetDirectoryName(current))
            Assert.False((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0);
        Assert.InRange(new FileInfo(path!).Length, 1L, 16_384L);
        var bytes = File.ReadAllBytes(path!);
        Assert.Equal(expected, Sha256Digest.FromBytes(bytes).Value);
        var record = JsonSerializer.Deserialize<NamespaceRecord>(bytes);
        Assert.NotNull(record);
        Assert.Equal("braid.document-pid-namespace.v1", record.Format);
        pidNamespace = ReadNamespace();
        Assert.Equal(pidNamespace, record.PidNamespace);
        Assert.Matches(@"^pid:\[[0-9]+\]$", record.ParentPidNamespace);
        Assert.NotEqual(pidNamespace, record.ParentPidNamespace);
        Assert.NotNull(record.Controller); Assert.Equal(1, record.Controller.Pid);
        Assert.NotNull(record.Baseline); Assert.Equal(record.Controller, Assert.Single(record.Baseline));
        var initial = Snapshot();
        before = initial.Select(entry => entry.Identity).ToHashSet();
        Assert.Contains(record.Controller, before);
        var allowed = record.Baseline.ToHashSet();
        Assert.Equal(record.Baseline.Length, allowed.Count);
        Assert.Contains(record.Controller, allowed);
        Assert.All(allowed, identity => { Assert.True(identity.Pid > 0 && identity.StartTicks > 0); Assert.Contains(identity, before); });
        // The external record may precede vstest/testhost creation. Permit only this test process's actual ancestor chain in addition.
        var parent = Environment.ProcessId;
        for (var depth = 0; parent != 0; depth++)
        {
            Assert.True(depth < 64);
            var entry = Assert.Single(initial, candidate => candidate.Identity.Pid == parent);
            allowed.Add(entry.Identity);
            parent = entry.Parent;
        }
        Assert.Empty(before.Except(allowed)); // Unrecorded unrelated siblings make this context unsuitable; never kill them.
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct EvidenceStat
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint Links;
        [FieldOffset(28)] internal ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatEvidence(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out EvidenceStat value);

    private static void ValidateEvidenceRecord(string path)
    {
        Assert.Equal(path, SpecDocumentHostSettings.UnixPath(path));
        var mounts = SpecDocumentInstallation.ParseMounts(File.ReadAllLines("/proc/self/mountinfo"));
        static bool Inside(string child, string parent) => child == parent || parent == "/" || child.StartsWith(parent + "/", StringComparison.Ordinal);
        SpecDocumentInstallation.Mount Effective(string name) => mounts.Where(m => Inside(name, m.Path)).MaxBy(m => m.Path.Length)!;
        var fileMount = Effective(path);
        Assert.True(fileMount.ReadOnly && Effective(Path.GetDirectoryName(path)!).ReadOnly,
            "The evidence file and its visible parent path must be protected read-only.");
        Assert.DoesNotContain(mounts, m => Inside(m.Path, path) && !m.ReadOnly);
        Assert.Equal(0, StatEvidence(-100, path, 0x100, 0x7ff, out var stat));
        Assert.Equal(7u, stat.Mask & 7u); // STATX_TYPE | STATX_MODE | STATX_NLINK must actually be returned.
        Assert.Equal(0x8000, stat.Mode & 0xf000);
        Assert.Equal(1u, stat.Links);
        var relative = path[fileMount.Path.Length..].TrimStart('/');
        var backing = relative.Length == 0 ? fileMount.Root : fileMount.Root.TrimEnd('/') + "/" + relative;
        // Narrow alias check for this DATA file. Disjoint writable roots on Btrfs are acceptable with nlink=1;
        // a writable mount exposing this exact backing path is not. Executable admission intentionally stays stricter.
        Assert.DoesNotContain(mounts, m => !m.ReadOnly && m.Device == fileMount.Device && Inside(backing, m.Root));
        // The trusted external controller must additionally keep its source private 0700/unmapped, outside every
        // writable grant, seal the inode before launch and supply the expected hash independently. Not self-attested here.
    }

    internal void AssertSettled()
    {
        Assert.Equal(pidNamespace, ReadNamespace());
        // Identity, not PID/name/PPID/state: includes reparented descendants, other thread groups and zombies.
        var survivors = Snapshot().Select(entry => entry.Identity).Except(before).ToArray();
        Assert.True(survivors.Length == 0,
            "New process identities remain after settlement: " + string.Join(", ", survivors.Select(p => $"{p.Pid}@{p.StartTicks}")));
    }

    private static string ReadNamespace()
    {
        var value = new FileInfo("/proc/self/ns/pid").LinkTarget;
        Assert.NotNull(value);
        Assert.Matches(@"^pid:\[[0-9]+\]$", value);
        return value;
    }

    private static ProcessEntry[] Snapshot()
    {
        // A procfs mounted for an ancestor namespace must not be mistaken for this namespace's complete view.
        var self = File.ReadAllText("/proc/self/stat");
        Assert.Equal(Environment.ProcessId, int.Parse(self.AsSpan(0, self.IndexOf(' ')), CultureInfo.InvariantCulture));
        var selfFields = self[(self.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(selfFields.Length > 19);
        var selfIdentity = new ProcessIdentity(Environment.ProcessId, ulong.Parse(selfFields[19], CultureInfo.InvariantCulture));
        var entries = new List<ProcessEntry>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            // Other errors are qualification failures, not silently omitted processes.
            var close = stat.LastIndexOf(')'); Assert.True(close > 0);
            var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(fields.Length > 19);
            Assert.Equal(pid, int.Parse(stat.AsSpan(0, stat.IndexOf(' ')), CultureInfo.InvariantCulture));
            entries.Add(new(new(pid, ulong.Parse(fields[19], CultureInfo.InvariantCulture)), int.Parse(fields[1], CultureInfo.InvariantCulture)));
            Assert.True(entries.Count <= 64, "This test requires a dedicated, bounded PID namespace, not a busy shared host.");
        }
        Assert.Contains(entries, entry => entry.Identity == selfIdentity);
        return entries.ToArray();
    }

    public void Dispose() => AssertSettled(); // Observation only. No signals, subreaper changes or deletion of unrelated state.
}
