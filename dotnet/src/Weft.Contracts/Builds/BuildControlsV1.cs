using Weft.Contracts.Catalog;

namespace Weft.Contracts.Builds;

public sealed record BuildBackendV1(string BackendId, string EngineRevision, string EngineBuild, string AdapterVersion);
public sealed record BuildRuntimeV1(string Id, string Version, string Runtime, string RuntimeVersion, string ArtifactFormat, long MaximumArtifactBytes);
public sealed record BuildBoundaryV1(string Kind, string? Name, string Format);
public sealed record BuildEnvironmentReferenceV1(string OrganizationId, string ProjectId, string EnvironmentId, string Version);
public sealed record BuildEnvironmentV1(BuildEnvironmentReferenceV1 Reference, BuildRuntimeV1 Profile, bool Active,
    string CreatorPrincipalId, string CreatedAtUtc);
public sealed record BuildEnvironmentPageV1(IReadOnlyList<BuildEnvironmentV1> Environments, string? NextEnvironmentId, string? NextVersion);
public sealed record BuildApprovalPutV1(string BuildId, string RevisionId, string EnvironmentId, string EnvironmentVersion, string RegressionEvidenceId);
public sealed record BuildApprovalV1(string ApprovalId, string BuildId, CatalogRevisionScopeV1 Revision, string NativeDigest,
    BuildBackendV1 Backend, BuildRuntimeV1 Profile, IReadOnlyList<BuildBoundaryV1> Boundaries, BuildEnvironmentReferenceV1 Environment,
    string RegressionEvidenceId, bool Revoked, bool EnvironmentActive, string CreatedAtUtc);
public sealed record BuildApprovalReceiptV1(string Outcome, BuildApprovalV1 Approval);
public sealed record BuildApprovalPageV1(IReadOnlyList<BuildApprovalV1> Approvals, string? NextApprovalId);
public sealed record BuildRunPutV1(string ApprovalId);
public sealed record BuildArtifactMetadataV1(string Sha256, long ByteCount, string Format);
public sealed record BuildRunV1(string BuildId, string ApprovalId, CatalogRevisionScopeV1 Revision, string State, int Attempt,
    string CreatedAtUtc, string? Outcome, BuildArtifactMetadataV1? Artifact);
public sealed record BuildRunReceiptV1(string Outcome, BuildRunV1 Run);
public sealed record BuildRunPageV1(IReadOnlyList<BuildRunV1> Runs, string? NextBuildId);
public sealed record BuildCapabilitiesV1(BuildBackendV1 Backend, BuildRuntimeV1 Profile, bool ReadOwnMetadata, bool ApproveBuilds, bool SubmitBuilds);
