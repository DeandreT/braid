namespace Weft.Contracts.Operations;

public sealed record LocalSandboxTargetV1(string TargetId, string EnvironmentId, string EnvironmentVersion, bool Enabled,
    string? ActiveReleaseId, string Version, string RuntimeImage);
public sealed record LocalSandboxBuildV1(string BuildId, string RevisionId, string EnvironmentId, string EnvironmentVersion, string CreatedAtUtc);
public sealed record LocalSandboxSampleV1(string Kind, string Reference, string Name);
public sealed record LocalSandboxStateV1(string Mode, IReadOnlyList<LocalSandboxTargetV1> Targets, IReadOnlyList<LocalSandboxBuildV1> Builds,
    IReadOnlyList<LocalSandboxSampleV1> Samples);
public sealed record LocalSandboxDeployV1(string BuildId);
public sealed record LocalSandboxDeploymentV1(string DeploymentId, string Outcome, LocalSandboxTargetV1? Target, string? ReleaseId, string? QualificationId);
/// <summary>Content is Base64 exact bytes. History pages omit it; a message read and a send include it.</summary>
public sealed record LocalSandboxDocumentV1(string? Name, string? MediaType, string? Sha256, string? Content);
public sealed record LocalSandboxParameterV1(string Name, string Kind, string Value);
/// <summary>Exactly one source: <c>case</c> with a sample reference, <c>inline</c> with inputs, or <c>replay</c> of an earlier message.</summary>
public sealed record LocalSandboxSendV1(string Source, string? Reference, IReadOnlyList<LocalSandboxDocumentV1>? Inputs,
    IReadOnlyList<LocalSandboxParameterV1>? Parameters, string? ReplayOf);
public sealed record LocalSandboxMessageV1(string MessageId, string TargetId, string SentAtUtc, long DurationMs, string Status, string? Outcome,
    bool Replayed, string? ReleaseId, string? BuildId, string Source, string? ReplayOf, IReadOnlyList<LocalSandboxDocumentV1>? Inputs,
    IReadOnlyList<LocalSandboxParameterV1>? Parameters, IReadOnlyList<LocalSandboxDocumentV1>? Outputs);
public sealed record LocalSandboxMessagePageV1(IReadOnlyList<LocalSandboxMessageV1> Items);
