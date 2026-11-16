using System.Net;

namespace Braid.Storage.Blob;

/// <summary>The data-plane credential source; no account key or connection string is accepted.</summary>
public enum BlobStorageCredentialKind
{
    /// <summary>The hosting platform's managed identity (system-assigned, or user-assigned by client ID).</summary>
    ManagedIdentity = 1,
    /// <summary>A federated workload identity configured through the standard Azure identity environment.</summary>
    WorkloadIdentity = 2,
    /// <summary>The local Azurite emulator's published development account, admitted only on loopback HTTP.</summary>
    Emulator = 3
}

/// <summary>Server-owned configuration for one private Azure Blob container.</summary>
public sealed record BlobStorageOptions
{
    public const long NativeHardMaximum = 4 * 1024 * 1024;
    public const long DerivedHardMaximum = 16 * 1024 * 1024;
    internal const string EmulatorAccount = "devstoreaccount1";

    public BlobStorageOptions(Uri serviceUri, string containerName, BlobStorageCredentialKind credential,
        Guid? managedIdentityClientId = null, long nativeByteLimit = NativeHardMaximum,
        long derivedByteLimit = DerivedHardMaximum, int maximumConcurrency = 2)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        if (!Enum.IsDefined(credential))
            throw new ArgumentOutOfRangeException(nameof(credential), credential, "A blob credential source must be known.");
        ServiceUri = RequireServiceUri(serviceUri, credential);
        ContainerName = RequireContainerName(containerName);
        if (managedIdentityClientId == Guid.Empty ||
            (managedIdentityClientId is not null && credential != BlobStorageCredentialKind.ManagedIdentity))
            throw new ArgumentException("Only a managed identity accepts a nonempty client ID.", nameof(managedIdentityClientId));
        if (nativeByteLimit is <= 0 or > NativeHardMaximum)
            throw new ArgumentOutOfRangeException(nameof(nativeByteLimit), "The native storage ceiling must be positive and at most 4 MiB.");
        if (derivedByteLimit is <= 0 or > DerivedHardMaximum)
            throw new ArgumentOutOfRangeException(nameof(derivedByteLimit), "The derived storage ceiling must be positive and at most 16 MiB.");
        if (maximumConcurrency is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency), "Storage concurrency must be between one and four.");
        Credential = credential;
        ManagedIdentityClientId = managedIdentityClientId;
        NativeByteLimit = nativeByteLimit;
        DerivedByteLimit = derivedByteLimit;
        MaximumConcurrency = maximumConcurrency;
    }

    /// <summary>The account's blob service endpoint, without a container, query or credentials.</summary>
    public Uri ServiceUri { get; }
    public string ContainerName { get; }
    public BlobStorageCredentialKind Credential { get; }
    public Guid? ManagedIdentityClientId { get; }
    public long NativeByteLimit { get; }
    public long DerivedByteLimit { get; }
    public int MaximumConcurrency { get; }

    internal Uri ContainerUri => new(ServiceUri.AbsoluteUri.TrimEnd('/') + "/" + ContainerName);

    private static Uri RequireServiceUri(Uri value, BlobStorageCredentialKind credential)
    {
        if (!value.IsAbsoluteUri || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0 ||
            value.OriginalString.Any(char.IsControl) || value.OriginalString.Length > 2048)
            throw new ArgumentException("The blob service endpoint must be an absolute URI without credentials, query or fragment.", nameof(value));
        if (credential == BlobStorageCredentialKind.Emulator)
        {
            // Azurite's path-style account on this machine only; never a remote HTTP endpoint.
            if (value.Scheme != Uri.UriSchemeHttp || !IsLoopback(value.Host) ||
                value.AbsolutePath.TrimEnd('/') != "/" + EmulatorAccount)
                throw new ArgumentException("The emulator endpoint must be loopback HTTP for its development account.", nameof(value));
            return value;
        }
        if (value.Scheme != Uri.UriSchemeHttps || value.AbsolutePath != "/" || value.HostNameType != UriHostNameType.Dns ||
            !value.IsDefaultPort)
            throw new ArgumentException("The blob service endpoint must be an HTTPS account host without a path.", nameof(value));
        return value;
    }

    private static bool IsLoopback(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static string RequireContainerName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 3 or > 63 || !char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1]) ||
            value.Any(character => character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) ||
            value.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException("The private container name must be a canonical Azure container name.", nameof(value));
        return value;
    }
}
