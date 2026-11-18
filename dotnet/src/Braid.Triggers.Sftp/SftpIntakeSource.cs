using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Braid.Triggers.Sftp;

/// <summary>Read-only, flat SFTP inbox. Publish files by rename to order-id.sha256.ready in an account-confined directory.</summary>
public sealed partial class SftpIntakeSource : ITriggerSource
{
    private readonly string directory;
    private readonly Func<SftpClient> open;
    public TriggerConnectorReference Connector { get; }

    public SftpIntakeSource(TriggerConnectorReference connector, string directory, string host, int port, string user,
        string hostKeySha256, string privateKeyFile, Func<string, int, string> privateReader)
    {
        ArgumentNullException.ThrowIfNull(privateReader);
        Connector = connector ?? throw new ArgumentNullException(nameof(connector));
        if (!DirectoryPattern().IsMatch(directory) || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(user) || !Path.IsPathFullyQualified(privateKeyFile) || Convert.FromBase64String(hostKeySha256 + "=").Length != 32)
            throw new ArgumentException("Invalid SFTP intake registration.");
        this.directory = directory;
        open = () =>
        {
            using var key = new MemoryStream(Encoding.UTF8.GetBytes(privateReader(privateKeyFile, 65536)));
            var client = new SftpClient(host, port, user, new PrivateKeyFile(key));
            client.HostKeyReceived += (_, e) => e.CanTrust = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hostKeySha256), Encoding.ASCII.GetBytes(e.FingerPrintSHA256));
            return client;
        };
    }

    [GeneratedRegex(@"\A(?:/[a-zA-Z0-9_-]{1,64}){1,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DirectoryPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9_-]{1,64}\.([0-9a-f]{64})\.ready\z", RegexOptions.CultureInvariant)]
    private static partial Regex PublishedName();

    private async Task ConnectAsync(SftpClient client, CancellationToken token)
    {
        await client.ConnectAsync(token).ConfigureAwait(false);
        var path = "";
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + segment;
            if (await client.GetAsync(path, token).ConfigureAwait(false) is not { IsSymbolicLink: false, IsDirectory: true })
                throw new InvalidOperationException("SFTP inbox unavailable.");
        }
    }

    public async Task<TriggerSourcePage> ListPageAsync(string? cursor, DateTimeOffset cutoff, CancellationToken token)
    {
        if (cursor is not null && !PublishedName().IsMatch(cursor)) throw new ArgumentException("Invalid intake cursor.");
        using var client = open();
        await ConnectAsync(client, token).ConfigureAwait(false);
        var items = new List<TriggerSourceItem>();
        var visited = 0;
        await foreach (var entry in client.ListDirectoryAsync(directory, token).ConfigureAwait(false))
        {
            if (++visited > 4096) throw new InvalidOperationException("Inbox exceeds the bounded listing policy.");
            if (!entry.IsRegularFile || entry.IsSymbolicLink || entry.LastWriteTimeUtc > cutoff.UtcDateTime ||
                string.CompareOrdinal(entry.Name, cursor) <= 0 || PublishedName().Match(entry.Name) is not { Success: true } match) continue;
            items.Add(new(entry.Name, match.Groups[1].Value, entry.Length));
        }
        // Only one object's bytes can be resident in the runner per page.
        var first = items.MinBy(item => item.Name, StringComparer.Ordinal);
        return new(first is null ? [] : [first], items.Count > 1 ? first!.Name : null);
    }

    public async Task<TriggerSourceRead> ReadAsync(TriggerSourceItem item, int maximumBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (PublishedName().Match(item.Name) is not { Success: true } match || match.Groups[1].Value != item.Version)
            throw new ArgumentException("Invalid published intake object.");
        if (maximumBytes is <= 0 or > TriggerBatchEnvelope.MaximumEnvelopeBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (item.Length > maximumBytes) return new(TriggerSourceReadStatus.TooLarge);
        using var client = open();
        await ConnectAsync(client, token).ConfigureAwait(false);
        var path = directory + "/" + item.Name;
        try
        {
            if (await client.GetAsync(path, token).ConfigureAwait(false) is not { IsRegularFile: true, IsSymbolicLink: false } file || file.Length != item.Length)
                return new(TriggerSourceReadStatus.Changed);
            using var stream = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
            var bytes = new byte[(int)item.Length];
            if (await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false) != bytes.Length ||
                await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                return new(TriggerSourceReadStatus.Changed);
            return Sha256Digest.FromBytes(bytes).Value != item.Version ? new(TriggerSourceReadStatus.DigestMismatch) : new(TriggerSourceReadStatus.Read, bytes);
        }
        catch (SftpPathNotFoundException) { return new(TriggerSourceReadStatus.Missing); }
    }
}
