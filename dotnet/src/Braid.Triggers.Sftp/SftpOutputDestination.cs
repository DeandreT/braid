using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Braid.Deployment.Triggers;
using Braid.Domain;

namespace Braid.Triggers.Sftp;

/// <summary>
/// How a server publishes a finished upload. Only a rename that refuses to replace an existing name (the SFTP v3 rename that
/// OpenSSH implements with link and unlink on a filesystem with hard links) is supported; anything else is refused, not simulated.
/// </summary>
public enum SftpPublishContract { Unsupported, NoReplaceRename }

/// <summary>
/// Operator-private settings for one SFTP destination. The host key is pinned (never trusted on first use) and the password or
/// private key is a private-file reference, never stored configuration. Outputs stay inside the registered root and prefix.
/// </summary>
public sealed partial record SftpDestinationConnector
{
    public SftpDestinationConnector(TriggerConnectorReference connector, string host, int port, string userName, string hostKeySha256,
        string? passwordFile, string? privateKeyFile, string root, string prefix, SftpPublishContract publish)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (host is null || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535 ||
            userName is null || !UserPattern().IsMatch(userName) || hostKeySha256 is null || !FingerprintPattern().IsMatch(hostKeySha256) ||
            (passwordFile is null) == (privateKeyFile is null) || !PrivateFile(passwordFile ?? privateKeyFile!) ||
            root is null || !RootPattern().IsMatch(root) || root.Split('/').Any(segment => segment is "." or "..") ||
            prefix is null || !PrefixPattern().IsMatch(prefix))
            throw new ArgumentException("Invalid SFTP destination connector.");
        if (publish != SftpPublishContract.NoReplaceRename)
            throw new ArgumentException("The SFTP server must support an atomic no-replace publish; stat-then-rename is not a substitute.");
        Connector = connector; Host = host; Port = port; UserName = userName; HostKeySha256 = hostKeySha256; PasswordFile = passwordFile;
        PrivateKeyFile = privateKeyFile; Root = root; Prefix = prefix;
    }

    public TriggerConnectorReference Connector { get; }
    public string Host { get; }
    public int Port { get; }
    public string UserName { get; }
    /// <summary>The unpadded base64 SHA-256 fingerprint that ssh-keygen -l prints after "SHA256:".</summary>
    public string HostKeySha256 { get; }
    public string? PasswordFile { get; }
    public string? PrivateKeyFile { get; }
    public string Root { get; }
    public string Prefix { get; }
    public string Directory => Prefix.Length == 0 ? Root : Root + "/" + Prefix.TrimEnd('/');
    public override string ToString() => nameof(SftpDestinationConnector);

    private static bool PrivateFile(string path) => Path.IsPathFullyQualified(path);
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();
    [GeneratedRegex("^[A-Za-z0-9+/]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintPattern();
    [GeneratedRegex("^(?:/[A-Za-z0-9._-]{1,128}){1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex RootPattern();
    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?/){0,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>
/// Create-only delivery: the exact bytes go to an attempt-specific temporary name, are read back and verified, then published
/// under the deterministic delivery key with a rename that never replaces. An existing final file is accepted only when it holds
/// exactly this delivery's bytes; anything else is a conflict. Nothing is overwritten, appended or renamed over.
/// </summary>
public sealed class SftpOutputDestination(SftpDestinationConnector connector, Func<string, int, string> privateReader) : IOutputDestination
{
    public TriggerDestinationKind Kind => TriggerDestinationKind.Sftp;
    public TriggerConnectorReference Connector { get; } = (connector ?? throw new ArgumentNullException(nameof(connector))).Connector;
    public string Key(FrozenTriggerDelivery delivery) => connector.Directory + "/" + (delivery ?? throw new ArgumentNullException(nameof(delivery))).DeliveryId.ToString("D");

    public async Task<DeliveryObservationKind> SendAsync(FrozenTriggerDelivery delivery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.DestinationVersion != Connector.Version) return DeliveryObservationKind.PermanentFailure;
        SftpClient client;
        try
        {
            client = new SftpClient(new ConnectionInfo(connector.Host, connector.Port, connector.UserName, Authentication()));
            client.HostKeyReceived += (_, e) => e.CanTrust = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(e.FingerPrintSHA256), Encoding.ASCII.GetBytes(connector.HostKeySha256));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return DeliveryObservationKind.ProvenKnownNotAccepted; }
        using (client)
        {
            // A connection, authentication or host-key failure happens before anything is written.
            try { await client.ConnectAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return DeliveryObservationKind.ProvenKnownNotAccepted; }
            var final = Key(delivery);
            var temporary = connector.Directory + "/." + delivery.DeliveryId.ToString("D") + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".part";
            var published = false;
            try
            {
                foreach (var path in new[] { connector.Root, connector.Directory }.Distinct(StringComparer.Ordinal))
                    if (await client.GetAsync(path, token).ConfigureAwait(false) is not { IsSymbolicLink: false, IsDirectory: true })
                        return DeliveryObservationKind.PermanentFailure;
                using (var content = new MemoryStream(delivery.CopyContent(), writable: false))
                    await client.UploadFileAsync(content, temporary, false, null, token).ConfigureAwait(false);
                if (!await MatchesAsync(client, temporary, delivery, token).ConfigureAwait(false)) return DeliveryObservationKind.ProvenKnownNotAccepted;
                try
                {
                    published = true;
                    await client.RenameFileAsync(temporary, final, token).ConfigureAwait(false);
                    return DeliveryObservationKind.Accepted;
                }
                // The server refused the rename; an existing final name is accepted only with this delivery's exact bytes.
                catch (SshException error) when (error is not SshConnectionException and not SshOperationTimeoutException)
                {
                    published = false;
                    return !await client.ExistsAsync(final, token).ConfigureAwait(false) ? DeliveryObservationKind.ProvenKnownNotAccepted
                        : await MatchesAsync(client, final, delivery, token).ConfigureAwait(false) ? DeliveryObservationKind.Accepted
                        : DeliveryObservationKind.PermanentFailure;
                }
            }
            catch (SftpPermissionDeniedException) when (!published) { return DeliveryObservationKind.PermanentFailure; }
            // Before the rename was requested nothing was published; afterwards the outcome is unknown, and the create-only key keeps a repeat safe.
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { return published ? DeliveryObservationKind.Unknown : DeliveryObservationKind.ProvenKnownNotAccepted; }
            finally
            {
                try { if (client.IsConnected && await client.ExistsAsync(temporary, CancellationToken.None).ConfigureAwait(false)) await client.DeleteFileAsync(temporary, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { }
            }
        }
    }

    private static async Task<bool> MatchesAsync(SftpClient client, string path, FrozenTriggerDelivery delivery, CancellationToken token)
    {
        if (await client.GetAsync(path, token).ConfigureAwait(false) is not { IsRegularFile: true } file || file.Length != delivery.ContentLength) return false;
        using var copy = new MemoryStream();
        await client.DownloadFileAsync(path, copy, token).ConfigureAwait(false);
        return copy.Length == delivery.ContentLength && Sha256Digest.FromBytes(copy.ToArray()) == delivery.ContentDigest;
    }

    private AuthenticationMethod Authentication()
    {
        if (connector.PasswordFile is { } password) return new PasswordAuthenticationMethod(connector.UserName, privateReader(password, 4096).TrimEnd('\r', '\n'));
        using var key = new MemoryStream(Encoding.UTF8.GetBytes(privateReader(connector.PrivateKeyFile!, 65536)));
        return new PrivateKeyAuthenticationMethod(connector.UserName, new PrivateKeyFile(key));
    }

    public override string ToString() => nameof(SftpOutputDestination);
}
