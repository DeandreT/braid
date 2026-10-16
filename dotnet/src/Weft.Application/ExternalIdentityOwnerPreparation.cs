using Weft.Domain;

namespace Weft.Application;

public enum ExternalIdentityPrincipalPreparationOutcome
{
    Created = 1,
    Existing = 2,
    Inactive = 3
}

public enum ExternalIdentityOrganizationAdmissionPreparationOutcome
{
    Created = 1,
    Existing = 2,
    OrganizationMissing = 3,
    PrincipalMissing = 4,
    PrincipalInactive = 5,
    AdmissionInactive = 6
}

/// <summary>
/// Pure classification of current owner-transaction observations only; does not create any row,
/// commit, grant access, or replace the owner's required locked current reads.
/// </summary>
public static class ExternalIdentityOwnerPreparationPolicy
{
    public static ExternalIdentityPrincipalPreparationOutcome ClassifyPrincipal(PrincipalId principalId,
        bool? principalActive)
    {
        ArgumentNullException.ThrowIfNull(principalId);
        return principalActive switch
        {
            null => ExternalIdentityPrincipalPreparationOutcome.Created,
            true => ExternalIdentityPrincipalPreparationOutcome.Existing,
            false => ExternalIdentityPrincipalPreparationOutcome.Inactive
        };
    }

    public static ExternalIdentityOrganizationAdmissionPreparationOutcome ClassifyOrganizationAdmission(
        Guid existingOrganizationId, PrincipalId principalId, bool organizationExists, bool? principalActive,
        bool? membershipActive)
    {
        if (existingOrganizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(existingOrganizationId));
        ArgumentNullException.ThrowIfNull(principalId);
        if (membershipActive is not null && (!organizationExists || principalActive is null))
            throw new InvalidOperationException("The organization admission parent observation is inconsistent.");
        // Organizations have existence, not an active flag, in the existing qualified schema.
        if (!organizationExists)
            return ExternalIdentityOrganizationAdmissionPreparationOutcome.OrganizationMissing;
        if (principalActive is null)
            return ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalMissing;
        if (!principalActive.Value)
            return ExternalIdentityOrganizationAdmissionPreparationOutcome.PrincipalInactive;
        return membershipActive switch
        {
            null => ExternalIdentityOrganizationAdmissionPreparationOutcome.Created,
            true => ExternalIdentityOrganizationAdmissionPreparationOutcome.Existing,
            false => ExternalIdentityOrganizationAdmissionPreparationOutcome.AdmissionInactive
        };
    }
}

/// <summary>
/// Explicit migration-owner preparation only; never registered in the web application or invoked
/// by identity resolution, binding, login or replay of a binding. These separate commands prepare
/// only their named facts. No organization, external binding, project membership, project-creation
/// capability or payload permission is created or inferred by this port.
/// Each operation checks the separate migration-owner/schema contract and current locked facts.
/// Return success only after acknowledged commit. Storage/integrity failure, cancellation and
/// ambiguous commit remain exceptions. Existing Development provisioning stays separate and unchanged.
/// </summary>
public interface IExternalIdentityOwnerPreparation
{
    /// <summary>
    /// Creates only the explicitly selected missing internal principal ID in active state. Existing
    /// active is an unchanged replay; existing inactive is refused, never reactivated. No display name
    /// is accepted because the existing principal schema has none. Creation time is not changed on replay.
    /// </summary>
    Task<ExternalIdentityPrincipalPreparationOutcome> EnsurePrincipalAsync(PrincipalId principalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requires the nonempty existing organization ID and explicitly existing active principal.
    /// Creates only their missing active organization_memberships row. Active membership is unchanged
    /// replay; inactive membership is refused. This never creates the organization or principal,
    /// changes an existing membership, or supplies a project role/capability. An organization has no
    /// active flag in the existing schema, so this operation checks existence only for that parent.
    /// </summary>
    Task<ExternalIdentityOrganizationAdmissionPreparationOutcome> EnsureOrganizationAdmissionAsync(
        Guid existingOrganizationId, PrincipalId principalId, CancellationToken cancellationToken = default);
}
