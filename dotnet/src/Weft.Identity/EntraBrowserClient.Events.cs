using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Weft.Application;

namespace Weft.Identity;

public sealed partial class EntraBrowserClient
{
    private OpenIdConnectEvents CreateEvents() => new()
    {
        OnMessageReceived = MessageReceivedAsync,
        OnRedirectToIdentityProvider = RedirectAsync,
        OnAuthorizationCodeReceived = CodeReceivedAsync,
        OnTokenValidated = TokenValidatedAsync,
        OnTicketReceived = TicketReceivedAsync,
        OnAuthenticationFailed = AuthenticationFailedAsync,
        OnRemoteFailure = RemoteFailureAsync,
        OnAccessDenied = AccessDeniedAsync,
        OnTokenResponseReceived = TokenResponseAsync,
        OnRedirectToIdentityProviderForSignOut = RefuseSignOutRedirect,
        OnRemoteSignOut = RefuseRemoteSignOut,
        OnSignedOutCallbackRedirect = RefuseRemoteSignOut,
        OnUserInformationReceived = RefuseUserInfo,
        OnPushAuthorization = RefusePush
    };

    private void RequireEvents()
    {
        if (events.OnMessageReceived != (Func<MessageReceivedContext, Task>)MessageReceivedAsync ||
            events.OnRedirectToIdentityProvider != (Func<RedirectContext, Task>)RedirectAsync ||
            events.OnAuthorizationCodeReceived != (Func<AuthorizationCodeReceivedContext, Task>)CodeReceivedAsync ||
            events.OnTokenValidated != (Func<TokenValidatedContext, Task>)TokenValidatedAsync ||
            events.OnTicketReceived != (Func<TicketReceivedContext, Task>)TicketReceivedAsync ||
            events.OnAuthenticationFailed != (Func<AuthenticationFailedContext, Task>)AuthenticationFailedAsync ||
            events.OnRemoteFailure != (Func<RemoteFailureContext, Task>)RemoteFailureAsync ||
            events.OnAccessDenied != (Func<AccessDeniedContext, Task>)AccessDeniedAsync ||
            events.OnTokenResponseReceived != (Func<TokenResponseReceivedContext, Task>)TokenResponseAsync ||
            events.OnRedirectToIdentityProviderForSignOut != (Func<RedirectContext, Task>)RefuseSignOutRedirect ||
            events.OnRemoteSignOut != (Func<RemoteSignOutContext, Task>)RefuseRemoteSignOut ||
            events.OnSignedOutCallbackRedirect != (Func<RemoteSignOutContext, Task>)RefuseRemoteSignOut ||
            events.OnUserInformationReceived != (Func<UserInformationReceivedContext, Task>)RefuseUserInfo ||
            events.OnPushAuthorization != (Func<PushedAuthorizationContext, Task>)RefusePush)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    private Task TokenResponseAsync(TokenResponseReceivedContext context)
    {
        var operation = RequireOperation();
        if (!operation.Callback || string.IsNullOrEmpty(context.TokenEndpointResponse.IdToken))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        return Task.CompletedTask;
    }

    private static Task RefuseSignOutRedirect(RedirectContext context) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
    private static Task RefuseRemoteSignOut(RemoteSignOutContext context) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
    private static Task RefuseUserInfo(UserInformationReceivedContext context) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    private static Task RefusePush(PushedAuthorizationContext context) =>
        throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);

