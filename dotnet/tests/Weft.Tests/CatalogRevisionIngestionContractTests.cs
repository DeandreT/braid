using System.Text.Json;
using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;

namespace Weft.Tests;

/// <summary>Synthetic wire/constructor proofs only; these do not qualify HTTP authority, persistence or native ingestion.</summary>
public sealed class CatalogRevisionIngestionContractTests
{
    private const string Organization = "00112233-4455-6677-8899-aabbccddeeff";
    private const string Project = "10213243-5465-7687-98a9-bacbdcedfe0f";
    private const string Mapping = "20314253-6475-8697-a8b9-cadbecfd0e1f";
    private const string Revision = "30415263-7485-96a7-b8c9-daebfc0d1e2f";
    private const string Operation = "40516273-8495-a6b7-c8d9-eafb0c1d2e3f";
    private const string Other = "50617283-94a5-b6c7-d8e9-fa0b1c2d3e4f";
    private const string Filename = "測定.NATIVE.JSON";
    private const string RevisionUrl = "/api/v1/organizations/00112233-4455-6677-8899-aabbccddeeff/projects/10213243-5465-7687-98a9-bacbdcedfe0f/mappings/20314253-6475-8697-a8b9-cadbecfd0e1f/revisions/30415263-7485-96a7-b8c9-daebfc0d1e2f";
    private static CatalogProjectScopeV1 ProjectScope => new(Organization, Project);
    private static CatalogMappingScopeV1 MappingScope => new(Organization, Project, Mapping);
    private static CatalogRevisionScopeV1 RevisionScope => new(Organization, Project, Mapping, Revision);
    private static DateTimeOffset Reserved => new(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(-7));
    private static DateTimeOffset Completed => Reserved.AddMinutes(1);
    private static JsonSerializerOptions Options => ProjectionJson.CreateOptions();

    [Fact]
    public void CommittedReceiptPreservesFullScopesUtcAndExactBigintWithoutPrivateFacts()
    {
        var receipt = Receipt(CatalogRevisionIngestionPhaseV1.Committed, sequence: "9223372036854775807",
            admission: CatalogAdmissionV1.DecodedInvalid);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(receipt, Options));
        Assert.Equal(new[]
        {
            "claimCount", "committedAdmission", "committedSequence", "completedAtUtc", "futureMapping", "futureRevision",
            "latestFailure", "operationId", "originalFilename", "phase", "project", "reservedAtUtc"
        }, json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(Operation, json.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("committed", json.RootElement.GetProperty("phase").GetString());
        Assert.Equal("decodedInvalid", json.RootElement.GetProperty("committedAdmission").GetString());
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("committedSequence").ValueKind);
        Assert.Equal("9223372036854775807", json.RootElement.GetProperty("committedSequence").GetString());
        Assert.Equal(Revision, json.RootElement.GetProperty("futureRevision").GetProperty("revisionId").GetString());
        Assert.Equal(Organization, json.RootElement.GetProperty("futureRevision").GetProperty("organizationId").GetString());
        Assert.Equal(TimeSpan.Zero, receipt.ReservedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, receipt.CompletedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("reservedAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("completedAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(Reserved.ToUniversalTime(), receipt.ReservedAtUtc);
        Assert.Equal(Completed.ToUniversalTime(), receipt.CompletedAtUtc);
        Assert.Equal(Filename, receipt.OriginalFilename);
        Assert.Equal(receipt, JsonSerializer.Deserialize<CatalogRevisionIngestionReceiptV1>(JsonSerializer.Serialize(receipt, Options), Options));
    }

    [Fact]
    public void FutureScopesRemainPendingFactsAndRejectedGetReceiptDoesNotInventARejectionReason()
    {
        foreach (var phase in new[] { CatalogRevisionIngestionPhaseV1.Reserved, CatalogRevisionIngestionPhaseV1.Staged, CatalogRevisionIngestionPhaseV1.Ready })
        {
            var receipt = Receipt(phase);
            Assert.Null(receipt.CommittedSequence);
            Assert.Null(receipt.CommittedAdmission);
            Assert.Null(receipt.CompletedAtUtc);
            Assert.Equal(RevisionScope, receipt.FutureRevision);
        }
        var rejected = Receipt(CatalogRevisionIngestionPhaseV1.Rejected, latestFailure: CatalogRevisionIngestionFailureV1.EvidenceLimit);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rejected, Options));
        Assert.Equal("rejected", json.RootElement.GetProperty("phase").GetString());
        Assert.Equal("evidenceLimit", json.RootElement.GetProperty("latestFailure").GetString());
        Assert.False(json.RootElement.TryGetProperty("rejection", out _));
        Assert.False(json.RootElement.TryGetProperty("rejectionReason", out _));
        Assert.Null(rejected.CommittedSequence);
        Assert.Null(rejected.CommittedAdmission);
    }

