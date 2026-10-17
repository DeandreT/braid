using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Identity;

public sealed partial class EntraBrowserClient
{
    internal void ConfigureNamedOptions(OpenIdConnectOptions value)
    {
        value.Authority = policy.Issuer;
        value.MetadataAddress = policy.Discovery;
        value.ClientId = policy.Client;
        value.ClientSecret = null;
        value.CallbackPath = CallbackPath;
        value.AccessDeniedPath = PathString.Empty;
        value.RemoteSignOutPath = PathString.Empty;
        value.SignedOutCallbackPath = PathString.Empty;
        value.SignedOutRedirectUri = "/";
        value.SignInScheme = Sink;
        value.SignOutScheme = Sink;
        value.Scope.Clear();
        value.Scope.Add("openid");
        value.ClaimActions.Clear();
        value.AdditionalAuthorizationParameters.Clear();
        value.Prompt = "select_account";
        value.ResponseType = OpenIdConnectResponseType.Code;
        value.ResponseMode = OpenIdConnectResponseMode.Query;
        value.UsePkce = true;
        value.MapInboundClaims = false;
        value.GetClaimsFromUserInfoEndpoint = false;
        value.SaveTokens = false;
        value.UseTokenLifetime = false;
        value.DisableTelemetry = true;
        value.RequireHttpsMetadata = true;
        value.UseSecurityTokenValidator = false;
        value.SkipUnrecognizedRequests = false;
        value.RefreshOnIssuerKeyNotFound = true;
        value.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        value.RemoteAuthenticationTimeout = FlowLifetime;
        value.Backchannel = backchannel.Client;
        value.BackchannelHttpHandler = null;
        value.Configuration = null;
        value.ConfigurationManager = manager;
        value.DataProtectionProvider = flowProtection;
        value.StateDataFormat = null!;
        value.StringDataFormat = null!;
        value.ProtocolValidator = protocol;
        value.TokenHandler = tokenHandler;
        value.TokenValidationParameters = tokenHandler.CreateParameters();
        value.Events = events;
        value.EventsType = null;
        value.TimeProvider = TimeProvider.System;
        value.ForwardDefault = null;
        value.ForwardDefaultSelector = null;
        value.ForwardAuthenticate = null;
        value.ForwardChallenge = null;
        value.ForwardForbid = null;
        value.ForwardSignIn = null;
        value.ForwardSignOut = null;
        ConfigureCookie(value.NonceCookie, NoncePrefix);
        ConfigureCookie(value.CorrelationCookie, CorrelationPrefix);
    }

    private static void ConfigureCookie(CookieBuilder value, string prefix)
    {
        value.Name = prefix;
        value.Domain = null;
        value.Path = "/";
        value.HttpOnly = true;
        value.SecurePolicy = CookieSecurePolicy.Always;
        value.SameSite = SameSiteMode.None;
        value.IsEssential = true;
        value.Expiration = FlowLifetime;
        // Use Expires only: standard cookie deletion must not retain a positive Max-Age.
        value.MaxAge = null;
    }

