using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Braid.Application;

namespace Braid.Storage.Blob;

public sealed partial class BlobPrivateObjectStore
{
    private static async Task<BlobPrivateObjectStore> OpenConfiguredAsync(BlobStorageOptions options,
        BlobContainerClient container, Func<IPrivateObjectPublicationRegistry>? factory, IBlobStorageObserver? observer,
        bool initialize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(container);
        if (!string.Equals(container.Name, options.ContainerName, StringComparison.Ordinal))
            throw new ArgumentException("The container client must address the configured container.", nameof(container));
        try
        {
            await RequirePrivateContainerAsync(container, cancellationToken).ConfigureAwait(false);
            if (initialize)
            {
                // A fresh store is never adopted from existing contents.
                await foreach (var _ in container.GetBlobsAsync(new GetBlobsOptions(), cancellationToken).ConfigureAwait(false))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
                var rootId = Guid.NewGuid();
                var controlId = Guid.NewGuid();
                await ProbeAsync(container, controlId, cancellationToken).ConfigureAwait(false);
                if (!await TryCreateAsync(container.GetBlockBlobClient(BlobLayout.MarkerName), BlobLayout.Marker(rootId, controlId),
                    BlobFacts.Metadata(BlobForm.Marker), cancellationToken).ConfigureAwait(false))
                    throw Failure(PrivateObjectStorageError.UnsafeStorage);
            }
            var marker = await ReadMarkerAsync(container, cancellationToken).ConfigureAwait(false);
            if (!initialize)
                await ProbeAsync(container, marker.ControlId, cancellationToken).ConfigureAwait(false);
            return new BlobPrivateObjectStore(options, container, marker.RootId, marker.ControlId, factory, observer);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            throw Normalize(error, cancellationToken);
        }
    }

    /// <summary>
    /// Admits only an existing container without anonymous access, immutability policy or legal hold.
    /// Account-level settings outside the data plane are a deployment responsibility.
    /// </summary>
    private static async Task RequirePrivateContainerAsync(BlobContainerClient container, CancellationToken token)
    {
        BlobContainerProperties properties;
        try { properties = (await container.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false)).Value; }
        catch (RequestFailedException error) when (error.Status == 404)
        { throw Failure(PrivateObjectStorageError.UnsafeStorage); }
        if ((properties.PublicAccess ?? PublicAccessType.None) != PublicAccessType.None ||
            properties.HasImmutabilityPolicy == true || properties.HasLegalHold == true ||
            properties.HasImmutableStorageWithVersioning)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
    }

    private static async Task<(Guid RootId, Guid ControlId)> ReadMarkerAsync(BlobContainerClient container, CancellationToken token)
    {
        var bytes = await ReadSmallAsync(container.GetBlobClient(BlobLayout.MarkerName), BlobForm.Marker, BlobLayout.MarkerLength, token)
            .ConfigureAwait(false) ?? throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var lines = text.Split('\n');
        if (bytes.Length != BlobLayout.MarkerLength || !text.StartsWith(BlobLayout.MarkerPrefix, StringComparison.Ordinal) ||
            lines.Length != 4 || lines[3].Length != 0 ||
            !BlobLayout.TryCanonicalId(lines[1], out var rootId) || !BlobLayout.TryCanonicalId(lines[2], out var controlId) ||
            rootId == controlId || !bytes.AsSpan().SequenceEqual(BlobLayout.Marker(rootId, controlId)))
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return (rootId, controlId);
    }

    /// <summary>
    /// Proves the actual credential can create, conditionally refuse a second creation, lease and
    /// delete. The probe name is fresh and confined to the control namespace.
    /// </summary>
    private static async Task ProbeAsync(BlobContainerClient container, Guid controlId, CancellationToken token)
    {
        var probe = container.GetBlockBlobClient(BlobLayout.Probe(controlId, Guid.NewGuid()));
        if (!await TryCreateAsync(probe, [], BlobFacts.Metadata(BlobForm.Probe), token).ConfigureAwait(false) ||
            await TryCreateAsync(probe, [], BlobFacts.Metadata(BlobForm.Probe), token).ConfigureAwait(false))
            throw Failure(PrivateObjectStorageError.Unsupported);
        var lease = probe.GetBlobLeaseClient(Guid.NewGuid().ToString("D"));
        await lease.AcquireAsync(TimeSpan.FromSeconds(15), conditions: null, token).ConfigureAwait(false);
        foreach (var invalidLease in new string?[] { null, Guid.NewGuid().ToString("D") })
        {
            try
            {
                await probe.CommitBlockListAsync([], new CommitBlockListOptions
                {
                    Conditions = new BlobRequestConditions { LeaseId = invalidLease },
                    Metadata = BlobFacts.Metadata(BlobForm.Probe)
                }, token).ConfigureAwait(false);
                throw Failure(PrivateObjectStorageError.Unsupported);
            }
            catch (RequestFailedException error) when (error.Status == 412) { }
        }
        await probe.CommitBlockListAsync([], new CommitBlockListOptions
        {
            Conditions = new BlobRequestConditions { LeaseId = lease.LeaseId },
            Metadata = BlobFacts.Metadata(BlobForm.Probe)
        }, token).ConfigureAwait(false);
        try
        {
            // A leased blob must refuse an unconditioned delete.
            await probe.DeleteAsync(DeleteSnapshotsOption.None, conditions: null, token).ConfigureAwait(false);
            throw Failure(PrivateObjectStorageError.Unsupported);
        }
        catch (RequestFailedException error) when (error.Status == 412) { }
        await probe.DeleteAsync(DeleteSnapshotsOption.None, new BlobRequestConditions { LeaseId = lease.LeaseId }, token)
            .ConfigureAwait(false);
    }

    /// <summary>A single Put Blob with If-None-Match:*; false means a blob already occupies the name.</summary>
    private static async Task<bool> TryCreateAsync(BlockBlobClient blob, byte[] content, IDictionary<string, string> metadata,
        CancellationToken token)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = metadata
            }, token).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { return false; }
    }

    /// <summary>Reads one bounded small blob of an exact form in a single consistent download, or null when absent.</summary>
    private static async Task<byte[]?> ReadSmallAsync(BlobClient blob, BlobForm form, int maximum, CancellationToken token)
    {
        Response<BlobDownloadStreamingResult> response;
        try { response = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), token).ConfigureAwait(false); }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
        using var result = response.Value;
        var details = result.Details;
        var facts = BlobFacts.Classify(details.ETag, details.ContentLength, details.Metadata);
        if (facts.Form != form || details.BlobType != BlobType.Block || details.ContentLength is < 1 || details.ContentLength > maximum)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        var bytes = new byte[details.ContentLength];
        await using var content = result.Content;
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await content.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) throw Failure(PrivateObjectStorageError.UnsafeStorage);
            count += read;
        }
        if (await content.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw Failure(PrivateObjectStorageError.UnsafeStorage);
        return bytes;
    }
}
