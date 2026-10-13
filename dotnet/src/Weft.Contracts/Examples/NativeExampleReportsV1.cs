using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Contracts.Validation;

namespace Weft.Contracts.Examples;

public sealed record NativeExampleDescriptorV1(
    string Id,
    string Title,
    string Source,
    ScopedRevisionId Revision,
    ArtifactDigest Digest,
    int ByteCount);

public sealed record NativeExampleResultV1(
    NativeExampleDescriptorV1 Example,
    ProjectionReportV1 Projection,
    ValidationReportV1 Validation);

public sealed record ProjectionReportV1(
    OperationStatusV1 Status,
    OperationProvenanceV1? Provenance,
    ProjectionDocumentV1? Document,
    IReadOnlyList<OperationDiagnosticV1> Diagnostics);
