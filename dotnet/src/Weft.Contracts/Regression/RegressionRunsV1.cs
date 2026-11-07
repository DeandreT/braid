using System.Text.Json.Serialization;
using Weft.Contracts.Catalog;

namespace Weft.Contracts.Regression;

public sealed record RegressionRunPutV1(string CaseId, string Version);
public sealed record RegressionRunReceiptV1(string RunId, string Outcome);
public sealed record RegressionRunSummaryV1(string RunId, string CaseId, string Version, CatalogRevisionScopeV1 Revision,
    string State, int Attempt, string CreatedAtUtc);
public sealed record RegressionRunPageV1(IReadOnlyList<RegressionRunSummaryV1> Runs, string? NextRunId);
public sealed record RegressionDifferenceV1(string? Name, string Kind, int? ExpectedBytes, int? ActualBytes,
    string? ExpectedDigest, string? ActualDigest, int? FirstDifferentByte,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionDocumentLocationV1? Location = null);
public sealed record RegressionDocumentLocationV1(int Interchange, int Segment, string SegmentId, int Element, int Repetition, int Component);
public sealed record RegressionDocumentDiagnosticV1(string? Name, string Side, RegressionDocumentLocationV1 Location);
public sealed record RegressionComparisonV1(string Policy, string Outcome, string NotEvaluatedReason, int ExpectedOutputs,
    int ActualOutputs, int TotalDifferences, bool Truncated, IReadOnlyList<RegressionDifferenceV1> Differences,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegressionDocumentDiagnosticV1? DocumentDiagnostic = null);
public sealed record RegressionRunV1(RegressionRunSummaryV1 Run, string? ExecutionOutcome, string? PreviewStatus,
    RegressionComparisonV1? Comparison, IReadOnlyList<RegressionDocumentV1>? Outputs);
