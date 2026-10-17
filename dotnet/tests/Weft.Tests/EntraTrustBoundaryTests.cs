using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Weft.Identity;

namespace Weft.Tests;

[SupportedOSPlatform("linux")]
public sealed class EntraTrustBoundaryTests
{
    [Fact]
    public Task ReplacementSigningKeyCannotRunVirtualEqualityOrThumbprintHooksDuringAdmission() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
        var configuration = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var original = configuration.SigningKeys.Single();
        EntraProtocolFixturePolicy.Check(original is RsaSecurityKey,
            "The replacement-key control did not start from the admitted standard RSA key.");
        var replacement = new EqualLookingKey((RsaSecurityKey)original);
        var flow = await test.FlowAsync();
        try
        {
            configuration.SigningKeys.Clear();
            configuration.SigningKeys.Add(replacement);
            var refused = await test.CompleteAsync(flow);
            test.Denied(refused, ServerSessionIdentityFailureKind.ConfigurationFailure);
            EntraProtocolFixturePolicy.Check(replacement.Callbacks == 0,
                "Admission invoked virtual equality or thumbprint behavior on a replacement signing key.");
        }
        finally
        {
            configuration.SigningKeys.Clear();
            configuration.SigningKeys.Add(original);
        }
        test.Success(await test.CompleteAsync(flow));
    });

    [Fact]
    public Task MutablePerKeyCryptoProviderCannotReplaceStandardSignatureVerification() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
        var configuration = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var key = configuration.SigningKeys.Single();
        var original = key.CryptoProviderFactory;
        var malicious = new AcceptingCryptoProvider();
        foreach (bool replace in new[] { true, false })
        {
            var flow = await test.FlowAsync();
            try
            {
                if (replace)
                    key.CryptoProviderFactory = new CryptoProviderFactory
                    {
                        CacheSignatureProviders = false,
                        CustomCryptoProvider = malicious
                    };
                else original.CustomCryptoProvider = malicious;
                test.Issuer.TokenCase = EntraProtocolTokenCase.BadSignature;
                var refused = await test.CompleteAsync(flow);
                test.Denied(refused, ServerSessionIdentityFailureKind.ConfigurationFailure); test.CheckPrivacy(refused);
                EntraProtocolFixturePolicy.Check(malicious.Creations == 0,
                    "Mutable per-key crypto behavior was invoked before its configuration was refused.");
            }
            finally
            {
                key.CryptoProviderFactory = original;
                original.CustomCryptoProvider = null;
            }
            test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
            test.Success(await test.CompleteAsync(flow));
            EntraProtocolFixturePolicy.Check(malicious.Creations == 0,
                "The healthy control relied on a mutable per-key crypto provider.");
        }
    });

    [Fact]
    public Task HistoricalAdmittedKeyCannotInvokeSelfRestoringCryptoProviderBeforeValidation() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
        var initial = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var manager = seed.Request.Policy!.Manager as ConfigurationManager<OpenIdConnectConfiguration>;
        EntraProtocolFixturePolicy.Check(initial.SigningKeys.Count == 1 && initial.SigningKeys.Single().KeyId == "weft-k1"
            && manager is not null && manager.UseLastKnownGoodConfiguration && manager.IsLastKnownGoodValid
            && ReferenceEquals(manager.LastKnownGoodConfiguration, initial),
            "The historical-key regression did not seed real admitted K1 standard LKG validation.");
        var key = initial.SigningKeys.Single();
        var original = key.CryptoProviderFactory;

        var rollover = await test.FlowAsync();
        int reads = test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Keys);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Issuer.NextKeysResponse = refreshed;
        test.Issuer.PublishSecond = true; test.Issuer.OnlySecond = true; test.Issuer.SignSecond = true;
        var refreshing = await test.CompleteAsync(rollover);
        if (refreshing.Request.Verified is null) test.Denied(refreshing); else test.Success(refreshing);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await test.WaitForPublishedSecondKeyAsync(seed.Request);
        test.Success(await test.CompleteAsync(await test.FlowAsync()));

        // A real K1 callback while publication remains K2-only proves the seeded
        // key is retained in standard LKG, rather than an injected unadmitted key.
        test.Issuer.SignSecond = false;
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
        // Standard LKG fallback does not overwrite the latest/current K2 value.
        EntraProtocolFixturePolicy.Check(manager!.LastKnownGoodConfiguration is OpenIdConnectConfiguration known
            && !ReferenceEquals(known, initial) && known.SigningKeys.All(candidate => candidate.KeyId != "weft-k1"),
            "The retained K1 control unexpectedly replaced the latest K2-only configuration.");
        test.Issuer.SignSecond = true;
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
        var current = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var latest = manager.LastKnownGoodConfiguration;
        EntraProtocolFixturePolicy.Check(test.Issuer.Snapshot().Count(value => value.Path == EntraProtocolFixturePolicy.Keys) > reads
            && test.Issuer.PublishSecond && test.Issuer.OnlySecond && manager.IsLastKnownGoodValid
            && !ReferenceEquals(current, initial) && ReferenceEquals(latest, current)
            && current.SigningKeys.Count == 1 && current.SigningKeys.Single().KeyId == "weft-k2"
            && latest.SigningKeys.All(candidate => candidate.KeyId != key.KeyId && !ReferenceEquals(candidate, key))
            && ReferenceEquals(initial.SigningKeys.Single(), key),
            "The historical K1 control remained current or latest instead of hidden behind admitted K2-only configuration.");

        test.Issuer.SignSecond = false;
        var flow = await test.FlowAsync(); int responses = test.Issuer.TokenResponses;
        var malicious = new SelfRestoringCryptoProvider(key, original);
        var replacement = new CryptoProviderFactory { CacheSignatureProviders = false, CustomCryptoProvider = malicious };
        try
        {
            key.CryptoProviderFactory = replacement;
            var refused = await test.CompleteAsync(flow);
            test.Denied(refused, ServerSessionIdentityFailureKind.ConfigurationFailure); test.CheckPrivacy(refused);
            EntraProtocolFixturePolicy.Check(test.Issuer.TokenResponses == responses + 1
                && malicious.Callbacks == 0 && ReferenceEquals(key.CryptoProviderFactory, replacement),
                "Historical signing-key crypto behavior ran before admission or the valid K1 callback never redeemed its code.");
            EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
        }
        finally { key.CryptoProviderFactory = original; }

        test.Issuer.SignSecond = true;
        var healthy = await test.CompleteAsync(await test.FlowAsync()); test.Success(healthy); test.CheckPrivacy(healthy);
        EntraProtocolFixturePolicy.Check(malicious.Callbacks == 0,
            "The healthy K2 control invoked the historical key's custom crypto provider.");
    });

    [Fact]
    public Task ProtocolWorkRestoresOriginalRequestCookieFeatureOnSuccessAndFailure() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        foreach (bool validState in new[] { true, false })
        {
            var flow = await test.FlowAsync();
            await using var scope = test.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(test.Origin.Authority);
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = EntraProtocolFixturePolicy.Callback;
            context.Request.QueryString = validState ? new QueryString(new Uri(test.Origin, flow.Callback).Query)
                : new QueryString("?code=unredeemed&state=invalid");
            context.Request.Headers.Cookie = flow.Browser.Header;
            var cookies = context.Request.Cookies;
            var feature = context.Features.Get<IRequestCookiesFeature>();
            var user = context.User;
            bool succeeded = false, denied = false;
            try
            {
                var result = await test.Facade.VerifyCallbackAsync(context, CancellationToken.None);
                succeeded = result.OrganizationId == EntraProtocolFixturePolicy.Organization
                    && result.ExternalIdentity.Subject == EntraProtocolFixturePolicy.Subject;
            }
            catch (ServerSessionIdentityException error)
            {
                denied = error.Kind == ServerSessionIdentityFailureKind.Denied;
            }
            EntraProtocolFixturePolicy.Check(validState ? succeeded && !denied : denied && !succeeded,
                "The request-feature control did not exercise its intended standard protocol outcome.");
            EntraProtocolFixturePolicy.Check(ReferenceEquals(context.Features.Get<IRequestCookiesFeature>(), feature)
                && ReferenceEquals(context.Request.Cookies, cookies) && ReferenceEquals(context.User, user)
                && test.Ports.Calls == 0,
                "Protocol completion did not restore the original cookie feature, collection or principal.");
        }
    });

    [Fact]
    public Task CachedUnadmittedConfigurationCannotBecomeTrustedAfterLatestLkgIsRestored() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
        var admitted = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var manager = seed.Request.Policy!.Manager as ConfigurationManager<OpenIdConnectConfiguration>;
        EntraProtocolFixturePolicy.Check(manager is not null && manager.UseLastKnownGoodConfiguration
            && manager.IsLastKnownGoodValid && ReferenceEquals(manager.LastKnownGoodConfiguration, admitted),
            "The cache regression did not begin with a real successful standard LKG validation.");

        var excluded = ExcludedPublicKeys(test.Crypto);
        var unadmitted = new OpenIdConnectConfiguration
        {
            Issuer = admitted.Issuer,
            AuthorizationEndpoint = admitted.AuthorizationEndpoint,
            TokenEndpoint = admitted.TokenEndpoint,
            JwksUri = admitted.JwksUri,
            JsonWebKeySet = excluded
        };
        foreach (var key in excluded.GetSigningKeys()) unadmitted.SigningKeys.Add(key);
        EntraProtocolFixturePolicy.Check(unadmitted.SigningKeys.Count == 1
            && admitted.SigningKeys.All(key => key.KeyId != unadmitted.SigningKeys.Single().KeyId),
            "The unadmitted cache candidate did not contain distinct excluded fixture signing material.");
        await CheckExcludedSignatureControlAsync(test.Crypto, unadmitted.SigningKeys);

        // Both assignments are public standard-manager operations. Restoring the latest
        // property does not remove the other candidate from the standard internal LKG cache.
        manager!.LastKnownGoodConfiguration = unadmitted;
        manager.LastKnownGoodConfiguration = admitted;
        EntraProtocolFixturePolicy.Check(ReferenceEquals(manager.LastKnownGoodConfiguration, admitted),
            "The latest LKG property was not restored before the independent callback.");

        var flow = await test.FlowAsync(); int responses = test.Issuer.TokenResponses;
        test.Issuer.TokenCase = EntraProtocolTokenCase.ExcludedKey;
        var refused = await test.CompleteAsync(flow);
        test.Denied(refused, ServerSessionIdentityFailureKind.ConfigurationFailure);
        EntraProtocolFixturePolicy.Check(test.Issuer.TokenResponses == responses + 1
            && refused.Request.Verified is null && refused.Request.EvidencePublished == 0,
            "A fresh excluded-key redemption bypassed actual configuration admission or published private facts.");
        EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow); test.CheckPrivacy(refused);

        test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
        var healthy = await test.CompleteAsync(await test.FlowAsync()); test.Success(healthy); test.CheckPrivacy(healthy);
    });

    [Fact]
    public Task OutstandingFlowsIgnoreEarlierDecodableInvalidNonceAndDeleteOnlyTheirExactPair() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var browser = new EntraProtocolBrowser();
        var first = await test.FlowAsync(browser); var second = await test.FlowAsync(browser);
        test.ChallengeCookies(first.Begin); test.ChallengeCookies(second.Begin);
        string prefix = first.Begin.Request.Policy!.NoncePrefix;
        string invalid = prefix + Base64UrlEncoder.Encode(new byte[32]);
        const string unrelated = "__Host-Weft.Fixture.Unrelated", unrelatedValue = "untouched-control";
        const string invalidValue = "N";
        var all = ChallengeCookies(first.Begin).Concat(ChallengeCookies(second.Begin)).ToArray();

        // Force the invalid protected-data input ahead of both valid nonces in both
        // insertion and ordinal order. Complete the later valid nonce first, so the
        // other outstanding valid nonce also precedes the one being authenticated.
        var ordered = new EntraProtocolBrowser();
        ordered.Set(invalid, invalidValue); ordered.Set(unrelated, unrelatedValue);
        foreach (var cookie in all.OrderBy(cookie => cookie.Name.ToString(), StringComparer.Ordinal))
            ordered.Set(cookie.Name.ToString(), cookie.Value.ToString());
        var flows = new[] { first, second }.OrderByDescending(flow => NonceName(flow.Begin), StringComparer.Ordinal).ToArray();
        EntraProtocolFixturePolicy.Check(Base64UrlEncoder.DecodeBytes(invalid[prefix.Length..]).Length == 32
            && all.Select(cookie => cookie.Name.ToString()).Distinct(StringComparer.Ordinal).Count() == 4
            && all.Where(cookie => cookie.Name.ToString().StartsWith(prefix, StringComparison.Ordinal))
                .All(cookie => StringComparer.Ordinal.Compare(invalid, cookie.Name.ToString()) < 0)
            && ordered.Names[0] == invalid && ordered.Names.Count == 6,
            "The nonce-isolation case did not place a distinct decodable-invalid cookie before two live valid flows.");

        var remaining = new HashSet<string>(ordered.Names, StringComparer.Ordinal);
        foreach (var original in flows)
        {
            var flow = new EntraProtocolFlow(ordered, original.Begin, original.Callback);
            var reply = await test.CompleteAsync(flow); test.Success(reply); test.CheckPrivacy(reply);
            var own = ChallengeCookies(original.Begin).Select(cookie => cookie.Name.ToString()).ToHashSet(StringComparer.Ordinal);
            CheckExactPairDeletion(reply, own);
            remaining.ExceptWith(own);
            EntraProtocolFixturePolicy.Check(remaining.SetEquals(ordered.Names)
                && ordered.Header.Split("; ", StringSplitOptions.None).Contains(unrelated + "=" + unrelatedValue, StringComparer.Ordinal)
                && ordered.Header.Split("; ", StringSplitOptions.None).Contains(invalid + "=" + invalidValue, StringComparer.Ordinal),
                "Completing one owned flow changed unrelated cookies or another outstanding nonce/correlation pair.");
            EntraProtocolHttpsFixture.MatchingFlowCookiesCleared(flow);
        }
        EntraProtocolFixturePolicy.Check(ordered.Names.Count == 2 && test.Issuer.TokenResponses == 2,
            "The independent outstanding flows did not both complete while retaining the unrelated cookies.");
    });

    [Fact]
    public Task AdmittedSigningKeyMaterialCannotMutateBehindStableConfigurationAndKeyReferences() => EntraProtocolHttpsFixture.RunAsync(async test =>
    {
        var seed = await test.CompleteAsync(await test.FlowAsync()); test.Success(seed);
        var admitted = await test.ReadFreshCurrentConfigurationAsync(seed.Request, CancellationToken.None);
        var flow = await test.FlowAsync();
        EntraProtocolFixturePolicy.Check(admitted.JsonWebKeySet is not null,
            "The admitted key-set observation was absent.");
        var admittedJwks = admitted.JsonWebKeySet!;
        var keys = admitted.SigningKeys.ToArray(); var jwks = admittedJwks.Keys.ToArray();
        var candidate = admitted.SigningKeys.Single() as RsaSecurityKey;
        EntraProtocolFixturePolicy.Check(candidate is not null,
            "The standard admitted signing key was not a public RSA-parameter key.");
        var key = candidate!;
        EntraProtocolFixturePolicy.Check(key.Rsa is null
            && key.Parameters.Modulus is { Length: > 0 } && key.Parameters.Exponent is { Length: > 0 },
            "The standard admitted key did not expose the expected public RSA-parameter mutation seam.");
        byte[] modulus = key.Parameters.Modulus!;
        byte[] original = modulus.ToArray(), thumbprint = key.ComputeJwkThumbprint();
        var excluded = ExcludedPublicKeys(test.Crypto).Keys.Single();
        byte[] replacement = Base64UrlEncoder.DecodeBytes(excluded.N);
        EntraProtocolFixturePolicy.Check(replacement.Length == modulus.Length
            && !replacement.AsSpan().SequenceEqual(original)
            && Base64UrlEncoder.DecodeBytes(excluded.E).AsSpan().SequenceEqual(key.Parameters.Exponent),
            "The material-mutation control did not supply a distinct compatible public RSA modulus.");
        int before = test.Ledger().Count, responses = test.Issuer.TokenResponses;
        try
        {
            replacement.CopyTo(modulus, 0);
            EntraProtocolFixturePolicy.Check(admitted.SigningKeys.SequenceEqual(keys)
                && ReferenceEquals(admitted.JsonWebKeySet, admittedJwks) && admittedJwks.Keys.SequenceEqual(jwks)
                && !key.ComputeJwkThumbprint().AsSpan().SequenceEqual(thumbprint),
                "The material-mutation case changed collection identity instead of only admitted signing-key bytes.");
            var refused = await test.CompleteAsync(flow);
            test.Denied(refused, ServerSessionIdentityFailureKind.ConfigurationFailure); test.CheckPrivacy(refused);
            EntraProtocolFixturePolicy.Check(test.Ledger().Count == before && test.Issuer.TokenResponses == responses,
                "Mutated admitted signing material escaped the pre-redemption configuration boundary.");
        }
        finally { original.CopyTo(modulus, 0); }

        EntraProtocolFixturePolicy.Check(key.ComputeJwkThumbprint().AsSpan().SequenceEqual(thumbprint)
            && admitted.SigningKeys.SequenceEqual(keys) && ReferenceEquals(admitted.JsonWebKeySet, admittedJwks)
            && admittedJwks.Keys.SequenceEqual(jwks),
            "Restoring signing material did not preserve the admitted configuration and key-reference controls.");
        // Admission refused before state/code consumption. A fresh request can still
        // finish that same genuine flow once the original stable material is restored.
        var restored = await test.CompleteAsync(flow); test.Success(restored); test.CheckPrivacy(restored);
        test.Success(await test.CompleteAsync(await test.FlowAsync()));
    });

    private static JsonWebKeySet ExcludedPublicKeys(EntraProtocolCrypto crypto)
    {
        var keys = new JsonWebKeySet(JsonSerializer.Serialize(crypto.Jwks(EntraProtocolKeyCase.ForeignOnly, false, false)));
        EntraProtocolFixturePolicy.Check(keys.Keys.Count == 1 && !keys.Keys.Single().HasPrivateKey,
            "The excluded signing-material control was not a singleton public fixture key.");
        // Keep the injected configuration otherwise in-scope: provenance, not an
        // intentionally foreign issuer annotation or malformed key, must reject it.
        keys.Keys.Single().AdditionalData["issuer"] = EntraProtocolFixturePolicy.Issuer;
        return keys;
    }

    private static async Task CheckExcludedSignatureControlAsync(EntraProtocolCrypto crypto, IEnumerable<SecurityKey> keys)
    {
        var code = new EntraProtocolCode("owned-control-nonce", "owned-control-state", "owned-control-challenge", "owned-control-redirect");
        string token = crypto.IdToken(code, EntraProtocolTokenCase.ExcludedKey, false, out _);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKeys = keys,
            ValidIssuer = EntraProtocolFixturePolicy.Issuer,
            ValidAudience = EntraProtocolFixturePolicy.Client.ToString("D"),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            IncludeTokenOnFailedValidation = false,
            LogTokenId = false,
            LogValidationExceptions = false,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        });
        EntraProtocolFixturePolicy.Check(result.IsValid,
            "The excluded fixture signer did not match the well-formed unadmitted candidate's public signing key.");
    }

    private static IReadOnlyList<SetCookieHeaderValue> ChallengeCookies(EntraProtocolReply begin)
    {
        EntraProtocolFixturePolicy.Check(begin.Headers.TryGetValue("Set-Cookie", out var headers),
            "The nonce-isolation control lost its real challenge-cookie observation.");
        return SetCookieHeaderValue.ParseList(headers!.ToList()).ToArray();
    }

    private static string NonceName(EntraProtocolReply begin) => ChallengeCookies(begin)
        .Single(cookie => cookie.Name.ToString().StartsWith(begin.Request.Policy!.NoncePrefix, StringComparison.Ordinal)).Name.ToString();

    private static void CheckExactPairDeletion(EntraProtocolReply reply, HashSet<string> own)
    {
        EntraProtocolFixturePolicy.Check(reply.Headers.TryGetValue("Set-Cookie", out var headers),
            "A successful owned callback emitted no matching transient-cookie deletions.");
        var deleted = SetCookieHeaderValue.ParseList(headers!.ToList());
        EntraProtocolFixturePolicy.Check(own.Count == 2 && deleted.Count == 2
            && own.SetEquals(deleted.Select(cookie => cookie.Name.ToString()))
            && deleted.All(cookie => cookie.Value.Length == 0
                && cookie.Expires is { } expiry && expiry <= DateTimeOffset.UtcNow
                && (cookie.MaxAge is null || cookie.MaxAge <= TimeSpan.Zero)),
            "A callback deleted more than its exact authenticated nonce/correlation pair or emitted a non-deletion cookie.");
    }

    private sealed class AcceptingCryptoProvider : ICryptoProvider
    {
        internal int Creations;
        public bool IsSupportedAlgorithm(string algorithm, params object[] args) => true;
        public object Create(string algorithm, params object[] args)
        {
            Interlocked.Increment(ref Creations);
            return new AcceptingSignatureProvider((SecurityKey)args[0], algorithm);
        }
        public void Release(object cryptoInstance) => ((SignatureProvider)cryptoInstance).Dispose();
    }

    private sealed class SelfRestoringCryptoProvider(SecurityKey key, CryptoProviderFactory original) : ICryptoProvider
    {
        internal int Callbacks;
        public bool IsSupportedAlgorithm(string algorithm, params object[] args)
        {
            Interlocked.Increment(ref Callbacks);
            key.CryptoProviderFactory = original;
            return true;
        }
        public object Create(string algorithm, params object[] args)
        {
            Interlocked.Increment(ref Callbacks);
            return new AcceptingSignatureProvider((SecurityKey)args[0], algorithm);
        }
        public void Release(object cryptoInstance)
        {
            Interlocked.Increment(ref Callbacks);
            ((SignatureProvider)cryptoInstance).Dispose();
        }
    }

    private sealed class AcceptingSignatureProvider(SecurityKey key, string algorithm) : SignatureProvider(key, algorithm)
    {
        public override byte[] Sign(byte[] input) => throw new NotSupportedException();
        public override bool Verify(byte[] input, byte[] signature) => true;
        protected override void Dispose(bool disposing) { }
    }

    private sealed class EqualLookingKey : RsaSecurityKey
    {
        private readonly byte[] thumbprint;
        internal int Callbacks;
        internal EqualLookingKey(RsaSecurityKey original) : base(original.Parameters)
        {
            KeyId = original.KeyId;
            CryptoProviderFactory = original.CryptoProviderFactory;
            thumbprint = original.ComputeJwkThumbprint();
        }
        public override bool Equals(object? obj) { Interlocked.Increment(ref Callbacks); return true; }
        public override int GetHashCode() => 0;
        public override byte[] ComputeJwkThumbprint() { Interlocked.Increment(ref Callbacks); return thumbprint; }
    }
}
