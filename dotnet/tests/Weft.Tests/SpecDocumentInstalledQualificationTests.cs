using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends.Workers;
using Weft.Server;

namespace Weft.Tests;

[CollectionDefinition("Installed document qualification", DisableParallelization = true)]
public sealed class SpecDocumentInstalledQualificationCollection;

public sealed class SpecDocumentInstalledQualificationFactAttribute : FactAttribute
{
    public SpecDocumentInstalledQualificationFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("WEFT_TEST_DOCUMENT_HOST_LINUX") != "1")
            Skip = "Requires Linux and explicit opt-in to an independently pinned, protected document installation.";
    }
}

[Collection("Installed document qualification")]
public sealed class SpecDocumentInstalledQualificationTests
{
    private static readonly SpecSourceIdentity Identity = new(new(new ProjectScope(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Guid.NewGuid(), 1);
    private static SpecCapturedSource Source(SpecSourceFormat format, byte[] bytes) => new(Identity, format, bytes);

    [Theory]
    [InlineData(true, "pdf-encrypted", 0)]
    [InlineData(false, "pdf-needs-ocr", 1)]
    public async Task Portable_pdf_fixture_validation_refuses_encrypted_and_image_only_documents(bool encrypted, string code, int pages)
    {
        // Portable fixture/protocol validation only: this does not qualify Linux containment.
        var executable = Environment.GetEnvironmentVariable("WEFT_TEST_PDF_EXECUTABLE");
        Assert.True(executable is not null && Path.IsPathFullyQualified(executable) && File.Exists(executable),
            "Build the Rust worker and set WEFT_TEST_PDF_EXECUTABLE to its absolute path.");
        var profile = new SpecPdfExtractionProfile(Sha256Digest.FromBytes(File.ReadAllBytes(executable!)));
        var source = Source(SpecSourceFormat.Pdf, encrypted ? Convert.FromBase64String(EncryptedPdf) : ImageOnlyPdf());
        var child = new SpecPdfProcessTests.Child();
        var wire = await child.Run(source.CopyBytes());
        var result = SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source, profile);
        Assert.Equal(SpecExtractionOutcome.Unsupported, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Equal(pages, result.Chunks.Count);
        Assert.All(result.Chunks, chunk => { Assert.True(chunk.MissingText); Assert.Equal(1, chunk.Locator.Page); });
        AssertIdentity(source, result.Identity, profile.Digest, result.CopyEvidenceBytes());
        Assert.True(child.Reaped && child.Joined);
    }

    [SpecDocumentInstalledQualificationFact]
    public async Task Installed_workers_parse_documents_refuse_atomic_failures_and_settle_real_children()
    {
        var settings = SpecDocumentHostSettings.Read(Environment.GetEnvironmentVariable);
        Assert.NotNull(settings);
        // No fixture factory: this performs the unchanged UID/mount/hash/procfs startup gates.
        await using var host = new SpecDocumentHostResources(settings);
        AssertSettled(settings);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await host.StartAsync(deadline.Token);
            Assert.Equal("admitted", host.Status);
            AssertSettled(settings);
            // Database admission is deliberately separate from installed worker qualification.
            var admission = host.Bind(_ => Task.FromResult(true));
            var office = Assert.IsType<QualifiedOfficeProcessWorker>(admission.Office);
            var pdf = Assert.IsType<QualifiedPdfProcessWorker>(admission.Pdf);
            Assert.Equal(settings.OfficeRuntime, office.RuntimeIdentity);
            Assert.Equal(settings.PdfProfile.Digest, pdf.ProfileDigest);
            Assert.Equal(6, (await admission.FormatsAsync(deadline.Token)).Count);

            foreach (var format in new[] { SpecSourceFormat.Docx, SpecSourceFormat.Xlsx })
            {
                var bytes = SpecOfficeExtractionTests.Zip(format == SpecSourceFormat.Docx
                    ? SpecOfficeExtractionTests.Word() : SpecOfficeExtractionTests.Excel());
                var source = Source(format, bytes);
                var result = await office.ExtractAsync(source, settings.OfficeLimits, deadline.Token);
                Assert.Equal(SpecExtractionOutcome.Partial, result.Outcome);
                Assert.Equal(format == SpecSourceFormat.Docx ? "café 東京" : "東京", Assert.Single(result.Chunks).Text);
                Assert.Equal(SpecOfficeParser.Extract(source, settings.OfficeLimits).CopyEvidenceBytes(), result.CopyEvidenceBytes());
                AssertIdentity(source, result.Identity, settings.OfficeLimits.Digest, result.CopyEvidenceBytes());
                AssertSettled(settings);

                result = await office.ExtractAsync(Source(format, "not a zip"u8.ToArray()), settings.OfficeLimits, deadline.Token);
                Assert.Equal(SpecExtractionOutcome.Malformed, result.Outcome);
                Assert.Empty(result.Chunks);
                AssertSettled(settings);

                // Same encrypted-directory marker fixture as the existing parser regression.
                var encrypted = (byte[])bytes.Clone();
                var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(encrypted.AsSpan(encrypted.Length - 6, 4));
                encrypted[central + 8] |= 1;
                result = await office.ExtractAsync(Source(format, encrypted), settings.OfficeLimits, deadline.Token);
                Assert.Equal(SpecExtractionOutcome.Unsupported, result.Outcome);
                Assert.Equal("encrypted-zip", result.Code);
                Assert.Empty(result.Chunks);
                AssertSettled(settings);
            }

            var readable = Source(SpecSourceFormat.Pdf, SpecPdfProcessTests.Pdf(["First page", "", "Third page"]));
            var extracted = await pdf.ExtractAsync(readable, deadline.Token);
            Assert.Equal(SpecExtractionOutcome.Partial, extracted.Outcome);
            Assert.Equal(new[] { 1, 2, 3 }, extracted.Chunks.Select(chunk => chunk.Locator.Page));
            Assert.Contains("First page", extracted.Chunks[0].Text);
            Assert.True(extracted.Chunks[1].MissingText);
            Assert.Contains("Third page", extracted.Chunks[2].Text);
            AssertIdentity(readable, extracted.Identity, settings.PdfProfile.Digest, extracted.CopyEvidenceBytes());
            AssertSettled(settings);

            foreach (var (bytes, outcome, code, pages) in new[]
            {
                ("not a pdf"u8.ToArray(), SpecExtractionOutcome.Malformed, "pdf-malformed", 0),
                (Convert.FromBase64String(EncryptedPdf), SpecExtractionOutcome.Unsupported, "pdf-encrypted", 0),
                (ImageOnlyPdf(), SpecExtractionOutcome.Unsupported, "pdf-needs-ocr", 1)
            })
            {
                var source = Source(SpecSourceFormat.Pdf, bytes);
                var result = await pdf.ExtractAsync(source, deadline.Token);
                Assert.Equal(outcome, result.Outcome);
                Assert.Equal(code, result.Code);
                Assert.Equal(pages, result.Chunks.Count);
                Assert.All(result.Chunks, chunk => { Assert.True(chunk.MissingText); Assert.Equal(1, chunk.Locator.Page); });
                AssertIdentity(source, result.Identity, settings.PdfProfile.Digest, result.CopyEvidenceBytes());
                AssertSettled(settings);
            }

            await CheckOfficeProtocolAsync(settings, deadline.Token);
            AssertSettled(settings);
            await CheckPdfShutdownAsync(settings, deadline.Token);
            AssertSettled(settings);

            await host.StopAsync(deadline.Token);
            Assert.Equal(3, (await admission.FormatsAsync(deadline.Token)).Count);
            await Assert.ThrowsAsync<SpecOfficeWorkerUnavailableException>(() => office.ExtractAsync(
                Source(SpecSourceFormat.Docx, SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word())), settings.OfficeLimits, default));
            await Assert.ThrowsAsync<SpecPdfWorkerUnavailableException>(() => pdf.ExtractAsync(readable, default));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            AssertSettled(settings);
        }
    }

