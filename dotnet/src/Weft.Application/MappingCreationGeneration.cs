using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Actual captured sources and extracted evidence, not just a caller-supplied snapshot hash.</summary>
public sealed class CreationSpecification
{
    public CreationSpecification(SpecBundleScope bundle, IEnumerable<SpecCapturedSource> sources, IEnumerable<SpecExtractionResult> extractions)
    {
        var captured = sources.Take(SpecBundleSnapshot.MaximumSources + 1).ToArray();
        var extracted = extractions.Take(SpecBundleSnapshot.MaximumSources + 1).ToArray();
        Snapshot = new(bundle, extracted.Select(e => e.Identity));
        if (captured.Length != extracted.Length || captured.Select(s => s.Identity).Distinct().Count() != captured.Length ||
            captured.Sum(s => (long)s.ByteCount) > 20 * 1024 * 1024 ||
            extracted.Sum(e => (long)e.CopyEvidenceBytes().Length) > 20 * 1024 * 1024 ||
            extracted.Any(e => !captured.Any(s => s.Identity == e.Identity.Source && s.Digest == e.Identity.SourceDigest && s.Format == e.Identity.Format)))
            throw new ArgumentException("Snapshot evidence must match every exact captured source.");
        Sources = Array.AsReadOnly(captured); Extractions = Array.AsReadOnly(extracted);
    }
    public SpecBundleSnapshot Snapshot { get; }
    public IReadOnlyList<SpecCapturedSource> Sources { get; }
    public IReadOnlyList<SpecExtractionResult> Extractions { get; }
}

/// <summary>Trusted compiler output. Only a registered implementation may provide native bytes; never deserialize this from model output.</summary>
public sealed class CreationCompilation
{
    private readonly byte[] native;
    private readonly byte[] validation, projection;
    // Only the trusted MappingBackends adapter (and tests) can mint this evidence from a verified checkpoint.
    internal CreationCompilation(ProjectScope project, Guid authoringId, Sha256Digest plan, Sha256Digest snapshot,
        string engineRevision, string workerBuild, ReadOnlySpan<byte> native, ReadOnlySpan<byte> validation, ReadOnlySpan<byte> projection)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(snapshot);
        if (authoringId == Guid.Empty || string.IsNullOrWhiteSpace(engineRevision) || engineRevision.Length > 256 ||
            string.IsNullOrWhiteSpace(workerBuild) || workerBuild.Length > 2048 || native.Length is < 1 or > 4 * 1024 * 1024 ||
            validation.Length is < 1 or > 8 * 1024 * 1024 || projection.Length is < 1 or > 8 * 1024 * 1024)
            throw new ArgumentException("Invalid compiler evidence.");
        Project = project; AuthoringId = authoringId; Plan = plan; Snapshot = snapshot;
        EngineRevision = engineRevision; WorkerBuild = workerBuild; this.native = native.ToArray();
        this.validation = validation.ToArray(); this.projection = projection.ToArray();
        ValidationDigest = Sha256Digest.FromBytes(this.validation); ProjectionDigest = Sha256Digest.FromBytes(this.projection);
        NativeDigest = Sha256Digest.FromBytes(this.native);
    }
    public ProjectScope Project { get; }
    public Guid AuthoringId { get; }
    public Sha256Digest Plan { get; }
    public Sha256Digest Snapshot { get; }
    public string EngineRevision { get; }
    public string WorkerBuild { get; }
    public Sha256Digest ValidationDigest { get; }
    public Sha256Digest ProjectionDigest { get; }
    public Sha256Digest NativeDigest { get; }
    public byte[] CopyNative() => (byte[])native.Clone();
    public byte[] CopyValidation() => (byte[])validation.Clone();
    public byte[] CopyProjection() => (byte[])projection.Clone();
}

/// <summary>Implemented by the trusted MappingBackends adapter; no host registration in this foundation. Compilation has no ingestion/catalog port.
/// Implementations compile only the bounded typed plan, verify their native output, and report exact input identities.</summary>
public interface IMappingCreationCompiler
{
    Task<CreationCompilationResult> CompileAsync(ProjectScope project, Guid authoringId, MappingCreationPlan plan, SpecBundleSnapshot snapshot, CancellationToken ct);
}

public sealed record CreationCompilationResult(CreationCompilation? Checkpoint, MappingEditFailure? Failure);

public enum CreationAuthority { Current = 1, Stale = 2, Revoked = 3 }

