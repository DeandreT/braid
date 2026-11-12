using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class CreationEvidenceGenerationTests
{
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
    private static readonly SpecBundleScope Bundle = new(new(Id(1), Id(2)), Id(3));
    private static SpecCapturedSource Source(int id, SpecSourceFormat format, byte[] bytes) => new(new(Bundle, Id(id), 1), format, bytes);
    private static SpecCapturedSource Text(int id = 4) => Source(id, SpecSourceFormat.Text, "Map input to output"u8.ToArray());

    private static SpecCapturedSource Office(int id, bool workbook = false, bool empty = false)
    {
        const string rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string types = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var parts = new Dictionary<string, string>();
        var main = workbook ? "xl/workbook.xml" : "word/document.xml";
        var content = workbook ? "spreadsheetml.sheet" : "wordprocessingml.document";
        parts["[Content_Types].xml"] = $"<Types xmlns='{types}'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/{main}' ContentType='application/vnd.openxmlformats-officedocument.{content}.main+xml'/></Types>";
        parts["_rels/.rels"] = $"<Relationships xmlns='{rel}'><Relationship Id='main' Type='{r}/officeDocument' Target='{main}'/></Relationships>";
        if (workbook)
        {
            parts[main] = $"<workbook xmlns='{s}' xmlns:r='{r}'><sheets><sheet name='Rules' sheetId='1' r:id='sheet'/></sheets></workbook>";
            parts["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns='{rel}'><Relationship Id='sheet' Type='{r}/worksheet' Target='worksheets/sheet1.xml'/></Relationships>";
            var cells = empty ? "" : "<c r='A1'><f>1+2</f><v>3</v></c><c r='B1'><f>4+5</f></c>";
            parts["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns='{s}'><sheetData><row r='1'>{cells}</row></sheetData></worksheet>";
        }
        else
        {
            var body = empty ? "" : "<w:p><w:r><w:t>Map input</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>to output</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
            parts[main] = $"<w:document xmlns:w='{w}'><w:body>{body}</w:body></w:document>";
        }
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var (name, value) in parts)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(Encoding.UTF8.GetBytes(value));
            }
        return Source(id, workbook ? SpecSourceFormat.Xlsx : SpecSourceFormat.Docx, bytes.ToArray());
    }

    internal static (CreationSpecification Spec, SpecOfficeExtractionResult Word, SpecOfficeExtractionResult Sheet) Mixed()
    {
        var text = Text(); var word = Office(5); var sheet = Office(6, true);
        var a = SpecOfficeParser.Extract(word); var b = SpecOfficeParser.Extract(sheet);
        Assert.Equal(SpecExtractionOutcome.Partial, a.Outcome); Assert.Equal(SpecExtractionOutcome.Partial, b.Outcome);
        return (new(Bundle, [text, word, sheet], new[] { CreationEvidenceDocument.From(SpecSourceExtractor.Extract(text)),
            CreationEvidenceDocument.From(a), CreationEvidenceDocument.From(b) }), a, b);
    }
    private static CreationGenerationNotes Notes(bool question = false) => new([new(Text().Identity, 0)],
        [new("ModelAssumption", "Input is text", false)], question ? [new("Which rule applies?", true)] : []);

    [Fact]
    public async Task Mixed_genuine_results_retain_exact_bytes_structural_locators_and_formula_cache_facts()
    {
        var (spec, word, sheet) = Mixed(); var f = new Fixture();
        var proposal = await f.Generate(spec, Notes());
        Assert.Equal(1, f.Compiles); Assert.Equal(spec.Snapshot.Digest, proposal.Compilation.Snapshot);
        Assert.Equal(word.CopyEvidenceBytes(), spec.Extractions[1].CopyEvidenceBytes());
        Assert.Equal(sheet.CopyEvidenceBytes(), spec.Extractions[2].CopyEvidenceBytes());
        Assert.Equal(word.Identity, spec.Extractions[1].Identity);
        Assert.Equal(sheet.Code, spec.Extractions[2].Code);
        Assert.IsType<SpecEvidenceLocator.ByteRange>(spec.Extractions[0].Chunks[0].Locator);
        Assert.IsType<SpecEvidenceLocator.WordParagraph>(spec.Extractions[1].Chunks[1].Locator);
        var cells = spec.Extractions[2].Chunks;
        Assert.Equal("1+2", cells[0].Formula); Assert.Equal("3", cells[0].Text);
        Assert.True(Assert.IsType<SpecEvidenceLocator.SheetCell>(cells[0].Locator).HasCachedValue);
        Assert.Equal("4+5", cells[1].Formula); Assert.Equal("", cells[1].Text);
        Assert.False(Assert.IsType<SpecEvidenceLocator.SheetCell>(cells[1].Locator).HasCachedValue);
        Assert.All(spec.Extractions.SelectMany(e => e.Chunks), c => Assert.False(c.MissingText));
        var assumptions = proposal.Notes.Assumptions.Where(a => a.Critical).ToArray();
        Assert.Equal(2, assumptions.Length);
        Assert.Contains(assumptions, a => a.Text.Contains("body paragraphs and table-cell text", StringComparison.Ordinal));
        Assert.Contains(assumptions, a => a.Text.Contains("cached values may be missing or stale", StringComparison.Ordinal));
        Assert.All(assumptions, a => { Assert.Equal(64, a.Id.Length); Assert.All(a.Id, c => Assert.True(char.IsAsciiLetterOrDigit(c))); });
        spec.Extractions[1].CopyEvidenceBytes()[0] ^= 1;
        Assert.Equal(word.CopyEvidenceBytes(), spec.Extractions[1].CopyEvidenceBytes());
        Assert.Throws<NotSupportedException>(() => ((IList<CreationEvidenceChunk>)spec.Extractions[1].Chunks).Clear());
    }

    [Fact]
    public async Task Complete_v1_constructor_bytes_notes_and_original_fingerprint_are_unchanged()
    {
        var source = Text(); var extraction = SpecSourceExtractor.Extract(source);
        var legacy = new CreationSpecification(Bundle, [source], [extraction]);
        var adapted = new CreationSpecification(Bundle, [source], new[] { CreationEvidenceDocument.From(extraction) });
        Assert.Equal("[\"weft.spec-evidence.v1\",\"complete\",[0,\"Map input to output\",null,0,19,1,1]]", Encoding.UTF8.GetString(adapted.Extractions[0].CopyEvidenceBytes()));
        Assert.Equal(new SpecBundleSnapshot(Bundle, [extraction.Identity]).Digest, adapted.Snapshot.Digest);
        var notes = Notes(); var f = new Fixture();
        var a = await f.Generate(legacy, notes); var b = await f.Generate(adapted, notes);
        Assert.Same(notes, a.Notes); Assert.Same(notes, b.Notes); Assert.Equal(a.Fingerprint, b.Fingerprint);
        // Frozen pre-wrapper v1 fingerprint expression; neither evidence projection
        // nor synthesized-note machinery may change the legacy canonical payload.
        var old = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "weft.mapping-creation-proposal.v1", author = a.Author, id = a.Id, key = a.RequestKey,
            a.Project, snapshot = new SpecBundleSnapshot(Bundle, [extraction.Identity]).Digest.Value,
            plan = a.Plan.Digest.Value, compilerPlan = a.Plan.CompilerDigest.Value, notes = notes.Digest.Value,
            native = a.Compilation.NativeDigest.Value, a.Compilation.EngineRevision, a.Compilation.WorkerBuild,
            validation = a.Compilation.ValidationDigest.Value, projection = a.Compilation.ProjectionDigest.Value,
            a.Request.BackendId, a.Request.OriginalFilename, a.Request.Metadata.Name, a.Request.Metadata.Description
        }));
        Assert.Equal(old, a.Fingerprint);
    }

    [Fact]
    public async Task Coverage_is_deterministic_replayable_and_bound_to_the_whole_extraction_identity()
    {
        var (spec, _, _) = Mixed(); var f = new Fixture(); var a = await f.Generate(spec, Notes());
        var reverse = new CreationSpecification(Bundle, spec.Sources.Reverse(), spec.Extractions.Reverse());
        Assert.Equal(a.Fingerprint, (await f.Generate(reverse, Notes())).Fingerprint);
        var reorderedReplay = new CreationGenerationNotes(a.Notes.Evidence, a.Notes.Assumptions.Reverse(), a.Notes.Questions);
        Assert.Equal(a.Fingerprint, (await f.Generate(spec, reorderedReplay)).Fingerprint);
        var word = spec.Sources[1];
        var differentProfile = SpecOfficeParser.Extract(word, new(maximumChunks: 9999));
        var changed = new CreationSpecification(Bundle, spec.Sources, new[] { spec.Extractions[0], CreationEvidenceDocument.From(differentProfile), spec.Extractions[2] });
        var b = await f.Generate(changed, Notes());
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Notes.Assumptions[1].Id, b.Notes.Assumptions[1].Id);
        Assert.False(new CreationApprovalDecision(b.Fingerprint, a.Notes.Assumptions.Where(x => x.Critical).Select(x => x.Id)).Matches(b));
    }

    [Fact]
    public async Task Synthesized_assumptions_cannot_be_downgraded_reworded_omitted_or_exceed_notes_budget()
    {
        var (spec, _, _) = Mixed(); var f = new Fixture(); var p = await f.Generate(spec, Notes());
        var generated = p.Notes.Assumptions.First(a => a.Critical);
        foreach (var collision in new[] { generated with { Critical = false }, generated with { Text = "Complete coverage" } })
        {
            var before = f.Compiles;
            await Assert.ThrowsAsync<ArgumentException>(() => f.Generate(spec, new(Notes().Evidence, [collision], [])));
            Assert.Equal(before, f.Compiles);
        }
        var tooMany = new CreationGenerationNotes(Notes().Evidence, Enumerable.Range(0, 63).Select(i => new CreationAssumption($"A{i}", "note", false)), []);
        var count = f.Compiles;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Generate(spec, tooMany)); Assert.Equal(count, f.Compiles);
        var omitted = await f.Generate(spec, new(Notes().Evidence, [], []));
        Assert.Equal(2, omitted.Notes.Assumptions.Count); Assert.All(omitted.Notes.Assumptions, a => Assert.True(a.Critical));
    }

    [Fact]
    public async Task Every_selected_partial_including_uncited_empty_documents_requires_exact_approval()
    {
        var text = Text(); var word = Office(5, empty: true); var office = SpecOfficeParser.Extract(word);
        Assert.Empty(office.Chunks); Assert.Equal(SpecExtractionOutcome.Partial, office.Outcome);
        var spec = new CreationSpecification(Bundle, [text, word], new[] { CreationEvidenceDocument.From(SpecSourceExtractor.Extract(text)), CreationEvidenceDocument.From(office) });
        var f = new Fixture(); var p = await f.Generate(spec, Notes()); f.State = new(p);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Approval.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Approval.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint, new(p.Fingerprint, ["wrong"])));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Approval.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint,
            new(Sha256Digest.FromBytes("wrong"u8), p.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id))));
        Assert.Equal(0, f.Acquires);
        await f.Approval.ApproveAsync(f.Actor, p.Project, p.Id, p.Fingerprint, new(p.Fingerprint, p.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id)));
        Assert.Equal(1, f.Acquires);
        var blocked = await f.Generate(spec, Notes(question: true)); f.State = new(blocked); f.Acquires = 0;
        await f.Approval.ApproveAsync(f.Actor, blocked.Project, blocked.Id, blocked.Fingerprint,
            new(blocked.Fingerprint, blocked.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id)));
        Assert.Equal(0, f.Acquires); Assert.True(blocked.Notes.HasCriticalQuestions);
    }

    [Fact]
    public async Task Refusal_outcomes_wrong_sources_digests_and_chunk_ordinals_never_dispatch()
    {
        var (spec, _, _) = Mixed(); var f = new Fixture();
        var changed = new SpecCapturedSource(spec.Sources[1].Identity, SpecSourceFormat.Docx, "wrong"u8);
        Assert.Throws<ArgumentException>(() => new CreationSpecification(Bundle, [spec.Sources[0], changed, spec.Sources[2]], spec.Extractions));
        var foreign = new SpecCapturedSource(new(new(Bundle.Project, Id(90)), Id(91), 1), SpecSourceFormat.Docx, spec.Sources[1].CopyBytes());
        Assert.Throws<ArgumentException>(() => new CreationSpecification(Bundle, [foreign], new[] { CreationEvidenceDocument.From(SpecOfficeParser.Extract(foreign)) }));
        foreach (var reference in new[] { new CreationEvidenceReference(new(Bundle, Id(99), 1), 0), new(spec.Sources[1].Identity, 99) })
            await Assert.ThrowsAsync<ArgumentException>(() => f.Generate(spec, new([reference], [], [])));
        var malformed = SpecOfficeParser.Extract(changed);
        var limited = SpecOfficeParser.Extract(spec.Sources[1], new(maximumSourceBytes: 1));
        var unavailable = await new UnavailableSpecOfficeExtractionWorker().ExtractAsync(spec.Sources[1], new(), default);
        foreach (var (source, result) in new[] { (changed, malformed), (spec.Sources[1], limited), (spec.Sources[1], unavailable) })
        {
            var refused = new CreationSpecification(Bundle, [spec.Sources[0], source], new[] { spec.Extractions[0], CreationEvidenceDocument.From(result) });
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate(refused, Notes()));
        }
        var byteOffice = new CreationSpecification(Bundle, [spec.Sources[0], spec.Sources[1]],
            [SpecSourceExtractor.Extract(spec.Sources[0]), SpecSourceExtractor.Extract(spec.Sources[1])]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate(byteOffice, Notes()));
        Assert.Equal(0, f.Compiles);
    }

    [Fact]
    public void Wrappers_are_closed_to_json_and_caller_minted_identity_or_chunks()
    {
        Assert.True(typeof(CreationEvidenceDocument).IsSealed); Assert.Empty(typeof(CreationEvidenceDocument).GetConstructors());
        Assert.True(typeof(CreationEvidenceChunk).IsSealed); Assert.Empty(typeof(CreationEvidenceChunk).GetConstructors());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<CreationEvidenceDocument>("{\"Identity\":{},\"Chunks\":[]}"));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<CreationEvidenceChunk>("{\"Ordinal\":0,\"Text\":\"invented\"}"));
    }

    [Fact]
    public void Existing_source_count_source_byte_and_canonical_evidence_budgets_remain_enforced()
    {
        var many = Enumerable.Range(10, 21).Select(i => Text(i)).ToArray();
        Assert.Throws<ArgumentException>(() => new CreationSpecification(Bundle, many, many.Select(s => SpecSourceExtractor.Extract(s))));
        var large = Enumerable.Range(10, 3).Select(i => Source(i, SpecSourceFormat.Text, new byte[7 * 1024 * 1024])).ToArray();
        Assert.Throws<ArgumentException>(() => new CreationSpecification(Bundle, large, large.Select(s => SpecSourceExtractor.Extract(s))));
        var escaped = Enumerable.Range(10, 11).Select(i => Source(i, SpecSourceFormat.Text, Encoding.UTF8.GetBytes(new string('"', 330_000)))).ToArray();
        var results = escaped.Select(s => SpecSourceExtractor.Extract(s)).ToArray();
        Assert.All(results, r => Assert.Equal(SpecExtractionOutcome.Complete, r.Outcome));
        Assert.True(results.Sum(r => (long)r.CopyEvidenceBytes().Length) > 20 * 1024 * 1024);
        Assert.Throws<ArgumentException>(() => new CreationSpecification(Bundle, escaped, results));
    }

    internal static async Task<SpecPdfExtractionResult> ExtractPdf(SpecCapturedSource source, int cap = 8 * 1024 * 1024)
    {
        var child = new SpecPdfProcessTests.Child();
        var wire = await child.Run(source.CopyBytes());
        Assert.True(child.Reaped && child.Joined);
        var executable = Environment.GetEnvironmentVariable("WEFT_TEST_PDF_EXECUTABLE")!;
        return SpecPdfExtractionResult.Decode(wire.Output, wire.Exit, source,
            new(Sha256Digest.FromBytes(File.ReadAllBytes(executable)), cap));
    }

    [Fact]
    public async Task Real_pdf_mixed_generation_preserves_every_page_and_requires_exact_coverage_acknowledgement()
    {
        var (office, _, _) = Mixed();
        var source = Source(7, SpecSourceFormat.Pdf, SpecPdfProcessTests.Pdf(["First page", "", "Third page"]));
        var result = await ExtractPdf(source); var document = CreationEvidenceDocument.From(result);
        var spec = new CreationSpecification(Bundle, office.Sources.Append(source), office.Extractions.Append(document));
        Assert.Equal(result.Identity, document.Identity); Assert.Equal(result.Code, document.Code);
        Assert.Equal(result.CopyEvidenceBytes(), document.CopyEvidenceBytes());
        Assert.Equal(new[] { 0, 1, 2 }, document.Chunks.Select(c => c.Ordinal));
        Assert.Equal(new[] { 1, 2, 3 }, document.Chunks.Select(c => Assert.IsType<SpecEvidenceLocator.PdfPage>(c.Locator).Page));
        Assert.Equal(new[] { false, true, false }, document.Chunks.Select(c => c.MissingText));
        Assert.Equal("", document.Chunks[1].Text); Assert.All(document.Chunks, c => Assert.Null(c.Formula));
        var f = new Fixture();
        // The PDF is selected but uncited, so its coverage acknowledgement cannot be avoided.
        var proposal = await f.Generate(spec, Notes());
        var coverage = proposal.Notes.Assumptions.Last();
        Assert.True(coverage.Critical);
        Assert.Equal("This PDF extraction contains only page text. Text or page content may be missing, including pages without usable text. OCR was not performed. It does not establish complete document coverage or rendered layout.", coverage.Text);
        Assert.Equal(3, proposal.Notes.Assumptions.Count(a => a.Critical));
        Assert.Equal(proposal.Fingerprint, (await f.Generate(spec, proposal.Notes)).Fingerprint);
        f.State = new(proposal);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Approval.ApproveAsync(f.Actor, proposal.Project, proposal.Id, proposal.Fingerprint,
            new(proposal.Fingerprint, proposal.Notes.Assumptions.Where(a => a.Critical && a.Id != coverage.Id).Select(a => a.Id))));
        Assert.Equal(0, f.Acquires);
        await f.Approval.ApproveAsync(f.Actor, proposal.Project, proposal.Id, proposal.Fingerprint,
            new(proposal.Fingerprint, proposal.Notes.Assumptions.Where(a => a.Critical).Select(a => a.Id)));
        Assert.Equal(1, f.Acquires);
        // Empty pages remain valid exact citations rather than disappearing during adaptation.
        await f.Generate(spec, new([new(source.Identity, 1)], [], []));
        var changed = CreationEvidenceDocument.From(await ExtractPdf(source, 1024 * 1024));
        var changedSpec = new CreationSpecification(Bundle, spec.Sources, office.Extractions.Append(changed));
        var changedProposal = await f.Generate(changedSpec, Notes());
        Assert.NotEqual(coverage.Id, changedProposal.Notes.Assumptions.Last().Id);
        Assert.NotEqual(proposal.Fingerprint, changedProposal.Fingerprint);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Generate(spec, new(Notes().Evidence, [coverage with { Critical = false }], [])));
        document.CopyEvidenceBytes()[0] ^= 1;
        Assert.Equal(result.CopyEvidenceBytes(), document.CopyEvidenceBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_unreadable_pdf_refusals_never_dispatch_creation(bool encrypted)
    {
        var bytes = encrypted ? Convert.FromBase64String(EncryptedPdfFixture) : SpecPdfProcessTests.Pdf(["", ""]);
        var source = Source(7, SpecSourceFormat.Pdf, bytes); var result = await ExtractPdf(source);
        Assert.Equal(SpecExtractionOutcome.Unsupported, result.Outcome);
        Assert.Equal(encrypted ? "pdf-encrypted" : "pdf-needs-ocr", result.Code);
        var document = CreationEvidenceDocument.From(result);
        Assert.Equal(result.CopyEvidenceBytes(), document.CopyEvidenceBytes());
        if (encrypted) Assert.Empty(document.Chunks);
        else { Assert.Equal(2, document.Chunks.Count); Assert.All(document.Chunks, c => Assert.True(c.MissingText)); }
        var text = Text();
        var spec = new CreationSpecification(Bundle, [text, source], new[] { CreationEvidenceDocument.From(SpecSourceExtractor.Extract(text)), document });
        var f = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Generate(spec, Notes()));
        Assert.Equal(0, f.Compiles);
    }

    // Password-protected PDF generated with lopdf 0.42 EncryptionVersion.V2,
    // fixture-only owner/reader passwords, one blank page and a fixed document ID.
    private const string EncryptedPdfFixture = "JVBERi0xLjQKJbutwN4KMSAwIG9iago8PC9UeXBlL1BhZ2VzL0tpZHNbMyAwIFJdL0NvdW50IDE+PgplbmRvYmoKMiAwIG9iago8PC9MZW5ndGggMD4+c3RyZWFtCgplbmRzdHJlYW0gCmVuZG9iagozIDAgb2JqCjw8L1R5cGUvUGFnZS9QYXJlbnQgMSAwIFIvTWVkaWFCb3hbMCAwIDYxMiA3OTJdL0NvbnRlbnRzIDIgMCBSPj4KZW5kb2JqCjQgMCBvYmoKPDwvVHlwZS9DYXRhbG9nL1BhZ2VzIDEgMCBSPj4KZW5kb2JqCjUgMCBvYmoKPDwvRmlsdGVyL1N0YW5kYXJkL1YgMi9SIDMvTGVuZ3RoIDEyOC9PKEFO6Z8VHqjqKhePEckx1T8dUcd5pQL3RxVcXPBqM3VFKikvVSh0NN5yn8cjkpe05nZrbXMk4csPzUkvXeliIIgStSIifSkvUCAtMzkwMD4+CmVuZG9iago2IDAgb2JqCjw8L1Jvb3QgNCAwIFIvSURbKHRlc3QtZG9jdW1lbnQtaWQpKHRlc3QtZG9jdW1lbnQtaWQpXS9FbmNyeXB0IDUgMCBSL1R5cGUvWFJlZi9TaXplIDcvV1sxIDQgMl0vSW5kZXhbMSA2XS9MZW5ndGggNDI+PnN0cmVhbQoBAAAADwAAAQAAAEIAAAEAAABxAAABAAAAwQAAAQAAAO4AAAEAAAF2AAAKZW5kc3RyZWFtIAplbmRvYmoKCnN0YXJ0eHJlZgozNzQKJSVFT0Y=";

    private sealed class Fixture : IMappingCreationCompiler, IMappingCreationAuthority, IMappingCreationProposalStore,
        IMappingCreationApprovalStore, IIngestionWorkflowReader
    {
        internal readonly ActorContext Actor = new(new(Id(7)));
        internal int Compiles, Acquires;
        internal MappingCreationState? State;
        internal MappingCreationApprovalService Approval => new(this, this, null!, this);
        internal Task<MappingCreationProposal> Generate(CreationSpecification spec, CreationGenerationNotes notes) =>
            new MappingCreationGenerationService(this, this).GenerateAsync(Actor, Id(8), Id(9), spec,
                new([new("input", CreationScalarType.Text, true)], [new("output", CreationScalarType.Text, true)], [new("input", "output")]),
                notes, new("Mapping", "Description"));
        public Task<CreationCompilationResult> CompileAsync(ProjectScope project, Guid authoringId, MappingCreationPlan plan, SpecBundleSnapshot snapshot, CancellationToken ct)
        {
            Compiles++;
            return Task.FromResult(new CreationCompilationResult(new(project, authoringId, plan.CompilerDigest, snapshot.Digest,
                "fixture-engine", "fixture-worker", "compiled fixture"u8, "{}"u8, "{}"u8), null));
        }
        public Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct) => Task.FromResult(CreationAuthority.Current);
        public Task<MappingCreationState?> RetainAsync(ActorContext actor, MappingCreationProposal proposal, CancellationToken ct) => throw new NotSupportedException();
        public Task<MappingCreationState?> ReadAsync(ActorContext actor, ProjectScope project, Guid proposalId, CancellationToken ct) => Task.FromResult(State);
        public Task<IMappingCreationApprovalLease?> AcquireAsync(ActorContext actor, ProjectScope project, Guid proposalId, Sha256Digest fingerprint, CreationApprovalDecision? decision, CancellationToken ct)
        { Acquires++; return Task.FromResult<IMappingCreationApprovalLease?>(null); }
        public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project, IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult<IngestionReceipt?>(null);
        public Task<RevisionIngestionReceipt?> GetOwnReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
