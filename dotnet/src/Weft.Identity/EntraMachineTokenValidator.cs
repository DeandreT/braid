using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Weft.Application;

namespace Weft.Identity;

/// <summary>The closed rule that refused a token. Callers see only Denied, or Unavailable for key retrieval.</summary>
internal enum EntraMachineTokenRefusal
{
    None, Unavailable, Format, Signature, Header, Duplicate, Issuer, Audience, Tenant, Version, Delegated, TokenType,
    ObjectId, Subject, Application, Lifetime
}

/// <summary>One organizational tenant and one exact audience: the client ID of the endpoint's own app registration.</summary>
public sealed class EntraMachineTokenOptions
{
    private static readonly Guid ConsumerTenant = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public EntraMachineTokenOptions(Guid tenantId, Guid audience)
    {
        if (tenantId == Guid.Empty || tenantId == ConsumerTenant || audience == Guid.Empty)
            throw new ArgumentException("A fixed organizational tenant and audience are required.");
        TenantId = tenantId; Audience = audience;
    }

    public Guid TenantId { get; }
    public Guid Audience { get; }
    public override string ToString() => nameof(EntraMachineTokenOptions);
}

/// <summary>
/// Validates Microsoft Entra v2 app-only access tokens from client-credentials applications and managed
/// identities. Discovery, signing keys, issuer, tenant and audience are fixed by configuration; the
/// caller is the token's <c>oid</c>. There is no issuer, key or transport override.
/// </summary>
public sealed class EntraMachineTokenValidator : IMachineTokenValidator, IAsyncDisposable
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

    public EntraMachineTokenValidator(EntraMachineTokenOptions options) : this(options, null) { }

    internal EntraMachineTokenValidator(EntraMachineTokenOptions options, EntraHttpsFixtureTransport? fixture)
    {
        ArgumentNullException.ThrowIfNull(options);
        policy = new(options.TenantId, options.Audience);
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

    public async Task<MachineCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        var (caller, refusal) = await CheckAsync(bearerToken, cancellationToken);
        return caller ?? throw new MachineTokenException(refusal == EntraMachineTokenRefusal.Unavailable
            ? MachineTokenFailure.Unavailable : MachineTokenFailure.Denied);
    }

    /// <summary>The caller, or the closed rule that refused the token. Only Unavailable is not a token finding.</summary>
    internal async Task<(MachineCaller? Caller, EntraMachineTokenRefusal Refusal)> CheckAsync(string bearerToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Compact JWS only: three base64url segments within the protocol bound.
        if (bearerToken is not { Length: > 0 and <= EntraEndpointPolicy.MaximumProtocolBytes } ||
            bearerToken.Any(c => c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.')) ||
            bearerToken.Count(c => c == '.') != 2)
            return (null, EntraMachineTokenRefusal.Format);
        IDisposable lease;
        try { lease = validations.Enter(); }
        catch (ServerSessionIdentityException) { return (null, EntraMachineTokenRefusal.Unavailable); }
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
                return (null, EntraMachineTokenRefusal.Unavailable);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (signal.Kind is ServerSessionIdentityFailureKind.Unavailable or ServerSessionIdentityFailureKind.ConfigurationFailure)
                return (null, EntraMachineTokenRefusal.Unavailable);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
                return (null, Standard(result.Exception));
            try { return (Caller(token), EntraMachineTokenRefusal.None); }
            catch (Refusal refusal) { return (null, refusal.Rule); }
            catch (Exception error) when (EntraErrors.Recoverable(error)) { return (null, EntraMachineTokenRefusal.Format); }
        }
    }

    private static EntraMachineTokenRefusal Standard(Exception? error) => error switch
    {
        SecurityTokenInvalidIssuerException => EntraMachineTokenRefusal.Issuer,
        SecurityTokenInvalidAudienceException => EntraMachineTokenRefusal.Audience,
        SecurityTokenExpiredException or SecurityTokenNotYetValidException or SecurityTokenNoExpirationException
            or SecurityTokenInvalidLifetimeException => EntraMachineTokenRefusal.Lifetime,
        SecurityTokenMalformedException => EntraMachineTokenRefusal.Format,
        _ => EntraMachineTokenRefusal.Signature
    };

    // Inspects only the already validated token, and supplements standard validation with the app-only profile.
    private MachineCaller Caller(JsonWebToken token)
    {
        using (var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedHeader), new JsonDocumentOptions { MaxDepth = 8 }))
        {
            Unique(header.RootElement);
            if (Text(header.RootElement, "alg", EntraMachineTokenRefusal.Header) != SecurityAlgorithms.RsaSha256 ||
                header.RootElement.TryGetProperty("typ", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "JWT") ||
                header.RootElement.TryGetProperty("crit", out _))
                throw new Refusal(EntraMachineTokenRefusal.Header);
        }
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload), new JsonDocumentOptions { MaxDepth = 16 });
        var root = payload.RootElement;
        Unique(root);
        if (Text(root, "iss", EntraMachineTokenRefusal.Issuer) != policy.Issuer) throw new Refusal(EntraMachineTokenRefusal.Issuer);
        if (Text(root, "aud", EntraMachineTokenRefusal.Audience) != policy.Client) throw new Refusal(EntraMachineTokenRefusal.Audience);
        if (Text(root, "tid", EntraMachineTokenRefusal.Tenant) != policy.Tenant) throw new Refusal(EntraMachineTokenRefusal.Tenant);
        if (Text(root, "ver", EntraMachineTokenRefusal.Version) != "2.0") throw new Refusal(EntraMachineTokenRefusal.Version);
        // Delegated (user) tokens carry scopes; an app-only token has none, and says so where idtyp is configured.
        if (root.TryGetProperty("scp", out _)) throw new Refusal(EntraMachineTokenRefusal.Delegated);
        if (root.TryGetProperty("idtyp", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "app"))
            throw new Refusal(EntraMachineTokenRefusal.TokenType);
        var objectId = Id(root, "oid", EntraMachineTokenRefusal.ObjectId);
        if (Text(root, "sub", EntraMachineTokenRefusal.Subject) != objectId.ToString("D")) throw new Refusal(EntraMachineTokenRefusal.Subject);
        var application = Id(root, "azp", EntraMachineTokenRefusal.Application);
        if (root.TryGetProperty("appid", out var appid) && (appid.ValueKind != JsonValueKind.String || appid.GetString() != application.ToString("D")))
            throw new Refusal(EntraMachineTokenRefusal.Application);
        var expires = NumericDate(root, "exp");
        var notBefore = NumericDate(root, "nbf");
        var issued = NumericDate(root, "iat");
        var now = DateTimeOffset.UtcNow;
        if (expires <= now || notBefore > now || issued > now || notBefore >= expires || issued >= expires)
            throw new Refusal(EntraMachineTokenRefusal.Lifetime);
        return new(new(MachinePrincipal.Entra, Guid.Parse(policy.Tenant), objectId), application, expires);
    }

    private static void Unique(JsonElement element)
    {
        try { EntraEndpointPolicy.RequireUniqueObject(element); }
        catch (ServerSessionIdentityException) { throw new Refusal(EntraMachineTokenRefusal.Duplicate); }
    }

    private static string Text(JsonElement element, string name, EntraMachineTokenRefusal rule) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : throw new Refusal(rule);

    private static Guid Id(JsonElement element, string name, EntraMachineTokenRefusal rule)
    {
        var text = Text(element, name, rule);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty && text == id.ToString("D") ? id : throw new Refusal(rule);
    }

    private static DateTimeOffset NumericDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) ||
            seconds is < 1 or > 253402300799)
            throw new Refusal(EntraMachineTokenRefusal.Lifetime);
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private sealed class Refusal(EntraMachineTokenRefusal rule) : Exception
    {
        internal EntraMachineTokenRefusal Rule { get; } = rule;
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

    public override string ToString() => nameof(EntraMachineTokenValidator);
}
