namespace Braid.Contracts.Projections;

public sealed record ScopedRevisionId(
    string OrganizationId,
    string ProjectId,
    string MappingId,
    string RevisionId);

public sealed record ArtifactDigest(string Algorithm, string Value);

public sealed record BackendIdentity(string Id, string Revision, string BuildId);

public sealed record ProjectionProvenance(
    ScopedRevisionId Revision,
    ArtifactDigest NativeArtifactDigest,
    BackendIdentity Backend,
    string AdapterVersion,
    string ProjectionVersion);
