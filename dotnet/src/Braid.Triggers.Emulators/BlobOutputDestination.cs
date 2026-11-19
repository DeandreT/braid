using System.Text.RegularExpressions;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Storage.Blob;

namespace Braid.Triggers.Emulators;

/// <summary>
/// Create-only delivery to one registered container and prefix. The key is the prefix plus the delivery ID, so every repeat of
/// a delivery targets the same object. An existing object is accepted only when it is verifiably this delivery's exact bytes;
/// anything else is a conflict. Nothing is overwritten or appended, and the client performs no hidden retries.
/// </summary>
public sealed partial class BlobOutputDestination : IOutputDestination
{
    internal const string DeliveryKey = "braiddelivery", ReceiptKey = "braidreceipt", DigestKey = "braidsha256", LengthKey = "braidbytes";
    private readonly BlobContainerClient container;
    private readonly string prefix;

    public BlobOutputDestination(TriggerConnectorReference connector, BlobStorageOptions storage, string prefix)
        : this(connector, TriggerContainer(storage), prefix) { }

    internal static BlobContainerClient TriggerContainer(BlobStorageOptions storage) =>
        BlobPrivateObjectStore.CreateContainerClient(storage is { Credential: BlobStorageCredentialKind.Emulator } or
            { Credential: BlobStorageCredentialKind.ManagedIdentity, ManagedIdentityClientId: not null }
            ? storage : throw new ArgumentException("An emulator or explicit trigger identity is required."), maxRetries: 0);

    internal BlobOutputDestination(TriggerConnectorReference connector, BlobContainerClient container, string prefix)
    {
        ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(container);
        if (prefix is null || !PrefixPattern().IsMatch(prefix)) throw new ArgumentException("The blob prefix must be empty or lowercase path segments ending in '/'.");
        Connector = connector; this.container = container; this.prefix = prefix;
    }

    public TriggerDestinationKind Kind => TriggerDestinationKind.Blob;
    public TriggerConnectorReference Connector { get; }
    public string Key(FrozenTriggerDelivery delivery) => prefix + (delivery ?? throw new ArgumentNullException(nameof(delivery))).DeliveryId.ToString("D");

    public async Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // A delivery frozen for another connector version is never redirected to this one.
        if (delivery.DestinationVersion != Connector.Version) return DeliveryObservationKind.PermanentFailure;
        var blob = container.GetBlobClient(Key(delivery));
        try
        {
            await blob.UploadAsync(new BinaryData(delivery.CopyContent()), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = delivery.MediaType },
                Metadata = Metadata(delivery),
                // One Put Blob request: the create-only condition and the bytes are a single atomic write.
                TransferOptions = new StorageTransferOptions { MaximumConcurrency = 1, InitialTransferSize = MachineInvocationPolicy.MaximumRetainedBytes + 1L }
            }, token).ConfigureAwait(false);
            return DeliveryObservationKind.Accepted;
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { return await VerifyAsync(blob, delivery, token).ConfigureAwait(false); }
        // The service refused the request before creating anything.
        catch (RequestFailedException error) when (error.Status is 401 or 403 or 404) { return DeliveryObservationKind.ProvenKnownNotAccepted; }
        catch (RequestFailedException error) when (error.Status is >= 400 and < 500 and not 408 and not 429) { return DeliveryObservationKind.PermanentFailure; }
        // Timeouts, throttling, 5xx and transport failures are Unknown; the create-only key keeps a later repeat safe.
    }

    private static async Task<DeliveryObservationKind> VerifyAsync(BlobClient blob, FrozenTriggerDelivery delivery, CancellationToken token)
    {
        var properties = (await blob.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false)).Value;
        var expected = Metadata(delivery);
        if (properties.ContentLength != delivery.ContentLength || properties.ContentType != delivery.MediaType ||
            properties.Metadata.Count != expected.Count || expected.Any(pair => !properties.Metadata.TryGetValue(pair.Key, out var value) || value != pair.Value))
            return DeliveryObservationKind.PermanentFailure;
        var content = (await blob.DownloadContentAsync(new BlobDownloadOptions { Conditions = new BlobRequestConditions { IfMatch = properties.ETag } }, token)
            .ConfigureAwait(false)).Value.Content;
        return Sha256Digest.FromBytes(content.ToArray()) == delivery.ContentDigest ? DeliveryObservationKind.Accepted : DeliveryObservationKind.PermanentFailure;
    }

    private static Dictionary<string, string> Metadata(FrozenTriggerDelivery delivery) => new(StringComparer.OrdinalIgnoreCase)
    {
        [DeliveryKey] = delivery.DeliveryId.ToString("D"),
        [ReceiptKey] = delivery.OutputReceiptId.ToString("D"),
        [DigestKey] = delivery.ContentDigest.Value,
        [LengthKey] = delivery.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?/){0,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public override string ToString() => nameof(BlobOutputDestination);
}
