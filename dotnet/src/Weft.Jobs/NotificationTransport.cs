using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Application.Automation;

namespace Weft.Jobs;

/// <summary>Metadata-only SMTP/ACS email and signed HTTPS webhooks; no redirects, proxies or unpinned DNS connections.</summary>
public sealed class NotificationTransport(NotificationConfiguration configuration, TimeProvider clock) : INotificationTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Func<HttpRequestMessage, CancellationToken, Task<NotificationDeliveryOutcome>>? HttpFixture { get; init; }
    internal Func<MailMessage, CancellationToken, Task>? SmtpFixture { get; init; }

    public async Task<NotificationDeliveryOutcome> SendAsync(NotificationDeliveryClaim claim, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (claim.Destination.StartsWith("webhook:", StringComparison.Ordinal))
            {
                var hook = configuration.Webhooks.SingleOrDefault(h => h.OrganizationId == claim.OrganizationId && h.ProjectId == claim.ProjectId &&
                    claim.Destination == "webhook:" + h.Id);
                if (hook is null) return NotificationDeliveryOutcome.Refused;
                var body = JsonSerializer.SerializeToUtf8Bytes(claim.Message, Json);
                if (body.Length > 4096) return NotificationDeliveryOutcome.Refused;
                var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add("X-Weft-Event-Id", claim.Message.Id.ToString("D"));
                request.Headers.Add("X-Weft-Timestamp", timestamp);
                request.Headers.Add("X-Weft-Signature", Sign(Convert.FromBase64String(hook.Secret), timestamp, body));
                return await SendHttpsAsync(request, timeout.Token).ConfigureAwait(false);
            }
            if (claim.Destination != "email:" + claim.Recipient.Value.ToString("D")) return NotificationDeliveryOutcome.Refused;
            var recipient = configuration.Recipients.SingleOrDefault(r => r.OrganizationId == claim.OrganizationId && r.PrincipalId == claim.Recipient.Value);
            if (recipient is null || configuration.Email is not { } email) return NotificationDeliveryOutcome.Refused;
            var text = $"{claim.Message.Event}\nProject: {claim.ProjectId:D}\nResource: {claim.Message.ResourceId:D}\n{new Uri(new Uri(configuration.Origin), claim.Message.Link)}";
            if (email.Provider == "smtp")
            {
                using var smtp = new SmtpClient(email.SmtpHost!, email.SmtpPort)
                {
                    EnableSsl = true,
                    UseDefaultCredentials = false,
                    Credentials = email.Username is null ? null : new NetworkCredential(email.Username, email.Password),
                    Timeout = 20000
                };
                using var message = new MailMessage(email.Sender, recipient.Address, "Weft: " + claim.Message.Event, text);
                if (SmtpFixture is not null)
                {
                    await SmtpFixture(message, timeout.Token).ConfigureAwait(false);
                    return NotificationDeliveryOutcome.Delivered;
                }
                await smtp.SendMailAsync(message, timeout.Token).ConfigureAwait(false);
                return NotificationDeliveryOutcome.Delivered;
            }
            if (email.Provider != "acs") return NotificationDeliveryOutcome.Refused;
            var uri = new Uri(new Uri(email.AcsEndpoint!), "/emails:send?api-version=2023-03-31");
            var content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                senderAddress = email.Sender,
                recipients = new { to = new[] { new { address = recipient.Address } } },
                content = new { subject = "Weft: " + claim.Message.Event, plainText = text }
            }, Json);
            using var acs = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(content) };
            acs.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var date = clock.GetUtcNow().ToString("r", CultureInfo.InvariantCulture);
            var digest = Convert.ToBase64String(SHA256.HashData(content));
            var toSign = $"POST\n{uri.PathAndQuery}\n{date};{uri.Authority};{digest}";
            var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(email.AcsAccessKey!), Encoding.UTF8.GetBytes(toSign)));
            acs.Headers.Add("x-ms-date", date); acs.Headers.Add("x-ms-content-sha256", digest);
            acs.Headers.TryAddWithoutValidation("Authorization", $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}");
            acs.Headers.Add("Repeatability-Request-ID", claim.Id.ToString("D")); acs.Headers.Add("Repeatability-First-Sent", claim.Message.CreatedAt.ToString("r", CultureInfo.InvariantCulture));
            return await SendHttpsAsync(acs, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Never retain endpoint URLs, addresses, server replies, credentials, or exception text.
            return NotificationDeliveryOutcome.TransportFailure;
        }
    }

    public static string Sign(ReadOnlySpan<byte> secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + body.Length]; prefix.CopyTo(signed, 0); body.CopyTo(signed.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, signed));
    }

    private async Task<NotificationDeliveryOutcome> SendHttpsAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        // ACS owns a fixed API query; configured webhook URLs may not smuggle credentials in a query.
        NotificationUrlPolicy.Validate(new Uri(uri.GetLeftPart(UriPartial.Path)), configuration.Hosted);
        if (HttpFixture is not null) return await HttpFixture(request, token).ConfigureAwait(false);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancellation) =>
            {
                if (context.DnsEndPoint.Host != uri.DnsSafeHost || context.DnsEndPoint.Port != uri.Port)
                    throw new HttpRequestException("Notification connection refused.");
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellation).ConfigureAwait(false);
                if (addresses.Length is < 1 or > 32 || addresses.Any(a => !NotificationUrlPolicy.Allowed(a, configuration.Hosted)))
                    throw new HttpRequestException("Notification address refused.");
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(addresses[0], uri.Port, cancellation).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 4096 };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? NotificationDeliveryOutcome.Delivered : NotificationDeliveryOutcome.TransportFailure;
    }
}
