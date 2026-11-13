using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weft.Application;
using Weft.Domain;
using Weft.MappingBackends;

namespace Weft.Tests;

public sealed class CreationEvidenceStorageTests
{
    private static MappingCreationPlan TypedPlan()
    {
        CreationBoundary Boundary(string name) => new(new(name, null, [new("value", CreationValueType.String)]), new CreationJsonOptions());
        return new(new CreationPlanV2(Boundary("Input"), Boundary("Output"), new(null, [new("value", new CreationSourceValue(["value"]))])));
    }

    private static MappingCreationProposal Proposal(CreationSpecification spec, bool legacy = false)
    {
        var plan = legacy ? new MappingCreationPlan([new("value", CreationScalarType.Text, true)], [new("value", CreationScalarType.Text, true)], [new("value", "value")]) : TypedPlan();
        var id = Guid.Parse("00000000-0000-0000-0000-000000000100");
        var notes = CreationEvidenceCoverage.Include(spec, new([new(spec.Extractions[0].Identity.Source, 0)], [], []));
        var compilation = new CreationCompilation(spec.Snapshot.Bundle.Project, id, plan.CompilerDigest, spec.Snapshot.Digest,
            "fixture-engine", "fixture-worker", "retained native fixture"u8, "{}"u8, "{}"u8);
        return new(new(Guid.Parse("00000000-0000-0000-0000-000000000101")), id, Guid.Parse("00000000-0000-0000-0000-000000000102"),
            spec, plan, notes, compilation, new(new("Mapping", "Fixture"), "ferrule"));
    }

    private static MappingCreationProposal WithNotes(MappingCreationProposal p, CreationGenerationNotes notes) =>
        new(p.Author, p.Id, p.RequestKey, p.Specification, p.Plan, notes, p.Compilation, p.Request);