    private static async Task CheckOfficeProtocolAsync(SpecDocumentHostSettings settings, CancellationToken token)
    {
        // The same installed launcher, fixed Office operation and real pre-input admission, not PortableWorker.
        await using var runner = new ProcessCompiledRuntimeRunner(new(settings.OfficeBuild, settings.OfficeAssembly,
            settings.OfficeLauncher, settings.OfficeWorkRoot, settings.DotnetRoot) { Operation = CompiledRuntimeOperation.OfficeExtraction });
        Assert.Equal(settings.OfficeRuntime, runner.RuntimeIdentity);
        var source = Source(SpecSourceFormat.Docx, SpecOfficeExtractionTests.Zip(SpecOfficeExtractionTests.Word()));
        var frame = SpecOfficeProcessProtocol.EncodeRequest(source, settings.OfficeLimits);
        var corrupt = (byte[])frame.Clone();
        corrupt[52] ^= 1;
        var refused = await runner.RunAsync(corrupt, token);
        Assert.Equal(WorkerProcessFailure.WorkerFailed, refused.Failure);
        Assert.Empty(refused.Output);
        AssertSettled(settings);

        var wire = await runner.RunAsync(frame, token);
        Assert.Equal(WorkerProcessFailure.None, wire.Failure);
        Assert.Equal(SpecExtractionOutcome.Partial, SpecOfficeProcessProtocol.DecodeResponse(wire.Output, source, settings.OfficeLimits).Outcome);
        // Parent outcome denial uses an actual contained response with one deliberately changed field.
        // This is decoder validation, not a claim that a hostile replacement executable was admitted.
        var forged = JsonNode.Parse(wire.Output)!;
        forged["outcome"] = "Complete";
        Assert.Throws<SpecOfficeProtocolException>(() => SpecOfficeProcessProtocol.DecodeResponse(
            Encoding.UTF8.GetBytes(forged.ToJsonString()), source, settings.OfficeLimits));
    }

