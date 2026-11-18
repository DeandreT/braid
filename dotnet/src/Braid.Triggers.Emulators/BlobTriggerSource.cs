using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Braid.Deployment.Triggers;
using Braid.Storage.Blob;

namespace Braid.Triggers.Emulators;

/// <summary>
/// One registered container prefix listed in bounded full-prefix passes. An item's version is its ETag and every read is
/// conditional on it, so a replaced blob is never read as the listed version. Nothing is deleted, renamed or acknowledged.
/// </summary>
public sealed partial class BlobTriggerSource : ITriggerSource
{
    public const int MaximumPageSize = 500;
    private readonly BlobContainerClient container;
    private readonly string prefix;
    private readonly int pageSize;

    public BlobTriggerSource(TriggerConnectorReference connector, BlobStorageOptions storage, string prefix, int pageSize)
        : this(connector, BlobPrivateObjectStore.CreateContainerClient(storage is { Credential: BlobStorageCredentialKind.Emulator } ? storage : throw new ArgumentException("Local emulator required."), maxRetries: 0), prefix, pageSize) { }

    internal BlobTriggerSource(TriggerConnectorReference connector, BlobContainerClient container, string prefix, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(container);
        if (prefix is null || !PrefixPattern().IsMatch(prefix) || pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentException("The blob source needs a lowercase prefix ending in '/' (or empty) and an explicit page size of 1 to 500.");
        Connector = connector; this.container = container; this.prefix = prefix; this.pageSize = pageSize;
    }

    public TriggerConnectorReference Connector { get; }

    public async Task<TriggerSourcePage> ListPageAsync(string? cursor, DateTimeOffset cutoff, CancellationToken token)
    {
        await foreach (var page in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, token).AsPages(cursor, pageSize).ConfigureAwait(false))
        {
            var items = page.Values.Where(blob => blob.Properties.LastModified is { } modified && modified <= cutoff && blob.Properties.ETag is not null)
                .Select(blob => new TriggerSourceItem(blob.Name, blob.Properties.ETag!.Value.ToString(), blob.Properties.ContentLength ?? 0)).ToArray();
            return new(items, string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken);
        }
        return new([], null);
    }

    public async Task<TriggerSourceRead> ReadAsync(TriggerSourceItem item, int maximumBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Length > maximumBytes) return new(TriggerSourceReadStatus.TooLarge);
        try
        {
            var content = (await container.GetBlobClient(item.Name).DownloadContentAsync(new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = new ETag(item.Version) }
            }, token).ConfigureAwait(false)).Value.Content.ToArray();
            return content.Length > maximumBytes ? new(TriggerSourceReadStatus.TooLarge) : new(TriggerSourceReadStatus.Read, content);
        }
        catch (RequestFailedException error) when (error.Status == 412) { return new(TriggerSourceReadStatus.Changed); }
        catch (RequestFailedException error) when (error.Status == 404) { return new(TriggerSourceReadStatus.Missing); }
    }

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?/){0,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public override string ToString() => nameof(BlobTriggerSource);
}
