namespace Weft.Contracts.Specs;

public sealed record SpecCreateV1(Guid CreationKey);
public sealed record SpecUploadV1(Guid SourceId, long SourceVersion, Guid OriginalObjectId, Guid UploadKey,
    Guid ExtractionObjectId, Guid ExtractionKey, long ExtractionExpectedVersion, string Format);
public sealed record SpecTextV1(SpecUploadV1 Upload, string Text);
public sealed record SpecStatusV1(Guid BundleId, long Version, bool Tombstoned);
public sealed record SpecIntakeV1(Guid OriginalObjectId, Guid ExtractionObjectId, long ReservationVersion, string Outcome);
public sealed record SpecObjectV1(Guid ObjectId, Guid SourceId, long SourceVersion, string Kind, string Digest,
    int ByteCount, string Format, string State, string? Outcome);
public sealed record SpecInventoryV1(SpecStatusV1 Bundle, IReadOnlyList<SpecObjectV1> Objects);
public sealed record SpecErrorV1(string Code);
