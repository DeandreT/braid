using System.Text.Json;
using Weft.Application;

namespace Weft.Identity;

/// <summary>
/// One organizational tenant, the exact audience (the client ID of the app registration that exposes the scope,
/// which Entra v2 access tokens always carry as <c>aud</c>), the scope name that must appear in <c>scp</c>, and
/// the allowlisted client applications (<c>azp</c>) that may present delegated tokens.
/// </summary>
public sealed class EntraUserTokenOptions
{
    public const int MaximumClients = 16;
    public const int MaximumScopeLength = 120;
    private static readonly Guid ConsumerTenant = new("9188040d-6c67-4c5b-b112-36a304b66dad");

    public EntraUserTokenOptions(Guid tenantId, Guid audience, string scope, IEnumerable<Guid> clientIds)
    {
        ArgumentNullException.ThrowIfNull(clientIds);
        var clients = clientIds.ToArray();
        if (tenantId == Guid.Empty || tenantId == ConsumerTenant || audience == Guid.Empty || !IsScope(scope) ||
            clients.Length is < 1 or > MaximumClients || clients.Distinct().Count() != clients.Length ||
            clients.Any(client => client == Guid.Empty || client == audience))
            throw new ArgumentException("A fixed organizational tenant, audience, scope and distinct client applications are required.");
        TenantId = tenantId; Audience = audience; Scope = scope; ClientIds = Array.AsReadOnly(clients);
    }

    public Guid TenantId { get; }
    public Guid Audience { get; }
    public string Scope { get; }
    public IReadOnlyList<Guid> ClientIds { get; }

    /// <summary>A scope name as Entra writes it in <c>scp</c>: no space or URI prefix.</summary>
    public static bool IsScope(string? value) => value is { Length: > 0 and <= MaximumScopeLength } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');

    public override string ToString() => nameof(EntraUserTokenOptions);
}

/// <summary>
/// Validates Microsoft Entra v2 delegated access tokens that a signed-in user's allowlisted client application
/// obtained for the configured scope. It shares the machine validator's fixed-tenant discovery, key refresh and
/// strict JWT handling. The caller is the tenant-stable <c>(tid, oid)</c> key; roles, groups and display claims
/// grant nothing. App-only tokens are refused.
/// </summary>
public sealed class EntraUserTokenValidator : IUserTokenValidator, IAsyncDisposable
{
    private readonly EntraAccessTokens tokens;
    private readonly string scope;
    private readonly HashSet<Guid> clients;

    public EntraUserTokenValidator(EntraUserTokenOptions options) : this(options, null) { }

    internal EntraUserTokenValidator(EntraUserTokenOptions options, EntraHttpsFixtureTransport? fixture)
    {
        ArgumentNullException.ThrowIfNull(options);
        scope = options.Scope;
        clients = [.. options.ClientIds];
        tokens = new(options.TenantId, options.Audience, fixture);
    }

    public async Task<UserCaller> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        var (caller, refusal) = await CheckAsync(bearerToken, cancellationToken);
        return caller ?? throw new UserTokenException(refusal == EntraTokenRefusal.Unavailable
            ? UserTokenFailure.Unavailable : UserTokenFailure.Denied);
    }

    /// <summary>The caller, or the closed rule that refused the token. Only Unavailable is not a token finding.</summary>
    internal Task<(UserCaller? Caller, EntraTokenRefusal Refusal)> CheckAsync(string bearerToken, CancellationToken cancellationToken) =>
        tokens.CheckAsync(bearerToken, Caller, cancellationToken);

    // The delegated profile on the validated, duplicate-free payload.
    private UserCaller Caller(JsonElement root, DateTimeOffset expires)
    {
        // An app-only token says so where idtyp is configured, and never carries scp.
        if (root.TryGetProperty("idtyp", out var kind) && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "user"))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.TokenType);
        var scopes = EntraAccessTokens.Text(root, "scp", EntraTokenRefusal.Scope).Split(' ');
        if (scopes.Any(item => item.Length == 0) || !scopes.Contains(scope, StringComparer.Ordinal))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Scope);
        var objectId = EntraAccessTokens.Id(root, "oid", EntraTokenRefusal.ObjectId);
        _ = EntraAccessTokens.Text(root, "sub", EntraTokenRefusal.Subject);
        var application = EntraAccessTokens.Id(root, "azp", EntraTokenRefusal.Application);
        if (root.TryGetProperty("appid", out var appid) && (appid.ValueKind != JsonValueKind.String || appid.GetString() != application.ToString("D")))
            throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Application);
        if (!clients.Contains(application)) throw new EntraAccessTokens.Refusal(EntraTokenRefusal.Client);
        return new(EntraUserIdentity.Key(tokens.Tenant, objectId), application, expires);
    }

    public ValueTask DisposeAsync() => tokens.DisposeAsync();

    public override string ToString() => nameof(EntraUserTokenValidator);
}
