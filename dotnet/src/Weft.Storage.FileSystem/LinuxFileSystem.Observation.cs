using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Weft.Application;

namespace Weft.Storage.FileSystem;

internal static partial class LinuxFileSystem
{
    // Names are Linux bytes, not strings. The only terminator added here is the
    // one required by the descriptor-relative native APIs.
    private static byte[] ObservationNativeName(ReadOnlySpan<byte> name)
    {
        if (name.Length is < 1 or > 255 || name.IndexOfAny((byte)0, (byte)'/') >= 0 ||
            name.SequenceEqual("."u8) || name.SequenceEqual(".."u8))
            throw Failure(PrivateObjectStorageError.Unsupported);
        var terminated = new byte[name.Length + 1];
        name.CopyTo(terminated);
        return terminated;
    }

    internal static bool TryInspectObservationChild(SafeFileHandle parent, ReadOnlySpan<byte> name, out Stat stat)
    {
        if (ObservationStatAt(Fd(parent), ObservationNativeName(name), AtSymlinkNoFollow,
            BasicStatsAndMount, out stat) == 0)
        {
            RequireStatMask(stat);
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == NotFound) return false;
        ThrowErrno(error);
        return false;
    }

    internal static SafeFileHandle OpenObservationEntry(SafeFileHandle parent, ReadOnlySpan<byte> name)
    {
        var how = new OpenHow
        {
            Flags = PathFlag | NoFollow | CloseOnExec,
            Resolve = NoSymlinks | Beneath | NoMountCrossing
        };
        var result = ObservationOpenAt2(OpenAt2SystemCall, Fd(parent), ObservationNativeName(name), ref how, 24);
        if (result < 0) ThrowErrno(Marshal.GetLastPInvokeError());
        var fd = checked((int)result);
        if (fd < 3)
        {
            var duplicate = DuplicateAboveStdio(fd, 1030, 3);
            var error = Marshal.GetLastPInvokeError();
            Close(fd);
            if (duplicate < 0) ThrowErrno(error);
            fd = duplicate;
        }
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    // A fresh open description owns its enumeration offset. Kernel prefetch is
    // permitted, but a name is parsed/consumed only when the caller asks for it.
    internal sealed class ObservationDirectory : IDisposable
    {
        private readonly SafeFileHandle _directory;
        private readonly byte[] _buffer = new byte[4096];
        private int _offset;
        private int _count;

        internal ObservationDirectory(SafeFileHandle held) => _directory = OpenSyncDirectory(held);

        internal byte[]? ReadName(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset == _count)
                {
                    var read = ReadDirectory(Fd(_directory), _buffer, (nuint)_buffer.Length);
                    if (read < 0) ThrowErrno(Marshal.GetLastPInvokeError());
                    if (read == 0) return null;
                    if (read > _buffer.Length) throw Failure(PrivateObjectStorageError.Unsupported);
                    _offset = 0;
                    _count = checked((int)read);
                }
                // linux_dirent64: ino64, off64, reclen16, type8, name[NUL].
                if (_count - _offset < 20) throw Failure(PrivateObjectStorageError.Unsupported);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_offset + 16, 2));
                if (length < 24 || length % 8 != 0 || length > _count - _offset)
                    throw Failure(PrivateObjectStorageError.Unsupported);
                var paddedName = _buffer.AsSpan(_offset + 19, length - 19);
                var end = paddedName.IndexOf((byte)0);
                if (end is < 1 or > 255) throw Failure(PrivateObjectStorageError.Unsupported);
                var name = paddedName[..end];
                _offset += length;
                if (name.SequenceEqual("."u8) || name.SequenceEqual(".."u8)) continue;
                if (name.IndexOf((byte)'/') >= 0) throw Failure(PrivateObjectStorageError.Unsupported);
                cancellationToken.ThrowIfCancellationRequested();
                return name.ToArray();
            }
        }

        // Used only by the internal qualification hook after a real entry. No
        // fabricated record is supplied; the next read is another getdents64.
        internal void Rewind()
        {
            if (ObservationSeek(Fd(_directory), 0, 0) != 0)
                throw Failure(PrivateObjectStorageError.Io);
            _offset = 0;
            _count = 0;
        }

        public void Dispose() => _directory.Dispose();
    }

    [DllImport(Libc, EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ObservationStatAt(int directory, [In] byte[] path,
        int flags, uint mask, out Stat stat);

    [DllImport(Libc, EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long ObservationOpenAt2(long number, int directory, [In] byte[] path,
        ref OpenHow how, nuint size);

    [DllImport(Libc, EntryPoint = "lseek", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long ObservationSeek(int descriptor, long offset, int origin);
}
