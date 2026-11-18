using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Braid.As2;

public sealed record As2Delivery(Guid DeliveryId, string BindingVersion, string PayloadSha256, ReadOnlyMemory<byte> Bytes);

// Resolve by immutable version, never by the partner's current settings.
public sealed record As2Partner(string Version, Uri Endpoint, string From, string To,
    X509Certificate2 SigningKey, X509Certificate2 EncryptionCertificate, X509Certificate2 ReceiptCertificate);

public sealed record As2Attempt(Guid DeliveryId, string BindingVersion, string PayloadSha256, string MessageId,
    string Mic, string From, string To, string ReceiptCertificateSha256);

public interface IAs2SendFence
{
    // Atomically CAS the pinned outbox claim to unknown, persist this expectation, then commit.
    // False for stale claims or any prior dispatch (including an expired lease after a crash).
    ValueTask<bool> TryBeginAsync(As2Attempt attempt, CancellationToken cancellationToken);
}

public enum As2Outcome { AlreadyFenced, Unknown, VerifiedTransportReceipt }
public sealed record As2Observation(As2Attempt Attempt, As2Outcome Outcome);

public sealed class As2Connector(HttpClient client, IAs2SendFence fence)
{
    internal const string Sha256 = "2.16.840.1.101.3.4.2.1";
    public const int MaxReceiptBytes = 256 * 1024;

    public static HttpClient CreateTransportClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseHeadersLength = 32
    });

    // Inject a dedicated no-redirect, no-retry HTTP client; use a bounded cancellation deadline.
    public async Task<As2Observation> SendAsync(As2Delivery delivery, As2Partner partner, CancellationToken token)
    {
        if (delivery.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(partner.Version) ||
            delivery.BindingVersion != partner.Version || !partner.Endpoint.IsAbsoluteUri ||
            partner.Endpoint.Scheme != "https" || partner.Endpoint.UserInfo.Length != 0 || partner.Endpoint.Fragment.Length != 0 ||
            !IsId(partner.From) || !IsId(partner.To))
            throw new ArgumentException("Invalid AS2 delivery or binding.");

        // Snapshot before hashing: callers can own mutable backing storage.
        var bytes = delivery.Bytes.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!hash.Equals(delivery.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pinned payload digest mismatch.");
        var entity = Encoding.ASCII.GetBytes("Content-Type: application/edi-x12\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Base64(bytes));
        var attempt = new As2Attempt(delivery.DeliveryId, partner.Version, hash, $"<{delivery.DeliveryId:N}@braid.invalid>",
            Convert.ToBase64String(SHA256.HashData(entity)), partner.From, partner.To, partner.ReceiptCertificate.GetCertHashString(HashAlgorithmName.SHA256));
        var signed = new SignedCms(new ContentInfo(entity), detached: true);
        signed.ComputeSignature(new CmsSigner(partner.SigningKey) { DigestAlgorithm = new Oid(Sha256), IncludeOption = X509IncludeOption.EndCertOnly }, silent: true);
        var boundary = "braid-" + Guid.NewGuid().ToString("N");
        var message = $"Content-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=\"{boundary}\"\r\n\r\n" +
            $"--{boundary}\r\n{Encoding.ASCII.GetString(entity)}\r\n--{boundary}\r\nContent-Type: application/pkcs7-signature\r\nContent-Transfer-Encoding: base64\r\n\r\n{Base64(signed.Encode())}\r\n--{boundary}--\r\n";
        var envelope = new EnvelopedCms(new ContentInfo(Encoding.ASCII.GetBytes(message)), new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.42")));
        envelope.Encrypt(new CmsRecipient(partner.EncryptionCertificate));
        using var request = new HttpRequestMessage(HttpMethod.Post, partner.Endpoint) { Content = new SingleWriteContent(envelope.Encode()) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pkcs7-mime; smime-type=enveloped-data");
        request.Headers.Add("AS2-Version", "1.1");
        request.Headers.Add("AS2-From", partner.From);
        request.Headers.Add("AS2-To", partner.To);
        request.Headers.Add("Message-ID", attempt.MessageId);
        request.Headers.Add("MIME-Version", "1.0");
        request.Headers.Add("Disposition-Notification-To", "receipt@braid.invalid");
        request.Headers.Add("Disposition-Notification-Options", "signed-receipt-protocol=required, pkcs7-signature; signed-receipt-micalg=required, sha-256");

        if (!await fence.TryBeginAsync(attempt, token)) return new(attempt, As2Outcome.AlreadyFenced);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode && Single(response.Headers, "AS2-From") is { } from && Single(response.Headers, "AS2-To") is { } to)
            {
                await response.Content.LoadIntoBufferAsync(MaxReceiptBytes, token);
                if (As2Mdn.Verify(attempt, from, to, Single(response.Content.Headers, "Content-Type"), await response.Content.ReadAsByteArrayAsync(token)))
                    return new(attempt, As2Outcome.VerifiedTransportReceipt);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or FormatException)
        {
            // The peer might already have accepted the bytes. No automatic resend.
        }
        return new(attempt, As2Outcome.Unknown);
    }

    private static bool IsId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks);
    private static string? Single(HttpHeaders headers, string name) => headers.TryGetValues(name, out var values) && values.Count() == 1 ? values.First() : null;

    // Even if the HTTP stack reconnects, it must never stream the envelope a second time.
    private sealed class SingleWriteContent(byte[] envelope) : HttpContent
    {
        private int written;
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, default);
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken token)
        {
            if (Interlocked.Exchange(ref written, 1) != 0) throw new IOException("AS2 envelope already dispatched.");
            return stream.WriteAsync(envelope, token).AsTask();
        }
        protected override bool TryComputeLength(out long length) { length = envelope.Length; return true; }
    }
}
