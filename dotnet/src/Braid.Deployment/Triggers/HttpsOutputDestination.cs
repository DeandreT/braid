using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using Braid.Application.Automation;

namespace Braid.Deployment.Triggers;

/// <summary>
/// Operator-private settings for one HTTPS receiver. Accepted and rejected statuses are the receiver's explicit contract:
/// accepted means remote acceptance only, rejected means proven non-acceptance. Every other reply is ambiguous.
/// The signing key is a private-file reference, never stored configuration.
/// </summary>
public sealed record HttpsDestinationConnector
{
    /// <param name="developmentCertificateSha256">A Development-only pin for a local receiver's self-signed certificate.</param>
    public HttpsDestinationConnector(TriggerConnectorReference connector, Uri endpoint, bool hosted, IReadOnlyCollection<int> acceptedStatuses,
        IReadOnlyCollection<int> rejectedStatuses, string signingKeyFile, string? developmentCertificateSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(connector); ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(acceptedStatuses); ArgumentNullException.ThrowIfNull(rejectedStatuses);
        if (!endpoint.IsAbsoluteUri || hosted && !endpoint.IsDefaultPort || acceptedStatuses.Count == 0 ||
            acceptedStatuses.Any(code => code is < 200 or > 299) || rejectedStatuses.Any(code => code is < 400 or > 599) ||
            signingKeyFile is null || !Path.IsPathFullyQualified(signingKeyFile) ||
            developmentCertificateSha256 is not null && (hosted || developmentCertificateSha256.Length != 64 ||
                !developmentCertificateSha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))))
            throw new ArgumentException("Invalid HTTPS destination connector.");
        // The notification policy (HTTPS, no credentials or query, admitted host) applies; only a Development host may use another port.
        NotificationUrlPolicy.Validate(new UriBuilder(endpoint) { Port = -1 }.Uri, hosted);
        Connector = connector; Endpoint = endpoint; Hosted = hosted; SigningKeyFile = signingKeyFile;
        AcceptedStatuses = acceptedStatuses.ToHashSet(); RejectedStatuses = rejectedStatuses.ToHashSet();
        DevelopmentCertificateSha256 = developmentCertificateSha256;
    }

    public TriggerConnectorReference Connector { get; }
    public Uri Endpoint { get; }
    public bool Hosted { get; }
    public IReadOnlySet<int> AcceptedStatuses { get; }
    public IReadOnlySet<int> RejectedStatuses { get; }
    public string SigningKeyFile { get; }
    public string? DevelopmentCertificateSha256 { get; }
    public override string ToString() => nameof(HttpsDestinationConnector);
}

/// <summary>
/// Posts the exact frozen bytes once, with the delivery ID as the Idempotency-Key, over the pinned notification connection.
/// Only a failure before the connection exists, a TLS handshake failure or a configured rejection proves non-acceptance;
/// a timeout, reset or unexpected reply after the connection exists is Unknown and is held for manual resolution.
/// </summary>
public sealed class HttpsOutputDestination(HttpsDestinationConnector connector, Func<string, int, string> privateReader, TimeProvider time) : IOutputDestination
{
    public TriggerDestinationKind Kind => TriggerDestinationKind.Https;
    public TriggerConnectorReference Connector { get; } = (connector ?? throw new ArgumentNullException(nameof(connector))).Connector;

    public async Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.DestinationVersion != Connector.Version) return DeliveryObservationKind.PermanentFailure;
        var connected = false;
        try
        {
            var key = Convert.FromBase64String(privateReader(connector.SigningKeyFile, 4096).Trim());
            if (key.Length is < 32 or > 64) throw new FormatException("Webhook keys require 32 to 64 random bytes.");
            var body = delivery.CopyContent();
            var id = delivery.DeliveryId.ToString("D");
            var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
            using var handler = PinnedHttpsHandler.Create(connector.Endpoint, connector.Hosted, () => connected = true);
            if (connector.DevelopmentCertificateSha256 is { } pin)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => errors == SslPolicyErrors.None ||
                    certificate is not null && Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())) == pin;
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4096 };
            using var request = new HttpRequestMessage(HttpMethod.Post, connector.Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(delivery.MediaType);
            request.Headers.Add("Idempotency-Key", id);
            request.Headers.Add("X-Braid-Delivery-Id", id);
            request.Headers.Add("X-Braid-Content-SHA256", delivery.ContentDigest.Value);
            request.Headers.Add("X-Braid-Timestamp", timestamp);
            request.Headers.Add("X-Braid-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, (byte[])[.. prefix, .. body])));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return connector.AcceptedStatuses.Contains(status) ? DeliveryObservationKind.Accepted
                : connector.RejectedStatuses.Contains(status) ? DeliveryObservationKind.ProvenKnownNotAccepted : DeliveryObservationKind.Unknown;
        }
        // Without a connection, or with a failed TLS handshake, no request byte reached the receiver.
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException) &&
            (!connected || error is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }))
        { return DeliveryObservationKind.ProvenKnownNotAccepted; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return DeliveryObservationKind.Unknown; }
    }

    public override string ToString() => nameof(HttpsOutputDestination);
}
