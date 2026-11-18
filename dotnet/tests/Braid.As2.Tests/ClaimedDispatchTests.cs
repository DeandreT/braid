using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Triggers.As2;
using Xunit;

namespace Braid.As2.Tests;

public sealed class ClaimedDispatchTests
{
    [Theory]
    [InlineData("success", DeliveryObservationKind.Accepted, 1)]
    [InlineData("failure", DeliveryObservationKind.Unknown, 1)]
    [InlineData("blocked", DeliveryObservationKind.Unknown, 0)]
    [InlineData("commitLost", DeliveryObservationKind.Unknown, 0)]
    [InlineData("wrongBinding", DeliveryObservationKind.PermanentFailure, 0)]
    public async Task SharedDispatcherUsesPinnedClaimAndRecordsOnlyItsObservation(string mode, DeliveryObservationKind expected, int posts)
    {
        var vectors = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mdns.json")))!;
        var payload = Convert.FromBase64String(vectors["payload"]);
        using var receipt = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(vectors["cert"]));
        using var rsa = RSA.Create(2048);
        using var signing = new CertificateRequest("CN=local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var org = Guid.NewGuid(); var project = Guid.NewGuid(); var binding = Guid.NewGuid();
        var target = new ReleaseTargetReference(new(org, project, Guid.NewGuid()), new(new(org, project), Guid.NewGuid(), 1), Guid.NewGuid());
        var reference = new TriggerConnectorReference("partner", 7);
        var route = new TriggerRoute(null, binding, TriggerDestinationKind.As2, reference);
        var delivery = new FrozenTriggerDelivery(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), binding, 7, DateTimeOffset.UtcNow,
            payload, Sha256Digest.FromBytes(payload), payload.Length, "application/edi-x12");
        var claim = new TriggerDeliveryClaim(TriggerDeliveryClaimStatus.Claimed, Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(1), delivery, route);
        var registered = mode == "wrongBinding" ? Guid.NewGuid() : binding;
        var partner = new As2Partner(As2OutputDestination.BindingVersion(reference, registered), new Uri("https://offline.invalid"), "LOCAL", "PARTNER", signing, signing, receipt);
        var store = new Store(target, claim, mode);
        var handler = new Stub(() =>
        {
            Assert.True(store.Begun);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(vectors[mode])) };
            response.Headers.Add("AS2-From", "PARTNER"); response.Headers.Add("AS2-To", "LOCAL");
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=outer");
            return response;
        });
        using var client = new HttpClient(handler);
        var destination = new As2OutputDestination(reference, registered, partner, client, store);
        var dispatcher = new TriggerDeliveryDispatcher(store, [destination], TimeProvider.System);
        Assert.True(await dispatcher.DispatchAsync(target, binding, default));
        Assert.False(await dispatcher.DispatchAsync(target, binding, default));
        Assert.Equal(expected, store.Recorded);
        Assert.Equal(posts, handler.Calls);
        Assert.False(route.CreateOnly);
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(respond()); }
    }

    private sealed class Store(ReleaseTargetReference target, TriggerDeliveryClaim claim, string mode) : ITriggerDeliveryStore, IAs2ClaimFenceStore
    {
        public bool Begun;
        public DeliveryObservationKind? Recorded;
        public Task<TriggerDeliveryClaim> ClaimAsync(ReleaseTargetReference t, Guid binding, CancellationToken token)
        {
            Assert.Equal(target, t); Assert.Equal(claim.Route!.DestinationBindingId, binding);
            return Task.FromResult(Recorded is null ? claim : new(TriggerDeliveryClaimStatus.None));
        }
        public ValueTask<bool> TryBeginAsync(ReleaseTargetReference t, TriggerDeliveryClaim c, As2Attempt attempt, CancellationToken token)
        {
            Assert.Equal(target, t); Assert.Equal(claim, c); Assert.Equal(c.Delivery!.ContentDigest.Value, attempt.PayloadSha256);
            Assert.Equal(c.Delivery.DeliveryId, attempt.DeliveryId);
            if (mode == "commitLost") throw new IOException();
            Begun = mode != "blocked";
            return ValueTask.FromResult(Begun);
        }
        public Task<TriggerObservationStatus> RecordObservationAsync(ReleaseTargetReference t, Guid delivery, Guid claimId, DeliveryObservationKind kind, CancellationToken token)
        {
            Assert.Equal(target, t); Assert.Equal(claim.Delivery!.DeliveryId, delivery); Assert.Equal(claim.ClaimId, claimId);
            Recorded = kind; return Task.FromResult(TriggerObservationStatus.Recorded);
        }
    }
}
