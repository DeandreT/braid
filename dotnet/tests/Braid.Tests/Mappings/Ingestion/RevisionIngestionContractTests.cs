using System.Reflection;
using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Tests;

/// <summary>
/// Admission evidence and adapter ports here are explicitly SYNTHETIC. These
/// tests qualify Application contracts, never native, store, or database I/O.
/// </summary>
public sealed class RevisionIngestionContractTests
{
    private static Guid OrganizationId => Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static Guid ProjectId => Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static Guid MappingId => Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static Guid RevisionId => Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static Guid OperationId => Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static Guid NativeObjectId => Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static Guid ActorId => Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static Guid OtherId => Guid.Parse("88888888-8888-4888-8888-888888888888");
    private static ProjectScope Project => new(OrganizationId, ProjectId);
    private static MappingScope Target => new(OrganizationId, ProjectId, MappingId);
    private static RevisionScope Scope => new(OrganizationId, ProjectId, MappingId, RevisionId);
    private static PrincipalId Actor => new(ActorId);
    private static Sha256Digest Digest => Sha256Digest.FromBytes("synthetic-native-body"u8);
    private static IngestionBackendProfile Profile => new("ferrule", "SYNTHETIC-private-engine",
        "SYNTHETIC-private-backend-image", "SYNTHETIC-private-adapter");
    private static DateTimeOffset Timestamp => new(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void RequestsPreserveValidatedDomainValuesAndDefaultOnlyAbsentFilenameAndHead()
    {
        var metadata = new IngestionCreateMetadata("  Résumé  ", "description kept exactly \n");
        var create = new CreateRevisionRequest(metadata, "Ferrule");
        var append = new AppendRevisionRequest();
        var head = new EqualHead(RevisionId);
        var explicitAppend = new AppendRevisionRequest(head, "測定.NATIVE.JSON");

        Assert.Same(metadata, create.Metadata);
        Assert.Equal("Résumé", create.Metadata.Name);
        Assert.Equal("description kept exactly \n", create.Metadata.Description);
        Assert.Equal("Ferrule", create.BackendId);
        Assert.Equal("entry.native.json", create.OriginalFilename);
        Assert.IsType<AnyHead>(append.HeadCondition);
        Assert.Equal("entry.native.json", append.OriginalFilename);
        Assert.Same(head, explicitAppend.HeadCondition);
        Assert.Equal("測定.NATIVE.JSON", explicitAppend.OriginalFilename);
        Assert.Equal(new AnyHead(), new AppendRevisionRequest(null).HeadCondition);
        AssertRejects(() => new CreateRevisionRequest(null!, "ferrule"));
        AssertRejects(() => new EqualHead(Guid.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("directory/file.json")]
    [InlineData("directory\\file.json")]
    [InlineData("file\0.json")]
    [InlineData("file\n.json")]
    [InlineData("file\u007f.json")]
    public void BothRequestsRejectUnsafeDisplayFilenames(string filename)
    {
        AssertRejects(() => new CreateRevisionRequest(new IngestionCreateMetadata("Name", ""), "ferrule", filename));
        AssertRejects(() => new AppendRevisionRequest(originalFilename: filename));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("nul\0backend")]
    public void BackendIdsRejectMissingBlankAndUnpersistableText(string? backendId)
    {
        AssertRejects(() => new CreateRevisionRequest(new IngestionCreateMetadata("Name", ""), backendId!));
        AssertRejects(() => new IngestionAppendTarget(Target, backendId!));
    }

    [Fact]
    public void RequestAndAppendTargetUtf8GuardsMatchDomainByteAndScalarSemantics()
    {
        var metadata = new IngestionCreateMetadata(string.Concat(Enumerable.Repeat("😀", 200)),
            string.Concat(Enumerable.Repeat("😀", 4000)));
        var backendId = new string('é', 64);
        var filename = new string('é', 127) + "a";
        var request = new CreateRevisionRequest(metadata, backendId, filename);

        Assert.Same(metadata, request.Metadata);
        Assert.Equal(128, Encoding.UTF8.GetByteCount(request.BackendId));
        Assert.Equal(255, Encoding.UTF8.GetByteCount(request.OriginalFilename));
        Assert.Equal(filename, new AppendRevisionRequest(originalFilename: filename).OriginalFilename);
        Assert.Equal(backendId, new IngestionAppendTarget(Target, backendId).BackendId);
        AssertRejects(() => new CreateRevisionRequest(metadata, backendId + "a"));
        AssertRejects(() => new IngestionAppendTarget(Target, backendId + "a"));
        AssertRejects(() => new CreateRevisionRequest(metadata, "ferrule", filename + "a"));
        AssertRejects(() => new AppendRevisionRequest(originalFilename: filename + "a"));
        AssertRejects(() => new IngestionCreateMetadata(metadata.Name + "😀", ""));
        AssertRejects(() => new IngestionCreateMetadata("Name", metadata.Description + "😀"));
    }

    [Fact]
    public void RuntimeConstructedMalformedUnicodeCannotEnterRequestsOrAppendTargets()
    {
        // Construct at runtime so test discovery never serializes malformed UTF16 InlineData.
        foreach (var malformed in new[] { new string((char)0xd800, 1), new string((char)0xdfff, 1) })
        {
            AssertRejects(() => new CreateRevisionRequest(new IngestionCreateMetadata("Name", ""), "backend" + malformed));
            AssertRejects(() => new CreateRevisionRequest(new IngestionCreateMetadata("Name", ""), "ferrule", "file" + malformed));
            AssertRejects(() => new AppendRevisionRequest(originalFilename: "file" + malformed));
            AssertRejects(() => new IngestionAppendTarget(Target, "backend" + malformed));
            AssertRejects(() => new IngestionCreateMetadata("name" + malformed, ""));
            AssertRejects(() => new IngestionCreateMetadata("Name", "description" + malformed));
        }
    }

    [Fact]
    public void AppendTargetPreservesItsExactScopeAndOrdinalBackendSpelling()
    {
        var target = new IngestionAppendTarget(Target, " Ferrule ");
        Assert.Equal(Target, target.Scope);
        Assert.Equal(" Ferrule ", target.BackendId);
        Assert.NotEqual(target, new IngestionAppendTarget(new MappingScope(OtherId, ProjectId, MappingId), " Ferrule "));
        Assert.NotEqual(target, new IngestionAppendTarget(new MappingScope(OrganizationId, OtherId, MappingId), " Ferrule "));
        Assert.NotEqual(target, new IngestionAppendTarget(new MappingScope(OrganizationId, ProjectId, OtherId), " Ferrule "));
        Assert.NotEqual(target, new IngestionAppendTarget(Target, "ferrule"));
        AssertRejects(() => new IngestionAppendTarget(null!, "ferrule"));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(4194305L)]
    [InlineData(long.MaxValue)]
    public void ServerLimitsCannotExceedTheNativeHardCeilingOrPermitAnEmptyBudget(long limit)
    {
        AssertRejects(() => new RevisionIngestionOptions(limit));
        AssertRejects(() => new IngestionBackendRegistration(Profile, limit, new SyntheticAdmissionBackend()));
        AssertRejects(() => new IngestionPrivateStoreRegistration(new SyntheticPrivateStore(), limit));
    }

    [Fact]
    public void ServerLimitsAcceptPositiveReductionsAndRegistrationsRequireRealPortReferences()
    {
        Assert.Equal(4194304, new RevisionIngestionOptions().NativeByteLimit);
        foreach (var limit in new[] { 1L, 1024L, 4194304L })
        {
            var backend = new SyntheticAdmissionBackend();
            var store = new SyntheticPrivateStore();
            var backendRegistration = new IngestionBackendRegistration(Profile, limit, backend);
            var storeRegistration = new IngestionPrivateStoreRegistration(store, limit);
            Assert.Equal(limit, new RevisionIngestionOptions(limit).NativeByteLimit);
            Assert.Equal(limit, backendRegistration.NativeByteLimit);
            Assert.Equal(limit, storeRegistration.NativeByteLimit);
            Assert.Same(backend, backendRegistration.Backend);
            Assert.Same(store, storeRegistration.Store);
            Assert.Equal(Profile, backendRegistration.Profile);
        }
        AssertRejects(() => new IngestionBackendRegistration(null!, 1, new SyntheticAdmissionBackend()));
        AssertRejects(() => new IngestionBackendRegistration(Profile, 1, null!));
        AssertRejects(() => new IngestionPrivateStoreRegistration(null!, 1));
    }

    [Fact]
    public void RegistryOwnsInputCollectionsAndRetainsOldExactProfilesWithoutUpgradingRetries()
    {
        var oldProfile = new IngestionBackendProfile("ferrule", "SYNTHETIC-engine-old", "SYNTHETIC-build-old", "SYNTHETIC-adapter-old");
        var newProfile = new IngestionBackendProfile("ferrule", "SYNTHETIC-engine-new", "SYNTHETIC-build-new", "SYNTHETIC-adapter-new");
        var oldRegistration = Registration(oldProfile, 1024);
        var newRegistration = Registration(newProfile, 2048);
        var current = new List<IngestionBackendRegistration> { newRegistration };
        var retained = new List<IngestionBackendRegistration> { oldRegistration };
        var registry = new IngestionBackendRegistry(current, retained);
        current.Clear();
        retained[0] = Registration(new IngestionBackendProfile("other", "e", "b", "a"));

        Assert.Same(newRegistration, registry.GetCurrent("ferrule"));
        Assert.Same(newRegistration, registry.GetReserved(newProfile));
        Assert.Same(oldRegistration, registry.GetReserved(new IngestionBackendProfile(
            "ferrule", "SYNTHETIC-engine-old", "SYNTHETIC-build-old", "SYNTHETIC-adapter-old")));
        Assert.Equal(1024, registry.GetReserved(oldProfile)!.NativeByteLimit);
        Assert.Equal(2048, registry.GetCurrent("ferrule")!.NativeByteLimit);
        Assert.Null(registry.GetReserved(new IngestionBackendProfile("ferrule", "missing-engine", "missing-build", "missing-adapter")));
        Assert.Null(registry.GetCurrent("other"));
        Assert.Null(new IngestionBackendRegistry([newRegistration]).GetReserved(oldProfile));
        var retainedOnly = new IngestionBackendRegistry([], [oldRegistration]);
        Assert.Null(retainedOnly.GetCurrent("ferrule"));
        Assert.Same(oldRegistration, retainedOnly.GetReserved(oldProfile));
    }

    [Fact]
    public void RegistryIdsAreOrdinalAndReservedLookupUsesEveryProfileFact()
    {
        var lower = Registration(Profile);
        var upper = Registration(new IngestionBackendProfile("Ferrule", Profile.EngineRevision, Profile.BackendBuild, Profile.AdapterBuild));
        var registry = new IngestionBackendRegistry([lower, upper]);

        Assert.Same(lower, registry.GetCurrent("ferrule"));
        Assert.Same(upper, registry.GetCurrent("Ferrule"));
        Assert.Null(registry.GetCurrent("FERRULE"));
        Assert.Null(registry.GetCurrent(" ferrule "));
        Assert.Null(registry.GetCurrent("unregistered"));
        Assert.Same(lower, registry.GetReserved(Profile));
        foreach (var changed in new[]
        {
            new IngestionBackendProfile("FERRULE", Profile.EngineRevision, Profile.BackendBuild, Profile.AdapterBuild),
            new IngestionBackendProfile("ferrule", "other-engine", Profile.BackendBuild, Profile.AdapterBuild),
            new IngestionBackendProfile("ferrule", Profile.EngineRevision, "other-build", Profile.AdapterBuild),
            new IngestionBackendProfile("ferrule", Profile.EngineRevision, Profile.BackendBuild, "other-adapter")
        })
            Assert.Null(registry.GetReserved(changed));
    }

    [Fact]
    public void RegistryRejectsAmbiguousDuplicatesMissingCollectionsAndNullEntries()
    {
        var registration = Registration(Profile);
        var sameIdNewProfile = Registration(new IngestionBackendProfile("ferrule", "other-engine", "other-build", "other-adapter"));
        AssertRejects(() => new IngestionBackendRegistry(null!));
        AssertRejects(() => new IngestionBackendRegistry([null!]));
        AssertRejects(() => new IngestionBackendRegistry([], [null!]));
        AssertRejects(() => new IngestionBackendRegistry([registration, registration]));
        AssertRejects(() => new IngestionBackendRegistry([registration, sameIdNewProfile]));
        AssertRejects(() => new IngestionBackendRegistry([registration], [registration]));
        AssertRejects(() => new IngestionBackendRegistry([], [registration, Registration(Profile)]));
        var registry = new IngestionBackendRegistry([]);
        Assert.Null(registry.GetCurrent("ferrule"));
        Assert.Null(registry.GetReserved(Profile));
        AssertRejects(() => registry.GetCurrent(null!));
        AssertRejects(() => registry.GetCurrent(" "));
        AssertRejects(() => registry.GetCurrent("nul\0backend"));
        AssertRejects(() => registry.GetCurrent(new string((char)0xd800, 1)));
        AssertRejects(() => registry.GetCurrent(new string('é', 65)));
        AssertRejects(() => registry.GetReserved(null!));
    }

    [Fact]
    public void ReceiptProjectionCopiesExactPublicFactsAndDoesNotRetainOrExposePrivateAdmissionData()
    {
        var metadata = SyntheticEligibleMetadata(isValid: true, phase: IngestionPhase.Committed, recovered: true);
        var projection = RevisionIngestionReceipt.FromMetadata(metadata);

        Assert.Equal(metadata.Facts.OperationId, projection.OperationId);
        Assert.Equal(metadata.Facts.Project, projection.Project);
        Assert.Equal(new MappingScope(OrganizationId, ProjectId, MappingId), projection.FutureMapping);
        Assert.Equal(metadata.Facts.FutureRevision, projection.FutureRevision);
        Assert.Equal(metadata.Phase, projection.Phase);
        Assert.Equal(2, projection.ClaimCount);
        Assert.Equal(metadata.Facts.ReservedAtUtc, projection.ReservedAtUtc);
        Assert.Equal(metadata.CompletedAtUtc, projection.CompletedAtUtc);
        Assert.Equal(TimeSpan.Zero, projection.ReservedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, projection.CompletedAtUtc!.Value.Offset);
        Assert.Equal("測定.NATIVE.JSON", projection.OriginalFilename);
        Assert.Equal(17, projection.CommittedSequence);
        Assert.Equal(IngestionAdmissionClassification.EligibleValid, projection.CommittedAdmission);
        Assert.Null(projection.LatestFailure);

        var serialized = JsonSerializer.Serialize(projection);
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(new[]
        {
            "ClaimCount", "CommittedAdmission", "CommittedSequence", "CompletedAtUtc", "FutureMapping", "FutureRevision",
            "LatestFailure", "OperationId", "OriginalFilename", "Phase", "Project", "ReservedAtUtc"
        }, document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        foreach (var secret in new[]
        {
            NativeObjectId.ToString("D"), ActorId.ToString("D"), OtherId.ToString("D"), Digest.Value,
            Profile.EngineRevision, Profile.BackendBuild, Profile.AdapterBuild, "SYNTHETIC-private-diagnostic",
            "SYNTHETIC-private-description", "braid.ingestion-validation.v1"
        })
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, projection.ToString(), StringComparison.Ordinal);
        }
        // Complement the visible serialization boundary: retaining the C1 object
        // privately would keep its native reference and evidence alive as well.
        foreach (var field in typeof(RevisionIngestionReceipt).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            Assert.False(field.GetValue(projection) is IngestionReceipt or ReservedIngestionFacts or PrivateObjectReference or
                PrivateObjectId or IngestionBackendProfile or IngestionClaim or IngestionAttemptReceipt or
                IngestionAttemptCompletion or IngestionEvidenceDocument or IngestionProvenance or IngestionValidationFacts or
                Sha256Digest or PrincipalId or JsonDocument or JsonElement or byte[] or IEnumerable<IngestionAttemptReceipt>);
        AssertRejects(() => RevisionIngestionReceipt.FromMetadata(null!));
    }

    [Fact]
    public void ProjectionUsesTheNewestAttemptAndDoesNotCarryAnEarlierFailureIntoPendingWork()
    {
        var facts = SyntheticFacts();
        var failure = SyntheticAttempt(facts, 1, IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource));
        var pending = SyntheticAttempt(facts, 2, null);
        var metadata = new IngestionReceipt(facts, IngestionPhase.Reserved, 2, 2, null, null, null, [failure, pending]);
        var projected = RevisionIngestionReceipt.FromMetadata(metadata);

        Assert.Null(projected.LatestFailure);
        Assert.Null(projected.CommittedAdmission);
        Assert.Null(projected.CommittedSequence);
        Assert.Null(projected.CompletedAtUtc);
        var result = RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, projected);
        Assert.Null(result.Failure);
        AssertRejects(() => RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.NeedsSource, projected));
        AssertRejects(() => RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.RetryableFailure, projected));

