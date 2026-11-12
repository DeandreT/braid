using Weft.Application;
using Weft.Domain;

namespace Weft.MappingBackends;

/// <summary>Executes the exact retained proposal using the dedicated bounded authored-preview profile.</summary>
public sealed class FerruleMappingCreationPreview(FerruleInspectionBackend backend) : IMappingCreationPreview
{
    public async Task<CreationPreviewEvidence> RunAsync(MappingCreationProposal proposal, ReadOnlyMemory<byte> sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (sample.Length > MappingAuthoredPreviewPolicy.MaximumSampleBytes) throw new ArgumentException("Sample exceeds its byte bound.");
        var owned = sample.ToArray(); var native = proposal.Compilation.CopyNative();
        var subject = new MappingAuthoredPreviewSubject(proposal.Project, proposal.Id, proposal.Fingerprint,
            proposal.Compilation.NativeDigest, Sha256Digest.FromBytes(owned));
        var result = await backend.PreviewAuthoredAsync(subject, native, owned, ct).ConfigureAwait(false);
        if (result is null || (result.Evidence is null) == (result.Failure is null))
            throw new InvalidOperationException("Preview returned inconsistent execution evidence.");
        var applicationSubject = new CreationPreviewSubject(subject.Project, subject.AuthoringId, subject.ProposalDigest, subject.NativeDigest, subject.SampleDigest);
        if (result.Failure is not null) return new(applicationSubject, CreationPreviewOutcome.Failed, [], null, null);
        var evidence = result.Evidence!;
        if (evidence.Subject != subject || evidence.EngineRevision != FerruleInspectionBackend.EngineRevision)
            throw new InvalidOperationException("Preview returned substituted execution evidence.");
        var output = evidence.CopyOutput();
        if (Sha256Digest.FromBytes(output) != evidence.OutputDigest || output.Length != evidence.OutputByteCount)
            throw new InvalidOperationException("Preview returned substituted output bytes.");
        return new(applicationSubject, CreationPreviewOutcome.Succeeded, output, evidence.Format, evidence.MediaType);
    }
}
