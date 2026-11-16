using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Braid.Application.Automation;
using Braid.Jobs;

namespace Braid.Tests;

public sealed class NotificationSecurityTests
{
    [Fact]
    public void Configuration_requires_an_explicit_admitted_private_reader()
    {
        var configuredPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "notifications.json"));
        string? Environment(string name) => name == "BRAID_NOTIFICATION_CONFIG_FILE" ? configuredPath : null;
        Assert.Throws<ArgumentException>(() => NotificationConfigurationFile.Read(Environment));
        var called = false;
        var configuration = NotificationConfigurationFile.Read(Environment, (path, maximum) =>
        {
            Assert.Equal(configuredPath, path); Assert.Equal(65536, maximum); called = true;
            return "{}";
        });
        Assert.True(called); Assert.Empty(configuration.Webhooks);
    }
    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://user:secret@example.com/hook")]
    [InlineData("https://example.com/hook?key=secret")]
    [InlineData("https://example.com:8443/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://example.com./hook")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    public void Hosted_webhooks_refuse_non_https_credentials_private_hosts_and_ambiguous_targets(string url) =>
        Assert.Throws<ArgumentException>(() => NotificationUrlPolicy.Validate(new(url), true));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2001:100::1")]
    [InlineData("3ffe::1")]
    [InlineData("3fff::1")]
    public void Every_resolved_address_is_checked_before_connection(string address) =>
        Assert.False(NotificationUrlPolicy.Allowed(IPAddress.Parse(address), true));

    [Fact]
    public void Signature_binds_timestamp_and_exact_utf8_body()
    {
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var body = "{\"event\":\"revisionUploaded\"}"u8.ToArray();
        var signature = NotificationTransport.Sign(secret, "1792886400", body);
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("1792886400." + Encoding.UTF8.GetString(body)))), signature);
        Assert.NotEqual(signature, NotificationTransport.Sign(secret, "1792886401", body));
        Assert.NotEqual(signature, NotificationTransport.Sign(secret, "1792886400", "{}"u8));
    }

    [Fact]
    public void Outbound_event_schema_contains_identifiers_and_link_only()
    {
        var message = new NotificationMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "regressionFailed", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/catalog", DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(message));
        Assert.Equal(["Id", "OrganizationId", "ProjectId", "Event", "MappingId", "RevisionId", "ResourceId", "Link", "CreatedAt"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("payload", JsonSerializer.Serialize(message), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("acs")]
    public async Task Email_adapters_send_only_fixed_metadata_through_a_local_fixture(string provider)
    {
        var org = Guid.NewGuid(); var project = Guid.NewGuid(); var user = Guid.NewGuid();
        var email = provider == "smtp" ? new ConfiguredEmail("smtp", "sender@example.test", "localhost") :
            new ConfiguredEmail("acs", "sender@example.test", AcsEndpoint: "https://fixture.communication.azure.com", AcsAccessKey: Convert.ToBase64String(new byte[32]));
        var config = new NotificationConfiguration { Origin = "https://braid.example.test", Email = email, Recipients = [new(org, user, "recipient@example.test")] };
        config.Validate();
        var calls = 0;
        var transport = new NotificationTransport(config, TimeProvider.System)
        {
            SmtpFixture = (message, _) =>
            {
                calls++;
                Assert.Equal("recipient@example.test", Assert.Single(message.To).Address);
                Assert.Contains("revisionUploaded", message.Body); Assert.DoesNotContain("secret", message.Body);
                return Task.CompletedTask;
            },
            HttpFixture = async (request, token) =>
            {
                calls++;
                Assert.Equal("fixture.communication.azure.com", request.RequestUri!.Host);
                Assert.StartsWith("HMAC-SHA256 ", request.Headers.GetValues("Authorization").Single());
                var body = await request.Content!.ReadAsStringAsync(token);
                Assert.Contains("recipient@example.test", body); Assert.Contains("revisionUploaded", body); Assert.DoesNotContain("acsAccessKey", body);
                return NotificationDeliveryOutcome.Delivered;
            }
        };
        var message = new NotificationMessage(Guid.NewGuid(), org, project, "revisionUploaded", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/catalog", DateTimeOffset.UtcNow);
        Assert.Equal(NotificationDeliveryOutcome.Delivered, await transport.SendAsync(new(org, Guid.NewGuid(), project, new(user), "email:" + user.ToString("D"), message, Guid.NewGuid(), 1), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Signed_webhook_fixture_receives_the_exact_body_without_secrets_or_unapproved_destination()
    {
        var org = Guid.NewGuid(); var project = Guid.NewGuid(); var user = Guid.NewGuid();
        var key = Enumerable.Repeat((byte)17, 32).ToArray();
        var config = new NotificationConfiguration { Origin = "https://braid.example.test", Webhooks = [new(org, project, "test", "https://hook.example.test/events", Convert.ToBase64String(key))] };
        var calls = 0;
        var transport = new NotificationTransport(config, TimeProvider.System)
        {
            HttpFixture = async (request, token) =>
            {
                calls++;
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                var timestamp = request.Headers.GetValues("X-Braid-Timestamp").Single();
                Assert.Equal(NotificationTransport.Sign(key, timestamp, bytes), request.Headers.GetValues("X-Braid-Signature").Single());
                Assert.DoesNotContain(Convert.ToBase64String(key), Encoding.UTF8.GetString(bytes));
                return NotificationDeliveryOutcome.Delivered;
            }
        };
        var message = new NotificationMessage(Guid.NewGuid(), org, project, "regressionFailed", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/catalog", DateTimeOffset.UtcNow);
        var claim = new NotificationDeliveryClaim(org, Guid.NewGuid(), project, new(user), "webhook:test", message, Guid.NewGuid(), 1);
        Assert.Equal(NotificationDeliveryOutcome.Delivered, await transport.SendAsync(claim, default));
        Assert.Equal(NotificationDeliveryOutcome.Refused, await transport.SendAsync(claim with { Destination = "webhook:unknown" }, default));
        Assert.Equal(1, calls);
    }
}
