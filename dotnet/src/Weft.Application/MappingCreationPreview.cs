using Weft.Domain;

namespace Weft.Application;

public sealed record CreationPreviewSubject(ProjectScope Project, Guid AuthoringId, Sha256Digest Proposal,
    Sha256Digest Native, Sha256Digest Sample);

public enum CreationPreviewOutcome { Succeeded = 1, Failed = 2 }

/// <summary>Ephemeral preview evidence, never a saved revision or approval token.</summary>
public sealed class CreationPreviewEvidence
{
    private readonly byte[] output;
    // Trusted runner adapter only: validation/projection alone must never construct execution evidence.
    internal CreationPreviewEvidence(CreationPreviewSubject subject, CreationPreviewOutcome outcome, ReadOnlySpan<byte> output)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Project is null || subject.AuthoringId == Guid.Empty || subject.Proposal is null || subject.Native is null || subject.Sample is null ||
            !Enum.IsDefined(outcome) || output.Length > 1024 * 1024 || outcome == CreationPreviewOutcome.Failed && output.Length != 0)
            throw new ArgumentException("Invalid bounded execution evidence.");
        Subject = subject; Outcome = outcome; this.output = output.ToArray();
    }
    public CreationPreviewSubject Subject { get; }
    public CreationPreviewOutcome Outcome { get; }
    public byte[] CopyOutput() => (byte[])output.Clone();
}

/// <summary>Unimplemented sandbox integration. Runs retained compiler output without catalog identity or ingestion.
/// Must use the trusted bounded worker profile, never invent a revision scope to use an existing-revision runner.
/// Evidence may only be minted after actual sample execution, with exact subject/native/sample identity verified.
/// Do not turn native validation success into execution success. No queue or catalog reservation is required.</summary>
public interface IMappingCreationPreview
{
    Task<CreationPreviewEvidence> RunAsync(MappingCreationProposal proposal, ReadOnlyMemory<byte> sample, CancellationToken ct);
}

public sealed class MappingCreationPreviewService(IMappingCreationPreview preview, IMappingCreationAuthority authority)
{
    public async Task<CreationPreviewEvidence?> PreviewAsync(ActorContext actor, MappingCreationProposal proposal,
        ReadOnlyMemory<byte> sample, CancellationToken ct = default)
    {
        if (actor.PrincipalId != proposal.Author) throw new ArgumentException("Preview requires the proposal author.");
        if (sample.Length > 1024 * 1024) throw new ArgumentException("Preview sample exceeds its budget.");
        var owned = sample.ToArray(); var digest = Sha256Digest.FromBytes(owned);
        var subject = new CreationPreviewSubject(proposal.Project, proposal.Id, proposal.Fingerprint, proposal.Compilation.NativeDigest, digest);
        if (await authority.CheckAsync(actor, proposal.Specification.Snapshot, ct).ConfigureAwait(false) != CreationAuthority.Current) return null;
        var result = await preview.RunAsync(proposal, owned, ct).ConfigureAwait(false);
        if (result is null || result.Subject != subject)
            throw new InvalidOperationException("Preview returned substituted evidence.");
        return await authority.CheckAsync(actor, proposal.Specification.Snapshot, ct).ConfigureAwait(false) == CreationAuthority.Current ? result : null;
    }
}
