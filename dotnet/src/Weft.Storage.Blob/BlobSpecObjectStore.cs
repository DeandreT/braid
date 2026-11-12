using System.Text;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Weft.Application;
using Weft.Domain;

namespace Weft.Storage.Blob;

/// <summary>
/// Separate bundle namespace in an explicitly private container. Conditional single-object publication avoids named
/// temporary objects: caller bytes are bounded and verified before upload. The required registry owns final admission,
/// crash recovery and cross-process serialization. The optional spec intake host opens this adapter only after registry admission.
/// </summary>
public sealed class BlobSpecObjectStore : SpecObjectStore
{
    internal const string MarkerName = "spec-v1-store";
    private readonly BlobContainerClient container;
    private readonly ETag markerTag;
    private readonly byte[] markerBytes;

    private BlobSpecObjectStore(Guid storeId, ISpecObjectRegistry registry, BlobContainerClient container, ETag markerTag)
        : base(storeId, registry)
    {
        this.container = container;
        this.markerTag = markerTag;
        markerBytes = Marker(storeId);
    }

    public static Task<BlobSpecObjectStore> OpenAsync(BlobStorageOptions options, Guid storeId, ISpecObjectRegistry? registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (registry is null) throw new SpecStorageException(SpecStorageError.Unavailable);
        return OpenAsync(BlobPrivateObjectStore.CreateContainerClient(options), storeId, registry, false, cancellationToken);
    }

    /// <summary>Explicitly creates only the disjoint marker in an existing private container; never creates a container.</summary>
    public static Task<BlobSpecObjectStore> InitializeAsync(BlobStorageOptions options, Guid storeId, ISpecObjectRegistry? registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (registry is null) throw new SpecStorageException(SpecStorageError.Unavailable);
        return OpenAsync(BlobPrivateObjectStore.CreateContainerClient(options), storeId, registry, true, cancellationToken);
    }

    internal static async Task<BlobSpecObjectStore> OpenAsync(BlobContainerClient container, Guid storeId,
        ISpecObjectRegistry? registry, bool initialize, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (storeId == Guid.Empty) throw new ArgumentException("A store identity is required.", nameof(storeId));
        if (registry is null) throw new SpecStorageException(SpecStorageError.Unavailable);
        try
        {
            await RequirePrivateAsync(container, token).ConfigureAwait(false);
            var marker = container.GetBlobClient(MarkerName);
            var bytes = Marker(storeId);
            if (initialize)
            {
                using var input = new MemoryStream(bytes, false);
                try { await marker.UploadAsync(input, UploadOptions(), token).ConfigureAwait(false); }
                catch (RequestFailedException e) when (e.Status is 409 or 412) { }
            }
            var tag = await CheckMarkerAsync(marker, bytes, null, token).ConfigureAwait(false);
            return new(storeId, registry, container, tag);
        }
        catch (Exception error) { throw Normalize(error, token); }
    }

    protected override async Task<SpecPublishResult> PublishCoreAsync(SpecPublicationClaim claim, byte[] verifiedBytes, CancellationToken token)
    {
        try
        {
            await CheckRootAsync(token).ConfigureAwait(false);
            using var stream = new MemoryStream(verifiedBytes, false);
            try
            {
                var options = UploadOptions();
                options.Metadata = Metadata(claim.Reference, "live");
                await container.GetBlobClient(ObjectName(claim.Reference)).UploadAsync(stream, options, token).ConfigureAwait(false);
                return SpecPublishResult.Created;
            }
            catch (RequestFailedException e) when (e.Status is 409 or 412)
            {
                try { await DownloadAsync(claim.Reference, token).ConfigureAwait(false); }
                catch (SpecStorageException failure) when (failure.Error is SpecStorageError.Integrity or SpecStorageError.Missing)
                { throw new SpecStorageException(SpecStorageError.Conflict); }
                return SpecPublishResult.AlreadyPresent;
            }
        }
        catch (Exception error) { throw Normalize(error, token); }
    }

    protected override async Task<byte[]> ReadCoreAsync(SpecObjectReference reference, CancellationToken token)
    {
        try
        {
            await CheckRootAsync(token).ConfigureAwait(false);
            return (await DownloadAsync(reference, token).ConfigureAwait(false)).Bytes;
        }
        catch (Exception error) { throw Normalize(error, token); }
    }

