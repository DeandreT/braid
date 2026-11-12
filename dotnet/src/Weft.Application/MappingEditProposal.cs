using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Weft.Domain;

namespace Weft.Application;

/// <summary>Generated or trusted-storage-restored edit content, never save authority. Restoration checks integrity, not worker authenticity.</summary>
public sealed class MappingEditProposal
{
    public const int MaximumChanges = 64;
    public const int MaximumChangesBytes = 65536;
    public const int MaximumChangeTextLength = 256;

    internal MappingEditProposal(NativeArtifact checkpoint, MappingEditScript operations, IEnumerable<MappingEditChange> changes,
        bool changesTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(changes);
        if (checkpoint.ByteCount > MappingEditPolicy.MaximumNativeBytes) throw new ArgumentException("Checkpoint exceeds its bound.");
        Checkpoint = checkpoint;
        Operations = operations;
        var retained = ImmutableArray.CreateBuilder<MappingEditChange>();
        var bytes = 2; // JSON array brackets; count exact escaped UTF-8, not character length.
        foreach (var change in changes)
        {
            if (retained.Count == MaximumChanges) { changesTruncated = true; break; }
            if (change is null || !Valid(change.Field) || !Valid(change.Before) || !Valid(change.After))
                throw new ArgumentException("Invalid change summary.", nameof(changes));
            var size = Encoding.UTF8.GetByteCount(ChatCanonicalJson.Write(w => WriteChange(w, change), 16384)) + (retained.Count == 0 ? 0 : 1);
            if (bytes + size > MaximumChangesBytes) { changesTruncated = true; break; }
            retained.Add(change); bytes += size;
        }
        Changes = retained.ToImmutable();
        ChangesTruncated = changesTruncated;
    }

    private static bool Valid(string? value) => value is { Length: > 0 and <= MaximumChangeTextLength } &&
        !value.Any(char.IsControl) && WellFormed(value);
    private static bool WellFormed(string value)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static void WriteChange(Utf8JsonWriter writer, MappingEditChange change)
    {
        writer.WriteStartArray(); writer.WriteStringValue(change.Field); writer.WriteStringValue(change.Before);
        writer.WriteStringValue(change.After); writer.WriteEndArray();
    }

    /// <summary>Exact bounded display summaries in input order. A deterministic prefix is retained; the complete operation script is never truncated.</summary>
    public byte[] CopyChangesUtf8() => Encoding.UTF8.GetBytes(ChatCanonicalJson.Write(w =>
    {
        w.WriteStartArray(); foreach (var change in Changes) WriteChange(w, change); w.WriteEndArray();
    }, MaximumChangesBytes));

    public RevisionScope Base => Checkpoint.Scope;
    /// <summary>The exact edited native artifact with copy-owned bytes. Worker authenticity depends on verified generation and restricted storage admission.</summary>
    public NativeArtifact Checkpoint { get; }
    /// <summary>Owns the complete bounded canonical script, its digest and operation count.</summary>
    public MappingEditScript Operations { get; }
    /// <summary>Bounded display summaries, not a complete diff or the identity of this proposal.</summary>
    public ImmutableArray<MappingEditChange> Changes { get; }
    public bool ChangesTruncated { get; }
    public override string ToString() => nameof(MappingEditProposal);
}

public sealed record MappingEditProposalResult(MappingDirectEditOutcome Outcome, MappingEditProposal? Proposal = null,
    MappingEditFailure? Failure = null)
{
    public override string ToString() => nameof(MappingEditProposalResult);
}
