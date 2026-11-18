using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Deployment;
using Braid.Deployment.Triggers;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Real PostgreSQL trigger configuration, admission, pinned ledger claims, atomic outbox finish and delivery claims.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TriggerPersistenceTests(PostgresFixture fixture)
{
    private const string Clock = "2026-11-07T12:00:00.0000000Z";

    [PostgresFact]
    public async Task The_trigger_contract_is_exact_and_admitted_with_the_invocation_ledger()
    {
        Assert.Equal(TriggerRoleGuard.MigrationChecksum, MigrationRunner.EmbeddedMigrations[49].Checksum);
        Assert.Equal(TriggerRoleGuard.ScheduleMigrationChecksum, MigrationRunner.EmbeddedMigrations[50].Checksum);
        Assert.Equal(TriggerRoleGuard.OperationsMigrationChecksum, MigrationRunner.EmbeddedMigrations[51].Checksum);
        await using (var schema = await fixture.CreateCaseAsync())
        {
            string fingerprint;
            await using (var owner = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(schema.MigrationConnectionString) { Pooling = false }.ConnectionString))
            {
                await owner.OpenAsync();
                await using var transaction = await owner.BeginTransactionAsync();
                fingerprint = await RegressionCaseSchemaContract.CaptureAsync(owner, transaction, TriggerRoleGuard.Tables, default);
            }
            Assert.True(fingerprint == TriggerRoleGuard.Fingerprint, "Trigger schema fingerprint: " + fingerprint);
        }
        await using var test = await Context.CreateAsync(fixture);
        Assert.Null(await test.Worker.ReadDefinitionAsync(test.Target, Guid.NewGuid()));
        Assert.Null(await test.Admin.ReadAsync(test.Actor, test.Target, Guid.NewGuid()));
        // A broadened worker or a tampered schema refuses every trigger and ledger path.
        await test.Case.ExecuteAsync("GRANT EXECUTE ON FUNCTION braid.operate_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,bytea) TO braid_trigger_worker");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Worker.ReadDefinitionAsync(test.Target, Guid.NewGuid()));
        await test.Case.ExecuteAsync("REVOKE EXECUTE ON FUNCTION braid.operate_trigger(uuid,uuid,uuid,uuid,bigint,uuid,uuid,uuid,text,bigint,bytea) FROM braid_trigger_worker");
        Assert.Null(await test.Worker.ReadDefinitionAsync(test.Target, Guid.NewGuid()));
        await test.Case.ExecuteAsync("ALTER TABLE braid.trigger_deliveries DISABLE TRIGGER trigger_delivery_guard");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Invocation.Ledger.PurgeExpiredAsync(test.Target, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Worker.ReadDefinitionAsync(test.Target, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task Configuration_is_versioned_idempotent_and_administrator_only()
    {
        await using var test = await Context.CreateAsync(fixture);
        var draft = test.Configuration(draft: true);
        var create = new TriggerOperation(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 0, draft);
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, create));
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, create));
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.RecoverAsync(test.Actor, test.Target, create));
        Assert.Equal(TriggerOperationOutcome.IdempotencyConflict, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(create.OperationId, test.TriggerId, TriggerAction.Configure, 0, test.Configuration())));
        Assert.Null(await test.Admin.RecoverAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Enable, 1, null)));
        var snapshot = (await test.Admin.ReadAsync(test.Actor, test.Target, test.TriggerId))!;
        Assert.Equal((1L, false, false, 1L), (snapshot.Version, snapshot.Enabled, snapshot.Retired, snapshot.Generation));
        Assert.Equal(draft.CopyCanonicalBytes(), snapshot.Configuration.CopyCanonicalBytes());
        // Missing explicit policies block enablement; nothing is filled in.
        Assert.Equal(TriggerOperationOutcome.Incomplete, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Enable, 1, null)));
        Assert.Equal(TriggerOperationOutcome.Stale, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 9, test.Configuration())));
        // The source binding identity survives edits; a different one is refused.
        Assert.Equal(TriggerOperationOutcome.Refused, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 1, test.Configuration(binding: Guid.NewGuid()))));
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 1, test.Configuration())));
        foreach (var role in new[] { "publisher", "runner", "viewer" })
        {
            await test.Invocation.Releases.Control.Runs.Cases.RoleAsync(role);
            Assert.Equal(TriggerOperationOutcome.Refused, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Enable, 2, null)));
        }
        await test.Invocation.Releases.Control.Runs.Cases.RoleAsync("publisher");
        Assert.Equal(2L, (await test.Admin.ReadAsync(test.Actor, test.Target, test.TriggerId))!.Version);
        await test.Invocation.Releases.Control.Runs.Cases.RoleAsync("viewer");
        Assert.Null(await test.Admin.ReadAsync(test.Actor, test.Target, test.TriggerId));
        await test.Invocation.Releases.Control.Runs.Cases.RoleAsync("administrator");
        // Enabling never creates a grant: without the configured caller's current grant it stays incomplete.
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Invocation.Grants.RevokeAsync(test.Actor, test.Target, test.Invocation.Grant.GrantId));
        Assert.Equal(TriggerOperationOutcome.Incomplete, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Enable, 2, null)));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.invocation_grants WHERE revoked_at IS NULL"));
        var retire = new TriggerOperation(Guid.NewGuid(), test.TriggerId, TriggerAction.Retire, 2, null);
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, retire));
        Assert.Equal(TriggerOperationOutcome.Refused, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 3, test.Configuration())));
        Assert.True((await test.Admin.ReadAsync(test.Actor, test.Target, test.TriggerId))!.Retired);
        Assert.Null(await test.Worker.ReadDefinitionAsync(test.Target, test.TriggerId));
    }

    [PostgresFact]
    public async Task Redelivery_reuses_the_original_work_and_changed_bytes_or_application_conflict()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var first = await test.AdmitAsync(definition, "Message-A", "{\"order\":1}");
        Assert.Equal(TriggerAdmissionStatus.Admitted, first.Status);
        var again = await test.AdmitAsync(definition, "Message-A", "{\"order\":1}");
        Assert.Equal((TriggerAdmissionStatus.Existing, first.WorkId), (again.Status, again.WorkId));
        Assert.Equal(TriggerAdmissionStatus.IdempotencyConflict, (await test.AdmitAsync(definition, "Message-A", "{\"order\":2}")).Status);
        // Broker IDs are exact and case-sensitive; another ID is another order.
        Assert.Equal(TriggerAdmissionStatus.Admitted, (await test.AdmitAsync(definition, "message-a", "{\"order\":1}")).Status);
        var otherApplication = new MachineCaller(test.Invocation.Caller, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(TriggerAdmissionStatus.IdempotencyConflict, (await test.Worker.AdmitAsync(definition, test.Identity("Message-A"), otherApplication,
            Encoding.UTF8.GetBytes("{\"order\":1}"), test.Digest(definition, "{\"order\":1}"))).Status);
        // A newer configuration version does not change the original work or its request.
        var request = await test.Case.ScalarAsync<Guid>("SELECT request_id FROM braid.trigger_work WHERE work_id=$1", first.WorkId!.Value);
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target,
            new(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 2, test.Configuration(parameter: "east"))));
        var newer = (await test.Worker.ReadDefinitionAsync(test.Target, test.TriggerId))!;
        Assert.Equal(2L, newer.Version);
        var replay = await test.AdmitAsync(newer, "Message-A", "{\"order\":1}");
        Assert.Equal((TriggerAdmissionStatus.Existing, first.WorkId), (replay.Status, replay.WorkId));
        Assert.Equal(request, await test.Case.ScalarAsync<Guid>("SELECT request_id FROM braid.trigger_work WHERE work_id=$1", first.WorkId.Value));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT configuration_version FROM braid.trigger_work WHERE work_id=$1", first.WorkId.Value));
        // The superseded configuration version admits nothing new.
        Assert.Equal(TriggerAdmissionStatus.Refused, (await test.AdmitAsync(definition, "Message-B", "{}")).Status);
        Assert.Equal(Sha256Digest.FromBytes(Encoding.UTF8.GetBytes("{\"order\":1}")).Value,
            await test.Case.ScalarAsync<string>("SELECT digest FROM braid.trigger_receipts WHERE work_id=$1 AND direction='input'", first.WorkId.Value));
    }

    [PostgresFact]
    public async Task Admission_needs_an_enabled_trigger_current_grant_release_and_room_under_explicit_caps()
    {
        await using var test = await Context.CreateAsync(fixture, pendingWork: 1, maximumMessageBytes: 16);
        var configured = new TriggerOperation(Guid.NewGuid(), test.TriggerId, TriggerAction.Configure, 0, test.Configuration());
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, configured));
        var definition = (await test.Worker.ReadDefinitionAsync(test.Target, test.TriggerId))!;
        Assert.Equal(TriggerAdmissionStatus.Refused, (await test.AdmitAsync(definition, "disabled", "{}")).Status);
        definition = await test.EnableAsync(created: true);
        Assert.Equal(TriggerAdmissionStatus.CapacityExceeded, (await test.AdmitAsync(definition, "large", "{\"too\":\"large!!\"}")).Status);
        Assert.Equal(TriggerAdmissionStatus.Admitted, (await test.AdmitAsync(definition, "first", "{}")).Status);
        Assert.Equal(TriggerAdmissionStatus.CapacityExceeded, (await test.AdmitAsync(definition, "second", "{}")).Status);
        Assert.Equal(TriggerAdmissionStatus.Existing, (await test.AdmitAsync(definition, "first", "{}")).Status);
        // Another principal is never admitted for this trigger, even with its own grant.
        var other = new MachinePrincipal(MachinePrincipal.Entra, test.Invocation.Caller.TenantId, Guid.NewGuid());
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Invocation.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, other)));
        await Assert.ThrowsAsync<ArgumentException>(() => test.Worker.AdmitAsync(definition, test.Identity("x"),
            new(other, test.Invocation.Application, DateTimeOffset.UtcNow.AddHours(1)), "{}"u8.ToArray(), test.Digest(definition, "{}")));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_work"));
    }

    [PostgresFact]
    public async Task Completion_commits_the_ledger_response_outputs_and_every_route_in_one_transaction()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var work = (await test.AdmitAsync(definition, "order-1", "{}")).WorkId!.Value;
        var claim = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        Assert.Equal(TriggerClaimStatus.Claimed, claim.Status);
        Assert.Equal(MachineInvocationProvenance.From(test.Invocation.Release), claim.Pin!.Provenance);
        Assert.Equal(test.Invocation.Release.Actor, claim.Pin.Publisher);
        Assert.Equal(test.Invocation.Grant.GrantId, claim.Pin.InvocationGrantId);
        Assert.Equal("{}"u8.ToArray(), claim.CopyInput());
        Assert.Equal(TriggerClaimStatus.InProgress, (await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller)).Status);
        // An output set that disagrees with the configured routes or the response is refused and writes nothing.
        var unrouted = Outputs(("named", "{\"x\":1}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.FinishAsync(work, claim, unrouted));
        var outputs = Outputs((null, "{\"ok\":true}"));
        var mismatched = MachineInvocationService.Encode(claim.RequestId, claim.Pin.Provenance, Clock, Outputs((null, "{\"ok\":false}")), documents: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Worker.FinishAndEnqueueAsync(test.Target, work, claim.ClaimId, test.Caller.ApplicationId,
            MachineInvocationOutcome.Completed, claim.Pin.Provenance, Clock, mismatched, outputs));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries"));
        Assert.Equal("running", await test.Case.ScalarAsync<string>("SELECT state FROM braid.machine_invocations"));
        Assert.Equal(TriggerFinishStatus.Recorded, await test.FinishAsync(work, claim, outputs));
        Assert.Equal("completed", await test.Case.ScalarAsync<string>("SELECT state FROM braid.machine_invocations WHERE request_id=$1", claim.RequestId));
        Assert.Equal("completed", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_work WHERE work_id=$1", work));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results WHERE request_id=$1", claim.RequestId));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE work_id=$1 AND phase='pending' AND sends=0", work));
        Assert.Equal(Sha256Digest.FromBytes("{\"ok\":true}"u8).Value, await test.Case.ScalarAsync<string>(
            "SELECT digest FROM braid.trigger_receipts WHERE work_id=$1 AND direction='output' AND output_name=''", work));
        // A redelivery recovers the durable outcome and never runs again.
        var replay = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        Assert.Equal((TriggerClaimStatus.Recorded, MachineInvocationOutcome.Completed), (replay.Status, replay.Outcome));
        Assert.Equal(TriggerFinishStatus.Superseded, await test.FinishAsync(work, claim, outputs));
        Assert.Equal(TriggerClaimStatus.Refused, (await test.Worker.ClaimPinnedAsync(test.Target, work,
            new(test.Invocation.Caller, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1)))).Status);
    }

    [PostgresFact]
    public async Task An_expired_attempt_is_reclaimed_under_the_same_pin_and_cannot_finish()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var work = (await test.AdmitAsync(definition, "order-1", "{}")).WorkId!.Value;
        var abandoned = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        await test.Invocation.ExpireLeaseAsync(abandoned.RequestId);
        var retry = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        Assert.Equal((TriggerClaimStatus.Claimed, 2, abandoned.RequestId, abandoned.Pin), (retry.Status, retry.Attempt, retry.RequestId, retry.Pin));
        Assert.Equal(TriggerFinishStatus.Superseded, await test.FinishAsync(work, abandoned, Outputs((null, "{}"))));
        Assert.Equal(TriggerFinishStatus.Recorded, await test.Worker.FinishAndEnqueueAsync(test.Target, work, retry.ClaimId, test.Caller.ApplicationId,
            MachineInvocationOutcome.Busy, null, null, null, null));
        Assert.Equal("admitted", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_work WHERE work_id=$1", work));
        var third = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        Assert.Equal(TriggerFinishStatus.Recorded, await test.Worker.FinishAndEnqueueAsync(test.Target, work, third.ClaimId, test.Caller.ApplicationId,
            MachineInvocationOutcome.InvalidInput, third.Pin!.Provenance, Clock, null, null));
        var final = await test.Worker.ClaimPinnedAsync(test.Target, work, test.Caller);
        Assert.Equal((TriggerClaimStatus.Recorded, MachineInvocationOutcome.InvalidInput), (final.Status, final.Outcome));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries"));
        Assert.Equal(6L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", abandoned.RequestId));
    }

    [PostgresFact]
    public async Task A_revoked_pinned_grant_fences_trigger_backlog_even_after_regrant_while_http_calls_resume()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var pending = (await test.AdmitAsync(definition, "pending", "{}")).WorkId!.Value;
        var running = (await test.AdmitAsync(definition, "running", "{}")).WorkId!.Value;
        var delivered = await test.CompletedWorkAsync(definition, "delivered");
        var claim = await test.Worker.ClaimPinnedAsync(test.Target, running, test.Caller);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Invocation.Grants.RevokeAsync(test.Actor, test.Target, test.Invocation.Grant.GrantId));
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Invocation.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, test.Invocation.Caller)));
        Assert.Equal(TriggerFinishStatus.Fenced, await test.FinishAsync(running, claim, Outputs((null, "{}"))));
        Assert.Equal(TriggerClaimStatus.Fenced, (await test.Worker.ClaimPinnedAsync(test.Target, pending, test.Caller)).Status);
        Assert.Equal(TriggerClaimStatus.Fenced, (await test.Worker.ClaimPinnedAsync(test.Target, running, test.Caller)).Status);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE work_id=$1", running));
        Assert.Equal(TriggerDeliveryClaimStatus.Settled, (await test.Worker.ClaimAsync(test.Target, test.Blob)).Status);
        Assert.Equal("fenced", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE work_id=$1 AND destination_binding_id=$2", delivered, test.Blob));
        // The established HTTP ledger still admits the regranted principal.
        Assert.Equal(MachineInvocationClaimStatus.Claimed, (await test.Invocation.Claim(Guid.NewGuid())).Status);
        // New orders pin the new grant.
        Assert.Equal(TriggerAdmissionStatus.Admitted, (await test.AdmitAsync(definition, "after", "{}")).Status);
    }

    [PostgresFact]
    public async Task Activation_supersedes_pending_work_and_held_outputs_and_disable_enable_revives_nothing()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var pending = (await test.AdmitAsync(definition, "pending", "{}")).WorkId!.Value;
        var delivered = await test.CompletedWorkAsync(definition, "delivered");
        var second = await test.Invocation.Releases.Build([4, 5]);
        var intent = await test.Invocation.Releases.Request(second, test.Invocation.Release.ReleaseId);
        Assert.Equal(ReleaseTransitionOutcome.Activated, await test.Invocation.Releases.Apply(intent));
        Assert.Equal(TriggerClaimStatus.ReleaseSuperseded, (await test.Worker.ClaimPinnedAsync(test.Target, pending, test.Caller)).Status);
        Assert.Equal(TriggerDeliveryClaimStatus.Settled, (await test.Worker.ClaimAsync(test.Target, test.Blob)).Status);
        Assert.Equal("superseded", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE work_id=$1 AND destination_binding_id=$2", delivered, test.Blob));
        // Work admitted now is pinned to the new release; a disable/enable cycle fences it permanently.
        var renewed = (await test.AdmitAsync(definition, "renewed", "{}")).WorkId!.Value;
        Assert.Equal(intent.ReleaseId, await test.Case.ScalarAsync<Guid>("SELECT release_id FROM braid.trigger_work WHERE work_id=$1", renewed));
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Disable, 2, null)));
        Assert.Equal(TriggerOperationOutcome.Unchanged, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Disable, 3, null)));
        Assert.Equal(TriggerOperationOutcome.Applied, await test.Admin.OperateAsync(test.Actor, test.Target, new(Guid.NewGuid(), test.TriggerId, TriggerAction.Enable, 3, null)));
        Assert.Equal(TriggerClaimStatus.Fenced, (await test.Worker.ClaimPinnedAsync(test.Target, renewed, test.Caller)).Status);
        Assert.Equal(TriggerDeliveryClaimStatus.Settled, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
    }

    [PostgresFact]
    public async Task Deliveries_carry_the_exact_bytes_and_a_late_observation_never_overrides_newer_state()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var work = await test.CompletedWorkAsync(definition, "order");
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, Guid.NewGuid())).Status);
        var claim = await test.Worker.ClaimAsync(test.Target, test.Https);
        Assert.Equal((TriggerDeliveryClaimStatus.Claimed, 1), (claim.Status, claim.Send));
        Assert.Equal("{\"ok\":true}"u8.ToArray(), claim.Delivery!.CopyContent());
        Assert.Equal("application/json", claim.Delivery.MediaType);
        Assert.Equal((TriggerDestinationKind.Https, "hook", 3L, (string?)null), (claim.Route!.Kind, claim.Route.Connector.ConnectorId, claim.Route.Connector.Version, claim.Route.OutputName));
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        Assert.Equal(TriggerObservationStatus.Late, await test.Worker.RecordObservationAsync(test.Target, claim.Delivery.DeliveryId, Guid.NewGuid(), DeliveryObservationKind.Accepted));
        Assert.Equal(TriggerObservationStatus.Recorded, await test.Worker.RecordObservationAsync(test.Target, claim.Delivery.DeliveryId, claim.ClaimId, DeliveryObservationKind.Accepted));
        Assert.Equal(TriggerObservationStatus.Late, await test.Worker.RecordObservationAsync(test.Target, claim.Delivery.DeliveryId, claim.ClaimId, DeliveryObservationKind.PermanentFailure));
        Assert.Equal("accepted", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", claim.Delivery.DeliveryId));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_events WHERE delivery_id=$1 AND kind='deliveryLate'", claim.Delivery.DeliveryId));
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        // The other route is independent: fan-out is per destination.
        Assert.Equal(TriggerDeliveryClaimStatus.Claimed, (await test.Worker.ClaimAsync(test.Target, test.Blob)).Status);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.trigger_deliveries WHERE work_id=$1 AND phase='sending'", work));
    }

    [PostgresFact]
    public async Task Delivery_claims_recheck_the_stored_publishers_current_project_role()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        var work = await test.CompletedWorkAsync(definition, "order");
        Assert.Equal(test.Actor.PrincipalId, test.Invocation.Release.Actor);
        await test.Invocation.Releases.Control.Runs.Cases.RoleAsync("viewer");
        Assert.Equal(TriggerDeliveryClaimStatus.Settled, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        Assert.Equal("fenced", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE work_id=$1 AND destination_binding_id=$2", work, test.Https));
        await test.Invocation.Releases.Control.Runs.Cases.RoleAsync("publisher");
        Assert.Equal(TriggerDeliveryClaimStatus.Claimed, (await test.Worker.ClaimAsync(test.Target, test.Blob)).Status);
    }

    [PostgresFact]
    public async Task Ambiguous_https_is_held_for_resolution_while_create_only_blob_retries_within_its_explicit_bounds()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        await test.CompletedWorkAsync(definition, "order");
        var https = await test.Worker.ClaimAsync(test.Target, test.Https);
        Assert.Equal(TriggerObservationStatus.Recorded, await test.Worker.RecordObservationAsync(test.Target, https.Delivery!.DeliveryId, https.ClaimId, DeliveryObservationKind.Unknown));
        Assert.Equal("unknown", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", https.Delivery.DeliveryId));
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        var blob = await test.Worker.ClaimAsync(test.Target, test.Blob);
        Assert.Equal(TriggerObservationStatus.Recorded, await test.Worker.RecordObservationAsync(test.Target, blob.Delivery!.DeliveryId, blob.ClaimId, DeliveryObservationKind.Unknown));
        await Task.Delay(50);
        var repeat = await test.Worker.ClaimAsync(test.Target, test.Blob);
        Assert.Equal((TriggerDeliveryClaimStatus.Claimed, blob.Delivery.DeliveryId, 2), (repeat.Status, repeat.Delivery!.DeliveryId, repeat.Send));
        Assert.True(repeat.Delivery.Matches(blob.Delivery));
        // The explicit two-send budget is exhausted by a proven non-acceptance; it never loops.
        Assert.Equal(TriggerObservationStatus.Recorded, await test.Worker.RecordObservationAsync(test.Target, repeat.Delivery.DeliveryId, repeat.ClaimId, DeliveryObservationKind.ProvenKnownNotAccepted));
        Assert.Equal("exhausted", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", blob.Delivery.DeliveryId));
        Assert.NotNull(await test.Case.ScalarAsync<DateTime?>("SELECT first_ambiguous_at FROM braid.trigger_deliveries WHERE delivery_id=$1", blob.Delivery.DeliveryId));
    }

    [PostgresFact]
    public async Task An_expired_send_without_an_observation_becomes_unknown_never_a_fresh_https_send()
    {
        await using var test = await Context.CreateAsync(fixture);
        var definition = await test.EnableAsync();
        await test.CompletedWorkAsync(definition, "order");
        var https = await test.Worker.ClaimAsync(test.Target, test.Https);
        await test.ExpireSendAsync(https.Delivery!.DeliveryId);
        Assert.Equal(TriggerDeliveryClaimStatus.None, (await test.Worker.ClaimAsync(test.Target, test.Https)).Status);
        Assert.Equal("unknown", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", https.Delivery.DeliveryId));
        Assert.Equal(TriggerObservationStatus.Late, await test.Worker.RecordObservationAsync(test.Target, https.Delivery.DeliveryId, https.ClaimId, DeliveryObservationKind.Accepted));
        Assert.Equal("unknown", await test.Case.ScalarAsync<string>("SELECT phase FROM braid.trigger_deliveries WHERE delivery_id=$1", https.Delivery.DeliveryId));
        var blob = await test.Worker.ClaimAsync(test.Target, test.Blob);
        await test.ExpireSendAsync(blob.Delivery!.DeliveryId);
        var resend = await test.Worker.ClaimAsync(test.Target, test.Blob);
        Assert.Equal((TriggerDeliveryClaimStatus.Claimed, 2), (resend.Status, resend.Send));
    }

    [RegressionPostgresTheory]
    [InlineData("trigger_controls")]
    [InlineData("trigger_configurations")]
    [InlineData("trigger_operations")]
    [InlineData("trigger_work")]
    [InlineData("trigger_receipts")]
    [InlineData("trigger_payloads")]
    [InlineData("trigger_deliveries")]
    [InlineData("trigger_events")]
    public async Task Trigger_history_cannot_be_rewritten_directly(string table)
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.CompletedWorkAsync(await test.EnableAsync(), "order");
        foreach (var statement in new[] { $"UPDATE braid.{table} SET organization_id=organization_id", $"DELETE FROM braid.{table}", $"TRUNCATE braid.{table} CASCADE" })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => test.Case.ExecuteAsync(statement));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
        }
    }

    private static RegressionOutputSet Outputs(params (string? Name, string Content)[] values) => RegressionOutputSet.Capture(values.Select(value =>
        RegressionOutput.Capture(value.Name is null ? RegressionOutputKey.Primary : RegressionOutputKey.Named(value.Name), Encoding.UTF8.GetBytes(value.Content),
            mediaType: "application/json")).ToArray());

    internal sealed class Context : IAsyncDisposable
    {
        private readonly int pendingWork;
        private readonly int maximumMessageBytes;
        private Context(int pendingWork, int maximumMessageBytes) { this.pendingWork = pendingWork; this.maximumMessageBytes = maximumMessageBytes; }
        internal required MachineInvocationPersistenceTests.Context Invocation { get; init; }
        internal required PostgresTriggerAdministrationStore Admin { get; init; }
        internal required PostgresTriggerWorkStore Worker { get; init; }
        internal Guid TriggerId { get; } = Guid.NewGuid();
        internal Guid Binding { get; } = Guid.NewGuid();
        internal Guid Blob { get; } = Guid.NewGuid();
        internal Guid Https { get; } = Guid.NewGuid();
        internal ReleaseTargetReference Target => Invocation.Target;
        internal ActorContext Actor => Invocation.Actor;
        internal PostgresCase Case => Invocation.Case;
        internal MachineCaller Caller => new(Invocation.Caller, Invocation.Application, DateTimeOffset.UtcNow.AddHours(1));

        internal static async Task<Context> CreateAsync(PostgresFixture fixture, int pendingWork = 100, int maximumMessageBytes = 64 * 1024, int? migrations = null)
        {
            var invocation = await MachineInvocationPersistenceTests.Context.CreateAsync(fixture, migrations: migrations);
            PostgresTriggerAdministrationStore? admin = null;
            try
            {
                admin = new(invocation.Case.RegressionConnectionString("braid_release_writer"));
                return new(pendingWork, maximumMessageBytes)
                {
                    Invocation = invocation,
                    Admin = admin,
                    Worker = new(invocation.Case.RegressionConnectionString(TriggerRoleGuard.Role))
                };
            }
            catch { if (admin is not null) await admin.DisposeAsync(); await invocation.DisposeAsync(); throw; }
        }

        internal TriggerConfiguration Configuration(bool draft = false, Guid? binding = null, string parameter = "west") =>
            new(TriggerId, Target, TriggerKind.ServiceBusQueue, binding ?? Binding, new("orders-in", 1), Invocation.Caller, Invocation.Application, "application/json",
                [new("region", PreviewParameterKind.String, parameter)],
                [new(null, Blob, TriggerDestinationKind.Blob, new("blob-out", 2)), new(null, Https, TriggerDestinationKind.Https, new("hook", 3))],
                draft ? null : new(new DeliveryRetryPolicy(2, [TimeSpan.FromMilliseconds(1)], TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)),
                    TimeSpan.FromSeconds(60), maximumMessageBytes, TimeSpan.FromHours(2), pendingWork, 1024 * 1024));

        internal async Task<TriggerDefinition> EnableAsync(bool created = false)
        {
            if (!created)
                Assert.Equal(TriggerOperationOutcome.Applied, await Admin.OperateAsync(Actor, Target, new(Guid.NewGuid(), TriggerId, TriggerAction.Configure, 0, Configuration())));
            Assert.Equal(TriggerOperationOutcome.Applied, await Admin.OperateAsync(Actor, Target, new(Guid.NewGuid(), TriggerId, TriggerAction.Enable, 1, null)));
            return (await Worker.ReadDefinitionAsync(Target, TriggerId))!;
        }

        internal QueueOrderIdentity Identity(string message) => new(Binding, message, Target, Invocation.Caller, QueueOrderKind.SingleOrder, null);
        internal Sha256Digest Digest(TriggerDefinition definition, string body) => new MachineInvocationRequest(new(null, Encoding.UTF8.GetBytes(body)), [],
            definition.Configuration.Parameters, [new(null, definition.Configuration.InputMediaType)]).Digest;
        internal Task<TriggerAdmissionResult> AdmitAsync(TriggerDefinition definition, string message, string body) =>
            Worker.AdmitAsync(definition, Identity(message), Caller, Encoding.UTF8.GetBytes(body), Digest(definition, body));

        internal Task<TriggerFinishStatus> FinishAsync(Guid work, TriggerWorkClaim claim, RegressionOutputSet outputs) =>
            Worker.FinishAndEnqueueAsync(Target, work, claim.ClaimId, Caller.ApplicationId, MachineInvocationOutcome.Completed, claim.Pin!.Provenance, Clock,
                MachineInvocationService.Encode(claim.RequestId, claim.Pin.Provenance, Clock, outputs, documents: true), outputs);

        internal async Task<Guid> CompletedWorkAsync(TriggerDefinition definition, string message)
        {
            var work = (await AdmitAsync(definition, message, "{}")).WorkId!.Value;
            var claim = await Worker.ClaimPinnedAsync(Target, work, Caller);
            Assert.Equal(TriggerFinishStatus.Recorded, await FinishAsync(work, claim, Outputs((null, "{\"ok\":true}"))));
            return work;
        }

        // Time travel for tests only: the owner ends one send claim's window, keeping every history invariant.
        internal Task ExpireSendAsync(Guid delivery) => Case.ExecuteAsync("""
            BEGIN;
            ALTER TABLE braid.trigger_deliveries DISABLE TRIGGER trigger_delivery_guard;
            UPDATE braid.trigger_deliveries SET claim_expires_at=clock_timestamp()-interval '1 second' WHERE delivery_id=$1;
            ALTER TABLE braid.trigger_deliveries ENABLE TRIGGER trigger_delivery_guard;
            COMMIT;
            """.Replace("$1", "'" + delivery.ToString("D") + "'", StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync(); await Admin.DisposeAsync(); await Invocation.DisposeAsync();
        }
    }
}
