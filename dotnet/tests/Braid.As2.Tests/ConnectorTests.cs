using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Braid.As2.Tests;

public sealed class ConnectorTests
{
    private const string ContentType = "multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=outer";
    private static readonly Dictionary<string, string> Vectors = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mdns.json")))!;
    private static byte[] Bytes(string name) => Convert.FromBase64String(Vectors[name]);

    [Theory]
    [InlineData("success", As2Outcome.VerifiedTransportReceipt)]
    [InlineData("failure", As2Outcome.Unknown)]
    [InlineData("timeout", As2Outcome.Unknown)]
    [InlineData("missing", As2Outcome.Unknown)]
    [InlineData("oversize", As2Outcome.Unknown)]
    public async Task FencesBeforePostingAndRequiresIndependentPartnerReceipt(string reply, As2Outcome expected)
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var receipt = X509CertificateLoader.LoadCertificate(Bytes("cert"));
        var partner = new As2Partner("partner/version1", new Uri("https://partner.invalid/as2"), "LOCAL", "PARTNER", cert, cert, receipt);
        var payload = Bytes("payload");
        var delivery = new As2Delivery(Guid.Parse("11111111-1111-1111-1111-111111111111"), partner.Version,
            Convert.ToHexString(SHA256.HashData(payload)), payload);
        var fence = new Fence();
        var calls = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.NotNull(fence.Begun);
            Assert.Equal(partner.Endpoint, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(partner.From, Assert.Single(request.Headers.GetValues("AS2-From")));
            Assert.Equal(partner.To, Assert.Single(request.Headers.GetValues("AS2-To")));
            Assert.Equal(fence.Begun.MessageId, Assert.Single(request.Headers.GetValues("Message-ID")));
            Assert.Equal(Vectors["mic"], fence.Begun.Mic);
            var envelope = new EnvelopedCms();
            envelope.Decode(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("2.16.840.1.101.3.4.1.42", envelope.ContentEncryptionAlgorithm.Oid.Value);
            envelope.Decrypt(envelope.RecipientInfos[0], rsa);
            var signed = Encoding.ASCII.GetString(envelope.ContentInfo.Content);
            var boundary = signed.Split("boundary=\"")[1].Split('"')[0];
            var parts = signed.Split("--" + boundary);
            var entity = parts[1][2..^2];
            Assert.Equal(payload, Convert.FromBase64String(entity.Split("\r\n\r\n")[1]));
            var signature = new SignedCms(new ContentInfo(Encoding.ASCII.GetBytes(entity)), true);
            signature.Decode(Convert.FromBase64String(parts[2].Split("\r\n\r\n")[1]));
            signature.CheckSignature(true);
            Assert.Equal(cert.RawData, signature.SignerInfos[0].Certificate!.RawData);
            if (reply == "timeout") throw new TaskCanceledException();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("AS2-From", partner.To);
            response.Headers.Add("AS2-To", partner.From);
            response.Content = new ByteArrayContent(reply switch { "oversize" => new byte[As2Connector.MaxReceiptBytes + 1], "missing" => [], _ => Bytes(reply) });
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
            return response;
        }));
        var connector = new As2Connector(client, fence);
        await Assert.ThrowsAsync<ArgumentException>(() => connector.SendAsync(delivery with { PayloadSha256 = "bad" }, partner, default));
        await Assert.ThrowsAsync<ArgumentException>(() => connector.SendAsync(delivery with { BindingVersion = "new" }, partner, default));
        Assert.Null(fence.Begun);
        var result = await connector.SendAsync(delivery, partner, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(As2Outcome.AlreadyFenced, (await new As2Connector(client, fence).SendAsync(delivery, partner, default)).Outcome);
        Assert.Equal(1, calls);

        // A delayed receipt can reconcile this dispatch, but not another message, pin or partner.
        var body = Bytes("success");
        bool Verify(As2Attempt a) => As2Mdn.Verify(a, "PARTNER", "LOCAL", ContentType, body);
        Assert.True(Verify(result.Attempt));
        Assert.False(Verify(result.Attempt with { MessageId = "<other@braid.invalid>" }));
        Assert.False(Verify(result.Attempt with { Mic = "wrong" }));
        Assert.False(Verify(result.Attempt with { To = "OTHER" }));
        Assert.False(Verify(result.Attempt with { ReceiptCertificateSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256) }));
        body[Array.IndexOf(body, (byte)'O')] ^= 1;
        Assert.False(Verify(result.Attempt));
    }

    private sealed class Fence : IAs2SendFence
    {
        public As2Attempt? Begun;
        public ValueTask<bool> TryBeginAsync(As2Attempt attempt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Interlocked.CompareExchange(ref Begun, attempt, null) is null);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
