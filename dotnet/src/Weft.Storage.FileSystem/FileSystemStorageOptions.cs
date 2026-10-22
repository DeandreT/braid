using System.Text;

namespace Weft.Storage.FileSystem;

/// <summary>Server-owned configuration for the qualified private local filesystem profile.</summary>
public sealed record FileSystemStorageOptions
{
    public const long NativeHardMaximum = 4 * 1024 * 1024;
    public const long DerivedHardMaximum = 16 * 1024 * 1024;

    public FileSystemStorageOptions(string rootPath, string applicationContentRoot,
        string? repositoryRoot = null, long nativeByteLimit = NativeHardMaximum,
        long derivedByteLimit = DerivedHardMaximum, int maximumConcurrency = 2)
    {
        RootPath = RequireCanonicalPath(rootPath, nameof(rootPath));
        ApplicationContentRoot = RequireCanonicalPath(applicationContentRoot, nameof(applicationContentRoot));
        RepositoryRoot = repositoryRoot is null ? null : RequireCanonicalPath(repositoryRoot, nameof(repositoryRoot));
        if (IsWithin(RootPath, ApplicationContentRoot) ||
            (RepositoryRoot is not null && IsWithin(RootPath, RepositoryRoot)))
            throw new ArgumentException("Private storage must be outside application and repository trees.", nameof(rootPath));
        if (nativeByteLimit is <= 0 or > NativeHardMaximum)
            throw new ArgumentOutOfRangeException(nameof(nativeByteLimit), "The native storage ceiling must be positive and at most 4 MiB.");
        if (derivedByteLimit is <= 0 or > DerivedHardMaximum)
            throw new ArgumentOutOfRangeException(nameof(derivedByteLimit), "The derived storage ceiling must be positive and at most 16 MiB.");
        if (maximumConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency), "Storage concurrency must be between one and four.");
        NativeByteLimit = nativeByteLimit;
        DerivedByteLimit = derivedByteLimit;
        MaximumConcurrency = maximumConcurrency;
    }

    public string RootPath { get; }
    public string ApplicationContentRoot { get; }
    public string? RepositoryRoot { get; }
    public long NativeByteLimit { get; }
    public long DerivedByteLimit { get; }
    public int MaximumConcurrency { get; }

    /// <summary>Checks one configured root's canonical spelling without consulting the filesystem.</summary>
    public static string RequireCanonicalRoot(string rootPath) => RequireCanonicalPath(rootPath, nameof(rootPath));

    private static string RequireCanonicalPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!value.StartsWith('/') || value == "/" || value.EndsWith('/') ||
            value.Contains('\0') || value.Contains('\\') ||
            value.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("Storage configuration requires canonical absolute paths without aliases.", parameterName);
        var encoding = new UTF8Encoding(false, true);
        try
        {
            if (encoding.GetByteCount(value) > 4095 || value.Split('/').Any(part => encoding.GetByteCount(part) > 255))
                throw new ArgumentException("Storage configuration paths exceed the admitted Linux path bounds.", parameterName);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Storage configuration paths must contain valid Unicode scalar values.", parameterName);
        }
        return value;
    }

    private static bool IsWithin(string path, string tree) =>
        string.Equals(path, tree, StringComparison.Ordinal) || path.StartsWith(tree + "/", StringComparison.Ordinal);
}