        var originalEligible = SyntheticEligibleMetadata(true, IngestionPhase.Ready);
        var recoveryFailure = SyntheticAttempt(originalEligible.Facts, 2,
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource));
        var readyRecovery = new IngestionReceipt(originalEligible.Facts, IngestionPhase.Ready, 2, 2, 1,
            originalEligible.VerifiedStageAtUtc, 1, [originalEligible.Attempts[0], recoveryFailure]);
        var readyProjection = RevisionIngestionReceipt.FromMetadata(readyRecovery);
        Assert.Equal(IngestionFailureCode.NeedsSource, readyProjection.LatestFailure);
        Assert.Null(readyProjection.CommittedAdmission);
        Assert.Equal(IngestionFailureCode.NeedsSource,
            RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.NeedsSource, readyProjection).Failure);
        AssertRejects(() => RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, readyProjection));
    }

    [Fact]
    public void OperationalFailuresProjectTheirActualBoundedCategoryWithoutInventingValidatorData()
    {
        foreach (var failure in Enum.GetValues<IngestionFailureCode>())
        {
            var observed = failure == IngestionFailureCode.EvidenceLimit
                ? new IngestionProvenance(new RevisionScope(OtherId, ProjectId, MappingId, RevisionId), Digest, Profile, false)
                : null;
            var completion = IngestionAttemptCompletion.FromOperationalFailure(failure, observed);
            var phase = failure == IngestionFailureCode.EvidenceLimit ? IngestionPhase.Rejected : IngestionPhase.Reserved;
            var metadata = SyntheticCompletedMetadata(completion, phase);
            var projected = RevisionIngestionReceipt.FromMetadata(metadata);
            Assert.Equal(failure, projected.LatestFailure);
            Assert.Null(projected.CommittedAdmission);
            Assert.Null(projected.CommittedSequence);
            var outcome = failure switch
            {
                IngestionFailureCode.NeedsSource => RevisionIngestionOutcome.NeedsSource,
                IngestionFailureCode.EvidenceLimit => RevisionIngestionOutcome.Rejected,
                _ => RevisionIngestionOutcome.RetryableFailure
            };
            var result = RevisionIngestionResult.ForReceipt(outcome, projected);
            Assert.Equal(failure, result.Failure);
            Assert.Equal(failure == IngestionFailureCode.EvidenceLimit ? RevisionIngestionRejection.ResourceLimit :
                (RevisionIngestionRejection?)null, result.Rejection);
        }
    }

    [Theory]
    [InlineData(IngestionBackendStatus.BackendUnavailable, IngestionFailureCode.BackendUnavailable)]
    [InlineData(IngestionBackendStatus.BackendFailure, IngestionFailureCode.BackendFailure)]
    [InlineData(IngestionBackendStatus.TimedOut, IngestionFailureCode.TimedOut)]
    [InlineData(IngestionBackendStatus.Canceled, IngestionFailureCode.Canceled)]
    [InlineData(IngestionBackendStatus.IsolationFailure, IngestionFailureCode.IsolationFailure)]
    [InlineData(IngestionBackendStatus.ProtocolFailure, IngestionFailureCode.ProtocolFailure)]
    public void SyntheticRetryableValidationPreservesItsKnownOperationalStatusCategory(
        IngestionBackendStatus status, IngestionFailureCode expected)
    {
        var metadata = SyntheticCompletedMetadata(SyntheticValidation(status, null));
        var projected = RevisionIngestionReceipt.FromMetadata(metadata);
        Assert.Equal(expected, projected.LatestFailure);
        Assert.Equal(expected, RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.RetryableFailure, projected).Failure);
    }

    [Fact]
    public void SyntheticMismatchesAndContradictoryValidationFactsProjectProtocolFailureBeforeRawStatus()
    {
        IngestionAttemptCompletion[] malformed =
        [
            SyntheticValidation(IngestionBackendStatus.BackendUnavailable, null,
                observedScope: new RevisionScope(OtherId, ProjectId, MappingId, RevisionId)),
            SyntheticValidation(IngestionBackendStatus.BackendFailure, null, observedDigest: Sha256Digest.FromBytes("different"u8)),
            SyntheticValidation(IngestionBackendStatus.TimedOut, null,
                observedProfile: new IngestionBackendProfile("ferrule", "different-engine", "different-build", "different-adapter")),
            SyntheticValidation(IngestionBackendStatus.BackendFailure, null,
                diagnostics: "[{\"code\":\"ferrule.digestMismatch\"}]"),
            SyntheticValidation(IngestionBackendStatus.BackendFailure, true),
            SyntheticValidation(IngestionBackendStatus.Succeeded, false),
            SyntheticValidation(IngestionBackendStatus.Succeeded, null),
            SyntheticValidation(IngestionBackendStatus.Succeeded, true, confirmed: false),
            SyntheticValidation(IngestionBackendStatus.InvalidMapping, true),
            SyntheticValidation(IngestionBackendStatus.InvalidMapping, null)
        ];
        foreach (var completion in malformed)
        {
            var projected = RevisionIngestionReceipt.FromMetadata(SyntheticCompletedMetadata(completion));
            Assert.Equal(IngestionFailureCode.ProtocolFailure, projected.LatestFailure);
            var result = RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.RetryableFailure, projected);
            Assert.Equal(IngestionFailureCode.ProtocolFailure, result.Failure);
            Assert.Null(result.Rejection);
        }
    }

    [Fact]
    public void EligibleReusedAndNativeRejectedEvidenceDoNotInventOperationalFailures()
    {
        foreach (var isValid in new[] { true, false })
        {
            Assert.Null(RevisionIngestionReceipt.FromMetadata(SyntheticEligibleMetadata(isValid, IngestionPhase.Ready)).LatestFailure);
            Assert.Null(RevisionIngestionReceipt.FromMetadata(SyntheticEligibleMetadata(isValid, IngestionPhase.Ready, recovered: true)).LatestFailure);
        }
        foreach (var pair in new[]
        {
            (SyntheticValidation(IngestionBackendStatus.InvalidMapping, null,
                diagnostics: "[{\"code\":\"ferrule.nativeDecodeFailed\"}]"), RevisionIngestionRejection.NativeDecode),
            (SyntheticValidation(IngestionBackendStatus.UnsupportedArtifact, null, confirmed: false), RevisionIngestionRejection.UnsupportedArtifact),
            (SyntheticValidation(IngestionBackendStatus.ResourceLimit, null, confirmed: false), RevisionIngestionRejection.ResourceLimit)
        })
        {
            var projected = RevisionIngestionReceipt.FromMetadata(SyntheticCompletedMetadata(pair.Item1, IngestionPhase.Rejected));
            Assert.Null(projected.LatestFailure);
            var rejected = RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.Rejected, projected);
            Assert.Equal(pair.Item2, rejected.Rejection);
            Assert.Null(rejected.Failure);
            Assert.Same(projected, rejected.Receipt);
        }
    }

    [Fact]
    public void ResultFactoriesRequireExactPhaseAdmissionAndLatestFailureCoherence()
    {
        var rows = new[]
        {
            (SyntheticReservedMetadata(), RevisionIngestionOutcome.InProgress),
            (SyntheticEligibleMetadata(true, IngestionPhase.Ready), RevisionIngestionOutcome.InProgress),
            (SyntheticCompletedMetadata(IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource)), RevisionIngestionOutcome.NeedsSource),
            (SyntheticCompletedMetadata(IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.StorageIo)), RevisionIngestionOutcome.RetryableFailure),
            (SyntheticEligibleMetadata(true, IngestionPhase.Committed), RevisionIngestionOutcome.CommittedValid),
            (SyntheticEligibleMetadata(false, IngestionPhase.Committed), RevisionIngestionOutcome.CommittedDecodedInvalid),
            (SyntheticCompletedMetadata(SyntheticValidation(IngestionBackendStatus.UnsupportedArtifact, null), IngestionPhase.Rejected), RevisionIngestionOutcome.Rejected),
            (SyntheticCompletedMetadata(IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit), IngestionPhase.Rejected), RevisionIngestionOutcome.Rejected),
            (SyntheticEligibleMetadata(true, IngestionPhase.HeadConflict), RevisionIngestionOutcome.HeadConflict),
            (SyntheticExhaustedMetadata(), RevisionIngestionOutcome.AttemptExhausted)
        };
        foreach (var (metadata, accepted) in rows)
        {
            var projected = RevisionIngestionReceipt.FromMetadata(metadata);
            var result = RevisionIngestionResult.ForReceipt(accepted, projected);
            Assert.Equal(accepted, result.Outcome);
            Assert.Same(projected, result.Receipt);
            Assert.Equal(projected.LatestFailure, result.Failure);
            foreach (var other in Enum.GetValues<RevisionIngestionOutcome>().Where(outcome => outcome != accepted))
                AssertRejects(() => RevisionIngestionResult.ForReceipt(other, projected));
        }
        AssertRejects(() => RevisionIngestionResult.ForReceipt(RevisionIngestionOutcome.InProgress, null!));
        var nonterminalPolicyFailure = RevisionIngestionReceipt.FromMetadata(SyntheticCompletedMetadata(
            IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.EvidenceLimit)));
        foreach (var outcome in Enum.GetValues<RevisionIngestionOutcome>())
            AssertRejects(() => RevisionIngestionResult.ForReceipt(outcome, nonterminalPolicyFailure));
        foreach (var unknown in new[] { -1, 0, 12, int.MaxValue })
            AssertRejects(() => RevisionIngestionResult.ForReceipt((RevisionIngestionOutcome)unknown,
                RevisionIngestionReceipt.FromMetadata(SyntheticReservedMetadata())));
    }

    [Fact]
    public void PreReservationAndReceiptlessResultsPermitOnlyTheirClosedCategories()
    {
        foreach (var rejection in Enum.GetValues<RevisionIngestionRejection>())
        {
            if (rejection is RevisionIngestionRejection.EmptyNative or RevisionIngestionRejection.NativeLimit or RevisionIngestionRejection.UnregisteredBackend)
            {
                var result = RevisionIngestionResult.RejectedBeforeReservation(rejection);
                Assert.Equal(RevisionIngestionOutcome.Rejected, result.Outcome);
                Assert.Equal(rejection, result.Rejection);
                Assert.Null(result.Receipt);
                Assert.Null(result.Failure);
            }
            else
                AssertRejects(() => RevisionIngestionResult.RejectedBeforeReservation(rejection));
        }
        foreach (var outcome in Enum.GetValues<RevisionIngestionOutcome>())
        {
            if (outcome is RevisionIngestionOutcome.IdempotencyConflict or RevisionIngestionOutcome.ClaimLost or RevisionIngestionOutcome.NotAuthorizedOrNotFound)
            {
                var result = RevisionIngestionResult.WithoutReceipt(outcome);
                Assert.Equal(outcome, result.Outcome);
                Assert.Null(result.Receipt);
                Assert.Null(result.Rejection);
                Assert.Null(result.Failure);
            }
            else
                AssertRejects(() => RevisionIngestionResult.WithoutReceipt(outcome));
        }
        foreach (var unknown in new[] { -1, 0, 7, int.MaxValue })
            AssertRejects(() => RevisionIngestionResult.RejectedBeforeReservation((RevisionIngestionRejection)unknown));
        foreach (var unknown in new[] { -1, 0, 12, int.MaxValue })
            AssertRejects(() => RevisionIngestionResult.WithoutReceipt((RevisionIngestionOutcome)unknown));
    }

    private static IngestionBackendRegistration Registration(IngestionBackendProfile profile, long limit = 4194304) =>
        new(profile, limit, new SyntheticAdmissionBackend());

    private static ReservedIngestionFacts SyntheticFacts()
    {
        var intent = IngestionIntent.ForAppend(Target, "ferrule", "測定.NATIVE.JSON", new AnyHead(), Digest, 21);
        var nativeObject = new PrivateObjectReference(PrivateObjectId.Restore(Scope, NativeObjectId, PrivateObjectClass.NativeArtifact), Digest, 21);
        return new(Project, OperationId, Actor, OtherId, CanonicalFingerprint.Create(Actor, Project, intent), intent,
            Scope, nativeObject, Profile, new IngestionAdmissionPolicy("SYNTHETIC-private-policy", 4194304), Timestamp);
    }

    private static IngestionReceipt SyntheticReservedMetadata() =>
        new(SyntheticFacts(), IngestionPhase.Reserved, 0, 0, null, null, null, []);

    private static IngestionAttemptCompletion SyntheticValidation(IngestionBackendStatus status, bool? isValid,
        string diagnostics = "[]", int total = 0, int emitted = 0, bool confirmed = true,
        RevisionScope? observedScope = null, Sha256Digest? observedDigest = null, IngestionBackendProfile? observedProfile = null)
    {
        var provenance = new IngestionProvenance(observedScope ?? Scope, observedDigest ?? Digest, observedProfile ?? Profile, confirmed);
        var facts = new IngestionValidationFacts(status, provenance, isValid, total, emitted, false);
        using var document = JsonDocument.Parse(diagnostics);
        return IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Create(facts, document.RootElement));
    }

    private static IngestionAttemptReceipt SyntheticAttempt(ReservedIngestionFacts facts, long generation, IngestionAttemptCompletion? completion) =>
        new(generation, Timestamp.AddSeconds(generation * 3), completion is null ? null : Timestamp.AddSeconds(generation * 3 + 1),
            completion, completion?.Classify(facts));

    private static IngestionReceipt SyntheticCompletedMetadata(IngestionAttemptCompletion completion,
        IngestionPhase phase = IngestionPhase.Reserved)
    {
        var facts = SyntheticFacts();
        return new(facts, phase, 1, 1, null, null, null, [SyntheticAttempt(facts, 1, completion)],
            completedAtUtc: phase == IngestionPhase.Rejected ? Timestamp.AddSeconds(5) : null);
    }

    private static IngestionReceipt SyntheticEligibleMetadata(bool isValid, IngestionPhase phase, bool recovered = false)
    {
        var facts = SyntheticFacts();
        var completion = isValid
            ? SyntheticValidation(IngestionBackendStatus.Succeeded, true,
                "[{\"code\":\"SYNTHETIC-private-diagnostic\",\"message\":\"SYNTHETIC-private-description\"}]", 1, 1)
            : SyntheticValidation(IngestionBackendStatus.InvalidMapping, false);
        var first = SyntheticAttempt(facts, 1, completion);
        IngestionAttemptReceipt[] attempts = recovered
            ? [first, SyntheticAttempt(facts, 2, IngestionAttemptCompletion.ReuseEligibleEvidence(1))]
            : [first];
        var committed = phase == IngestionPhase.Committed ? new CommittedRevisionFacts(Scope, 17, facts.NativeObject, Profile,
            IngestionIntent.EntryDocument, IngestionIntent.EmptyDependencyIdentity, Actor, Timestamp.AddSeconds(10), OperationId,
            1, first.Classification!.Value) : null;
        return new(facts, phase, attempts.Length, attempts.Length, attempts.Length, Timestamp.AddSeconds(attempts.Length * 3),
            1, attempts, committed, phase is IngestionPhase.Committed or IngestionPhase.HeadConflict ? Timestamp.AddSeconds(10) : null);
    }

    private static IngestionReceipt SyntheticExhaustedMetadata()
    {
        var facts = SyntheticFacts();
        var completion = IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.BackendFailure);
        var attempts = Enumerable.Range(1, 8).Select(generation => SyntheticAttempt(facts, generation, completion));
        return new(facts, IngestionPhase.AttemptExhausted, 8, 8, null, null, null, attempts, completedAtUtc: Timestamp.AddSeconds(30));
    }

    private static void AssertRejects(Action action) => Assert.ThrowsAny<ArgumentException>(action);

    private sealed class SyntheticAdmissionBackend : IIngestionAdmissionBackend
    {
        public Task<IngestionAttemptCompletion> ValidateAsync(NativeArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This synthetic contract port never invokes a native backend.");
    }

    private sealed class SyntheticPrivateStore : IPrivateObjectStore
    {
        public Task<PrivateObjectPublishResult> PublishCreateOnlyAsync(RevisionScope expectedScope,
            PrivateObjectReference reference, Stream source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This synthetic contract port never writes a private object.");

        public Task<byte[]> ReadVerifiedAsync(RevisionScope expectedScope, PrivateObjectReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This synthetic contract port never reads a private object.");
    }
}
