using System.Net;
using System.Net.Mail;

namespace Weft.Application.Automation;

/// <summary>Operator-owned private configuration. Public APIs accept destination IDs, never URLs, addresses or secrets.</summary>
public sealed record NotificationConfiguration
{
    public string Origin { get; init; } = "";
    public bool Hosted { get; init; } = true;
    public IReadOnlyList<ConfiguredWebhook> Webhooks { get; init; } = [];
    public IReadOnlyList<ConfiguredRecipient> Recipients { get; init; } = [];
    public ConfiguredEmail? Email { get; init; }
    public static NotificationConfiguration Disabled { get; } = new();

    public void Validate()
    {
        if (Webhooks.Count > 256 || Recipients.Count > 10000) throw new ArgumentException("Notification configuration exceeds its bound.");
        if (Webhooks.Count == 0 && Recipients.Count == 0 && Email is null && Origin == "") return;
        var origin = new Uri(Origin, UriKind.Absolute);
        if (origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" || origin.UserInfo != "")
            throw new ArgumentException("Notification origin requires an HTTPS origin.");
        var ids = new HashSet<(Guid, Guid, string)>();
        foreach (var hook in Webhooks)
        {
            if (hook.OrganizationId == Guid.Empty || hook.ProjectId == Guid.Empty || !ValidId(hook.Id) ||
                !ids.Add((hook.OrganizationId, hook.ProjectId, hook.Id))) throw new ArgumentException("Notification destination identity refused.");
            NotificationUrlPolicy.Validate(new(hook.Url), Hosted);
            if (Convert.FromBase64String(hook.Secret).Length is < 32 or > 64) throw new ArgumentException("Webhook keys require 32 to 64 random bytes.");
        }
        if (Recipients.Select(r => (r.OrganizationId, r.PrincipalId)).Distinct().Count() != Recipients.Count)
            throw new ArgumentException("Duplicate notification recipients.");
        foreach (var recipient in Recipients)
            if (recipient.OrganizationId == Guid.Empty || recipient.PrincipalId == Guid.Empty ||
                !MailAddress.TryCreate(recipient.Address, out var parsed) || parsed.Address != recipient.Address || recipient.Address.Length > 254)
                throw new ArgumentException("Notification recipient refused.");
        Email?.Validate(Hosted);
    }

    public static bool ValidId(string id) => id.Length is >= 1 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public override string ToString() => nameof(NotificationConfiguration);
}

public sealed record ConfiguredWebhook(Guid OrganizationId, Guid ProjectId, string Id, string Url, string Secret)
{
    public override string ToString() => nameof(ConfiguredWebhook);
}
public sealed record ConfiguredRecipient(Guid OrganizationId, Guid PrincipalId, string Address)
{
    public override string ToString() => nameof(ConfiguredRecipient);
}
public sealed record ConfiguredEmail(string Provider, string Sender, string? SmtpHost = null, int SmtpPort = 587,
    string? Username = null, string? Password = null, string? AcsEndpoint = null, string? AcsAccessKey = null)
{
    public void Validate(bool hosted)
    {
        if (!MailAddress.TryCreate(Sender, out var sender) || sender.Address != Sender || Sender.Length > 254)
            throw new ArgumentException("Notification sender refused.");
        if (Provider == "smtp")
        {
            if (SmtpHost is null || Uri.CheckHostName(SmtpHost) == UriHostNameType.Unknown || SmtpPort is < 1 or > 65535 ||
                (Username is null) != (Password is null)) throw new ArgumentException("SMTP configuration refused.");
        }
        else if (Provider == "acs")
        {
            var endpoint = new Uri(AcsEndpoint ?? "", UriKind.Absolute);
            NotificationUrlPolicy.Validate(endpoint, hosted);
            if (!endpoint.DnsSafeHost.EndsWith(".communication.azure.com", StringComparison.Ordinal) || !endpoint.IsDefaultPort ||
                endpoint.AbsolutePath != "/" || endpoint.Query != "" || Convert.FromBase64String(AcsAccessKey ?? "").Length != 32)
                throw new ArgumentException("ACS configuration refused.");
        }
        else throw new ArgumentException("Unknown email provider.");
    }
    public override string ToString() => nameof(ConfiguredEmail);
}

public static class NotificationUrlPolicy
{
    public static void Validate(Uri uri, bool hosted)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo != "" || uri.Fragment != "" || uri.Query != "" ||
            !uri.IsDefaultPort || uri.OriginalString.Length > 2048 || uri.AbsoluteUri.Length > 2048 || uri.HostNameType == UriHostNameType.Unknown ||
            uri.DnsSafeHost.EndsWith(".", StringComparison.Ordinal) || uri.DnsSafeHost.Contains('%', StringComparison.Ordinal))
            throw new ArgumentException("Notification HTTPS destination refused.");
        if (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && !Allowed(ip, hosted)) throw new ArgumentException("Notification address refused.");
        if (hosted && (uri.IsLoopback || !uri.DnsSafeHost.Contains('.', StringComparison.Ordinal) ||
            uri.DnsSafeHost.EndsWith(".localhost", StringComparison.Ordinal) || uri.DnsSafeHost.EndsWith(".local", StringComparison.Ordinal)))
            throw new ArgumentException("Notification host refused.");
    }

    public static bool Allowed(IPAddress address, bool hosted)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!hosted && IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is not (0 or 10 or 127) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] is 0 or 168 || bytes[1] == 88 && bytes[2] == 99)) &&
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) && bytes[0] < 224;
        // Only global unicast; reject unique-local/link-local, multicast, NAT64, 6to4 and Teredo.
        return bytes.Length == 16 && address.ScopeId == 0 && (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] <= 0x01 || bytes[2] == 0x0d)) &&
            !(bytes[0] == 0x3f && (bytes[1] == 0xfe || bytes[1] == 0xff && (bytes[2] & 0xf0) == 0));
    }
}
