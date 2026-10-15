using System.Text;
using Microsoft.Win32.SafeHandles;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore : IPrivateObjectStorageObserver
{
    public Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project,
        CancellationToken cancellationToken = default) => ObserveStorageAsync(project, null, cancellationToken);

    internal Task<PrivateStorageObservationPage> ObserveAsync(ProjectScope project,
        IPrivateStorageObservationHook hook, CancellationToken cancellationToken = default) =>
        ObserveStorageAsync(project, hook ?? throw new ArgumentNullException(nameof(hook)), cancellationToken);

    private async Task<PrivateStorageObservationPage> ObserveStorageAsync(ProjectScope project,
        IPrivateStorageObservationHook? hook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            var incarnation = LinuxFileSystem.ReadIncarnation();
            var state = new StorageObservationState(project, incarnation, hook);
            var native = await ObserveNamespaceAsync(state, PrivateObjectClass.NativeArtifact, cancellationToken)
                .ConfigureAwait(false);
            var derived = !state.AtLimit && await ObserveNamespaceAsync(state, PrivateObjectClass.DerivedArtifact,
                cancellationToken).ConfigureAwait(false);
            ValidateRoot();
            if (LinuxFileSystem.ReadIncarnation() != incarnation)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            cancellationToken.ThrowIfCancellationRequested();
            return new(project, _marker.RootId, _marker.ControlId, state.Entries, native, derived,
                state.AtLimit, state.UntraversedSubtrees, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateStorageObservationChangedException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception error) { throw NormalizeUnexpected(error); }
    }

    private async Task<bool> ObserveNamespaceAsync(StorageObservationState state, PrivateObjectClass objectClass,
        CancellationToken cancellationToken)
    {
        using var chain = new StorageObservationChain(this);
        foreach (var name in new[] { "objects", objectClass == PrivateObjectClass.NativeArtifact ? "native" : "derived",
            state.Project.OrganizationId.ToString("N"), state.Project.ProjectId.ToString("N") })
        {
            cancellationToken.ThrowIfCancellationRequested();
            chain.Validate();
            var rawName = Encoding.ASCII.GetBytes(name);
            if (!LinuxFileSystem.TryInspectObservationChild(chain.Current, rawName, out var before))
            {
                // An absent namespace is not provisioned. Its held ancestors and
                // exact absent name must still agree when it is reported empty.
                chain.Validate();
                if (LinuxFileSystem.TryInspectObservationChild(chain.Current, rawName, out _))
                    throw new PrivateStorageObservationChangedException();
                return true;
            }
            chain.Push(OpenObservationDirectory(chain.Current, rawName, before));
        }
        var complete = await WalkObservationDirectoryAsync(state, chain, objectClass,
            PrivateStorageEntryLevel.Mapping, null, null, cancellationToken).ConfigureAwait(false);
        chain.Validate();
        return complete;
    }

    private async Task<bool> WalkObservationDirectoryAsync(StorageObservationState state,
        StorageObservationChain chain, PrivateObjectClass objectClass, PrivateStorageEntryLevel level,
        Guid? mappingId, Guid? revisionId, CancellationToken cancellationToken)
    {
        chain.Validate();
        using var directory = new LinuxFileSystem.ObservationDirectory(chain.Current);
        while (!state.AtLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chain.Validate();
            var rawName = directory.ReadName(cancellationToken);
            if (rawName is null) { chain.Validate(); return true; }
            var parent = LinuxFileSystem.PublicationIdentity(LinuxFileSystem.Inspect(chain.Current).Identity);
            var ordinal = state.Consume(parent, rawName);
            await RunObservationHookAsync(state, directory, PrivateStorageObservationPhase.EntryConsumed,
                ordinal, cancellationToken).ConfigureAwait(false);
            chain.Validate();

            LinuxFileSystem.Stat? facts = null;
            PrivateStorageSlotObservation? slot = null;
            StorageObservationDirectory? child = null;
            var kind = PrivateStorageEntryKind.Unproved;
            var reason = PrivateStorageUnprovedReason.UnknownName;
            Guid childId = default;
            try
            {
                if (!LinuxFileSystem.TryInspectObservationChild(chain.Current, rawName, out var before))
                    reason = PrivateStorageUnprovedReason.Unavailable;
                else
                {
                    facts = before;
                    await RunObservationHookAsync(state, directory, PrivateStorageObservationPhase.ChildInspected,
                        ordinal, cancellationToken).ConfigureAwait(false);
                    if (level != PrivateStorageEntryLevel.Payload && TryObservationId(rawName, out childId))
                    {
                        child = OpenObservationDirectory(chain.Current, rawName, before);
                        kind = PrivateStorageEntryKind.LayoutDirectory;
                    }
                    else if (level == PrivateStorageEntryLevel.Payload &&
                        TryObservationSlot(rawName, out var id, out var temporary))
                    {
                        var actual = ObservePayloadIdentity(state, chain, rawName, before, temporary);
                        var scope = new RevisionScope(state.Project.OrganizationId, state.Project.ProjectId,
                            mappingId!.Value, revisionId!.Value);
                        slot = temporary
                            ? PrivateStorageSlotObservation.Temporary(ordinal, scope, objectClass, id, actual)
                            : PrivateStorageSlotObservation.Final(ordinal, PrivateObjectId.Restore(scope, id, objectClass), actual);
                        kind = PrivateStorageEntryKind.CandidateSlot;
                    }
                    else if ((before.Mode & 0xf000) is not (0x4000 or 0x8000))
                        reason = PrivateStorageUnprovedReason.WrongType;
                }
            }
            catch (PrivateStorageObservationChangedException) { reason = PrivateStorageUnprovedReason.Changed; }
            catch (PrivateObjectStorageException error)
            {
                reason = error.Error switch
                {
                    PrivateObjectStorageError.Missing => PrivateStorageUnprovedReason.Unavailable,
                    PrivateObjectStorageError.Unsupported => PrivateStorageUnprovedReason.Unsupported,
                    PrivateObjectStorageError.Io => PrivateStorageUnprovedReason.Unavailable,
                    _ => PrivateStorageUnprovedReason.UnsafeFacts
                };
            }

            using (child)
            {
                chain.Validate();
                var entryFacts = facts is { } observed ? new PrivateStorageEntryFacts(
                    LinuxFileSystem.PublicationIdentity(observed.Identity), observed.UserId, observed.Mode,
                    observed.LinkCount, observed.Size) : null;
                state.Entries.Add(new(ordinal, objectClass, level, mappingId, revisionId, parent, rawName,
                    kind, entryFacts, slot, kind == PrivateStorageEntryKind.Unproved ? reason : null, cancellationToken));
                if (kind == PrivateStorageEntryKind.Unproved &&
                    (facts is { } unknown && (unknown.Mode & 0xf000) == 0x4000 ||
                        facts is null && level != PrivateStorageEntryLevel.Payload))
                    state.UntraversedSubtrees++;
                await RunObservationHookAsync(state, directory, PrivateStorageObservationPhase.EntryObserved,
                    ordinal, cancellationToken).ConfigureAwait(false);
                if (child is not null && !state.AtLimit)
                {
                    chain.Push(child);
                    try
                    {
                        _ = await WalkObservationDirectoryAsync(state, chain, objectClass,
                            level == PrivateStorageEntryLevel.Mapping ? PrivateStorageEntryLevel.Revision : PrivateStorageEntryLevel.Payload,
                            level == PrivateStorageEntryLevel.Mapping ? childId : mappingId,
                            level == PrivateStorageEntryLevel.Revision ? childId : null, cancellationToken).ConfigureAwait(false);
                        chain.Validate();
                    }
                    finally { chain.Pop(); }
                }
            }
        }
        chain.Validate();
        return false;
    }

    private PrivatePublicationFileIdentity ObservePayloadIdentity(StorageObservationState state,
        StorageObservationChain chain, byte[] name, LinuxFileSystem.Stat before, bool temporary)
    {
        RequireObservationFile(before, temporary);
        using var held = LinuxFileSystem.OpenObservationEntry(chain.Current, name);
        var opened = LinuxFileSystem.Inspect(held);
        RequireObservationFile(opened, temporary);
        if (!SameObservationFacts(before, opened)) throw new PrivateStorageObservationChangedException();
        if (!LinuxFileSystem.TryInspectObservationChild(chain.Current, name, out var named))
            throw new PrivateStorageObservationChangedException();
        var after = LinuxFileSystem.Inspect(held);
        RequireObservationFile(named, temporary);
        RequireObservationFile(after, temporary);
        if (!SameObservationFacts(opened, named) || !SameObservationFacts(opened, after))
            throw new PrivateStorageObservationChangedException();
        chain.Validate();
        ValidateRoot();
        var parent = LinuxFileSystem.Inspect(chain.Current);
        if (after.DeviceMajor != parent.DeviceMajor || after.DeviceMinor != parent.DeviceMinor ||
            after.MountId != parent.MountId || after.Inode == parent.Inode)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return new(_marker.RootId, _marker.ControlId, state.Incarnation.BootId,
            state.Incarnation.NamespaceDevice, state.Incarnation.NamespaceInode, _userId,
            LinuxFileSystem.PublicationIdentity(after.Identity), LinuxFileSystem.PublicationIdentity(parent.Identity));
    }

    private void RequireObservationFile(LinuxFileSystem.Stat facts, bool temporary)
    {
        var mode = (uint)(facts.Mode & 0xfff);
        if (!temporary && mode != LinuxFileSystem.FinalFileMode || temporary &&
            mode is not (LinuxFileSystem.FinalFileMode or LinuxFileSystem.TemporaryFileMode))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireFile(facts, mode, _userId);
    }

    private StorageObservationDirectory OpenObservationDirectory(SafeFileHandle parent, byte[] name,
        LinuxFileSystem.Stat before)
    {
        LinuxFileSystem.RequireDirectory(before, _userId);
        var handle = LinuxFileSystem.OpenObservationEntry(parent, name);
        try
        {
            var opened = LinuxFileSystem.Inspect(handle);
            LinuxFileSystem.RequireDirectory(opened, _userId);
            if (!SameObservationFacts(before, opened)) throw new PrivateStorageObservationChangedException();
            var result = new StorageObservationDirectory(parent, name, handle, opened.Identity, _userId);
            result.Validate();
            return result;
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool SameObservationFacts(LinuxFileSystem.Stat left, LinuxFileSystem.Stat right) =>
        left.Identity == right.Identity && left.Mode == right.Mode && left.UserId == right.UserId &&
        left.LinkCount == right.LinkCount && left.Size == right.Size;

    private static bool TryObservationId(ReadOnlySpan<byte> name, out Guid id)
    {
        id = default;
        if (name.Length != 32) return false;
        foreach (var value in name)
            if (value is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f')) return false;
        return Guid.TryParseExact(Encoding.ASCII.GetString(name), "N", out id) && id != Guid.Empty;
    }

    private static bool TryObservationSlot(ReadOnlySpan<byte> name, out Guid id, out bool temporary)
    {
        temporary = false;
        id = default;
        if (name.Length == 37 && name[32..].SequenceEqual(".blob"u8)) return TryObservationId(name[..32], out id);
        if (name.Length != 45 || !name[..9].SequenceEqual(".attempt-"u8) || !name[41..].SequenceEqual(".tmp"u8)) return false;
        temporary = true;
        return TryObservationId(name.Slice(9, 32), out id);
    }

    private static async ValueTask RunObservationHookAsync(StorageObservationState state,
        LinuxFileSystem.ObservationDirectory directory, PrivateStorageObservationPhase phase, int ordinal,
        CancellationToken cancellationToken)
    {
        if (state.Hook is null) return;
        if (await state.Hook.AfterEntryAsync(phase, ordinal, cancellationToken).ConfigureAwait(false))
        {
            if (state.Rewound) throw Failure(PrivateObjectStorageError.Unsupported);
            state.Rewound = true;
            directory.Rewind();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class StorageObservationState(ProjectScope project, LinuxFileSystem.Incarnation incarnation,
        IPrivateStorageObservationHook? hook)
    {
        private readonly HashSet<ObservationNameKey> _seen = [];
        internal ProjectScope Project { get; } = project;
        internal LinuxFileSystem.Incarnation Incarnation { get; } = incarnation;
        internal IPrivateStorageObservationHook? Hook { get; } = hook;
        internal List<PrivateStorageEntryObservation> Entries { get; } = [];
        internal bool Rewound { get; set; }
        internal int UntraversedSubtrees { get; set; }
        private int Consumed { get; set; }
        internal bool AtLimit => Consumed == 100;
        internal int Consume(PrivatePublicationInodeIdentity parent, byte[] name)
        {
            // A repeated occurrence spends its consumed unit, then refuses the
            // whole invocation; no successful partial page can hide that unit.
            Consumed++;
            if (!_seen.Add(new(parent, name))) throw new PrivateStorageObservationChangedException();
            return Entries.Count;
        }
    }

    private sealed class ObservationNameKey(PrivatePublicationInodeIdentity parent, byte[] name) : IEquatable<ObservationNameKey>
    {
        private PrivatePublicationInodeIdentity Parent { get; } = parent;
        private byte[] Name { get; } = name;
        public bool Equals(ObservationNameKey? other) => other is not null && Parent == other.Parent &&
            Name.AsSpan().SequenceEqual(other.Name);
        public override bool Equals(object? obj) => obj is ObservationNameKey other && Equals(other);
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Parent);
            foreach (var value in Name) hash.Add(value);
            return hash.ToHashCode();
        }
        public override string ToString() => nameof(ObservationNameKey);
    }

    private sealed class StorageObservationDirectory(SafeFileHandle parent, byte[] name, SafeFileHandle handle,
        LinuxFileSystem.Identity identity, uint userId) : IDisposable
    {
        internal SafeFileHandle Handle { get; } = handle;
        internal void Validate()
        {
            var held = LinuxFileSystem.Inspect(Handle);
            LinuxFileSystem.RequireDirectory(held, userId);
            if (!LinuxFileSystem.TryInspectObservationChild(parent, name, out var named))
                throw new PrivateStorageObservationChangedException();
            LinuxFileSystem.RequireDirectory(named, userId);
            if (held.Identity != identity || named.Identity != identity || !SameObservationFacts(held, named))
                throw new PrivateStorageObservationChangedException();
        }
        public void Dispose() => Handle.Dispose();
    }

    private sealed class StorageObservationChain(FileSystemV2PrivateObjectStore store) : IDisposable
    {
        private readonly List<StorageObservationDirectory> _nodes = [];
        internal SafeFileHandle Current => _nodes.Count == 0 ? store._root : _nodes[^1].Handle;
        internal void Push(StorageObservationDirectory directory) => _nodes.Add(directory);
        internal void Pop() => _nodes.RemoveAt(_nodes.Count - 1);
        internal void Validate()
        {
            foreach (var node in _nodes) node.Validate();
        }
        public void Dispose()
        {
            for (var index = _nodes.Count - 1; index >= 0; index--) _nodes[index].Dispose();
        }
    }
}

// Internal qualification seam. Names always come from real getdents; production
// passes no hook. A request can rewind one actual directory description once.
internal enum PrivateStorageObservationPhase { EntryConsumed = 1, ChildInspected = 2, EntryObserved = 3 }
internal interface IPrivateStorageObservationHook
{
    ValueTask<bool> AfterEntryAsync(PrivateStorageObservationPhase phase, int ordinal, CancellationToken cancellationToken);
}
