using Microsoft.Win32.SafeHandles;
using Braid.Application;

namespace Braid.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    private enum ActivationState { ControlStaged, Ready, Active }

    private static V2ActivationRecord ReadActivationReceipt(SafeFileHandle activation)
    {
        var read = ReadSafeBytes(activation, "receipt", V2ActivationReceiptCodec.Size);
        var record = V2ActivationReceiptCodec.Decode(read.Bytes);
        if (record.ReceiptIdentity != read.Identity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return record;
    }

    private static void ValidateActivationAnchors(FileSystemStorageOptions options, SafeFileHandle root,
        SafeFileHandle activation, LinuxFileSystem.Gate gate, V2ActivationRecord record)
    {
        ValidateEnvironment(options, true);
        var user = LinuxFileSystem.EffectiveUserId;
        if (user != record.EffectiveUserId) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        if (LinuxFileSystem.ReadIncarnation() != record.Incarnation) throw Failure(PrivateObjectStorageError.Unsupported);
        RequireActivationDirectory(root, record.RootIdentity, user);
        RequireActivationDirectory(activation, record.ActivationDirectoryIdentity, user);
        using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(options.RootPath)!);
        using var namedRoot = LinuxFileSystem.OpenConfiguredRoot(parent, Path.GetFileName(options.RootPath));
        RequireActivationDirectory(namedRoot, record.RootIdentity, user);
        using var namedActivation = OpenPrivateDirectory(root, ActivationName, false);
        RequireActivationDirectory(namedActivation, record.ActivationDirectoryIdentity, user);
        gate.Validate();
        if (gate.Identity != record.GateIdentity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var read = ReadSafeBytes(activation, "receipt", V2ActivationReceiptCodec.Size);
        if (read.Identity != record.ReceiptIdentity ||
            !read.Bytes.AsSpan().SequenceEqual(V2ActivationReceiptCodec.Encode(record)))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        ValidateBaseNamespace(root, false);
    }

    private static ActivationState ReadActivationState(SafeFileHandle root, SafeFileHandle activation,
        V2ActivationRecord record)
    {
        var staged = LinuxFileSystem.TryInspectChild(activation, "control", out _);
        var installed = LinuxFileSystem.TryInspectChild(root, ControlName, out _);
        if (staged == installed) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var marker = ReadSafeBytes(root, MarkerName, 91);
        var counterpart = ReadSafeBytes(activation, "marker", 91);
        var old = marker.Identity == record.OriginalMarkerIdentity && marker.Bytes.AsSpan().SequenceEqual(record.OriginalMarker.Span);
        var active = marker.Identity == record.ActiveMarkerIdentity && marker.Bytes.AsSpan().SequenceEqual(record.ActiveMarker.Span);
        var counterpartOld = counterpart.Identity == record.OriginalMarkerIdentity && counterpart.Bytes.AsSpan().SequenceEqual(record.OriginalMarker.Span);
        var counterpartNew = counterpart.Identity == record.ActiveMarkerIdentity && counterpart.Bytes.AsSpan().SequenceEqual(record.ActiveMarker.Span);
        if ((!old || !counterpartNew) && (!active || !counterpartOld || !installed))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireEntries(activation, staged ? ["gate", "marker", "receipt", "control"] : ["gate", "marker", "receipt"]);
        using var controlRoot = OpenPrivateDirectory(staged ? activation : root, staged ? "control" : ControlName, false);
        RequireActivationDirectory(controlRoot, record.ControlRootIdentity, record.EffectiveUserId);
        LinuxFileSystem.RequireEntries(controlRoot, record.ControlNamespaceId.ToString("N"));
        using var control = OpenPrivateDirectory(controlRoot, record.ControlNamespaceId.ToString("N"), false);
        RequireActivationDirectory(control, record.ControlNamespaceIdentity, record.EffectiveUserId);
        LinuxFileSystem.RequireEntries(control, "objects", "control.meta");
        var meta = ReadSafeBytes(control, "control.meta", 69);
        if (meta.Identity != record.ControlMetaIdentity || !meta.Bytes.AsSpan().SequenceEqual(record.ControlMeta.Span))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var objects = OpenPrivateDirectory(control, "objects", false);
        LinuxFileSystem.RequireEntries(objects, "native", "derived");
        using var native = OpenPrivateDirectory(objects, "native", false);
        using var derived = OpenPrivateDirectory(objects, "derived", false);
        if (!active)
        {
            LinuxFileSystem.RequireEntries(native);
            LinuxFileSystem.RequireEntries(derived);
        }
        return active ? ActivationState.Active : staged ? ActivationState.ControlStaged : ActivationState.Ready;
    }

    private static void RequireActivationDirectory(SafeFileHandle directory, LinuxFileSystem.Identity expected, uint user)
    {
        var stat = LinuxFileSystem.Inspect(directory);
        LinuxFileSystem.RequireDirectory(stat, user);
        if (stat.Identity != expected) throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private static void ProbeActivationExchange(SafeFileHandle activation)
    {
        var name = ".exchange-probe-" + Guid.NewGuid().ToString("N");
        if (!LinuxFileSystem.CreateDirectory(activation, name)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var probe = OpenPrivateDirectory(activation, name, false);
        var identity = LinuxFileSystem.Inspect(probe).Identity;
        LinuxFileSystem.Identity? first = null;
        LinuxFileSystem.Identity? second = null;
        try
        {
            WriteImmutable(probe, "first", [0xa5]);
            WriteImmutable(probe, "second", [0x5a]);
            first = ReadSafeBytes(probe, "first", 1).Identity;
            second = ReadSafeBytes(probe, "second", 1).Identity;
            using var sync = LinuxFileSystem.OpenSyncDirectory(probe);
            LinuxFileSystem.Exchange(probe, "first", probe, "second");
            LinuxFileSystem.SyncDirectory(probe, sync);
            var newFirst = ReadSafeBytes(probe, "first", 1);
            var newSecond = ReadSafeBytes(probe, "second", 1);
            if (newFirst.Identity != second || newSecond.Identity != first ||
                newFirst.Bytes[0] != 0x5a || newSecond.Bytes[0] != 0xa5)
                throw Failure(PrivateObjectStorageError.Unsupported);
        }
        finally
        {
            foreach (var child in new[] { "first", "second" })
                if (LinuxFileSystem.TryInspectChild(probe, child, out var stat))
                {
                    if (stat.Identity != first && stat.Identity != second) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                    LinuxFileSystem.UnlinkOwned(probe, child, stat.Identity);
                }
            LinuxFileSystem.UnlinkOwned(activation, name, identity, directory: true);
        }
    }
}
