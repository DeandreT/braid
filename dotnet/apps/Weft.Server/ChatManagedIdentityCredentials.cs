using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Weft.Ai;

namespace Weft.Server;

/// <summary>
/// Keyless model credentials: Microsoft Entra access tokens for Azure OpenAI from the Container Apps managed-identity
/// endpoint (<c>IDENTITY_ENDPOINT</c>, with the platform's <c>IDENTITY_HEADER</c>), always for the fixed Foundry
/// data-plane resource. Each credential reference names one identity. Its token is held only in an
/// <see cref="AiBearerSecret"/>, reused until shortly before it expires and for at most an hour, and every dispatch
/// receives its own copy. A failed refresh keeps serving the current token until it nearly expires. Every failure is
/// categorical, and nothing about the endpoint, header or token is logged.
/// </summary>
internal sealed class ManagedIdentityCredentialResolver : IAiCredentialResolver, IDisposable
{
    internal const string Resource = "https://ai.azure.com";
    internal const string ApiVersion = "2019-08-01";
    internal const string EndpointSetting = "IDENTITY_ENDPOINT";
    internal const string HeaderSetting = "IDENTITY_HEADER";
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumReuse = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int ResponseBytes = 16_384;

    private readonly Uri endpoint;
    private readonly Func<string, string?> read;
    private readonly TimeProvider clock;
    private readonly HttpClient client;
    private readonly Dictionary<string, Identity> references = new(StringComparer.Ordinal);
    private int disposed;

    internal ManagedIdentityCredentialResolver(IReadOnlyDictionary<string, Guid?> identities, Uri endpoint, Func<string, string?> read,
        TimeProvider clock, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(identities);
        this.endpoint = RequireEndpoint(endpoint);
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // References that name the same identity share one cached token.
        var shared = new Dictionary<Guid, Identity>();
        Identity? system = null;
        foreach (var (reference, clientId) in identities)
            references.Add(reference, clientId is { } id
                ? shared.TryGetValue(id, out var existing) ? existing : shared[id] = new(clientId)
                : system ??= new(null));
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
            var fetched = await FetchAsync(identity.ClientId, ct).ConfigureAwait(false);
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

    private async Task<(AiBearerSecret Secret, DateTimeOffset ExpiresAt)?> FetchAsync(Guid? clientId, CancellationToken ct)
    {
        // The platform rotates the header; read it for every request.
        if (read(HeaderSetting) is not { Length: > 0 } header) return null;
        var target = endpoint.AbsoluteUri + "?resource=" + Uri.EscapeDataString(Resource) + "&api-version=" + ApiVersion +
            (clientId is { } id ? "&client_id=" + id.ToString("D") : "");
        byte[]? body = null;
        var length = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, target)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.Add("X-IDENTITY-HEADER", header);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > ResponseBytes ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return null;
            body = ArrayPool<byte>.Shared.Rent(ResponseBytes + 1);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            int read;
            while ((read = await stream.ReadAsync(body.AsMemory(length, ResponseBytes + 1 - length), timeout.Token).ConfigureAwait(false)) > 0)
                if ((length += read) > ResponseBytes) return null;
            return Parse(body.AsSpan(0, length), clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return null; }
        finally
        {
            if (body is not null)
            {
                Array.Clear(body);
                ArrayPool<byte>.Shared.Return(body);
            }
        }
    }

    /// <summary>The token response: <c>access_token</c>, <c>expires_on</c> (Unix seconds) and an optional Bearer type.</summary>
    internal static (AiBearerSecret Secret, DateTimeOffset ExpiresAt)? Parse(ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        AiBearerSecret? secret = null;
        long? expiresOn = null;
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
                        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Number)) return null;
                        var text = reader.TokenType == JsonTokenType.String ? reader.GetString()! : System.Text.Encoding.ASCII.GetString(reader.ValueSpan);
                        if (text.Length is < 1 or > 10 || text.Any(c => c is < '0' or > '9') ||
                            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
                        expiresOn = seconds;
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
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || secret is null || expiresOn is not { } expiry) return null;
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry);
            // A token that would need refreshing at once is refused rather than served.
            if (expiresAt - RefreshMargin <= now) return null;
            var result = (secret, expiresAt);
            secret = null;
            return result;
        }
        catch (Exception error) when (SessionHostResources.Nonfatal(error)) { return null; }
        finally { secret?.Dispose(); }
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

    private sealed class Identity(Guid? clientId)
    {
        internal Guid? ClientId { get; } = clientId;
        internal SemaphoreSlim Refresh { get; } = new(1, 1);
        internal object Gate { get; } = new();
        internal AiBearerSecret? Token { get; set; }
        internal DateTimeOffset RefreshAt { get; set; }
        internal DateTimeOffset ExpiresAt { get; set; }
        public override string ToString() => nameof(Identity);
    }
}
