using System.Text;
using Npgsql;
using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Real PostgreSQL grants and invocation ledger over a registered target with a synthetic activated release.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class MachineInvocationPersistenceTests(PostgresFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("a67cb0f3-6f11-4e5e-921a-71c3b894fb32");

    [PostgresFact]
    public async Task Grants_are_administrative_one_way_and_retain_their_history()
    {
        await using var test = await Context.CreateAsync(fixture);
        Assert.Equal(InvocationGrantOutcome.Replayed, await test.Grants.GrantAsync(test.Actor, test.Grant));
        Assert.Equal(InvocationGrantOutcome.Conflict, await test.Grants.GrantAsync(test.Actor, new(test.Grant.GrantId, test.Target, Principal())));
        Assert.Equal(InvocationGrantOutcome.Conflict, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, test.Caller)));
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.RevokeAsync(test.Actor, test.Target, test.Grant.GrantId));
        Assert.Equal(InvocationGrantOutcome.Replayed, await test.Grants.RevokeAsync(test.Actor, test.Target, test.Grant.GrantId));
        // Replaying the original grant reports it without enabling it again.
        Assert.Equal(InvocationGrantOutcome.Replayed, await test.Grants.GrantAsync(test.Actor, test.Grant));
        Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.RevokeAsync(test.Actor, test.Target, Guid.NewGuid()));
        var renewed = new InvocationGrant(Guid.NewGuid(), test.Target, test.Caller);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, renewed));
        var history = await test.Grants.ReadAsync(test.Actor, test.Target);
        Assert.Equal([test.Grant.GrantId, renewed.GrantId], history.Select(item => item.GrantId));
        Assert.False(history[0].Active); Assert.Equal(test.Actor.PrincipalId, history[0].RevokedBy);
        Assert.True(history[1].Active); Assert.Equal(test.Caller, history[1].Principal);
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.invocation_grant_audit WHERE grant_id=$1", test.Grant.GrantId));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.invocation_grant_audit WHERE grant_id=$1", renewed.GrantId));
    }

    [PostgresFact]
    public async Task Only_a_current_administrator_changes_or_lists_grants()
    {
        await using var test = await Context.CreateAsync(fixture);
        foreach (var role in new[] { "publisher", "runner", "viewer" })
        {
            await test.Releases.Control.Runs.Cases.RoleAsync(role);
            Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, Principal())));
            Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.RevokeAsync(test.Actor, test.Target, test.Grant.GrantId));
            Assert.Empty(await test.Grants.ReadAsync(test.Actor, test.Target));
        }
        var outsider = new ActorContext(new(Guid.NewGuid()));
        Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.GrantAsync(outsider, new(Guid.NewGuid(), test.Target, Principal())));
        await test.Releases.Control.Runs.Cases.RoleAsync("administrator");
        Assert.Single(await test.Grants.ReadAsync(test.Actor, test.Target));
    }

    [PostgresFact]
    public async Task Disabled_or_foreign_targets_admit_no_new_grant_and_revocation_still_applies()
    {
        await using var test = await Context.CreateAsync(fixture);
        var reference = test.Target; var mapping = reference.Mapping;
        var foreign = new ReleaseTargetReference(mapping, new(new(mapping.OrganizationId, mapping.ProjectId), reference.Environment.EnvironmentId,
            reference.Environment.Version + 1), reference.TargetId);
        Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), foreign, Principal())));
        Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.GrantAsync(test.Actor,
            new(Guid.NewGuid(), new(mapping, reference.Environment, Guid.NewGuid()), Principal())));
        Assert.True(await test.Releases.Store.DisableAsync(test.Actor, reference));
        Assert.Equal(InvocationGrantOutcome.Refused, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), reference, Principal())));
        Assert.Equal(MachineInvocationClaimStatus.Refused, (await test.Claim(Guid.NewGuid())).Status);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.RevokeAsync(test.Actor, reference, test.Grant.GrantId));
    }

    [PostgresFact]
    public async Task A_completed_request_replays_its_retained_response_and_a_different_body_conflicts()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, claim.Status);
        Assert.Equal(1, claim.Attempt); Assert.Equal(test.Actor.PrincipalId, claim.Publisher);
        Assert.Equal(MachineInvocationClaimStatus.InProgress, (await test.Claim(request)).Status);
        var response = Encoding.UTF8.GetBytes("{\"outputs\":[]}");
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, claim, MachineInvocationOutcome.Completed, response));
        var replay = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Recorded, replay.Status);
        Assert.Equal(MachineInvocationOutcome.Completed, replay.Outcome);
        Assert.Equal(test.Release.ReleaseId, replay.ReleaseId);
        Assert.Equal(response, replay.RetainedResponse);
        Assert.Equal(MachineInvocationClaimStatus.Conflict, (await test.Claim(request, Digest("other body"))).Status);
        // The finished claim cannot record a second outcome.
        Assert.Equal(MachineInvocationFinishStatus.Superseded, await test.Finish(request, claim, MachineInvocationOutcome.Completed, response));
        // Audit holds digests and provenance only; the response is held separately for its retention period.
        var audit = await test.Case.ScalarAsync<string>("""
            SELECT jsonb_agg(jsonb_build_array(sequence,kind,outcome,release_id,build_id,artifact_digest,revision_id,output_digest,output_bytes) ORDER BY sequence)::text
            FROM braid.machine_invocation_audit WHERE request_id=$1
            """, request);
        Assert.Contains("\"claimed\", \"none\", null", audit, StringComparison.Ordinal);
        Assert.Contains(Sha256Digest.FromBytes(response).Value, audit, StringComparison.Ordinal);
        Assert.Contains(test.Release.Qualification.Artifact.Intent.BuildId.ToString("D"), audit, StringComparison.Ordinal);
        Assert.Contains(test.Release.Qualification.Artifact.Intent.Revision.RevisionId.ToString("D"), audit, StringComparison.Ordinal);
        Assert.True(await test.Case.ScalarAsync<bool>("SELECT expires_at=retained_at+interval '24 hours' FROM braid.machine_invocation_results WHERE request_id=$1", request));
        Assert.Equal(Digest("body").Value, await test.Case.ScalarAsync<string>("SELECT request_digest FROM braid.machine_invocations WHERE request_id=$1", request));
    }

    [PostgresFact]
    public async Task Released_attempts_retry_under_the_same_ID_and_recorded_failures_are_final()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var first = await test.Claim(request);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, first, MachineInvocationOutcome.Busy, null, provenance: false));
        var second = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, second.Status); Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.ClaimId, second.ClaimId);
        Assert.Equal(MachineInvocationFinishStatus.Superseded, await test.Finish(request, first, MachineInvocationOutcome.Unavailable, null));
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, second, MachineInvocationOutcome.InvalidInput, null));
        var replay = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Recorded, replay.Status);
        Assert.Equal(MachineInvocationOutcome.InvalidInput, replay.Outcome); Assert.Null(replay.RetainedResponse);
        Assert.Equal(4L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results WHERE request_id=$1", request));
    }

    [PostgresFact]
    public async Task An_expired_lease_is_claimed_again_and_the_abandoned_attempt_cannot_finish()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var abandoned = await test.Claim(request);
        await test.ExpireLeaseAsync(request);
        var retry = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, retry.Status); Assert.Equal(2, retry.Attempt);
        Assert.Equal(MachineInvocationFinishStatus.Superseded, await test.Finish(request, abandoned, MachineInvocationOutcome.Completed, [1]));
        Assert.Equal("interrupted", await test.Case.ScalarAsync<string>(
            "SELECT outcome FROM braid.machine_invocation_audit WHERE request_id=$1 AND sequence=2", request));
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, retry, MachineInvocationOutcome.Completed, [2]));
        Assert.Equal(new byte[] { 2 }, (await test.Claim(request)).RetainedResponse);
    }

    [RegressionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_first_claims_admit_one_attempt_and_report_progress_or_conflict(bool differentBody)
    {
        await using var test = await Context.CreateAsync(fixture);
        await using var other = new PostgresInvocationLedger(test.Case.RegressionConnectionString("braid_invocation_writer"));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.MigrationConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        // Hold both first claims at their insert, so both have already read that no row exists.
        await using var barrier = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE ONLY braid.machine_invocations IN SHARE MODE", connection, barrier))
            await command.ExecuteNonQueryAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var request = Guid.NewGuid();
        var first = test.Ledger.ClaimAsync(test.Key(request), test.Application, Digest("body"), deadline.Token);
        var second = other.ClaimAsync(test.Key(request), test.Application, Digest(differentBody ? "other body" : "body"), deadline.Token);
        var released = false;
        try
        {
            var waiting = 0L;
            for (var attempt = 0; attempt < 400 && waiting != 2; attempt++)
            {
                await using var count = new NpgsqlCommand("""
                    SELECT count(*) FROM pg_catalog.pg_locks
                    WHERE relation='braid.machine_invocations'::regclass AND mode='RowExclusiveLock' AND NOT granted
                    """, connection, barrier);
                waiting = (long)(await count.ExecuteScalarAsync(deadline.Token))!;
                if (waiting != 2) await Task.Delay(50, deadline.Token);
            }
            Assert.Equal(2L, waiting);
            await barrier.CommitAsync(deadline.Token);
            released = true;
            var outcomes = await Task.WhenAll(first, second);
            Assert.Single(outcomes, result => result.Status == MachineInvocationClaimStatus.Claimed);
            Assert.Single(outcomes, result => result.Status == (differentBody ? MachineInvocationClaimStatus.Conflict : MachineInvocationClaimStatus.InProgress));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocations"));
            Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
        }
        finally
        {
            if (!released) await barrier.RollbackAsync();
            await deadline.CancelAsync();
            try { await Task.WhenAll(first, second); }
            catch (Exception error) when (error is NpgsqlException or OperationCanceledException) { }
        }
    }

    [PostgresFact]
    public async Task An_expired_claim_cannot_record_or_retain_an_outcome_even_before_a_retry()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await test.Claim(request);
        await test.ExpireLeaseAsync(request);
        Assert.Equal(MachineInvocationFinishStatus.Superseded, await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1]));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results"));
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
        var retry = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, retry.Status); Assert.Equal(2, retry.Attempt);
    }

    [PostgresFact]
    public async Task Attempts_are_bounded_and_exhaustion_is_recorded()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        for (var attempt = 1; attempt <= MachineInvocationPolicy.MaximumAttempts; attempt++)
        {
            var claim = await test.Claim(request);
            Assert.Equal(attempt, claim.Attempt);
            Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(request, claim, MachineInvocationOutcome.ReleaseChanged, null, provenance: false));
        }
        var final = await test.Claim(request);
        Assert.Equal(MachineInvocationClaimStatus.Recorded, final.Status);
        Assert.Equal(MachineInvocationOutcome.AttemptsExhausted, final.Outcome);
        Assert.Equal(16L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
    }

    [PostgresFact]
    public async Task Revocation_during_an_attempt_delivers_nothing_and_refuses_the_next_call()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var done = await test.Claim(completed);
        Assert.Equal(MachineInvocationFinishStatus.Recorded, await test.Finish(completed, done, MachineInvocationOutcome.Completed, [7]));
        var claim = await test.Claim(request);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.RevokeAsync(test.Actor, test.Target, test.Grant.GrantId));
        Assert.Equal(MachineInvocationFinishStatus.Revoked, await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1]));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results WHERE request_id=$1", request));
        Assert.Equal(MachineInvocationClaimStatus.Refused, (await test.Claim(request)).Status);
        // Even the retained response of an earlier request is no longer delivered.
        Assert.Equal(MachineInvocationClaimStatus.Refused, (await test.Claim(completed)).Status);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, test.Caller)));
        Assert.Equal(MachineInvocationClaimStatus.Claimed, (await test.Claim(request)).Status);
    }

    [PostgresFact]
    public async Task Requests_are_scoped_to_their_caller_and_need_a_current_release()
    {
        await using var test = await Context.CreateAsync(fixture, activate: false);
        var request = Guid.NewGuid();
        Assert.Equal(MachineInvocationClaimStatus.NoRelease, (await test.Claim(request)).Status);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocations"));
        await test.ActivateAsync();
        var other = Principal();
        Assert.Equal(MachineInvocationClaimStatus.Refused, (await test.Claim(request, caller: other)).Status);
        Assert.Equal(InvocationGrantOutcome.Applied, await test.Grants.GrantAsync(test.Actor, new(Guid.NewGuid(), test.Target, other)));
        Assert.Equal(MachineInvocationClaimStatus.Claimed, (await test.Claim(request)).Status);
        Assert.Equal(MachineInvocationClaimStatus.Claimed, (await test.Claim(request, Digest("another caller's body"), other)).Status);
        Assert.Equal(MachineInvocationClaimStatus.Conflict, (await test.Claim(request, application: Guid.NewGuid())).Status);
    }

    [PostgresFact]
    public async Task Provenance_must_name_the_activated_release_and_its_exact_build()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await test.Claim(request);
        var real = MachineInvocationProvenance.From(test.Release);
        foreach (var forged in new[]
        {
            real with { ReleaseId = Guid.NewGuid() }, real with { BuildId = Guid.NewGuid() }, real with { RevisionId = Guid.NewGuid() },
            real with { ArtifactDigest = Digest("artifact") }, real with { BuildIntentDigest = Digest("intent") },
            real with { RuntimeImage = "sha256:" + new string('e', 64) }, real with { QualificationId = Guid.NewGuid() }
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => test.Ledger.FinishAsync(test.Key(request), claim.ClaimId, test.Application,
                MachineInvocationOutcome.Completed, forged, Clock, [1]));
        Assert.Equal(MachineInvocationClaimStatus.InProgress, (await test.Claim(request)).Status);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
    }

    [PostgresFact]
    public async Task Expired_responses_are_unreadable_and_purged_while_their_audit_remains()
    {
        await using var test = await Context.CreateAsync(fixture);
        var request = Guid.NewGuid();
        var claim = await test.Claim(request);
        await test.Finish(request, claim, MachineInvocationOutcome.Completed, [1, 2, 3]);
        Assert.Equal(0, await test.Ledger.PurgeExpiredAsync(test.Target, 10));
        await test.AgeResultAsync(request);
        var replay = await test.Claim(request);
        Assert.Equal(MachineInvocationOutcome.Completed, replay.Outcome); Assert.Null(replay.RetainedResponse);
        Assert.Equal(1, await test.Ledger.PurgeExpiredAsync(test.Target, 10));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_results"));
        Assert.Equal(2L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.machine_invocation_audit WHERE request_id=$1", request));
        Assert.Equal(MachineInvocationOutcome.Completed, (await test.Claim(request)).Outcome);
    }

    [RegressionPostgresTheory]
    [InlineData("invocation_grants")]
    [InlineData("invocation_grant_audit")]
    [InlineData("machine_invocations")]
    [InlineData("machine_invocation_audit")]
    [InlineData("machine_invocation_results")]
    [InlineData("host_order_pins")]
    [InlineData("host_target_slots")]
    public async Task History_cannot_be_rewritten_directly(string table)
    {
        await using var test = await Context.CreateAsync(fixture);
        var claim = await test.Claim(Guid.NewGuid());
        await test.Finish(claim, MachineInvocationOutcome.Completed, [1]);
        foreach (var statement in new[] { $"UPDATE braid.{table} SET organization_id=organization_id", $"DELETE FROM braid.{table}", $"TRUNCATE braid.{table} CASCADE" })
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => test.Case.ExecuteAsync(statement));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
        }
    }

    [RegressionPostgresTheory]
    [InlineData("table")]
    [InlineData("trigger")]
    [InlineData("function")]
    public async Task Broadened_database_contracts_are_refused(string change)
    {
        await using var test = await Context.CreateAsync(fixture);
        await test.Case.ExecuteAsync(change switch
        {
            "table" => "GRANT SELECT ON braid.machine_invocation_results TO braid_invocation_writer",
            "trigger" => "ALTER TABLE braid.machine_invocations DISABLE TRIGGER machine_invocation_history",
            _ => "GRANT EXECUTE ON FUNCTION braid.read_invocation_grants(uuid,uuid,uuid,uuid,bigint,uuid) TO braid_invocation_writer"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Ledger.PurgeExpiredAsync(test.Target, 1));
    }

    private const string Clock = "2026-10-22T12:00:00.0000000Z";
    private static Sha256Digest Digest(string value) => Sha256Digest.FromBytes(Encoding.UTF8.GetBytes(value));
    private static MachinePrincipal Principal() => new(MachinePrincipal.Entra, Tenant, Guid.NewGuid());

    internal sealed class Context : IAsyncDisposable
    {
        internal required LocalReleasePersistenceTests.Context Releases { get; init; }
        internal required PostgresInvocationGrantStore Grants { get; init; }
        internal required PostgresInvocationLedger Ledger { get; init; }
        internal required InvocationGrant Grant { get; init; }
        internal ReleaseIntent Release { get; private set; } = null!;
        internal Guid Application { get; } = Guid.NewGuid();
        internal MachinePrincipal Caller => Grant.Principal;
        internal ReleaseTargetReference Target => Releases.Target.Reference;
        internal ActorContext Actor => Releases.Actor;
        internal PostgresCase Case => Releases.Case;

        internal static async Task<Context> CreateAsync(PostgresFixture fixture, bool activate = true, int? migrations = null, bool documents = false, bool shipment = false)
        {
            var releases = await LocalReleasePersistenceTests.Context.CreateAsync(fixture, migrations, documents, shipment);
            PostgresInvocationGrantStore? grants = null; PostgresInvocationLedger? ledger = null;
            try
            {
                grants = new(releases.Case.RegressionConnectionString("braid_invocation_grant_writer"));
                ledger = new(releases.Case.RegressionConnectionString("braid_invocation_writer"));
                var grant = new InvocationGrant(Guid.NewGuid(), releases.Target.Reference, Principal());
                Assert.Equal(InvocationGrantOutcome.Applied, await grants.GrantAsync(releases.Actor, grant));
                var context = new Context { Releases = releases, Grants = grants, Ledger = ledger, Grant = grant };
                if (activate) await context.ActivateAsync();
                return context;
            }
            catch
            {
                if (ledger is not null) await ledger.DisposeAsync();
                if (grants is not null) await grants.DisposeAsync();
                await releases.DisposeAsync(); throw;
            }
        }

        internal async Task ActivateAsync()
        {
            var active = (await Releases.Store.ReadTargetAsync(Actor, Target))!.ActiveReleaseId;
            Release = await Releases.Request(Releases.First, active);
            Assert.Equal(ReleaseTransitionOutcome.Activated, await Releases.Apply(Release));
        }

        internal MachineInvocationKey Key(Guid request, MachinePrincipal? caller = null) => new(Target, caller ?? Caller, request);
        internal Task<MachineInvocationClaim> Claim(Guid request, Sha256Digest? digest = null, MachinePrincipal? caller = null, Guid? application = null) =>
            Ledger.ClaimAsync(Key(request, caller), application ?? Application, digest ?? Digest("body"));
        internal Task<MachineInvocationFinishStatus> Finish(Guid request, MachineInvocationClaim claim, MachineInvocationOutcome outcome, byte[]? response, bool provenance = true) =>
            Ledger.FinishAsync(Key(request), claim.ClaimId, Application, outcome, provenance ? MachineInvocationProvenance.From(Release) : null,
                provenance ? Clock : null, response);
        internal async Task Finish(MachineInvocationClaim claim, MachineInvocationOutcome outcome, byte[]? response)
        {
            var request = await Case.ScalarAsync<Guid>("SELECT request_id FROM braid.machine_invocations WHERE claim_id=$1", claim.ClaimId);
            Assert.Equal(MachineInvocationFinishStatus.Recorded, await Finish(request, claim, outcome, response));
        }

        // Time travel for tests only: the owner moves one lease or retention back, keeping every history invariant.
        internal Task ExpireLeaseAsync(Guid request) => Case.ExecuteAsync("""
            BEGIN;
            ALTER TABLE braid.machine_invocations DISABLE TRIGGER machine_invocation_guard;
            UPDATE braid.machine_invocations SET lease_until=created_at WHERE request_id=$1;
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE braid.machine_invocations ENABLE TRIGGER machine_invocation_guard;
            COMMIT;
            """.Replace("$1", "'" + request.ToString("D") + "'", StringComparison.Ordinal));
        internal Task AgeResultAsync(Guid request) => Case.ExecuteAsync("""
            BEGIN;
            ALTER TABLE braid.machine_invocation_results DISABLE TRIGGER machine_invocation_result_guard;
            UPDATE braid.machine_invocation_results SET retained_at=retained_at-interval '25 hours',expires_at=expires_at-interval '25 hours' WHERE request_id=$1;
            ALTER TABLE braid.machine_invocation_results ENABLE TRIGGER machine_invocation_result_guard;
            COMMIT;
            """.Replace("$1", "'" + request.ToString("D") + "'", StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {
            await Ledger.DisposeAsync(); await Grants.DisposeAsync(); await Releases.DisposeAsync();
        }
    }
}
