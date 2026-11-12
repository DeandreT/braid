using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;

namespace Weft.Tests;

public sealed class SpecPdfProcessTests
{
    private static readonly SpecSourceIdentity Identity = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    private static string Executable
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("WEFT_TEST_PDF_EXECUTABLE");
            Assert.True(path is not null && Path.IsPathFullyQualified(path) && File.Exists(path), "Build the Rust worker and set WEFT_TEST_PDF_EXECUTABLE to its absolute path.");
            return path!;
        }
    }
    private static SpecPdfExtractionProfile Profile(int cap = 8 * 1024 * 1024) => new(Sha256Digest.FromBytes(File.ReadAllBytes(Executable)), cap);
    private static SpecCapturedSource Source(params string[] pages) => new(Identity, SpecSourceFormat.Pdf, Pdf(pages));

    // Real minimal PDFs with byte-accurate xref tables, including explicit empty page streams.
    internal static byte[] Pdf(string[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        for (var i = 0; i < pages.Length; i++)
        {
            var page = 4 + i * 2;
            var text = pages[i];
            var content = text.Length == 0 ? "" : "BT /F1 12 Tf 72 720 Td (" + text + ") Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {page + 1} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objects[1] = $"<< /Type /Pages /Count {pages.Length} /Kids [" + string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{4 + i * 2} 0 R")) + "] >>";
        var document = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        { offsets.Add(document.Length); document.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = document.Length;
        document.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) document.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        document.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    // Portable actual-process harness only. Production uses the native sandbox runner and stays gated.
    internal sealed class Child
    {
        public bool Reaped { get; private set; }
        public bool Joined { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(byte[] Output, int Exit)> Run(byte[] input, int cap = SpecPdfWorkerResponse.MaximumResponseBytes,
            bool withhold = false, int milliseconds = 20000, CancellationToken token = default, string[]? arguments = null)
        {
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments ?? []) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Started.TrySetResult();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(milliseconds);
            var output = SpecOfficeProcessProtocol.ReadBoundedAsync(process.StandardOutput.BaseStream, cap, deadline.Token);
            var error = SpecOfficeProcessProtocol.ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
            var write = Task.Run(async () =>
            {
                if (withhold) await Task.Delay(Timeout.Infinite, deadline.Token);
                await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token); process.StandardInput.Close();
            });
            Task[] tasks = [output, error, write, process.WaitForExitAsync(deadline.Token)];
            try
            {
                var pending = tasks.ToList();
                while (pending.Count > 0)
                { var done = await Task.WhenAny(pending).WaitAsync(deadline.Token); pending.Remove(done); await done; }
                Assert.Empty(await error);
                return (await output, process.ExitCode);
            }
            finally
            {
                await deadline.CancelAsync();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Reaped = process.HasExited;
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (tasks.All(t => t.IsCompleted)) { }
                Joined = tasks.All(t => t.IsCompleted);
            }
        }
    }

    [Fact]
    public async Task Real_pdf_child_preserves_page_order_blanks_and_canonical_citations()
    {
        var source = Source("First page", "", "Third page"); var profile = Profile(); var child = new Child();
        var wire = await child.Run(source.CopyBytes());
        var result = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, profile);
        Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome); Assert.Equal(3, result.Chunks.Count);
        Assert.Contains("First page", result.Chunks[0].Text); Assert.True(result.Chunks[1].MissingText); Assert.Contains("Third page", result.Chunks[2].Text);
        Assert.Equal(new[] { 1, 2, 3 }, result.Chunks.Select(p => p.Locator.Page));
        Assert.Equal(new[] { 0, 1, 2 }, result.Chunks.Select(p => p.Ordinal)); Assert.All(result.Chunks, p => Assert.Null(p.Formula));
        Assert.Equal(3, result.Cite(2).Locator.Page); Assert.Equal(result.Identity, result.Cite(2).Extraction);
        Assert.Equal(profile.Digest, result.Identity.ProfileDigest); Assert.Equal(source.Digest, result.Identity.SourceDigest);
        using var json = JsonDocument.Parse(result.CopyEvidenceBytes());
        Assert.Equal("weft.spec-evidence.v2", json.RootElement[0].GetString());
        Assert.Equal("pdf-page", json.RootElement[3][2][0].GetString()); Assert.Equal(2, json.RootElement[3][2][1].GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement[3][3].ValueKind); Assert.True(json.RootElement[3][4].GetBoolean());
        Assert.Equal(Sha256Digest.FromBytes(result.CopyEvidenceBytes()), result.Identity.EvidenceDigest);
        var repeat = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, profile);
        Assert.Equal(new SpecBundleSnapshot(Identity.Bundle, [result.Identity]).Digest, new SpecBundleSnapshot(Identity.Bundle, [repeat.Identity]).Digest);
        Assert.True(child.Reaped && child.Joined);
    }

    [Fact]
    public void Legacy_extractor_refuses_pdf_before_utf8_or_json_decoding()
    {
        foreach (var bytes in new[] { Pdf(["Real PDF text"]), "{}"u8.ToArray(), new byte[] { 0xff, 0xfe } })
        {
            var source = new SpecCapturedSource(Identity, SpecSourceFormat.Pdf, bytes);
            var result = SpecSourceExtractor.Extract(source);
            Assert.Equal(SpecExtractionOutcome.Unsupported, result.Outcome);
            Assert.Equal("pdf-worker-required", result.Code);
            Assert.Empty(result.Chunks);
            Assert.Equal(source.Identity, result.Identity.Source);
            Assert.Equal(source.Digest, result.Identity.SourceDigest);
        }
    }

    [Fact]
    public void Canonical_pdf_results_have_no_public_constructor_or_decode_factory()
    {
        Assert.Empty(typeof(SpecPdfExtractionResult).GetConstructors());
        Assert.Empty(typeof(SpecPdfExtractionResult).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public async Task Actual_blank_pdf_requests_ocr_and_malformed_pdf_refuses_atomically()
    {
        var source = Source("", ""); var wire = await new Child().Run(source.CopyBytes());
        var result = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, Profile());
        Assert.Equal(SpecExtractionOutcome.Unsupported, result.Outcome); Assert.Equal("pdf-needs-ocr", result.Code);
        Assert.Equal(2, result.Chunks.Count); Assert.All(result.Chunks, page => Assert.True(page.MissingText));
        source = new(Identity, SpecSourceFormat.Pdf, "malformed"u8);
        wire = await new Child().Run(source.CopyBytes());
        result = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, Profile());
        Assert.Equal(SpecExtractionOutcome.Malformed, result.Outcome); Assert.Empty(result.Chunks);
    }

    [Fact]
    public async Task Actual_wire_cannot_be_rebound_to_changed_source_or_admitted_after_nonzero_exit()
    {
        var source = Source("Text"); var wire = await new Child().Run(source.CopyBytes()); var profile = Profile();
        var changed = new SpecCapturedSource(Identity, SpecSourceFormat.Pdf, source.CopyBytes().Concat(new byte[] { 0 }).ToArray());
        Assert.Throws<SpecPdfWorkerProtocolException>(() => SpecPdfExtractionResult.Decode(wire.Output, 0, changed, profile));
        Assert.Throws<SpecPdfWorkerProtocolException>(() => SpecPdfExtractionResult.Decode(wire.Output, 1, source, profile));
        var json = JsonNode.Parse(wire.Output)!; json["pages"]![0]!["page"] = 2;
        Assert.Throws<SpecPdfWorkerProtocolException>(() => SpecPdfExtractionResult.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), 0, source, profile));
        wire = await new Child().Run([], arguments: ["caller.pdf"]);
        Assert.NotEqual(0, wire.Exit); Assert.Empty(wire.Output);
    }

    [Fact]
    public async Task Canonical_physical_expansion_is_atomic_and_profile_bound()
    {
        var source = Source(Enumerable.Repeat("visible text", 12).ToArray()); var wire = await new Child().Run(source.CopyBytes());
        var result = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, Profile(256));
        Assert.Equal(SpecExtractionOutcome.LimitExceeded, result.Outcome); Assert.Equal("serialized-evidence-bytes", result.Code);
        Assert.Empty(result.Chunks); Assert.True(result.CopyEvidenceBytes().Length <= 256);
        Assert.NotEqual(Profile().Digest, result.Identity.ProfileDigest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_child_cancellation_and_deadline_join_and_reap(bool caller)
    {
        var child = new Child(); using var cancel = new CancellationTokenSource();
        var run = child.Run([], withhold: true, milliseconds: caller ? 20000 : 200, token: cancel.Token);
        await child.Started.Task; if (caller) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(child.Reaped && child.Joined);
    }

    [Fact]
    public async Task Actual_stdout_overflow_never_reaches_decoder()
    {
        var child = new Child();
        await Assert.ThrowsAsync<SpecOfficeProtocolException>(() => child.Run(Source("Text").CopyBytes(), cap: 16));
        Assert.True(child.Reaped && child.Joined);
    }

    [Fact]
    public async Task Pdf_capture_caps_at_native_bound_and_production_gate_is_closed()
    {
        using var stream = new MemoryStream(new byte[SpecPdfWorkerResponse.MaximumSourceBytes + 2]);
        var result = await SpecSourceCapture.ReadAsync(Identity, SpecSourceFormat.Pdf, stream);
        Assert.Equal(SpecCaptureOutcome.LimitExceeded, result.Outcome); Assert.Null(result.Source);
        Assert.Equal(SpecPdfWorkerResponse.MaximumSourceBytes + 1, stream.Position);
        using var small = new MemoryStream(Pdf(["text"]));
        result = await SpecSourceCapture.ReadAsync(Identity, SpecSourceFormat.Pdf, small);
        Assert.Equal(SpecSourceFormat.Pdf, result.Source!.Format);
        await using var host = QualifiedPdfProcessWorker.Create(Profile());
        Assert.False(host.Available);
        Assert.Equal(OperatingSystem.IsLinux() ? "pdf-worker-linux-qualification-pending" : "pdf-worker-platform-unsupported", host.Refusal);
        await Assert.ThrowsAsync<SpecPdfWorkerUnavailableException>(() => host.ExtractAsync(result.Source, default));
    }

    [Fact]
    public void Build_and_profile_pins_are_independent_of_protocol_strings()
    {
        var profile = Profile();
        Assert.True(QualifiedPdfProcessWorker.MatchesBuild(Executable, profile));
        var changed = new SpecPdfExtractionProfile(Sha256Digest.FromBytes("different build"u8));
        Assert.False(QualifiedPdfProcessWorker.MatchesBuild(Executable, changed)); Assert.NotEqual(profile.Digest, changed.Digest);
        Assert.Equal(profile.Digest, Profile().Digest); Assert.NotEqual(profile.Digest, Profile(256).Digest);
        var native = DockerWorkerProfile.Pdf;
        Assert.Equal(10, native.CpuSeconds); Assert.Equal(536_870_912, native.MemoryBytes); Assert.Equal(64, native.FileLimit);
        Assert.Equal(TimeSpan.FromSeconds(10), native.MaximumWallTimeout);
        Assert.Equal(SpecPdfWorkerResponse.MaximumSourceBytes, native.MaximumRequestBytes);
        Assert.Equal(SpecPdfWorkerResponse.MaximumResponseBytes, native.MaximumOutputBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpecPdfExtractionProfile(profile.ExecutableDigest, 255));
    }
}
