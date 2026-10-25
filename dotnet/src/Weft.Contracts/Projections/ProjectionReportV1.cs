using Weft.Contracts.Operations;

namespace Weft.Contracts.Projections;

public sealed record ProjectionReportV1(
    OperationStatusV1 Status,
    OperationProvenanceV1? Provenance,
    ProjectionDocumentV1? Document,
    IReadOnlyList<OperationDiagnosticV1> Diagnostics);
