using Weft.Contracts.Builds;
using Weft.Contracts.Catalog;
using Weft.Contracts.Chat;
using Weft.Contracts.Comparisons;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Regression;
using Weft.Contracts.Validation;

namespace Weft.Tests.Cli;

/// <summary>Valid contract values for scripted stub responses, all within one organization, project and mapping.</summary>
internal sealed class CliStubData(string organization)
{
    public string Organization { get; } = organization;
    public string Project { get; } = Id();
    public string Mapping { get; } = Id();
    public string Revision { get; } = Id();
    public string Actor { get; } = Id();
    public static readonly DateTimeOffset Time = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static string Id() => Guid.NewGuid().ToString("D");

    public CatalogProjectScopeV1 ProjectScope => new(Organization, Project);
    public CatalogMappingScopeV1 MappingScope => new(Organization, Project, Mapping);
    public CatalogRevisionScopeV1 RevisionScope(string? revision = null) => new(Organization, Project, Mapping, revision ?? Revision);
    private static CatalogAccessV1 Access => new(CatalogRoleV1.Administrator, new CatalogActionsV1(true, true, true, true, true, true, true, true, false, true));

    public CatalogProjectV1 ProjectValue(string name, string? project = null) =>
        new(new CatalogProjectScopeV1(Organization, project ?? Project), name, "", Actor, Time, Time, "1", Access);

    public CatalogMappingV1 MappingValue(string name = "Orders") => new(MappingScope, name, "Order mapping", "ferrule", Actor, Actor, Time, Time,
        "3", new CatalogHeadV1(RevisionScope(), "2", CatalogAdmissionV1.Valid), Access);

    public CatalogRevisionV1 RevisionValue(string? revision = null, string sequence = "1") => new(RevisionScope(revision), sequence,
        CatalogAdmissionV1.Valid, Actor, Time, "orders.json", "entry.native.json", new ArtifactDigest("sha256", new string('a', 64)), 12, "ferrule");

    public CatalogRevisionIngestionReceiptV1 Receipt(string operation, CatalogRevisionIngestionPhaseV1 phase,
        CatalogRevisionIngestionFailureV1? failure = null) => new(operation, ProjectScope, MappingScope, RevisionScope(), phase,
        phase == CatalogRevisionIngestionPhaseV1.Reserved && failure is null ? 0 : 1, Time,
        phase is CatalogRevisionIngestionPhaseV1.Committed or CatalogRevisionIngestionPhaseV1.Rejected or
            CatalogRevisionIngestionPhaseV1.HeadConflict ? Time.AddSeconds(5) : null,
        "orders.json", phase == CatalogRevisionIngestionPhaseV1.Committed ? "2" : null,
        phase == CatalogRevisionIngestionPhaseV1.Committed ? CatalogAdmissionV1.Valid : null, failure);

    public CatalogRevisionIngestionResultV1 Committed(string operation) => new(CatalogRevisionIngestionOutcomeV1.CommittedValid,
        Receipt(operation, CatalogRevisionIngestionPhaseV1.Committed),
        committedRevisionUrl: $"/api/v1/organizations/{Organization}/projects/{Project}/mappings/{Mapping}/revisions/{Revision}");

    public CatalogRevisionIngestionResultV1 InProgress(string operation) => new(CatalogRevisionIngestionOutcomeV1.InProgress,
        Receipt(operation, CatalogRevisionIngestionPhaseV1.Staged));

    public CatalogRevisionIngestionResultV1 NeedsSource(string operation) => new(CatalogRevisionIngestionOutcomeV1.NeedsSource,
        Receipt(operation, CatalogRevisionIngestionPhaseV1.Reserved, CatalogRevisionIngestionFailureV1.NeedsSource),
        failure: CatalogRevisionIngestionFailureV1.NeedsSource);

    public CatalogRevisionIngestionResultV1 HeadConflict(string operation) => new(CatalogRevisionIngestionOutcomeV1.HeadConflict,
        Receipt(operation, CatalogRevisionIngestionPhaseV1.HeadConflict));

    public static CatalogRevisionIngestionResultV1 Rejected(CatalogRevisionIngestionRejectionV1 rejection) =>
        new(CatalogRevisionIngestionOutcomeV1.Rejected, rejection: rejection);

    public CatalogMappingMetadataV1 Metadata(string name, string description, string version) => new(MappingScope, name, description, version);

