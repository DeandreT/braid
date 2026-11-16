using Braid.Contracts.Projections;

namespace Braid.Contracts.Comparisons;

public enum ComparisonEntityV1 { Boundary, Field, Parameter, Rule, Control, Relationship, Validation }
public enum ComparisonMatchV1 { Matched, OnlyLeft, OnlyRight, Ambiguous }
public enum ComparisonAspectV1
{
    Shape, DataType, Cardinality, Requirement, Nullability, Value, Condition, Default,
    Details, Control, Relationship, Presentation, ValidationValidity, ValidationCounts
}
public enum ComparisonReasonV1
{
    IncompleteCoverage, UnknownFact, UnsupportedDetail, IncompatibleProfile,
    AmbiguousMatch, UnresolvedReference, MissingValidation, TruncatedValidation,
    CandidatesOmitted, ItemsOmitted
}

public sealed record ComparisonReportCitationV1(string DerivationId, string Generation, string KeyFingerprint)
{
    public override string ToString() => "Revision comparison report citation.";
}
public sealed record ComparisonDraftCitationV1(string OrganizationId, string ProjectId, string MappingId, string DraftId, string Version);
public sealed record ComparisonEditOperationV1(string Index, string Description, bool DescriptionTruncated);
public sealed record ComparisonSideCitationV1(ScopedRevisionId? Revision, string? Sequence,
    ArtifactDigest NativeDigest, string NativeByteCount, ProjectionProvenance ProjectionProvenance,
    ComparisonReportCitationV1? Projection, ComparisonReportCitationV1? Validation, ComparisonDraftCitationV1? Draft = null)
{
    public string Kind => Draft is null ? "revision" : "draft";
    public override string ToString() => "Revision comparison side citation.";
}
public sealed record ComparisonValidationSummaryV1(bool IsValid, int TotalIssueCount,
    int EmittedIssueCount, bool Truncated, bool HasTruncatedDiagnosticText)
{
    public override string ToString() => "Revision comparison validation summary.";
}
/// <summary>Exactly one target is populated; it is local to the cited side's report.</summary>
public sealed record ComparisonCandidateV1(ProjectionNodeReference? Node, string? RelationshipId)
{
    public override string ToString() => "Revision comparison candidate.";
}
public sealed record ComparisonFacetV1(ComparisonAspectV1 Aspect, bool HasObservedDifference,
    bool HasUncertainty, IReadOnlyList<ComparisonReasonV1> Reasons)
{
    public override string ToString() => "Revision comparison facet.";
}
public sealed record ComparisonItemV1(ComparisonEntityV1 Entity, ComparisonMatchV1 Match,
    int LeftCandidateCount, int RightCandidateCount, bool CandidatesTruncated,
    IReadOnlyList<ComparisonCandidateV1> Left, IReadOnlyList<ComparisonCandidateV1> Right,
    IReadOnlyList<ComparisonFacetV1> Facets)
{
    public override string ToString() => "Revision comparison item.";
}
/// <summary>Observed projection facts under the stated policy, never native execution equivalence.</summary>
public sealed record RevisionComparisonV1(string SchemaVersion, string PolicyVersion,
    ComparisonSideCitationV1 Left, ComparisonSideCitationV1 Right,
    ProjectionCoverage LeftCoverage, ProjectionCoverage RightCoverage, bool ValidationIncluded,
    ComparisonValidationSummaryV1? LeftValidation, ComparisonValidationSummaryV1? RightValidation,
    bool HasObservedDifferences, bool HasUncertainty, int TotalItemCount, int EmittedItemCount,
    bool Truncated, IReadOnlyList<ComparisonItemV1> Items, IReadOnlyList<ComparisonEditOperationV1>? Operations = null)
{
    public override string ToString() => "Revision comparison result.";
}