    private static List<byte[]> Blocks(byte[] storage)
    {
        using var reader = new BinaryReader(new MemoryStream(storage)); var result = new List<byte[]>();
        while (reader.BaseStream.Position < reader.BaseStream.Length) result.Add(reader.ReadBytes(reader.ReadInt32()));
        return result;
    }
    private static byte[] Storage(IEnumerable<byte[]> blocks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var block in blocks) { writer.Write(block.Length); writer.Write(block); }
        return stream.ToArray();
    }
    private static byte[] EditHeader(byte[] storage, Action<JsonNode> edit)
    {
        var blocks = Blocks(storage); var header = JsonNode.Parse(blocks[0])!; edit(header);
        blocks[0] = JsonSerializer.SerializeToUtf8Bytes(header); return Storage(blocks);
    }

    [Fact]
    public async Task Actual_office_and_pdf_roundtrip_typed_v2_without_reparsing_or_replacing_profile_pins()
    {
        var (office, _, _) = CreationEvidenceGenerationTests.Mixed();
        var pdfSource = new SpecCapturedSource(new(office.Snapshot.Bundle, Guid.NewGuid(), 1), SpecSourceFormat.Pdf,
            SpecPdfProcessTests.Pdf(["Readable", "", "Last page"]));
        var pdf = await CreationEvidenceGenerationTests.ExtractPdf(pdfSource, 1024 * 1024);
        var word = SpecOfficeParser.Extract(office.Sources[1], new(maximumChunks: 123));
        var sheet = SpecOfficeParser.Extract(office.Sources[2], new(maximumEvidenceBytes: 1024 * 1024));
        var spec = new CreationSpecification(office.Snapshot.Bundle, office.Sources.Append(pdfSource),
            new[] { office.Extractions[0], CreationEvidenceDocument.From(word), CreationEvidenceDocument.From(sheet), CreationEvidenceDocument.From(pdf) });
        var p = Proposal(spec);
        Assert.True(File.Exists(Environment.GetEnvironmentVariable("WEFT_AUTHORING_TEST_EXECUTABLE")), "Set WEFT_AUTHORING_TEST_EXECUTABLE to the native compiler fixture.");
        var compiler = new FerruleMappingCreationCompiler(new FerruleInspectionBackend(new MappingAuthoringBackendTests.NativeRunner()));
        var compiled = await compiler.CompileAsync(p.Project, p.Id, p.Plan, spec.Snapshot, default);
        Assert.Null(compiled.Failure); Assert.NotNull(compiled.Checkpoint);
        p = new(p.Author, p.Id, p.RequestKey, spec, p.Plan, p.Notes, compiled.Checkpoint!, p.Request);
        var bytes = MappingCreationStorageCodec.Encode(p); var copy = MappingCreationStorageCodec.Decode(bytes);
        Assert.Equal(p.Fingerprint, copy.Fingerprint); Assert.Equal(bytes, MappingCreationStorageCodec.Encode(copy));
        Assert.Equal(p.Compilation.CopyNative(), copy.Compilation.CopyNative());
        Assert.Equal(p.Plan.CopyBytes(), copy.Plan.CopyBytes());
        Assert.Equal("weft.creation-plan.v2", JsonDocument.Parse(copy.Plan.CopyBytes()).RootElement.GetProperty("schema").GetString());
        Assert.NotEqual(p.Plan.CopyCompilerPlanBytes(), copy.Plan.CopyBytes());
        Assert.Equal(p.Notes.Assumptions, copy.Notes.Assumptions);
        for (var i = 0; i < spec.Extractions.Count; i++)
        {
            var original = spec.Extractions[i]; var restored = copy.Specification.Extractions[i];
            Assert.Equal(original.Identity, restored.Identity); Assert.Equal(original.Code, restored.Code);
            Assert.Equal(original.CopyEvidenceBytes(), restored.CopyEvidenceBytes());
            Assert.Equal(original.Chunks.Select(c => (c.Ordinal, c.Text, c.Locator, c.Formula, c.MissingText)),
                restored.Chunks.Select(c => (c.Ordinal, c.Text, c.Locator, c.Formula, c.MissingText)));
        }
        Assert.True(copy.Specification.Extractions[3].Chunks[1].MissingText);
        Assert.Equal("", copy.Specification.Extractions[3].Chunks[1].Text);
        Assert.NotEqual(new SpecOfficeExtractionLimits().Digest, copy.Specification.Extractions[1].Identity.ProfileDigest);
    }

    [Fact]
    public void Complete_legacy_storage_remains_exact_v1_and_keeps_fingerprint()
    {
        var (mixed, _, _) = CreationEvidenceGenerationTests.Mixed();
        var spec = new CreationSpecification(mixed.Snapshot.Bundle, [mixed.Sources[0]], [mixed.Extractions[0]]);
        var p = Proposal(spec, legacy: true); var encoded = MappingCreationStorageCodec.Encode(p);
        var frozen = FrozenLegacyStorage(p);
        Assert.Equal(frozen, encoded);
        Assert.Equal(p.Fingerprint, MappingCreationStorageCodec.Decode(frozen).Fingerprint);
    }

    private static byte[] FrozenLegacyStorage(MappingCreationProposal p)
    {
        var spec = p.Specification;
        var identity = spec.Extractions[0].Identity;
        // Frozen pre-mixed codec header and block order. Digest strings match the old converter.
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            p.Author,
            p.Id,
            Key = p.RequestKey,
            Bundle = spec.Snapshot.Bundle,
            Sources = new[] { new { Identity = new { identity.Source, SourceDigest = identity.SourceDigest.Value, identity.Format,
                identity.ExtractorVersion, ProfileDigest = identity.ProfileDigest.Value, identity.Outcome, EvidenceDigest = identity.EvidenceDigest.Value } } },
            Evidence = p.Notes.Evidence.ToArray(),
            Assumptions = p.Notes.Assumptions.ToArray(),
            Questions = p.Notes.Questions.ToArray(),
            p.Request.Metadata,
            Backend = p.Request.BackendId,
            Filename = p.Request.OriginalFilename,
            Engine = p.Compilation.EngineRevision,
            Worker = p.Compilation.WorkerBuild,
            Fingerprint = p.Fingerprint.Value
        });
        return Storage([header, p.Plan.CopyBytes(), spec.Sources[0].CopyBytes(), spec.Extractions[0].CopyEvidenceBytes(),
            p.Compilation.CopyNative(), p.Compilation.CopyValidation(), p.Compilation.CopyProjection()]);
    }

    [Theory]
    [InlineData('"', 400_000)]
    [InlineData('"', (10 * 1024 * 1024 - 128) / 6)]
    [InlineData('x', 2 * 1024 * 1024 - 64)]
    public void Genuine_legacy_evidence_roundtrips_decoded_and_escaped_serialized_boundaries(char character, int count)
    {
        var seed = CreationEvidenceGenerationTests.Mixed().Spec.Sources[0];
        var source = new SpecCapturedSource(seed.Identity, SpecSourceFormat.Text, Encoding.UTF8.GetBytes(new string(character, count)));
        var result = SpecSourceExtractor.Extract(source);
        Assert.Equal(SpecExtractionOutcome.Complete, result.Outcome);
        Assert.Equal(count + 64, Assert.Single(result.Chunks).ByteLength + 64);
        var evidence = result.CopyEvidenceBytes();
        if (character == '"') Assert.True(evidence.Length > 2 * 1024 * 1024);
        if (count > 1_000_000 && character == '"') Assert.InRange(evidence.Length, SpecCapturedSource.MaximumBytes - 128, SpecCapturedSource.MaximumBytes);
        var p = Proposal(new(source.Identity.Bundle, [source], [result]), legacy: true);
        var encoded = MappingCreationStorageCodec.Encode(p);
        Assert.Equal(FrozenLegacyStorage(p), encoded);
        var restored = MappingCreationStorageCodec.Decode(encoded);
        Assert.Equal(p.Fingerprint, restored.Fingerprint);
        Assert.Equal(result.Identity, restored.Specification.Extractions[0].Identity);
        Assert.Equal(evidence, restored.Specification.Extractions[0].CopyEvidenceBytes());
        Assert.Equal(encoded, MappingCreationStorageCodec.Encode(restored));
    }

    [Theory]
    [InlineData('"', (10 * 1024 * 1024 + 5) / 6, "serialized-evidence-bytes")]
    [InlineData('x', 2 * 1024 * 1024 - 63, "evidence-bytes")]
    public void Genuine_legacy_extractor_refuses_past_each_independent_budget(char character, int count, string code)
    {
        var seed = CreationEvidenceGenerationTests.Mixed().Spec.Sources[0];
        var source = new SpecCapturedSource(seed.Identity, SpecSourceFormat.Text, Encoding.UTF8.GetBytes(new string(character, count)));
        var result = SpecSourceExtractor.Extract(source);
        Assert.Equal(SpecExtractionOutcome.LimitExceeded, result.Outcome); Assert.Equal(code, result.Code);
        Assert.Throws<ArgumentException>(() => CreationEvidenceDocument.RestoreRetained(source, result.Identity, result.CopyEvidenceBytes()));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("sourceDigest")]
    [InlineData("evidenceDigest")]
    [InlineData("version")]
    [InlineData("outcome")]
    [InlineData("format")]
    [InlineData("citation")]
    public void Changed_retained_identity_or_citation_is_rejected(string mutation)
    {
        var p = Proposal(CreationEvidenceGenerationTests.Mixed().Spec);
        var bytes = EditHeader(MappingCreationStorageCodec.Encode(p), h =>
        {
            var identity = h["Sources"]![1]!["Identity"]!;
            switch (mutation)
            {
                case "profile": identity["ProfileDigest"] = new string('0', 64); break;
                case "sourceDigest": identity["SourceDigest"] = new string('0', 64); break;
                case "evidenceDigest": identity["EvidenceDigest"] = new string('0', 64); break;
                case "version": identity["ExtractorVersion"] = 99; break;
                case "outcome": identity["Outcome"] = (int)SpecExtractionOutcome.Complete; break;
                case "format": identity["Format"] = (int)SpecSourceFormat.Pdf; break;
                case "citation": h["Evidence"]![0]!["ChunkOrdinal"] = 9999; break;
            }
        });
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_downgraded_coverage_cannot_restore_even_with_matching_crafted_fingerprint(bool downgrade)
    {
        var p = Proposal(CreationEvidenceGenerationTests.Mixed().Spec);
        var badNotes = new CreationGenerationNotes(p.Notes.Evidence,
            downgrade ? p.Notes.Assumptions.Select(a => a with { Critical = false }) : [], p.Notes.Questions);
        var crafted = WithNotes(p, badNotes);
        Assert.Throws<ArgumentException>(() => MappingCreationStorageCodec.Encode(crafted));
        var storage = EditHeader(MappingCreationStorageCodec.Encode(p), h =>
        {
            h["Assumptions"] = JsonSerializer.SerializeToNode(badNotes.Assumptions);
            h["Fingerprint"] = crafted.Fingerprint.Value;
        });
        Assert.Throws<ArgumentException>(() => MappingCreationStorageCodec.Decode(storage));
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("locator")]
    [InlineData("cache")]
    [InlineData("formula")]
    [InlineData("duplicate")]
    [InlineData("noncanonical")]
    public void Retained_office_canonical_grammar_rejects_rehashed_forgery(string mutation)
    {
        var spec = CreationEvidenceGenerationTests.Mixed().Spec; var source = spec.Sources[2]; var extraction = spec.Extractions[2];
        var node = JsonNode.Parse(extraction.CopyEvidenceBytes())!;
        switch (mutation)
        {
            case "ordinal": node[2]![0] = 1; break;
            case "locator": node[2]![2]![0] = "pdf-page"; break;
            case "cache": node[2]![2]![5] = false; break;
            case "formula": node[2]![3] = null; break;
            case "duplicate": node[3]![2] = node[2]![2]!.DeepClone(); break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        if (mutation == "noncanonical") bytes = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(bytes));
        var old = extraction.Identity;
        var identity = new SpecExtractionIdentity(old.Source, old.SourceDigest, old.Format, old.ExtractorVersion, old.ProfileDigest, old.Outcome, Sha256Digest.FromBytes(bytes));
        Assert.ThrowsAny<Exception>(() => CreationEvidenceDocument.RestoreRetained(source, identity, bytes));
    }

    [Fact]
    public async Task Refusals_and_storage_or_plan_boundary_substitutions_are_rejected()
    {
        var mixed = CreationEvidenceGenerationTests.Mixed().Spec;
        var refusal = await new UnavailableSpecOfficeExtractionWorker().ExtractAsync(mixed.Sources[1], new(), default);
        var spec = new CreationSpecification(mixed.Snapshot.Bundle, mixed.Sources.Take(2),
            new[] { mixed.Extractions[0], CreationEvidenceDocument.From(refusal) });
        Assert.Throws<ArgumentException>(() => MappingCreationStorageCodec.Encode(Proposal(spec)));
        Assert.Throws<ArgumentException>(() => MappingCreationStorageCodec.Decode(new byte[MappingCreationStorageCodec.MaximumBytes + 1]));
        var p = Proposal(mixed); var bytes = MappingCreationStorageCodec.Encode(p);
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(bytes[..^1]));
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        var blocks = Blocks(bytes); blocks[1] = p.Plan.CopyCompilerPlanBytes();
        Assert.ThrowsAny<Exception>(() => MappingCreationStorageCodec.Decode(Storage(blocks)));
    }

    [Fact]
    public async Task Retained_pdf_canonical_grammar_refuses_rehashed_missing_text_formula_and_page_substitutions()
    {
        var bundle = CreationEvidenceGenerationTests.Mixed().Spec.Snapshot.Bundle;
        var source = new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Pdf, SpecPdfProcessTests.Pdf(["Text", ""]));
        var result = await CreationEvidenceGenerationTests.ExtractPdf(source);
        foreach (var mutation in new[] { "missing", "formula", "page", "allblank" })
        {
            var node = JsonNode.Parse(result.CopyEvidenceBytes())!;
            if (mutation == "missing") node[3]![4] = false;
            if (mutation == "formula") node[2]![3] = "invented";
            if (mutation == "page") node[3]![2]![1] = 3;
            if (mutation == "allblank") { node[2]![1] = ""; node[2]![4] = true; }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(node); var old = result.Identity;
            var identity = new SpecExtractionIdentity(old.Source, old.SourceDigest, old.Format, old.ExtractorVersion, old.ProfileDigest, old.Outcome, Sha256Digest.FromBytes(bytes));
            Assert.ThrowsAny<Exception>(() => CreationEvidenceDocument.RestoreRetained(source, identity, bytes));
        }
    }
}
