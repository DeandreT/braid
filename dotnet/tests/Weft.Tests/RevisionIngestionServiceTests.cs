using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

/// <summary>
/// Synthetic ports verify application orchestration only. Real native admission,
/// database fencing, and filesystem isolation are qualified by integration tests.
/// </summary>
public sealed class RevisionIngestionServiceTests
{
    [Fact]
    public async Task ExactCurrentPositionBytesAreOwnedAndSeparatelyVerifiedBeforeOneValidation()
    {
        var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes(" \n {\"unknown\":9007199254740993, \"native\": true} \t");
        var source = new SourceStream([0xff, .. bytes], initialOffset: 1);

        var result = await fixture.Service().CreateAsync(fixture.Actor, fixture.Project,
            fixture.Key, fixture.Request, source);

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, result.Outcome);
        Assert.Equal(bytes, fixture.Storage.PublishedBytes);
        Assert.Equal(bytes, fixture.Backend.Artifact!.CopyBytes());
        Assert.Equal(Sha256Digest.FromBytes(bytes), fixture.Backend.Artifact.Digest);
        Assert.Equal(bytes.Length, fixture.Backend.Artifact.ByteCount);
        Assert.False(source.Disposed);
        Assert.Equal(1, fixture.Storage.PublishCalls);
        Assert.Equal(1, fixture.Storage.ReadCalls);
        Assert.Equal(1, fixture.Backend.Calls);
        Assert.Equal(["access", "find", "current", "reserve", "claim", "publish", "read", "stage",
            "reserved", "validate", "complete", "finalize"], fixture.Events);
        Assert.Equal(fixture.Metadata.Receipt!.Facts.FutureRevision, fixture.Storage.Scope);
        Assert.Equal(fixture.Metadata.Receipt.Facts.NativeObject, fixture.Storage.Reference);
        Assert.Equal(1, result.Receipt!.CommittedSequence);
        var exposed = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("backendBuild", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nativeObject", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("leaseToken", exposed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("reader")]
    [InlineData("actor")]
    [InlineData("organization")]
    [InlineData("project")]
    public async Task ExactFreshUploadAuthorityPrecedesSourceAndNamespaceReads(string denied)
    {
        var fixture = new Fixture();
        fixture.Access = denied switch
        {
            "absent" => null,
            "reader" => new(fixture.Project, fixture.Actor.PrincipalId, ProjectRole.Viewer),
            "actor" => new(fixture.Project, new PrincipalId(Guid.NewGuid()), ProjectRole.Publisher),
            "organization" => new(new ProjectScope(Guid.NewGuid(), fixture.Project.ProjectId), fixture.Actor.PrincipalId, ProjectRole.Publisher),
            _ => new(new ProjectScope(fixture.Project.OrganizationId, Guid.NewGuid()), fixture.Actor.PrincipalId, ProjectRole.Publisher)
        };
        var source = new SourceStream("native"u8.ToArray());

        var result = await fixture.Service().CreateAsync(fixture.Actor, fixture.Project,
            fixture.Key, fixture.Request, source);

        Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(["access"], fixture.Events);
    }

    [Fact]
    public async Task UnknownCurrentBackendIsRejectedBeforeSourceWithoutInventingAReservation()
    {
        var fixture = new Fixture();
        fixture.Registry.CurrentAvailable = false;
        var source = new SourceStream("native"u8.ToArray());

        var result = await fixture.Service().CreateAsync(fixture.Actor, fixture.Project,
            fixture.Key, fixture.Request, source);

        Assert.Equal(RevisionIngestionOutcome.Rejected, result.Outcome);
        Assert.Equal(RevisionIngestionRejection.UnregisteredBackend, result.Rejection);
        Assert.Null(result.Receipt);
        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(0, fixture.Metadata.ReserveCalls);
    }

    [Theory]
    [InlineData(0, RevisionIngestionRejection.EmptyNative)]
    [InlineData(8, RevisionIngestionRejection.NativeLimit)]
    public async Task SourceAdmissionDoesNotReserveEmptyOrOversizedBodies(int byteCount,
        RevisionIngestionRejection rejection)
    {
        var fixture = new Fixture(storeLimit: 6, backendLimit: 7);
        var source = new SourceStream(new byte[byteCount]);

        var result = await fixture.Service(applicationLimit: 5).CreateAsync(fixture.Actor,
            fixture.Project, fixture.Key, fixture.Request, source);

        Assert.Equal(rejection, result.Rejection);
        Assert.Null(result.Receipt);
        Assert.Equal(0, fixture.Metadata.ReserveCalls);
        Assert.Equal(byteCount == 0 ? 0 : 6, source.BytesRead);
        Assert.True(source.MaximumRequested <= 6);
        Assert.False(source.Disposed);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("store")]
    [InlineData("backend")]
    public async Task EveryCurrentNativeCapParticipatesInTheEffectiveNewPolicy(string limiting)
    {
        var fixture = new Fixture(storeLimit: limiting == "store" ? 3 : 8,
            backendLimit: limiting == "backend" ? 3 : 8);
        var result = await fixture.Service(limiting == "application" ? 3 : 8)
            .CreateAsync(fixture.Actor, fixture.Project, fixture.Key, fixture.Request,
                new SourceStream("abc"u8.ToArray()));

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, result.Outcome);
        Assert.Equal(3, fixture.Metadata.Receipt!.Facts.AdmissionPolicy.EffectiveNativeByteLimit);
    }

