using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Braid.Application;
using Braid.Domain;

namespace Braid.Storage.FileSystem;

/// <summary>Private, immutable local objects on the explicitly admitted Linux filesystem profile.</summary>
public sealed class FileSystemPrivateObjectStore : IPrivateObjectStore, IDisposable, IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;
    private const string MarkerName = ".braid-private-storage";
    private const string MarkerPrefix = "braid-private-storage-v1\n";
    private readonly FileSystemStorageOptions _options;
    private readonly SafeFileHandle _root;
    private readonly LinuxFileSystem.Identity _rootIdentity;
    private readonly Marker _marker;
    private readonly uint _userId;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _operations;
    private bool _disposing;
    private Task? _disposeTask;

    private sealed record Marker(Guid Id, LinuxFileSystem.Identity Identity);

    private FileSystemPrivateObjectStore(FileSystemStorageOptions options, SafeFileHandle root, Marker marker)
    {
        _options = options;
        _root = root;
        _rootIdentity = LinuxFileSystem.Inspect(root).Identity;
        _marker = marker;
        _userId = LinuxFileSystem.EffectiveUserId;
        _concurrency = new SemaphoreSlim(options.MaximumConcurrency, options.MaximumConcurrency);
    }

    /// <summary>Explicitly initializes a new, unactivated v2 component root.</summary>
    public static FileSystemV2PrivateObjectStore InitializeV2(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null) =>
        FileSystemV2PrivateObjectStore.Initialize(options, registryFactory);

    /// <summary>Validates an existing v2 root without adopting a v1 root.</summary>
    public static FileSystemV2PrivateObjectStore OpenV2(FileSystemStorageOptions options,
        Func<IPrivateObjectPublicationRegistry>? registryFactory = null) =>
        FileSystemV2PrivateObjectStore.Open(options, registryFactory);

    /// <summary>Explicitly creates one new private root beneath an existing verified parent.</summary>
    public static FileSystemPrivateObjectStore Initialize(FileSystemStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenConfigured(() =>
        {
            LinuxFileSystem.RequireProfile();
            ValidateEnvironment(options, rootExists: false);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(options.RootPath)!);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(parent), LinuxFileSystem.EffectiveUserId);
            LinuxFileSystem.RequireLocalFileSystem(parent);
            var leaf = Path.GetFileName(options.RootPath);
            if (!LinuxFileSystem.CreateDirectory(parent, leaf))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var root = LinuxFileSystem.OpenConfiguredRoot(parent, leaf);
            try
            {
                LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(root), LinuxFileSystem.EffectiveUserId);
                LinuxFileSystem.RequireLocalFileSystem(root);
                ValidateBaseNamespace(root, create: true);
                CreateMarker(root);
                var marker = ReadMarker(root);
                ProbePublication(root);
                var store = new FileSystemPrivateObjectStore(options, root, marker);
                store.ValidateRoot();
                return store;
            }
            catch { root.Dispose(); throw; }
        });
    }

    /// <summary>Reopens a marked root without adopting, repairing, inventorying, or sweeping it.</summary>
    public static FileSystemPrivateObjectStore Open(FileSystemStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenConfigured(() =>
        {
            LinuxFileSystem.RequireProfile();
            ValidateEnvironment(options, rootExists: true);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(options.RootPath)!);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(parent), LinuxFileSystem.EffectiveUserId);
            var root = LinuxFileSystem.OpenConfiguredRoot(parent, Path.GetFileName(options.RootPath));
            try
            {
                LinuxFileSystem.RequireDirectory(LinuxFileSystem.Inspect(root), LinuxFileSystem.EffectiveUserId);
                LinuxFileSystem.RequireLocalFileSystem(root);
                var marker = ReadMarker(root);
                ValidateBaseNamespace(root, create: false);
                ProbePublication(root);
                var store = new FileSystemPrivateObjectStore(options, root, marker);
                store.ValidateRoot();
                return store;
            }
            catch { root.Dispose(); throw; }
        });
    }

    public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        ArgumentNullException.ThrowIfNull(source);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            using var directory = OpenNamespace(reference.Id, create: true);
            var temporary = $".attempt-{Guid.NewGuid():N}.tmp";
            LinuxFileSystem.Identity? temporaryIdentity = null;
            try
            {
                using (var handle = LinuxFileSystem.CreateTemporary(directory, temporary))
                {
                    var initial = LinuxFileSystem.Inspect(handle);
                    temporaryIdentity = initial.Identity;
                    LinuxFileSystem.RequireFile(initial, LinuxFileSystem.TemporaryFileMode, _userId);
                    using var output = new FileStream(handle, FileAccess.Write, ChunkSize);
                    await WriteVerifiedAsync(reference, source, output, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Flush(flushToDisk: true);
                    cancellationToken.ThrowIfCancellationRequested();
                    LinuxFileSystem.MakeReadOnly(handle);
                    var complete = LinuxFileSystem.Inspect(handle);
                    LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, _userId);
                    if (complete.Identity != temporaryIdentity || complete.Size != (ulong)reference.ByteCount)
                        throw Failure(PrivateObjectStorageError.UnsafeStorage);
                }

                // The writable descriptor is closed and the complete object is 0400 before rename.
                ValidateRoot();
                ValidateNamespace(reference.Id, directory);
                cancellationToken.ThrowIfCancellationRequested();
                if (LinuxFileSystem.PublishNoReplace(directory, temporary, FinalName(reference.Id)))
                {
                    // Successful publication has no subsequent filesystem cleanup or cancellation check.
                    temporaryIdentity = null;
                    return PrivateObjectPublishResult.Created;
                }

                try
                {
                    await ReadVerifiedFileAsync(reference, directory, cancellationToken).ConfigureAwait(false);
                    ValidateRoot();
                    ValidateNamespace(reference.Id, directory);
                    cancellationToken.ThrowIfCancellationRequested();
                    return PrivateObjectPublishResult.AlreadyPresent;
                }
                catch (PrivateObjectStorageException exception) when
                    (exception.Error is PrivateObjectStorageError.Integrity or PrivateObjectStorageError.Missing)
                {
                    throw Failure(PrivateObjectStorageError.Conflict);
                }
            }
            finally
            {
                if (temporaryIdentity is { } identity)
                    LinuxFileSystem.UnlinkOwned(directory, temporary, identity);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception exception) { throw NormalizeUnexpected(exception); }
    }

    public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope,
        PrivateObjectReference reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(expectedScope, reference);
        using var operation = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateRoot();
            using var directory = OpenNamespace(reference.Id, create: false);
            var bytes = await ReadVerifiedFileAsync(reference, directory, cancellationToken).ConfigureAwait(false);
            ValidateRoot();
            ValidateNamespace(reference.Id, directory);
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch (PrivateObjectStorageException) { throw; }
        catch (Exception exception) { throw NormalizeUnexpected(exception); }
    }

    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(BeginDispose());

    private async Task WriteVerifiedAsync(PrivateObjectReference reference, Stream source, FileStream output,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        var ceiling = LimitFor(reference.Id.Class);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read is < 0 or > ChunkSize)
                throw Failure(PrivateObjectStorageError.Io);
            if (read == 0)
                break;
            count += read;
            if (count > ceiling)
                throw Failure(PrivateObjectStorageError.Limit);
            if (count > reference.ByteCount)
                throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (count != reference.ByteCount ||
            !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), reference.Digest.Value, StringComparison.Ordinal))
            throw Failure(PrivateObjectStorageError.Integrity);
    }

    private async Task<byte[]> ReadVerifiedFileAsync(PrivateObjectReference reference, SafeFileHandle directory,
        CancellationToken cancellationToken)
    {
        using var handle = LinuxFileSystem.OpenRegularRead(directory, FinalName(reference.Id),
            LinuxFileSystem.FinalFileMode, _userId);
        var initial = LinuxFileSystem.Inspect(handle);
        if (initial.Size != (ulong)reference.ByteCount)
            throw Failure(PrivateObjectStorageError.Integrity);
        using var input = new FileStream(handle, FileAccess.Read, ChunkSize);
        var bytes = new byte[checked((int)reference.ByteCount)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        while (count < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(bytes.AsMemory(count, Math.Min(ChunkSize, bytes.Length - count)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw Failure(PrivateObjectStorageError.Integrity);
            hash.AppendData(bytes.AsSpan(count, read));
            count += read;
        }
        var extra = new byte[1];
        if (await input.ReadAsync(extra.AsMemory(), cancellationToken).ConfigureAwait(false) != 0 ||
            !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), reference.Digest.Value, StringComparison.Ordinal))
            throw Failure(PrivateObjectStorageError.Integrity);
        var complete = LinuxFileSystem.Inspect(handle);
        LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, _userId);
        if (complete.Identity != initial.Identity || complete.Size != initial.Size)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return bytes;
    }

    private void ValidateReference(RevisionScope expectedScope, PrivateObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(expectedScope);
        ArgumentNullException.ThrowIfNull(reference);
        if (expectedScope != reference.Id.Scope)
            throw Failure(PrivateObjectStorageError.ScopeMismatch);
        if (reference.ByteCount > LimitFor(reference.Id.Class) ||
            (reference.Id.Class == PrivateObjectClass.NativeArtifact && reference.ByteCount == 0))
            throw Failure(PrivateObjectStorageError.Limit);
    }

    private long LimitFor(PrivateObjectClass @class) => @class switch
    {
        PrivateObjectClass.NativeArtifact => _options.NativeByteLimit,
        PrivateObjectClass.DerivedArtifact => _options.DerivedByteLimit,
        _ => throw Failure(PrivateObjectStorageError.Unsupported)
    };

    private void ValidateRoot()
    {
        try
        {
            var held = LinuxFileSystem.Inspect(_root);
            LinuxFileSystem.RequireDirectory(held, _userId);
            using var parent = LinuxFileSystem.OpenAbsoluteDirectory(Path.GetDirectoryName(_options.RootPath)!);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(parent), _userId);
            using var named = LinuxFileSystem.OpenConfiguredRoot(parent, Path.GetFileName(_options.RootPath));
            var current = LinuxFileSystem.Inspect(named);
            LinuxFileSystem.RequireDirectory(current, _userId);
            if (current.Identity != _rootIdentity || held.Identity != _rootIdentity)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
            var marker = ReadMarker(_root);
            if (marker != _marker || LinuxFileSystem.ContainsGitMarker(_root))
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        catch (PrivateObjectStorageException exception) when (exception.Error == PrivateObjectStorageError.Missing)
        {
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
    }

    private SafeFileHandle OpenNamespace(PrivateObjectId id, bool create)
    {
        SafeFileHandle? current = null;
        try
        {
            foreach (var name in NamespaceNames(id))
            {
                var next = OpenPrivateDirectory(current ?? _root, name, create);
                current?.Dispose();
                current = next;
            }
            return current!;
        }
        catch { current?.Dispose(); throw; }
    }

    private void ValidateNamespace(PrivateObjectId id, SafeFileHandle held)
    {
        try
        {
            var original = LinuxFileSystem.Inspect(held);
            LinuxFileSystem.RequireDirectory(original, _userId);
            using var named = OpenNamespace(id, create: false);
            if (LinuxFileSystem.Inspect(named).Identity != original.Identity)
                throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
        catch (PrivateObjectStorageException exception) when (exception.Error == PrivateObjectStorageError.Missing)
        {
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        }
    }

    private static IEnumerable<string> NamespaceNames(PrivateObjectId id) =>
    [
        "objects", id.Class == PrivateObjectClass.NativeArtifact ? "native" : "derived",
        id.Scope.OrganizationId.ToString("N"), id.Scope.ProjectId.ToString("N"),
        id.Scope.MappingId.ToString("N"), id.Scope.RevisionId.ToString("N")
    ];

    private static string FinalName(PrivateObjectId id) => id.ObjectId.ToString("N") + ".blob";

    private static SafeFileHandle OpenPrivateDirectory(SafeFileHandle parent, string name, bool create)
    {
        if (create)
            LinuxFileSystem.CreateDirectory(parent, name);
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

    private static void ValidateEnvironment(FileSystemStorageOptions options, bool rootExists)
    {
        using var application = LinuxFileSystem.OpenAbsoluteDirectory(options.ApplicationContentRoot);
        if (options.RepositoryRoot is { } repository)
        {
            using var repositoryDirectory = LinuxFileSystem.OpenAbsoluteDirectory(repository);
        }
        var ancestor = rootExists ? options.RootPath : Path.GetDirectoryName(options.RootPath)!;
        while (true)
        {
            using var directory = LinuxFileSystem.OpenAbsoluteDirectory(ancestor);
            LinuxFileSystem.RequireSafeParent(LinuxFileSystem.Inspect(directory), LinuxFileSystem.EffectiveUserId);
            if (LinuxFileSystem.ContainsGitMarker(directory))
                throw new ArgumentException("Private storage must be outside detected Git repository trees.", nameof(options));
            if (ancestor == "/")
                break;
            ancestor = Path.GetDirectoryName(ancestor)!;
        }
    }

    private static void CreateMarker(SafeFileHandle root)
    {
        using var handle = LinuxFileSystem.CreateTemporary(root, MarkerName);
        LinuxFileSystem.RequireFile(LinuxFileSystem.Inspect(handle), LinuxFileSystem.TemporaryFileMode,
            LinuxFileSystem.EffectiveUserId);
        using var output = new FileStream(handle, FileAccess.Write, ChunkSize, isAsync: false);
        output.Write(Encoding.ASCII.GetBytes(MarkerPrefix + Guid.NewGuid().ToString("N") + "\n"));
        output.Flush(flushToDisk: true);
        LinuxFileSystem.MakeReadOnly(handle);
    }

    private static Marker ReadMarker(SafeFileHandle root)
    {
        using var handle = LinuxFileSystem.OpenRegularRead(root, MarkerName, LinuxFileSystem.FinalFileMode,
            LinuxFileSystem.EffectiveUserId);
        var initial = LinuxFileSystem.Inspect(handle);
        var expectedLength = MarkerPrefix.Length + 33;
        if (initial.Size != (ulong)expectedLength)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var input = new FileStream(handle, FileAccess.Read, expectedLength, isAsync: false);
        var bytes = new byte[expectedLength];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal) || text[^1] != '\n' ||
            !Guid.TryParseExact(text.AsSpan(MarkerPrefix.Length, 32), "N", out var id) || id == Guid.Empty ||
            !string.Equals(text, MarkerPrefix + id.ToString("N") + "\n", StringComparison.Ordinal))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var complete = LinuxFileSystem.Inspect(handle);
        LinuxFileSystem.RequireFile(complete, LinuxFileSystem.FinalFileMode, LinuxFileSystem.EffectiveUserId);
        if (initial.Identity != complete.Identity || initial.Size != complete.Size)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return new Marker(id, initial.Identity);
    }

    private static void ProbePublication(SafeFileHandle root)
    {
        var name = $".probe-{Guid.NewGuid():N}";
        if (!LinuxFileSystem.CreateDirectory(root, name))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        using var directory = OpenPrivateDirectory(root, name, create: false);
        var directoryIdentity = LinuxFileSystem.Inspect(directory).Identity;
        LinuxFileSystem.Identity? first = null;
        LinuxFileSystem.Identity? second = null;
        LinuxFileSystem.Identity? published = null;
        try
        {
            first = WriteProbe(directory, "first", 0xa5);
            second = WriteProbe(directory, "second", 0x5a);
            if (!LinuxFileSystem.PublishNoReplace(directory, "first", "published"))
                throw Failure(PrivateObjectStorageError.Unsupported);
            published = first;
            if (LinuxFileSystem.PublishNoReplace(directory, "second", "published"))
            {
                published = second;
                throw Failure(PrivateObjectStorageError.Unsupported);
            }
            if (!ProbeContent(directory, "published", 0xa5) || !ProbeContent(directory, "second", 0x5a))
                throw Failure(PrivateObjectStorageError.Unsupported);
        }
        finally
        {
            if (first is { } firstIdentity)
                LinuxFileSystem.UnlinkOwned(directory, "first", firstIdentity);
            if (second is { } secondIdentity)
                LinuxFileSystem.UnlinkOwned(directory, "second", secondIdentity);
            if (published is { } publishedIdentity)
                LinuxFileSystem.UnlinkOwned(directory, "published", publishedIdentity);
            LinuxFileSystem.UnlinkOwned(root, name, directoryIdentity, directory: true);
        }
    }

    private static LinuxFileSystem.Identity WriteProbe(SafeFileHandle directory, string name, byte value)
    {
        using var handle = LinuxFileSystem.CreateTemporary(directory, name);
        var stat = LinuxFileSystem.Inspect(handle);
        LinuxFileSystem.RequireFile(stat, LinuxFileSystem.TemporaryFileMode, LinuxFileSystem.EffectiveUserId);
        using var output = new FileStream(handle, FileAccess.Write, 1, isAsync: false);
        output.WriteByte(value);
        output.Flush(flushToDisk: true);
        LinuxFileSystem.MakeReadOnly(handle);
        return stat.Identity;
    }

    private static bool ProbeContent(SafeFileHandle directory, string name, byte value)
    {
        using var handle = LinuxFileSystem.OpenRegularRead(directory, name, LinuxFileSystem.FinalFileMode,
            LinuxFileSystem.EffectiveUserId);
        using var input = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        return input.ReadByte() == value && input.ReadByte() == -1;
    }

    private static FileSystemPrivateObjectStore OpenConfigured(Func<FileSystemPrivateObjectStore> open)
    {
        try { return open(); }
        catch (PrivateObjectStorageException exception) when (exception.Error == PrivateObjectStorageError.Missing)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
        catch (PrivateObjectStorageException) { throw; }
        catch (ArgumentException) { throw; }
        catch (Exception exception) { throw NormalizeUnexpected(exception); }
    }

    private async Task<Operation> AcquireOperationAsync(CancellationToken cancellationToken)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _operations++;
        }
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Operation(this);
        }
        catch { ReleaseOperation(acquired: false); throw; }
    }

    private void ReleaseOperation(bool acquired)
    {
        if (acquired)
            _concurrency.Release();
        lock (_lifetime)
        {
            _operations--;
            if (_disposing && _operations == 0)
                _drained.TrySetResult(true);
        }
    }

    private Task BeginDispose()
    {
        lock (_lifetime)
        {
            if (_disposeTask is not null)
                return _disposeTask;
            _disposing = true;
            if (_operations == 0)
                _drained.TrySetResult(true);
            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _drained.Task.ConfigureAwait(false);
        _root.Dispose();
        _concurrency.Dispose();
    }

    private sealed class Operation(FileSystemPrivateObjectStore owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.ReleaseOperation(acquired: true);
        }
    }

    private static PrivateObjectStorageException NormalizeUnexpected(Exception exception) => Failure(exception is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            ? PrivateObjectStorageError.Unsupported : PrivateObjectStorageError.Io);

    private static PrivateObjectStorageException Failure(PrivateObjectStorageError error) => new(error);
}
