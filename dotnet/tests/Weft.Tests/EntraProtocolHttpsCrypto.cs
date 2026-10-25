using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Weft.Tests;

/// <summary>Owned fixture keys only. All JWT signing and verification use standard IdentityModel.</summary>
internal sealed class EntraProtocolCrypto : IDisposable
{
    internal object? DisplayName { get; set; } = EntraProtocolFixturePolicy.PrivateMarker;
    internal X509Certificate2 Root { get; private set; } = null!;
    internal X509Certificate2 IssuerTls { get; private set; } = null!;
    internal X509Certificate2 ApplicationTls { get; private set; } = null!;
    internal X509Certificate2 ClientCertificate { get; private set; } = null!;
    internal X509Certificate2? UntrustedRoot { get; private set; }
    private RSA first = null!, second = null!, foreign = null!, unknown = null!;
    private byte[] symmetric = [];
    private readonly HashSet<string> assertionIds = new(StringComparer.Ordinal);
    private int disposed;

    internal EntraProtocolCrypto(bool wrongIssuerName = false, bool wrongTrustRoot = false)
    {
        try
        {
            first = RSA.Create(2048); second = RSA.Create(2048); foreign = RSA.Create(2048); unknown = RSA.Create(2048);
            symmetric = RandomNumberGenerator.GetBytes(32);
            using var ca = RSA.Create(2048);
            var request = new CertificateRequest("CN=Weft owned OIDC fixture CA", ca, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
            IssuerTls = Leaf(wrongIssuerName ? "wrong-owned-name.invalid" : "login.microsoftonline.com", false, false);
            ApplicationTls = Leaf("127.0.0.1", true, false);
            ClientCertificate = Leaf("Weft owned client assertion", false, true);
            if (wrongTrustRoot)
            {
                using var other = RSA.Create(2048);
                var untrusted = new CertificateRequest("CN=Weft unrelated fixture root", other, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                untrusted.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                untrusted.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                UntrustedRoot = untrusted.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
            }
        }
        catch
        {
            try { Dispose(); } catch { throw new EntraProtocolFixtureFailure("Owned protocol certificate construction and cleanup failed."); }
            throw new EntraProtocolFixtureFailure("Owned protocol certificates could not be constructed.");
        }
    }

    private X509Certificate2 Leaf(string name, bool ip, bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client)
        {
            var names = new SubjectAlternativeNameBuilder();
            if (ip) names.AddIpAddress(System.Net.IPAddress.Loopback); else names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
        }
        using var publicLeaf = request.Create(Root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(16));
        return publicLeaf.CopyWithPrivateKey(key);
    }

    internal object Jwks(EntraProtocolKeyCase scope, bool publishSecond, bool onlySecond)
    {
        var keys = new List<Dictionary<string, object?>>();
        if (scope != EntraProtocolKeyCase.ForeignOnly)
        {
            string? issuer = scope switch
            {
                EntraProtocolKeyCase.Template => "https://login.microsoftonline.com/{tenantid}/v2.0",
                EntraProtocolKeyCase.MissingIssuer => null,
                EntraProtocolKeyCase.MalformedIssuer => "https://login.microsoftonline.com/{not-tenant}/v2.0",
                _ => EntraProtocolFixturePolicy.Issuer
            };
            if (!onlySecond) keys.Add(PublicJwk(first, "weft-k1", issuer));
            if (publishSecond) keys.Add(PublicJwk(second, "weft-k2", issuer));
        }
        if (scope is EntraProtocolKeyCase.MixedForeign or EntraProtocolKeyCase.ForeignOnly)
            keys.Add(PublicJwk(foreign, "weft-foreign", "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0"));
        return new { keys };
    }

    private static Dictionary<string, object?> PublicJwk(RSA key, string id, string? issuer)
    {
        var publicKey = key.ExportParameters(false);
        var result = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = id,
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["n"] = Base64UrlEncoder.Encode(publicKey.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(publicKey.Exponent!)
        };
        if (issuer is not null) result.Add("issuer", issuer);
        return result;
    }

    internal string IdToken(EntraProtocolCode code, EntraProtocolTokenCase variant, bool useSecond,
        out DateTimeOffset expiry, int lifetimeSeconds = 300)
    {
        EntraProtocolFixturePolicy.Check(lifetimeSeconds is > 0 and <= 3600, "The owned token lifetime exceeded its fixture bound.");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = EntraProtocolFixturePolicy.Issuer,
            ["aud"] = EntraProtocolFixturePolicy.Client.ToString("D"),
            ["tid"] = EntraProtocolFixturePolicy.Tenant.ToString("D"),
            ["sub"] = EntraProtocolFixturePolicy.Subject,
            ["iat"] = now - 1,
            ["nbf"] = now - 1,
            ["exp"] = now + lifetimeSeconds,
            ["nonce"] = code.Nonce,
            ["name"] = DisplayName,
            ["email"] = EntraProtocolFixturePolicy.PrivateMarker + "@invalid.example",
            ["oid"] = EntraProtocolFixturePolicy.ObjectId.ToString("D"),
            ["roles"] = new[] { "administrator", "run.payload.read" },
            ["groups"] = new[] { EntraProtocolFixturePolicy.PrivateMarker },
            ["wids"] = new[] { EntraProtocolFixturePolicy.PrivateMarker }
        };
        switch (variant)
        {
            case EntraProtocolTokenCase.WrongIssuer: claims["iss"] = "https://login.microsoftonline.com/df07c377-bdb9-4d8a-b15f-d1dfc3537e99/v2.0"; break;
            case EntraProtocolTokenCase.WrongAudience: claims["aud"] = "b2617125-6fe6-4447-a993-c10c1c3e8f35"; break;
            case EntraProtocolTokenCase.WrongTenant: claims["tid"] = "df07c377-bdb9-4d8a-b15f-d1dfc3537e99"; break;
            case EntraProtocolTokenCase.MissingSubject: claims.Remove("sub"); break;
            case EntraProtocolTokenCase.MissingNbf: claims.Remove("nbf"); break;
            case EntraProtocolTokenCase.MissingExp: claims.Remove("exp"); break;
            case EntraProtocolTokenCase.Expired: claims["exp"] = now - 1; claims["nbf"] = now - 300; claims["iat"] = now - 300; break;
            case EntraProtocolTokenCase.NotYetValid: claims["nbf"] = now + 60; break;
            case EntraProtocolTokenCase.BadNonce: claims["nonce"] = "wrong-owned-nonce"; break;
            case EntraProtocolTokenCase.MissingObjectId: claims.Remove("oid"); break;
            case EntraProtocolTokenCase.NoncanonicalObjectId: claims["oid"] = EntraProtocolFixturePolicy.ObjectId.ToString("D").ToUpperInvariant(); break;
            case EntraProtocolTokenCase.EmptyObjectId: claims["oid"] = Guid.Empty.ToString("D"); break;
            case EntraProtocolTokenCase.ControlSubject: claims["sub"] = "opaque\u0001subject"; break;
            case EntraProtocolTokenCase.OversizedSubject: claims["sub"] = new string('s', 513); break;
            case EntraProtocolTokenCase.AlternateSubject: claims["sub"] = EntraProtocolFixturePolicy.AlternateSubject; break;
        }
        expiry = DateTimeOffset.FromUnixTimeSeconds(claims.TryGetValue("exp", out var exp) ? (long)exp! : now + 300);
        string json = JsonSerializer.Serialize(claims);
        string? duplicate = variant switch
        {
            EntraProtocolTokenCase.DuplicateIssuer => "iss",
            EntraProtocolTokenCase.DuplicateAudience => "aud",
            EntraProtocolTokenCase.DuplicateTenant => "tid",
            EntraProtocolTokenCase.DuplicateSubject => "sub",
            EntraProtocolTokenCase.DuplicateExp => "exp",
            EntraProtocolTokenCase.DuplicateObjectId => "oid",
            _ => null
        };
        if (duplicate is not null) json = json[..^1] + "," + JsonSerializer.Serialize(duplicate) + ":" + JsonSerializer.Serialize(claims[duplicate]) + "}";
        if (variant == EntraProtocolTokenCase.Unsigned)
            return Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Base64UrlEncoder.Encode(json) + ".";
        SecurityKey key; string algorithm = variant == EntraProtocolTokenCase.UnapprovedRsaAlgorithm ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256;
        if (variant == EntraProtocolTokenCase.SymmetricAlgorithm)
        { key = new SymmetricSecurityKey(symmetric) { KeyId = "weft-k1" }; algorithm = SecurityAlgorithms.HmacSha256; }
        else if (variant == EntraProtocolTokenCase.BadSignature) key = new RsaSecurityKey(unknown) { KeyId = "weft-k1" };
        else if (variant == EntraProtocolTokenCase.ExcludedKey) key = new RsaSecurityKey(foreign) { KeyId = "weft-foreign" };
        else key = new RsaSecurityKey(useSecond ? second : first) { KeyId = useSecond ? "weft-k2" : "weft-k1" };
        string token = new JsonWebTokenHandler().CreateToken(json, new SigningCredentials(key, algorithm)
        { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } });
        if (duplicate is not null)
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
            EntraProtocolFixturePolicy.Check(payload.RootElement.EnumerateObject().Count(property => property.NameEquals(duplicate)) == 2,
                "The standard signing helper did not preserve the intended duplicate-critical-claim vector.");
        }
        return token;
    }

    /// <summary>Signs an exact claims document, for access tokens, with the same owned key selection as ID tokens.</summary>
    internal string SignClaims(string json, EntraProtocolTokenCase variant = EntraProtocolTokenCase.Good, bool useSecond = false)
    {
        if (variant == EntraProtocolTokenCase.Unsigned)
            return Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Base64UrlEncoder.Encode(json) + ".";
        SecurityKey key; string algorithm = variant == EntraProtocolTokenCase.UnapprovedRsaAlgorithm ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256;
        if (variant == EntraProtocolTokenCase.SymmetricAlgorithm)
        { key = new SymmetricSecurityKey(symmetric) { KeyId = "weft-k1" }; algorithm = SecurityAlgorithms.HmacSha256; }
        else if (variant == EntraProtocolTokenCase.BadSignature) key = new RsaSecurityKey(unknown) { KeyId = "weft-k1" };
        else if (variant == EntraProtocolTokenCase.ExcludedKey) key = new RsaSecurityKey(foreign) { KeyId = "weft-foreign" };
        else key = new RsaSecurityKey(useSecond ? second : first) { KeyId = useSecond ? "weft-k2" : "weft-k1" };
        return new JsonWebTokenHandler().CreateToken(json, new SigningCredentials(key, algorithm)
        { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } });
    }

    internal async Task AssertValidUnsupportedRsaVectorAsync(string token, bool secondKey)
    {
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = EntraProtocolFixturePolicy.Issuer,
            ValidAudience = EntraProtocolFixturePolicy.Client.ToString("D"),
            IssuerSigningKey = new RsaSecurityKey((secondKey ? second : first).ExportParameters(false)),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha512],
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        EntraProtocolFixturePolicy.Check(result.IsValid && new JsonWebToken(token).Alg == SecurityAlgorithms.RsaSha512,
            "The unsupported-algorithm fixture was not a genuinely valid RSA-signed control.");
    }

    internal async Task<bool> ValidateAssertionAsync(string assertion)
    {
        if (assertion.Length is < 1 or > 16_384) return false;
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = EntraProtocolFixturePolicy.Client.ToString("D"),
            ValidAudience = EntraProtocolFixturePolicy.TokenAddress,
            IssuerSigningKey = new X509SecurityKey(ClientCertificate),
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSsaPssSha256],
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(assertion, parameters);
        if (!result.IsValid) return false;
        var token = new JsonWebToken(assertion);
        string certificateDigest = Base64UrlEncoder.Encode(SHA256.HashData(ClientCertificate.RawData));
        if (!token.TryGetHeaderValue<string>("x5t#S256", out var digest)
            || !string.Equals(digest, certificateDigest, StringComparison.Ordinal)) return false;
        var ids = token.Claims.Where(value => value.Type == "jti").Select(value => value.Value).ToArray();
        if (token.Alg != SecurityAlgorithms.RsaSsaPssSha256 || token.Issuer != EntraProtocolFixturePolicy.Client.ToString("D")
            || !token.Audiences.SequenceEqual([EntraProtocolFixturePolicy.TokenAddress]) || token.Subject != EntraProtocolFixturePolicy.Client.ToString("D")
            || ids.Length != 1 || ids[0].Length is < 1 or > 256 || token.ValidTo <= DateTime.UtcNow
            || token.ValidTo > DateTime.UtcNow.AddMinutes(5) || token.ValidFrom > DateTime.UtcNow) return false;
        lock (assertionIds) return assertionIds.Add(ids[0]);
    }

    internal string AssertionControl(string? certificateDigest)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = EntraProtocolFixturePolicy.Client.ToString("D"),
            Audience = EntraProtocolFixturePolicy.TokenAddress,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = EntraProtocolFixturePolicy.Client.ToString("D"),
                ["jti"] = Guid.NewGuid().ToString("D")
            },
            IssuedAt = now.AddSeconds(-1),
            NotBefore = now.AddSeconds(-1),
            Expires = now.AddMinutes(2),
            SigningCredentials = new X509SigningCredentials(ClientCertificate, SecurityAlgorithms.RsaSsaPssSha256)
            { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } },
            AdditionalHeaderClaims = certificateDigest is null ? null : new Dictionary<string, object> { ["x5t#S256"] = certificateDigest }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        first?.Dispose(); second?.Dispose(); foreign?.Dispose(); unknown?.Dispose(); CryptographicOperations.ZeroMemory(symmetric);
        ClientCertificate?.Dispose(); ApplicationTls?.Dispose(); IssuerTls?.Dispose(); UntrustedRoot?.Dispose(); Root?.Dispose();
    }
    public override string ToString() => nameof(EntraProtocolCrypto);
}
