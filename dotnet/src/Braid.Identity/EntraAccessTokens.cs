using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Braid.Identity;

/// <summary>The closed rule that refused a token. Callers see only Denied, or Unavailable for key retrieval.</summary>
internal enum EntraTokenRefusal
{
    None, Unavailable, Format, Signature, Header, Duplicate, Issuer, Audience, Tenant, Version, Delegated, TokenType,
    ObjectId, Subject, Application, Lifetime, Scope, Client
}

/// <summary>
/// Fixed-tenant validation shared by the Entra v2 access-token profiles: the fixed discovery document and
/// signing-key refresh, strict standard JWT validation (RS256 from an admitted key, exact issuer and audience,
/// finite lifetime with no skew), then the common claim checks on the validated token. A profile adds its own
/// rules on the unique payload. There is no issuer, key, authority or transport override.
/// </summary>
internal sealed class EntraAccessTokens : IAsyncDisposable
{
    private readonly EntraEndpointPolicy policy;
    private readonly CancellationTokenSource lifetime = new();
    private readonly EntraOwnedWork validations = new();
    private readonly EntraBackchannel backchannel;
    private readonly EntraMetadata metadata;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> manager;
    private readonly EntraStrictTokenHandler handler;
    private readonly AsyncLocal<CancellationToken> current = new();
    private readonly object disposeSync = new();
    private Task? disposal;

    internal EntraAccessTokens(Guid tenant, Guid audience, EntraHttpsFixtureTransport? fixture)
    {
        policy = new(tenant, audience);
        Tenant = tenant;
        backchannel = new(policy, lifetime.Token, fixture);
        try
        {
            metadata = new(policy, lifetime.Token);
            // The standard manager's default refresh bounds apply: automatic refresh twice a day, and an
            // unknown signing key refreshes at most once per five minutes before last-known-good use.
            manager = new(policy.Discovery, metadata, backchannel, metadata);
            handler = new(policy, manager, metadata, () => current.Value);
        }
        catch
        {
            backchannel.Client.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    internal Guid Tenant { get; }

    /// <summary>
    /// The profile's result, or the closed rule that refused the token. Only Unavailable is not a token finding.
    /// The profile reads only the validated, duplicate-free payload and throws <see cref="Refusal"/> for its rules.
    /// </summary>
    internal async Task<(T? Result, EntraTokenRefusal Refusal)> CheckAsync<T>(string bearerToken,
        Func<JsonElement, DateTimeOffset, T> profile, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Compact JWS only: three base64url segments within the protocol bound.
        if (bearerToken is not { Length: > 0 and <= EntraEndpointPolicy.MaximumProtocolBytes } ||
            bearerToken.Any(c => c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.')) ||
            bearerToken.Count(c => c == '.') != 2)
            return (null, EntraTokenRefusal.Format);
        IDisposable lease;
        try { lease = validations.Enter(); }
        catch (ServerSessionIdentityException) { return (null, EntraTokenRefusal.Unavailable); }
        using (lease)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var signal = new EntraFailureSignal();
            EntraFailureScope.Current.Value = signal;
            current.Value = linked.Token;
            TokenValidationResult result;
            try { result = await handler.ValidateTokenAsync(bearerToken, handler.CreateParameters()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (EntraErrors.Recoverable(error))
            {
                // Configuration and key retrieval failures are thrown, not returned as token findings.
                return (null, EntraTokenRefusal.Unavailable);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (signal.Kind is ServerSessionIdentityFailureKind.Unavailable or ServerSessionIdentityFailureKind.ConfigurationFailure)
                return (null, EntraTokenRefusal.Unavailable);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
                return (null, Standard(result.Exception));
            try { return (Admit(token, profile), EntraTokenRefusal.None); }
            catch (Refusal refusal) { return (null, refusal.Rule); }
            catch (Exception error) when (EntraErrors.Recoverable(error)) { return (null, EntraTokenRefusal.Format); }
        }
    }

    private static EntraTokenRefusal Standard(Exception? error) => error switch
    {
        SecurityTokenInvalidIssuerException => EntraTokenRefusal.Issuer,
        SecurityTokenInvalidAudienceException => EntraTokenRefusal.Audience,
        SecurityTokenExpiredException or SecurityTokenNotYetValidException or SecurityTokenNoExpirationException
            or SecurityTokenInvalidLifetimeException => EntraTokenRefusal.Lifetime,
        SecurityTokenMalformedException => EntraTokenRefusal.Format,
        _ => EntraTokenRefusal.Signature
    };

    // Inspects only the already validated token, and supplements standard validation with the v2 profile.
    private T Admit<T>(JsonWebToken token, Func<JsonElement, DateTimeOffset, T> profile)
    {
        using (var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedHeader), new JsonDocumentOptions { MaxDepth = 8 }))
        {
            Unique(header.RootElement);
            if (Text(header.RootElement, "alg", EntraTokenRefusal.Header) != SecurityAlgorithms.RsaSha256 ||
                header.RootElement.TryGetProperty("typ", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "JWT") ||
                header.RootElement.TryGetProperty("crit", out _))
                throw new Refusal(EntraTokenRefusal.Header);
        }
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload), new JsonDocumentOptions { MaxDepth = 16 });
        var root = payload.RootElement;
        Unique(root);
        if (Text(root, "iss", EntraTokenRefusal.Issuer) != policy.Issuer) throw new Refusal(EntraTokenRefusal.Issuer);
        if (Text(root, "aud", EntraTokenRefusal.Audience) != policy.Client) throw new Refusal(EntraTokenRefusal.Audience);
        if (Text(root, "tid", EntraTokenRefusal.Tenant) != policy.Tenant) throw new Refusal(EntraTokenRefusal.Tenant);
        if (Text(root, "ver", EntraTokenRefusal.Version) != "2.0") throw new Refusal(EntraTokenRefusal.Version);
        var expires = NumericDate(root, "exp");
        var notBefore = NumericDate(root, "nbf");
        var issued = NumericDate(root, "iat");
        var result = profile(root, expires);
        var now = DateTimeOffset.UtcNow;
        if (expires <= now || notBefore > now || issued > now || notBefore >= expires || issued >= expires)
            throw new Refusal(EntraTokenRefusal.Lifetime);
        return result;
    }

    private static void Unique(JsonElement element)
    {
        try { EntraEndpointPolicy.RequireUniqueObject(element); }
        catch (ServerSessionIdentityException) { throw new Refusal(EntraTokenRefusal.Duplicate); }
    }

    internal static string Text(JsonElement element, string name, EntraTokenRefusal rule) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : throw new Refusal(rule);

    /// <summary>A required canonical nonzero lowercase <c>D</c>-format UUID claim.</summary>
    internal static Guid Id(JsonElement element, string name, EntraTokenRefusal rule)
    {
        var text = Text(element, name, rule);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") ? id : throw new Refusal(rule);
    }

    private static DateTimeOffset NumericDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) ||
            seconds is < 1 or > 253402300799)
            throw new Refusal(EntraTokenRefusal.Lifetime);
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    internal sealed class Refusal(EntraTokenRefusal rule) : Exception
    {
        internal EntraTokenRefusal Rule { get; } = rule;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeSync) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var validationDrain = validations.CloseAndDrain();
        var retrievalDrain = metadata.CloseAndDrainAsync();
        var sendDrain = backchannel.CloseAndDrainAsync();
        await lifetime.CancelAsync();
        await Task.WhenAll(validationDrain, retrievalDrain, sendDrain);
        backchannel.Client.Dispose();
        lifetime.Dispose();
    }

    public override string ToString() => nameof(EntraAccessTokens);
}
