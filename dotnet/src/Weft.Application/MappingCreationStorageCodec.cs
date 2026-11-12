using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Internal trusted-store codec. Restoration is not compilation or public native admission.</summary>
internal static class MappingCreationStorageCodec
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
        Converters = { new DigestConverter() }
    };
    private sealed class DigestConverter : JsonConverter<Sha256Digest>
    {
        public override Sha256Digest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Sha256Digest.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, Sha256Digest value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
    private sealed record Source(SpecExtractionIdentity Identity);
    private sealed record Header(int Version, PrincipalId Author, Guid Id, Guid Key, SpecBundleScope Bundle,
        Source[] Sources, CreationEvidenceReference[] Evidence, CreationAssumption[] Assumptions, CreationQuestion[] Questions,
        IngestionCreateMetadata Metadata, string Backend, string Filename, string Engine, string Worker, string Fingerprint);

    internal static byte[] Encode(MappingCreationProposal p)
    {
        if (p.Compilation.Project != p.Project || p.Compilation.AuthoringId != p.Id || p.Compilation.Plan != p.Plan.CompilerDigest ||
            p.Compilation.Snapshot != p.Specification.Snapshot.Digest || p.Specification.Extractions.Any(e => e.Outcome != SpecExtractionOutcome.Complete))
            throw new ArgumentException("Substituted compiler or snapshot identity.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        void Block(byte[] bytes)
        {
            if (bytes.Length > MaximumBytes - stream.Length - sizeof(int))
                throw new ArgumentException("Creation storage budget exceeded.");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        Block(JsonSerializer.SerializeToUtf8Bytes(new Header(1, p.Author, p.Id, p.RequestKey, p.Specification.Snapshot.Bundle,
            p.Specification.Extractions.Select(e => new Source(e.Identity)).ToArray(), p.Notes.Evidence.ToArray(),
            p.Notes.Assumptions.ToArray(), p.Notes.Questions.ToArray(), p.Request.Metadata, p.Request.BackendId,
            p.Request.OriginalFilename, p.Compilation.EngineRevision, p.Compilation.WorkerBuild, p.Fingerprint.Value), Options));
        Block(p.Plan.CopyBytes());
        foreach (var extraction in p.Specification.Extractions)
        {
            Block(p.Specification.Sources.Single(s => s.Identity == extraction.Identity.Source).CopyBytes());
            Block(extraction.CopyEvidenceBytes());
        }
        Block(p.Compilation.CopyNative()); Block(p.Compilation.CopyValidation()); Block(p.Compilation.CopyProjection());
        if (stream.Length > MaximumBytes) throw new ArgumentException("Creation storage budget exceeded.");
        return stream.ToArray();
    }

    internal static MappingCreationProposal Decode(byte[] bytes)
    {
        if (bytes.Length is < 4 or > MaximumBytes) throw new ArgumentException("Invalid creation storage length.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        byte[] Block(int maximum)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum || count > stream.Length - stream.Position) throw new ArgumentException("Invalid creation block.");
            return reader.ReadBytes(count);
        }
        var header = Block(1024 * 1024);
        // Canonical roundtrip also refuses duplicate properties and alternate spellings/unknown fields.
        var h = JsonSerializer.Deserialize<Header>(header, Options) ?? throw new ArgumentException("Missing creation header.");
        if (!JsonSerializer.SerializeToUtf8Bytes(h, Options).AsSpan().SequenceEqual(header) || h.Version != 1 ||
            h.Sources.Length is < 1 or > 20 || h.Backend != "ferrule") throw new ArgumentException("Invalid creation header.");
        var plan = MappingCreationPlan.Parse(Block(MappingCreationPlan.MaximumBytes));
        var sources = new List<SpecCapturedSource>(); var extractions = new List<SpecExtractionResult>();
        foreach (var source in h.Sources)
        {
            sources.Add(new(source.Identity.Source, source.Identity.Format, Block(SpecCapturedSource.MaximumBytes)));
            extractions.Add(new(source.Identity, Block(SpecCapturedSource.MaximumBytes)));
        }
        var specification = new CreationSpecification(h.Bundle, sources, extractions);
        var notes = new CreationGenerationNotes(h.Evidence, h.Assumptions, h.Questions);
        foreach (var citation in notes.Evidence)
            if (!extractions.Any(e => e.Identity.Source == citation.Source && e.Chunks.Any(c => c.Ordinal == citation.ChunkOrdinal)))
                throw new ArgumentException("Invalid retained citation.");
        var compilation = new CreationCompilation(h.Bundle.Project, h.Id, plan.CompilerDigest, specification.Snapshot.Digest,
            h.Engine, h.Worker, Block(4 * 1024 * 1024), Block(8 * 1024 * 1024), Block(8 * 1024 * 1024));
        var proposal = new MappingCreationProposal(h.Author, h.Id, h.Key, specification, plan, notes, compilation, new(h.Metadata, h.Backend, h.Filename));
        if (stream.Position != stream.Length || proposal.Fingerprint.Value != h.Fingerprint || !Encode(proposal).AsSpan().SequenceEqual(bytes))
            throw new ArgumentException("Substituted creation storage content.");
        return proposal;
    }
}
