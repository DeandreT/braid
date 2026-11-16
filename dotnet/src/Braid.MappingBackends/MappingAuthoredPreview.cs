using Braid.Domain;

namespace Braid.MappingBackends;

public sealed record MappingAuthoredPreviewSubject(ProjectScope Project, Guid AuthoringId,
    Sha256Digest ProposalDigest, Sha256Digest NativeDigest, Sha256Digest SampleDigest);

/// <summary>Verified ephemeral execution output, not a catalog revision or approval.</summary>
public sealed class MappingAuthoredPreviewEvidence
{
    private readonly byte[] output;
    internal MappingAuthoredPreviewEvidence(MappingAuthoredPreviewSubject subject, byte[] output,
        string format, string mediaType, string workerBuild)
    {
        Subject = subject; this.output = output.ToArray(); OutputDigest = Sha256Digest.FromBytes(output);
        Format = format; MediaType = mediaType; WorkerBuild = workerBuild;
    }
    public MappingAuthoredPreviewSubject Subject { get; }
    public Sha256Digest OutputDigest { get; }
    public int OutputByteCount => output.Length;
    public string Format { get; }
    public string MediaType { get; }
    public string WorkerBuild { get; }
    public string EngineRevision => FerruleInspectionBackend.EngineRevision;
    public byte[] CopyOutput() => output.ToArray();
}

public sealed record MappingAuthoredPreviewFailure(string Code, MappingOperationStatus Status);
public sealed record MappingAuthoredPreviewResult(MappingAuthoredPreviewEvidence? Evidence, MappingAuthoredPreviewFailure? Failure);

public static class MappingAuthoredPreviewPolicy
{
    public const int MaximumHeaderBytes = 16 * 1024;
    public const int MaximumNativeBytes = 4 * 1024 * 1024;
    public const int MaximumSampleBytes = 1024 * 1024;
    public const int MaximumOutputBytes = 1024 * 1024;
    public const int MaximumRequestBytes = MaximumHeaderBytes + MaximumNativeBytes + MaximumSampleBytes + 12;
    public const int MaximumResponseBytes = MaximumHeaderBytes + MaximumOutputBytes + 8;
}
