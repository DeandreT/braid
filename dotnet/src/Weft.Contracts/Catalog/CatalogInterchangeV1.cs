namespace Weft.Contracts.Catalog;

public sealed record CatalogInterchangeCapabilitiesV1(bool Enabled, int MaximumFiles, int MaximumFileBytes,
    int MaximumTotalBytes, int MaximumPathBytes);
public sealed record CatalogInterchangeWarningV1(string Code, string Message);
public sealed record CatalogInterchangeEngineV1(string BackendId, string EngineRevision, string BackendBuild, string AdapterBuild);
public sealed record CatalogInterchangeReviewV1(string ReviewId, string ReviewDigest, string PackageDigest,
    string NativeDigest, CatalogInterchangeEngineV1 Engine, string Summary,
    IReadOnlyList<CatalogInterchangeWarningV1> Warnings, DateTimeOffset ExpiresAt,
    string? TargetMappingId, string? ExpectedHeadRevisionId, string? MappingName);
public sealed record CatalogInterchangeConfirmationV1(string ReviewDigest, bool AcknowledgeWarnings);
