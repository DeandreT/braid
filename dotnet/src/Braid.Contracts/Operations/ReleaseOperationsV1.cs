using System.Text.Json;
using System.Text.Json.Serialization;

namespace Braid.Contracts.Operations;

public sealed record ReleaseOperationsPageV1(IReadOnlyList<ReleaseOperationsItemV1> Items, int? NextOffset);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ReleaseTargetMetadataV1), "target")]
[JsonDerivedType(typeof(ReleaseMetadataV1), "release")]
[JsonDerivedType(typeof(ReleaseTargetEventV1), "history")]
[JsonDerivedType(typeof(ReleaseActionAuditV1), "action")]
[JsonDerivedType(typeof(ReleaseCallerGrantV1), "caller")]
[JsonDerivedType(typeof(ReleaseInvocationMetadataV1), "invocation")]
public abstract record ReleaseOperationsItemV1;
public sealed record ReleaseRuntimeProfileV1(string Id, long Version, string Runtime, string RuntimeVersion, string ArtifactFormat, long MaximumArtifactBytes);
public sealed record ReleaseApprovalMetadataV1(Guid ApprovalId, bool Revoked, DateTimeOffset CreatedAt);
public sealed record ReleaseActiveMetadataV1(Guid ReleaseId, Guid ActorId, Guid QualificationId, Guid BuildId, string ArtifactDigest, Guid ApprovalId, ReleaseApprovalMetadataV1 Approval);
public sealed record ReleaseTargetMetadataV1(
    [property: JsonPropertyName("target_id")] Guid TargetId,
    [property: JsonPropertyName("environment_id")] Guid EnvironmentId,
    [property: JsonPropertyName("environment_version")] string EnvironmentVersion,
    bool Enabled,
    [property: JsonPropertyName("active_release_id")] Guid? ActiveReleaseId,
    [property: JsonPropertyName("runtime_image")] string RuntimeImage,
    ReleaseRuntimeProfileV1 Profile, string Version, ReleaseActiveMetadataV1? Active) : ReleaseOperationsItemV1;
public sealed record ReleaseMetadataV1(
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("actor_id")] Guid ActorId,
    [property: JsonPropertyName("qualification_id")] Guid QualificationId,
    [property: JsonPropertyName("rollback_of_id")] Guid? RollbackOfId,
    string Phase, string Rejection,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("build_id")] Guid BuildId,
    [property: JsonPropertyName("artifact_digest")] string ArtifactDigest,
    [property: JsonPropertyName("runtime_image")] string RuntimeImage,
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("approval_revoked")] bool ApprovalRevoked,
    [property: JsonPropertyName("approval_created_at")] DateTimeOffset ApprovalCreatedAt) : ReleaseOperationsItemV1;
public sealed record ReleaseTargetEventV1(string Version,
    [property: JsonPropertyName("actor_id")] Guid? ActorId, bool Enabled,
    [property: JsonPropertyName("active_release_id")] Guid? ActiveReleaseId, string Origin, string Action,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt) : ReleaseOperationsItemV1;
public sealed record ReleaseActionAuditV1(
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("actor_id")] Guid ActorId, string Action,
    [property: JsonPropertyName("expected_version")] string ExpectedVersion,
    [property: JsonPropertyName("qualification_id")] Guid? QualificationId,
    [property: JsonPropertyName("source_release_id")] Guid? SourceReleaseId, int Outcome,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt) : ReleaseOperationsItemV1;
public sealed record ReleaseCallerGrantV1(
    [property: JsonPropertyName("grant_id")] Guid GrantId, string Provider,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("object_id")] Guid ObjectId,
    [property: JsonPropertyName("granted_by")] Guid GrantedBy,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt,
    [property: JsonPropertyName("revoked_by")] Guid? RevokedBy,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt) : ReleaseOperationsItemV1;
public sealed record ReleaseInvocationMetadataV1(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("caller_client_id")] Guid CallerClientId, string Provider,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("object_id")] Guid ObjectId,
    [property: JsonPropertyName("release_id")] Guid? ReleaseId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    string State, string? Outcome, int Attempt,
    [property: JsonPropertyName("output_bytes")] int? OutputBytes,
    [property: JsonPropertyName("input_bytes")] int? InputBytes,
    [property: JsonPropertyName("replay_count")] long? ReplayCount, bool? Replayed,
    [property: JsonPropertyName("latency_ms")] double? LatencyMs) : ReleaseOperationsItemV1;
public sealed record ReleaseOperationsSummaryV1(long Count, long Failed, long Running, long Retryable,
    IReadOnlyDictionary<string, long> FailuresByCategory, double? MeanLatencyMs, double? P95LatencyMs);
public sealed record ReleaseOperationV1(string Action, string EnvironmentId, string EnvironmentVersion,
    string? QualificationId, string? SourceReleaseId);
public sealed record ReleaseOperationReceiptV1(string OperationId, string Outcome);
