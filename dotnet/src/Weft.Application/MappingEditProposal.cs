using System.Collections.Immutable;
using Weft.Domain;

namespace Weft.Application;

/// <summary>A verified edited checkpoint and its exact canonical operations. This is content, never save authority.</summary>
public sealed class MappingEditProposal
{
    internal MappingEditProposal(NativeArtifact checkpoint, MappingEditScript operations, IEnumerable<MappingEditChange> changes)
    {
        Checkpoint = checkpoint;
        Operations = operations;
        Changes = changes.ToImmutableArray();
    }

    public RevisionScope Base => Checkpoint.Scope;
    /// <summary>The exact edited native artifact, with copy-owned bytes and verified worker provenance.</summary>
    public NativeArtifact Checkpoint { get; }
    /// <summary>Owns the complete bounded canonical script, its digest and operation count.</summary>
    public MappingEditScript Operations { get; }
    /// <summary>Bounded display summaries, not a complete diff or the identity of this proposal.</summary>
    public ImmutableArray<MappingEditChange> Changes { get; }
    public override string ToString() => nameof(MappingEditProposal);
}

public sealed record MappingEditProposalResult(MappingDirectEditOutcome Outcome, MappingEditProposal? Proposal = null,
    MappingEditFailure? Failure = null)
{
    public override string ToString() => nameof(MappingEditProposalResult);
}