    [Fact]
    public async Task TerminalMatchingReplayComparesFullBodyBeforeReturningDespiteRemovedRegistryAndReducedCaps()
    {
        var fixture = new Fixture();
        var body = "original exact bytes"u8.ToArray();
        var initial = await fixture.Create(body);
        fixture.Events.Clear();
        fixture.Registry.CurrentAvailable = fixture.Registry.ReservedAvailable = false;
        fixture.StoreLimit = 1;
        var source = new SourceStream(body);

        var replay = await fixture.Service(applicationLimit: 1).CreateAsync(fixture.Actor,
            fixture.Project, fixture.Key, fixture.Request, source);

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, replay.Outcome);
        Assert.Equal(initial.Receipt, replay.Receipt);
        Assert.Equal(body.Length, source.BytesRead);
        Assert.Equal(["access", "find", "reserve"], fixture.Events);
        Assert.Equal(1, fixture.Storage.PublishCalls);
        Assert.Equal(1, fixture.Storage.ReadCalls);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("backend")]
    [InlineData("filename")]
    public async Task ChangedAcceptedCreateIntentConflictsAfterBodyComparisonEvenAfterCommit(string changed)
    {
        var fixture = new Fixture();
        var body = "native bytes"u8.ToArray();
        await fixture.Create(body);
        var request = changed switch
        {
            "name" => new CreateRevisionRequest(new("changed", fixture.Request.Metadata.Description), fixture.Profile.BackendId),
            "description" => new CreateRevisionRequest(new(fixture.Request.Metadata.Name, "changed"), fixture.Profile.BackendId),
            "backend" => new CreateRevisionRequest(fixture.Request.Metadata, "another.backend"),
            "filename" => new CreateRevisionRequest(fixture.Request.Metadata, fixture.Profile.BackendId, "other.native"),
            _ => fixture.Request
        };
        if (changed == "body")
            body = "changed bytes"u8.ToArray();
        fixture.Events.Clear();
        fixture.Registry.CurrentAvailable = false;
        var source = new SourceStream(body);

        var conflict = await fixture.Service().CreateAsync(fixture.Actor, fixture.Project,
            fixture.Key, request, source);

        Assert.Equal(RevisionIngestionOutcome.IdempotencyConflict, conflict.Outcome);
        Assert.Null(conflict.Receipt);
        Assert.Equal(body.Length, source.BytesRead);
        Assert.Equal(["access", "find", "reserve"], fixture.Events);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedExistingBodyBeyondOriginalReducedCapStillComparesCompleteIntent(bool pending)
    {
        var fixture = new Fixture();
        if (pending)
            fixture.Storage.PublishError = PrivateObjectStorageError.Io;
        await fixture.Service(applicationLimit: 5).CreateAsync(fixture.Actor, fixture.Project,
            fixture.Key, fixture.Request, new SourceStream("abcde"u8.ToArray()));
        var originalPolicy = fixture.Metadata.Receipt!.Facts.AdmissionPolicy;
        fixture.Events.Clear();
        var source = new SourceStream("abcdefghi"u8.ToArray());

        var result = await fixture.Service(applicationLimit: 1).CreateAsync(fixture.Actor,
            fixture.Project, fixture.Key, fixture.Request, source);

        Assert.Equal(RevisionIngestionOutcome.IdempotencyConflict, result.Outcome);
        Assert.Equal(9, source.BytesRead);
        Assert.Equal(originalPolicy, fixture.Metadata.Receipt!.Facts.AdmissionPolicy);
        Assert.Equal(["access", "find", "reserve"], fixture.Events);
    }

    [Fact]
    public async Task AlreadyPresentStillRequiresAnIndependentVerifiedRead()
    {
        var fixture = new Fixture();
        fixture.Storage.PublishResult = PrivateObjectPublishResult.AlreadyPresent;

        var result = await fixture.Create("same bytes"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, result.Outcome);
        Assert.Equal(1, fixture.Storage.ReadCalls);
        Assert.True(fixture.Events.IndexOf("read") < fixture.Events.IndexOf("stage"));
    }

    [Theory]
    [InlineData("different")]
    [InlineData("truncated")]
    [InlineData("extra")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task IndependentlyMismatchedVerifiedBytesCannotReachStageOrValidation(string mismatch)
    {
        var fixture = new Fixture();
        fixture.Storage.ReadTransform = bytes => mismatch switch
        {
            "different" => Enumerable.Repeat((byte)'x', bytes.Length).ToArray(),
            "truncated" => bytes[..^1],
            "extra" => [.. bytes, 1],
            "empty" => [],
            _ => null!
        };

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(IngestionFailureCode.StorageIntegrity, result.Failure);
        Assert.Equal(0, fixture.Metadata.StageCalls);
        Assert.Equal(0, fixture.Backend.Calls);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Theory]
    [InlineData(PrivateObjectStorageError.ScopeMismatch, IngestionFailureCode.StorageIntegrity)]
    [InlineData(PrivateObjectStorageError.Conflict, IngestionFailureCode.StorageIntegrity)]
    [InlineData(PrivateObjectStorageError.Integrity, IngestionFailureCode.StorageIntegrity)]
    [InlineData(PrivateObjectStorageError.Limit, IngestionFailureCode.StorageIo)]
    [InlineData(PrivateObjectStorageError.UnsafeStorage, IngestionFailureCode.StorageIo)]
    [InlineData(PrivateObjectStorageError.Io, IngestionFailureCode.StorageIo)]
    [InlineData(PrivateObjectStorageError.Unsupported, IngestionFailureCode.StorageIo)]
    public async Task SafeStorageCategoriesProduceOperationalFailuresWithoutRepair(
        PrivateObjectStorageError storageError, IngestionFailureCode expected)
    {
        var fixture = new Fixture();
        fixture.Storage.PublishError = storageError;

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(expected, result.Failure);
        Assert.Equal(0, fixture.Storage.ReadCalls);
        Assert.Equal(0, fixture.Backend.Calls);
        Assert.Null(result.Receipt!.CommittedSequence);
    }

    [Fact]
    public async Task ResumeMissingObjectNeedsSourceAndMatchingSubmissionPublishesReservedIdentity()
    {
        var fixture = new Fixture();
        fixture.Storage.PublishError = PrivateObjectStorageError.Io;
        var initial = await fixture.Create("native"u8.ToArray());
        var reference = fixture.Metadata.Receipt!.Facts.NativeObject;
        fixture.Storage.PublishError = null;

        var missing = await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project,
            initial.Receipt!.OperationId);
        var recovered = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.NeedsSource, missing.Outcome);
        Assert.Equal(IngestionFailureCode.NeedsSource, missing.Failure);
        Assert.Equal(RevisionIngestionOutcome.CommittedValid, recovered.Outcome);
        Assert.Equal(reference, fixture.Storage.Reference);
        Assert.Equal(initial.Receipt.FutureRevision, recovered.Receipt!.FutureRevision);
        Assert.Equal(3, recovered.Receipt.ClaimCount);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Fact]
    public async Task DurableEligibleRecoveryNewlyVerifiesAndReusesOriginalEvidenceWithoutOldRegistry()
    {
        var fixture = new Fixture();
        fixture.Metadata.FinalizeReturnsClaimLost = true;
        var initial = await fixture.Create("native"u8.ToArray());
        Assert.Equal(RevisionIngestionOutcome.ClaimLost, initial.Outcome);
        Assert.Equal(IngestionPhase.Ready, fixture.Metadata.Receipt!.Phase);
        fixture.Metadata.ReleaseClaim();
        fixture.Metadata.FinalizeReturnsClaimLost = false;
        fixture.Registry.CurrentAvailable = fixture.Registry.ReservedAvailable = false;
        fixture.Events.Clear();

        var resumed = await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project,
            fixture.Metadata.Receipt.Facts.OperationId);

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, resumed.Outcome);
        Assert.Equal(["access", "get", "claim", "read", "stage", "complete", "finalize"], fixture.Events);
        Assert.Equal(1, fixture.Backend.Calls);
        Assert.Equal(2, fixture.Storage.ReadCalls);
        var completion = fixture.Metadata.Receipt!.Attempts[1].Completion!;
        Assert.Equal(IngestionAttemptCompletionKind.ReusedEvidence, completion.Kind);
        Assert.Equal(1, completion.ReusedAttemptGeneration);
        Assert.Equal(1, fixture.Metadata.Receipt.CommittedRevision!.InitialAdmissionAttemptGeneration);
    }

    [Fact]
    public async Task PendingRecoveryRequiresExactReservedWorkerRatherThanCurrentReplacement()
    {
        var fixture = new Fixture();
        fixture.Registry.ReservedAvailable = false;

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(IngestionFailureCode.ReservedProfileUnavailable, result.Failure);
        Assert.Equal(1, fixture.Metadata.StageCalls);
        Assert.Equal(0, fixture.Backend.Calls);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Fact]
    public async Task CurrentPhysicalStoreReductionCannotRewriteReservedAdmissionPolicy()
    {
        var fixture = new Fixture();
        fixture.Storage.PublishError = PrivateObjectStorageError.Io;
        var initial = await fixture.Create("native bytes"u8.ToArray());
        var policy = fixture.Metadata.Receipt!.Facts.AdmissionPolicy;
        fixture.Storage.PublishError = null;
        fixture.StoreLimit = 1;

        var result = await fixture.Service(applicationLimit: 1).CreateAsync(fixture.Actor,
            fixture.Project, fixture.Key, fixture.Request, new SourceStream("native bytes"u8.ToArray()));

        Assert.Equal(IngestionFailureCode.StorageIo, result.Failure);
        Assert.Equal(policy, fixture.Metadata.Receipt!.Facts.AdmissionPolicy);
        Assert.Equal(initial.Receipt!.FutureRevision, result.Receipt!.FutureRevision);
        Assert.Equal(1, fixture.Storage.PublishCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualSyntheticValidityIsPreservedWithoutInferringItFromIssueCounts(bool valid)
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (artifact, _) => Task.FromResult(SyntheticValidation(
            artifact, fixture.Profile, valid ? IngestionBackendStatus.Succeeded : IngestionBackendStatus.InvalidMapping,
            valid, total: valid ? 1 : 0));

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(valid ? RevisionIngestionOutcome.CommittedValid :
            RevisionIngestionOutcome.CommittedDecodedInvalid, result.Outcome);
        Assert.Equal(valid, fixture.Metadata.Receipt!.Attempts[0].Completion!.Evidence!.Facts.IsValid);
    }

    [Theory]
    [InlineData(IngestionBackendStatus.InvalidMapping, "ferrule.nativeDecodeFailed", RevisionIngestionRejection.NativeDecode)]
    [InlineData(IngestionBackendStatus.UnsupportedArtifact, "fixture.unsupported", RevisionIngestionRejection.UnsupportedArtifact)]
    [InlineData(IngestionBackendStatus.ResourceLimit, "fixture.resource", RevisionIngestionRejection.ResourceLimit)]
    public async Task ExplicitSyntheticPolicyRejectionsNeverFinalize(IngestionBackendStatus status,
        string diagnosticCode, RevisionIngestionRejection rejection)
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (artifact, _) => Task.FromResult(SyntheticValidation(
            artifact, fixture.Profile, status, null, diagnosticCode));

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.Rejected, result.Outcome);
        Assert.Equal(rejection, result.Rejection);
        Assert.Equal(IngestionPhase.Rejected, fixture.Metadata.Receipt!.Phase);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Fact]
    public async Task StoreEvidenceLimitOverrideDecidesOutcomeInsteadOfProposedEligibleCompletion()
    {
        var fixture = new Fixture();
        fixture.Metadata.ReplaceValidationWithEvidenceLimit = true;

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.Rejected, result.Outcome);
        Assert.Equal(RevisionIngestionRejection.ResourceLimit, result.Rejection);
        Assert.Equal(IngestionFailureCode.EvidenceLimit, result.Failure);
        Assert.Equal(1, fixture.Backend.Calls);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("digest")]
    [InlineData("profile")]
    [InlineData("unconfirmed")]
    [InlineData("digestDiagnostic")]
    public async Task SyntheticMismatchedValidationFactsRemainRetryableRatherThanPublish(string mismatch)
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (artifact, _) =>
        {
            var scope = mismatch == "scope" ? new RevisionScope(artifact.Scope.OrganizationId,
                artifact.Scope.ProjectId, artifact.Scope.MappingId, Guid.NewGuid()) : artifact.Scope;
            var digest = mismatch == "digest" ? Sha256Digest.FromBytes("other"u8) : artifact.Digest;
            var profile = mismatch == "profile" ? new IngestionBackendProfile(fixture.Profile.BackendId,
                fixture.Profile.EngineRevision, "other.build", fixture.Profile.AdapterBuild) : fixture.Profile;
            var provenance = new IngestionProvenance(scope, digest, profile, mismatch != "unconfirmed");
            using var diagnostics = JsonDocument.Parse(mismatch == "digestDiagnostic" ?
                "[{\"code\":\"ferrule.digestMismatch\"}]" : "[]");
            var facts = new IngestionValidationFacts(IngestionBackendStatus.Succeeded, provenance,
                true, mismatch == "digestDiagnostic" ? 1 : 0, mismatch == "digestDiagnostic" ? 1 : 0, false);
            return Task.FromResult(IngestionAttemptCompletion.FromValidation(
                IngestionEvidenceDocument.Create(facts, diagnostics.RootElement)));
        };

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
        Assert.Null(fixture.Metadata.Receipt!.CommittedRevision);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("complete")]
    [InlineData("finalize")]
    public async Task FreshMetadataAuthorityStopsPublicationAfterEarlierPermittedWork(string revokedAt)
    {
        var fixture = new Fixture();
        fixture.Metadata.Before = (operation, _) =>
        {
            if (operation == revokedAt)
                fixture.Metadata.Allowed = false;
            return Task.CompletedTask;
        };

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Null(fixture.Metadata.Receipt!.CommittedRevision);
        Assert.Equal(revokedAt == "stage" ? 0 : 1, fixture.Backend.Calls);
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("complete")]
    [InlineData("finalize")]
    public async Task StaleFencesStopWithoutClaimRetryOrSecondWorkerInvocation(string lostAt)
    {
        var fixture = new Fixture();
        fixture.Metadata.Before = (operation, _) =>
        {
            if (operation == lostAt)
                fixture.Metadata.ReleaseClaim();
            return Task.CompletedTask;
        };

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.ClaimLost, result.Outcome);
        Assert.Equal(1, fixture.Metadata.ClaimCalls);
        Assert.Equal(lostAt == "stage" ? 0 : 1, fixture.Backend.Calls);
        Assert.Null(fixture.Metadata.Receipt!.CommittedRevision);
    }

    [Fact]
    public async Task LiveClaimReturnsInProgressWithoutPrivateOrWorkerOperations()
    {
        var fixture = new Fixture();
        fixture.Metadata.FinalizeReturnsClaimLost = true;
        await fixture.Create("native"u8.ToArray());
        fixture.Events.Clear();

        var result = await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project,
            fixture.Metadata.Receipt!.Facts.OperationId);

        Assert.Equal(RevisionIngestionOutcome.InProgress, result.Outcome);
        Assert.Equal(["access", "get", "claim"], fixture.Events);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    [Fact]
    public async Task EveryExplicitCallMakesAtMostOneClaimAndTheNinthExhaustsEightFailures()
    {
        var fixture = new Fixture();
        fixture.Storage.PublishError = PrivateObjectStorageError.Io;
        await fixture.Create("native"u8.ToArray());
        for (var index = 1; index < IngestionWorkflowLimits.MaximumClaims; index++)
        {
            var failure = await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project,
                fixture.Metadata.Receipt!.Facts.OperationId);
            Assert.Equal(RevisionIngestionOutcome.NeedsSource, failure.Outcome);
            Assert.Equal(index + 1, failure.Receipt!.ClaimCount);
        }

        var exhausted = await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project,
            fixture.Metadata.Receipt!.Facts.OperationId);

        Assert.Equal(RevisionIngestionOutcome.AttemptExhausted, exhausted.Outcome);
        Assert.Equal(8, exhausted.Receipt!.ClaimCount);
        Assert.Equal(9, fixture.Metadata.ClaimCalls);
        Assert.Equal(0, fixture.Backend.Calls);
    }

    [Fact]
    public async Task OneServiceInstanceKeepsItsServerIdentityAcrossExplicitClaims()
    {
        var fixture = new Fixture();
        fixture.Storage.PublishError = PrivateObjectStorageError.Io;
        var service = fixture.Service();
        var initial = await service.CreateAsync(fixture.Actor, fixture.Project, fixture.Key,
            fixture.Request, new SourceStream("native"u8.ToArray()));
        await service.ResumeAsync(fixture.Actor, fixture.Project, initial.Receipt!.OperationId);
        await fixture.Service().ResumeAsync(fixture.Actor, fixture.Project, initial.Receipt.OperationId);

        Assert.NotEqual(Guid.Empty, fixture.Metadata.ClaimedInstanceIds[0]);
        Assert.Equal(fixture.Metadata.ClaimedInstanceIds[0], fixture.Metadata.ClaimedInstanceIds[1]);
        Assert.NotEqual(fixture.Metadata.ClaimedInstanceIds[1], fixture.Metadata.ClaimedInstanceIds[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedBackendCompletionCannotInventEligibleEvidenceReuse(bool nullCompletion)
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (_, _) => Task.FromResult(nullCompletion ? null! :
            IngestionAttemptCompletion.ReuseEligibleEvidence(1));

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(IngestionFailureCode.ProtocolFailure, result.Failure);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Theory]
    [InlineData("find")]
    [InlineData("reserve")]
    [InlineData("claim")]
    [InlineData("stage")]
    [InlineData("complete")]
    [InlineData("finalize")]
    public async Task CategoricalMetadataExceptionsPropagateWithoutParsingTheirMessages(string failedAt)
    {
        var fixture = new Fixture();
        var expected = new InvalidOperationException("Categorical metadata failure (opaque).");
        fixture.Metadata.Before = (operation, _) => operation == failedAt ?
            Task.FromException(expected) : Task.CompletedTask;

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Create("native"u8.ToArray()));

        Assert.Same(expected, observed);
        Assert.Null(fixture.Metadata.Receipt?.CommittedRevision);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("publish")]
    [InlineData("read")]
    [InlineData("validate")]
    [InlineData("complete")]
    [InlineData("finalize")]
    public async Task CallerCancellationIsRethrownWithOnlyOrdinaryIndependentFencedBookkeeping(string canceledAt)
    {
        var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var source = new SourceStream("native"u8.ToArray());
        if (canceledAt == "source")
            source.BeforeRead = token => { caller.Cancel(); token.ThrowIfCancellationRequested(); };
        fixture.Storage.Before = (operation, token) =>
        {
            if (operation == canceledAt) { caller.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };
        fixture.Backend.Validate = (artifact, token) =>
        {
            if (canceledAt == "validate") { caller.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(SyntheticValidation(artifact, fixture.Profile, IngestionBackendStatus.Succeeded, true));
        };
        var interrupted = false;
        fixture.Metadata.Before = (operation, token) =>
        {
            if (!interrupted && operation == canceledAt)
            {
                interrupted = true;
                caller.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().CreateAsync(
            fixture.Actor, fixture.Project, fixture.Key, fixture.Request, source, caller.Token));

        if (canceledAt == "source")
        {
            Assert.Null(fixture.Metadata.Receipt);
            Assert.Equal(0, fixture.Metadata.CompleteCalls);
        }
        else
        {
            Assert.Equal(IngestionFailureCode.Canceled, fixture.Metadata.LastSubmittedCompletion!.Failure);
            Assert.True(fixture.Metadata.LastCompletionToken.CanBeCanceled);
            Assert.NotEqual(caller.Token, fixture.Metadata.LastCompletionToken);
            if (canceledAt != "finalize")
                Assert.Equal(IngestionFailureCode.Canceled, fixture.Metadata.Receipt!.Attempts.Last().Completion!.Failure);
            else
                Assert.Equal(IngestionPhase.Ready, fixture.Metadata.Receipt!.Phase);
        }
        Assert.Null(fixture.Metadata.Receipt?.CommittedRevision);
    }

    [Fact]
    public async Task CancellationCleanupCannotBypassRevocationAndCleanupFailureDoesNotMaskCancellation()
    {
        var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        fixture.Backend.Validate = (_, token) =>
        {
            fixture.Metadata.Allowed = false;
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service().CreateAsync(
            fixture.Actor, fixture.Project, fixture.Key, fixture.Request,
            new SourceStream("native"u8.ToArray()), caller.Token));

        Assert.Null(fixture.Metadata.Receipt!.Attempts.Last().Completion);
        Assert.True(fixture.Metadata.LiveClaim);
        Assert.Equal(1, fixture.Metadata.CompleteCalls);
    }

    [Fact]
    public async Task CancellationBookkeepingStopsAtItsIndependentBoundEvenForAnUnresponsivePort()
    {
        var fixture = new Fixture();
        using var caller = new CancellationTokenSource();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.Validate = (_, token) =>
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };
        fixture.Metadata.Before = (operation, _) =>
        {
            if (operation != "complete") return Task.CompletedTask;
            cleanupStarted.SetResult();
            return releaseCleanup.Task;
        };
        var canceled = fixture.Service().CreateAsync(fixture.Actor, fixture.Project, fixture.Key,
            fixture.Request, new SourceStream("native"u8.ToArray()), caller.Token);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(fixture.Metadata.LastCompletionToken.IsCancellationRequested);
            Assert.Null(fixture.Metadata.Receipt!.Attempts.Last().Completion);
        }
        finally
        {
            releaseCleanup.SetResult();
            if (fixture.Metadata.LastOperationTask is { } cleanup)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup);
        }
    }

    [Fact]
    public async Task ActualSyntheticCanceledStatusWithoutCallerCancellationIsAnOrdinaryRetryableObservation()
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (artifact, _) => Task.FromResult(SyntheticValidation(artifact,
            fixture.Profile, IngestionBackendStatus.Canceled, null, "fixture.canceled"));

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(RevisionIngestionOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(IngestionFailureCode.Canceled, result.Failure);
        Assert.Equal(1, fixture.Metadata.CompleteCalls);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Fact]
    public async Task BackendExceptionHasOnlyBoundedFailureAndCannotLeakExceptionTextIntoReceipt()
    {
        var fixture = new Fixture();
        fixture.Backend.Validate = (_, _) => throw new IOException("secret /host/private native stderr");

        var result = await fixture.Create("native"u8.ToArray());

        Assert.Equal(IngestionFailureCode.BackendFailure, result.Failure);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Metadata.FinalizeCalls);
    }

    [Fact]
    public async Task AppendHidesMissingTargetBeforeSourceAndUsesThePersistedBackendAndHeadCondition()
    {
        var fixture = new Fixture();
        var target = new MappingScope(fixture.Project.OrganizationId, fixture.Project.ProjectId, Guid.NewGuid());
        var source = new SourceStream("native"u8.ToArray());
        var request = new AppendRevisionRequest(new EqualHead(Guid.NewGuid()), "append.native");

        var hidden = await fixture.Service().AppendAsync(fixture.Actor, target,
            fixture.Key, request, source);
        Assert.Equal(RevisionIngestionOutcome.NotAuthorizedOrNotFound, hidden.Outcome);
        Assert.Equal(0, source.ReadCalls);
        fixture.Metadata.AppendTarget = new(target, fixture.Profile.BackendId);
        fixture.Events.Clear();

        var appended = await fixture.Service().AppendAsync(fixture.Actor, target,
            fixture.Key, request, source);

        Assert.Equal(RevisionIngestionOutcome.CommittedValid, appended.Outcome);
        Assert.Equal(target, fixture.Metadata.Receipt!.Facts.Intent.Target);
        Assert.Equal(request.HeadCondition, fixture.Metadata.Receipt.Facts.Intent.HeadCondition);
        Assert.Equal(fixture.Profile.BackendId, fixture.Metadata.Receipt.Facts.Intent.BackendId);
        Assert.Equal(["access", "target", "find", "current", "reserve", "claim", "publish", "read",
            "stage", "reserved", "validate", "complete", "finalize"], fixture.Events);
    }

    [Fact]
    public async Task HeadConflictAndOwnReceiptExposeOnlyImmutableOwnedOutput()
    {
        var fixture = new Fixture();
        fixture.Metadata.FinalizeHeadConflict = true;
        var target = new MappingScope(fixture.Project.OrganizationId, fixture.Project.ProjectId, Guid.NewGuid());
        fixture.Metadata.AppendTarget = new(target, fixture.Profile.BackendId);
        var result = await fixture.Service().AppendAsync(fixture.Actor, target, fixture.Key,
            new AppendRevisionRequest(new EqualHead(Guid.NewGuid())), new SourceStream("native"u8.ToArray()));

        Assert.Equal(RevisionIngestionOutcome.HeadConflict, result.Outcome);
        var snapshot = await fixture.Service().GetOwnReceiptAsync(fixture.Actor, fixture.Project,
            result.Receipt!.OperationId);
        Assert.Equal(result.Receipt, snapshot);
        fixture.Access = null;
        Assert.Null(await fixture.Service().GetOwnReceiptAsync(fixture.Actor, fixture.Project,
            result.Receipt.OperationId));
        Assert.Null(snapshot!.CommittedSequence);
    }

    [Fact]
    public async Task TrustedReadIdentityMismatchPropagatesBeforeSourceOrObjectIO()
    {
        var fixture = new Fixture();
        await fixture.Create("native"u8.ToArray());
        fixture.Metadata.ReturnForeignReceipt = true;
        var source = new SourceStream("native"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().CreateAsync(
            fixture.Actor, fixture.Project, fixture.Key, fixture.Request, source));

        Assert.Equal(0, source.ReadCalls);
        Assert.Equal(1, fixture.Storage.PublishCalls);
        Assert.Equal(1, fixture.Backend.Calls);
    }

    private static IngestionAttemptCompletion SyntheticValidation(NativeArtifact artifact,
        IngestionBackendProfile profile, IngestionBackendStatus status, bool? valid,
        string? code = null, int total = 0)
    {
        using var diagnostics = JsonDocument.Parse(code is null ? "[]" :
            JsonSerializer.Serialize(new[] { new { code } }));
        var emitted = valid is null || code is null ? 0 : 1;
        var facts = new IngestionValidationFacts(status,
            new IngestionProvenance(artifact.Scope, artifact.Digest, profile, true),
            valid, Math.Max(total, emitted), emitted, false);
        return IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(facts, diagnostics.RootElement));
    }

    private sealed class Fixture : IProjectReader
    {
        public readonly List<string> Events = [];
        public readonly ActorContext Actor = new(new PrincipalId(Guid.NewGuid()));
        public readonly ProjectScope Project = new(Guid.NewGuid(), Guid.NewGuid());
        public readonly Guid Key = Guid.NewGuid();
        public readonly IngestionBackendProfile Profile = new("synthetic.backend", "synthetic.engine", "synthetic.worker", "synthetic.adapter");
        public readonly MetadataPort Metadata;
        public readonly StoragePort Storage;
        public readonly BackendPort Backend;
        public readonly RegistryPort Registry;
        public ProjectAccess? Access;
        public long StoreLimit;

        public Fixture(long storeLimit = IngestionAdmissionPolicy.MaximumNativeByteCount,
            long backendLimit = IngestionAdmissionPolicy.MaximumNativeByteCount)
        {
            StoreLimit = storeLimit;
            Access = new(Project, Actor.PrincipalId, ProjectRole.Publisher);
            Metadata = new(this);
            Storage = new(this);
            Backend = new(this);
            Registry = new(this, new(Profile, backendLimit, Backend));
        }

        public CreateRevisionRequest Request => new(new("Synthetic mapping", "Orchestration model only."), Profile.BackendId);
        public RevisionIngestionService Service(long applicationLimit = IngestionAdmissionPolicy.MaximumNativeByteCount) =>
            new(Metadata, Metadata, this, new(Storage, StoreLimit), Registry, new(applicationLimit));
        public Task<RevisionIngestionResult> Create(byte[] bytes) => Service().CreateAsync(Actor,
            Project, Key, Request, new SourceStream(bytes));
        public Task<ProjectAccess?> GetAccessAsync(ActorContext actor, ProjectScope scope, CancellationToken cancellationToken = default)
        {
            Events.Add("access");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Access);
        }
        public Task<ProjectPage> ListVisibleProjectsAsync(ActorContext actor, Guid organizationId, int limit,
            Guid? afterProjectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectSummary?> GetVisibleProjectAsync(ActorContext actor, ProjectScope scope,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void External(string name)
        {
            Assert.False(Metadata.InOperation);
            Events.Add(name);
        }
    }

    private sealed class RegistryPort(Fixture fixture, IngestionBackendRegistration registration) : IIngestionBackendRegistry
    {
        public bool CurrentAvailable = true;
        public bool ReservedAvailable = true;
        public IngestionBackendRegistration? GetCurrent(string backendId)
        {
            fixture.Events.Add("current");
            return CurrentAvailable && backendId == registration.Profile.BackendId ? registration : null;
        }
        public IngestionBackendRegistration? GetReserved(IngestionBackendProfile exactProfile)
        {
            fixture.Events.Add("reserved");
            return ReservedAvailable && exactProfile == registration.Profile ? registration : null;
        }
    }

    private sealed class BackendPort(Fixture fixture) : IIngestionAdmissionBackend
    {
        public int Calls;
        public NativeArtifact? Artifact;
        public Func<NativeArtifact, CancellationToken, Task<IngestionAttemptCompletion>>? Validate;
        public Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default)
        {
            fixture.External("validate");
            Calls++;
            Artifact = artifact;
            return Validate is null ? Task.FromResult(SyntheticValidation(artifact, fixture.Profile,
                IngestionBackendStatus.Succeeded, true)) : Validate(artifact, cancellationToken);
        }
    }

    private sealed class StoragePort(Fixture fixture) : IPrivateObjectStore
    {
        public int PublishCalls;
        public int ReadCalls;
        public byte[]? PublishedBytes;
        public RevisionScope? Scope;
        public PrivateObjectReference? Reference;
        public PrivateObjectStorageError? PublishError;
        public PrivateObjectPublishResult PublishResult = PrivateObjectPublishResult.Created;
        public Func<byte[], byte[]>? ReadTransform;
        public Func<string, CancellationToken, Task>? Before;

        public async Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default)
        {
            fixture.External("publish");
            PublishCalls++;
            Scope = expectedScope;
            Reference = reference;
            if (Before is not null) await Before("publish", cancellationToken);
            if (PublishError is { } error) throw new PrivateObjectStorageException(error);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            PublishedBytes = buffer.ToArray();
            Assert.Equal(reference.Digest, Sha256Digest.FromBytes(PublishedBytes));
            Assert.Equal(reference.ByteCount, PublishedBytes.LongLength);
            return PublishResult;
        }

        public async Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default)
        {
            fixture.External("read");
            ReadCalls++;
            Scope = expectedScope;
            Reference = reference;
            if (Before is not null) await Before("read", cancellationToken);
            if (PublishedBytes is null) throw new PrivateObjectStorageException(PrivateObjectStorageError.Missing);
            var copy = PublishedBytes.ToArray();
            return ReadTransform is null ? copy : ReadTransform(copy);
        }
    }

    private sealed class SourceStream(byte[] bytes, int initialOffset = 0) : Stream
    {
        private int _offset = initialOffset;
        public int ReadCalls;
        public int BytesRead;
        public int MaximumRequested;
        public bool Disposed;
        public Action<CancellationToken>? BeforeRead;
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
            BeforeRead?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class MetadataPort(Fixture fixture) : IIngestionMetadataStore, IIngestionWorkflowReader
    {
        private DateTimeOffset _time = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        private IngestionClaim? _claim;
        public IngestionReceipt? Receipt;
        public IngestionAppendTarget? AppendTarget;
        public bool Allowed = true;
        public bool InOperation;
        public bool LiveClaim;
        public bool FinalizeReturnsClaimLost;
        public bool FinalizeHeadConflict;
        public bool ReplaceValidationWithEvidenceLimit;
        public bool ReturnForeignReceipt;
        public int ReserveCalls;
        public int ClaimCalls;
        public int StageCalls;
        public int CompleteCalls;
        public int FinalizeCalls;
        public readonly List<Guid> ClaimedInstanceIds = [];
        public IngestionAttemptCompletion? LastSubmittedCompletion;
        public CancellationToken LastCompletionToken;
        public Task? LastOperationTask;
        public Func<string, CancellationToken, Task>? Before;

        private DateTimeOffset Now() => _time = _time.AddMilliseconds(1);
        public void ReleaseClaim() => LiveClaim = false;
        private async Task<T> Operation<T>(string name, CancellationToken token, Func<T> action)
        {
            Assert.False(InOperation);
            InOperation = true;
            fixture.Events.Add(name);
            try
            {
                if (Before is not null) await Before(name, token);
                token.ThrowIfCancellationRequested();
                return action();
            }
            finally { InOperation = false; }
        }

        public Task<IngestionReceipt?> FindOwnReceiptAsync(ActorContext actor, ProjectScope project,
            IngestionOperationKind kind, MappingScope? target, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
            Operation("find", cancellationToken, () =>
            {
                if (ReturnForeignReceipt && Receipt is not null) return Foreign();
                var receipt = Receipt;
                return Allowed && receipt is not null && receipt.Facts.IdempotencyKey == idempotencyKey &&
                    receipt.Facts.Intent.Kind == kind && receipt.Facts.Intent.Target == target ? receipt : null;
            });
        public Task<IngestionAppendTarget?> GetAppendTargetAsync(ActorContext actor, MappingScope target,
            CancellationToken cancellationToken = default) => Operation("target", cancellationToken,
                () => Allowed && AppendTarget?.Scope == target ? AppendTarget : null);
        public Task<IngestionReceipt?> GetReceiptAsync(ActorContext actor, ProjectScope project, Guid operationId,
            CancellationToken cancellationToken = default) => Operation("get", cancellationToken,
                () => Allowed && Receipt?.Facts.OperationId == operationId ? Receipt : null);

        public Task<IngestionStoreResult> ReserveAsync(ActorContext actor, ProjectScope project, Guid idempotencyKey,
            IngestionIntent intent, IngestionBackendProfile proposedProfile, IngestionAdmissionPolicy proposedPolicy,
            CancellationToken cancellationToken = default)
        {
            ReserveCalls++;
            return Operation("reserve", cancellationToken, () =>
            {
                if (!Allowed) return Hidden();
                if (Receipt is not null)
                    return Receipt.Facts.Intent == intent ? With(IngestionStoreOutcome.Existing) :
                        IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.IdempotencyConflict);
                var scope = new RevisionScope(project.OrganizationId, project.ProjectId,
                    intent.Target?.MappingId ?? Guid.NewGuid(), Guid.NewGuid());
                var reference = new PrivateObjectReference(PrivateObjectId.Create(scope, PrivateObjectClass.NativeArtifact),
                    intent.NativeDigest, intent.NativeByteCount);
                var facts = new ReservedIngestionFacts(project, Guid.NewGuid(), actor.PrincipalId,
                    idempotencyKey, CanonicalFingerprint.Create(actor.PrincipalId, project, intent), intent,
                    scope, reference, proposedProfile, proposedPolicy, Now());
                Receipt = new(facts, IngestionPhase.Reserved, 0, 0, null, null, null, []);
                return With(IngestionStoreOutcome.Reserved);
            });
        }

        public Task<IngestionStoreResult> ClaimAsync(ActorContext actor, ProjectScope project, Guid operationId,
            Guid serverInstanceId, CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            return Operation("claim", cancellationToken, () =>
            {
                if (!Allowed) return Hidden();
                var receipt = Receipt!;
                if (Terminal(receipt.Phase)) return With(IngestionStoreOutcome.Existing);
                if (LiveClaim) return With(IngestionStoreOutcome.InProgress);
                if (receipt.ClaimCount == IngestionWorkflowLimits.MaximumClaims)
                {
                    Receipt = Copy(receipt, IngestionPhase.AttemptExhausted, completed: Now());
                    return With(IngestionStoreOutcome.Existing);
                }
                var generation = receipt.CurrentGeneration + 1;
                var started = Now();
                var attempts = receipt.Attempts.Append(new IngestionAttemptReceipt(generation, started, null, null, null));
                Receipt = new(receipt.Facts, receipt.Phase, generation, (int)generation,
                    receipt.VerifiedStageGeneration, receipt.VerifiedStageAtUtc, receipt.EligibleAttemptGeneration, attempts);
                _claim = new(project, operationId, actor.PrincipalId, serverInstanceId, Guid.NewGuid(),
                    generation, started.AddSeconds(IngestionWorkflowLimits.ClaimLeaseSeconds));
                ClaimedInstanceIds.Add(serverInstanceId);
                LiveClaim = true;
                return IngestionStoreResult.ForClaim(Receipt, _claim);
            });
        }

        public Task<IngestionStoreResult> MarkStagedAsync(ActorContext actor, IngestionClaim claim,
            PrivateObjectReference verifiedNativeObject, CancellationToken cancellationToken = default)
        {
            StageCalls++;
            return Operation("stage", cancellationToken, () =>
            {
                if (!Allowed) return Hidden();
                if (!LiveClaim || claim != _claim) return Lost();
                Assert.Equal(Receipt!.Facts.NativeObject, verifiedNativeObject);
                var phase = Receipt.EligibleAttemptGeneration is null ? IngestionPhase.Staged : IngestionPhase.Ready;
                Receipt = new(Receipt.Facts, phase, Receipt.CurrentGeneration, Receipt.ClaimCount,
                    claim.Generation, Now(), Receipt.EligibleAttemptGeneration, Receipt.Attempts);
                return With(IngestionStoreOutcome.Applied);
            });
        }

        public Task<IngestionStoreResult> CompleteAttemptAsync(ActorContext actor, IngestionClaim claim,
            IngestionAttemptCompletion completion, CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            LastSubmittedCompletion = completion;
            LastCompletionToken = cancellationToken;
            var operation = Operation("complete", cancellationToken, () =>
            {
                if (!Allowed) return Hidden();
                if (!LiveClaim || claim != _claim || Receipt!.Attempts.Last().FinishedAtUtc is not null) return Lost();
                if (ReplaceValidationWithEvidenceLimit && completion.Kind == IngestionAttemptCompletionKind.Validation)
                    completion = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit,
                        completion.Evidence!.Facts.Provenance);
                var receipt = Receipt!;
                var classification = completion.Classify(receipt.Facts);
                var finished = Now();
                var attempts = receipt.Attempts.Take(receipt.Attempts.Count - 1).Append(
                    new IngestionAttemptReceipt(claim.Generation, receipt.Attempts.Last().StartedAtUtc,
                        finished, completion, classification));
                var eligible = classification is IngestionAdmissionClassification.EligibleValid or
                    IngestionAdmissionClassification.EligibleDecodedInvalid;
                var ready = eligible || classification == IngestionAdmissionClassification.ReusedEvidence;
                var rejected = classification is IngestionAdmissionClassification.RejectedNativeDecode or
                    IngestionAdmissionClassification.RejectedUnsupported or IngestionAdmissionClassification.RejectedResourceLimit;
                Receipt = new(receipt.Facts, ready ? IngestionPhase.Ready : rejected ? IngestionPhase.Rejected : receipt.Phase,
                    receipt.CurrentGeneration, receipt.ClaimCount, receipt.VerifiedStageGeneration, receipt.VerifiedStageAtUtc,
                    eligible ? claim.Generation : receipt.EligibleAttemptGeneration, attempts,
                    completedAtUtc: rejected ? finished : null);
                LiveClaim = ready;
                return With(IngestionStoreOutcome.Applied);
            });
            LastOperationTask = operation;
            return operation;
        }

        public Task<IngestionStoreResult> FinalizeAsync(ActorContext actor, IngestionClaim claim,
            CancellationToken cancellationToken = default)
        {
            FinalizeCalls++;
            return Operation("finalize", cancellationToken, () =>
            {
                if (!Allowed) return Hidden();
                if (!LiveClaim || claim != _claim || FinalizeReturnsClaimLost) return Lost();
                var receipt = Receipt!;
                var committedAt = Now();
                if (FinalizeHeadConflict)
                    Receipt = Copy(receipt, IngestionPhase.HeadConflict, completed: committedAt);
                else
                {
                    var initial = receipt.Attempts.Single(attempt => attempt.Generation == receipt.EligibleAttemptGeneration);
                    var committed = new CommittedRevisionFacts(receipt.Facts.FutureRevision, 1, receipt.Facts.NativeObject,
                        receipt.Facts.BackendProfile, IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity,
                        receipt.Facts.OriginalActor, committedAt, receipt.Facts.OperationId, initial.Generation,
                        initial.Classification!.Value);
                    Receipt = Copy(receipt, IngestionPhase.Committed, committed, committedAt);
                }
                LiveClaim = false;
                return With(IngestionStoreOutcome.Applied);
            });
        }

        private IngestionReceipt Foreign()
        {
            var original = Receipt!.Facts;
            var other = new PrincipalId(Guid.NewGuid());
            var facts = new ReservedIngestionFacts(original.Project, original.OperationId, other,
                original.IdempotencyKey, CanonicalFingerprint.Create(other, original.Project, original.Intent),
                original.Intent, original.FutureRevision, original.NativeObject, original.BackendProfile,
                original.AdmissionPolicy, original.ReservedAtUtc);
            return new(facts, IngestionPhase.Reserved, 0, 0, null, null, null, []);
        }
        private static IngestionReceipt Copy(IngestionReceipt receipt, IngestionPhase phase,
            CommittedRevisionFacts? committed = null, DateTimeOffset? completed = null) => new(receipt.Facts,
                phase, receipt.CurrentGeneration, receipt.ClaimCount, receipt.VerifiedStageGeneration,
                receipt.VerifiedStageAtUtc, receipt.EligibleAttemptGeneration, receipt.Attempts, committed, completed);
        private IngestionStoreResult With(IngestionStoreOutcome outcome) => IngestionStoreResult.ForReceipt(outcome, Receipt!);
        private static IngestionStoreResult Hidden() => IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.NotAuthorizedOrNotFound);
        private static IngestionStoreResult Lost() => IngestionStoreResult.WithoutReceipt(IngestionStoreOutcome.ClaimLost);
        private static bool Terminal(IngestionPhase phase) => phase is IngestionPhase.Committed or IngestionPhase.Rejected or
            IngestionPhase.HeadConflict or IngestionPhase.AttemptExhausted;
    }
}