    protected override async Task<SpecRetireResult> RetireCoreAsync(SpecRetirementClaim claim, CancellationToken token)
    {
        try
        {
            await CheckRootAsync(token).ConfigureAwait(false);
            var blob = container.GetBlobClient(ObjectName(claim.Reference));
            ETag? tag = null;
            var present = false;
            try
            {
                var response = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), token).ConfigureAwait(false);
                using var body = response.Value.Content;
                ValidateMetadata(claim.Reference, response.Value.Details.Metadata);
                if (response.Value.Details.Metadata["state"] == "retired")
                {
                    if (response.Value.Details.ContentLength != 0 || await body.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                        throw new SpecStorageException(SpecStorageError.Integrity);
                    return SpecRetireResult.Missing;
                }
                await SpecObjectBytes.ReadVerifiedAsync(claim.Reference, body, token).ConfigureAwait(false);
                tag = response.Value.Details.ETag;
                present = true;
            }
            catch (RequestFailedException e) when (e.Status == 404) { }
            // Keep a zero-payload physical fence at the SAME name. Deletion would allow a delayed create-only
            // request to resurrect an object after its DB claim/worker had expired. Retired names are never reused.
            var options = UploadOptions();
            options.Conditions = tag is { } known ? new BlobRequestConditions { IfMatch = known } : new BlobRequestConditions { IfNoneMatch = ETag.All };
            options.Metadata = Metadata(claim.Reference, "retired");
            using var empty = new MemoryStream([]);
            await blob.UploadAsync(empty, options, token).ConfigureAwait(false);
            return present ? SpecRetireResult.Retired : SpecRetireResult.Missing;
        }
        catch (Exception error) { throw Normalize(error, token); }
    }

    private async Task<(byte[] Bytes, ETag Tag)> DownloadAsync(SpecObjectReference reference, CancellationToken token)
    {
        try
        {
            var response = await container.GetBlobClient(ObjectName(reference)).DownloadStreamingAsync(new BlobDownloadOptions(), token).ConfigureAwait(false);
            using var body = response.Value.Content;
            ValidateMetadata(reference, response.Value.Details.Metadata);
            if (response.Value.Details.Metadata["state"] == "retired") throw new SpecStorageException(SpecStorageError.Missing);
            if (response.Value.Details.ContentLength != reference.ByteCount) throw new SpecStorageException(SpecStorageError.Integrity);
            var bytes = await SpecObjectBytes.ReadVerifiedAsync(reference, body, token).ConfigureAwait(false);
            return (bytes, response.Value.Details.ETag);
        }
        catch (Exception error) { throw Normalize(error, token); }
    }

    private async Task CheckRootAsync(CancellationToken token)
    {
        await RequirePrivateAsync(container, token).ConfigureAwait(false);
        await CheckMarkerAsync(container.GetBlobClient(MarkerName), markerBytes, markerTag, token).ConfigureAwait(false);
    }

    private static async Task RequirePrivateAsync(BlobContainerClient container, CancellationToken token)
    {
        var response = await container.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false);
        if (response.Value.PublicAccess != PublicAccessType.None) throw new SpecStorageException(SpecStorageError.UnsafeStorage);
    }

    private static async Task<ETag> CheckMarkerAsync(BlobClient marker, byte[] expected, ETag? tag, CancellationToken token)
    {
        var response = await marker.DownloadStreamingAsync(new BlobDownloadOptions(), token).ConfigureAwait(false);
        using var body = response.Value.Content;
        if (response.Value.Details.ContentLength != expected.Length || tag is { } known && known != response.Value.Details.ETag)
            throw new SpecStorageException(SpecStorageError.UnsafeStorage);
        var bytes = new byte[expected.Length + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await body.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length != expected.Length || !bytes.AsSpan(0, length).SequenceEqual(expected)) throw new SpecStorageException(SpecStorageError.UnsafeStorage);
        return response.Value.Details.ETag;
    }

    private static byte[] Marker(Guid storeId) => Encoding.ASCII.GetBytes($"weft.spec-store.v1\n{storeId:N}\n");
    private static Dictionary<string, string> Metadata(SpecObjectReference reference, string state) => new()
    {
        ["state"] = state,
        ["digest"] = reference.Digest.Value,
        ["length"] = reference.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
    private static void ValidateMetadata(SpecObjectReference reference, IDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("state", out var state) || state is not ("live" or "retired") ||
            !metadata.TryGetValue("digest", out var digest) || digest != reference.Digest.Value ||
            !metadata.TryGetValue("length", out var length) || length != reference.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new SpecStorageException(SpecStorageError.Integrity);
    }
    private static BlobUploadOptions UploadOptions() => new()
    {
        Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        TransferOptions = new StorageTransferOptions
        {
            InitialTransferSize = SpecObjectReference.MaximumBytes,
            MaximumTransferSize = SpecObjectReference.MaximumBytes,
            MaximumConcurrency = 1
        }
    };
    private static Exception Normalize(Exception error, CancellationToken token) => error switch
    {
        OperationCanceledException when token.IsCancellationRequested => error,
        SpecStorageException => error,
        RequestFailedException { Status: 404 } => new SpecStorageException(SpecStorageError.Missing),
        RequestFailedException { Status: 401 or 403 } => new SpecStorageException(SpecStorageError.UnsafeStorage),
        RequestFailedException { Status: 409 or 412 } => new SpecStorageException(SpecStorageError.Conflict),
        _ => new SpecStorageException(SpecStorageError.Io)
    };
}
