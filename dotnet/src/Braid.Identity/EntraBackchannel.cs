using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;

namespace Braid.Identity;

internal sealed class EntraBackchannel : DelegatingHandler, IDocumentRetriever
{
    private readonly EntraEndpointPolicy policy;
    private readonly EntraOwnedWork sends = new();
    private readonly CancellationToken lifetime;
    private readonly EntraHttpsFixtureTransport? fixture;
    internal HttpClient Client { get; }

    internal EntraBackchannel(EntraEndpointPolicy policy, CancellationToken lifetime,
        EntraHttpsFixtureTransport? fixture)
    {
        this.policy = policy;
        this.lifetime = lifetime;
        this.fixture = fixture;
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 32
        };
        fixture?.Configure(sockets);
        InnerHandler = sockets;
        Client = new HttpClient(this, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = EntraEndpointPolicy.MaximumDocumentBytes
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var lease = sends.Enter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        HttpResponseMessage? response = null;
        try
        {
            policy.RequireSend(request.Method, request.RequestUri);
            cancellation.Token.ThrowIfCancellationRequested();
            fixture?.Record(request);
            response = await base.SendAsync(request, cancellation.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 or 408 or 429 or >= 500 ||
                (request.Method == HttpMethod.Post && (status is 401 or 403)))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            await BufferOwnedResponseAsync(response, cancellation.Token);
            if (request.Method == HttpMethod.Post)
                RequireTokenResponse(await response.Content.ReadAsByteArrayAsync(cancellation.Token), response.IsSuccessStatusCode);
            // Buffer before releasing the owned send lease: consumers cannot start an
            // untracked network read after shutdown, including ordinary token redemption.
            var result = response;
            response = null;
            return result;
        }
        catch (ServerSessionIdentityException error) { EntraFailureScope.Report(error.Kind); throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        {
            EntraFailureScope.Report(ServerSessionIdentityFailureKind.Unavailable);
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
        finally { response?.Dispose(); }
    }

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        try
        {
            using var response = await Client.GetAsync(address, HttpCompletionOption.ResponseContentRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            var json = new UTF8Encoding(false, true).GetString(bytes);
            if (address == policy.Discovery)
                policy.RequireDiscovery(json); // Before standard retrieval follows JwksUri.
            else
                EntraEndpointPolicy.RequireKeysJson(json);
            return json;
        }
        catch (ServerSessionIdentityException error) { EntraFailureScope.Report(error.Kind); throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        {
            EntraFailureScope.Report(ServerSessionIdentityFailureKind.Unavailable);
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    internal Task CloseAndDrainAsync() => sends.CloseAndDrain();

    private static async Task BufferOwnedResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        var original = response.Content;
        await using var stream = await original.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(block.AsMemory(), cancellation);
            if (read == 0) break;
            if (buffer.Length + read > EntraEndpointPolicy.MaximumDocumentBytes)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
            buffer.Write(block, 0, read);
        }
        var replacement = new ByteArrayContent(buffer.ToArray());
        foreach (var header in original.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;
        original.Dispose();
    }

    private static void RequireTokenResponse(byte[] bytes, bool success)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            EntraEndpointPolicy.RequireUniqueObject(json.RootElement);
            if (success && (EntraEndpointPolicy.ReadRequired(json.RootElement, "id_token").Length >
                EntraEndpointPolicy.MaximumProtocolBytes))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        { throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied); }
    }
}
