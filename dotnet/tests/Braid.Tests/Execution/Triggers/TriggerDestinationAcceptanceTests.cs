using Renci.SshNet;
using Braid.Deployment.Triggers;
using Braid.Triggers.Emulators;
using Braid.Triggers.Sftp;

namespace Braid.Tests;

/// <summary>The Service Bus topic and SFTP destinations against the owned emulator project.</summary>
[Collection(TriggerEmulatorCollection.Name)]
public sealed class TriggerDestinationAcceptanceTests(TriggerEmulatorFixture emulator)
{
    private static readonly string PasswordFile = Path.Combine(Path.GetTempPath(), "braid-trigger-sftp.password");

    [TriggerEmulatorFact]
    public async Task A_topic_delivery_is_accepted_by_the_broker_with_its_delivery_id_and_frozen_properties()
    {
        await using var destination = new ServiceBusTopicDestination(new("topic-out", 4), emulator.Client, "braid-deliveries");
        var delivery = TriggerQueueProcessingTests.Delivery(version: 4);
        Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
        await using var receiver = emulator.Client.CreateReceiver("braid-deliveries", "braid-receipts");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(message);
        await receiver.CompleteMessageAsync(message);
        Assert.Equal((delivery.DeliveryId.ToString("D"), "application/json"), (message.MessageId, message.ContentType));
        Assert.Equal(delivery.CopyContent(), message.Body.ToArray());
        Assert.Equal(delivery.ContentDigest.Value, message.ApplicationProperties[ServiceBusTopicDestination.DigestProperty]);
        Assert.Equal(delivery.OutputReceiptId.ToString("D"), message.ApplicationProperties[ServiceBusTopicDestination.ReceiptProperty]);
        // Another connector version is never redirected here, and a missing topic is refused before any transfer.
        Assert.Equal(DeliveryObservationKind.PermanentFailure, await destination.SendAsync(TriggerQueueProcessingTests.Delivery(version: 5), default));
        await using var missing = new ServiceBusTopicDestination(new("topic-out", 4), emulator.Client, "braid-missing-topic");
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await missing.SendAsync(delivery, default));
        Assert.Null(await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2)));
    }

    [TriggerEmulatorFact]
    public async Task An_sftp_delivery_is_published_once_under_its_key_and_a_repeat_with_equal_bytes_is_accepted()
    {
        var (destination, client) = await SftpAsync("a/");
        using (client)
        {
            var delivery = TriggerQueueProcessingTests.Delivery(version: 6);
            Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
            Assert.Equal(DeliveryObservationKind.Accepted, await destination.SendAsync(delivery, default));
            Assert.Equal(delivery.CopyContent(), client.ReadAllBytes("/upload/a/" + delivery.DeliveryId.ToString("D")));
            // No temporary upload is left behind and nothing else was published.
            Assert.Equal([delivery.DeliveryId.ToString("D")], Names(client, "/upload/a"));
        }
    }

    [TriggerEmulatorFact]
    public async Task An_existing_sftp_file_with_other_bytes_is_a_conflict_and_stays_untouched()
    {
        var (destination, client) = await SftpAsync("b/");
        using (client)
        {
            var delivery = TriggerQueueProcessingTests.Delivery(version: 6);
            client.WriteAllBytes("/upload/b/" + delivery.DeliveryId.ToString("D"), "{\"other\":true}"u8.ToArray());
            Assert.Equal(DeliveryObservationKind.PermanentFailure, await destination.SendAsync(delivery, default));
            Assert.Equal("{\"other\":true}"u8.ToArray(), client.ReadAllBytes("/upload/b/" + delivery.DeliveryId.ToString("D")));
            Assert.Equal([delivery.DeliveryId.ToString("D")], Names(client, "/upload/b"));
        }
    }

    [TriggerEmulatorFact]
    public async Task A_wrong_host_key_pin_bad_credentials_or_a_missing_directory_writes_nothing()
    {
        var (port, hostKey) = await emulator.SftpAsync();
        var delivery = TriggerQueueProcessingTests.Delivery(version: 6);
        var wrongKey = new string('A', 43);
        Assert.NotEqual(hostKey, wrongKey);
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(port, wrongKey, emulator.SftpPassword, "").SendAsync(delivery, default));
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(port, hostKey, "wrong", "").SendAsync(delivery, default));
        Assert.Equal(DeliveryObservationKind.ProvenKnownNotAccepted, await Destination(port, hostKey, emulator.SftpPassword, "missing/").SendAsync(delivery, default));
        using var client = Client(port, hostKey);
        Assert.DoesNotContain(delivery.DeliveryId.ToString("D"), Names(client, "/upload"));
    }

    [Fact]
    public void Sftp_connectors_require_a_pinned_key_a_private_credential_reference_and_no_replace_publish()
    {
        SftpDestinationConnector Create(string root = "/upload", string prefix = "", SftpPublishContract publish = SftpPublishContract.NoReplaceRename,
            string key = "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", string? password = null, string? privateKey = null) =>
            new(new("sftp-out", 1), "sftp.example.com", 22, "braid", key, password ?? (privateKey is null ? PasswordFile : null), privateKey, root, prefix, publish);
        Assert.Throws<ArgumentException>(() => Create(publish: SftpPublishContract.Unsupported));
        Assert.Throws<ArgumentException>(() => Create(key: "SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og"));
        Assert.Throws<ArgumentException>(() => Create(root: "/upload/../etc"));
        Assert.Throws<ArgumentException>(() => Create(root: "upload"));
        Assert.Throws<ArgumentException>(() => Create(prefix: "../"));
        Assert.Throws<ArgumentException>(() => Create(password: "relative.password"));
        Assert.Throws<ArgumentException>(() => Create(password: PasswordFile, privateKey: PasswordFile));
        Assert.Equal("/upload/out", Create(prefix: "out/").Directory);
    }

    private async Task<(SftpOutputDestination Destination, SftpClient Client)> SftpAsync(string prefix)
    {
        var (port, hostKey) = await emulator.SftpAsync();
        var client = Client(port, hostKey);
        if (!client.Exists("/upload/" + prefix.TrimEnd('/'))) client.CreateDirectory("/upload/" + prefix.TrimEnd('/'));
        foreach (var file in client.ListDirectory("/upload/" + prefix.TrimEnd('/')).Where(file => file.IsRegularFile)) client.DeleteFile(file.FullName);
        return (Destination(port, hostKey, emulator.SftpPassword, prefix), client);
    }

    private static SftpOutputDestination Destination(int port, string hostKey, string password, string prefix) =>
        new(new(new("sftp-out", 6), "127.0.0.1", port, "braid", hostKey, PasswordFile, null, "/upload", prefix, SftpPublishContract.NoReplaceRename),
            (path, _) => path == PasswordFile ? password : throw new FileNotFoundException());

    private SftpClient Client(int port, string hostKey)
    {
        var client = new SftpClient("127.0.0.1", port, "braid", emulator.SftpPassword);
        client.HostKeyReceived += (_, e) => e.CanTrust = e.FingerPrintSHA256 == hostKey;
        client.Connect();
        return client;
    }

    private static string[] Names(SftpClient client, string directory) =>
        client.ListDirectory(directory).Where(file => file.Name is not ("." or "..")).Select(file => file.Name).Order(StringComparer.Ordinal).ToArray();
}
