using System.Text;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Application;

public enum MappingDirectEditOutcome { Saved, Proposed, NotPermitted, OutOfDate, Pending, Rejected, Unavailable, Busy, NotAuthorizedOrNotFound }

/// <summary>One field whose plain-language rule an edit changes, from verified projections of both sides.</summary>
public sealed record MappingEditChange(string Field, string Before, string After);

public sealed record MappingDirectEditResult(MappingDirectEditOutcome Outcome, IReadOnlyList<MappingEditChange> Changes,
    MappingEditFailure? Failure = null, RevisionIngestionResult? Ingestion = null)
{
    public override string ToString() => nameof(MappingDirectEditResult);
}

/// <summary>
/// Replays a typed edit on one exact base revision and, only when asked to write, appends the result as the next
/// version on the condition that the base is still the head. It never creates a draft, never changes the version
/// in use and never starts a build, run or activation; the database and ingestion recheck the actor's authority.
/// </summary>
public sealed class MappingDirectEditService(IMappingDraftStore drafts, IRevisionReportMetadataReader metadata,
    IPrivateObjectStore objects, IMappingEditBackend backend, IRevisionIngestionService ingestion) : IDisposable
{
    private const int MaximumText = 256;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public async Task<MappingDirectEditResult> ApplyAsync(ActorContext actor, RevisionScope baseRevision, MappingEditScript operations,
        Guid key, bool write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(baseRevision); ArgumentNullException.ThrowIfNull(operations);
        if (key == Guid.Empty || operations.Count == 0) throw new ArgumentException("A nonempty edit and request identity are required.");
        var source = await metadata.GetSourceAsync(actor, baseRevision, cancellationToken).ConfigureAwait(false);
        if (source is null || source.Source.Scope != baseRevision) return new(MappingDirectEditOutcome.NotAuthorizedOrNotFound, []);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDirectEditOutcome.Busy, []);
        try
        {
            var bytes = await objects.ReadVerifiedAsync(baseRevision, source.Source.NativeObject, cancellationToken).ConfigureAwait(false);
            var native = NativeArtifact.Admit(baseRevision, bytes, MappingEditPolicy.MaximumNativeBytes);
            var before = await ReplayAsync(native, MappingEditScript.Empty, cancellationToken).ConfigureAwait(false);
            if (before.Checkpoint is not { } original) return new(MappingDirectEditOutcome.Unavailable, [], before.Failure);
            var after = await ReplayAsync(native, operations, cancellationToken).ConfigureAwait(false);
            if (after.Checkpoint is not { } edited) return new(MappingDirectEditOutcome.Rejected, [], after.Failure);
            var changes = Changes(original.Projection, edited.Projection);
            if (!write) return new(MappingDirectEditOutcome.Proposed, changes);
            var mapping = new MappingScope(baseRevision.OrganizationId, baseRevision.ProjectId, baseRevision.MappingId);
            // The database rechecks the actor's current edit authority, then whether this exact base is still the head.
            if (await drafts.SelectBaseAsync(actor, mapping, baseRevision.RevisionId, true, cancellationToken).ConfigureAwait(false) is null)
                return new(await drafts.SelectBaseAsync(actor, mapping, baseRevision.RevisionId, false, cancellationToken).ConfigureAwait(false) is null
                    ? MappingDirectEditOutcome.NotPermitted : MappingDirectEditOutcome.OutOfDate, changes);
            using var stream = new MemoryStream(edited.Artifact.CopyBytes(), false);
            var result = await ingestion.AppendAsync(actor, mapping, key, new AppendRevisionRequest(new EqualHead(baseRevision.RevisionId)),
                stream, cancellationToken).ConfigureAwait(false);
            for (var attempt = 1; result.Outcome == RevisionIngestionOutcome.InProgress && result.Receipt is { } receipt && attempt <= 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                result = await ingestion.ResumeAsync(actor, new(mapping.OrganizationId, mapping.ProjectId), receipt.OperationId, cancellationToken).ConfigureAwait(false);
            }
            return new(result.Outcome switch
            {
                RevisionIngestionOutcome.CommittedValid or RevisionIngestionOutcome.CommittedDecodedInvalid => MappingDirectEditOutcome.Saved,
                RevisionIngestionOutcome.HeadConflict => MappingDirectEditOutcome.OutOfDate,
                RevisionIngestionOutcome.InProgress => MappingDirectEditOutcome.Pending,
                RevisionIngestionOutcome.NotAuthorizedOrNotFound => MappingDirectEditOutcome.NotPermitted,
                _ => MappingDirectEditOutcome.Unavailable
            }, changes, Ingestion: result);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Saves an earlier version's exact content as the next version, only while the expected version is the head.</summary>
    public async Task<RevisionIngestionResult> RestoreAsync(ActorContext actor, MappingScope mapping, Guid sourceRevisionId,
        Guid expectedHeadRevisionId, Guid key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(mapping);
        if (key == Guid.Empty || sourceRevisionId == Guid.Empty || expectedHeadRevisionId == Guid.Empty || sourceRevisionId == expectedHeadRevisionId)
            throw new ArgumentException("Restoring needs a distinct source, expected head and request identity.");
        var source = await drafts.SelectBaseAsync(actor, mapping, sourceRevisionId, false, cancellationToken).ConfigureAwait(false);
        if (source is null) return RevisionIngestionResult.WithoutReceipt(RevisionIngestionOutcome.NotAuthorizedOrNotFound);
        var bytes = await objects.ReadVerifiedAsync(source.Revision, source.NativeObject, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes, false);
        return await ingestion.AppendAsync(actor, mapping, key, new AppendRevisionRequest(new EqualHead(expectedHeadRevisionId)),
            stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fields whose rule summaries differ between two projections, keyed by boundary and typed path.</summary>
    public static IReadOnlyList<MappingEditChange> Changes(ProjectionReportV1 before, ProjectionReportV1 after)
    {
        var left = Rules(before.Document); var right = Rules(after.Document);
        var changes = new List<MappingEditChange>();
        foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            var had = left.TryGetValue(key, out var was); var has = right.TryGetValue(key, out var now);
            var oldRule = had ? was.Rule : null; var newRule = has ? now.Rule : null;
            if (oldRule != newRule)
                changes.Add(new(Bound(has ? now.Field : was.Field), Bound(oldRule ?? "Not mapped"), Bound(newRule ?? "Not mapped")));
        }
        return changes;
    }

    private static Dictionary<string, (string Field, string? Rule)> Rules(ProjectionDocumentV1? document)
    {
        var result = new Dictionary<string, (string Field, string? Rule)>(StringComparer.Ordinal);
        if (document is null) return result;
        var rules = document.Rules.GroupBy(rule => rule.OutputFieldId)
            .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(rule => rule.Summary).Order(StringComparer.Ordinal)));
        foreach (var field in document.Fields)
        {
            var path = string.Join('/', field.Path.Select(segment => segment switch
            {
                FieldPathSegment named => named.Name,
                AttributePathSegment attribute => "@" + attribute.Name,
                IndexPathSegment index => "[" + index.Index + "]",
                _ => "*"
            }));
            var display = path.Length > 0 ? path : field.Name;
            result.TryAdd(field.BoundaryId + "\n" + display, (display, rules.GetValueOrDefault(field.Id)));
        }
        return result;
    }

    private static string Bound(string text)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length == 0) return "(unnamed)";
        if (clean.Length <= MaximumText) return clean;
        var kept = new StringBuilder();
        foreach (var rune in clean.EnumerateRunes())
        {
            if (kept.Length + rune.Utf16SequenceLength > MaximumText - 1) break;
            kept.Append(rune.ToString());
        }
        return kept.Append('…').ToString();
    }

    private async Task<MappingEditResult> ReplayAsync(NativeArtifact native, MappingEditScript operations, CancellationToken token)
    {
        var result = await backend.ReplayAsync(native, operations, token).ConfigureAwait(false);
        if ((result.Checkpoint is null) == (result.Failure is null)) throw new InvalidOperationException("Edit replay requires exactly one checkpoint or failure.");
        if (result.Checkpoint is { } checkpoint && (checkpoint.Artifact.Scope != native.Scope
            || checkpoint.Artifact.ByteCount > MappingEditPolicy.MaximumNativeBytes
            || checkpoint.Evidence.Facts.Provenance.NativeDigest != checkpoint.Artifact.Digest))
            throw new InvalidOperationException("The edit worker returned inconsistent checkpoint provenance.");
        return result;
    }

    public void Dispose() => _gate.Dispose();
}
