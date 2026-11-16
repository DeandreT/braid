using Braid.Storage.Blob;
using Braid.Storage.FileSystem;

namespace Braid.Storage.Hosting;

public enum PrivateStorageKind
{
    FileSystem = 1,
    AzureBlob = 2
}

/// <summary>
/// The explicit private storage selection read from server-owned environment settings. An absent
/// kind selects the local filesystem store. Settings that belong to the other kind are refused
/// rather than ignored, so a partial or mixed configuration never selects storage by accident.
/// </summary>
public sealed record PrivateStorageSettings
{
    public const string KindVariable = "BRAID_PRIVATE_STORAGE_KIND";
    public const string RootVariable = "BRAID_PRIVATE_STORAGE_ROOT";
    public const string BlobEndpointVariable = "BRAID_PRIVATE_STORAGE_BLOB_ENDPOINT";
    public const string BlobContainerVariable = "BRAID_PRIVATE_STORAGE_BLOB_CONTAINER";
    public const string BlobCredentialVariable = "BRAID_PRIVATE_STORAGE_BLOB_CREDENTIAL";
    public const string BlobClientIdVariable = "BRAID_PRIVATE_STORAGE_BLOB_CLIENT_ID";
    private static readonly string[] BlobVariables =
        [BlobEndpointVariable, BlobContainerVariable, BlobCredentialVariable, BlobClientIdVariable];

    private PrivateStorageSettings(PrivateStorageKind kind, string? rootPath, BlobStorageOptions? blob)
    {
        Kind = kind;
        RootPath = rootPath;
        Blob = blob;
    }

    public PrivateStorageKind Kind { get; }
    /// <summary>The canonical filesystem root; null for Azure Blob.</summary>
    public string? RootPath { get; }
    /// <summary>The validated Blob options; null for the filesystem.</summary>
    public BlobStorageOptions? Blob { get; }

    public static PrivateStorageSettings ForFileSystem(string rootPath) =>
        new(PrivateStorageKind.FileSystem, FileSystemStorageOptions.RequireCanonicalRoot(rootPath), null);

    public static PrivateStorageSettings ForBlob(BlobStorageOptions options) =>
        new(PrivateStorageKind.AzureBlob, null, options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>Parses only the selected kind: absent or <c>filesystem</c>, or <c>azure-blob</c>.</summary>
    public static PrivateStorageKind ReadKind(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment(KindVariable) switch
        {
            null or "filesystem" => PrivateStorageKind.FileSystem,
            "azure-blob" => PrivateStorageKind.AzureBlob,
            _ => throw new ArgumentException("The private storage kind must be filesystem or azure-blob.")
        };
    }

    /// <summary>
    /// Reads the complete selection. A missing required setting throws
    /// <see cref="InvalidOperationException"/>; a malformed, unknown or conflicting one throws
    /// <see cref="ArgumentException"/>. No storage is contacted.
    /// </summary>
    public static PrivateStorageSettings Read(Func<string, string?> environment)
    {
        var kind = ReadKind(environment);
        if (kind == PrivateStorageKind.FileSystem)
        {
            if (BlobVariables.Any(name => environment(name) is not null))
                throw new ArgumentException("Filesystem private storage refuses Azure Blob settings.");
            var root = environment(RootVariable);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Private storage requires an explicit private storage root.");
            return ForFileSystem(root);
        }
        return ReadBlob(environment);
    }

    /// <summary>Reads Azure Blob settings; the kind must be explicitly <c>azure-blob</c>.</summary>
    public static PrivateStorageSettings ReadBlob(Func<string, string?> environment)
    {
        if (ReadKind(environment) != PrivateStorageKind.AzureBlob)
            throw new ArgumentException("Azure Blob private storage must be selected explicitly.");
        if (environment(RootVariable) is not null)
            throw new ArgumentException("Azure Blob private storage refuses a filesystem root.");
        var endpoint = Required(environment, BlobEndpointVariable);
        var container = Required(environment, BlobContainerVariable);
        var credential = Required(environment, BlobCredentialVariable) switch
        {
            "managed-identity" => BlobStorageCredentialKind.ManagedIdentity,
            "workload-identity" => BlobStorageCredentialKind.WorkloadIdentity,
            "emulator" => BlobStorageCredentialKind.Emulator,
            _ => throw new ArgumentException("The blob credential must be managed-identity, workload-identity or emulator.")
        };
        Guid? clientId = null;
        if (environment(BlobClientIdVariable) is { } text)
        {
            if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString("D"))
                throw new ArgumentException("The managed identity client ID must be a canonical lowercase UUID.");
            clientId = id;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new ArgumentException("The blob service endpoint must be an absolute URI.");
        return ForBlob(new BlobStorageOptions(uri, container, credential, clientId));
    }

    /// <summary>Binds the selection to one host's application tree; a filesystem root must lie outside it.</summary>
    public PrivateStorageConfiguration Bind(string applicationContentRoot, string? repositoryRoot = null) => Kind switch
    {
        PrivateStorageKind.FileSystem => PrivateStorageConfiguration.ForFileSystem(
            new FileSystemStorageOptions(RootPath!, applicationContentRoot, repositoryRoot)),
        _ => PrivateStorageConfiguration.ForBlob(Blob!)
    };

    private static string Required(Func<string, string?> environment, string name) =>
        environment(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Azure Blob private storage requires {name}.");
}