    [Fact]
    public void ReceiptScopesCannotLoseOrMixAnyAncestor()
    {
        foreach (var project in new[] { new CatalogProjectScopeV1(Other, Project), new CatalogProjectScopeV1(Organization, Other) })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, project: project));
        foreach (var mapping in new[]
        {
            new CatalogMappingScopeV1(Other, Project, Mapping), new CatalogMappingScopeV1(Organization, Other, Mapping),
            new CatalogMappingScopeV1(Organization, Project, Other)
        })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, mapping: mapping));
        foreach (var revision in new[]
        {
            new CatalogRevisionScopeV1(Other, Project, Mapping, Revision), new CatalogRevisionScopeV1(Organization, Other, Mapping, Revision),
            new CatalogRevisionScopeV1(Organization, Project, Other, Revision)
        })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, revision: revision));
        Assert.Throws<ArgumentNullException>(() => new CatalogRevisionIngestionReceiptV1(Operation, null!, MappingScope,
            RevisionScope, CatalogRevisionIngestionPhaseV1.Reserved, 0, Reserved, null, Filename, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new CatalogRevisionIngestionReceiptV1(Operation, ProjectScope, null!,
            RevisionScope, CatalogRevisionIngestionPhaseV1.Reserved, 0, Reserved, null, Filename, null, null, null));
        Assert.Throws<ArgumentNullException>(() => new CatalogRevisionIngestionReceiptV1(Operation, ProjectScope, MappingScope,
            null!, CatalogRevisionIngestionPhaseV1.Reserved, 0, Reserved, null, Filename, null, null, null));
    }

    [Fact]
    public void CanonicalOperationIdsAndPositiveDecimalSequencesRejectAmbiguousWireFacts()
    {
        foreach (var id in new[] { "", Guid.Empty.ToString("D"), Operation.ToUpperInvariant(), " " + Operation, "{" + Operation + "}" })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, operationId: id));
        foreach (var sequence in new[] { "", "0", "01", "+1", " 1", "-1", "1.0", "١", "9223372036854775808" })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Committed, 1, Completed, sequence, CatalogAdmissionV1.Valid));
        Assert.Equal("9007199254740993", Receipt(CatalogRevisionIngestionPhaseV1.Committed, sequence: "9007199254740993").CommittedSequence);
        Assert.Equal("1", Receipt(CatalogRevisionIngestionPhaseV1.Committed, sequence: "1").CommittedSequence);
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Committed, 1, Completed, null, CatalogAdmissionV1.Valid));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Committed, 1, Completed, "1", null));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Ready, 1, null, "1", CatalogAdmissionV1.Valid));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Committed, 1, Completed, "1", (CatalogAdmissionV1)0));
    }

    [Fact]
    public void ClaimsTerminalCompletionAndLatestFailureMustAgreeWithTheObservedPhase()
    {
        Assert.Equal(0, Receipt().ClaimCount);
        Assert.Equal(8, Receipt(CatalogRevisionIngestionPhaseV1.Reserved, claimCount: 8).ClaimCount);
        Assert.Equal(8, Receipt(CatalogRevisionIngestionPhaseV1.AttemptExhausted).ClaimCount);
        foreach (var count in new[] { -1, 9, int.MaxValue })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, count, null));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Staged, 0, null));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, latestFailure: CatalogRevisionIngestionFailureV1.NeedsSource));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.AttemptExhausted, 7, Completed));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Rejected, 1, null));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 1, Completed));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Rejected, 1, Reserved.AddTicks(-1)));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.HeadConflict, 1, Completed, latestFailure: CatalogRevisionIngestionFailureV1.BackendFailure));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Rejected, 1, Completed, latestFailure: CatalogRevisionIngestionFailureV1.StorageIo));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Ready, 1, null, latestFailure: CatalogRevisionIngestionFailureV1.EvidenceLimit));
        Rejects(() => RawReceipt((CatalogRevisionIngestionPhaseV1)8, 1, null));
        Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 1, null, latestFailure: (CatalogRevisionIngestionFailureV1)12));
    }

    [Fact]
    public void FilenameIsAnExactBoundedUnicodeBasename()
    {
        var maximum = new string('é', 127) + "a";
        Assert.Equal(maximum, RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, filename: maximum).OriginalFilename);
        foreach (var invalid in new[] { "", ".", "..", "directory/file.json", "directory\\file.json", "file\0", "file\n", maximum + "a", new string((char)0xd800, 1) })
            Rejects(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, filename: invalid));
        Assert.Throws<ArgumentNullException>(() => RawReceipt(CatalogRevisionIngestionPhaseV1.Reserved, 0, null, filename: null!));
    }

    [Fact]
    public void EveryActualOutcomeHasItsOwnCoherentReceiptOrAdmissionShape()
    {
        CatalogRevisionIngestionResultV1[] results =
        [
            new(CatalogRevisionIngestionOutcomeV1.CommittedValid, Receipt(CatalogRevisionIngestionPhaseV1.Committed), committedRevisionUrl: RevisionUrl),
            new(CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid, Receipt(CatalogRevisionIngestionPhaseV1.Committed, admission: CatalogAdmissionV1.DecodedInvalid), committedRevisionUrl: RevisionUrl),
            new(CatalogRevisionIngestionOutcomeV1.InProgress, Receipt(CatalogRevisionIngestionPhaseV1.Ready)),
            new(CatalogRevisionIngestionOutcomeV1.NeedsSource, Receipt(latestFailure: CatalogRevisionIngestionFailureV1.NeedsSource), failure: CatalogRevisionIngestionFailureV1.NeedsSource),
            new(CatalogRevisionIngestionOutcomeV1.RetryableFailure, Receipt(CatalogRevisionIngestionPhaseV1.Staged, latestFailure: CatalogRevisionIngestionFailureV1.ReservedProfileUnavailable), failure: CatalogRevisionIngestionFailureV1.ReservedProfileUnavailable),
            new(CatalogRevisionIngestionOutcomeV1.Rejected, Receipt(CatalogRevisionIngestionPhaseV1.Rejected), CatalogRevisionIngestionRejectionV1.NativeDecode),
            new(CatalogRevisionIngestionOutcomeV1.HeadConflict, Receipt(CatalogRevisionIngestionPhaseV1.HeadConflict)),
            new(CatalogRevisionIngestionOutcomeV1.AttemptExhausted, Receipt(CatalogRevisionIngestionPhaseV1.AttemptExhausted, latestFailure: CatalogRevisionIngestionFailureV1.BackendFailure), failure: CatalogRevisionIngestionFailureV1.BackendFailure),
            new(CatalogRevisionIngestionOutcomeV1.IdempotencyConflict),
            new(CatalogRevisionIngestionOutcomeV1.ClaimLost),
            new(CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound)
        ];
        foreach (var result in results)
        {
            var encoded = JsonSerializer.Serialize(result, Options);
            using var json = JsonDocument.Parse(encoded);
            Assert.Equal(new[] { "committedRevisionUrl", "failure", "outcome", "receipt", "rejection" },
                json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(result, JsonSerializer.Deserialize<CatalogRevisionIngestionResultV1>(encoded, Options));
            if (result.Outcome is not (CatalogRevisionIngestionOutcomeV1.CommittedValid or CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid))
                Assert.Null(result.CommittedRevisionUrl);
        }
    }

    [Fact]
    public void ResultCannotInferRejectionFailureOrCommitFromPartialFacts()
    {
        foreach (var rejection in new[] { CatalogRevisionIngestionRejectionV1.EmptyNative, CatalogRevisionIngestionRejectionV1.NativeLimit, CatalogRevisionIngestionRejectionV1.UnregisteredBackend })
        {
            var result = new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, rejection: rejection);
            Assert.Null(result.Receipt);
            Assert.Null(result.Failure);
            Assert.Null(result.CommittedRevisionUrl);
            Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, Receipt(CatalogRevisionIngestionPhaseV1.Rejected), rejection));
        }
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, rejection: CatalogRevisionIngestionRejectionV1.NativeDecode));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, rejection: (CatalogRevisionIngestionRejectionV1)7));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, Receipt(CatalogRevisionIngestionPhaseV1.Rejected)));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.CommittedValid, Receipt(CatalogRevisionIngestionPhaseV1.Ready), committedRevisionUrl: RevisionUrl));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.CommittedDecodedInvalid, Receipt(CatalogRevisionIngestionPhaseV1.Committed), committedRevisionUrl: RevisionUrl));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.InProgress, Receipt(latestFailure: CatalogRevisionIngestionFailureV1.NeedsSource), failure: CatalogRevisionIngestionFailureV1.NeedsSource));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.RetryableFailure, Receipt(latestFailure: CatalogRevisionIngestionFailureV1.NeedsSource), failure: CatalogRevisionIngestionFailureV1.NeedsSource));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.NeedsSource, Receipt(latestFailure: CatalogRevisionIngestionFailureV1.NeedsSource)));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.IdempotencyConflict, Receipt()));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.ClaimLost, failure: CatalogRevisionIngestionFailureV1.BackendFailure));
        Rejects(() => new CatalogRevisionIngestionResultV1((CatalogRevisionIngestionOutcomeV1)12));
        var policyRejected = Receipt(CatalogRevisionIngestionPhaseV1.Rejected, latestFailure: CatalogRevisionIngestionFailureV1.EvidenceLimit);
        Assert.Equal(CatalogRevisionIngestionRejectionV1.ResourceLimit,
            new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, policyRejected,
                CatalogRevisionIngestionRejectionV1.ResourceLimit, CatalogRevisionIngestionFailureV1.EvidenceLimit).Rejection);
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.Rejected, policyRejected,
            CatalogRevisionIngestionRejectionV1.NativeDecode, CatalogRevisionIngestionFailureV1.EvidenceLimit));
    }

    [Fact]
    public void CommittedLinkMustMatchTheExactFullScopeAndNeverAppearOnPendingOrFailedResults()
    {
        var committed = Receipt(CatalogRevisionIngestionPhaseV1.Committed);
        Assert.Equal(RevisionUrl, new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.CommittedValid,
            committed, committedRevisionUrl: RevisionUrl).CommittedRevisionUrl);
        foreach (var invalid in new[]
        {
            "", RevisionUrl + "/", RevisionUrl + "?version=1", RevisionUrl + "#field", "http://127.0.0.1" + RevisionUrl,
            RevisionUrl.Replace(Organization, Other, StringComparison.Ordinal), RevisionUrl.Replace(Project, Other, StringComparison.Ordinal),
            RevisionUrl.Replace(Mapping, Other, StringComparison.Ordinal), RevisionUrl.Replace(Revision, Other, StringComparison.Ordinal)
        })
            Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.CommittedValid, committed, committedRevisionUrl: invalid));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.CommittedValid, committed));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.InProgress, Receipt(), committedRevisionUrl: RevisionUrl));
        Rejects(() => new CatalogRevisionIngestionResultV1(CatalogRevisionIngestionOutcomeV1.NotAuthorizedOrNotFound, committedRevisionUrl: RevisionUrl));
    }

    [Fact]
    public void DedicatedEnumsHaveIndependentClosedStringVectorsWithoutGlobalJsonConfiguration()
    {
        CheckEnum<CatalogRevisionIngestionOutcomeV1>(
            ["committedValid", "committedDecodedInvalid", "inProgress", "needsSource", "retryableFailure", "rejected", "headConflict", "attemptExhausted", "idempotencyConflict", "claimLost", "notAuthorizedOrNotFound"]);
        CheckEnum<CatalogRevisionIngestionPhaseV1>(["reserved", "staged", "ready", "committed", "rejected", "headConflict", "attemptExhausted"]);
        CheckEnum<CatalogRevisionIngestionRejectionV1>(["emptyNative", "nativeLimit", "unregisteredBackend", "nativeDecode", "unsupportedArtifact", "resourceLimit"]);
        CheckEnum<CatalogRevisionIngestionFailureV1>(["needsSource", "storageIntegrity", "storageIo", "backendUnavailable", "backendFailure", "timedOut", "canceled", "isolationFailure", "protocolFailure", "reservedProfileUnavailable", "evidenceLimit"]);
        Assert.Equal(11, (int)CatalogErrorCodeV1.VersionExhausted);
        Assert.Equal(12, (int)CatalogErrorCodeV1.IngestionAdmissionBusy);
        Assert.Equal("\"ingestionAdmissionBusy\"", JsonSerializer.Serialize(CatalogErrorCodeV1.IngestionAdmissionBusy));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CatalogErrorCodeV1>("12"));
    }

    private static void CheckEnum<TEnum>(string[] expected) where TEnum : struct, Enum
    {
        Assert.Equal(expected.Length, Enum.GetValues<TEnum>().Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var value = (TEnum)Enum.ToObject(typeof(TEnum), index + 1);
            Assert.Equal("\"" + expected[index] + "\"", JsonSerializer.Serialize(value));
            Assert.Equal(value, JsonSerializer.Deserialize<TEnum>("\"" + expected[index] + "\""));
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TEnum>("1"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TEnum>("\"unqualifiedFutureValue\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((TEnum)Enum.ToObject(typeof(TEnum), 999)));
    }

    private static CatalogRevisionIngestionReceiptV1 Receipt(CatalogRevisionIngestionPhaseV1 phase = CatalogRevisionIngestionPhaseV1.Reserved,
        CatalogRevisionIngestionFailureV1? latestFailure = null, int? claimCount = null, string? sequence = "17",
        CatalogAdmissionV1? admission = CatalogAdmissionV1.Valid) =>
        RawReceipt(phase, claimCount ?? (phase == CatalogRevisionIngestionPhaseV1.AttemptExhausted ? 8 :
            phase == CatalogRevisionIngestionPhaseV1.Reserved && latestFailure is null ? 0 : 1),
            phase is CatalogRevisionIngestionPhaseV1.Committed or CatalogRevisionIngestionPhaseV1.Rejected or
                CatalogRevisionIngestionPhaseV1.HeadConflict or CatalogRevisionIngestionPhaseV1.AttemptExhausted ? Completed : null,
            phase == CatalogRevisionIngestionPhaseV1.Committed ? sequence : null,
            phase == CatalogRevisionIngestionPhaseV1.Committed ? admission : null, latestFailure);

    private static CatalogRevisionIngestionReceiptV1 RawReceipt(CatalogRevisionIngestionPhaseV1 phase, int claimCount,
        DateTimeOffset? completedAtUtc, string? sequence = null, CatalogAdmissionV1? admission = null,
        CatalogRevisionIngestionFailureV1? latestFailure = null, CatalogProjectScopeV1? project = null,
        CatalogMappingScopeV1? mapping = null, CatalogRevisionScopeV1? revision = null, string filename = Filename,
        string operationId = Operation) =>
        new(operationId, project ?? ProjectScope, mapping ?? MappingScope, revision ?? RevisionScope, phase,
            claimCount, Reserved, completedAtUtc, filename, sequence, admission, latestFailure);

    private static void Rejects(Action action) => Assert.ThrowsAny<ArgumentException>(action);
}
