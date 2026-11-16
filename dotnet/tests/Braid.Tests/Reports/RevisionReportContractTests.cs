using System.Text;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>Constructor and identity contracts using explicitly synthetic typed publication facts.</summary>
public sealed class RevisionReportContractTests
{
    private const string CanonicalPolicy = """{"schemaVersion":"braid.revision-report-policy.v1","nativeByteLimit":4194304,"workerResponseByteLimit":8388608,"itemLimit":8192,"responseItemLimit":32768,"jsonDepthLimit":64,"detailDepthLimit":16,"diagnosticLimit":128,"validationIssueLimit":32768,"textByteLimit":2048,"headerByteLimit":16384,"storedObjectByteLimit":16777216,"responseByteLimit":16777216}""";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static RevisionScope Scope => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static Guid NativeId => Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static Guid DerivedId => Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static Guid CacheId => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static Guid ResultId => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static PrincipalId Actor => new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static Sha256Digest Digest => Sha256Digest.Parse("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    private static RevisionReportBackendProfile Profile => new("braid.synthetic", "engine-1", "engine-build-1", "build-1");

    [Fact]
    public void PolicyHasAnIndependentCanonicalVectorAndOwnsReturnedBytes()
    {
        var policy = new RevisionReportPolicy();
        Assert.Equal(CanonicalPolicy, Encoding.UTF8.GetString(policy.CopyCanonicalUtf8()));
        Assert.Equal("9f4e62be9b7834ccb1de3bd301ff4dfe0e068a5361dcb6bf0dc74b5f8d5de339", policy.Fingerprint.Value);
        var bytes = policy.CopyCanonicalUtf8();
        bytes[0] = 0;
        Assert.Equal(CanonicalPolicy, Encoding.UTF8.GetString(policy.CopyCanonicalUtf8()));
        Assert.Equal(new RevisionReportPolicy(), policy);
        Assert.Contains(new RevisionReportPolicy(), new HashSet<RevisionReportPolicy> { policy });
    }

    [Fact]
    public void FullKeyHasAnIndependentCanonicalVectorAndDeterministicRecordEquality()
    {
        const string expected = """{"schemaVersion":"braid.revision-report-key.v1","operation":"projection","scope":{"organizationId":"11111111-1111-1111-1111-111111111111","projectId":"22222222-2222-2222-2222-222222222222","mappingId":"33333333-3333-3333-3333-333333333333","revisionId":"44444444-4444-4444-4444-444444444444"},"nativeObject":{"objectId":"66666666-6666-6666-6666-666666666666","class":"native","digest":"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","byteCount":3},"logicalDocument":"entry.native.json","dependencyIdentity":"braid.dependencies.empty.v1","sourceBackendId":"braid.synthetic","profile":{"backendId":"braid.synthetic","backendRevision":"engine-1","backendBuild":"engine-build-1","adapterBuild":"build-1"},"reportSchemaVersion":"braid.mapping-projection.v1","algorithmVersion":"braid.synthetic-projection.v1","codecVersion":"braid.synthetic-codec.v1","policy":""" + CanonicalPolicy + ""","policyFingerprint":"9f4e62be9b7834ccb1de3bd301ff4dfe0e068a5361dcb6bf0dc74b5f8d5de339"}""";
        var key = Key();
        Assert.Equal(expected, Encoding.UTF8.GetString(key.CopyCanonicalUtf8()));
        Assert.Equal("0b8cd4ba3c80a23910bc54e5fb621976c563b5b9723a58c72f8ad48878bb64a3", key.Fingerprint.Value);
        var copy = key.CopyCanonicalUtf8();
        copy[0] = 0;
        Assert.Equal(expected, Encoding.UTF8.GetString(key.CopyCanonicalUtf8()));
        Assert.Equal(Key(), key);
        Assert.Contains(Key(), new HashSet<RevisionReportKey> { key });
        Assert.Equal(Scope, key.Scope);
        Assert.Equal(Digest, key.NativeDigest);
    }

    [Fact]
    public void EveryAdditionalIdentityFactAndOperationSeparatesTheKey()
    {
        var original = Key();
        RevisionReportKey[] changed =
        [
            Key(source: Source(objectId: DerivedId)),
            Key(source: Source(byteCount: 4)),
            Key(source: Source(logical: "other.native.json")),
            Key(source: Source(dependency: "braid.dependencies.other.v1")),
            Key(algorithm: "braid.synthetic-projection.v2"),
            Key(codec: "braid.synthetic-codec.v2"),
            new ValidationCacheKey(Source(), Profile, original.SchemaVersion, original.AlgorithmVersion, original.CodecVersion, original.Policy)
        ];
        Assert.All(changed, key =>
        {
            Assert.NotEqual(original, key);
            Assert.NotEqual(original.Fingerprint, key.Fingerprint);
        });
        for (var index = 0; index < 12; index++)
        {
            var limits = DefaultLimits();
            limits[index]--;
            var key = Key(policy: Policy(limits));
            Assert.NotEqual(original, key);
            Assert.NotEqual(original.Fingerprint, key.Fingerprint);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void EveryEffectiveLimitRejectsZeroAndAboveItsHardCeiling(int index)
    {
        var limits = DefaultLimits();
        limits[index] = 1;
        _ = Policy(limits);
        limits[index] = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(limits));
        limits[index] = DefaultLimits()[index] + 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(limits));
        limits[index] = int.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(limits));
    }