    private ScopedRevisionId Scoped(string? revision = null) => new(Organization, Project, Mapping, revision ?? Revision);
    private static ArtifactDigest Digest => new("sha256", new string('a', 64));
    private static BackendIdentity Backend => new("ferrule", "rev", "build");

    public CatalogProjectionReportV1 ProjectionPending(string derivation) => new("weft.catalog-revision-report.v1", RevisionScope(), "projection",
        CatalogRevisionReportStateV1.Pending, CatalogRevisionReportReadOutcomeV1.Observed, null, null,
        new CatalogRevisionReportAttemptV1(derivation, "1", Time, null, null, null), Time.AddSeconds(30), null, null);

    public CatalogProjectionReportV1 ProjectionAvailable(string derivation, string? revision = null)
    {
        var scoped = Scoped(revision);
        var document = new ProjectionDocumentV1(new ProjectionProvenance(scoped, Digest, Backend, "adapter", "weft.mapping-projection.v1"),
            new ProjectionCoverage(CoverageStatus.Complete, []), [], [], [], [], [], [], [], []);
        return new("weft.catalog-revision-report.v1", RevisionScope(revision), "projection", CatalogRevisionReportStateV1.Available,
            CatalogRevisionReportReadOutcomeV1.Observed, OperationStatusV1.Succeeded, new CatalogRevisionReportResultV1(derivation, "1", Time, Time.AddSeconds(2)),
            null, null, null, new ProjectionReportV1(OperationStatusV1.Succeeded, new OperationProvenanceV1(scoped, Digest, Backend, "adapter", true), document, []));
    }

    public CatalogValidationReportV1 ValidationAvailable(string derivation, bool valid, string? projection = null) =>
        new("weft.catalog-revision-report.v1", RevisionScope(), "validation", CatalogRevisionReportStateV1.Available,
            CatalogRevisionReportReadOutcomeV1.Observed, valid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping,
            new CatalogRevisionReportResultV1(derivation, "1", Time, Time.AddSeconds(2)), null, null, null,
            new ValidationReportV1(valid ? OperationStatusV1.Succeeded : OperationStatusV1.InvalidMapping,
                new OperationProvenanceV1(Scoped(), Digest, Backend, "adapter", true), valid, valid ? 0 : 1, valid ? 0 : 1, false,
                valid ? [] : [new ValidationDiagnosticV1("unknown-field", DiagnosticSeverity.Error, "The field \u001b[31mtotal\u001b[0m is unknown.", false)]),
            projection);

    public CatalogValidationReportV1 ValidationFailed(string derivation) => new("weft.catalog-revision-report.v1", RevisionScope(), "validation",
        CatalogRevisionReportStateV1.Failed, CatalogRevisionReportReadOutcomeV1.Observed, OperationStatusV1.TimedOut, null,
        new CatalogRevisionReportAttemptV1(derivation, "1", Time, Time.AddSeconds(9), OperationStatusV1.TimedOut, CatalogRevisionReportFailureV1.TimedOut),
        Time.AddMinutes(5), CatalogRevisionReportFailureV1.TimedOut, null, null);

    public RevisionComparisonV1 Comparison(string left, string right, string leftProjection, string rightProjection)
    {
        ComparisonSideCitationV1 Side(string revision, string projection) => new(Scoped(revision), "1", Digest, "12",
            new ProjectionProvenance(Scoped(revision), Digest, Backend, "adapter", "weft.mapping-projection.v1"),
            new ComparisonReportCitationV1(projection, "1", new string('b', 64)), null);
        return new RevisionComparisonV1("weft.revision-comparison.v1", "weft.revision-comparison-policy.v1", Side(left, leftProjection),
            Side(right, rightProjection), new ProjectionCoverage(CoverageStatus.Complete, []), new ProjectionCoverage(CoverageStatus.Complete, []),
            false, null, null, true, false, 2, 2, false,
        [
            new ComparisonItemV1(ComparisonEntityV1.Field, ComparisonMatchV1.Matched, 1, 1, false,
                [new ComparisonCandidateV1(new FieldNodeReference("field-total"), null)], [new ComparisonCandidateV1(new FieldNodeReference("field-total"), null)],
                [new ComparisonFacetV1(ComparisonAspectV1.DataType, true, false, [])]),
            new ComparisonItemV1(ComparisonEntityV1.Field, ComparisonMatchV1.Matched, 1, 1, false,
                [new ComparisonCandidateV1(new FieldNodeReference("field-id"), null)], [new ComparisonCandidateV1(new FieldNodeReference("field-id"), null)],
                [new ComparisonFacetV1(ComparisonAspectV1.DataType, false, false, [])])
        ]);
    }

