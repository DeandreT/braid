using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Braid.Application;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AuthHttpDisplayMetadataTests(PostgresFixture postgres)
{
    [PostgresFact]
    public Task OnlyFullyValidatedEnabledSignInPublishesDisplayMetadataAndRefreshesWithoutChangingOldIdentityContext() =>
        AuthHttpFixture.RunAsync(postgres, async test =>
        {
            var jar = new AuthHttpJar();
            Assert.Equal(JsonValueKind.Null, (await test.StatusAsync(jar)).Reply.Json().GetProperty("displayName").ValueKind);
            test.Crypto.DisplayName = "Zoë <script>&";
            foreach (var invalidToken in new[] { EntraProtocolTokenCase.BadNonce, EntraProtocolTokenCase.WrongTenant, EntraProtocolTokenCase.DuplicateObjectId })
            {
                var flow = await test.FlowAsync(jar);
                test.Issuer.TokenCase = invalidToken;
                var rejected = await test.CompleteAsync(flow);
                Assert.NotEqual(HttpStatusCode.SeeOther, rejected.Status);
                Assert.Null(await test.DisplayMetadata!.ReadAsync(test.Data.Seed.OrganizationA, test.Binding.BindingId, default));
            }
            test.Issuer.TokenCase = EntraProtocolTokenCase.Good;
            jar = await test.LoginAsync(new());
            var first = await test.StatusAsync(jar);
            Assert.Equal("Zoë <script>&", first.Reply.Json().GetProperty("displayName").GetString());
            Assert.All(first.Reply.Record.ActionCookieUsers, user =>
            {
                Assert.Equal(3, user.Claims.Count());
                Assert.DoesNotContain(user.Claims, claim => claim.Type == "name" || claim.Value == "Zoë <script>&");
            });
            var oldJar = jar.Copy();
            test.Crypto.DisplayName = "Renamed";
            _ = await test.LoginAsync(new());
            var refreshed = await test.StatusAsync(oldJar);
            Assert.Equal("Renamed", refreshed.Reply.Json().GetProperty("displayName").GetString());
            Assert.Equal(first.Fingerprint, refreshed.Fingerprint);
            await test.AddSecondAsync();
            var restarted = await test.StatusAsync(oldJar, host: test.Second);
            Assert.Equal("Renamed", restarted.Reply.Json().GetProperty("displayName").GetString());
            foreach (var invalid in new object?[] { null, " ", "unsafe\u202e", new string('a', 201), 42, new[] { "Array name" } })
            {
                test.Crypto.DisplayName = invalid;
                _ = await test.LoginAsync(new());
                Assert.Equal(JsonValueKind.Null, (await test.StatusAsync(oldJar)).Reply.Json().GetProperty("displayName").ValueKind);
            }
            var disabled = await test.Owner.DisableAsync(test.Binding.Key, test.Binding.PrincipalId, test.Binding.Version);
            Assert.Equal(ExternalIdentityBindingOwnerOutcome.Disabled, disabled.Outcome);
            Assert.Equal(JsonValueKind.Null, (await test.StatusAsync(oldJar)).Reply.Json().GetProperty("displayName").ValueKind);
            test.Crypto.DisplayName = "Must not persist";
            var denied = await test.CompleteAsync(await test.FlowAsync(new()));
            Assert.NotEqual(HttpStatusCode.SeeOther, denied.Status);
            Assert.Null(await test.DisplayMetadata!.ReadAsync(test.Data.Seed.OrganizationA, test.Binding.BindingId, default));
        }, displayMetadata: true);
}
