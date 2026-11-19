using System.Text.Json.Nodes;
using Azure.Core;
using Braid.Application;
using Braid.MachineEndpoint;
using Braid.Storage.Blob;
using Braid.Triggers.Emulators;

namespace Braid.Tests;

public sealed class ManagedTriggerRegistrationTests
{
    private static JsonObject Registration() => new()
    {
        ["version"] = 2,
        ["managedIdentityClientId"] = Guid.NewGuid().ToString("D"),
        ["serviceBusNamespace"] = "demo-broker.servicebus.windows.net",
        ["queues"] = new JsonArray(new JsonObject
        {
            ["triggerId"] = Guid.NewGuid().ToString("D"), ["connectorId"] = "orders", ["connectorVersion"] = 1,
            ["queueName"] = "orders-in", ["subscription"] = "selected", ["prefetchCount"] = 0,
            ["maximumConcurrentCalls"] = 1, ["retryBackoffSeconds"] = 5
        }),
        ["schedules"] = new JsonArray(new JsonObject
        {
            ["triggerId"] = Guid.NewGuid().ToString("D"), ["connectorId"] = "daily", ["connectorVersion"] = 1,
            ["container"] = "inbox", ["credential"] = "managedIdentity", ["prefix"] = "orders/",
            ["serviceUri"] = "https://demobraid.blob.core.windows.net", ["tickSeconds"] = 60, ["pageSize"] = 50
        }),
        ["destinations"] = new JsonArray()
    };

    [Fact]
    public void Managed_registration_preserves_the_subscription_and_uses_the_target_identity_for_storage()
    {
        var plan = TriggerHostPlan.Parse(Registration().ToJsonString(), managed: true);
        var connector = Assert.Single(plan.Queues).Connector;
        Assert.Equal(("orders-in", "selected"), (connector.QueueName, connector.Subscription));
        var scan = Assert.Single(plan.Schedules);
        Assert.Equal(BlobStorageCredentialKind.ManagedIdentity, scan.Storage.Credential);
        Assert.Equal(plan.ManagedIdentityClientId, scan.Storage.ManagedIdentityClientId);
        Assert.NotNull(new BlobTriggerSource(scan.Connector, scan.Storage, scan.Prefix, scan.PageSize));
        Assert.NotNull(new BlobOutputDestination(scan.Connector, scan.Storage, scan.Prefix));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("namespace")]
    [InlineData("foreignBlob")]
    [InlineData("blobEmulator")]
    [InlineData("tokenFile")]
    [InlineData("brokerKey")]
    [InlineData("scopeOverride")]
    [InlineData("localMode")]
    public void A_managed_registration_cannot_fall_back_to_other_credentials_or_destinations(string defect)
    {
        var file = Registration();
        switch (defect)
        {
            case "identity": file.Remove("managedIdentityClientId"); break;
            case "namespace": file["serviceBusNamespace"] = "demo.servicebus.windows.net.example.invalid"; break;
            case "foreignBlob": file["schedules"]![0]!["serviceUri"] = "https://blob.example.invalid"; break;
            case "blobEmulator": file["schedules"]![0]!["credential"] = "emulator"; break;
            case "tokenFile": file["tokenFile"] = Path.GetFullPath("test-token"); break;
            case "brokerKey": file["serviceBusEmulatorConnectionFile"] = Path.GetFullPath("broker"); break;
            case "scopeOverride": file["callerScope"] = "api://demo/.default"; break;
        }
        Assert.Throws<ArgumentException>(() => TriggerHostPlan.Parse(file.ToJsonString(), managed: defect != "localMode"));
    }

    [Fact]
    public void Existing_local_registrations_remain_local()
    {
        var file = Registration();
        file["version"] = 1;
        file.Remove("managedIdentityClientId");
        file.Remove("serviceBusNamespace");
        file["serviceBusEmulatorConnectionFile"] = Path.GetFullPath("broker");
        file["tokenFile"] = Path.GetFullPath("test-token");
        file["schedules"]![0]!["serviceUri"] = "http://127.0.0.1:10000/devstoreaccount1";
        file["schedules"]![0]!["credential"] = "emulator";
        var plan = TriggerHostPlan.Parse(file.ToJsonString());
        Assert.Null(plan.ManagedIdentityClientId);
        Assert.Equal(BlobStorageCredentialKind.Emulator, Assert.Single(plan.Schedules).Storage.Credential);
        Assert.Throws<ArgumentException>(() => TriggerHostPlan.Parse(file.ToJsonString(), managed: true));
        Assert.Throws<ArgumentException>(() => TriggerHostPlan.Read(k => k is TriggerHostPlan.FileSetting or TriggerHostPlan.ManagedFileSetting
            ? Path.GetFullPath("plan") : null, (_, _) => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invocation_uses_the_endpoint_audience_and_requires_the_validated_target_application(bool expectedClient)
    {
        var audience = Guid.NewGuid();
        var client = Guid.NewGuid();
        var result = new MachineCaller(new(MachinePrincipal.Entra, Guid.NewGuid(), Guid.NewGuid()),
            expectedClient ? client : Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10));
        var credential = new Credential(audience);
        var validator = new Validator(result);
        if (expectedClient)
            Assert.Same(result, await TriggerWorkerHost.ManagedCallerAsync(credential, audience, client, validator, CancellationToken.None));
        else
            Assert.Equal(MachineTokenFailure.Denied, (await Assert.ThrowsAsync<MachineTokenException>(
                () => TriggerWorkerHost.ManagedCallerAsync(credential, audience, client, validator, CancellationToken.None))).Failure);
    }

    private sealed class Credential(Guid audience) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal($"api://{audience:D}/.default", Assert.Single(requestContext.Scopes));
            return ValueTask.FromResult(new AccessToken("fixture", DateTimeOffset.UtcNow.AddMinutes(10)));
        }
    }

    private sealed class Validator(MachineCaller caller) : IMachineTokenValidator
    {
        public Task<MachineCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
        {
            Assert.Equal("fixture", bearerToken);
            return Task.FromResult(caller);
        }
    }
}
