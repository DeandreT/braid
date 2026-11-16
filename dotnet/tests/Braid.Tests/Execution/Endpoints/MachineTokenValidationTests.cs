using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Braid.Application;
using Braid.Identity;

namespace Braid.Tests;

/// <summary>Real HTTPS discovery and signing keys from the owned issuer; access tokens minted with owned keys.</summary>
public sealed class MachineTokenValidationTests
{
    private static readonly Guid Caller = Guid.Parse("0f8f2a1e-5bd4-4c43-9f1a-7d1b8f3e2c01");
    private static readonly Guid Application = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    [Fact]
    public async Task App_only_tokens_for_the_fixed_tenant_and_audience_identify_the_caller_by_object_ID()
    {
        await using var test = await Fixture.StartAsync();
        var caller = await test.Validator.ValidateAsync(test.Sign(Claims()), default);
        Assert.Equal(new MachinePrincipal(MachinePrincipal.Entra, EntraProtocolFixturePolicy.Tenant, Caller), caller.Principal);
        Assert.Equal(Application, caller.ApplicationId);
        Assert.True(caller.ExpiresAt > DateTimeOffset.UtcNow);
        // A managed identity token: no idtyp claim, the v1 application claim alongside azp, and app roles.
        var managed = Claims(); managed.Remove("idtyp"); managed["appid"] = Application.ToString("D"); managed["roles"] = new JsonArray("Invoke");
        Assert.Equal(Caller, (await test.Validator.ValidateAsync(test.Sign(managed), default)).Principal.ObjectId);
        // Validation reads only the fixed discovery document and signing keys; it never sends a token anywhere.
        Assert.All(test.Transport.SnapshotAttempts(), attempt => Assert.Equal("GET", attempt.Method));
        Assert.All(test.Transport.SnapshotAttempts(), attempt => Assert.Contains(attempt.LogicalPath,
            new[] { EntraProtocolFixturePolicy.Discovery, EntraProtocolFixturePolicy.Keys }));
    }

