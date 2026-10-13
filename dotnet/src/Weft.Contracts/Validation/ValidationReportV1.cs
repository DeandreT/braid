using Weft.Contracts.Operations;
using Weft.Contracts.Projections;

namespace Weft.Contracts.Validation;

public sealed record ValidationReportV1(
    OperationStatusV1 Status,
    OperationProvenanceV1? Provenance,
    bool? IsValid,
    int TotalIssueCount,
    int EmittedIssueCount,
    bool Truncated,
    IReadOnlyList<ValidationDiagnosticV1> Diagnostics);

public sealed record ValidationDiagnosticV1(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    bool MessageTruncated,
    NativeLocator? NativeLocator = null,
    NativeDiagnosticOwnerV1? Owner = null,
    string? Location = null,
    bool LocationTruncated = false,
    ProjectionNodeReference? ProjectionLocator = null);
