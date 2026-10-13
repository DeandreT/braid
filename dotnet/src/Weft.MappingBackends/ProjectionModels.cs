using Weft.Contracts.Projections;

namespace Weft.MappingBackends;

public sealed record MappingProjectionResult(
    MappingOperationStatus Status,
    MappingOperationProvenance Provenance,
    ProjectionDocumentV1? Projection,
    IReadOnlyList<MappingDiagnostic> Diagnostics);
