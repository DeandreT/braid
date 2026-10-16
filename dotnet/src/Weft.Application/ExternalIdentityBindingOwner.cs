using Weft.Domain;

namespace Weft.Application;

public enum ExternalIdentityBindingOwnerOutcome
{
    Created = 1,
    Existing = 2,
    Disabled = 3,
    AlreadyDisabled = 4,
    UnknownBinding = 5,
    BindingConflict = 6,
    PrincipalMissing = 7,
    PrincipalInactive = 8,
    OwnerConflict = 9,
    VersionConflict = 10,
    VersionExhausted = 11
}

/// <summary>Only committed binding creation/replay/deactivation carries owner-visible binding facts.</summary>
public sealed class ExternalIdentityBindingOwnerResult
{
    private ExternalIdentityBindingOwnerResult(ExternalIdentityBindingOwnerOutcome outcome,
        ExternalIdentityBinding? binding)
    {
        Outcome = outcome;
        Binding = binding;
    }

    public ExternalIdentityBindingOwnerOutcome Outcome { get; }
    public ExternalIdentityBinding? Binding { get; }
    public override string ToString() => "ExternalIdentityBindingOwnerResult";

    public static ExternalIdentityBindingOwnerResult ForBinding(ExternalIdentityBindingOwnerOutcome outcome,
        ExternalIdentityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var valid = outcome switch
        {
            ExternalIdentityBindingOwnerOutcome.Created => binding.Enabled && binding.Version == 1,
            ExternalIdentityBindingOwnerOutcome.Existing => true,
            ExternalIdentityBindingOwnerOutcome.Disabled or ExternalIdentityBindingOwnerOutcome.AlreadyDisabled => !binding.Enabled,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("Only coherent binding success or replay may carry binding facts.", nameof(outcome));
        return new(outcome, binding);
    }

    public static ExternalIdentityBindingOwnerResult WithoutBinding(ExternalIdentityBindingOwnerOutcome outcome)
    {
        if (outcome is not (ExternalIdentityBindingOwnerOutcome.UnknownBinding or
            ExternalIdentityBindingOwnerOutcome.BindingConflict or ExternalIdentityBindingOwnerOutcome.PrincipalMissing or
            ExternalIdentityBindingOwnerOutcome.PrincipalInactive or ExternalIdentityBindingOwnerOutcome.OwnerConflict or
            ExternalIdentityBindingOwnerOutcome.VersionConflict or ExternalIdentityBindingOwnerOutcome.VersionExhausted))
            throw new ArgumentException("Only a closed refusal may omit binding facts.", nameof(outcome));
        return new(outcome, null);
    }
}

/// <summary>
/// Pure classification of observations already locked by the owner transaction. Decisions do not mutate,
/// commit or authorize a later operation. Persistence must enforce this policy atomically and independently.
/// </summary>
public static class ExternalIdentityBindingPolicy
{
    public static ExternalIdentityBindingOwnerOutcome ClassifyBind(ExternalIdentityKey key,
        PrincipalId intendedPrincipal, ExternalIdentityBinding? existing, bool? principalActive)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(intendedPrincipal);
        RequireKey(key, existing);
        if (existing is not null && existing.PrincipalId != intendedPrincipal)
            return ExternalIdentityBindingOwnerOutcome.BindingConflict;
        if (principalActive is null)
        {
            if (existing is not null)
                throw new InvalidOperationException("The external binding principal observation is inconsistent.");
            return ExternalIdentityBindingOwnerOutcome.PrincipalMissing;
        }
        if (!principalActive.Value)
            return ExternalIdentityBindingOwnerOutcome.PrincipalInactive;
        // Same-owner replay preserves all original facts, including disabled state and version.
        return existing is null ? ExternalIdentityBindingOwnerOutcome.Created : ExternalIdentityBindingOwnerOutcome.Existing;
    }

    public static ExternalIdentityBindingOwnerOutcome ClassifyDisable(ExternalIdentityKey key,
        PrincipalId expectedPrincipal, long expectedVersion, ExternalIdentityBinding? existing)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedPrincipal);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedVersion, 1);
        RequireKey(key, existing);
        if (existing is null)
            return ExternalIdentityBindingOwnerOutcome.UnknownBinding;
        if (existing.PrincipalId != expectedPrincipal)
            return ExternalIdentityBindingOwnerOutcome.OwnerConflict;
        if (existing.Version != expectedVersion)
            return ExternalIdentityBindingOwnerOutcome.VersionConflict;
        if (!existing.Enabled)
            return ExternalIdentityBindingOwnerOutcome.AlreadyDisabled;
        return existing.Version == long.MaxValue ? ExternalIdentityBindingOwnerOutcome.VersionExhausted
            : ExternalIdentityBindingOwnerOutcome.Disabled;
    }

    private static void RequireKey(ExternalIdentityKey key, ExternalIdentityBinding? existing)
    {
        if (existing is not null && existing.Key != key)
            throw new InvalidOperationException("The external binding key observation is inconsistent.");
    }
}

/// <summary>
/// Owner-only operations, never registered in the web application or called by sign-in. The dedicated
/// migration owner and exact current schema contract are required. No actor, role, project, capability,
/// organization preparation, principal creation or reactivation is inferred from a key.
/// SQL/storage failures, cancellation and ambiguous commit remain exceptions, not absent identity.
/// Returned success facts describe an acknowledged commit only. There is no enable/reassign/delete API.
/// </summary>
public interface IExternalIdentityBindingOwner
{
    /// <summary>
    /// Requires an explicitly existing active internal principal. Creates an enabled version-one binding
    /// only for an unbound exact key. Same key/owner returns the entire existing record unchanged, even
    /// when disabled; a different owner conflicts. Replays never create/reactivate any admission or grant.
    /// </summary>
    Task<ExternalIdentityBindingOwnerResult> BindAsync(ExternalIdentityKey key, PrincipalId existingPrincipalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the exact key and compares immutable owner and current version. Unknown/foreign/stale
    /// observations are refused. Current-version disabled is a no-op; stale expected version conflicts.
    /// A successful disable increments exactly once, sets actual UTC disable time, and changes no other
    /// binding fact. Maximum version refuses without overflow. Principal inactivity does not block revocation.
    /// </summary>
    Task<ExternalIdentityBindingOwnerResult> DisableAsync(ExternalIdentityKey key, PrincipalId expectedPrincipalId,
        long expectedVersion, CancellationToken cancellationToken = default);
}
