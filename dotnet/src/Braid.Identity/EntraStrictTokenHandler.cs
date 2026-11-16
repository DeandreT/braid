using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Braid.Identity;

internal sealed class EntraStrictTokenHandler : JsonWebTokenHandler
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> manager;
    private readonly EntraMetadata metadata;
    private readonly TokenValidationParameters baseline;
    private readonly Func<CancellationToken> requestCancellation;
    internal IssuerSigningKeyValidatorUsingConfiguration ConfigurationKeyValidator { get; }
    internal AlgorithmValidator KeyAlgorithmValidator { get; }

    internal EntraStrictTokenHandler(EntraEndpointPolicy policy,
        ConfigurationManager<OpenIdConnectConfiguration> manager, EntraMetadata metadata,
        Func<CancellationToken> requestCancellation)
    {
        this.manager = manager;
        this.metadata = metadata;
        this.requestCancellation = requestCancellation;
        ConfigurationKeyValidator = ValidateConfigurationKey;
        KeyAlgorithmValidator = ValidateKeyBeforeVerification;
        MapInboundClaims = false;
        MaximumTokenSizeInBytes = EntraEndpointPolicy.MaximumProtocolBytes;
        baseline = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            RequireAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidIssuer = policy.Issuer,
            ValidIssuers = [policy.Issuer],
            ValidAudience = policy.Client,
            ValidAudiences = [policy.Client],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ClockSkew = TimeSpan.Zero,
            ConfigurationManager = manager,
            IssuerSigningKeyValidatorUsingConfiguration = ConfigurationKeyValidator,
            AlgorithmValidator = KeyAlgorithmValidator,
            SaveSigninToken = false,
            IncludeTokenOnFailedValidation = false,
            LogTokenId = false,
            LogValidationExceptions = false,
            NameClaimType = "sub",
            RoleClaimType = ".braid.no-role"
        };
    }

    internal TokenValidationParameters CreateParameters() => baseline.Clone();

    private bool ValidateKeyBeforeVerification(string algorithm, SecurityKey key,
        SecurityToken token, TokenValidationParameters parameters)
    {
        // The standard algorithm hook precedes verifier construction. Historical
        // LKG keys need the same material/factory checks as the current config.
        try { metadata.RequireAdmittedKey(key); }
        catch (ServerSessionIdentityException error)
        {
            EntraFailureScope.Report(error.Kind);
            throw;
        }
        return algorithm == SecurityAlgorithms.RsaSha256;
    }

    private bool ValidateConfigurationKey(SecurityKey key, SecurityToken token,
        TokenValidationParameters parameters, BaseConfiguration configuration)
    {
        // The standard manager keeps a private LKG cache, not just its public latest
        // property. Check the actual configuration selected for every validation.
        try
        {
            if (configuration is not OpenIdConnectConfiguration oidc)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            metadata.RequireAdmitted(oidc);
        }
        catch (ServerSessionIdentityException error)
        {
            EntraFailureScope.Report(error.Kind);
            throw;
        }
        var standard = baseline.Clone();
        standard.IssuerSigningKeyValidatorUsingConfiguration = null;
        Validators.ValidateIssuerSecurityKey(key, token, standard);
        return true;
    }

    public override async Task<TokenValidationResult> ValidateTokenAsync(string token,
        TokenValidationParameters validationParameters)
    {
        // Never clone incoming stock code-flow parameters: they intentionally turn signed
        // tokens off, and may contain ambient delegates/material. Only this private baseline.
        if (!ReferenceEquals(validationParameters.ConfigurationManager, manager) ||
            manager.ConfigurationEventHandler is not null)
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        metadata.RequireAdmitted(await manager.GetConfigurationAsync(requestCancellation()));
        if (manager.LastKnownGoodConfiguration is { } lkg)
        {
            if (lkg is not OpenIdConnectConfiguration oidc)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            metadata.RequireAdmitted(oidc);
        }
        var parameters = baseline.Clone();
        parameters.RequireSignedTokens = true;
        // Never select signature behavior from a mutable key or the named options
        // bag. This per-validation standard factory is not exposed to either.
        parameters.CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        return await base.ValidateTokenAsync(token, parameters);
    }
}
