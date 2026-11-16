using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Braid.Ai;

namespace Braid.Server;

/// <summary>
/// A credential's managed identity (no client ID: system-assigned), the fixed resource its tokens are for and the
/// platform endpoint that issues them.
/// </summary>
internal readonly record struct ChatManagedIdentity(Guid? ClientId, ChatTokenResource Resource,
    ChatIdentitySource Source = ChatIdentitySource.ContainerApps)
{
    public override string ToString() => nameof(ChatManagedIdentity);
}

internal enum ChatTokenResource { AzureAi, AzureDatabricks }

/// <summary>Where tokens come from: the Container Apps identity endpoint, or a virtual machine's Instance Metadata Service.</summary>
internal enum ChatIdentitySource { ContainerApps, InstanceMetadata }

/// <summary>
/// Keyless model credentials: Microsoft Entra access tokens from the Container Apps managed-identity endpoint
/// (<c>IDENTITY_ENDPOINT</c>, with the platform's <c>IDENTITY_HEADER</c>) or a virtual machine's Instance Metadata
/// Service (fixed at 169.254.169.254), as each credential names, each for one of two fixed resources: the Foundry data
/// plane for Azure OpenAI or Azure Databricks. Each credential reference names one identity, resource and source.
/// Its token is held only in an <see cref="AiBearerSecret"/>, reused until shortly before it expires and for at most an
/// hour, and every dispatch receives its own copy. A failed refresh keeps serving the current token until it nearly
/// expires. Every failure is categorical, and nothing about the endpoint, header, response or token is logged.
/// </summary>
internal sealed class ManagedIdentityCredentialResolver : IAiCredentialResolver, IDisposable
{
    internal const string AzureAiResource = "https://ai.azure.com";
    // The Azure Databricks first-party application, the same in every Azure environment (scope <id>/.default).
    internal const string AzureDatabricksResource = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d";
    internal const string ApiVersion = "2019-08-01";
    // https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-to-use-vm-token
    internal static readonly Uri InstanceMetadataEndpoint = new("http://169.254.169.254/metadata/identity/oauth2/token");
    internal const string InstanceMetadataApiVersion = "2018-02-01";
    // IMDS retry guidance: retry 404, 410, 429, 5xx and timeouts with exponential backoff, waiting at least a second.
    // Three attempts bound a dispatch; a longer IMDS update (410 lasts up to 70 seconds) fails this turn, not the next.
    internal const int InstanceMetadataAttempts = 3;
    internal static readonly TimeSpan InstanceMetadataBackoff = TimeSpan.FromSeconds(1);
    internal const string EndpointSetting = "IDENTITY_ENDPOINT";
    internal const string HeaderSetting = "IDENTITY_HEADER";
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumReuse = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int ResponseBytes = 16_384;

    private readonly Uri? endpoint;
    private readonly Uri instanceMetadata;
    private readonly Func<string, string?> read;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly HttpClient client;
    private readonly Dictionary<string, Identity> references = new(StringComparer.Ordinal);
    private int disposed;

    /// <param name="endpoint">The Container Apps endpoint; required only when a credential uses that source.</param>
    /// <param name="instanceMetadata">Tests only: a loopback stand-in for the fixed Instance Metadata Service endpoint.</param>
    /// <param name="delay">Tests only: the wait between Instance Metadata Service attempts.</param>
    internal ManagedIdentityCredentialResolver(IReadOnlyDictionary<string, ChatManagedIdentity> identities, Uri? endpoint, Func<string, string?> read,
        TimeProvider clock, HttpMessageHandler? handler = null, Uri? instanceMetadata = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.instanceMetadata = RequireInstanceMetadata(instanceMetadata ?? InstanceMetadataEndpoint);
        this.delay = delay ?? ((wait, ct) => Task.Delay(wait, clock, ct));
        // References that name the same identity, resource and source share one cached token.
        var shared = new Dictionary<ChatManagedIdentity, Identity>();
        foreach (var (reference, identity) in identities)
        {
            if (!Enum.IsDefined(identity.Resource) || !Enum.IsDefined(identity.Source)) throw ServerHostOptions.Failure();
            references.Add(reference, shared.TryGetValue(identity, out var existing) ? existing : shared[identity] = new(identity));
        }
        if (shared.Keys.Any(identity => identity.Source == ChatIdentitySource.ContainerApps))
            this.endpoint = RequireEndpoint(endpoint ?? throw ServerHostOptions.Failure());
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ActivityHeadersPropagator = null
        }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The token endpoint the platform publishes: plain HTTP on this host only, without credentials or query.</summary>
    internal static Uri ReadEndpoint(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(HeaderSetting) is not { Length: > 0 } || read(EndpointSetting) is not { Length: > 0 } text ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw ServerHostOptions.Failure();
        return RequireEndpoint(uri);
    }

