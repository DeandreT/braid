using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            using var control = OpenObjectControl(reference.Id, true);
            using var gate = LinuxFileSystem.TryLockGate(control, "object.gate", false, _userId) ??
                throw Failure(PrivateObjectStorageError.Io);
            await ObserveAsync(V2StoragePhase.ReadGateAcquired, cancellationToken).ConfigureAwait(false);
            if (HasFence(control, "object.retired")) throw Failure(PrivateObjectStorageError.Missing);
            using var data = OpenDataParent(reference.Id, false);
            var result = await ReadVerifiedFileAsync(reference, data, cancellationToken).ConfigureAwait(false);
            ValidateRoot();
            ValidateDataParent(reference.Id, data);
            ValidateObjectControl(reference.Id, control);
            gate.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception error) { throw NormalizeUnexpected(error); }
    }

    private async Task<byte[]> ReadVerifiedFileAsync(PrivateObjectReference reference, SafeFileHandle parent,
        CancellationToken token)
    {
        using var file = LinuxFileSystem.OpenRegularRead(parent, FinalName(reference.Id), LinuxFileSystem.FinalFileMode, _userId);
        var initial = LinuxFileSystem.Inspect(file);
        if (initial.Size != (ulong)reference.ByteCount) throw Failure(PrivateObjectStorageError.Integrity);
        using var input = new FileStream(file, FileAccess.Read, ChunkSize, isAsync: false);
        var bytes = new byte[checked((int)reference.ByteCount)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        while (count < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(bytes.AsMemory(count, Math.Min(ChunkSize, bytes.Length - count)), token).ConfigureAwait(false);
            if (read == 0) throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(bytes.AsSpan(count, read));
            count += read;
        }
        var extra = new byte[1];
        if (await input.ReadAsync(extra.AsMemory(), token).ConfigureAwait(false) != 0 ||
            Convert.ToHexStringLower(hash.GetHashAndReset()) != reference.Digest.Value)
            throw Failure(PrivateObjectStorageError.Integrity);
        var complete = LinuxFileSystem.Inspect(file);
        LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, _userId);
        if (!LinuxFileSystem.TryInspectChild(parent, FinalName(reference.Id), out var named))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireFile(named, LinuxFileSystem.FinalFileMode, _userId);
        if (complete.Identity != initial.Identity || named.Identity != initial.Identity ||
            complete.Size != initial.Size || named.Size != initial.Size)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return bytes;
    }
}
