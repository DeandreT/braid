using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Weft.Storage.FileSystem;

namespace Weft.MachineEndpoint;

/// <summary>
/// A container health check without a shell or HTTP client in the image: one GET of this host's own live or ready route
/// on its loopback listener. It reads only the listener and transport settings, never a credential, and a directHttps
/// probe admits only the host's configured leaf certificate.
/// </summary>
internal static class MappingEndpointProbe
{
    internal const string Command = "probe";

    internal static async Task<int> RunAsync(string[] arguments, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(environment);
        if (arguments is not [Command, "live" or "ready"])
        {
            Console.Error.WriteLine("Usage: weft-mapping-endpoint [probe live|ready]");
            return 2;
        }
        Uri address;
        byte[]? pinned;
        try { (address, pinned) = Target(arguments[1], environment); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            Console.Error.WriteLine("WeftMappingEndpointProbeConfigurationFailed");
            return 2;
        }
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            pinned is not null && certificate is not null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pinned);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.NoContent ? 0 : 1;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException) { return 1; }
    }

    private static (Uri Address, byte[]? Pinned) Target(string route, Func<string, string?> environment)
    {
        var listen = environment(MappingEndpointSettings.Listen);
        if (listen is null || !IPEndPoint.TryParse(listen, out var endpoint) || endpoint.Port == 0 || listen != endpoint.ToString())
            throw MappingEndpointSettings.Failure();
        // A wildcard listener is probed on the loopback address of the same family.
        var host = endpoint.Address.Equals(IPAddress.Any) ? IPAddress.Loopback
            : endpoint.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : endpoint.Address;
        byte[]? pinned = null;
        var scheme = environment(MappingEndpointSettings.Transport) switch
        {
            "platformIngress" => Uri.UriSchemeHttp,
            "directHttps" => Uri.UriSchemeHttps,
            _ => throw MappingEndpointSettings.Failure()
        };
        if (scheme == Uri.UriSchemeHttps)
        {
            using var leaf = X509Certificate2.CreateFromPem(ProtectedHostFiles.ReadText(
                environment(MappingEndpointSettings.TlsCertificateFile) ?? throw MappingEndpointSettings.Failure(), 65_536));
            pinned = leaf.GetCertHash(HashAlgorithmName.SHA256);
        }
        var path = route == "live" ? MappingEndpointRoutes.LivePath : MappingEndpointRoutes.ReadyPath;
        return (new UriBuilder(scheme, host.ToString(), endpoint.Port, path).Uri, pinned);
    }
}
