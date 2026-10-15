using Microsoft.Win32.SafeHandles;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.FileSystem;

public sealed partial class FileSystemV2PrivateObjectStore
{
    private sealed record Seal(V2PublicationRecord Record, Sha256Digest Digest, LinuxFileSystem.Identity Identity);
    private static string SealName(V2RecordKind kind) => kind switch
    {
        V2RecordKind.Absence => "absence",
        V2RecordKind.Reservation => "reservation",
        V2RecordKind.Bound => "bound",
        V2RecordKind.Prepared => "prepared",
        _ => throw Failure(PrivateObjectStorageError.UnsafeStorage)
    };

    private async Task<Seal> InstallSealAsync(SafeFileHandle directory, V2PublicationRecord record,
        CancellationToken cancellationToken)
    {
        var bytes = V2PublicationRecordCodec.Encode(record);
        var name = SealName(record.Kind);
        var stage = name + ".stage";
        if (LinuxFileSystem.TryInspectChild(directory, name, out _) ||
            LinuxFileSystem.TryInspectChild(directory, stage, out _))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var prefix = Math.Max(1, bytes.Length / 2);
        LinuxFileSystem.Identity identity;
        // A genuine partial closed/flushed stage, not a pre-syscall notification.
        using (var file = LinuxFileSystem.CreateTemporary(directory, stage))
        {
            var initial = LinuxFileSystem.Inspect(file);
            LinuxFileSystem.RequireFile(initial, LinuxFileSystem.TemporaryFileMode, _userId);
            identity = initial.Identity;
            using var output = new FileStream(file, FileAccess.Write, 4096, isAsync: false);
            output.Write(bytes.AsSpan(0, prefix));
            output.Flush();
            LinuxFileSystem.SyncFile(file);
        }
        var key = new PrivatePublicationKey(record.Context.RootId, record.Context.Reference.Id, record.Context.TempId);
        await ObserveAsync(V2StoragePhase.SealStageWritten, cancellationToken, key, record.Kind, identity).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using (var file = LinuxFileSystem.OpenRegularWrite(directory, stage, LinuxFileSystem.TemporaryFileMode, _userId))
        {
            var before = LinuxFileSystem.Inspect(file);
            if (before.Identity != identity || before.Size != (ulong)prefix)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            RandomAccess.Write(file, bytes.AsSpan(prefix), prefix);
            LinuxFileSystem.MakeReadOnly(file);
            LinuxFileSystem.SyncFile(file);
            var complete = LinuxFileSystem.Inspect(file);
            LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, _userId);
            if (complete.Identity != identity || complete.Size != (ulong)bytes.Length)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        using (var sync = LinuxFileSystem.OpenSyncDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!LinuxFileSystem.PublishNoReplace(directory, stage, name))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            LinuxFileSystem.SyncDirectory(directory, sync);
        }
        var installed = ReadSeal(directory, record.Kind);
        if (installed.Identity != identity || installed.Record != record)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        await ObserveAsync(V2StoragePhase.SealInstalled, cancellationToken, key, record.Kind, identity).ConfigureAwait(false);
        return installed;
    }

    private static Seal ReadSeal(SafeFileHandle directory, V2RecordKind kind)
    {
        var read = ReadSafeBytes(directory, SealName(kind), V2PublicationRecordCodec.MaximumRecordBytes);
        var record = V2PublicationRecordCodec.Decode(read.Bytes);
        if (record.Kind != kind) throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return new(record, Sha256Digest.FromBytes(read.Bytes), read.Identity);
    }

    private Seal? ReadOptionalSeal(SafeFileHandle directory, V2RecordKind kind)
    {
        if (!LinuxFileSystem.TryInspectChild(directory, SealName(kind), out _)) return null;
        return ReadSeal(directory, kind);
    }

    private void RequireNoStage(SafeFileHandle directory, V2RecordKind kind)
    {
        if (LinuxFileSystem.TryInspectChild(directory, SealName(kind) + ".stage", out _))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private void ValidateUnacceptedStage(SafeFileHandle directory, V2RecordKind kind)
    {
        if (!LinuxFileSystem.TryInspectChild(directory, SealName(kind) + ".stage", out var stat)) return;
        if ((uint)(stat.Mode & 0xfff) is not (LinuxFileSystem.TemporaryFileMode or LinuxFileSystem.FinalFileMode) ||
            stat.Size > V2PublicationRecordCodec.MaximumRecordBytes)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        LinuxFileSystem.RequireFile(stat, (uint)(stat.Mode & 0xfff), _userId);
        // It remains unaccepted and untouched, even if its bytes happen to decode.
    }
}
