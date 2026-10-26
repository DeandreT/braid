using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Weft.Application;
using Weft.Contracts.Projections;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class AdministrationEnrollmentHttpsTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Bodyless_invitation_revocation_works_through_the_actual_browser_and_bearer_boundary(bool bearer) =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            _ = await InviteAsync(test);
            var org = test.Data.Seed.OrganizationA;
            var store = test.First.Services.GetRequiredService<IAdministrationStore>();
            var snapshot = (await store.ReadOrganizationAsync(test.Data.Actor, org, default))!;
            var path = $"/api/v1/organizations/{org}/administration/invitations/{Assert.Single(snapshot.Invitations).InvitationId}/revoke";
            AuthHttpJar? jar = bearer ? null : await test.LoginAsync();
            var epoch = jar is null ? null : (await test.StatusAsync(jar)).Epoch;
            var token = bearer ? test.Crypto.SignClaims(UserTokenValidationTests.Claims().ToJsonString(), EntraProtocolTokenCase.Good) : null;
            var response = await test.SendAsync(path, jar, "POST", epoch: epoch, mutate: request =>
            {
                request.Headers.Add("If-Match", $"\"admin-{org}-{snapshot.Version}\"");
                if (token is not null) { request.Headers.Remove("Origin"); request.Headers.Add("Authorization", "Bearer " + token); }
            });
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal("updated", response.Json().GetProperty("outcome").GetString());
            Assert.Equal(1L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.administration_audit WHERE operation='inviteRevoke'"));
        }, bearerBurst: 100, administration: true);

    [PostgresFact]
    public Task Principal_disable_refuses_the_existing_session_and_bearer_on_the_next_request() => DeactivateAsync(false);

    [PostgresFact]
    public Task Binding_disable_refuses_the_existing_session_and_bearer_on_the_next_request() => DeactivateAsync(true);

    private Task DeactivateAsync(bool binding) => AuthHttpFixture.RunAsync(fixture, async test =>
    {
        var flow = await test.FlowAsync(invitation: await InviteAsync(test));
        Assert.Equal(HttpStatusCode.SeeOther, (await test.CompleteAsync(flow)).Status);
        var store = test.First.Services.GetRequiredService<IAdministrationStore>();
        var snapshot = (await store.ReadOrganizationAsync(test.Data.Actor, test.Data.Seed.OrganizationA, default))!;
        var member = snapshot.Members.Single(m => m.Bindings.Any(b => b.Subject == EntraProtocolFixturePolicy.UserKey.Subject));
        Assert.Equal("updated", await store.WriteAsync(test.Data.Actor, test.Data.Seed.OrganizationA, null, snapshot.Version,
            binding ? AdministrationOperation.BindingDisable : AdministrationOperation.PrincipalDisable,
            binding ? member.Bindings.Single().BindingId : member.PrincipalId, JsonSerializer.SerializeToElement(new { }), default));
        Assert.Equal("authenticationRequired", (await test.StatusAsync(flow.Jar)).State);
        var bearer = test.Crypto.SignClaims(UserTokenValidationTests.Claims().ToJsonString(), EntraProtocolTokenCase.Good);
        var refused = await test.SendAsync(AuthHttpFixture.ContextPath, new(), mutate: request => request.Headers.Add("Authorization", "Bearer " + bearer));
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal(0, refused.Record.CatalogReads);
    }, bindIssuer: false, bearerBurst: 100, administration: true);

    [PostgresFact]
    public Task Verified_unbound_login_redeems_the_correlated_invitation_before_fresh_session_issue() =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            var secret = await InviteAsync(test);
            var flow = await test.FlowAsync(invitation: secret);
            var reply = await test.CompleteAsync(flow);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.SeeOther && reply.Record.Creates == 1, "Invitation enrollment did not issue one acknowledged session.");
            var status = await test.StatusAsync(flow.Jar);
            AuthHttpFixture.Check(status.State == "authenticated", "Redeemed identity did not admit its fresh session.");
            var key = EntraProtocolFixturePolicy.UserKey;
            var principal = await test.Data.Case.ScalarAsync<Guid>("SELECT principal_id FROM weft.external_identity_bindings WHERE provider=$1 AND authority=$2 AND subject=$3", key.Provider, key.Authority, key.Subject);
            Assert.NotEqual(test.Data.Actor.PrincipalId.Value, principal);
            Assert.True(await test.Data.Case.ScalarAsync<bool>("SELECT active FROM weft.organization_memberships WHERE organization_id=$1 AND principal_id=$2", test.Data.Seed.OrganizationA, principal));
            Assert.Equal(0L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.project_memberships WHERE principal_id=$1", principal));
            Assert.Equal(1L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.administration_audit WHERE operation='inviteRedeem'"));
        }, bindIssuer: false, administration: true);

    [PostgresFact]
    public Task Ordinary_sign_in_preserves_the_validated_return_path_through_ticket_completion() =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            const string target = "/help/reports#coverage";
            var flow = await test.FlowAsync(returnTo: target);
            var reply = await test.CompleteAsync(flow);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.SeeOther, "Ordinary callback did not complete.");
            AuthHttpFixture.Check(reply.Headers.TryGetValue("Location", out var location) && location.SequenceEqual([target]), "Ordinary callback lost its protected return path.");
            AuthHttpFixture.Check((await test.StatusAsync(flow.Jar)).State == "authenticated", "Ordinary callback did not issue its session.");
        });

    [PostgresFact]
    public Task Invitation_return_path_and_display_metadata_stay_bound_to_the_verified_login() =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            test.Crypto.DisplayName = "Invited <member>";
            var flow = await test.FlowAsync(invitation: await InviteAsync(test), returnTo: "/administration");
            var reply = await test.CompleteAsync(flow);
            AuthHttpFixture.Check(reply.Status == HttpStatusCode.SeeOther, "Invitation callback did not complete.");
            AuthHttpFixture.Check(reply.Headers.TryGetValue("Location", out var location) && location.SequenceEqual(["/administration"]), "Invitation callback lost its protected return path.");
            var status = await test.StatusAsync(flow.Jar);
            AuthHttpFixture.Check(status.State == "authenticated", "Invitation session was not admitted.");
            AuthHttpFixture.Check(status.Reply.Json().GetProperty("displayName").GetString() == "Invited <member>", "Invitation display metadata was not retained.");
            var key = EntraProtocolFixturePolicy.UserKey;
            var principal = await test.Data.Case.ScalarAsync<Guid>("SELECT principal_id FROM weft.external_identity_bindings WHERE provider=$1 AND authority=$2 AND subject=$3", key.Provider, key.Authority, key.Subject);
            AuthHttpFixture.Check(principal != test.Data.Actor.PrincipalId.Value, "Invitation replaced the inviter identity.");
            AuthHttpFixture.Check(await test.Data.Case.ScalarAsync<string>("SELECT coalesce((SELECT role FROM weft.organization_administration_roles WHERE organization_id=$1 AND principal_id=$2),'member')", test.Data.Seed.OrganizationA, principal) == "member", "Invitation unexpectedly granted an administrative role.");
            AuthHttpFixture.Check(await test.First.Services.GetRequiredService<IAdministrationStore>().ReadOrganizationAsync(new(new(principal)), test.Data.Seed.OrganizationA, default) is null, "Invitation return path granted administration access.");
        }, bindIssuer: false, administration: true, displayMetadata: true);

    [PostgresFact]
    public Task Revocation_between_challenge_and_verified_callback_refuses_enrollment_and_session() =>
        AuthHttpFixture.RunAsync(fixture, async test =>
        {
            var secret = await InviteAsync(test);
            var flow = await test.FlowAsync(invitation: secret);
            await test.Data.Case.ExecuteAsync("UPDATE weft.organization_invitations SET revoked_at=clock_timestamp()");
            var reply = await test.CompleteAsync(flow);
            Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            Assert.Equal(0, reply.Record.Creates);
            Assert.Equal(0L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.external_identity_bindings"));
            Assert.Equal(0L, await test.Data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.account_display_metadata"));
        }, bindIssuer: false, administration: true, displayMetadata: true);

    internal static async Task<string> InviteAsync(AuthHttpFixture test)
    {
        var store = test.First.Services.GetRequiredService<IAdministrationStore>();
        var org = test.Data.Seed.OrganizationA;
        var snapshot = await store.ReadOrganizationAsync(test.Data.Actor, org, default);
        var secret = OrganizationInvitationSecret.Create();
        Assert.Equal("updated", await store.WriteAsync(test.Data.Actor, org, null, snapshot!.Version, AdministrationOperation.Invite,
            Guid.NewGuid(), JsonSerializer.SerializeToElement(new
            {
                tenantId = EntraProtocolFixturePolicy.Tenant,
                expectedObjectId = Guid.Parse(EntraProtocolFixturePolicy.UserKey.Subject),
                projectRoles = Array.Empty<object>(),
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                secretHash = OrganizationInvitationSecret.Hash(secret)
            }, ProjectionJson.CreateOptions()), default));
        return secret;
    }
}
