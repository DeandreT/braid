using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Identity;

internal sealed class EntraMetadata : IConfigurationRetriever<OpenIdConnectConfiguration>,
    IConfigurationValidator<OpenIdConnectConfiguration>
{
    private readonly EntraEndpointPolicy policy;
    private readonly EntraOwnedWork retrievals = new();
    private readonly CancellationToken lifetime;
    private readonly ConditionalWeakTable<OpenIdConnectConfiguration, Shape> admitted = new();
    private readonly ConditionalWeakTable<SecurityKey, OpenIdConnectConfiguration> keyConfigurations = new();

    internal EntraMetadata(EntraEndpointPolicy policy, CancellationToken lifetime)
    { this.policy = policy; this.lifetime = lifetime; }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address,
        IDocumentRetriever retriever, CancellationToken cancel)
    {
        using var lease = retrievals.Enter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancel, lifetime);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (address != policy.Discovery)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            var configuration = await OpenIdConnectConfigurationRetriever.GetAsync(address, retriever, cancellation.Token);
            var keys = configuration.JsonWebKeySet;
            if (keys is null || keys.Keys.Count is < 1 or > 128)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            // Scope the unpublished object before manager validation/LKG admission.
            foreach (var key in keys.Keys.ToArray())
                if (!Eligible(key)) keys.Keys.Remove(key);
            if (keys.Keys.Select(key => key.Kid).Distinct(StringComparer.Ordinal).Count() != keys.Keys.Count)
                throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
            keys.SkipUnresolvedJsonWebKeys = true;
            configuration.SigningKeys.Clear();
            foreach (var key in keys.GetSigningKeys())
            {
                key.CryptoProviderFactory = new CryptoProviderFactory
                {
                    CacheSignatureProviders = false,
                    SignatureProviderObjectPoolCacheSize = 1
                };
                configuration.SigningKeys.Add(key);
            }
            policy.RequireConfiguration(configuration);
            admitted.Add(configuration, new(keys.Keys.ToArray(), configuration.SigningKeys.ToArray(),
                configuration.SigningKeys.Select(key => key.ComputeJwkThumbprint()).ToArray(),
                configuration.SigningKeys.Select(key => key.CryptoProviderFactory).ToArray()));
            foreach (var key in configuration.SigningKeys) keyConfigurations.Add(key, configuration);
            return configuration;
        }
        catch (ServerSessionIdentityException error) { EntraFailureScope.Report(error.Kind); throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        {
            EntraFailureScope.Report(ServerSessionIdentityFailureKind.Unavailable);
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.Unavailable);
        }
    }

    public ConfigurationValidationResult Validate(OpenIdConnectConfiguration configuration)
    {
        try
        {
            RequireAdmitted(configuration);
            return new ConfigurationValidationResult { Succeeded = true };
        }
        catch (Exception error) when (EntraErrors.Recoverable(error))
        { return new ConfigurationValidationResult { Succeeded = false, ErrorMessage = "Entra configuration refused." }; }
    }

    internal void RequireAdmitted(OpenIdConnectConfiguration configuration)
    {
        // Reject replacement objects by identity before invoking any key members.
        if (!admitted.TryGetValue(configuration, out var shape) || configuration.JsonWebKeySet is null ||
            configuration.JsonWebKeySet.Keys.Count == 0 ||
            configuration.JsonWebKeySet.Keys.Count != shape.Keys.Length ||
            configuration.SigningKeys.Count != shape.SigningKeys.Length ||
            configuration.JsonWebKeySet.Keys.Where((key, index) => !ReferenceEquals(key, shape.Keys[index])).Any() ||
            configuration.SigningKeys.Where((key, index) => !ReferenceEquals(key, shape.SigningKeys[index])).Any() ||
            configuration.JsonWebKeySet.Keys.Any(key => !Eligible(key)) ||
            configuration.SigningKeys.Where((key, index) =>
                !ReferenceEquals(key.CryptoProviderFactory, shape.Factories[index]) ||
                key.CryptoProviderFactory.CustomCryptoProvider is not null ||
                key.CryptoProviderFactory.CacheSignatureProviders ||
                key.CryptoProviderFactory.SignatureProviderObjectPoolCacheSize != 1 ||
                !key.ComputeJwkThumbprint().AsSpan().SequenceEqual(shape.Thumbprints[index])).Any())
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        policy.RequireConfiguration(configuration);
    }

    internal void RequireAdmittedKey(SecurityKey key)
    {
        if (!keyConfigurations.TryGetValue(key, out var configuration))
            throw EntraErrors.Create(ServerSessionIdentityFailureKind.ConfigurationFailure);
        RequireAdmitted(configuration);
    }

    private bool Eligible(JsonWebKey key)
    {
        if (!key.AdditionalData.TryGetValue("issuer", out var value)) return false;
        var issuer = value is string text ? text : value is JsonElement { ValueKind: JsonValueKind.String } json
            ? json.GetString() : null;
        return (issuer == policy.Issuer || issuer == EntraEndpointPolicy.TemplateIssuer) &&
            key.Kty == "RSA" && (string.IsNullOrEmpty(key.Use) || key.Use == "sig") &&
            (string.IsNullOrEmpty(key.Alg) || key.Alg == SecurityAlgorithms.RsaSha256) &&
            !string.IsNullOrEmpty(key.Kid) && !key.HasPrivateKey;
    }

    internal Task CloseAndDrainAsync() => retrievals.CloseAndDrain();
    private sealed record Shape(JsonWebKey[] Keys, SecurityKey[] SigningKeys, byte[][] Thumbprints,
        CryptoProviderFactory[] Factories);
}
