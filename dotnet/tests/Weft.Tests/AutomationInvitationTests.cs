using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

public sealed partial class AdministrationPersistenceTests
{
    [PostgresFact]
    public async Task Automation_org_only_invitation_is_recipient_only_without_project_subscription_or_email()
    {
        await using var data = await Data.CreateAsync(fixture, projectAdministrator: false);
        await using var notifications = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var secret = OrganizationInvitationSecret.Create(); var id = Guid.NewGuid();
        Assert.Equal("updated", await data.Invite(id, secret, Guid.NewGuid()));
        var inbox = await notifications.ReadInboxAsync(data.Actor, data.Org, null, 10);
        var entry = Assert.Single(inbox!.Value.GetProperty("notifications").EnumerateArray());
        Assert.Equal("invitation", entry.GetProperty("event").GetString());
        Assert.Equal(id, entry.GetProperty("resourceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("projectId").ValueKind);
        Assert.False(inbox.Value.GetRawText().Contains(secret, StringComparison.Ordinal));
        Assert.False(inbox.Value.GetRawText().Contains(OrganizationInvitationSecret.Hash(secret), StringComparison.Ordinal));
        Assert.Equal(0L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notification_deliveries"));
        var other = await data.AdmitAsync();
        var hidden = await notifications.ReadInboxAsync(new(new(other)), data.Org, null, 10);
        Assert.Empty(hidden!.Value.GetProperty("notifications").EnumerateArray());
        Assert.False(await notifications.MarkInboxReadAsync(new(new(other)), data.Org, entry.GetProperty("id").GetGuid()));
        Assert.True(await notifications.MarkInboxReadAsync(data.Actor, data.Org, entry.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task Automation_invitation_targets_the_verified_binding_and_first_enrollment_receipt()
    {
        await using var data = await Data.CreateAsync(fixture, projectAdministrator: false);
        await using var notifications = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        var principal = await data.AdmitAsync(); var oid = Guid.NewGuid();
        await data.Case.ExecuteAsync("INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'entra.oid',$2,$3,$4)",
            Guid.NewGuid(), Tenant.ToString("D"), oid.ToString("D"), principal);
        Assert.Equal("updated", await data.Invite(Guid.NewGuid(), OrganizationInvitationSecret.Create(), oid));
        var known = await notifications.ReadInboxAsync(new(new(principal)), data.Org, null, 10);
        Assert.Single(known!.Value.GetProperty("notifications").EnumerateArray());

        var newOid = Guid.NewGuid(); var secret = OrganizationInvitationSecret.Create();
        Assert.Equal("updated", await data.Invite(Guid.NewGuid(), secret, newOid));
        Assert.True(await data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Tenant, newOid), default));
        var enrolled = await data.Case.ScalarAsync<Guid>("SELECT principal_id FROM weft.external_identity_bindings WHERE provider='entra.oid' AND authority=$1 AND subject=$2", Tenant.ToString("D"), newOid.ToString("D"));
        var received = await notifications.ReadInboxAsync(new(new(enrolled)), data.Org, null, 10);
        Assert.Single(received!.Value.GetProperty("notifications").EnumerateArray());
        Assert.True(await data.Store.RedeemAsync(data.Org, secret, EntraUserIdentity.Key(Tenant, newOid), default));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.organization_notifications WHERE principal_id=$1", enrolled));
        await data.Case.ExecuteAsync("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", data.Org, enrolled);
        Assert.Null(await notifications.ReadInboxAsync(new(new(enrolled)), data.Org, null, 10));
    }

    [PostgresFact]
    public async Task Automation_global_inbox_rechecks_project_visibility_without_deleting_history()
    {
        await using var data = await Data.CreateAsync(fixture);
        var member = await data.AdmitAsync();
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, member, new { active = true, role = "runner" }, true));
        var actor = new ActorContext(new(member));
        await using var notifications = new PostgresAutomationStore(data.Case.RegressionConnectionString("weft_run_writer"), true);
        Assert.True(await notifications.WriteAsync(actor, new(data.Org, data.Project), "subscription", null,
            JsonSerializer.SerializeToElement(new { events = new[] { "revisionUploaded" }, inApp = true, email = false, webhooks = Array.Empty<string>() })));
        await data.Case.ExecuteAsync("SELECT weft.notification_emit($1,$2,'revisionUploaded',NULL,NULL,$3,NULL)", data.Org, data.Project, Guid.NewGuid());
        var before = await notifications.ReadInboxAsync(actor, data.Org, null, 10);
        var id = Assert.Single(before!.Value.GetProperty("notifications").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal("updated", await data.Write(AdministrationOperation.ProjectMember, member, new { active = false, role = "runner" }, true));
        var after = await notifications.ReadInboxAsync(actor, data.Org, null, 10);
        Assert.Empty(after!.Value.GetProperty("notifications").EnumerateArray());
        Assert.False(await notifications.MarkInboxReadAsync(actor, data.Org, id));
        Assert.Equal(1L, await data.Case.ScalarAsync<long>("SELECT count(*) FROM weft.notifications WHERE principal_id=$1", member));
    }
}
