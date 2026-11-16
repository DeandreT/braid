using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs.Models;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.Blob;

public sealed partial class BlobPrivateObjectStore : IPrivateObjectStorageObserver
{
    // Retirement fences and empty final-name reservations are not payload entries.
    private const int MaximumSkippedFences = 10_000;

    /// <summary>
    /// A bounded read-only listing of one project's data names, native first, through virtual
    /// mapping, revision and payload levels. It reads no payload bytes and changes nothing.
    /// </summary>
    public async Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = new ObservationState(project);
            var native = await ObserveNamespaceAsync(state, PrivateObjectClass.NativeArtifact, cancellationToken)
                .ConfigureAwait(false);
            var derived = !state.AtLimit && await ObserveNamespaceAsync(state, PrivateObjectClass.DerivedArtifact,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(project, _rootId, _controlId, state.Entries, native, derived, state.AtLimit,
                state.UntraversedSubtrees, cancellationToken);
        }
        catch (PrivateStorageObservationChangedException) { throw; }
        catch (Exception error) { throw Normalize(error, cancellationToken); }
    }

    private Task<bool> ObserveNamespaceAsync(ObservationState state, PrivateObjectClass objectClass, CancellationToken token) =>
        WalkAsync(state, objectClass, BlobLayout.ProjectPrefix(objectClass, state.Project), PrivateStorageEntryLevel.Mapping,
            null, null, token);

    private async Task<bool> WalkAsync(ObservationState state, PrivateObjectClass objectClass, string prefix,
        PrivateStorageEntryLevel level, Guid? mappingId, Guid? revisionId, CancellationToken token)
    {
        var parent = BlobIdentity.Directory(_rootId, _controlId, prefix);
        var listing = _container.GetBlobsByHierarchyAsync(BlobTraits.Metadata, BlobStates.None, "/", prefix, token);
        await foreach (var item in listing.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (state.AtLimit) return false;
            var full = item.IsPrefix ? item.Prefix : item.Blob.Name;
            if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var leaf = full[prefix.Length..];
            if (item.IsPrefix)
            {
                if (!leaf.EndsWith('/')) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                leaf = leaf[..^1];
            }
            var rawName = RawName(leaf);
            if (item.IsBlob && level == PrivateStorageEntryLevel.Payload && IsFence(item.Blob, leaf))
            {
                if (++state.SkippedFences > MaximumSkippedFences) return false;
                continue;
            }
            var ordinal = state.Consume(parent, rawName);
            if (item.IsPrefix)
            {
                var facts = new PrivateStorageEntryFacts(BlobIdentity.Directory(_rootId, _controlId, full), BlobIdentity.OwnerId,
                    BlobIdentity.DirectoryMode, 2, 0);
                if (level != PrivateStorageEntryLevel.Payload && BlobLayout.TryCanonicalId(leaf, out var childId))
                {
                    state.Entries.Add(new(ordinal, objectClass, level, mappingId, revisionId, parent, rawName,
                        PrivateStorageEntryKind.LayoutDirectory, facts, null, null, token));
                    if (state.AtLimit) continue;
                    _ = await WalkAsync(state, objectClass, full,
                        level == PrivateStorageEntryLevel.Mapping ? PrivateStorageEntryLevel.Revision : PrivateStorageEntryLevel.Payload,
                        level == PrivateStorageEntryLevel.Mapping ? childId : mappingId,
                        level == PrivateStorageEntryLevel.Revision ? childId : null, token).ConfigureAwait(false);
                }
                else
                {
                    state.Entries.Add(new(ordinal, objectClass, level, mappingId, revisionId, parent, rawName,
                        PrivateStorageEntryKind.Unproved, facts, null,
                        level == PrivateStorageEntryLevel.Payload ? PrivateStorageUnprovedReason.WrongType : PrivateStorageUnprovedReason.UnknownName,
                        token));
                    state.UntraversedSubtrees++;
                }
                continue;
            }
            var blob = BlobFacts.Classify(item.Blob.Properties.ETag ?? default, item.Blob.Properties.ContentLength ?? -1,
                item.Blob.Metadata);
            if (item.Blob.Properties.BlobType != BlobType.Block) blob = blob with { Form = BlobForm.Unknown };
            var mode = blob.Form switch
            {
                BlobForm.Object => BlobIdentity.FinalMode,
                BlobForm.Attempt => BlobIdentity.AttemptMode,
                _ => BlobIdentity.AttemptMode
            };
            var length = (ulong)Math.Max(item.Blob.Properties.ContentLength ?? 0, 0);
            if (level == PrivateStorageEntryLevel.Payload && TrySlot(leaf, out var slotId, out var temporary) &&
                blob.Form == (temporary ? BlobForm.Attempt : BlobForm.Object) &&
                (temporary || blob.DeclaredLength == blob.Length))
            {
                var scope = new RevisionScope(state.Project.OrganizationId, state.Project.ProjectId, mappingId!.Value, revisionId!.Value);
                var identity = BlobIdentity.Publication(_rootId, _controlId, objectClass, scope, blob.Incarnation);
                var facts = new PrivateStorageEntryFacts(identity.FileIdentity, BlobIdentity.OwnerId, mode, 1, length);
                var slot = temporary
                    ? PrivateStorageSlotObservation.Temporary(ordinal, scope, objectClass, slotId, identity)
                    : PrivateStorageSlotObservation.Final(ordinal, PrivateObjectId.Restore(scope, slotId, objectClass), identity);
                state.Entries.Add(new(ordinal, objectClass, level, mappingId, revisionId, parent, rawName,
                    PrivateStorageEntryKind.CandidateSlot, facts, slot, null, token));
                continue;
            }
            var reason = level == PrivateStorageEntryLevel.Payload && TrySlot(leaf, out _, out _)
                ? PrivateStorageUnprovedReason.UnsafeFacts
                : level != PrivateStorageEntryLevel.Payload && BlobLayout.TryCanonicalId(leaf, out _)
                    ? PrivateStorageUnprovedReason.WrongType : PrivateStorageUnprovedReason.UnknownName;
            state.Entries.Add(new(ordinal, objectClass, level, mappingId, revisionId, parent, rawName,
                PrivateStorageEntryKind.Unproved, new(EntryIdentity(full), BlobIdentity.OwnerId, mode, 1, length), null, reason, token));
        }
        // Consuming the last available unit leaves the listing's remainder unknown.
        return !state.AtLimit;
    }

    private static bool IsFence(BlobItem blob, string leaf) => TrySlot(leaf, out _, out var temporary) &&
        blob.Properties.BlobType == BlobType.Block &&
        (BlobFacts.Classify(blob.Properties.ETag ?? default, blob.Properties.ContentLength ?? -1, blob.Metadata).Form is var form &&
            (form == BlobForm.Retired || (!temporary && form == BlobForm.Gate)));

    private static bool TrySlot(string leaf, out Guid id, out bool temporary)
    {
        temporary = false;
        id = default;
        if (leaf.Length == 37 && leaf.EndsWith(".blob", StringComparison.Ordinal)) return BlobLayout.TryCanonicalId(leaf.AsSpan(0, 32), out id);
        if (leaf.Length != 45 || !leaf.StartsWith(".attempt-", StringComparison.Ordinal) || !leaf.EndsWith(".tmp", StringComparison.Ordinal))
            return false;
        temporary = true;
        return BlobLayout.TryCanonicalId(leaf.AsSpan(9, 32), out id);
    }

    private static byte[] RawName(string leaf)
    {
        var bytes = Encoding.UTF8.GetBytes(leaf);
        // Names Braid cannot generate and the observation contract cannot represent stop the run.
        if (bytes.Length is < 1 or > 255 || leaf.Contains('\0') || leaf is "." or "..")
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return bytes;
    }

    private PrivatePublicationInodeIdentity EntryIdentity(string name)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("braid-blob-entry\n" + _rootId.ToString("N") + "\n" + name));
        return new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest) | 1, 0, 0, BlobIdentity.MountId(_controlId));
    }

    private sealed class ObservationState(ProjectScope project)
    {
        private readonly HashSet<(PrivatePublicationInodeIdentity Parent, string Name)> _seen = [];
        internal ProjectScope Project { get; } = project;
        internal List<PrivateStorageEntryObservation> Entries { get; } = [];
        internal int UntraversedSubtrees { get; set; }
        internal int SkippedFences { get; set; }
        private int Consumed { get; set; }
        internal bool AtLimit => Consumed == 100;
        internal int Consume(PrivatePublicationInodeIdentity parent, byte[] name)
        {
            // A repeated listing entry spends its unit, then refuses the whole invocation.
            Consumed++;
            if (!_seen.Add((parent, Convert.ToHexString(name)))) throw new PrivateStorageObservationChangedException();
            return Entries.Count;
        }
    }
}
