using Weft.Application;

namespace Weft.MappingBackends.Ferrule;

internal sealed record PreviewHostResponse(
    string ProtocolVersion,
    string? RequestId,
    HostRevision? Revision,
    string? NativeDigest,
    string? DocumentId,
    string EngineRevision,
    string Profile,
    PreviewHostResult? Result,
    PreviewHostFailure? Failure);

internal sealed record PreviewHostResult(ulong RecordsWritten, IReadOnlyList<PreviewHostArtifact> Artifacts);

internal sealed record PreviewHostArtifact(
    string BoundaryId,
    string TargetName,
    bool TargetNameTruncated,
    string MediaType,
    ulong RecordsWritten,
    uint ByteLength,
    string Sha256,
    CsvDocumentDialect? CsvDialect = null);

internal sealed record PreviewHostFailure(string Code, string Message, bool MessageTruncated, string? Subject, bool SubjectTruncated);
