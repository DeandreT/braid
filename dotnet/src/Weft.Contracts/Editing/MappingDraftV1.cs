using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;

namespace Weft.Contracts.Editing;

public sealed record MappingDraftCreateV1(string BaseRevisionId);
public sealed record MappingDraftVersionV1(string Version);
public sealed record MappingDraftSaveV1(string Version, string? OriginalFilename = null);
public sealed record MappingDraftRebaseV1(string TargetRevisionId);
public sealed record MappingDraftBaseV1(CatalogRevisionScopeV1 Revision, string Sequence, ArtifactDigest NativeDigest, int NativeByteCount);
public sealed record MappingDraftCheckpointV1(string Version, ArtifactDigest NativeDigest, int NativeByteCount, bool IsValid, int TotalIssueCount);
public sealed record MappingDraftV1(string Id, CatalogMappingScopeV1 Mapping, string AuthorPrincipalId, MappingDraftBaseV1 Base,
    string State, string Version, int OperationCount, ArtifactDigest? OperationsDigest, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ChangedAtUtc, DateTimeOffset ExpiresAtUtc, MappingDraftCheckpointV1? Checkpoint, string? SavingOperationId, string? SavedRevisionId,
    string? SavingRequestId = null, string? SavingFilename = null);
public sealed record MappingDraftListV1(IReadOnlyList<MappingDraftV1> Drafts);
public sealed record MappingDraftResultV1(string Outcome, MappingDraftV1? Draft, MappingDraftFailureV1? Failure = null);
public sealed record MappingDraftFailureV1(string Code, string Message, bool MessageTruncated, string? OperationIndex, string Status);
