using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Projections;
using Weft.Domain;
using Ports = Weft.Tests.RevisionReportServiceTestPorts;

namespace Weft.Tests;

/// <summary>
/// Pure orchestration checks against explicitly synthetic metadata and object ports.
/// These do not qualify real database authority, native execution, filesystem durability or transport loss.
/// </summary>
public sealed class RevisionReportServiceTests
{
    [Fact]
    public void ConstructionRetainsLazyDependenciesWithoutObservingAuthorityOrResolvingIo()
    {
        var ports = new Ports();
        _ = ports.Service();
        Assert.Equal(0, ports.SourceCalls);
        Assert.Equal(0, ports.CurrentProfileLookups);
        AssertNoWorkOrStorage(ports);
        Assert.Throws<ArgumentNullException>(() => new RevisionReportService(null!, ports, ports, ports.Storage, Ports.Instance));
        Assert.Throws<ArgumentNullException>(() => new RevisionReportService(ports, null!, ports, ports.Storage, Ports.Instance));
        Assert.Throws<ArgumentNullException>(() => new RevisionReportService(ports, ports, null!, ports.Storage, Ports.Instance));
        Assert.Throws<ArgumentNullException>(() => new RevisionReportService(ports, ports, ports, null!, Ports.Instance));
        Assert.Throws<ArgumentException>(() => new RevisionReportService(ports, ports, ports, ports.Storage, Guid.Empty));
        AssertNoWorkOrStorage(ports);
    }

