using System.Text.Json.Serialization;
using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// One persisted binding observation. BindingId, Key, PrincipalId and CreatedAtUtc never change.
/// A disabled binding stays disabled; no identity read supplies organization/project grants.
/// </summary>
public sealed class ExternalIdentityBinding
{
    public ExternalIdentityBinding(Guid bindingId, ExternalIdentityKey key, PrincipalId principalId,
        long version, bool enabled, DateTimeOffset createdAtUtc, DateTimeOffset? disabledAtUtc = null)
    {
        if (bindingId == Guid.Empty)
            throw new ArgumentException("A binding identifier cannot be empty.", nameof(bindingId));
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(principalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (disabledAtUtc is { } disabled)
        {
            RequireUtc(disabled, nameof(disabledAtUtc));
            if (disabled < createdAtUtc)
                throw new ArgumentException("A binding cannot be disabled before creation.", nameof(disabledAtUtc));
        }
        if (enabled != (disabledAtUtc is null) || !enabled && version < 2)
            throw new ArgumentException("Binding activity and version facts are inconsistent.");
        BindingId = bindingId;
        Key = key;
        PrincipalId = principalId;
        Version = version;
        Enabled = enabled;
        CreatedAtUtc = createdAtUtc;
        DisabledAtUtc = disabledAtUtc;
    }

    public Guid BindingId { get; }
    [JsonIgnore]
    public ExternalIdentityKey Key { get; }
    public PrincipalId PrincipalId { get; }
    public long Version { get; }
    public bool Enabled { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? DisabledAtUtc { get; }
    public override string ToString() => "ExternalIdentityBinding";

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A binding instant must use UTC.", parameterName);
    }
}

public enum ExternalIdentityResolutionOutcome
{
    Resolved = 1,
    Unknown = 2,
    Disabled = 3,
    PrincipalInactive = 4
}

/// <summary>
/// A current identity observation only. A later session/request must re-read it;
/// resolution never implies organization admission, project access, creation or payload capability.
/// </summary>
public sealed class ExternalIdentityResolution
{
    private ExternalIdentityResolution(ExternalIdentityResolutionOutcome outcome, ExternalIdentityBinding? binding)
    {
        Outcome = outcome;
        Binding = binding;
    }

    public ExternalIdentityResolutionOutcome Outcome { get; }
    public ExternalIdentityBinding? Binding { get; }
    public override string ToString() => "ExternalIdentityResolution";

    /// <summary>Pure classification of one fresh store observation; never performs or caches a lookup.</summary>
    public static ExternalIdentityResolution FromCurrent(ExternalIdentityBinding? binding, bool principalActive)
    {
        if (binding is null)
            return new(ExternalIdentityResolutionOutcome.Unknown, null);
        if (!binding.Enabled)
            return new(ExternalIdentityResolutionOutcome.Disabled, null);
        return principalActive ? new(ExternalIdentityResolutionOutcome.Resolved, binding)
            : new(ExternalIdentityResolutionOutcome.PrincipalInactive, null);
    }
}

/// <summary>
/// Trusted storage reads only. Each call observes current binding and principal activity together;
/// unknown/disabled/inactive resolve closed. No provisioning, grant, provider validation or positive cache.
/// Storage/integrity failures remain exceptions; cancellation remains cancellation, never Unknown.
/// The implementation must verify an exact returned key or binding ID before creating the result.
/// </summary>
public interface IExternalIdentityReader
{
    Task<ExternalIdentityResolution> ResolveAsync(ExternalIdentityKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requires a nonempty bindingId. Lets later server-held sessions check their immutable internal
    /// binding ID and current version without retaining provider keys or inferring any grants.
    /// </summary>
    Task<ExternalIdentityResolution> ResolveBindingAsync(Guid bindingId,
        CancellationToken cancellationToken = default);
}
