using Weft.Domain;

namespace Weft.Application;

/// <summary>The immutable input to a run. A draft's base is an execution locator, not a committed preview subject.</summary>
public abstract record PreviewSubject
{
    private protected PreviewSubject(RevisionScope executionScope) => ExecutionScope = executionScope ?? throw new ArgumentNullException(nameof(executionScope));
    public RevisionScope ExecutionScope { get; }
    public static implicit operator PreviewSubject(RevisionScope revision) => new RevisionPreviewSubject(revision);
    public override string ToString() => "Preview subject.";
}

public sealed record RevisionPreviewSubject(RevisionScope Revision) : PreviewSubject(Revision)
{
    public override string ToString() => "Revision preview subject.";
}

public sealed record DraftPreviewSubject : PreviewSubject
{
    public DraftPreviewSubject(RevisionScope baseRevision, Guid draftId, long version, Sha256Digest nativeDigest, int nativeByteCount) : base(baseRevision)
    {
        if (draftId == Guid.Empty) throw new ArgumentException("A draft identity is required.", nameof(draftId));
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        if (nativeByteCount is < 1 or > MappingEditPolicy.MaximumNativeBytes) throw new ArgumentOutOfRangeException(nameof(nativeByteCount));
        DraftId = draftId; Version = version; NativeDigest = nativeDigest ?? throw new ArgumentNullException(nameof(nativeDigest)); NativeByteCount = nativeByteCount;
    }
    public Guid DraftId { get; }
    public long Version { get; }
    public Sha256Digest NativeDigest { get; }
    public int NativeByteCount { get; }
    public override string ToString() => "Draft preview subject.";
}

/// <summary>A leased run owns either a private-object reference or independently retained checkpoint bytes, never both.</summary>
public abstract record PreviewNativeSource
{
    private protected PreviewNativeSource() { }
    public static implicit operator PreviewNativeSource(PrivateObjectReference reference) => new StoredPreviewNative(reference);
    public override string ToString() => "Preview native source.";
}
public sealed record StoredPreviewNative(PrivateObjectReference Reference) : PreviewNativeSource
{
    public override string ToString() => "Stored preview native source.";
}
public sealed record CheckpointPreviewNative(NativeArtifact Artifact) : PreviewNativeSource
{
    public override string ToString() => "Checkpoint preview native source.";
}