/// <summary>Fresh current publisher/content authority AND exact retained source/extraction versions, tombstones and project lifecycle.
/// Missing state denies. Never equate possession of a snapshot with permission. Rechecked after compiler awaits.</summary>
public interface IMappingCreationAuthority
{
    Task<CreationAuthority> CheckAsync(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct);
}

/// <summary>Only generation can construct proposals. This is a project creation identity, with no pretend base revision.</summary>
public sealed class MappingCreationProposal
{
    internal MappingCreationProposal(PrincipalId author, Guid id, Guid key, CreationSpecification specification,
        MappingCreationPlan plan, CreationGenerationNotes notes, CreationCompilation compilation, CreateRevisionRequest request)
    {
        if (id == Guid.Empty || key == Guid.Empty) throw new ArgumentException("Stable proposal and ingestion keys are required.");
        Author = author; Id = id; RequestKey = key; Specification = specification; Plan = plan; Notes = notes; Compilation = compilation; Request = request;
        Fingerprint = Sha256Digest.FromBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "weft.mapping-creation-proposal.v1",
            author,
            id,
            key,
            Project,
            snapshot = specification.Snapshot.Digest.Value,
            plan = plan.Digest.Value,
            compilerPlan = plan.CompilerDigest.Value,
            notes = notes.Digest.Value,
            native = compilation.NativeDigest.Value,
            compilation.EngineRevision,
            compilation.WorkerBuild,
            validation = compilation.ValidationDigest.Value,
            projection = compilation.ProjectionDigest.Value,
            request.BackendId,
            request.OriginalFilename,
            request.Metadata.Name,
            request.Metadata.Description
        }));
    }
    public PrincipalId Author { get; }
    public Guid Id { get; }
    public Guid RequestKey { get; }
    public ProjectScope Project => Specification.Snapshot.Bundle.Project;
    public CreationSpecification Specification { get; }
    public MappingCreationPlan Plan { get; }
    public CreationGenerationNotes Notes { get; }
    public CreationCompilation Compilation { get; }
    public CreateRevisionRequest Request { get; }
    public Sha256Digest Fingerprint { get; }
}

public sealed class MappingCreationGenerationService(IMappingCreationCompiler compiler, IMappingCreationAuthority authority)
{
    /// <summary>Retained generation/review only. Preview consumers use this same immutable proposal; neither can create a catalog mapping.</summary>
    public async Task<MappingCreationProposal> GenerateAsync(ActorContext actor, Guid proposalId, Guid requestKey,
        CreationSpecification specification, MappingCreationPlan plan, CreationGenerationNotes notes,
        IngestionCreateMetadata metadata, CancellationToken ct = default)
    {
        if (proposalId == Guid.Empty || requestKey == Guid.Empty) throw new ArgumentException("Stable identities are required.");
        foreach (var reference in notes.Evidence)
            if (!specification.Extractions.Any(e => e.Identity.Source == reference.Source && e.Chunks.Any(c => c.Ordinal == reference.ChunkOrdinal)))
                throw new ArgumentException("Generation evidence must cite the selected extraction.");
        if (specification.Extractions.Any(e => e.Outcome != SpecExtractionOutcome.Complete))
            throw new InvalidOperationException("Incomplete extraction cannot support generation.");
        await RequireCurrent(actor, specification.Snapshot, ct);
        var result = await compiler.CompileAsync(specification.Snapshot.Bundle.Project, proposalId, plan, specification.Snapshot, ct).ConfigureAwait(false);
        if (result is null || (result.Checkpoint is null) == (result.Failure is null))
            throw new InvalidOperationException("Compiler returned inconsistent evidence.");
        if (result.Failure is not null) throw new InvalidOperationException("Creation compilation did not produce a checkpoint.");
        var compiled = result.Checkpoint;
        if (compiled is null || compiled.Project != specification.Snapshot.Bundle.Project || compiled.AuthoringId != proposalId ||
            compiled.Plan != plan.CompilerDigest || compiled.Snapshot != specification.Snapshot.Digest)
            throw new InvalidOperationException("Compiler returned substituted input evidence.");
        await RequireCurrent(actor, specification.Snapshot, ct);
        return new(actor.PrincipalId, proposalId, requestKey, specification, plan, notes, compiled, new(metadata, "ferrule"));
    }
    private async Task RequireCurrent(ActorContext actor, SpecBundleSnapshot snapshot, CancellationToken ct)
    {
        if (await authority.CheckAsync(actor, snapshot, ct).ConfigureAwait(false) != CreationAuthority.Current)
            throw new InvalidOperationException("Creation authority is no longer current.");
    }
}