    [Theory]
    [InlineData("wrongIssuer", "Issuer")]
    [InlineData("v1Issuer", "Issuer")]
    [InlineData("wrongAudience", "Audience")]
    [InlineData("audienceArray", "Audience")]
    [InlineData("wrongTenant", "Tenant")]
    [InlineData("v1Token", "Version")]
    [InlineData("missingVersion", "Version")]
    [InlineData("expired", "Lifetime")]
    [InlineData("notYetValid", "Lifetime")]
    [InlineData("missingExp", "Lifetime")]
    [InlineData("missingNbf", "Lifetime")]
    [InlineData("missingIat", "Lifetime")]
    [InlineData("delegatedScope", "Delegated")]
    [InlineData("userTokenType", "TokenType")]
    [InlineData("missingObjectId", "ObjectId")]
    [InlineData("noncanonicalObjectId", "ObjectId")]
    [InlineData("subjectMismatch", "Subject")]
    [InlineData("idTokenSubject", "Subject")]
    [InlineData("missingApplication", "Application")]
    [InlineData("applicationMismatch", "Application")]
    [InlineData("duplicateObjectId", "Duplicate")]
    [InlineData("duplicateAudience", "Audience")]
    [InlineData("unsigned", "Signature")]
    [InlineData("badSignature", "Signature")]
    [InlineData("symmetric", "Signature")]
    [InlineData("rs512", "Signature")]
    [InlineData("excludedKey", "Signature")]
    [InlineData("tamperedPayload", "Signature")]
    public async Task Every_other_token_is_denied_by_its_own_rule_after_the_issuer_was_reached(string variant, string rule)
    {
        await using var test = await Fixture.StartAsync();
        // The owned issuer is reachable: discovery and keys load and a valid token is admitted first.
        Assert.Equal(Caller, (await test.Validator.ValidateAsync(test.Sign(Claims()), default)).Principal.ObjectId);
        Assert.Contains(test.Transport.SnapshotAttempts(), attempt => attempt.LogicalPath == EntraProtocolFixturePolicy.Keys);
        var claims = Claims();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signing = EntraProtocolTokenCase.Good;
        string? duplicate = null;
        switch (variant)
        {
            case "wrongIssuer": claims["iss"] = "https://login.microsoftonline.com/df07c377-bdb9-4d8a-b15f-d1dfc3537e99/v2.0"; break;
            case "v1Issuer": claims["iss"] = $"https://sts.windows.net/{EntraProtocolFixturePolicy.Tenant:D}/"; break;
            case "wrongAudience": claims["aud"] = "b2617125-6fe6-4447-a993-c10c1c3e8f35"; break;
            case "audienceArray": claims["aud"] = new JsonArray(EntraProtocolFixturePolicy.Client.ToString("D")); break;
            case "wrongTenant": claims["tid"] = "df07c377-bdb9-4d8a-b15f-d1dfc3537e99"; break;
            case "v1Token": claims["ver"] = "1.0"; break;
            case "missingVersion": claims.Remove("ver"); break;
            case "expired": claims["exp"] = now - 1; claims["nbf"] = now - 600; claims["iat"] = now - 600; break;
            case "notYetValid": claims["nbf"] = now + 120; break;
            case "missingExp": claims.Remove("exp"); break;
            case "missingNbf": claims.Remove("nbf"); break;
            case "missingIat": claims.Remove("iat"); break;
            case "delegatedScope": claims["scp"] = "Invoke"; break;
            case "userTokenType": claims["idtyp"] = "user"; break;
            case "missingObjectId": claims.Remove("oid"); break;
            case "noncanonicalObjectId": claims["oid"] = Caller.ToString("D").ToUpperInvariant(); claims["sub"] = Caller.ToString("D").ToUpperInvariant(); break;
            case "subjectMismatch": claims["sub"] = Guid.NewGuid().ToString("D"); break;
            case "idTokenSubject":
                claims.Remove("idtyp");
                claims["sub"] = "pairwise-user-subject";
                claims["nonce"] = "owned-id-token-nonce";
                break;
            case "missingApplication": claims.Remove("azp"); break;
            case "applicationMismatch": claims["appid"] = Guid.NewGuid().ToString("D"); break;
            case "duplicateObjectId": duplicate = "\"oid\":\"" + Guid.NewGuid().ToString("D") + "\""; break;
            case "duplicateAudience": duplicate = "\"aud\":\"b2617125-6fe6-4447-a993-c10c1c3e8f35\""; break;
            case "unsigned": signing = EntraProtocolTokenCase.Unsigned; break;
            case "badSignature": signing = EntraProtocolTokenCase.BadSignature; break;
            case "symmetric": signing = EntraProtocolTokenCase.SymmetricAlgorithm; break;
            case "rs512": signing = EntraProtocolTokenCase.UnapprovedRsaAlgorithm; break;
            case "excludedKey": signing = EntraProtocolTokenCase.ExcludedKey; break;
        }
        var json = claims.ToJsonString();
        if (duplicate is not null) json = json[..^1] + "," + duplicate + "}";
        var token = test.Crypto.SignClaims(json, signing);
        if (variant == "tamperedPayload")
        {
            var parts = token.Split('.');
            claims["oid"] = Guid.NewGuid().ToString("D");
            token = parts[0] + "." + Base64UrlEncoder.Encode(claims.ToJsonString()) + "." + parts[2];
        }
        var (caller, reason) = await test.Validator.CheckAsync(token, default);
        Assert.Null(caller);
        Assert.Equal(rule, reason.ToString());
        var refusal = await Assert.ThrowsAsync<MachineTokenException>(() => test.Validator.ValidateAsync(token, default));
        Assert.Equal(MachineTokenFailure.Denied, refusal.Failure);
        Assert.DoesNotContain(Caller.ToString("D"), refusal.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_owned_issuer_answers_fixed_discovery_through_the_fixture_transport_without_a_token()
    {
        await using var test = await Fixture.StartAsync();
        using var sockets = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        test.Transport.Configure(sockets);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync("https://login.microsoftonline.com" + EntraProtocolFixturePolicy.Discovery);
        Assert.True(response.IsSuccessStatusCode, "The owned issuer must accept the fixed discovery request.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(EntraProtocolFixturePolicy.Issuer, document.RootElement.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task Malformed_and_oversized_credentials_are_denied_without_contacting_the_issuer()
    {
        await using var test = await Fixture.StartAsync();
        foreach (var token in new[] { "", "a.b", "a.b.c.d", "a b.c.d", new string('a', 16 * 1024 + 1), "eyJ.eyJ.%" })
        {
            Assert.Equal(EntraTokenRefusal.Format, (await test.Validator.CheckAsync(token, default)).Refusal);
            var refusal = await Assert.ThrowsAsync<MachineTokenException>(() => test.Validator.ValidateAsync(token, default));
            Assert.Equal(MachineTokenFailure.Denied, refusal.Failure);
        }
        Assert.Empty(test.Transport.SnapshotAttempts());
    }

    [Fact]
    public async Task A_new_signing_key_is_admitted_after_the_bounded_standard_refresh()
    {
        await using var test = await Fixture.StartAsync();
        Assert.Equal(Caller, (await test.Validator.ValidateAsync(test.Sign(Claims()), default)).Principal.ObjectId);
        var reads = test.KeyReads();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Issuer.NextKeysResponse = refreshed;
        test.Issuer.PublishSecond = true;
        var rotated = test.Crypto.SignClaims(Claims().ToJsonString(), useSecond: true);
        try { await test.Validator.ValidateAsync(rotated, default); }
        catch (MachineTokenException error) { Assert.Equal(MachineTokenFailure.Denied, error.Failure); }
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        MachineCaller? accepted = null;
        for (var attempt = 0; accepted is null && attempt < 50; attempt++)
        {
            try { accepted = await test.Validator.ValidateAsync(rotated, default); }
            catch (MachineTokenException) { await Task.Delay(100); }
        }
        Assert.NotNull(accepted);
        Assert.True(test.KeyReads() > reads);
        // A further unknown key does not refetch: the standard refresh interval bounds key discovery.
        var afterRotation = test.KeyReads();
        var unknown = await Assert.ThrowsAsync<MachineTokenException>(() =>
            test.Validator.ValidateAsync(test.Crypto.SignClaims(Claims().ToJsonString(), EntraProtocolTokenCase.ExcludedKey), default));
        Assert.Equal(MachineTokenFailure.Denied, unknown.Failure);
        await Task.Delay(500);
        Assert.Equal(afterRotation, test.KeyReads());
    }

    [Theory]
    [InlineData(nameof(EntraProtocolMetadataCase.Redirect))]
    [InlineData(nameof(EntraProtocolMetadataCase.WrongIssuer))]
    [InlineData(nameof(EntraProtocolMetadataCase.ForeignKeys))]
    public async Task Unusable_issuer_metadata_is_unavailable_not_accepted(string metadata)
    {
        await using var test = await Fixture.StartAsync();
        test.Issuer.Metadata = Enum.Parse<EntraProtocolMetadataCase>(metadata);
        Assert.Equal(EntraTokenRefusal.Unavailable, (await test.Validator.CheckAsync(test.Sign(Claims()), default)).Refusal);
        var refusal = await Assert.ThrowsAsync<MachineTokenException>(() => test.Validator.ValidateAsync(test.Sign(Claims()), default));
        Assert.Equal(MachineTokenFailure.Unavailable, refusal.Failure);
    }

    [Fact]
    public async Task Signing_keys_without_eligible_tenant_scope_are_unavailable()
    {
        await using var test = await Fixture.StartAsync();
        test.Issuer.KeyCase = EntraProtocolKeyCase.ForeignOnly;
        var refusal = await Assert.ThrowsAsync<MachineTokenException>(() => test.Validator.ValidateAsync(test.Sign(Claims()), default));
        Assert.Equal(MachineTokenFailure.Unavailable, refusal.Failure);
    }

    [Fact]
    public void Options_require_one_organizational_tenant_and_an_exact_audience()
    {
        Assert.Throws<ArgumentException>(() => new EntraMachineTokenOptions(Guid.Empty, EntraProtocolFixturePolicy.Client));
        Assert.Throws<ArgumentException>(() => new EntraMachineTokenOptions(Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad"), EntraProtocolFixturePolicy.Client));
        Assert.Throws<ArgumentException>(() => new EntraMachineTokenOptions(EntraProtocolFixturePolicy.Tenant, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MachinePrincipal("aws", EntraProtocolFixturePolicy.Tenant, Caller));
        Assert.Throws<ArgumentException>(() => new MachinePrincipal(MachinePrincipal.Entra, EntraProtocolFixturePolicy.Tenant, Guid.Empty));
    }

    [Fact]
    public async Task Disposal_refuses_later_validation_as_unavailable()
    {
        await using var test = await Fixture.StartAsync();
        var token = test.Sign(Claims());
        await test.Validator.DisposeAsync();
        var refusal = await Assert.ThrowsAsync<MachineTokenException>(() => test.Validator.ValidateAsync(token, default));
        Assert.Equal(MachineTokenFailure.Unavailable, refusal.Failure);
    }

    internal static JsonObject Claims(Guid? caller = null, Guid? application = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var oid = (caller ?? Caller).ToString("D");
        return new JsonObject
        {
            ["aud"] = EntraProtocolFixturePolicy.Client.ToString("D"),
            ["iss"] = EntraProtocolFixturePolicy.Issuer,
            ["iat"] = now - 60,
            ["nbf"] = now - 60,
            ["exp"] = now + 600,
            ["aio"] = "opaque",
            ["azp"] = (application ?? Application).ToString("D"),
            ["azpacr"] = "2",
            ["idtyp"] = "app",
            ["oid"] = oid,
            ["rh"] = "opaque",
            ["sub"] = oid,
            ["tid"] = EntraProtocolFixturePolicy.Tenant.ToString("D"),
            ["uti"] = "opaque",
            ["ver"] = "2.0"
        };
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal EntraProtocolCrypto Crypto { get; private init; } = null!;
        internal EntraProtocolHttpsIssuer Issuer { get; private init; } = null!;
        internal EntraHttpsFixtureTransport Transport { get; private init; } = null!;
        internal EntraMachineTokenValidator Validator { get; private init; } = null!;

        internal static async Task<Fixture> StartAsync()
        {
            var crypto = new EntraProtocolCrypto();
            var issuer = new EntraProtocolHttpsIssuer(crypto);
            try
            {
                await issuer.StartAsync();
                var transport = new EntraHttpsFixtureTransport(issuer.Port, crypto.Root);
                return new()
                {
                    Crypto = crypto,
                    Issuer = issuer,
                    Transport = transport,
                    Validator = new(new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client), transport)
                };
            }
            catch { await issuer.DisposeAsync(); crypto.Dispose(); throw; }
        }

        internal string Sign(JsonObject claims) => Crypto.SignClaims(claims.ToJsonString());
        internal int KeyReads() => Issuer.Snapshot().Count(send => send.Path == EntraProtocolFixturePolicy.Keys);

        public async ValueTask DisposeAsync()
        {
            await Validator.DisposeAsync();
            await Issuer.DisposeAsync();
            Crypto.Dispose();
        }
    }
}
