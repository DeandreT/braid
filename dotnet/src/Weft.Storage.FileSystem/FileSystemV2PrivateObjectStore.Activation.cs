using System.Text;
using Microsoft.Win32.SafeHandles;
using Weft.Application;

namespace Weft.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    private const string ActivationName = ".weft-private-storage.activation";
    private const string OriginalMarkerPrefix = "weft-private-storage-v1\n";

    /// <summary>
    /// Explicit offline conversion. The acknowledgement is the operator's assertion,
    /// not proof that old users stopped. No incomplete preparation is adopted.
    /// </summary>
    public static Task<PrivateStorageV2ActivationResult> ActivateAsync(FileSystemStorageOptions options,
        bool offlineAcknowledged, CancellationToken cancellationToken = default) =>
        ActivateAsync(options, offlineAcknowledged, null, cancellationToken);

    internal static async Task<PrivateStorageV2ActivationResult> ActivateAsync(FileSystemStorageOptions options,
        bool offlineAcknowledged, IV2ActivationObserver? observer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!offlineAcknowledged)
            throw new ArgumentException("Offline activation requires an explicit acknowledgement that all users are quiesced.", nameof(offlineAcknowledged));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            LinuxFileSystem.RequireProfile();
            ValidateEnvironment(options, true);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(options.RootPath)!);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(parent), LinuxFileSystem.EffectiveUserId);
            LinuxFileSystem.RequireLocalFileSystem(parent);
            using var root = LinuxFileSystem.OpenConfiguredRoot(parent, Path.GetFileName(options.RootPath));
            LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(root), LinuxFileSystem.EffectiveUserId);
            LinuxFileSystem.RequireLocalFileSystem(root);
            ValidateBaseNamespace(root, false);
            if (LinuxFileSystem.TryInspectChild(root, ActivationName, out _))
            {
                using var activation = OpenPrivateDirectory(root, ActivationName, false);
                var initial = ReadActivationReceipt(activation);
                using (var namedGate = LinuxFileSystem.OpenRegularRead(activation, "gate",
                    LinuxFileSystem.TemporaryFileMode, LinuxFileSystem.EffectiveUserId))
                {
                    var stat = LinuxFileSystem.Inspect(namedGate);
                    LinuxFileSystem.RequireFile(stat, LinuxFileSystem.TemporaryFileMode, LinuxFileSystem.EffectiveUserId);
                    if (stat.Identity != initial.GateIdentity || stat.Size != 0)
                        throw Failure(PrivateObjectStorageError.UnsafeStorage);
                }
                using var gate = LinuxFileSystem.TryLockGate(activation, "gate", true, LinuxFileSystem.EffectiveUserId);
                if (gate is null) return PrivateStorageV2ActivationResult.Busy();
                ValidateActivationAnchors(options, root, activation, gate, initial);
                return await CompleteActivationAsync(options, root, activation, gate, initial, observer, cancellationToken).ConfigureAwait(false);
            }

            var original = ReadSafeBytes(root, MarkerName, 90);
            var rootId = ActivationOriginalRootId(original.Bytes);
            if (LinuxFileSystem.TryInspectChild(root, ControlName, out _)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            using (var sync = LinuxFileSystem.OpenSyncDirectory(root))
            {
                if (!LinuxFileSystem.CreateDirectory(root, ActivationName)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                LinuxFileSystem.SyncDirectory(root, sync);
            }
            using var fresh = OpenPrivateDirectory(root, ActivationName, false);
            CreateEmptyGate(fresh, "gate");
            using var freshGate = LinuxFileSystem.TryLockGate(fresh, "gate", true, LinuxFileSystem.EffectiveUserId) ??
                throw Failure(PrivateObjectStorageError.Io);
            var activationId = Guid.NewGuid();
            var controlId = Guid.NewGuid();
            await ObserveActivationAsync(observer, V2ActivationPhase.PreparationCreated, activationId, rootId,
                controlId, cancellationToken).ConfigureAwait(false);
            if (!LinuxFileSystem.CreateDirectory(fresh, "control")) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            using var staged = OpenPrivateDirectory(fresh, "control", false);
            if (!LinuxFileSystem.CreateDirectory(staged, controlId.ToString("N"))) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            using var control = OpenPrivateDirectory(staged, controlId.ToString("N"), false);
            ValidateBaseNamespace(control, true);
            var metaBytes = ControlBytes(rootId, controlId);
            WriteImmutable(control, "control.meta", metaBytes);
            ProbeCapabilities(control);
            ProbeActivationExchange(fresh);
            var markerBytes = ActivationMarkerBytes(rootId, controlId);
            WriteImmutable(fresh, "marker", markerBytes);
            var meta = ReadSafeBytes(control, "control.meta", 68);
            var marker = ReadSafeBytes(fresh, "marker", 90);
            var incarnation = LinuxFileSystem.ReadIncarnation();
            V2ActivationRecord record;
            using (var file = LinuxFileSystem.CreateTemporary(fresh, "receipt.stage"))
            {
                var self = LinuxFileSystem.Inspect(file);
                LinuxFileSystem.RequireFile(self, LinuxFileSystem.TemporaryFileMode, LinuxFileSystem.EffectiveUserId);
                record = new(activationId, rootId, controlId, incarnation, LinuxFileSystem.EffectiveUserId,
                    LinuxFileSystem.Inspect(root).Identity, LinuxFileSystem.Inspect(fresh).Identity, freshGate.Identity,
                    LinuxFileSystem.Inspect(staged).Identity, LinuxFileSystem.Inspect(control).Identity, meta.Identity,
                    original.Identity, marker.Identity, self.Identity, original.Bytes, markerBytes, metaBytes);
                var bytes = V2ActivationReceiptCodec.Encode(record);
                using var output = new FileStream(file, FileAccess.Write, 4096, isAsync: false);
                output.Write(bytes.AsSpan(0, bytes.Length / 2));
                output.Flush();
                LinuxFileSystem.SyncFile(file);
            }
            await ObserveActivationAsync(observer, V2ActivationPhase.ReceiptStageWritten, activationId, rootId,
                controlId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using (var file = LinuxFileSystem.OpenRegularWrite(fresh, "receipt.stage", LinuxFileSystem.TemporaryFileMode,
                LinuxFileSystem.EffectiveUserId))
            {
                var current = LinuxFileSystem.Inspect(file);
                if (current.Identity != record.ReceiptIdentity || current.Size != V2ActivationReceiptCodec.Size / 2)
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var bytes = V2ActivationReceiptCodec.Encode(record);
                RandomAccess.Write(file, bytes.AsSpan(bytes.Length / 2), bytes.Length / 2);
                LinuxFileSystem.MakeReadOnly(file);
                LinuxFileSystem.SyncFile(file);
            }
            using (var sync = LinuxFileSystem.OpenSyncDirectory(fresh))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LinuxFileSystem.PublishNoReplace(fresh, "receipt.stage", "receipt"))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                LinuxFileSystem.SyncDirectory(fresh, sync);
            }
            ValidateActivationAnchors(options, root, fresh, freshGate, record);
            await ObserveActivationAsync(observer, V2ActivationPhase.ReceiptSealed, activationId, rootId,
                controlId, cancellationToken).ConfigureAwait(false);
            return await CompleteActivationAsync(options, root, fresh, freshGate, record, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
        catch (PrivateObjectStorageException) { throw; }
        catch (ArgumentException) { throw; }
        catch (Exception error) { throw NormalizeUnexpected(error); }
    }

    private static async Task<PrivateStorageV2ActivationResult> CompleteActivationAsync(FileSystemStorageOptions options,
        SafeFileHandle root, SafeFileHandle activation, LinuxFileSystem.Gate gate, V2ActivationRecord record,
        IV2ActivationObserver? observer, CancellationToken token)
    {
        ValidateActivationAnchors(options, root, activation, gate, record);
        var state = ReadActivationState(root, activation, record);
        if (state == ActivationState.Active)
        {
            token.ThrowIfCancellationRequested();
            return PrivateStorageV2ActivationResult.Success(true, record.PublicReceipt());
        }
        if (state == ActivationState.ControlStaged)
        {
            using var rootSync = LinuxFileSystem.OpenSyncDirectory(root);
            using var activationSync = LinuxFileSystem.OpenSyncDirectory(activation);
            token.ThrowIfCancellationRequested();
            if (!LinuxFileSystem.MoveNoReplace(activation, "control", root, ControlName))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            LinuxFileSystem.SyncDirectory(activation, activationSync);
            LinuxFileSystem.SyncDirectory(root, rootSync);
            await ObserveActivationAsync(observer, V2ActivationPhase.ControlInstalled, record.ActivationId,
                record.RootId, record.ControlNamespaceId, token).ConfigureAwait(false);
        }
        ValidateActivationAnchors(options, root, activation, gate, record);
        if (ReadActivationState(root, activation, record) != ActivationState.Ready)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var result = PrivateStorageV2ActivationResult.Success(false, record.PublicReceipt());
        token.ThrowIfCancellationRequested();
        // Both existing names, bytes and inodes have been checked under the gate.
        // Exchange preserves the original marker. Success has no fallible tail.
        LinuxFileSystem.Exchange(root, MarkerName, activation, "marker");
        return result;
    }

    private static ValueTask ObserveActivationAsync(IV2ActivationObserver? observer, V2ActivationPhase phase,
        Guid activation, Guid root, Guid control, CancellationToken token) => observer?.ObserveAsync(
            new V2ActivationObservation(phase, activation, root, control), token) ?? ValueTask.CompletedTask;

    internal static byte[] ActivationMarkerBytes(Guid root, Guid control) =>
        Encoding.ASCII.GetBytes(MarkerPrefix + root.ToString("N") + "\n" + control.ToString("N") + "\n");

    internal static byte[] ActivationControlBytes(Guid root, Guid control) => ControlBytes(root, control);

    internal static Guid ActivationOriginalRootId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 57) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith(OriginalMarkerPrefix, StringComparison.Ordinal) || text[^1] != '\n' ||
            !Guid.TryParseExact(text.AsSpan(OriginalMarkerPrefix.Length, 32), "N", out var root) || root == Guid.Empty ||
            text != OriginalMarkerPrefix + root.ToString("N") + "\n")
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return root;
    }
}