    private static async Task CheckPdfShutdownAsync(SpecDocumentHostSettings settings, CancellationToken token)
    {
        // Withhold EOF only: the executable, sandbox arguments, verification and resource limits stay real.
        await using var runner = new ProcessWorkerRunner(new(settings.PdfExecutable, DockerWorkerProfile.Pdf, settings.PdfWorkRoot)
        { WithholdEndOfInput = true });
        Assert.Equal("sha256:" + settings.PdfProfile.ExecutableDigest.Value, runner.BuildIdentity);
        var pending = runner.RunAsync(SpecPdfProcessTests.Pdf(["Shutdown fixture"]), token);
        try
        {
            using var started = CancellationTokenSource.CreateLinkedTokenSource(token);
            started.CancelAfter(TimeSpan.FromSeconds(5));
            // Observe hardening on the actual waiting child; completion first is a failure, not a skip.
            while (!ChildIds().Any(IsHardened) && !pending.IsCompleted) await Task.Delay(10, started.Token);
            Assert.False(pending.IsCompleted);
            Assert.Contains(ChildIds(), IsHardened);
        }
        finally
        {
            await runner.StopAsync();
            await pending; // Join the operation even if a qualification assertion above failed.
        }
        var result = await pending;
        Assert.Equal(WorkerProcessFailure.Canceled, result.Failure);
        Assert.Empty(result.Output);
        Assert.False(runner.Available);
    }

    private static void AssertIdentity(SpecCapturedSource source, SpecExtractionIdentity identity, Sha256Digest profile, byte[] evidence)
    {
        Assert.Equal(source.Identity, identity.Source);
        Assert.Equal(source.Digest, identity.SourceDigest);
        Assert.Equal(profile, identity.ProfileDigest);
        Assert.Equal(Sha256Digest.FromBytes(evidence), identity.EvidenceDigest);
    }