    private Task RedirectAsync(RedirectContext context)
    {
        var operation = RequireOperation();
        if (operation.Callback || context.ProtocolMessage.IssuerAddress != policy.Authorize ||
            context.ProtocolMessage.RedirectUri != options.PublicOrigin + CallbackPath ||
            context.ProtocolMessage.ClientId != policy.Client || context.ProtocolMessage.Scope != string.Join(' ', Scopes) ||
            context.ProtocolMessage.ResponseType != "code" || context.ProtocolMessage.Prompt != "select_account")
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        if (operation.NonceName is not { } nonce || !IsBoundNonceCookieName(nonce) || context.Properties is null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        // Standard handler generated/appended this exact randomized protected name before
        // RedirectToIdentityProvider; its standard state protection happens afterwards.
        context.Properties.Items[".weft.nonce-cookie"] = nonce;
        RequireFlowProperties(context.Properties);
        return Task.CompletedTask;
    }

    private Task MessageReceivedAsync(MessageReceivedContext context)
    {
        var operation = RequireOperation();
        if (!operation.Callback) throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        RequireFlowProperties(context.Properties);
        // Only authenticated, admitted state can nominate a matching nonce for cleanup.
        operation.NonceName = context.Properties!.Items[".weft.nonce-cookie"];
        if (context.Properties!.Items.TryGetValue(".xsrf", out var correlation) &&
            correlation is { Length: > 0 and <= 256 } && correlation.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            operation.CorrelationName = CorrelationPrefix + correlation;
        else throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        if (context.Request.Query.TryGetValue("iss", out var issuer) && issuer.ToString() != policy.Issuer)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        return Task.CompletedTask;
    }

    private Task CodeReceivedAsync(AuthorizationCodeReceivedContext context)
    {
        var operation = RequireOperation();
        if (!operation.Callback || context.TokenEndpointRequest is null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.UntrustedIssuance);
        try
        {
            RequireCertificate();
            var now = DateTime.UtcNow;
            var assertion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = policy.Client,
                Audience = policy.Token,
                Claims = new Dictionary<string, object> { ["sub"] = policy.Client, ["jti"] = Guid.NewGuid().ToString("D") },
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                SigningCredentials = new X509SigningCredentials(certificate, "PS256"),
                AdditionalHeaderClaims = new Dictionary<string, object>
                { ["x5t#S256"] = Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData)) }
            });
            context.TokenEndpointRequest.Parameters["client_assertion_type"] =
                "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            context.TokenEndpointRequest.Parameters["client_assertion"] = assertion;
            // Never HandleCodeRedemption: standard redemption/nonce/response checks remain.
            return Task.CompletedTask;
        }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        {
            operation.Refuse(ServerSessionIdentityFailureKind.Unavailable);
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    private Task TokenValidatedAsync(TokenValidatedContext context)
    {
        var operation = RequireOperation();
        if (!operation.Callback || operation.Candidate is not null || context.Principal is null ||
            context.SecurityToken is not JwtSecurityToken token)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        RequireFlowProperties(context.Properties);
        // Inspect only the already cryptographically validated pinned handler token. A
        // JSON duplicate guard supplements standard validation; it does not validate JWTs.
        try
        {
            var pieces = token.RawData.Split('.');
            if (pieces.Length != 3 || token.RawData.Length > EntraEndpointPolicy.MaximumProtocolBytes)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(pieces[1]),
                new JsonDocumentOptions { MaxDepth = 16 });
            var root = payload.RootElement;
            EntraEndpointPolicy.RequireUniqueObject(root);
            var issuer = EntraEndpointPolicy.ReadRequired(root, "iss");
            var subject = EntraEndpointPolicy.ReadRequired(root, "sub");
            // The pairwise sub is not the key, but it keeps the opaque subject rules it had as one.
            if (!ExternalIdentityKey.IsValidSubject(subject))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            if (issuer != policy.Issuer || EntraEndpointPolicy.ReadRequired(root, "tid") != policy.Tenant ||
                EntraEndpointPolicy.ReadRequired(root, "aud") != policy.Client ||
                EntraEndpointPolicy.ReadRequired(root, "nonce").Length > 512 ||
                root.TryGetProperty("azp", out var azp) && (azp.ValueKind != JsonValueKind.String || azp.GetString() != policy.Client))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            var deadline = NumericDate(root, "exp");
            var notBefore = NumericDate(root, "nbf");
            var issued = NumericDate(root, "iat");
            var now = DateTimeOffset.UtcNow;
            if (deadline <= now || notBefore > now || issued > now || notBefore >= deadline || issued >= deadline)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            // The binding key is the tenant-stable (tid, oid), never the pairwise sub: the same person then
            // resolves to the same binding through a bearer token issued to another client application.
            var objectId = EntraEndpointPolicy.ReadRequired(root, "oid");
            if (!Guid.TryParseExact(objectId, "D", out var oid) || oid == Guid.Empty || objectId != oid.ToString("D"))
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            var key = EntraUserIdentity.Key(Guid.Parse(policy.Tenant), oid);
            RequirePrincipal(context.Principal, key, subject);
            operation.Candidate = new(key, subject, deadline, context.Principal, context.Properties!);
            // Candidate remains private: framework nonce/ValidateTokenResponse still follow.
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        { throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied); }
    }

    private Task TicketReceivedAsync(TicketReceivedContext context)
    {
        context.HandleResponse(); // Before publishing, including when later checks throw.
        var operation = RequireOperation();
        var candidate = operation.Candidate;
        if (!operation.Callback || candidate is null || operation.Verified is not null ||
            !ReferenceEquals(context.Principal, candidate.Principal) ||
            !ReferenceEquals(context.Properties, candidate.Properties))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        RequireFlowProperties(context.Properties);
        RequirePrincipal(context.Principal!, candidate.Identity, candidate.Subject);
        if (candidate.Deadline <= DateTimeOffset.UtcNow)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        operation.Verified = new(this, operation.Context, operation, organization, candidate.Identity, candidate.Deadline);
        return Task.CompletedTask;
    }

    private Task AuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        context.HandleResponse();
        RequireOperation().Refuse(ClosedFailure(context.Exception));
        return Task.CompletedTask;
    }

    private Task RemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        RequireOperation().Refuse(ClosedFailure(context.Failure));
        return Task.CompletedTask;
    }

    private Task AccessDeniedAsync(AccessDeniedContext context)
    {
        context.HandleResponse();
        RequireOperation().Refuse(ServerSessionIdentityFailureKind.Denied);
        return Task.CompletedTask;
    }

    private static ServerSessionIdentityFailureKind ClosedFailure(Exception? error) => error switch
    {
        ServerSessionIdentityException closed => closed.Kind,
        HttpRequestException or IOException or OperationCanceledException => ServerSessionIdentityFailureKind.Unavailable,
        _ => ServerSessionIdentityFailureKind.Denied
    };

    private static void RequireFlowProperties(AuthenticationProperties? properties)
    {
        var now = DateTimeOffset.UtcNow;
        if (properties is null || properties.RedirectUri is not ("/" or null) || properties.IsPersistent ||
            !properties.Items.TryGetValue(".weft.flow", out var version) || version != "1" ||
            !properties.Items.TryGetValue(".weft.nonce-cookie", out var nonce) ||
            nonce is null || !IsBoundNonceCookieName(nonce) ||
            properties.IssuedUtc is not { } issued || properties.ExpiresUtc is not { } expires ||
            issued > now || expires <= now || expires - issued != FlowLifetime)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
    }

    private static DateTimeOffset NumericDate(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        try
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (instant == DateTimeOffset.MinValue || instant == DateTimeOffset.MaxValue)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
            return instant;
        }
        catch (ArgumentOutOfRangeException) { throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied); }
    }

    private void RequirePrincipal(ClaimsPrincipal principal, ExternalIdentityKey identity, string subject)
    {
        if (identity.Provider != EntraUserIdentity.Provider || identity.Authority != policy.Tenant)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        foreach (var pair in new[] { ("iss", policy.Issuer), ("sub", subject), ("tid", policy.Tenant),
            ("aud", policy.Client), ("oid", identity.Subject) })
        {
            var claims = principal.FindAll(pair.Item1).ToArray();
            if (claims.Length != 1 || claims[0].Value != pair.Item2)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.Denied);
        }
    }

    internal sealed record Candidate(ExternalIdentityKey Identity, string Subject, DateTimeOffset Deadline,
        ClaimsPrincipal Principal, AuthenticationProperties Properties);
}
