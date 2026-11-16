using Braid.Contracts.Projections;

namespace Braid.MappingBackends;

public sealed record MappingProjectionResult(
    MappingOperationStatus Status,
    MappingOperationProvenance Provenance,
    ProjectionDocumentV1? Projection,
    IReadOnlyList<MappingDiagnostic> Diagnostics);