    [Fact]
    public async Task InvalidOperationAndEmptySelectorsAreRejectedBeforeAnyPortIsCalled()
    {
        var ports = new Ports();
        var service = ports.Service();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetCurrentAsync(Ports.Actor, Ports.Scope, (RevisionReportOperation)99));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetRetainedAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, Ports.DerivationId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, Guid.Empty));
        Assert.Equal(0, ports.SourceCalls);
        Assert.Equal(0, ports.CurrentCalls);
        Assert.Equal(0, ports.RetainedCalls);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AlreadyCanceledRequestDoesNotObserveOrResolveDependencies(int entrypoint)
    {
        var ports = new Ports();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(ports.Service(), entrypoint, cancellation.Token));
        Assert.Equal(0, ports.SourceCalls);
        Assert.Equal(0, ports.CurrentCalls);
        Assert.Equal(0, ports.RetainedCalls);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DeniedSourceDoesNotResolveProfileBackendOrStore(int entrypoint)
    {
        var ports = new Ports { Authorized = false };
        var service = ports.Service();
        if (entrypoint == 2)
            Assert.Equal(RevisionReportEnsureOutcome.NotAuthorizedOrNotFound,
                (await service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection)).Outcome);
        else
        {
            var result = entrypoint == 0
                ? await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection)
                : await service.GetRetainedAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, Ports.DerivationId);
            Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, result.Outcome);
            Assert.Null(result.Report);
        }
        Assert.Equal(0, ports.CurrentProfileLookups);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentCurrentProfileReturnsNoDataWithoutClaimOrLazyIo(bool ensure)
    {
        var ports = new Ports { CurrentProfileAvailable = false };
        var service = ports.Service();
        if (ensure)
            Assert.Equal(RevisionReportEnsureOutcome.ProfileUnavailable,
                (await service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection)).Outcome);
        else
            Assert.Equal(RevisionReportReadOutcome.ProfileUnavailable,
                (await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection)).Outcome);
        Assert.True(ports.SourceCalls > 0);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSourceCapIsRefusedBeforeClaimBackendFactoryAndStorageFactory(bool storageCap)
    {
        var ports = storageCap ? new Ports(nativeCap: 2) : new Ports(policy: new(nativeByteLimit: 2));
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() =>
            ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection));
        Assert.Equal(RevisionReportFailureCategory.ResourceLimit, error.Failure);
        Assert.Null(ports.FinishedCompletion);
        AssertNoWorkOrStorage(ports);
    }

    [Fact]
    public async Task TrustedStorageCapsReduceCurrentKeyBeforeMetadataLookup()
    {
        var ports = new Ports(nativeCap: 3, derivedCap: 4096, derivedReadCap: 3072);
        var result = await ports.Service().GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportState.NotDerived, result.State);
        Assert.NotNull(ports.LastCurrentKey);
        Assert.Equal(3, ports.LastCurrentKey.Policy.NativeByteLimit);
        Assert.Equal(3072, ports.LastCurrentKey.Policy.StoredObjectByteLimit);
        Assert.NotEqual(ports.Key().Fingerprint, ports.LastCurrentKey.Fingerprint);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedReferenceAboveActiveReadCapRetainsExactMetadataWithoutResolvingStore(bool retained)
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        var service = ports.Service(new(storedObjectByteLimit: 1));
        var result = retained
            ? await service.GetRetainedAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, selected.DerivationId)
            : await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportReadOutcome.ResourceLimit, result.Outcome);
        Assert.Equal(selected, result.SelectedResult);
        Assert.Null(result.Report);
        AssertNoWorkOrStorage(ports);
    }

    [Fact]
    public async Task RetainedHistoricalReportNeedsNoCurrentProfileAndIgnoresReducedNativeIoCap()
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        ports.CurrentProfileAvailable = false;
        var result = await ports.Service(new(nativeByteLimit: 1)).GetRetainedAsync(Ports.Actor, Ports.Scope,
            RevisionReportOperation.Projection, selected.DerivationId);
        Assert.Equal(RevisionReportReadOutcome.Observed, result.Outcome);
        Assert.Equal(selected.Key, result.Report!.Metadata.Key);
        Assert.Equal(4_194_304, result.Report.Metadata.Key.Policy.NativeByteLimit);
        Assert.Equal(0, ports.CurrentProfileLookups);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(1, ports.DerivedReads);
        Assert.Equal(0, ports.ClaimCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAndRetryDelayedSnapshotsDoNotAcquireNativeBytesOrStartWork(bool retry)
    {
        var ports = new Ports();
        ports.OnCurrent = (key, _) => retry ? ports.RetryDelayed(key) : ports.Pending(key);
        var service = ports.Service();
        var read = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportReadOutcome.Observed, read.Outcome);
        Assert.Equal(retry ? RevisionReportState.Failed : RevisionReportState.Pending, read.State);
        Assert.Null(read.Report);
        var ensure = await service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(retry ? RevisionReportEnsureOutcome.RetryDelayed : RevisionReportEnsureOutcome.Pending, ensure.Outcome);
        AssertNoWorkOrStorage(ports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RepairSelectorMismatchPrecedesEmptyPendingRetryAndCachedShortcuts(int state)
    {
        var ports = new Ports();
        if (state == 3) _ = ports.Cache(ports.Key());
        else if (state == 1) ports.OnCurrent = (key, _) => ports.Pending(key);
        else if (state == 2) ports.OnCurrent = (key, _) => ports.RetryDelayed(key);
        var requested = Guid.Parse("1059fe00-0000-4000-8000-0000000000ff");
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, requested);
        Assert.Equal(RevisionReportEnsureOutcome.SelectionChanged, result.Outcome);
        Assert.Null(result.Read);
        Assert.Null(result.Snapshot);
        AssertNoWorkOrStorage(ports);
    }

    [Fact]
    public async Task RepairSelectionChangeAfterInitialMissingProofPreventsFurtherIoAndRepairCas()
    {
        var ports = new Ports();
        var original = ports.Cache(ports.Key());
        var originalSnapshot = ports.Snapshot!;
        _ = ports.Cache(ports.Key(), id: Guid.Parse("1059fe00-0000-4000-8000-0000000000f1"),
            candidateId: Guid.Parse("1059fe00-0000-4000-8000-0000000000f2"));
        var changedSnapshot = ports.Snapshot!;
        ports.OnCurrent = (_, call) => call == 1 ? originalSnapshot : changedSnapshot;
        ports.OnRead = reference =>
        {
            Assert.Equal(original.DerivedObject, reference);
            throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
        };
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, original.DerivationId);
        Assert.Equal(RevisionReportEnsureOutcome.SelectionChanged, result.Outcome);
        Assert.True(ports.CurrentCalls >= 2);
        // The exact original selection received one authorized missing-object proof before admission.
        // The changed snapshot permits no further object/native I/O, replacement adoption or repair CAS.
        Assert.Equal(1, ports.DerivedReads);
        Assert.Equal(1, ports.StorageFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.RepairCalls);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task HealthyExactRepairSelectionReturnsOwnedCacheWithoutDerivationOrRepairWrite()
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, selected.DerivationId);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.Equal(selected, result.Read!.SelectedResult);
        Assert.IsType<VerifiedProjectionReport>(result.Read.Report);
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.RepairCalls);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task CachedPayloadIsOwnedAfterMutableStorageBytesAreReturned()
    {
        var ports = new Ports();
        var key = (ProjectionCacheKey)ports.Key();
        var parameter = RevisionReportCodecTestSupport.Parameter("{\"kind\":\"string\",\"value\":\"preserved\"}");
        var selected = ports.Cache(key, RevisionReportCodecTestSupport.ProjectionJson(key, $"[{parameter}]"),
            Ports.Observation(key, items: 1));
        var read = await ports.Service().GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        var report = Assert.IsType<VerifiedProjectionReport>(read.Report);
        var before = JsonSerializer.SerializeToUtf8Bytes(report.Report, ProjectionJson.CreateOptions());
        Array.Fill(ports.ObjectBytes(selected), (byte)'!');
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(report.Report, ProjectionJson.CreateOptions()));
        Assert.False(report.Report.Document!.Parameters is Array);
        var list = Assert.IsAssignableFrom<IList<ProjectionParameter>>(report.Report.Document.Parameters);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Equal(0, ports.BackendCalls);
    }

    [Fact]
    public async Task RevocationAfterVerifiedByteReadSuppressesFinalPayload()
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        ports.OnRead = reference =>
        {
            Assert.Equal(selected.DerivedObject, reference);
            ports.Authorized = false;
            return ports.ObjectBytes(selected);
        };
        var result = await ports.Service().GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportReadOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Null(result.Report);
        Assert.Null(result.SelectedResult);
        Assert.Equal(1, ports.DerivedReads);
        Assert.Equal(0, ports.BackendCalls);
        Assert.Equal(0, ports.ClaimCalls);
    }

    [Fact]
    public async Task UnsupportedStoredFormatReturnsTypedFailureWithoutBackendResolution()
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        ports.StoredFormatAvailable = false;
        var result = await ports.Service().GetRetainedAsync(Ports.Actor, Ports.Scope,
            RevisionReportOperation.Projection, selected.DerivationId);
        Assert.Equal(RevisionReportReadOutcome.UnsupportedFormat, result.Outcome);
        Assert.Equal(selected, result.SelectedResult);
        Assert.Null(result.Report);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.ClaimCalls);
    }

    [Theory]
    [InlineData(PrivateObjectStorageError.Missing, RevisionReportReadOutcome.ObjectUnavailable)]
    [InlineData(PrivateObjectStorageError.Integrity, RevisionReportReadOutcome.StorageIntegrity)]
    public async Task SyntheticMissingAndCorruptCacheRemainExplicitFailuresWithoutAutomaticRepair(
        PrivateObjectStorageError storageError, RevisionReportReadOutcome expected)
    {
        var ports = new Ports();
        var selected = ports.Cache(ports.Key());
        ports.OnRead = _ => throw new PrivateObjectStorageException(storageError);
        var service = ports.Service();
        var read = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(expected, read.Outcome);
        Assert.Equal(selected, read.SelectedResult);
        Assert.Null(read.Report);
        var ensure = await service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, ensure.Outcome);
        Assert.Equal(expected, ensure.Read!.Outcome);
        Assert.Null(ensure.Read.Report);
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.RepairCalls);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task SelectedProjectionLinksUseExactVerifiedOriginAndLeaveStoredValidationUnlinked()
    {
        var ports = new Ports();
        var (validation, projection) = CacheLinkReports(ports);
        var savedBytes = ports.ObjectBytes(validation).ToArray();
        var service = ports.Service();
        var linkedRead = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation,
            projection.DerivationId);
        var linked = Assert.IsType<VerifiedValidationReport>(linkedRead.Report);
        Assert.Equal(projection.DerivationId, linked.ProjectionDerivationId);
        Assert.Equal(new ParameterNodeReference("p"), Assert.Single(linked.Report.Diagnostics).ProjectionLocator);
        Assert.Equal(savedBytes, ports.ObjectBytes(validation));
        var unlinkedRead = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation);
        var unlinked = Assert.IsType<VerifiedValidationReport>(unlinkedRead.Report);
        Assert.Null(unlinked.ProjectionDerivationId);
        Assert.Null(Assert.Single(unlinked.Report.Diagnostics).ProjectionLocator);
        Assert.False(ReferenceEquals(linked.Report.Diagnostics, unlinked.Report.Diagnostics));
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrForeignProfileSelectedProjectionDoesNotAdoptAnotherReport(bool foreignProfile)
    {
        var ports = new Ports();
        var (validation, projection) = CacheLinkReports(ports, foreignProfile: foreignProfile);
        var requested = foreignProfile ? projection.DerivationId : Guid.Parse("1059fe00-0000-4000-8000-0000000000c0");
        var result = await ports.Service().GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation, requested);
        Assert.Equal(RevisionReportReadOutcome.SelectedProjectionUnavailable, result.Outcome);
        Assert.Equal(validation, result.SelectedResult);
        Assert.Null(result.Report);
        Assert.Equal(requested, ports.LastRetainedId);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task LinkedResponseOverheadIsBudgetedAfterOtherwiseBoundedReportsAreRead()
    {
        var ports = new Ports();
        var (validation, projection) = CacheLinkReports(ports, message: new string('m', 1500));
        var unlimited = await ports.Service().GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation);
        var unlinked = Assert.IsType<VerifiedValidationReport>(unlimited.Report);
        // Independent contract serialization establishes the unlinked ceiling. Adding the real
        // parameter locator must be charged; neither native bytes nor the envelope size is this ceiling.
        var unlinkedBytes = JsonSerializer.SerializeToUtf8Bytes(unlinked.Report, ProjectionJson.CreateOptions());
        var service = ports.Service(new(responseByteLimit: unlinkedBytes.Length));
        var ordinary = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation);
        Assert.Equal(RevisionReportReadOutcome.Observed, ordinary.Outcome);
        Assert.NotNull(ordinary.Report);
        var linked = await service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Validation, projection.DerivationId);
        Assert.Equal(RevisionReportReadOutcome.ResourceLimit, linked.Outcome);
        Assert.Equal(validation, linked.SelectedResult);
        Assert.Null(linked.Report);
        Assert.Equal(0, ports.BackendCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task SyntheticAlreadyPresentPublicationIsRefusedWithoutReadbackOrUsableCompletion()
    {
        var ports = new Ports { OnPublish = _ => PrivateObjectPublishResult.AlreadyPresent };
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.Equal(RevisionReportFailureCategory.StorageIntegrity, result.Read!.Failure);
        Assert.Null(result.Read.Report);
        Assert.Equal(1, ports.PublishCalls);
        Assert.Equal(0, ports.DerivedReads);
        Assert.False(ports.FinishedCompletion!.IsUsable);
    }

    [Fact]
    public async Task SyntheticExceptionAfterCommittedFinishResolvesExactRetainedSuccess()
    {
        var ports = new Ports
        {
            // This is a synthetic post-commit acknowledgement fault, not PostgreSQL transport loss.
            OnFinish = (_, _) => throw new IOException("synthetic acknowledgement fault")
        };
        ports.OnCurrent = (key, _) => ports.FinishedCompletion is null
            ? new(ports.Authority(key), key, null, 0, null, null)
            : throw new InvalidOperationException("Current selection cannot be used to resolve this successful acknowledgement.");
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.NotNull(result.Read!.Report);
        Assert.Equal(ports.Claimed!.DerivationId, result.Read.SelectedResult!.DerivationId);
        Assert.Equal(ports.Claimed.DerivationId, ports.LastRetainedId);
        Assert.True(ports.RetainedCalls > 0);
        Assert.Equal(1, ports.BackendCalls);
        Assert.Equal(1, ports.FinishCalls);
        Assert.Equal(1, ports.PublishCalls);
    }

    [Fact]
    public async Task SyntheticAcknowledgedResultMissingObjectDoesNotAdoptNewCurrentSelection()
    {
        var ports = new Ports();
        RevisionReportResult? exactCommitted = null;
        RevisionReportResult? replacement = null;
        ports.OnFinish = (claim, _) =>
        {
            exactCommitted = ports.Snapshot!.SelectedResult!;
            replacement = ports.Cache(claim.Key, id: Guid.Parse("1059fe00-0000-4000-8000-0000000000a0"),
                candidateId: Guid.Parse("1059fe00-0000-4000-8000-0000000000a1"));
            // The actual published candidate was read back before this synthetic Finish callback.
            // Only later delivery of the exact acknowledged A is now unavailable; B remains healthy.
            ports.OnRead = reference =>
            {
                Assert.Equal(exactCommitted!.DerivedObject, reference);
                throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
            };
            throw new IOException("synthetic acknowledgement fault after another selection became current");
        };
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.Observed, result.Outcome);
        Assert.Equal(RevisionReportReadOutcome.ObjectUnavailable, result.Read!.Outcome);
        Assert.Equal(exactCommitted, result.Read.SelectedResult);
        Assert.Null(result.Read.Report);
        Assert.NotEqual(exactCommitted!.DerivationId, replacement!.DerivationId);
        Assert.Equal(replacement, ports.Snapshot!.SelectedResult);
        Assert.Equal(exactCommitted.DerivationId, ports.LastRetainedId);
        Assert.Equal(1, ports.BackendCalls);
        Assert.Equal(1, ports.ClaimCalls);
        Assert.Equal(1, ports.FinishCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SyntheticRetainedSubstitutionCannotResolveAnotherSuccessfulAttempt(int alteredFact)
    {
        var ports = new Ports();
        ports.OnFinish = (claim, completion) =>
        {
            var otherClaim = ports.Claim(claim.Key,
                id: alteredFact == 0 ? Guid.Parse("1059fe00-0000-4000-8000-0000000000e0") : claim.DerivationId,
                generation: alteredFact == 1 ? 2 : claim.Generation,
                candidateId: alteredFact == 2 ? Guid.Parse("1059fe00-0000-4000-8000-0000000000e2") : claim.CandidateObjectId.ObjectId,
                start: alteredFact == 3 ? claim.StartedAtUtc.AddSeconds(1) : claim.StartedAtUtc);
            var reference = new PrivateObjectReference(otherClaim.CandidateObjectId,
                completion.DerivedObject!.Digest, completion.DerivedObject.ByteCount);
            var otherCompletion = RevisionReportCompletion.ForUsable(claim.Key, completion.Observation!, reference);
            var other = ports.Retain(otherClaim, otherCompletion, Ports.Metadata(otherClaim, reference, completion.Observation!));
            ports.OnRetained = _ => other;
            throw new IOException("synthetic acknowledgement fault with substituted retained facts");
        };
        var error = await Assert.ThrowsAsync<RevisionReportServiceException>(() =>
            ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection));
        Assert.Equal(RevisionReportFailureCategory.ProtocolFailure, error.Failure);
        Assert.Equal(ports.Claimed!.DerivationId, ports.LastRetainedId);
        Assert.Equal(1, ports.BackendCalls);
        Assert.Equal(1, ports.FinishCalls);
    }

    [Fact]
    public async Task SyntheticFailedAcknowledgementNeverSubstitutesChangedCurrentAttempt()
    {
        var ports = new Ports();
        ports.OnBackend = (key, _, _) => Task.FromResult(
            RevisionReportBackendResult.ForFailure(RevisionReportFailureCategory.BackendUnavailable));
        ports.OnFinish = (claim, _) =>
        {
            var nextClaim = ports.Claim(claim.Key, id: Guid.Parse("1059fe00-0000-4000-8000-0000000000d0"),
                generation: 2, candidateId: Guid.Parse("1059fe00-0000-4000-8000-0000000000d1"));
            ports.Snapshot = new(ports.Authority(claim.Key), claim.Key, nextClaim.CacheEntryId, 2, null,
                new(nextClaim.DerivationId, 2, nextClaim.StartedAtUtc, nextClaim.LeaseExpiresAtUtc, null, null));
            throw new IOException("synthetic failed completion acknowledgement fault");
        };
        var result = await ports.Service().EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection);
        Assert.Equal(RevisionReportEnsureOutcome.ClaimLost, result.Outcome);
        Assert.Null(result.Read);
        Assert.Null(result.Snapshot);
        Assert.Equal(ports.Claimed!.DerivationId, ports.LastRetainedId);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task SyntheticBackendIsolationFailurePrecedesCancellationDuringIndependentCleanup()
    {
        var ports = new Ports();
        using var cancellation = new CancellationTokenSource();
        ports.OnBackend = (key, _, _) =>
        {
            cancellation.Cancel();
            var actualSyntheticObservation = new RevisionReportObservation(key.Operation,
                RevisionReportBackendStatus.IsolationFailure, new(key.Scope, key.NativeDigest, key.Profile, true), null, null);
            return Task.FromResult(RevisionReportBackendResult.ForFailure(
                RevisionReportFailureCategory.IsolationFailure, actualSyntheticObservation));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service().EnsureAsync(Ports.Actor,
            Ports.Scope, RevisionReportOperation.Projection, cancellationToken: cancellation.Token));
        Assert.NotNull(ports.FinishedCompletion);
        Assert.Equal(RevisionReportFailureCategory.IsolationFailure, ports.FinishedCompletion.Failure);
        Assert.Equal(RevisionReportBackendStatus.IsolationFailure, ports.FinishedCompletion.Observation!.Status);
        Assert.False(ports.FinishTokenWasCanceled);
        Assert.Equal(1, ports.FinishCalls);
        Assert.Equal(0, ports.PublishCalls);
    }

    [Fact]
    public async Task CancellationAfterSyntheticUsableBackendDoesNotPublishOrInventUsableCompletion()
    {
        var ports = new Ports();
        using var cancellation = new CancellationTokenSource();
        ports.OnBackend = (key, _, _) =>
        {
            var accepted = RevisionReportBackendResult.ForProjection((ProjectionCacheKey)key,
                Ports.Projection((ProjectionCacheKey)key), Ports.Observation(key));
            cancellation.Cancel();
            return Task.FromResult(accepted);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.Service().EnsureAsync(Ports.Actor,
            Ports.Scope, RevisionReportOperation.Projection, cancellationToken: cancellation.Token));
        Assert.Equal(0, ports.PublishCalls);
        Assert.NotNull(ports.FinishedCompletion);
        Assert.False(ports.FinishedCompletion.IsUsable);
        Assert.Equal(RevisionReportFailureCategory.Canceled, ports.FinishedCompletion.Failure);
        Assert.False(ports.FinishTokenWasCanceled);
    }

    private static (RevisionReportResult Validation, RevisionReportResult Projection) CacheLinkReports(Ports ports,
        bool foreignProfile = false, string message = "mapping")
    {
        var projectionKey = (ProjectionCacheKey)ports.Key(profile: foreignProfile
            ? new("neutral", "historical-v1", "historical-build", "historical-adapter") : null);
        var parameter = """
            {"id":"p","name":"exact","dataType":{"kind":"scalar","scalarTypes":["string"],"nativeName":null},"requirement":"optional","defaultValue":null,"nativeLocator":{"kind":"jsonPointer","documentId":"a/../logical~1.json","pointer":"/parameters/0"}}
            """;
        var projection = ports.Cache(projectionKey, RevisionReportCodecTestSupport.ProjectionJson(projectionKey, $"[{parameter}]"),
            Ports.Observation(projectionKey, items: 1), Guid.Parse("1059fe00-0000-4000-8000-0000000000b0"),
            Guid.Parse("1059fe00-0000-4000-8000-0000000000b1"));
        var validationKey = (ValidationCacheKey)ports.Key(RevisionReportOperation.Validation);
        var diagnostics = $$"""
            [{"code":"invalid","severity":"error","message":{{JsonSerializer.Serialize(message)}},"messageTruncated":false,"nativeLocator":{"kind":"jsonPointer","documentId":"a/../logical~1.json","pointer":"/parameters/0"},"owner":{"kind":"failureRule","index":"0"},"location":null,"locationTruncated":false,"projectionLocator":null}]
            """;
        var reportJson = $$"""
            {"status":"invalidMapping","provenance":{{RevisionReportCodecTestSupport.OperationProvenanceJson(validationKey)}},"isValid":false,"totalIssueCount":1,"emittedIssueCount":1,"truncated":false,"diagnostics":{{diagnostics}}}
            """;
        var validation = ports.Cache(validationKey, reportJson, Ports.Observation(validationKey, valid: false, issues: 1));
        return (validation, projection);
    }

    private static Task Invoke(RevisionReportService service, int entrypoint, CancellationToken cancellationToken) => entrypoint switch
    {
        0 => service.GetCurrentAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, cancellationToken: cancellationToken),
        1 => service.GetRetainedAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection,
            Ports.DerivationId, cancellationToken: cancellationToken),
        2 => service.EnsureAsync(Ports.Actor, Ports.Scope, RevisionReportOperation.Projection, cancellationToken: cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(entrypoint))
    };

    private static void AssertNoWorkOrStorage(Ports ports)
    {
        Assert.Equal(0, ports.ClaimCalls);
        Assert.Equal(0, ports.FinishCalls);
        Assert.Equal(0, ports.RepairCalls);
        Assert.Equal(0, ports.BackendFactoryCalls);
        Assert.Equal(0, ports.BackendCalls);
        Assert.Equal(0, ports.StorageFactoryCalls);
        Assert.Equal(0, ports.NativeReads);
        Assert.Equal(0, ports.DerivedReads);
        Assert.Equal(0, ports.PublishCalls);
    }
}
