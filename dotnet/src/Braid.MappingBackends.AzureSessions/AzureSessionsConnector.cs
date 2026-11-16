using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using Azure.Core;
using Braid.MappingBackends.Workers;

namespace Braid.MappingBackends.AzureSessions;

/// <summary>Trusted host configuration for one custom-container session pool that runs the Ferrule service image.</summary>
public sealed class AzureSessionsConnectorOptions
{
    /// <summary>The Microsoft Entra scope of the dynamic-sessions management API.</summary>
    public const string TokenScope = "https://dynamicsessions.io/.default";

    /// <summary>The management API version of the session stop operation.</summary>
    public const string ManagementApiVersion = "2025-02-02-preview";

    public AzureSessionsConnectorOptions(Uri poolManagementEndpoint, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        PoolManagementEndpoint = RequireEndpoint(poolManagementEndpoint);
        Credential = credential;
    }

    /// <summary>One canonical HTTPS origin with a DNS host: no user information, path, query or fragment.</summary>
    public static Uri RequireEndpoint(Uri poolManagementEndpoint)
    {
        ArgumentNullException.ThrowIfNull(poolManagementEndpoint);
        if (!poolManagementEndpoint.IsAbsoluteUri || poolManagementEndpoint.Scheme != Uri.UriSchemeHttps || poolManagementEndpoint.UserInfo.Length != 0 ||
            poolManagementEndpoint.AbsolutePath != "/" || poolManagementEndpoint.Query.Length != 0 || poolManagementEndpoint.Fragment.Length != 0 ||
            poolManagementEndpoint.HostNameType != UriHostNameType.Dns ||
            poolManagementEndpoint.OriginalString != poolManagementEndpoint.GetLeftPart(UriPartial.Authority))
            throw new ArgumentException("The pool management endpoint must be one canonical HTTPS origin.", nameof(poolManagementEndpoint));
        return poolManagementEndpoint;
    }

    public Uri PoolManagementEndpoint { get; }
    public TokenCredential Credential { get; }

    /// <summary>Test seam for a local stub; production endpoints use the platform trust store and host name.</summary>
    internal RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }

    internal TimeSpan StopBound { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan TokenRefreshMargin { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Azure Container Apps dynamic sessions: every channel is one fresh session, addressed by a random identifier that
/// is never used again, in a custom-container pool with egress disabled. Requests carry a managed-identity token
/// for the pool's management endpoint, which forwards them to the session's Ferrule service. The session is stopped
/// once its operation has settled; the pool's lifecycle removes it if the stop request is lost.
/// </summary>
public sealed class AzureSessionsConnector : IFerruleServiceConnector, IDisposable
{
    private readonly AzureSessionsConnectorOptions options;
    private readonly HttpClient client;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private AccessToken? token;
    private int disposed;

    public AzureSessionsConnector(AzureSessionsConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = null,
            PreAuthenticate = false,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ActivityHeadersPropagator = null
        };
        if (options.ServerCertificateValidation is { } validation)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validation };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public IFerruleServiceChannel Open() => new Channel(this, "braid-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    /// <summary>How long closing a channel may wait for its session's stop request.</summary>
    public TimeSpan StopBound => options.StopBound;

    private Uri Address(string path, string identifier, bool management) => new(options.PoolManagementEndpoint,
        path + (management ? "?api-version=" + AzureSessionsConnectorOptions.ManagementApiVersion + "&" : "?") + "identifier=" + identifier);

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (token is not { } current || current.ExpiresOn - options.TokenRefreshMargin <= DateTimeOffset.UtcNow)
            {
                try
                {
                    token = current = await options.Credential.GetTokenAsync(
                        new TokenRequestContext([AzureSessionsConnectorOptions.TokenScope]), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                {
                    // Nothing was sent; the runner reports an unavailable backend and may retry.
                    throw new HttpRequestException("The session pool token could not be acquired.", error);
                }
            }

            return current.Token;
        }
        finally
        {
            tokenGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string identifier, bool management, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: false } path || !path.OriginalString.StartsWith('/') || path.OriginalString.Contains('?'))
            throw new ArgumentException("A service-relative request path is required.", nameof(request));
        var bearer = await TokenAsync(cancellationToken).ConfigureAwait(false);
        request.RequestUri = Address(path.OriginalString, identifier, management);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose();
        tokenGate.Dispose();
    }

    private sealed class Channel(AzureSessionsConnector connector, string identifier) : IFerruleServiceChannel
    {
        private int stopped;

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            return connector.SendAsync(request, identifier, management: false, cancellationToken);
        }

        /// <summary>Stops the session; the identifier is never used again, so a lost stop only delays reclamation.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            try
            {
                using var bound = new CancellationTokenSource(connector.options.StopBound);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/.management/stopSession", UriKind.Relative))
                {
                    Content = new ByteArrayContent([])
                };
                using var response = await connector.SendAsync(request, identifier, management: true, bound.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException
                or System.Security.Authentication.AuthenticationException)
            {
                // The pool's lifecycle reclaims the session.
            }
        }
    }
}