    [Fact]
    public void SourceRefusesWrongClassScopeAndNativeSize()
    {
        var wrongScope = new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, DerivedId);
        Assert.Throws<ArgumentException>(() => new RevisionReportSource(Scope,
            Reference(wrongScope, NativeId, PrivateObjectClass.NativeArtifact, 3), "entry.native.json", "dependencies", Profile.BackendId));
        Assert.Throws<ArgumentException>(() => new RevisionReportSource(Scope,
            Reference(Scope, NativeId, PrivateObjectClass.DerivedArtifact, 3), "entry.native.json", "dependencies", Profile.BackendId));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(byteCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(byteCount: 4194305));
        _ = Source(byteCount: 4194304);
        Assert.Throws<ArgumentNullException>(() => new RevisionReportSource(null!, Reference(Scope, NativeId, PrivateObjectClass.NativeArtifact, 3), "entry", "dependencies", "backend"));
    }

    [Fact]
    public void IdentityTextIsExactStrictUnicodeAndHasUtf8ByteBounds()
    {
        Assert.NotEqual(Key(source: Source(logical: "entry")), Key(source: Source(logical: " entry ")));
        Assert.NotEqual(Key(source: Source(logical: "\u00e9")), Key(source: Source(logical: "e\u0301")));
        _ = Source(logical: new string('\u00e9', 128));
        Assert.Throws<ArgumentException>(() => Source(logical: new string('\u00e9', 129)));
        var malformed = "entry" + new string((char)0xd800, 1);
        Assert.Throws<ArgumentException>(() => Source(logical: malformed));
        Assert.Throws<ArgumentException>(() => Source(dependency: "dependency\0id"));
        Assert.Throws<ArgumentException>(() => new RevisionReportBackendProfile("backend", malformed, "build", "adapter"));
        Assert.Throws<ArgumentException>(() => Key(codec: malformed));
        Assert.Throws<ArgumentException>(() => Key(algorithm: " "));
        Assert.Throws<ArgumentException>(() => new ProjectionCacheKey(Source(), Profile, "", "algorithm", "codec", new RevisionReportPolicy()));
        Assert.Throws<ArgumentException>(() => Key(codec: new string('x', 129)));
        Assert.Throws<ArgumentException>(() => Key(profile: new RevisionReportBackendProfile("other", "revision", "build", "adapter")));
    }

    [Fact]
    public void UsableProjectionRequiresEveryObservedProvenanceFactAndAnActualDocument()
    {
        var key = Key();
        var wrongScope = new RevisionScope(Scope.OrganizationId, Scope.ProjectId, Scope.MappingId, DerivedId);
        RevisionReportObservation[] refused =
        [
            Projection(key, confirmed: false),
            Projection(key, provenance: new RevisionReportProvenance(wrongScope, Digest, Profile, true)),
            Projection(key, provenance: new RevisionReportProvenance(Scope, Sha256Digest.FromBytes("other"u8), Profile, true)),
            Projection(key, provenance: new RevisionReportProvenance(Scope, Digest, new RevisionReportBackendProfile(Profile.BackendId, "new", Profile.BackendBuild, Profile.AdapterBuild), true)),
            Projection(key, status: RevisionReportBackendStatus.InvalidMapping),
            new(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded, Provenance(key), null, null),
            new(RevisionReportOperation.Projection, RevisionReportBackendStatus.Succeeded, Provenance(key), new(null, 0, 0, 0, 1), null),
            new(RevisionReportOperation.Projection, null, Provenance(key), new(RevisionReportCoverage.Complete, 0, 0, 0, 0), null)
        ];
        Assert.All(refused, observation => Assert.Throws<ArgumentException>(() => RevisionReportCompletion.ForUsable(key, observation, Derived())));
        var accepted = RevisionReportCompletion.ForUsable(key, Projection(key), Derived());
        Assert.True(accepted.IsUsable);
        Assert.Equal(key, accepted.Key);
        Assert.Null(accepted.Failure);
    }

    [Fact]
    public void ValidationEligibilityUsesActualStatusAndBoolWithoutInferringFromCounts()
    {
        var key = ValidationKey();
        var validWithWarning = Validation(key, RevisionReportBackendStatus.Succeeded, new(true, 1, 1, true, 1));
        var invalidWithoutFindings = Validation(key, RevisionReportBackendStatus.InvalidMapping, new(false, 0, 0, false, 0));
        Assert.True(RevisionReportCompletion.ForUsable(key, validWithWarning, Derived()).IsUsable);
        Assert.True(RevisionReportCompletion.ForUsable(key, invalidWithoutFindings, Derived()).IsUsable);
        Assert.True(validWithWarning.Validation!.Truncated);
        RevisionReportObservation[] refused =
        [
            Validation(key, RevisionReportBackendStatus.Succeeded, new(false, 0, 0, false, 0)),
            Validation(key, RevisionReportBackendStatus.InvalidMapping, new(true, 0, 0, false, 0)),
            Validation(key, RevisionReportBackendStatus.InvalidMapping, new(null, 0, 0, false, 1)),
            Validation(key, RevisionReportBackendStatus.BackendFailure, new(true, 0, 0, false, 0)),
            new(RevisionReportOperation.Validation, RevisionReportBackendStatus.Succeeded, Provenance(key), null, null)
        ];
        Assert.All(refused, observation => Assert.Throws<ArgumentException>(() => RevisionReportCompletion.ForUsable(key, observation, Derived())));
    }

    [Fact]
    public void SummariesEnforceCoherenceWithoutInventingCoverageOrValidity()
    {
        Assert.Throws<ArgumentException>(() => new RevisionReportProjectionSummary(null, 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new RevisionReportProjectionSummary(RevisionReportCoverage.Complete, 1, 0, 1, 0));
        Assert.Throws<ArgumentException>(() => new RevisionReportProjectionSummary(RevisionReportCoverage.Partial, 0, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new RevisionReportProjectionSummary(RevisionReportCoverage.Unavailable, 1, 1, 1, 0));
        Assert.Equal(1, new RevisionReportProjectionSummary(RevisionReportCoverage.Unavailable, 1, 0, 1, 0).DocumentItemCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportProjectionSummary(RevisionReportCoverage.Complete, 8193, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new RevisionReportValidationSummary(true, 1, 1, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportValidationSummary(null, 32769, 0, false, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportValidationSummary(null, 129, 129, false, 1));
        Assert.Null(new RevisionReportValidationSummary(null, 0, 0, false, 1).IsValid);
        Assert.False(new RevisionReportValidationSummary(false, 2, 1, false, 1).Truncated);
    }

    [Fact]
    public void FailuresPreserveHonestObservationsAndAbsenceIncludingReducedPolicyRefusal()
    {
        var key = Key(policy: new RevisionReportPolicy(itemLimit: 1));
        var actual = Projection(key, summary: new(RevisionReportCoverage.Complete, 2, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportCompletion.ForUsable(key, actual, Derived()));
        var failed = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.ResourceLimit, actual);
        Assert.False(failed.IsUsable);
        Assert.Null(failed.Key);
        Assert.Null(failed.DerivedObject);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, failed.Observation!.Status);
        Assert.Equal(2, failed.Observation.Projection!.DocumentItemCount);
        var absent = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo);
        var empty = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo,
            new(RevisionReportOperation.Projection, null, null, null, null));
        Assert.Null(absent.Observation);
        Assert.NotNull(empty.Observation);
        Assert.NotEqual(absent, empty);
    }

    [Fact]
    public void DerivedPublicationRefusesForeignScopeWrongClassEmptyAndOversizedObjects()
    {
        var key = Key();
        var foreign = new RevisionScope(Scope.OrganizationId, Scope.ProjectId, DerivedId, Scope.RevisionId);
        PrivateObjectReference[] refused =
        [
            Reference(foreign, DerivedId, PrivateObjectClass.DerivedArtifact, 1),
            Reference(Scope, DerivedId, PrivateObjectClass.NativeArtifact, 1),
            Reference(Scope, DerivedId, PrivateObjectClass.DerivedArtifact, 0),
            Reference(Scope, DerivedId, PrivateObjectClass.DerivedArtifact, 16777217)
        ];
        Assert.All(refused, reference => Assert.Throws<ArgumentException>(() => RevisionReportCompletion.ForUsable(key, Projection(key), reference)));
        var smaller = Key(policy: new RevisionReportPolicy(storedObjectByteLimit: 1));
        Assert.Throws<ArgumentException>(() => RevisionReportCompletion.ForUsable(smaller, Projection(smaller), Derived(2)));
        var nativeCap = Key(policy: new RevisionReportPolicy(nativeByteLimit: 2));
        Assert.Throws<ArgumentException>(() => RevisionReportCompletion.ForUsable(nativeCap, Projection(nativeCap), Derived()));
    }

    [Fact]
    public void ClaimHasExactLeaseFullScopeAndNonzeroFenceWithUncappedPositiveGeneration()
    {
        var key = Key();
        Assert.Equal(long.MaxValue, Claim(key, generation: long.MaxValue).Generation);
        Assert.Throws<ArgumentOutOfRangeException>(() => Claim(key, generation: 0));
        Assert.Throws<ArgumentException>(() => Claim(key, cache: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Claim(key, derivation: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Claim(key, instance: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Claim(key, token: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Claim(key, expiry: Start.AddSeconds(59)));
        Assert.Throws<ArgumentException>(() => Claim(key, expiry: Start.AddSeconds(61)));
        Assert.Throws<ArgumentException>(() => Claim(key, candidate: PrivateObjectId.Restore(Scope, DerivedId, PrivateObjectClass.NativeArtifact)));
        var foreign = new RevisionScope(Scope.OrganizationId, DerivedId, Scope.MappingId, Scope.RevisionId);
        Assert.Throws<ArgumentException>(() => Claim(key, candidate: PrivateObjectId.Restore(foreign, DerivedId, PrivateObjectClass.DerivedArtifact)));
        Assert.Throws<ArgumentException>(() => Claim(key, repair: ResultId));
        Assert.Throws<ArgumentException>(() => Claim(key, repair: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new AuthorizedRevisionReportSource(Source(), Actor, Start.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void EmptyKeysAndExpiredIntentRemainTruthfulReadSnapshots()
    {
        var key = Key();
        foreach (Guid? id in new Guid?[] { null, CacheId })
        {
            var empty = new RevisionReportReadSnapshot(Authorized(key), key, id, 0, null, null);
            Assert.Equal(RevisionReportState.NotDerived, empty.State);
            Assert.False(empty.IsClaimLive);
            Assert.Null(empty.NextAttemptAtUtc);
        }
        var current = OpenAttempt();
        var live = new RevisionReportReadSnapshot(Authorized(key, Start.AddSeconds(59)), key, CacheId, 1, null, current);
        var expired = new RevisionReportReadSnapshot(Authorized(key, Start.AddSeconds(60)), key, CacheId, 1, null, current);
        Assert.True(live.IsClaimLive);
        Assert.Equal(RevisionReportState.Pending, expired.State);
        Assert.False(expired.IsClaimLive);
        Assert.Equal(Start.AddSeconds(60), expired.NextAttemptAtUtc);
        Assert.Null(expired.CurrentAttempt!.Completion);
        Assert.Throws<ArgumentException>(() => new RevisionReportReadSnapshot(Authorized(key), key, CacheId, 0, null, current));
        Assert.Throws<ArgumentException>(() => new RevisionReportReadSnapshot(Authorized(key), key, CacheId, 1, null, null));
        Assert.Throws<ArgumentException>(() => new RevisionReportReadSnapshot(Authorized(key), key, null, 1, null, current));
    }

    [Fact]
    public void TerminalAttemptNeedsPairedFactsAndDatabaseRetryClock()
    {
        var key = Key();
        var completion = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.TimedOut);
        var attempt = new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), Start.AddSeconds(10), completion);
        var failed = new RevisionReportReadSnapshot(Authorized(key, Start.AddSeconds(39)), key, CacheId, 1, null, attempt);
        Assert.Equal(RevisionReportState.Failed, failed.State);
        Assert.False(failed.IsClaimLive);
        Assert.Equal(Start.AddSeconds(40), failed.NextAttemptAtUtc);
        Assert.Equal(RevisionReportStoreOutcome.RetryDelayed, RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.RetryDelayed, failed).Outcome);
        var readyToRetry = new RevisionReportReadSnapshot(Authorized(key, Start.AddSeconds(40)), key, CacheId, 1, null, attempt);
        Assert.Throws<ArgumentException>(() => RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.RetryDelayed, readyToRetry));
        Assert.Throws<ArgumentException>(() => new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), null, completion));
        Assert.Throws<ArgumentException>(() => new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), Start, null));
        Assert.Throws<ArgumentException>(() => new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), Start.AddSeconds(60), completion));
    }

    [Fact]
    public void ImmutableResultIsSeparateFromCacheSelectionAndCurrentReadersIdentity()
    {
        var key = Key();
        var observation = Projection(key);
        var result = new RevisionReportResult(ResultId, CacheId, key, 1, Actor, Start, Start.AddSeconds(10), Derived(), observation);
        var completion = RevisionReportCompletion.ForUsable(key, observation, Derived());
        var attempt = new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), Start.AddSeconds(10), completion);
        var reader = new PrincipalId(Guid.Parse("88888888-8888-8888-8888-888888888888"));
        var source = new AuthorizedRevisionReportSource(key.Source, reader, Start.AddHours(1));
        var retained = new AuthorizedRevisionReportAttempt(source, key, CacheId, attempt, result);
        var snapshot = new RevisionReportReadSnapshot(source, key, CacheId, 1, result, attempt);
        Assert.Equal(RevisionReportState.Available, snapshot.State);
        Assert.Equal(Actor, retained.Result!.OriginalActor);
        Assert.NotEqual(reader, retained.Result.OriginalActor);
        Assert.NotEqual(result.DerivationId, result.CacheEntryId);
        Assert.Null(snapshot.NextAttemptAtUtc);
        Assert.Throws<ArgumentException>(() => new AuthorizedRevisionReportAttempt(source, key, CacheId, attempt, null));
        Assert.Throws<ArgumentException>(() => new RevisionReportReadSnapshot(source, key, CacheId, 1, result, OpenAttempt()));
        Assert.Throws<ArgumentException>(() => new AuthorizedRevisionReportAttempt(source, key, Guid.NewGuid(), attempt, result));
        var repair = new RevisionReportRepairTarget(key, CacheId, ResultId, result.DerivedObject, RevisionReportRepairReason.Integrity);
        var nextClaim = Claim(key, derivation: Guid.NewGuid(), generation: 2, repair: repair.DerivationId);
        Assert.Equal(result.DerivationId, nextClaim.RepairTargetDerivationId);
        Assert.Equal(1, result.Generation);
        Assert.Equal(Derived(), result.DerivedObject);
    }

    [Fact]
    public void ExactRetainedLookupCanRepresentFailedOrUnfinishedAttemptsWithoutPublicResult()
    {
        var key = Key();
        var unfinished = new AuthorizedRevisionReportAttempt(Authorized(key), key, CacheId, OpenAttempt(), null);
        Assert.Null(unfinished.Result);
        Assert.Null(unfinished.Attempt.Completion);
        var failure = RevisionReportCompletion.ForFailure(RevisionReportFailureCategory.StorageIo, Projection(key));
        var attempt = new RevisionReportAttemptSummary(ResultId, 1, Start, Start.AddSeconds(60), Start.AddSeconds(10), failure);
        var failed = new AuthorizedRevisionReportAttempt(Authorized(key), key, CacheId, attempt, null);
        Assert.Equal(RevisionReportBackendStatus.Succeeded, failed.Attempt.Completion!.Observation!.Status);
        Assert.Null(failed.Result);
    }

    [Fact]
    public void ResultFactoriesRejectContradictoryOutcomeAndFencingData()
    {
        var key = Key();
        var snapshot = new RevisionReportReadSnapshot(Authorized(key), key, CacheId, 1, null, OpenAttempt());
        Assert.Equal(RevisionReportStoreOutcome.Claimed, RevisionReportStoreResult.ForClaim(snapshot, Claim(key)).Outcome);
        Assert.Throws<ArgumentException>(() => RevisionReportStoreResult.ForClaim(snapshot, Claim(key, derivation: Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Available, snapshot));
        Assert.Throws<ArgumentException>(() => RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Applied, snapshot));
        foreach (var outcome in new[] { RevisionReportStoreOutcome.ClaimLost, RevisionReportStoreOutcome.SelectionChanged,
                     RevisionReportStoreOutcome.GenerationExhausted, RevisionReportStoreOutcome.NotAuthorizedOrNotFound })
        {
            var result = RevisionReportStoreResult.WithoutData(outcome);
            Assert.Null(result.Snapshot);
            Assert.Null(result.Claim);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.Claimed));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportStoreResult.WithoutData((RevisionReportStoreOutcome)999));
    }

    [Fact]
    public void UnknownCategoriesAndOtherOperationSummariesFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportObservation((RevisionReportOperation)999, null, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportObservation(RevisionReportOperation.Projection, (RevisionReportBackendStatus)999, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportProjectionSummary((RevisionReportCoverage)999, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionReportCompletion.ForFailure((RevisionReportFailureCategory)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionReportRepairTarget(Key(), CacheId, ResultId, Derived(), (RevisionReportRepairReason)999));
        Assert.Throws<ArgumentException>(() => new RevisionReportObservation(RevisionReportOperation.Projection, null, null, null, new(null, 0, 0, false, 0)));
        Assert.Throws<ArgumentException>(() => new RevisionReportObservation(RevisionReportOperation.Validation, null, null, new(null, 0, 0, 0, 0), null));
    }

    [Fact]
    public void MetadataRecordsExposeNoMutablePropertyAliases()
    {
        Type[] types = [typeof(RevisionReportSource), typeof(RevisionReportBackendProfile), typeof(RevisionReportPolicy),
            typeof(ProjectionCacheKey), typeof(ValidationCacheKey), typeof(RevisionReportObservation),
            typeof(RevisionReportCompletion), typeof(RevisionReportClaim), typeof(RevisionReportAttemptSummary),
            typeof(RevisionReportResult), typeof(AuthorizedRevisionReportAttempt), typeof(RevisionReportReadSnapshot),
            typeof(RevisionReportRepairTarget), typeof(RevisionReportStoreResult)];
        Assert.All(types, type => Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod)));
    }

    private static RevisionReportSource Source(Guid? objectId = null, long byteCount = 3,
        string logical = "entry.native.json", string dependency = "braid.dependencies.empty.v1") =>
        new(Scope, Reference(Scope, objectId ?? NativeId, PrivateObjectClass.NativeArtifact, byteCount), logical, dependency, Profile.BackendId);

    private static ProjectionCacheKey Key(RevisionReportSource? source = null, RevisionReportBackendProfile? profile = null,
        RevisionReportPolicy? policy = null, string algorithm = "braid.synthetic-projection.v1", string codec = "braid.synthetic-codec.v1") =>
        new(source ?? Source(), profile ?? Profile, "braid.mapping-projection.v1", algorithm, codec, policy ?? new RevisionReportPolicy());

    private static ValidationCacheKey ValidationKey() =>
        new(Source(), Profile, "braid.mapping-validation.v1", "braid.synthetic-validation.v1", "braid.synthetic-codec.v1", new RevisionReportPolicy());

    private static PrivateObjectReference Reference(RevisionScope scope, Guid objectId, PrivateObjectClass @class, long byteCount) =>
        new(PrivateObjectId.Restore(scope, objectId, @class), Digest, byteCount);

    private static PrivateObjectReference Derived(long bytes = 1) => Reference(Scope, DerivedId, PrivateObjectClass.DerivedArtifact, bytes);
    private static RevisionReportProvenance Provenance(RevisionReportKey key, bool confirmed = true) => new(key.Scope, key.NativeDigest, key.Profile, confirmed);

    private static RevisionReportObservation Projection(RevisionReportKey key, bool confirmed = true,
        RevisionReportProvenance? provenance = null, RevisionReportBackendStatus status = RevisionReportBackendStatus.Succeeded,
        RevisionReportProjectionSummary? summary = null) =>
        new(RevisionReportOperation.Projection, status, provenance ?? Provenance(key, confirmed),
            summary ?? new RevisionReportProjectionSummary(RevisionReportCoverage.Complete, 0, 0, 0, 0), null);

    private static RevisionReportObservation Validation(RevisionReportKey key, RevisionReportBackendStatus status, RevisionReportValidationSummary summary) =>
        new(RevisionReportOperation.Validation, status, Provenance(key), null, summary);

    private static AuthorizedRevisionReportSource Authorized(RevisionReportKey key, DateTimeOffset? observed = null) => new(key.Source, Actor, observed ?? Start);

    private static RevisionReportClaim Claim(RevisionReportKey key, Guid? cache = null, Guid? derivation = null,
        Guid? instance = null, Guid? token = null, long generation = 1, DateTimeOffset? expiry = null,
        PrivateObjectId? candidate = null, Guid? repair = null) =>
        new(cache ?? CacheId, derivation ?? ResultId, key, Actor,
            instance ?? Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), token ?? Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            generation, Start, expiry ?? Start.AddSeconds(60), candidate ?? Derived().Id, repair);

    private static RevisionReportAttemptSummary OpenAttempt() => new(ResultId, 1, Start, Start.AddSeconds(60), null, null);
    private static int[] DefaultLimits() => [4194304, 8388608, 8192, 32768, 64, 16, 128, 32768, 2048, 16384, 16777216, 16777216];
    private static RevisionReportPolicy Policy(int[] limits) => new(limits[0], limits[1], limits[2], limits[3], limits[4], limits[5],
        limits[6], limits[7], limits[8], limits[9], limits[10], limits[11]);
}
