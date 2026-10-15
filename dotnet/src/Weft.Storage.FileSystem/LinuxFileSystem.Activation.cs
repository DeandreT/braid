using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Weft.Application;

namespace Weft.Storage.FileSystem;

internal static partial class LinuxFileSystem
{
    internal static bool MoveNoReplace(SafeFileHandle from, string source, SafeFileHandle to, string destination)
    {
        if (RenameNoReplace(Fd(from), source, Fd(to), destination, 1) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == AlreadyExists) return false;
        ThrowErrno(error); return false;
    }
    internal static void Exchange(SafeFileHandle from, string source, SafeFileHandle to, string destination)
    {
        if (RenameNoReplace(Fd(from), source, Fd(to), destination, 2) != 0)
            ThrowErrno(Marshal.GetLastPInvokeError());
    }

    // Only fixed newly-owned control directories are enumerated, never payload trees.
    internal static void RequireEntries(SafeFileHandle directory, params string[] expected)
    {
        using var ordinary = OpenSyncDirectory(directory);
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        var buffer = new byte[4096];
        for (var batch = 0; batch < 4; batch++)
        {
            var read = ReadDirectory(Fd(ordinary), buffer, (nuint)buffer.Length);
            if (read < 0) ThrowErrno(Marshal.GetLastPInvokeError());
            if (read == 0)
            {
                if (remaining.Count != 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
                return;
            }
            if (read > buffer.Length) throw Failure(PrivateObjectStorageError.Unsupported);
            var offset = 0;
            while (offset < read)
            {
                // Linux dirent64: ino64,off64,reclen16,type8,name[NUL].
                if (read - offset < 20) throw Failure(PrivateObjectStorageError.Unsupported);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 16, 2));
                if (length < 20 || length > read - offset) throw Failure(PrivateObjectStorageError.Unsupported);
                var nameBytes = buffer.AsSpan(offset + 19, length - 19);
                var end = nameBytes.IndexOf((byte)0);
                if (end < 0) throw Failure(PrivateObjectStorageError.Unsupported);
                var name = Encoding.ASCII.GetString(nameBytes[..end]);
                if (name is not ("." or "..") && !remaining.Remove(name))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                offset += length;
            }
        }
        throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    [DllImport(Libc, EntryPoint = "getdents64", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long ReadDirectory(int fd, [Out] byte[] buffer, nuint count);
}
