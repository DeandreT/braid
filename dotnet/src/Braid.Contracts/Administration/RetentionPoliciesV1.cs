namespace Braid.Contracts.Administration;

/// <summary>One family's current version; mode notConfigured has no version, duration or effective time.</summary>
public sealed record RetentionFamilyPolicyV1(string Family, string Mode, string? DurationSeconds, string? PolicyVersion,
    bool AppliesToExisting, DateTimeOffset? EffectiveAtUtc);
public sealed record RetentionPoliciesV1(Guid OrganizationId, string Version, IReadOnlyList<RetentionFamilyPolicyV1> Policies);
public sealed record RetentionFamilyPolicyWriteV1(string Family, string Mode, string? DurationSeconds, bool ApplyToExisting);
/// <summary>The complete four-family policy; each family appears exactly once.</summary>
public sealed record RetentionPoliciesWriteV1(IReadOnlyList<RetentionFamilyPolicyWriteV1> Policies);
public sealed record RetentionPoliciesWriteResultV1(string Outcome);
