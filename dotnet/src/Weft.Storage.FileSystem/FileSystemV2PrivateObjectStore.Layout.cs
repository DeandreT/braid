using Microsoft.Win32.SafeHandles;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    private static IEnumerable<string> NamespaceNames(PrivateObjectId id) =>
    [
        "objects", id.Class == PrivateObjectClass.NativeArtifact ? "native" : "derived",
        id.Scope.OrganizationId.ToString("N"), id.Scope.ProjectId.ToString("N"),
        id.Scope.MappingId.ToString("N"), id.Scope.RevisionId.ToString("N")
    ];

    private static string FinalName(PrivateObjectId id) => id.ObjectId.ToString("N") + ".blob";
    private static string TempName(Guid tempId) => ".attempt-" + tempId.ToString("N") + ".tmp";

    private static SafeFileHandle OpenPrivateDirectory(SafeFileHandle parent, string name, bool create)
    {
        if (create)
        {
            using var sync = LinuxFileSystem.OpenSyncDirectory(parent);
            if (LinuxFileSystem.CreateDirectory(parent, name)) LinuxFileSystem.SyncDirectory(parent, sync);
        }
        var directory = LinuxFileSystem.OpenDirectory(parent, name);
        try
        {
            LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(directory), LinuxFileSystem.EffectiveUserId);
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }

    private static void ValidateBaseNamespace(SafeFileHandle root, bool create)
    {
        using var objects = OpenPrivateDirectory(root, "objects", create);
        using var native = OpenPrivateDirectory(objects, "native", create);
        using var derived = OpenPrivateDirectory(objects, "derived", create);
    }

    private static SafeFileHandle OpenChain(SafeFileHandle root, IEnumerable<string> names, bool create)
    {
        SafeFileHandle? current = null;
        try
        {
            foreach (var name in names)
            {
                var next = OpenPrivateDirectory(current ?? root, name, create);
                current?.Dispose();
                current = next;
            }
            return current ?? throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        catch { current?.Dispose(); throw; }
    }

    private SafeFileHandle OpenDataParent(PrivateObjectId id, bool create) =>
        OpenChain(_root, NamespaceNames(id), create);

    private SafeFileHandle OpenObjectControl(PrivateObjectId id, bool create)
    {
        try { return OpenObjectControlCore(id, create); }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
    }

    private SafeFileHandle OpenObjectControlCore(PrivateObjectId id, bool create)
    {
        using var parent = OpenChain(_control, NamespaceNames(id), create);
        var name = id.ObjectId.ToString("N");
        if (create && !LinuxFileSystem.TryInspectChild(parent, name, out _))
        {
            var stage = ".object-init-" + Guid.NewGuid().ToString("N") + ".stage";
            if (!LinuxFileSystem.CreateDirectory(parent, stage)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            using (var staged = OpenPrivateDirectory(parent, stage, false))
            {
                CreateEmptyGate(staged, "object.gate");
                using var publications = OpenPrivateDirectory(staged, "publications", true);
                LinuxFileSystem.SyncDirectory(staged);
            }
            using var sync = LinuxFileSystem.OpenSyncDirectory(parent);
            _ = LinuxFileSystem.PublishNoReplace(parent, stage, name);
            LinuxFileSystem.SyncDirectory(parent, sync);
            // A losing owned stage is retained, including its never-recycled gate.
        }
        var result = OpenPrivateDirectory(parent, name, false);
        try
        {
            RequireEmptyFile(result, "object.gate", _userId);
            using var publications = OpenPrivateDirectory(result, "publications", false);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private SafeFileHandle CreateTempControl(SafeFileHandle objectControl, Guid tempId)
    {
        using var parent = OpenPrivateDirectory(objectControl, "publications", false);
        using var sync = LinuxFileSystem.OpenSyncDirectory(parent);
        var name = tempId.ToString("N");
        if (!LinuxFileSystem.CreateDirectory(parent, name)) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var result = OpenPrivateDirectory(parent, name, false);
        try
        {
            CreateEmptyGate(result, "temp.gate");
            LinuxFileSystem.SyncDirectory(parent, sync);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private SafeFileHandle OpenTempControl(SafeFileHandle objectControl, Guid tempId)
    {
        using var parent = OpenPrivateDirectory(objectControl, "publications", false);
        var result = OpenPrivateDirectory(parent, tempId.ToString("N"), false);
        try { RequireEmptyFile(result, "temp.gate", _userId); return result; }
        catch { result.Dispose(); throw; }
    }

    private static void CreateEmptyGate(SafeFileHandle directory, string name)
    {
        using var sync = LinuxFileSystem.OpenSyncDirectory(directory);
        using (var file = LinuxFileSystem.CreateTemporary(directory, name))
        {
            LinuxFileSystem.RequireFile(LinuxFileSystem.Inspect(file), LinuxFileSystem.TemporaryFileMode,
                LinuxFileSystem.EffectiveUserId);
            LinuxFileSystem.SyncFile(file);
        }
        LinuxFileSystem.SyncDirectory(directory, sync);
    }

    private static void RequireEmptyFile(SafeFileHandle parent, string name, uint userId)
    {
        using var file = LinuxFileSystem.OpenRegularRead(parent, name, LinuxFileSystem.TemporaryFileMode, userId);
        if (LinuxFileSystem.Inspect(file).Size != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private bool HasFence(SafeFileHandle directory, string name)
    {
        if (!LinuxFileSystem.TryInspectChild(directory, name, out var stat)) return false;
        LinuxFileSystem.RequireFile(stat, LinuxFileSystem.TemporaryFileMode, _userId);
        if (stat.Size != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        RequireEmptyFile(directory, name, _userId);
        return true;
    }

    private void EstablishFence(SafeFileHandle directory, string name)
    {
        using var sync = LinuxFileSystem.OpenSyncDirectory(directory);
        if (!LinuxFileSystem.TryInspectChild(directory, name, out _))
        {
            using var file = LinuxFileSystem.CreateTemporary(directory, name);
            LinuxFileSystem.RequireFile(LinuxFileSystem.Inspect(file), LinuxFileSystem.TemporaryFileMode, _userId);
            LinuxFileSystem.SyncFile(file);
        }
        else
        {
            using var file = LinuxFileSystem.OpenRegularWrite(directory, name, LinuxFileSystem.TemporaryFileMode, _userId);
            if (LinuxFileSystem.Inspect(file).Size != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            LinuxFileSystem.SyncFile(file);
        }
        RequireEmptyFile(directory, name, _userId);
        LinuxFileSystem.SyncDirectory(directory, sync);
    }

    private void ValidateDataParent(PrivateObjectId id, SafeFileHandle held)
    {
        ValidateNamedDirectory(() => OpenDataParent(id, false), held);
    }

    private void ValidateObjectControl(PrivateObjectId id, SafeFileHandle held)
    {
        ValidateNamedDirectory(() => OpenObjectControl(id, false), held);
    }

    private void ValidateTempControl(SafeFileHandle objectControl, Guid tempId, SafeFileHandle held)
    {
        ValidateNamedDirectory(() => OpenTempControl(objectControl, tempId), held);
    }

    private void ValidateNamedDirectory(Func<SafeFileHandle> open, SafeFileHandle held)
    {
        try
        {
            using var named = open();
            RequireSameDirectory(held, named);
        }
        catch (PrivateObjectStorageException error) when (error.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
    }

    private void RequireSameDirectory(SafeFileHandle held, SafeFileHandle named)
    {
        var original = LinuxFileSystem.Inspect(held);
        var current = LinuxFileSystem.Inspect(named);
        LinuxFileSystem.RequireDirectory(original, _userId);
        LinuxFileSystem.RequireDirectory(current, _userId);
        if (original.Identity != current.Identity) throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private void ValidatePublicationPaths(V2PublicationContext context, SafeFileHandle dataParent,
        SafeFileHandle objectControl, SafeFileHandle tempControl, LinuxFileSystem.Gate tempGate)
    {
        ValidateRoot();
        ValidateDataParent(context.Reference.Id, dataParent);
        ValidateObjectControl(context.Reference.Id, objectControl);
        ValidateTempControl(objectControl, context.TempId, tempControl);
        tempGate.Validate();
        if (LinuxFileSystem.Inspect(dataParent).Identity != LinuxFileSystem.FileIdentity(context.DataParentIdentity) ||
            LinuxFileSystem.Inspect(objectControl).Identity != LinuxFileSystem.FileIdentity(context.ObjectDirectoryIdentity) ||
            LinuxFileSystem.Inspect(tempControl).Identity != LinuxFileSystem.FileIdentity(context.TempDirectoryIdentity) ||
            tempGate.Identity != LinuxFileSystem.FileIdentity(context.TempGateIdentity))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }
}
