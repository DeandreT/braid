using System.Text;
using System.Text.Json;
using Braid.Contracts.Projections;
using Braid.Domain;

namespace Braid.Application;

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
    private const int MaximumText = MappingEditProposal.MaximumChangeTextLength;
    private readonly SemaphoreSlim _gate = new(2, 2);

    /// <summary>Produces immutable review content without selecting a writable base, preparing a save or invoking ingestion.</summary>
    public async Task<MappingEditProposalResult> ProposeAsync(ActorContext actor, RevisionScope baseRevision, MappingEditScript operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(baseRevision); ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0) throw new ArgumentException("A nonempty edit is required.", nameof(operations));
        var source = await metadata.GetSourceAsync(actor, baseRevision, cancellationToken).ConfigureAwait(false);
        if (source is null || source.Source.Scope != baseRevision) return new(MappingDirectEditOutcome.NotAuthorizedOrNotFound);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDirectEditOutcome.Busy);
        try
        {
            var generated = await ReadEditAsync(baseRevision, source.Source.NativeObject, operations, cancellationToken).ConfigureAwait(false);
            return generated.Checkpoint is { } checkpoint
                ? new(generated.Outcome, new(checkpoint, operations, generated.Changes!)
                { VerifiedGeneration = new(actor.PrincipalId, generated.Provenance!) })
                : new(generated.Outcome, Failure: generated.Failure);
        }
        finally { _gate.Release(); }
    }

    public async Task<MappingDirectEditResult> ApplyAsync(ActorContext actor, RevisionScope baseRevision, MappingEditScript operations,
        Guid key, bool write, CancellationToken cancellationToken = default,
        Func<IReadOnlyList<MappingEditChange>, NativeArtifact, CancellationToken, Task<bool>>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(baseRevision); ArgumentNullException.ThrowIfNull(operations);
        if (key == Guid.Empty || operations.Count == 0) throw new ArgumentException("A nonempty edit and request identity are required.");
        var source = await metadata.GetSourceAsync(actor, baseRevision, cancellationToken).ConfigureAwait(false);
        if (source is null || source.Source.Scope != baseRevision) return new(MappingDirectEditOutcome.NotAuthorizedOrNotFound, []);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(MappingDirectEditOutcome.Busy, []);
        try
        {
            var generated = await ReadEditAsync(baseRevision, source.Source.NativeObject, operations, cancellationToken).ConfigureAwait(false);
            if (generated.Checkpoint is not { } checkpoint) return new(generated.Outcome, [], generated.Failure);
            // Legacy Apply and its preparation callback have no truncation flag: preserve every summary.
            // Only Propose constructs the bounded, explicitly truncated persistence content.
            var changes = generated.Changes!;
            if (!write) return new(MappingDirectEditOutcome.Proposed, changes);
            var mapping = new MappingScope(baseRevision.OrganizationId, baseRevision.ProjectId, baseRevision.MappingId);
            // The database rechecks the actor's current edit authority, then whether this exact base is still the head.
            if (await drafts.SelectBaseAsync(actor, mapping, baseRevision.RevisionId, true, cancellationToken).ConfigureAwait(false) is null)
                return new(await drafts.SelectBaseAsync(actor, mapping, baseRevision.RevisionId, false, cancellationToken).ConfigureAwait(false) is null
                    ? MappingDirectEditOutcome.NotPermitted : MappingDirectEditOutcome.OutOfDate, changes);
            // Chat persists the exact result and its audit identity BEFORE ingestion can reserve or commit anything.
            // A missing/ambiguous journal acknowledgement grants no permission to append.
            if (prepare is not null && !await prepare(changes, checkpoint, cancellationToken).ConfigureAwait(false))
                return new(MappingDirectEditOutcome.Unavailable, changes);
            using var stream = new MemoryStream(checkpoint.CopyBytes(), false);
            var result = await ingestion.AppendAsync(actor, mapping, key, new AppendRevisionRequest(new EqualHead(baseRevision.RevisionId)),
                stream, cancellationToken).ConfigureAwait(false);
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
        // Projection IDs are local to each report. Match the complete boundary and typed path instead,
        // without using a flattened or bounded display label as identity. JSON retains segment kinds,
        // namespace URIs and string boundaries, including names containing path punctuation.
        foreach (var fields in document.Fields.GroupBy(field => JsonSerializer.Serialize(new { field.BoundaryId, field.Path })))
        {
            var field = fields.First();
            var path = string.Join('/', field.Path.Select(segment => segment switch
            {
                FieldPathSegment named => DisplayName(named.Name, named.NamespaceUri),
                AttributePathSegment attribute => "@" + DisplayName(attribute.Name, attribute.NamespaceUri),
                IndexPathSegment index => "[" + index.Index + "]",
                AnyItemPathSegment => "*",
                _ => throw new InvalidOperationException("Unknown projection path segment.")
            }));
            var display = path.Length > 0 ? path : field.Name;
            // If a projection contains multiple field IDs for the same location, retain all their rules.
            var summaries = fields.Select(item => rules.GetValueOrDefault(item.Id)).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            result.Add(fields.Key, (display, summaries.Length > 0 ? string.Join("; ", summaries) : null));
        }
        return result;
    }

    private static string DisplayName(string name, string? namespaceUri)
    {
        var readable = name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            ? name : JsonSerializer.Serialize(name);
        return namespaceUri is null ? readable : "{" + namespaceUri + "}" + readable;
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

    private sealed record GeneratedEdit(MappingDirectEditOutcome Outcome, NativeArtifact? Checkpoint = null,
        IReadOnlyList<MappingEditChange>? Changes = null, MappingEditFailure? Failure = null, IngestionProvenance? Provenance = null);

    private async Task<GeneratedEdit> ReadEditAsync(RevisionScope baseRevision, PrivateObjectReference reference,
        MappingEditScript operations, CancellationToken token)
    {
        var bytes = await objects.ReadVerifiedAsync(baseRevision, reference, token).ConfigureAwait(false);
        var native = NativeArtifact.Admit(baseRevision, bytes, MappingEditPolicy.MaximumNativeBytes);
        var before = await ReplayAsync(native, MappingEditScript.Empty, token).ConfigureAwait(false);
        if (before.Checkpoint is not { } original) return new(MappingDirectEditOutcome.Unavailable, Failure: before.Failure);
        var after = await ReplayAsync(native, operations, token).ConfigureAwait(false);
        if (after.Checkpoint is not { } edited) return new(MappingDirectEditOutcome.Rejected, Failure: after.Failure);
        return new(MappingDirectEditOutcome.Proposed, edited.Artifact, Changes(original.Projection, edited.Projection),
            Provenance: edited.Evidence.Facts.Provenance);
    }

    private async Task<MappingEditResult> ReplayAsync(NativeArtifact native, MappingEditScript operations, CancellationToken token)
    {
        var result = await backend.ReplayAsync(native, operations, token).ConfigureAwait(false);
        if ((result.Checkpoint is null) == (result.Failure is null)) throw new InvalidOperationException("Edit replay requires exactly one checkpoint or failure.");
        if (result.Checkpoint is { } checkpoint && (checkpoint.Artifact.Scope != native.Scope
            || checkpoint.Artifact.ByteCount > MappingEditPolicy.MaximumNativeBytes
            || !checkpoint.Evidence.Facts.Provenance.Confirmed || checkpoint.Evidence.Facts.Provenance.Scope != native.Scope
            || checkpoint.Evidence.Facts.Provenance.NativeDigest != checkpoint.Artifact.Digest))
            throw new InvalidOperationException("The edit worker returned inconsistent checkpoint provenance.");
        return result;
    }

    public void Dispose() => _gate.Dispose();
}
