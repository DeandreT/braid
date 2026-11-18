namespace Braid.Contracts.Builds;

/// <summary>Canonical package metadata, not a signature or an execution authorization.</summary>
public sealed record MappingPackageManifestV1(string Schema, string Artifact, string Sha256, long ByteCount,
    MappingPackageIntentV1 Intent);

/// <summary>The complete immutable build intent carried by a package manifest.</summary>
public sealed record MappingPackageIntentV1(string Schema, string BuildId, string OrganizationId, string ProjectId,
    string MappingId, string RevisionId, string NativeDigest, string BackendId, string EngineRevision,
    string EngineBuild, string AdapterVersion, string ProfileId, int ProfileVersion, string Runtime,
    string RuntimeVersion, string ArtifactFormat, long MaximumArtifactBytes, string EnvironmentId,
    long EnvironmentVersion, string ApprovalId, string RegressionEvidenceId, IReadOnlyList<BuildBoundaryV1> Boundaries);
