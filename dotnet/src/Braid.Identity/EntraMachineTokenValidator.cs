using System.Text.Json;
using Braid.Application;

namespace Braid.Identity;

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
    private readonly EntraAccessTokens tokens;

    public EntraMachineTokenValidator(EntraMachineTokenOptions options) : this(options, null) { }

    internal EntraMachineTokenValidator(EntraMachineTokenOptions options, EntraHttpsFixtureTransport? fixture)
    {
        ArgumentNullException.ThrowIfNull(options);
        tokens = new(options.TenantId, options.Audience, fixture);
    }

    public async Task<MachineCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        var (caller, refusal) = await CheckAsync(bearerToken, cancellationToken);
        return caller ?? throw new MachineTokenException(refusal == EntraTokenRefusal.Unavailable
            ? MachineTokenFailure.Unavailable : MachineTokenFailure.Denied);
    }

    /// <summary>The caller, or the closed rule that refused the token. Only Unavailable is not a token finding.</summary>
    internal Task<(MachineCaller? Caller, EntraTokenRefusal Refusal)> CheckAsync(string bearerToken, CancellationToken cancellationToken) =>
        tokens.CheckAsync(bearerToken, Caller, cancellationToken);

    // The app-only profile on the validated, duplicate-free payload.
    private MachineCaller Caller(JsonElement root, DateTimeOffset expires)
    {
        // Delegated (user) tokens carry scopes; an app-only token has none, and says so where idtyp is configured.
        if (root.TryGetProperty("scp", out _)) throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Delegated);
        if (root.TryGetProperty("idtyp", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "app"))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.TokenType);
        var objectId = EntraAccessTokens.Id(root, "oid", EntraTokenRefusal.ObjectId);
        if (EntraAccessTokens.Text(root, "sub", EntraTokenRefusal.Subject) != objectId.ToString("D"))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Subject);
        var application = EntraAccessTokens.Id(root, "azp", EntraTokenRefusal.Application);
        if (root.TryGetProperty("appid", out var appid) && (appid.ValueKind != JsonValueKind.String || appid.GetString() != application.ToString("D")))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Application);
        return new(new(MachinePrincipal.Entra, tokens.Tenant, objectId), application, expires);
    }

    public ValueTask DisposeAsync() => tokens.DisposeAsync();

    public override string ToString() => nameof(EntraMachineTokenValidator);
}