    private static void AssertSettled(SpecDocumentHostSettings settings)
    {
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.OfficeWorkRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(settings.PdfWorkRoot));
        Assert.Empty(ChildIds());
    }

    // Linux assigns children to individual threads. Include all threads and zombies, not only process names.
    private static HashSet<int> ChildIds()
    {
        var children = new HashSet<int>();
        foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(task, "children")); }
            catch (FileNotFoundException) { continue; } // A runtime thread can exit during enumeration.
            catch (DirectoryNotFoundException) { continue; }
            foreach (var child in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                children.Add(int.Parse(child, CultureInfo.InvariantCulture));
        }
        return children;
    }

    private static bool IsHardened(int child)
    {
        try
        {
            var status = File.ReadAllLines($"/proc/{child}/status");
            return status.Contains("NoNewPrivs:\t1", StringComparer.Ordinal) && status.Contains("Seccomp:\t2", StringComparer.Ordinal);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static byte[] ImageOnlyPdf()
    {
        // A painted one-pixel image XObject, not merely an empty text stream. ASCIIHex keeps offsets ASCII.
        const string content = "q 10 0 0 10 30 700 cm /Im1 Do Q";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length 7 >>\nstream\n000000>\nendstream"];
        var document = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(document.Length);
            document.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = document.Length;
        document.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) document.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        document.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(document.ToString());
    }

    // 900-byte PDF Standard Security Handler V1/R2 (40-bit RC4), public fixture passwords
    // fixture-owner / fixture-reader. The page stream is encrypted, not just an /Encrypt marker.
    // SHA256 40649c72b43425c05b2d3f4b5ced88f8a9cb3106e277d43cd4e9ab1aa4dc3667.
    private const string EncryptedPdf =
        "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9Db3VudCAxIC9LaWRzIFs0IDAgUl0gPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9udCAvSGVsdmV0aWNhID4+CmVuZG9iago0IDAgb2JqCjw8IC9UeXBlIC9QYWdlIC9QYXJlbnQgMiAwIFIgL01lZGlhQm94IFswIDAgNjEyIDc5Ml0gL1Jlc291cmNlcyA8PCAvRm9udCA8PCAvRjEgMyAwIFIgPj4gPj4gL0NvbnRlbnRzIDUgMCBSID4+CmVuZG9iago1IDAgb2JqCjw8IC9MZW5ndGggNTEgPj4Kc3RyZWFtCrkbxTWKKv9UCC8H+JZbfpD95k/waspe20LUJmZz99BwOdJuycMTLtZy7Bo9nXiG1oWHCgplbmRzdHJlYW0KZW5kb2JqCjYgMCBvYmoKPDwgL0ZpbHRlciAvU3RhbmRhcmQgL1YgMSAvUiAyIC9PIDxiODA4ZWVjMWFmNGY1MDVkMjE2MTE2ZTExYjYzNzY2MjVhNGE5NGMxNGY1NjAzM2JlNTM3ZDNmNWMzMGIwMjk4PiAvVSA8YWM0ZGUzZWFkYWU3ZGNlZGRhYTNmYWE1NWM3NWNiYjFiMDdlMTdjZTQ5NTM0NGY3N2M2OThkZjcwYzY2OTEyMz4gL1AgLTQgPj4KZW5kb2JqCnhyZWYKMCA3CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAwOSAwMDAwMCBuIAowMDAwMDAwMDU4IDAwMDAwIG4gCjAwMDAwMDAxMTUgMDAwMDAgbiAKMDAwMDAwMDE4NSAwMDAwMCBuIAowMDAwMDAwMzExIDAwMDAwIG4gCjAwMDAwMDA0MTIgMDAwMDAgbiAKdHJhaWxlcgo8PCAvU2l6ZSA3IC9Sb290IDEgMCBSIC9FbmNyeXB0IDYgMCBSIC9JRCBbPDc3NjU2Njc0MmQ2NjY5Nzg3NDc1NzI2NTJkNjk2NDIxPjw3NzY1NjY3NDJkNjY2OTc4NzQ3NTcyNjUyZDY5NjQyMT5dID4+CnN0YXJ0eHJlZgo2MDcKJSVFT0YK";
}