    private static Uri RequireEndpoint(Uri endpoint) =>
        endpoint is { Scheme: "http", IsLoopback: true, UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 }
            ? endpoint : throw ServerHostOptions.Failure();

    // Production always uses the fixed address; only a test may substitute a loopback listener.
    private static Uri RequireInstanceMetadata(Uri endpoint) =>
        endpoint == InstanceMetadataEndpoint || endpoint is { Scheme: "http", IsLoopback: true, UserInfo.Length: 0, Query.Length: 0, Fragment.Length: 0 }
            ? endpoint : throw ServerHostOptions.Failure();

    internal bool Resolves(string reference) => references.ContainsKey(reference);

    public async ValueTask<AiCredentialResult> ResolveAsync(string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference is null || !references.TryGetValue(reference, out var identity) || Volatile.Read(ref disposed) != 0)
            return CredentialUnavailable.Instance;
        if (Current(identity, refresh: true) is { } cached) return CredentialFound.Create(cached);
        // One refresh per identity at a time; callers that waited reuse its result.
        await identity.Refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Current(identity, refresh: true) is { } refreshed) return CredentialFound.Create(refreshed);
            var fetched = await FetchAsync(identity.Source, ct).ConfigureAwait(false);
            if (fetched is { } token)
            {
                var now = clock.GetUtcNow();
                lock (identity.Gate)
                {
                    // A token fetched while the host disposed this resolver is cleared rather than kept.
                    if (Volatile.Read(ref disposed) != 0)
                    {
                        token.Secret.Dispose();
                        return CredentialUnavailable.Instance;
                    }
                    identity.Token?.Dispose();
                    identity.Token = token.Secret;
                    identity.ExpiresAt = token.ExpiresAt;
                    identity.RefreshAt = Min(token.ExpiresAt - RefreshMargin, now + MaximumReuse);
                    return CredentialFound.Create(token.Secret.Copy());
                }
            }
            return Current(identity, refresh: false) is { } current ? CredentialFound.Create(current) : CredentialUnavailable.Instance;
        }
        finally { identity.Refresh.Release(); }
    }

    private AiBearerSecret? Current(Identity identity, bool refresh)
    {
        var now = clock.GetUtcNow();
        lock (identity.Gate)
            return identity.Token is { } token && now < (refresh ? identity.RefreshAt : identity.ExpiresAt - ExpiryMargin)
                ? token.Copy() : null;
    }

    private Task<(AiBearerSecret Secret, DateTimeOffset ExpiresAt)?> FetchAsync(ChatManagedIdentity identity, CancellationToken ct)
    {
        var resource = identity.Resource == ChatTokenResource.AzureDatabricks ? AzureDatabricksResource : AzureAiResource;
        return identity.Source == ChatIdentitySource.InstanceMetadata ? FetchInstanceMetadataAsync(identity.ClientId, resource, ct)
            : FetchContainerAppsAsync(identity.ClientId, resource, ct);
    }

    private async Task<(AiBearerSecret Secret, DateTimeOffset ExpiresAt)?> FetchContainerAppsAsync(Guid? clientId, string resource, CancellationToken ct)
    {
        // The platform rotates the header; read it for every request.
        if (read(HeaderSetting) is not { Length: > 0 } header) return null;
        var target = endpoint!.AbsoluteUri + "?resource=" + Uri.EscapeDataString(resource) + "&api-version=" + ApiVersion +
            (clientId is { } id ? "&client_id=" + id.ToString("D") : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, target)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try { request.Headers.Add("X-IDENTITY-HEADER", header); }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return null; }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return (await SendAsync(request, relativeExpiry: false, ct).ConfigureAwait(false)).Token;
    }

    private async Task<(AiBearerSecret Secret, DateTimeOffset ExpiresAt)?> FetchInstanceMetadataAsync(Guid? clientId, string resource, CancellationToken ct)
    {
        var target = instanceMetadata.AbsoluteUri + "?api-version=" + InstanceMetadataApiVersion + "&resource=" + Uri.EscapeDataString(resource) +
            (clientId is { } id ? "&client_id=" + id.ToString("D") : "");
        for (var attempt = 1; ; attempt++)
        {
            int status;
            using (var request = new HttpRequestMessage(HttpMethod.Get, target)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact })
            {
                request.Headers.Add("Metadata", "true");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var sent = await SendAsync(request, relativeExpiry: true, ct).ConfigureAwait(false);
                if (sent.Token is { } token) return token;
                status = sent.Status;
            }
            // A refused request or an invalid token response is not retried.
            if (attempt == InstanceMetadataAttempts || !(status is TransportFailure or 404 or 410 or 429 or (>= 500 and <= 599))) return null;
            await delay(InstanceMetadataBackoff * (1 << (attempt - 1)), ct).ConfigureAwait(false);
        }
    }

    private const int TransportFailure = -1;

    /// <summary>One bounded token request: its HTTP status (or a transport failure) and, on success, the parsed token.</summary>
    private async Task<(int Status, (AiBearerSecret Secret, DateTimeOffset ExpiresAt)? Token)> SendAsync(HttpRequestMessage request,
        bool relativeExpiry, CancellationToken ct)
    {
        byte[]? body = null;
        var length = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > ResponseBytes ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return (status, null);
            body = ArrayPool<byte>.Shared.Rent(ResponseBytes + 1);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            int read;
            while ((read = await stream.ReadAsync(body.AsMemory(length, ResponseBytes + 1 - length), timeout.Token).ConfigureAwait(false)) > 0)
                if ((length += read) > ResponseBytes) return (status, null);
            return (status, Parse(body.AsSpan(0, length), clock.GetUtcNow(), relativeExpiry));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return (TransportFailure, null); }
        finally
        {
            if (body is not null)
            {
                Array.Clear(body);
                ArrayPool<byte>.Shared.Return(body);
            }
        }
    }

    /// <summary>
    /// The token response: <c>access_token</c>, <c>expires_on</c> (Unix seconds) and an optional Bearer type. With
    /// <paramref name="relativeExpiry"/> (the Instance Metadata Service), <c>expires_in</c> seconds stand in for a missing
    /// <c>expires_on</c>, as Azure.Identity reads them.
    /// </summary>
    internal static (AiBearerSecret Secret, DateTimeOffset ExpiresAt)? Parse(ReadOnlySpan<byte> json, DateTimeOffset now, bool relativeExpiry = false)
    {
        AiBearerSecret? secret = null;
        long? expiresOn = null, expiresIn = null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (!names.Add(name) || !reader.Read()) return null;
                switch (name)
                {
                    case "access_token":
                        if (reader.TokenType != JsonTokenType.String) return null;
                        secret = Secret(ref reader);
                        if (secret is null) return null;
                        break;
                    case "expires_on":
                        if (Seconds(ref reader) is not { } seconds) return null;
                        expiresOn = seconds;
                        break;
                    case "expires_in" when relativeExpiry:
                        if (Seconds(ref reader) is not { } lifetime) return null;
                        expiresIn = lifetime;
                        break;
                    case "token_type":
                        if (reader.TokenType != JsonTokenType.String || !string.Equals(reader.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                            return null;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || secret is null) return null;
            DateTimeOffset expiresAt;
            if (expiresOn is { } expiry) expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry);
            else if (expiresIn is { } lifetime) expiresAt = now.AddSeconds(lifetime);
            else return null;
            // A token that would need refreshing at once is refused rather than served.
            if (expiresAt - RefreshMargin <= now) return null;
            var result = (secret, expiresAt);
            secret = null;
            return result;
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return null; }
        finally { secret?.Dispose(); }
    }

    // Whole seconds, as a string or a number of at most ten digits.
    private static long? Seconds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number)) return null;
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString()! : System.Text.Encoding.ASCII.GetString(reader.ValueSpan);
        return text.Length is < 1 or > 10 || text.Any(c => c is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? null : seconds;
    }

    private static AiBearerSecret? Secret(ref Utf8JsonReader reader)
    {
        if (reader.ValueSpan.Length is < 1 or > 8_192) return null;
        var characters = ArrayPool<char>.Shared.Rent(reader.ValueSpan.Length);
        try
        {
            var count = reader.CopyString(characters);
            return AiBearerSecret.CopyFrom(characters.AsSpan(0, count));
        }
        finally
        {
            Array.Clear(characters);
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose();
        foreach (var identity in references.Values.Distinct())
            lock (identity.Gate)
            {
                identity.Token?.Dispose();
                identity.Token = null;
            }
    }

    public override string ToString() => nameof(ManagedIdentityCredentialResolver);

    private sealed class Identity(ChatManagedIdentity source)
    {
        internal ChatManagedIdentity Source { get; } = source;
        internal SemaphoreSlim Refresh { get; } = new(1, 1);
        internal object Gate { get; } = new();
        internal AiBearerSecret? Token { get; set; }
        internal DateTimeOffset RefreshAt { get; set; }
        internal DateTimeOffset ExpiresAt { get; set; }
        public override string ToString() => nameof(Identity);
    }
}