    internal void PostConfigure(string? name, OpenIdConnectOptions value)
    {
        if (name != Scheme) return;
        if (value.StateDataFormat is not null || value.StringDataFormat is not null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        new OpenIdConnectPostConfigureOptions(flowProtection).PostConfigure(name, value);
        RememberFormats(value);
        ValidateOptions(value);
    }

    internal void ValidateOptions(OpenIdConnectOptions value)
    {
        if (value.Authority != policy.Issuer || value.MetadataAddress != policy.Discovery ||
            value.ClientId != policy.Client || value.ClientSecret is not null || value.CallbackPath != CallbackPath || value.AccessDeniedPath.HasValue ||
            value.RemoteSignOutPath.HasValue || value.SignedOutCallbackPath.HasValue || value.SignedOutRedirectUri != "/" ||
            value.SignInScheme != Sink || value.SignOutScheme != Sink || value.Scope.Count != 1 ||
            !value.Scope.Contains("openid") || value.ClaimActions.Count() != 0 ||
            value.AdditionalAuthorizationParameters.Count != 0 || value.Prompt != "select_account" ||
            value.ResponseType != OpenIdConnectResponseType.Code || value.ResponseMode != OpenIdConnectResponseMode.Query ||
            !value.UsePkce || value.MapInboundClaims || value.GetClaimsFromUserInfoEndpoint || value.SaveTokens ||
            value.UseTokenLifetime || !value.DisableTelemetry || !value.RequireHttpsMetadata ||
            value.UseSecurityTokenValidator || value.SkipUnrecognizedRequests || !value.RefreshOnIssuerKeyNotFound ||
            value.PushedAuthorizationBehavior != PushedAuthorizationBehavior.Disable ||
            value.RemoteAuthenticationTimeout != FlowLifetime || !ReferenceEquals(value.Backchannel, backchannel.Client) ||
            backchannel.Client.BaseAddress is not null || backchannel.Client.DefaultRequestHeaders.Any() ||
            backchannel.Client.Timeout != TimeSpan.FromSeconds(30) ||
            backchannel.Client.MaxResponseContentBufferSize != EntraEndpointPolicy.MaximumDocumentBytes ||
            value.BackchannelHttpHandler is not null || value.Configuration is not null ||
            !ReferenceEquals(value.ConfigurationManager, manager) || !ReferenceEquals(value.DataProtectionProvider, flowProtection) ||
            !ReferenceEquals(value.ProtocolValidator, protocol) || !ReferenceEquals(value.TokenHandler, tokenHandler) ||
            !ReferenceEquals(value.Events, events) || value.EventsType is not null ||
            !ReferenceEquals(value.TimeProvider, TimeProvider.System) || value.MaxAge is not null || value.Resource is not null ||
            value.AuthenticationMethod != OpenIdConnectRedirectBehavior.RedirectGet ||
            value.ForwardDefault is not null || value.ForwardDefaultSelector is not null ||
            value.ForwardAuthenticate is not null || value.ForwardChallenge is not null || value.ForwardForbid is not null ||
            value.ForwardSignIn is not null || value.ForwardSignOut is not null ||
            protocol.NonceLifetime != FlowLifetime || !protocol.RequireNonce || protocol.RequireStateValidation ||
            !protocol.RequireState || !protocol.RequireSub || !protocol.RequireTimeStampInNonce ||
            protocol.RequireAcr || protocol.RequireAmr || protocol.RequireAuthTime || protocol.RequireAzp ||
            protocol.HashAlgorithmMap.Count != protocolHashes.Count ||
            protocolHashes.Any(pair => !protocol.HashAlgorithmMap.TryGetValue(pair.Key, out var actual) || actual != pair.Value) ||
            tokenHandler.MapInboundClaims || tokenHandler.MaximumTokenSizeInBytes != EntraEndpointPolicy.MaximumProtocolBytes ||
            manager.MetadataAddress != policy.Discovery || manager.ConfigurationEventHandler is not null ||
            !manager.UseLastKnownGoodConfiguration ||
            manager.RefreshInterval != BaseConfigurationManager.DefaultRefreshInterval ||
            manager.AutomaticRefreshInterval != BaseConfigurationManager.DefaultAutomaticRefreshInterval ||
            manager.LastKnownGoodLifetime != BaseConfigurationManager.DefaultLastKnownGoodConfigurationLifetime ||
            !formats.TryGetValue(value, out var expected) ||
            !ReferenceEquals(value.StateDataFormat, expected.State) || !ReferenceEquals(value.StringDataFormat, expected.Nonce))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        RequireCookie(value.NonceCookie, NoncePrefix);
        RequireCookie(value.CorrelationCookie, CorrelationPrefix);
        RequireParameters(value.TokenValidationParameters);
        RequireEvents();
        if (manager.LastKnownGoodConfiguration is { } known)
        {
            if (known is not OpenIdConnectConfiguration configuration)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            metadata.RequireAdmitted(configuration);
        }
    }

    private static void RequireCookie(CookieBuilder value, string prefix)
    {
        if (value.Name != prefix || value.Domain is not null || value.Path != "/" || !value.HttpOnly ||
            value.SecurePolicy != CookieSecurePolicy.Always || value.SameSite != SameSiteMode.None ||
            !value.IsEssential || value.Expiration != FlowLifetime || value.MaxAge is not null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    private void RequireParameters(TokenValidationParameters value)
    {
        if (value is null || !value.RequireSignedTokens || !value.RequireExpirationTime || !value.RequireAudience ||
            !value.ValidateIssuer || !value.ValidateAudience || !value.ValidateIssuerSigningKey || !value.ValidateLifetime ||
            value.ClockSkew != TimeSpan.Zero || value.IgnoreTrailingSlashWhenValidatingAudience ||
            value.ValidIssuer != policy.Issuer || value.ValidAudience != policy.Client ||
            value.ValidIssuers is null || !value.ValidIssuers.SequenceEqual([policy.Issuer], StringComparer.Ordinal) ||
            value.ValidAudiences is null || !value.ValidAudiences.SequenceEqual([policy.Client], StringComparer.Ordinal) ||
            value.ValidAlgorithms is null || !value.ValidAlgorithms.SequenceEqual([SecurityAlgorithms.RsaSha256], StringComparer.Ordinal) ||
            !ReferenceEquals(value.ConfigurationManager, manager) || value.SaveSigninToken ||
            value.IncludeTokenOnFailedValidation || value.LogTokenId || value.LogValidationExceptions ||
            value.AlgorithmValidator != tokenHandler.KeyAlgorithmValidator || value.AudienceValidator is not null ||
            value.IssuerValidator is not null || value.IssuerValidatorUsingConfiguration is not null ||
            value.IssuerSigningKeyValidator is not null ||
            value.IssuerSigningKeyValidatorUsingConfiguration != tokenHandler.ConfigurationKeyValidator ||
            value.IssuerSigningKeyResolver is not null || value.IssuerSigningKeyResolverUsingConfiguration is not null ||
            value.LifetimeValidator is not null || value.SignatureValidator is not null ||
            value.SignatureValidatorUsingConfiguration is not null || value.TransformBeforeSignatureValidation is not null ||
            value.TokenReader is not null || value.TokenDecryptionKey is not null || value.TokenDecryptionKeys is not null ||
            value.TokenDecryptionKeyResolver is not null || value.IssuerSigningKey is not null ||
            value.IssuerSigningKeys is not null || value.CryptoProviderFactory is not null ||
            value.ActorValidationParameters is not null || value.NameClaimTypeRetriever is not null ||
            value.RoleClaimTypeRetriever is not null || value.TokenReplayValidator is not null ||
            value.TokenReplayCache is not null || value.TypeValidator is not null || value.TryReadJwtClaim is not null ||
            value.NameClaimType != "sub" || value.RoleClaimType != ".weft.no-role")
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }

    private static void RequireAuthenticationRegistration(AuthenticationOptions value)
    {
        if (value.DefaultScheme is not null || value.DefaultAuthenticateScheme is not null ||
            value.DefaultChallengeScheme is not null || value.DefaultForbidScheme is not null ||
            value.DefaultSignInScheme is not null || value.DefaultSignOutScheme is not null ||
            value.Schemes.SingleOrDefault(scheme => scheme.Name == Scheme)?.HandlerType != typeof(PrivateEntraHandler) ||
            value.Schemes.SingleOrDefault(scheme => scheme.Name == Sink)?.HandlerType != typeof(EntraRefusingSignInHandler))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
    }
}

internal sealed class EntraNamedPostConfigure : IPostConfigureOptions<OpenIdConnectOptions>
{
    private readonly EntraBrowserClient client;
    public EntraNamedPostConfigure(EntraBrowserClient client) => this.client = client;
    public void PostConfigure(string? name, OpenIdConnectOptions options) => client.PostConfigure(name, options);
}
