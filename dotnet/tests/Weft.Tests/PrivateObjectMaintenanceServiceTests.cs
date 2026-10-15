using System.Text.Json;
using Weft.Application;
using Weft.Domain;
using static Weft.Tests.PrivateObjectMaintenanceContractFacts;

namespace Weft.Tests;

/// <summary>Synthetic port/metadata fixtures only; no real deletion, lease expiry or database acknowledgement is claimed.</summary>
public sealed class PrivateObjectMaintenanceServiceTests
{
    [Fact]
    public async Task EmptyKnownBatchFollowsAllThreeObservationsAndReportsActualZeros()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(new[] { "status", "storage", "match", "claim" }, ports.Calls);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, result.Outcome);
        Assert.True(result.ClaimAttempted);
        Assert.Equal(0, result.ClaimedCount);
        Assert.Equal(0, result.Observations.MetadataRetirementsExamined);
        Assert.Equal(0, result.Observations.FilesystemEntriesExamined);
        Assert.Equal(0, result.Observations.OwnershipUnprovedEntriesObserved);
        Assert.True(result.Observations.MetadataStatusPageComplete);
        Assert.True(result.Observations.FilesystemTraversalComplete);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public async Task ValidPreCanceledRunRetainsUnavailableEvidenceWithoutResolvingPorts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        var result = await ports.Service().RunOnceAsync(Project, cancellationToken: cancellation.Token);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Canceled, result.Outcome);
        Assert.Null(result.Failure);
        Assert.False(result.ClaimAttempted);
        Assert.Null(result.ClaimedCount);
        Assert.Null(result.Observations.MetadataRetirementsExamined);
        Assert.Null(result.Observations.FilesystemEntriesExamined);
        Assert.Null(result.Observations.FilesystemTraversalComplete);
        Assert.Null(result.ExhaustedRetirementsObserved);
        Assert.Empty(ports.Calls);
    }

    [Fact]
    public async Task ConstructorAndSeparateCursorScopeValidationPrecedeAnyPort()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        Assert.Throws<ArgumentNullException>(() => new PrivateObjectMaintenanceService(null!, ports, ports, ports, ports, Id(20)));
        Assert.Throws<ArgumentException>(() => ports.Service(instance: Guid.Empty));
        var foreign = new ProjectScope(Id(1), Id(99));
        var target = new ObjectCleanupTarget(Native(scope: new(Id(1), Id(99), Id(3), Id(4))), CleanupTargetKind.FinalObject);
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service().RunOnceAsync(Project,
            new ObjectCleanupScanCursor(foreign, ObjectCleanupScanStream.NativeStage, At, target)));
        await Assert.ThrowsAsync<ArgumentException>(() => ports.Service().RunOnceAsync(Project,
            statusAfter: new ObjectCleanupStatusCursor(foreign, Id(50))));
        Assert.Empty(ports.Calls);
    }

    [Theory]
    [InlineData("status", PrivateObjectMaintenanceRunFailure.MetadataUnavailable)]
    [InlineData("storage", PrivateObjectMaintenanceRunFailure.StorageUnavailable)]
    [InlineData("match", PrivateObjectMaintenanceRunFailure.MetadataUnavailable)]
    public async Task FailureRetainsOnlyCompletedEvidenceAndNeverClaims(string stage, PrivateObjectMaintenanceRunFailure expected)
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        var secret = new IOException("PRIVATE-root/credential must never be printed");
        if (stage == "status") ports.OnStatus = (_, _, _) => Task.FromException<ObjectCleanupStatusPage>(secret);
        if (stage == "storage") ports.OnObserve = (_, _) => Task.FromException<PrivateStorageObservationPage>(secret);
        if (stage == "match") ports.OnMatch = (_, _) => Task.FromException<PrivatePublicationObservationBatch>(secret);
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure);
        Assert.False(result.ClaimAttempted);
        Assert.Null(result.ClaimedCount);
        Assert.Equal(stage == "status" ? null : (int?)0, result.Observations.MetadataRetirementsExamined);
        Assert.Equal(stage == "match" ? (int?)0 : null, result.Observations.FilesystemEntriesExamined);
        Assert.Null(result.Observations.OwnershipUnprovedEntriesObserved);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("claim", ports.Calls);
    }

    [Fact]
    public async Task WholeObservationChangedHasNoPhysicalPageMatchOrClaim()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnObserve = (_, _) => Task.FromException<PrivateStorageObservationPage>(new PrivateStorageObservationChangedException())
        };
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.ObservationChanged, result.Failure);
        Assert.Null(result.Observations.FilesystemEntriesExamined);
        Assert.Null(result.Observations.FilesystemTraversalComplete);
        Assert.Equal(new[] { "status", "storage" }, ports.Calls);
    }

    [Fact]
    public async Task CombinedOwnershipCountsUnknownEntriesAndNonuniqueSlotsOnceButNotLayoutDirectories()
    {
        var first = MaintenanceObservationContractFacts.Final(2);
        var second = MaintenanceObservationContractFacts.Final(3, MaintenanceObservationContractFacts.Object(6),
            MaintenanceObservationContractFacts.Identity(file: new(43, 7, 8, 9)));
        var directory = new PrivateStorageEntryObservation(0, PrivateObjectClass.NativeArtifact, PrivateStorageEntryLevel.Mapping,
            null, null, new(39, 7, 8, 9), System.Text.Encoding.ASCII.GetBytes(Id(3).ToString("N")),
            PrivateStorageEntryKind.LayoutDirectory, new(new(40, 7, 8, 9), 1000, 0x41c0, 2, 4096), null, null);
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            Storage = new(Project, Id(10), Id(11), [directory, MaintenanceObservationContractFacts.Unknown(1),
                MaintenanceObservationContractFacts.Candidate(first), MaintenanceObservationContractFacts.Candidate(second)], true, true, false, 0),
            OnMatch = (request, _) => Task.FromResult(new PrivatePublicationObservationBatch(request, RetiredAt,
            [
                new(2, PublicationObservationOutcome.ExactlyOne, [MaintenanceObservationContractFacts.Witness()]),
                new(3, PublicationObservationOutcome.Ambiguous,
                [
                    MaintenanceObservationContractFacts.Witness(51, 61, second.ObjectId, second.ActualIdentity),
                    MaintenanceObservationContractFacts.Witness(52, 62, second.ObjectId, second.ActualIdentity)
                ])
            ]))
        };
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(4, result.Observations.FilesystemEntriesExamined);
        Assert.Equal(2, result.Observations.PublicationSlotsExamined);
        Assert.Equal(1, result.Observations.BoundPublicationMatchesObserved);
        Assert.Equal(2, result.Observations.OwnershipUnprovedEntriesObserved);
        Assert.True(result.Observations.FilesystemTraversalComplete);
    }

    [Fact]
    public async Task IndependentStatusAndCleanupCursorsAreForwardedAndRemainScopedInResult()
    {
        var cleanup = new ObjectCleanupScanCursor(Project, ObjectCleanupScanStream.NativeStage, At,
            new(Native(), CleanupTargetKind.FinalObject));
        var status = new ObjectCleanupStatusCursor(Project, Id(99));
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        ports.OnStatus = (project, after, _) =>
        {
            Assert.Equal(Project, project);
            Assert.Same(status, after);
            return Task.FromResult(ports.Statuses);
        };
        ports.OnClaim = (project, instance, after, _) =>
        {
            Assert.Equal(Project, project);
            Assert.Equal(Id(20), instance);
            Assert.Same(cleanup, after);
            return Task.FromResult(ports.Batch);
        };
        var result = await ports.Service().RunOnceAsync(Project, cleanup, status);
        Assert.Same(cleanup, result.CleanupAfter);
        Assert.Same(status, result.StatusAfter);
        Assert.True(result.Observations.MetadataStatusPageComplete); // selected suffix EOF only
        Assert.Null(result.NextCleanupCursor);
    }

    [Fact]
    public async Task ForeignOrSubstitutedObservedEvidenceStopsBeforeClaim()
    {
        var foreign = new ProjectScope(Id(1), Id(99));
        var badStatus = new PrivateObjectMaintenanceServiceTestPorts { Statuses = new(foreign, RetiredAt, []) };
        var badStorage = new PrivateObjectMaintenanceServiceTestPorts { Storage = new(foreign, Id(10), Id(11), [], true, true, false, 0) };
        var badMatch = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnMatch = (_, _) => Task.FromResult(new PrivatePublicationObservationBatch(
                new(Project, [PrivateStorageSlotObservation.Final(3, Native(), FileIdentity())]), RetiredAt,
                [new(3, PublicationObservationOutcome.NoMatch, [])]))
        };
        foreach (var ports in new[] { badStatus, badStorage, badMatch })
        {
            var result = await ports.Service().RunOnceAsync(Project);
            Assert.Equal(PrivateObjectMaintenanceRunFailure.InvalidEvidence, result.Failure);
            Assert.DoesNotContain("claim", ports.Calls);
            Assert.Empty(ports.Retirements);
        }
    }

    [Fact]
    public async Task OldStatusPageAtOrBeforeSeekDoesNotAuthorizeClaim()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        var target = new ObjectCleanupTarget(Native(), CleanupTargetKind.FinalObject);
        ports.Statuses = new(Project, RetiredAt, [new(Id(50), target, RetiredAt, 0, null, null, RetiredAt)]);
        var result = await ports.Service().RunOnceAsync(Project, statusAfter: new(Project, Id(50)));
        Assert.Equal(PrivateObjectMaintenanceRunFailure.InvalidEvidence, result.Failure);
        Assert.Null(result.Observations.MetadataRetirementsExamined);
        Assert.Equal(new[] { "status" }, ports.Calls);
    }

    [Theory]
    [InlineData(PrivateObjectRetirementOutcome.Deleted, PrivateObjectMaintenancePhysicalObservation.Deleted, ObjectCleanupDisposition.Deleted)]
    [InlineData(PrivateObjectRetirementOutcome.Missing, PrivateObjectMaintenancePhysicalObservation.Missing, ObjectCleanupDisposition.Missing)]
    [InlineData(PrivateObjectRetirementOutcome.Io, PrivateObjectMaintenancePhysicalObservation.Io, ObjectCleanupDisposition.StorageIo)]
    [InlineData(PrivateObjectRetirementOutcome.UnsafeStorage, PrivateObjectMaintenancePhysicalObservation.UnsafeStorage, ObjectCleanupDisposition.UnsafeStorage)]
    [InlineData(PrivateObjectRetirementOutcome.Busy, PrivateObjectMaintenancePhysicalObservation.Busy, null)]
    [InlineData(PrivateObjectRetirementOutcome.Unsupported, PrivateObjectMaintenancePhysicalObservation.Unsupported, null)]
    public async Task ActualReturnCategoriesForwardOriginalClaimAndOnlyMatchingDispositions(
        PrivateObjectRetirementOutcome actual, PrivateObjectMaintenancePhysicalObservation expected, ObjectCleanupDisposition? disposition)
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts { OnRetire = (_, _) => Task.FromResult(actual) };
        var claim = CleanupClaim();
        ports.SetClaims(claim);
        var result = await ports.Service().RunOnceAsync(Project);
        var row = Assert.Single(result.Targets);
        Assert.Same(claim, Assert.Single(ports.Retirements).Claim);
        Assert.Equal(expected, row.Physical);
        Assert.Equal(disposition, row.AcknowledgedDisposition);
        Assert.Equal(PrivateObjectMaintenanceRunOutcome.Finished, result.Outcome);
        if (disposition is not null)
        {
            var finish = Assert.Single(ports.Finishes);
            Assert.Same(claim, finish.Claim);
            Assert.Equal(disposition, finish.Disposition);
            Assert.Equal(ports.Retirements[0].Token, finish.Work);
            Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Applied, row.Acknowledgement);
        }
        else
        {
            Assert.Empty(ports.Finishes);
            Assert.Equal(PrivateObjectMaintenanceAcknowledgement.NotAttempted, row.Acknowledgement);
        }
        Assert.Empty(ports.Gets);
    }

    [Theory]
    [InlineData(ObjectCleanupFinishOutcome.ClaimLost, PrivateObjectMaintenanceAcknowledgement.ClaimLost)]
    [InlineData(ObjectCleanupFinishOutcome.SafetyRefusal, PrivateObjectMaintenanceAcknowledgement.SafetyRefusal)]
    public async Task KnownRefusalsNeverRetryDeleteOrReadAReplacement(ObjectCleanupFinishOutcome outcome,
        PrivateObjectMaintenanceAcknowledgement expected)
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts { OnFinish = _ => Task.FromResult(ObjectCleanupFinishResult.WithoutAttempt(outcome)) };
        ports.SetClaims(CleanupClaim());
        var row = Assert.Single((await ports.Service().RunOnceAsync(Project)).Targets);
        Assert.Equal(expected, row.Acknowledgement);
        Assert.Null(row.AcknowledgedDisposition);
        Assert.Single(ports.Retirements);
        Assert.Single(ports.Finishes);
        Assert.Empty(ports.Gets);
    }

    [Fact]
    public async Task UnknownClaimReplyAndWrongInstanceReturnNoPhysicalAuthority()
    {
        var lost = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnClaim = (_, _, _, _) => Task.FromException<ObjectCleanupClaimBatch>(new IOException("synthetic lost reply"))
        };
        var wrongInstance = new PrivateObjectMaintenanceServiceTestPorts();
        wrongInstance.SetClaims(CleanupClaim(instance: Id(99)));
        foreach (var ports in new[] { lost, wrongInstance })
        {
            var result = await ports.Service().RunOnceAsync(Project);
            Assert.True(result.ClaimAttempted);
            Assert.Null(result.ClaimedCount);
            Assert.Null(result.NextCleanupCursor);
            Assert.Empty(result.Targets);
            Assert.Empty(ports.Retirements);
        }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("token")]
    [InlineData("disposition")]
    public async Task RecoveryCannotAdoptAnotherClaimOrDifferentCompletion(string changed)
    {
        var claim = CleanupClaim();
        var other = changed == "generation" ? CleanupClaim(claim.Retirement, generation: 2) :
            changed == "token" ? CleanupClaim(claim.Retirement, token: Id(99)) : claim;
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnFinish = _ => Task.FromException<ObjectCleanupFinishResult>(new IOException("synthetic transport uncertainty")),
            OnGet = _ => Task.FromResult<ObjectCleanupAttempt?>(new(other, other.StartedAtUtc,
                changed == "disposition" ? ObjectCleanupDisposition.Missing : ObjectCleanupDisposition.Deleted))
        };
        ports.SetClaims(claim);
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.InvalidEvidence, result.Failure);
        Assert.Equal(PrivateObjectMaintenanceAcknowledgement.Unresolved, Assert.Single(result.Targets).Acknowledgement);
        Assert.Equal(0, result.AcknowledgedDeleted);
        Assert.Single(ports.Retirements);
        Assert.Single(ports.Finishes);
        var get = Assert.Single(ports.Gets);
        Assert.Equal(claim.Generation, get.Generation);
        Assert.Equal(claim.Retirement.RetirementId, get.Retirement);
        Assert.Equal(ports.Finishes[0].Settlement, get.Token);
    }

    [Fact]
    public async Task KnownInvalidFinishResponseIsNotTreatedAsAmbiguousAcknowledgement()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts
        {
            OnFinish = _ => Task.FromResult(ObjectCleanupFinishResult.ForAttempt(ObjectCleanupFinishOutcome.Applied,
                new(CleanupClaim(seed: 1), RetiredAt, ObjectCleanupDisposition.Deleted)))
        };
        ports.SetClaims(CleanupClaim());
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(PrivateObjectMaintenanceRunFailure.InvalidEvidence, result.Failure);
        Assert.Equal(1, result.PhysicalDeleted);
        Assert.Equal(0, result.AcknowledgedDeleted);
        Assert.Empty(ports.Gets);
    }

    [Fact]
    public async Task ExhaustionUnionCanContainOneHundredObservedAndSixteenNewAcknowledgedTargets()
    {
        var ports = new PrivateObjectMaintenanceServiceTestPorts { OnRetire = (_, _) => Task.FromResult(PrivateObjectRetirementOutcome.Io) };
        var observed = Enumerable.Range(0, 100).Select(seed => CleanupClaim(seed: seed, generation: 8)).ToArray();
        ports.Statuses = new(Project, RetiredAt.AddSeconds(60), observed.Select(claim => new ObjectCleanupStatusHeader(
            claim.Retirement.RetirementId, claim.Retirement.Target, claim.Retirement.RetiredAtUtc, 8,
            new(claim.AttemptId, 8, claim.StartedAtUtc, claim.LeaseExpiresAtUtc, null, null), null, RetiredAt.AddSeconds(60))));
        ports.SetClaims(Enumerable.Range(100, 16).Select(seed => CleanupClaim(seed: seed, generation: 8,
            started: RetiredAt.AddSeconds(61))).ToArray());
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Equal(100, result.Observations.ExhaustedRetirementsObserved);
        Assert.Equal(16, result.AcknowledgedExhausted);
        Assert.Equal(116, result.ExhaustedRetirementsObserved);
        Assert.Equal(0, result.AcknowledgedRetryable);
        Assert.False(result.Observations.MetadataStatusPageComplete);
        Assert.NotNull(result.Observations.NextStatusCursor);
    }

    [Fact]
    public void SyntheticOverlappingCounterEvidenceIsDeduplicatedWithoutClaimingLegalSqlHistory()
    {
        // Deliberately overlapping authored inputs test only the sanitized union arithmetic.
        // A real next Claim cannot reissue an already exhausted generation-eight retirement.
        var claim = CleanupClaim(generation: 8);
        var statuses = new ObjectCleanupStatusPage(Project, claim.LeaseExpiresAtUtc,
            [new(claim.Retirement.RetirementId, claim.Retirement.Target, claim.Retirement.RetiredAtUtc, 8,
                new(claim.AttemptId, 8, claim.StartedAtUtc, claim.LeaseExpiresAtUtc, null, null), null, claim.LeaseExpiresAtUtc)]);
        var storage = new PrivateStorageObservationPage(Project, Id(10), Id(11), [], true, true, false, 0);
        var matches = new PrivatePublicationObservationBatch(new(Project, []), RetiredAt, []);
        var summary = new PrivateObjectMaintenanceObservationSummary(statuses, storage, matches);
        var result = new PrivateObjectMaintenanceRunResult(Project, null, null, PrivateObjectMaintenanceRunOutcome.Finished,
            null, summary, true, new(Project, [claim], 1, 0, 0),
            [new(0, claim, PrivateObjectMaintenancePhysicalObservation.Io, PrivateObjectMaintenanceAcknowledgement.Applied, ObjectCleanupDisposition.StorageIo)]);
        Assert.Equal(1, summary.ExhaustedRetirementsObserved);
        Assert.Equal(1, result.AcknowledgedExhausted);
        Assert.Equal(1, result.ExhaustedRetirementsObserved);
    }

    [Fact]
    public async Task ResultRowsAreOwnedAndSerializationContainsNoClaimCredentialsOrRawEvidence()
    {
        var claim = CleanupClaim();
        var ports = new PrivateObjectMaintenanceServiceTestPorts();
        ports.SetClaims(claim);
        var result = await ports.Service().RunOnceAsync(Project);
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateObjectMaintenanceTargetResult>)result.Targets).Clear());
        Assert.Empty(typeof(PrivateObjectMaintenanceRunResult).GetConstructors());
        Assert.Empty(typeof(PrivateObjectMaintenanceTargetResult).GetConstructors());
        Assert.Empty(typeof(PrivateObjectMaintenanceObservationSummary).GetConstructors());
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(claim.LeaseToken.ToString("D"), json);
        Assert.DoesNotContain(claim.AttemptId.ToString("D"), json);
        Assert.DoesNotContain(claim.Retirement.Target.Object.ObjectId.ToString("D"), json);
        Assert.DoesNotContain("LeaseToken", json);
        Assert.DoesNotContain("RawName", json);
    }

    [Fact]
    public void InternalResultsCannotInventAdmissionWithoutCompleteSameProjectEvidence()
    {
        var statusOnly = new PrivateObjectMaintenanceObservationSummary(new(Project, RetiredAt, []), null, null);
        Assert.Throws<ArgumentException>(() => new PrivateObjectMaintenanceRunResult(Project, null, null,
            PrivateObjectMaintenanceRunOutcome.Failed, PrivateObjectMaintenanceRunFailure.MetadataUnavailable,
            statusOnly, true, null, []));
        var foreign = new ProjectScope(Id(1), Id(99));
        Assert.Throws<ArgumentException>(() => new PrivateObjectMaintenanceRunResult(foreign, null, null,
            PrivateObjectMaintenanceRunOutcome.Failed, PrivateObjectMaintenanceRunFailure.MetadataUnavailable,
            statusOnly, false, null, []));
        Assert.Throws<ArgumentException>(() => new PrivateObjectMaintenanceRunResult(Project, null, null,
            PrivateObjectMaintenanceRunOutcome.Finished, null, statusOnly, false, null, []));
    }
}
