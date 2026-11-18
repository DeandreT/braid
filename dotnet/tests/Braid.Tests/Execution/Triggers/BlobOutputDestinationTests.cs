using Azure.Storage.Blobs.Models;
using Braid.Deployment.Triggers;
using Braid.Triggers.Emulators;

namespace Braid.Tests;

/// <summary>Create-only Blob delivery against the actual Azurite emulator.</summary>
[Collection(AzuriteCollection.Name)]
public sealed class BlobOutputDestinationTests(AzuriteFixture azurite)
{
    [AzuriteFact]
    public async Task Scheduled_listing_pages_only_the_registered_prefix_and_refuses_changed_versions()
    {
        var container = await azurite.CreateContainerAsync();
        foreach (var name in new[] { "in/a", "in/b", "other/c" })
            await container.GetBlobClient(name).UploadAsync(new BinaryData("{}"));
        var source = new BlobTriggerSource(new("orders-in", 1), azurite.Options(container), "in/", 1);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = await source.ListPageAsync(null, cutoff, default);
        var a = Assert.Single(first.Items); Assert.Equal("in/a", a.Name);
        Assert.NotNull(first.NextCursor);
        var next = await source.ListPageAsync(first.NextCursor, cutoff, default);
        Assert.Equal("in/b", Assert.Single(next.Items).Name);
        Assert.Equal(TriggerSourceReadStatus.TooLarge, (await source.ReadAsync(a, 1, default)).Status);
        Assert.Equal("{}"u8.ToArray(), (await source.ReadAsync(a, 32, default)).Content);
        await container.GetBlobClient(a.Name).UploadAsync(new BinaryData("different"), overwrite: true);
        Assert.Equal(TriggerSourceReadStatus.Changed, (await source.ReadAsync(a, 32, default)).Status);
    }

    [AzuriteFact]
    public async Task A_delivery_is_written_once_and_a_repeat_with_the_same_bytes_is_accepted_without_rewriting()
    {
        var container = await azurite.CreateContainerAsync();
        var destination = new BlobOutputDestination(new("blob-out", 1), azurite.Options(container), "orders/");
        var delivery = TriggerQueueProcessingTests.Delivery();
        Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
        var blob = container.GetBlobClient("orders/" + delivery.DeliveryId.ToString("D"));
        BlobProperties written = await blob.GetPropertiesAsync();
        Assert.Equal(("application/json", delivery.ContentLength), (written.ContentType, written.ContentLength));
        Assert.Equal(delivery.DeliveryId.ToString("D"), written.Metadata["braiddelivery"]);
        Assert.Equal(delivery.ContentDigest.Value, written.Metadata["braidsha256"]);
        Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
        Assert.Equal(written.ETag, (await blob.GetPropertiesAsync()).Value.ETag);
        Assert.Equal(delivery.CopyContent(), (await blob.DownloadContentAsync()).Value.Content.ToArray());
    }

    [AzuriteFact]
    public async Task An_existing_object_that_is_not_this_deliverys_exact_bytes_is_a_conflict_and_stays_untouched()
    {
        var container = await azurite.CreateContainerAsync();
        var destination = new BlobOutputDestination(new("blob-out", 1), azurite.Options(container), "");
        var delivery = TriggerQueueProcessingTests.Delivery();
        var changed = TriggerQueueProcessingTests.Delivery(content: "{\"ok\":false}", deliveryId: delivery.DeliveryId);
        Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
        Assert.Equal(DeliveryObservationKind.PermanentFailure, await destination.SendAsync(changed, default));
        // A foreign object under a delivery key is never adopted, even with identical bytes.
        var foreign = TriggerQueueProcessingTests.Delivery();
        await container.GetBlobClient(foreign.DeliveryId.ToString("D")).UploadAsync(new BinaryData(foreign.CopyContent()));
        Assert.Equal(DeliveryObservationKind.PermanentFailure, await destination.SendAsync(foreign, default));
        Assert.Equal(delivery.CopyContent(), (await container.GetBlobClient(delivery.DeliveryId.ToString("D")).DownloadContentAsync()).Value.Content.ToArray());
    }

    [AzuriteFact]
    public async Task A_missing_container_is_proven_not_accepted()
    {
        var container = await azurite.CreateContainerAsync();
        await container.DeleteAsync();
        var destination = new BlobOutputDestination(new("blob-out", 1), azurite.Options(container), "");
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await destination.SendAsync(TriggerQueueProcessingTests.Delivery(), default));
    }
}
