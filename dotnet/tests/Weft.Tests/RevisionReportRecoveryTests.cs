using System.Runtime.Versioning;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

[Collection(PostgresCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class RevisionReportRecoveryTests(PostgresFixture fixture)
{
    [RevisionIngestionFact]
    public async Task ActualFinishCommitAcknowledgementLossResolvesTheExactCommittedUsableAttemptAfterOriginalCallSettles()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var pids = await test.AssertPoolsUnboundAsync();
        var actual = new NpgsqlConnectionStringBuilder(test.Case.DerivationConnectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(actual.Host!, actual.Port);
        await using var routedDatabase = new PostgresRevisionDerivationDatabase(relay.Route(actual.ConnectionString));
        var finish = new FinishOnlyStore(test.MetadataPort, new PostgresRevisionDerivationStore(routedDatabase));
        var recovery = new RecoveryReader(test.MetadataPort, finish);
        var service = test.NewService(reader: recovery, metadata: finish);
        var original = service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            AssertActualCommitFrames(relay);
            Assert.False(original.IsCompleted);
            Assert.False(finish.OriginalFinishSettled.IsCompleted);
            var claim = Assert.IsType<RevisionReportClaim>(finish.Claim);
            var completion = Assert.IsType<RevisionReportCompletion>(finish.Completion);
            Assert.True(completion.IsUsable);
            Assert.True(Assert.Single(test.BackendPort.Completions).IsUsable);

            // This independent direct login observes durable server facts while the routed
            // Finish call still cannot see CommandComplete or ReadyForQuery.
            var durable = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            AssertExactAttempt(claim, completion, durable);
            var expected = Assert.IsType<RevisionReportResult>(durable.Result);
            Assert.Equal(claim.CandidateObjectId, expected.DerivedObject.Id);
            Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
            var immutableFacts = await test.AttemptSnapshotAsync(claim.DerivationId);
            Assert.False(original.IsCompleted);

            relay.DisconnectCaller();
            var observed = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(await original));
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(finish.OriginalFinishSettled.IsCompletedSuccessfully);
            Assert.True(finish.OriginalFinishThrew);
            Assert.Equal(expected, observed.Metadata);
            Assert.Equal(1, finish.FinishCalls);
            Assert.NotEmpty(recovery.RetainedLookups);
            Assert.All(recovery.RetainedLookups, id => Assert.Equal(claim.DerivationId, id));
            Assert.Equal(0, recovery.CurrentAfterFinishCalls);
            Assert.Equal(immutableFacts, await test.AttemptSnapshotAsync(claim.DerivationId));
            Assert.Equal(source, await test.SourceSnapshotAsync());
            await test.AssertPoolsUnboundAsync(pids);

            var calls = test.BackendPort.Calls;
            var publications = test.StoragePort.PublishCalls;
            var nativeReads = test.StoragePort.NativeReads;
            var retained = RevisionReportTestSupport.Available(await test.Service.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            var cached = RevisionReportTestSupport.Available(await test.Service.EnsureAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection));
            Assert.Equal(expected, retained.Metadata);
            Assert.Equal(expected, cached.Metadata);
            Assert.Equal(calls, test.BackendPort.Calls);
            Assert.Equal(publications, test.StoragePort.PublishCalls);
            Assert.Equal(nativeReads, test.StoragePort.NativeReads);
            Assert.Equal(1, test.MetadataPort.ClaimCalls);
            Assert.Equal(0, test.MetadataPort.RepairCalls);
            Assert.Equal(new long[] { 1, 1, 1, 0 }, await test.CountsAsync());
        }
        finally
        {
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally { await DrainAsync(original); }
        }
    }

    [RevisionIngestionFact]
    public async Task ActualFailedFinishAcknowledgementLossResolvesRetainedFailureBeforeExactCurrentResponseShaping()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var actual = new NpgsqlConnectionStringBuilder(test.Case.DerivationConnectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(actual.Host!, actual.Port);
        await using var routedDatabase = new PostgresRevisionDerivationDatabase(relay.Route(actual.ConnectionString));
        var finish = new FinishOnlyStore(test.MetadataPort, new PostgresRevisionDerivationStore(routedDatabase));
        var recovery = new RecoveryReader(test.MetadataPort, finish);
        var policy = new RevisionReportPolicy(itemLimit: 1);
        var service = test.NewService(registry: test.NewRegistry(policy), reader: recovery, metadata: finish);
        var nativeCalls = test.Ingestion.Runner.NativeBodies.Count;
        var original = service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            AssertActualCommitFrames(relay);
            Assert.False(original.IsCompleted);
            Assert.False(finish.OriginalFinishSettled.IsCompleted);
            var claim = Assert.IsType<RevisionReportClaim>(finish.Claim);
            var completion = Assert.IsType<RevisionReportCompletion>(finish.Completion);
            var worker = Assert.Single(test.BackendPort.Completions);
            Assert.False(worker.IsUsable);
            Assert.Equal(RevisionReportFailureCategory.ResourceLimit, worker.Failure);
            Assert.Equal(nativeCalls + 1, test.Ingestion.Runner.NativeBodies.Count);
            Assert.Equal(test.NativeBytes, test.Ingestion.Runner.NativeBodies[^1]);
            Assert.False(completion.IsUsable);
            Assert.Equal(worker.Failure, completion.Failure);
            Assert.Equal(worker.Observation, completion.Observation);
            Assert.Equal(test.Key(RevisionReportOperation.Projection, policy), claim.Key);
            Assert.Equal(0, test.StoragePort.PublishCalls);
            var durable = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            AssertExactAttempt(claim, completion, durable);
            Assert.Null(durable.Result);
            var immutableFacts = await test.AttemptSnapshotAsync(claim.DerivationId);
            Assert.Equal(new long[] { 1, 1, 0, 1 }, await test.CountsAsync());

            relay.DisconnectCaller();
            var observed = await original;
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RevisionReportEnsureOutcome.Observed, observed.Outcome);
            Assert.Equal(RevisionReportReadOutcome.Observed, observed.Read!.Outcome);
            Assert.Equal(RevisionReportState.Failed, observed.Read.State);
            Assert.Equal(RevisionReportFailureCategory.ResourceLimit, observed.Read.Failure);
            Assert.Null(observed.Read.Report);
            Assert.Equal(claim.DerivationId, observed.Read.Snapshot!.CurrentAttempt!.DerivationId);
            Assert.Equal(completion, observed.Read.Snapshot.CurrentAttempt.Completion);
            Assert.Equal(durable.Attempt.FinishedAtUtc!.Value.AddSeconds(30), observed.NextAttemptAtUtc);
            Assert.True(finish.OriginalFinishThrew);
            Assert.Equal(1, finish.FinishCalls);
            Assert.NotEmpty(recovery.RetainedLookups);
            Assert.All(recovery.RetainedLookups, id => Assert.Equal(claim.DerivationId, id));
            Assert.True(recovery.CurrentAfterFinishCalls > 0);
            Assert.Equal(immutableFacts, await test.AttemptSnapshotAsync(claim.DerivationId));
            Assert.Equal(source, await test.SourceSnapshotAsync());
            Assert.Equal(1, test.BackendPort.Calls);
            Assert.Equal(0, test.MetadataPort.RepairCalls);
            await test.AssertPoolsUnboundAsync();
        }
        finally
        {
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally { await DrainAsync(original); }
        }
    }

    [RevisionIngestionFact]
    public async Task ActualCompetingRepairNeverSubstitutesCurrentBForAcknowledgedRetainedAWhoseObjectBecameMissing()
    {
        await using var test = await RevisionReportTestSupport.CreateAsync(fixture);
        var source = await test.SourceSnapshotAsync();
        var actual = new NpgsqlConnectionStringBuilder(test.Case.DerivationConnectionString);
        await using var relay = new RevisionReportCommitAckLossRelay(actual.Host!, actual.Port);
        await using var routedDatabase = new PostgresRevisionDerivationDatabase(relay.Route(actual.ConnectionString));
        var finish = new FinishOnlyStore(test.MetadataPort, new PostgresRevisionDerivationStore(routedDatabase));
        var recovery = new RecoveryReader(test.MetadataPort, finish);
        var service = test.NewService(reader: recovery, metadata: finish);
        var original = service.EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection);
        try
        {
            await relay.CommitAcknowledgementWithheld.WaitAsync(TimeSpan.FromSeconds(10));
            AssertActualCommitFrames(relay);
            var claim = Assert.IsType<RevisionReportClaim>(finish.Claim);
            var completion = Assert.IsType<RevisionReportCompletion>(finish.Completion);
            var durableA = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, claim.DerivationId));
            AssertExactAttempt(claim, completion, durableA);
            var resultA = Assert.IsType<RevisionReportResult>(durableA.Result);
            Assert.False(original.IsCompleted);

            // Delete only the actual owned derived object. Another independent service
            // proves Missing, performs the production full-reference Repair CAS, and really
            // publishes/finishes B while A's original caller still lacks its acknowledgement.
            File.Delete(test.PhysicalRoot.FinalPath(resultA.DerivedObject));
            var resultB = Assert.IsType<VerifiedProjectionReport>(RevisionReportTestSupport.Available(
                await test.NewService().EnsureAsync(test.Actor, test.Revision, RevisionReportOperation.Projection,
                    repairDerivationId: resultA.DerivationId)));
            Assert.NotEqual(resultA.DerivationId, resultB.Metadata.DerivationId);
            Assert.Equal(resultA.Generation + 1, resultB.Metadata.Generation);
            Assert.NotEqual(resultA.DerivedObject.Id, resultB.Metadata.DerivedObject.Id);
            Assert.Equal(2, test.BackendPort.Calls);
            Assert.Equal(1, test.MetadataPort.RepairCalls);
            Assert.False(original.IsCompleted);

            relay.DisconnectCaller();
            var resolved = await original;
            await relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RevisionReportEnsureOutcome.Observed, resolved.Outcome);
            Assert.Equal(RevisionReportReadOutcome.ObjectUnavailable, resolved.Read!.Outcome);
            Assert.Null(resolved.Read.Report);
            Assert.Equal(resultA, resolved.Read.SelectedResult);
            Assert.Equal(0, recovery.CurrentAfterFinishCalls);
            Assert.All(recovery.RetainedLookups, id => Assert.Equal(resultA.DerivationId, id));
            var currentB = RevisionReportTestSupport.Available(await test.Service.GetCurrentAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection));
            Assert.Equal(resultB.Metadata, currentB.Metadata);
            var retainedA = Assert.IsType<AuthorizedRevisionReportAttempt>(await test.Reader.GetRetainedAsync(test.Actor,
                test.Revision, RevisionReportOperation.Projection, resultA.DerivationId));
            AssertExactAttempt(claim, completion, retainedA);
            Assert.Equal(resultA, retainedA.Result);
            Assert.Equal(new long[] { 1, 2, 2, 0 }, await test.CountsAsync());
            Assert.Equal(source, await test.SourceSnapshotAsync());
            Assert.Equal(1, finish.FinishCalls);
            await test.AssertPoolsUnboundAsync();
        }
        finally
        {
            relay.DisconnectCaller();
            try { await relay.DisposeAsync(); }
            finally { await DrainAsync(original); }
        }
    }

    private static void AssertActualCommitFrames(RevisionReportCommitAckLossRelay relay)
    {
        Assert.True(relay.FrontendCommitSeen);
        Assert.True(relay.BackendCommitCompleteSeen);
        Assert.True(relay.BackendIdleReadySeen);
    }

    private static void AssertExactAttempt(RevisionReportClaim claim, RevisionReportCompletion completion,
        AuthorizedRevisionReportAttempt actual)
    {
        Assert.Equal(claim.Key.Source, actual.Source.Source);
        Assert.Equal(claim.Key, actual.Key);
        Assert.Equal(claim.CacheEntryId, actual.CacheEntryId);
        Assert.Equal(claim.DerivationId, actual.Attempt.DerivationId);
        Assert.Equal(claim.Generation, actual.Attempt.Generation);
        Assert.Equal(claim.StartedAtUtc, actual.Attempt.StartedAtUtc);
        Assert.Equal(claim.LeaseExpiresAtUtc, actual.Attempt.LeaseExpiresAtUtc);
        Assert.Equal(claim.RepairTargetDerivationId, actual.Attempt.RepairTargetDerivationId);
        Assert.NotNull(actual.Attempt.FinishedAtUtc);
        Assert.Equal(completion, actual.Attempt.Completion);
        if (completion.IsUsable)
        {
            var result = Assert.IsType<RevisionReportResult>(actual.Result);
            Assert.Equal(claim.OriginalActor, result.OriginalActor);
            Assert.Equal(claim.CandidateObjectId, result.DerivedObject.Id);
            Assert.Equal(completion.DerivedObject, result.DerivedObject);
            Assert.Equal(completion.Observation, result.Observation);
        }
        else Assert.Null(actual.Result);
    }

    /// <summary>Only Finish uses the transport relay; no Claim/Repair COMMIT can be mistaken for Finish.</summary>
    private sealed class FinishOnlyStore(IRevisionReportMetadataStore direct, IRevisionReportMetadataStore routed)
        : IRevisionReportMetadataStore
    {
        private readonly TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        internal RevisionReportClaim? Claim { get; private set; }
        internal RevisionReportCompletion? Completion { get; private set; }
        internal Task OriginalFinishSettled => settled.Task;
        internal bool OriginalFinishThrew { get; private set; }
        internal int FinishCalls => Volatile.Read(ref calls);
        public Task<RevisionReportStoreResult> TryClaimAsync(ActorContext actor, RevisionReportKey key,
            Guid serverInstanceId, CancellationToken cancellationToken = default) => direct.TryClaimAsync(actor, key, serverInstanceId, cancellationToken);
        public Task<RevisionReportStoreResult> RepairAsync(ActorContext actor, RevisionReportRepairTarget target,
            Guid serverInstanceId, CancellationToken cancellationToken = default) => direct.RepairAsync(actor, target, serverInstanceId, cancellationToken);
        public async Task<RevisionReportStoreResult> FinishAsync(ActorContext actor, RevisionReportClaim claim,
            RevisionReportCompletion completion, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1, Interlocked.Increment(ref calls));
            Claim = claim;
            Completion = completion;
            try { return await routed.FinishAsync(actor, claim, completion, cancellationToken); }
            catch { OriginalFinishThrew = true; throw; }
            finally { settled.TrySetResult(); }
        }
    }

    /// <summary>All reads are real; assertions distinguish acknowledgement resolution from response shaping.</summary>
    private sealed class RecoveryReader(IRevisionReportMetadataReader direct, FinishOnlyStore finish) : IRevisionReportMetadataReader
    {
        private readonly List<Guid> retainedLookups = [];
        private AuthorizedRevisionReportAttempt? acknowledged;
        internal IReadOnlyList<Guid> RetainedLookups => retainedLookups.AsReadOnly();
        internal int CurrentAfterFinishCalls { get; private set; }
        public Task<AuthorizedRevisionReportSource?> GetSourceAsync(ActorContext actor, RevisionScope scope,
            CancellationToken cancellationToken = default) => direct.GetSourceAsync(actor, scope, cancellationToken);
        public async Task<RevisionReportReadSnapshot?> GetCurrentAsync(ActorContext actor, RevisionReportKey key,
            CancellationToken cancellationToken = default)
        {
            var current = await direct.GetCurrentAsync(actor, key, cancellationToken);
            if (finish.OriginalFinishSettled.IsCompleted)
            {
                CurrentAfterFinishCalls++;
                var claim = Assert.IsType<RevisionReportClaim>(finish.Claim);
                var completion = Assert.IsType<RevisionReportCompletion>(finish.Completion);
                Assert.False(completion.IsUsable);
                Assert.NotNull(acknowledged);
                var shaped = Assert.IsType<RevisionReportReadSnapshot>(current);
                Assert.Equal(claim.Key, shaped.Key);
                Assert.Equal(claim.CacheEntryId, shaped.CacheEntryId);
                Assert.Equal(claim.Generation, shaped.Generation);
                Assert.Equal(acknowledged.Attempt, shaped.CurrentAttempt);
                Assert.Equal(completion, shaped.CurrentAttempt!.Completion);
            }
            return current;
        }
        public async Task<AuthorizedRevisionReportAttempt?> GetRetainedAsync(ActorContext actor, RevisionScope scope,
            RevisionReportOperation operation, Guid derivationId, CancellationToken cancellationToken = default)
        {
            Assert.True(finish.OriginalFinishSettled.IsCompletedSuccessfully);
            var claim = Assert.IsType<RevisionReportClaim>(finish.Claim);
            Assert.Equal(claim.Key.Scope, scope);
            Assert.Equal(claim.Key.Operation, operation);
            Assert.Equal(claim.DerivationId, derivationId);
            retainedLookups.Add(derivationId);
            var actual = await direct.GetRetainedAsync(actor, scope, operation, derivationId, cancellationToken);
            AssertExactAttempt(claim, Assert.IsType<RevisionReportCompletion>(finish.Completion), Assert.IsType<AuthorizedRevisionReportAttempt>(actual));
            acknowledged = actual;
            return actual;
        }
    }

    private static async Task DrainAsync(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }
}