    public CatalogPreviewRunV1 PreviewRun(string run, string state, string? outcome = null, IReadOnlyList<CatalogPreviewOutputV1>? outputs = null) =>
        new(run, RevisionScope(), new string('c', 64), "2026-10-20T12:00:00Z", state, state == "queued" ? 0 : 1, 3, false, outcome,
            outcome is null or "succeeded" ? null : new CatalogPreviewFailureV1("executionFailed", "The mapping failed.", false, null, false),
            null, outputs is null ? null : "2", outputs ?? [], Time, state == "queued" ? null : Time.AddSeconds(1),
            state is "completed" or "canceled" ? Time.AddSeconds(3) : null, Time.AddDays(1));

    public RegressionRuntimeV1 Runtime => new("ferrule", "rev", "build", "adapter", "default");

    public RegressionCasePutV1 CaseDefinition => new("Orders smoke", Revision, Runtime, "2026-10-20T12:00:00Z", new RegressionTargetV1("all", null),
        new RegressionLimitsV1(1024, 1024, 4, 1024), "eyJhIjoxfQ", [], [new RegressionParameterV1("limit", "integer", "5")], "exactBytes",
        [new RegressionDocumentV1(null, "eyJhIjoxfQ")]);

    public RegressionRunV1 RegressionRun(string run, string caseId, string state, string? comparison) => new(
        new RegressionRunSummaryV1(run, caseId, "1", RevisionScope(), state, 1, "2026-10-20T12:00:00.0000000+00:00"),
        state == "completed" ? "succeeded" : null, state == "completed" ? "succeeded" : null,
        comparison is null ? null : new RegressionComparisonV1("exactBytes", comparison, "none", 1, 1, comparison == "passed" ? 0 : 1, false,
            comparison == "passed" ? [] : [new RegressionDifferenceV1(null, "bytesDiffer", 7, 8, new string('d', 64), new string('e', 64), 3)]),
        null);

    public BuildRuntimeV1 BuildProfile => new("dotnet-function", "1", "dotnet", "10.0", "zip", 1 << 20);
    public BuildEnvironmentV1 Environment(string environment, string version) =>
        new(new BuildEnvironmentReferenceV1(Organization, Project, environment, version), BuildProfile, true, Actor, "2026-10-20T12:00:00.0000000+00:00");

    public BuildApprovalV1 Approval(string approval, string build, string environment) => new(approval, build, RevisionScope(), new string('a', 64),
        new BuildBackendV1("ferrule", "rev", "build", "adapter"), BuildProfile, [new BuildBoundaryV1("primary", null, "json")],
        new BuildEnvironmentReferenceV1(Organization, Project, environment, "1"), Id(), false, true, "2026-10-20T12:00:00.0000000+00:00");

    public BuildRunV1 BuildRun(string build, string approval, string state) => new(build, approval, RevisionScope(), state, 1,
        "2026-10-20T12:00:00.0000000+00:00", state == "succeeded" ? "succeeded" : null,
        state == "succeeded" ? new BuildArtifactMetadataV1(new string('f', 64), 2048, "zip") : null);

    public ChatAvailabilityV1 ChatAvailability => new(RevisionScope(), [new ChatProfileV1("local", 2, "Local model", "ollama", "qwen", true)],
        new ChatLimitsV1(4096, 8, 180), true);

    public ChatConversationV1 Conversation(string conversation) => new(conversation, RevisionScope(), Time, Time.AddDays(7), Id(), Id(), null, 0, null);

    public ChatTurnV1 Turn(string conversation, string turn, ChatTurnPhaseV1 phase = ChatTurnPhaseV1.Active) => new(turn, conversation, phase,
        "What does total mean?", [], new ChatTurnProfileV1("local", 2, "ollama", "qwen"), Time, Time.AddMinutes(3),
        phase == ChatTurnPhaseV1.Active ? null : Time.AddSeconds(20), 1);
}
