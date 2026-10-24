using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Weft.Application;
using Weft.Identity;

namespace Weft.Tests;

/// <summary>Real HTTPS discovery and signing keys from the owned issuer; delegated tokens minted with owned keys.</summary>
public sealed class UserTokenValidationTests
{
    internal static readonly Guid Cli = Guid.Parse("8f3c1d2a-4b5e-4f60-9a7b-1c2d3e4f5a6b");
    internal static readonly Guid SecondCli = Guid.Parse("2b4d6f80-1a3c-4e5f-8a9b-0c1d2e3f4a5b");
    internal const string Scope = "weft.api";

    [Fact]
    public async Task Delegated_tokens_for_the_scope_from_an_allowlisted_client_identify_the_user_by_tenant_and_object_ID()
    {
        await using var test = await Fixture.StartAsync();
        var caller = await test.Validator.ValidateAsync(test.Sign(Claims()), default);
        Assert.True(caller.Identity == EntraProtocolFixturePolicy.UserKey, "The caller was not the tenant-stable key.");
        Assert.Equal(Cli, caller.ClientApplicationId);
        Assert.True(caller.ExpiresAt > DateTimeOffset.UtcNow);
        // Another allowlisted client, more scopes, configured idtyp, roles and display claims: the same key.
        var other = Claims(application: SecondCli);
        other["scp"] = "other.scope " + Scope + " offline";
        other["idtyp"] = "user";
        other["roles"] = new JsonArray("administrator");
        other["name"] = EntraProtocolFixturePolicy.PrivateMarker;
        other["sub"] = "a-different-pairwise-subject";
        var second = await test.Validator.ValidateAsync(test.Sign(other), default);
        Assert.True(second.Identity == caller.Identity, "A second client changed the user's key.");
        Assert.Equal(SecondCli, second.ClientApplicationId);
        // Validation reads only the fixed discovery document and signing keys; it never sends a token anywhere.
        Assert.All(test.Transport.SnapshotAttempts(), attempt => Assert.Equal("GET", attempt.Method));
        Assert.All(test.Transport.SnapshotAttempts(), attempt => Assert.Contains(attempt.LogicalPath,
            new[] { EntraProtocolFixturePolicy.Discovery, EntraProtocolFixturePolicy.Keys }));
    }

    [Theory]
    [InlineData("wrongIssuer", "Issuer")]
    [InlineData("v1Issuer", "Issuer")]
    [InlineData("wrongAudience", "Audience")]
    [InlineData("applicationIdUriAudience", "Audience")]
    [InlineData("audienceArray", "Audience")]
    [InlineData("wrongTenant", "Tenant")]
    [InlineData("noncanonicalTenant", "Tenant")]
    [InlineData("v1Token", "Version")]
    [InlineData("missingVersion", "Version")]
    [InlineData("expired", "Lifetime")]
    [InlineData("notYetValid", "Lifetime")]
    [InlineData("issuedInFuture", "Lifetime")]
    [InlineData("missingExp", "Lifetime")]
    [InlineData("missingNbf", "Lifetime")]
    [InlineData("missingIat", "Lifetime")]
    [InlineData("fractionalExp", "Lifetime")]
    [InlineData("appOnly", "TokenType")]
    [InlineData("appOnlyWithoutType", "Scope")]
    [InlineData("rolesOnly", "Scope")]
    [InlineData("missingScope", "Scope")]
    [InlineData("otherScope", "Scope")]
    [InlineData("scopePrefix", "Scope")]
    [InlineData("scopeUri", "Scope")]
    [InlineData("scopeCase", "Scope")]
    [InlineData("scopeDoubleSpace", "Scope")]
    [InlineData("scopeArray", "Scope")]
    [InlineData("unknownClient", "Client")]
    [InlineData("audienceClient", "Client")]
    [InlineData("missingClient", "Application")]
    [InlineData("noncanonicalClient", "Application")]
    [InlineData("applicationMismatch", "Application")]
    [InlineData("missingObjectId", "ObjectId")]
    [InlineData("noncanonicalObjectId", "ObjectId")]
    [InlineData("emptyObjectId", "ObjectId")]
    [InlineData("missingSubject", "Subject")]
    [InlineData("duplicateObjectId", "Duplicate")]
    [InlineData("duplicateScope", "Duplicate")]
    [InlineData("duplicateAudience", "Audience")]
    [InlineData("idToken", "Scope")]
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
        Assert.True((await test.Validator.ValidateAsync(test.Sign(Claims()), default)).Identity == EntraProtocolFixturePolicy.UserKey);
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
            case "applicationIdUriAudience": claims["aud"] = "api://" + EntraProtocolFixturePolicy.Client.ToString("D"); break;
            case "audienceArray": claims["aud"] = new JsonArray(EntraProtocolFixturePolicy.Client.ToString("D")); break;
            case "wrongTenant": claims["tid"] = "df07c377-bdb9-4d8a-b15f-d1dfc3537e99"; break;
            case "noncanonicalTenant": claims["tid"] = EntraProtocolFixturePolicy.Tenant.ToString("D").ToUpperInvariant(); break;
            case "v1Token": claims["ver"] = "1.0"; break;
            case "missingVersion": claims.Remove("ver"); break;
            case "expired": claims["exp"] = now - 1; claims["nbf"] = now - 600; claims["iat"] = now - 600; break;
            case "notYetValid": claims["nbf"] = now + 120; break;
            case "issuedInFuture": claims["iat"] = now + 120; break;
            case "missingExp": claims.Remove("exp"); break;
            case "missingNbf": claims.Remove("nbf"); break;
            case "missingIat": claims.Remove("iat"); break;
            case "fractionalExp": claims["exp"] = now + 600.5; break;
            case "appOnly":
                // A client-credentials token for the same audience: idtyp app, no scp, sub equals oid.
                claims = MachineTokenValidationTests.Claims(EntraProtocolFixturePolicy.ObjectId, Cli);
                break;
            case "appOnlyWithoutType":
                claims = MachineTokenValidationTests.Claims(EntraProtocolFixturePolicy.ObjectId, Cli); claims.Remove("idtyp");
                break;
            case "rolesOnly": claims.Remove("scp"); claims["roles"] = new JsonArray(Scope); break;
            case "missingScope": claims.Remove("scp"); break;
            case "otherScope": claims["scp"] = "other.scope"; break;
            case "scopePrefix": claims["scp"] = Scope + "x"; break;
            case "scopeUri": claims["scp"] = "api://" + EntraProtocolFixturePolicy.Client.ToString("D") + "/" + Scope; break;
            case "scopeCase": claims["scp"] = Scope.ToUpperInvariant(); break;
            case "scopeDoubleSpace": claims["scp"] = "other  " + Scope; break;
            case "scopeArray": claims["scp"] = new JsonArray(Scope); break;
            case "unknownClient": claims["azp"] = Guid.NewGuid().ToString("D"); break;
            case "audienceClient": claims["azp"] = EntraProtocolFixturePolicy.Client.ToString("D"); break;
            case "missingClient": claims.Remove("azp"); break;
            case "noncanonicalClient": claims["azp"] = Cli.ToString("D").ToUpperInvariant(); break;
            case "applicationMismatch": claims["appid"] = SecondCli.ToString("D"); break;
            case "missingObjectId": claims.Remove("oid"); break;
            case "noncanonicalObjectId": claims["oid"] = EntraProtocolFixturePolicy.ObjectId.ToString("D").ToUpperInvariant(); break;
            case "emptyObjectId": claims["oid"] = Guid.Empty.ToString("D"); break;
            case "missingSubject": claims.Remove("sub"); break;
            case "duplicateObjectId": duplicate = "\"oid\":\"" + Guid.NewGuid().ToString("D") + "\""; break;
            case "duplicateScope": duplicate = "\"scp\":\"other.scope\""; break;
            case "duplicateAudience": duplicate = "\"aud\":\"b2617125-6fe6-4447-a993-c10c1c3e8f35\""; break;
            case "idToken":
                // The Session app's own ID token has the same audience and tenant, but no scope or allowlisted client.
                claims.Remove("scp"); claims.Remove("azp"); claims["nonce"] = "owned-id-token-nonce";
                break;
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
        var refusal = await Assert.ThrowsAsync<UserTokenException>(() => test.Validator.ValidateAsync(token, default));
        Assert.Equal(UserTokenFailure.Denied, refusal.Failure);
        foreach (var text in new[] { refusal.ToString(), refusal.Message })
        {
            Assert.DoesNotContain(EntraProtocolFixturePolicy.ObjectId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
        Assert.Null(refusal.InnerException);
    }

    [Fact]
    public async Task Malformed_and_oversized_credentials_are_denied_without_contacting_the_issuer()
    {
        await using var test = await Fixture.StartAsync();
        foreach (var token in new[] { "", "a.b", "a.b.c.d", "a b.c.d", " " + test.Sign(Claims()), new string('a', 16 * 1024 + 1), "eyJ.eyJ.%" })
        {
            Assert.Equal(EntraTokenRefusal.Format, (await test.Validator.CheckAsync(token, default)).Refusal);
            var refusal = await Assert.ThrowsAsync<UserTokenException>(() => test.Validator.ValidateAsync(token, default));
            Assert.Equal(UserTokenFailure.Denied, refusal.Failure);
        }
        Assert.Empty(test.Transport.SnapshotAttempts());
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
        var refusal = await Assert.ThrowsAsync<UserTokenException>(() => test.Validator.ValidateAsync(test.Sign(Claims()), default));
        Assert.Equal(UserTokenFailure.Unavailable, refusal.Failure);
    }

    [Fact]
    public async Task A_new_signing_key_is_admitted_after_the_bounded_standard_refresh()
    {
        await using var test = await Fixture.StartAsync();
        Assert.Equal(Cli, (await test.Validator.ValidateAsync(test.Sign(Claims()), default)).ClientApplicationId);
        var reads = test.KeyReads();
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Issuer.NextKeysResponse = refreshed;
        test.Issuer.PublishSecond = true;
        var rotated = test.Crypto.SignClaims(Claims().ToJsonString(), useSecond: true);
        try { await test.Validator.ValidateAsync(rotated, default); }
        catch (UserTokenException error) { Assert.Equal(UserTokenFailure.Denied, error.Failure); }
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        UserCaller? accepted = null;
        for (var attempt = 0; accepted is null && attempt < 50; attempt++)
        {
            try { accepted = await test.Validator.ValidateAsync(rotated, default); }
            catch (UserTokenException) { await Task.Delay(100); }
        }
        Assert.NotNull(accepted);
        Assert.True(test.KeyReads() > reads);
    }

    [Fact]
    public void Options_require_one_organizational_tenant_an_audience_a_scope_name_and_distinct_clients()
    {
        var tenant = EntraProtocolFixturePolicy.Tenant;
        var audience = EntraProtocolFixturePolicy.Client;
        _ = new EntraUserTokenOptions(tenant, audience, Scope, [Cli, SecondCli]);
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(Guid.Empty, audience, Scope, [Cli]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad"), audience, Scope, [Cli]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, Guid.Empty, Scope, [Cli]));
        foreach (var scope in new[] { "", " ", "weft api", "api://" + audience.ToString("D") + "/weft.api", "weft/api", "wéft", new string('a', 121) })
            Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, scope, [Cli]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, Scope, []));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, Scope, [Cli, Cli]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, Scope, [Guid.Empty]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, Scope, [audience]));
        Assert.Throws<ArgumentException>(() => new EntraUserTokenOptions(tenant, audience, Scope,
            Enumerable.Range(0, 17).Select(_ => Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => new UserCaller(EntraProtocolFixturePolicy.UserKey, Guid.Empty, DateTimeOffset.UtcNow));
        Assert.Equal(nameof(EntraUserTokenOptions), new EntraUserTokenOptions(tenant, audience, Scope, [Cli]).ToString());
        Assert.Equal(nameof(UserCaller), new UserCaller(EntraProtocolFixturePolicy.UserKey, Cli, DateTimeOffset.UtcNow).ToString());
    }

    [Fact]
    public async Task Disposal_refuses_later_validation_as_unavailable()
    {
        await using var test = await Fixture.StartAsync();
        var token = test.Sign(Claims());
        await test.Validator.DisposeAsync();
        var refusal = await Assert.ThrowsAsync<UserTokenException>(() => test.Validator.ValidateAsync(token, default));
        Assert.Equal(UserTokenFailure.Unavailable, refusal.Failure);
    }

    /// <summary>The claims Entra issues in a v2 delegated access token for the Session app's exposed scope.</summary>
    internal static JsonObject Claims(Guid? objectId = null, Guid? application = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["aud"] = EntraProtocolFixturePolicy.Client.ToString("D"),
            ["iss"] = EntraProtocolFixturePolicy.Issuer,
            ["iat"] = now - 60,
            ["nbf"] = now - 60,
            ["exp"] = now + 600,
            ["aio"] = "opaque",
            ["azp"] = (application ?? Cli).ToString("D"),
            ["azpacr"] = "0",
            ["name"] = "Fixture user",
            ["oid"] = (objectId ?? EntraProtocolFixturePolicy.ObjectId).ToString("D"),
            ["preferred_username"] = "fixture.user@invalid.example",
            ["rh"] = "opaque",
            ["scp"] = Scope,
            ["sid"] = Guid.NewGuid().ToString("D"),
            ["sub"] = "pairwise-cli-subject",
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
        internal EntraUserTokenValidator Validator { get; private init; } = null!;

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
                    Validator = new(new(EntraProtocolFixturePolicy.Tenant, EntraProtocolFixturePolicy.Client, Scope, [Cli, SecondCli]), transport)
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
