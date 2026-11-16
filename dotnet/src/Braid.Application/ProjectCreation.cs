using Braid.Domain;

namespace Braid.Application;

/// <summary>
/// One fresh observation of active organization admission and its explicit
/// creation capability. A false capability remains an admitted observation;
/// neither value grants a later operation or access to any project.
/// </summary>
public sealed record OrganizationProjectCreationAdmission
{
    public OrganizationProjectCreationAdmission(Guid organizationId, PrincipalId principalId, bool canCreateProjects)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("An organization identifier cannot be empty.", nameof(organizationId));
        ArgumentNullException.ThrowIfNull(principalId);
        OrganizationId = organizationId;
        PrincipalId = principalId;
        CanCreateProjects = canCreateProjects;
    }

    public Guid OrganizationId { get; }
    public PrincipalId PrincipalId { get; }
    public bool CanCreateProjects { get; }
}

/// <summary>
/// Immutable original creation facts. Initial membership fields describe the
/// atomic creation only; current project membership may have changed or been
/// deleted, and every later operation must check current authority separately.
/// </summary>
public sealed record ProjectCreationReceipt
{
    public ProjectCreationReceipt(ProjectScope project, Guid idempotencyKey, PrincipalId originalActor,
        ProjectCreationMetadata metadata, ProjectCreationFingerprint fingerprint, DateTimeOffset createdAtUtc,
        long initialProjectVersion = 1, ProjectRole initialMembershipRole = ProjectRole.Administrator,
        bool initialMembershipActive = true, long initialMembershipVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (idempotencyKey == Guid.Empty)
            throw new ArgumentException("A project creation key cannot be empty.", nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(originalActor);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (initialProjectVersion != 1)
            throw new ArgumentOutOfRangeException(nameof(initialProjectVersion), "The initial project version must be one.");
        if (initialMembershipRole != ProjectRole.Administrator)
            throw new ArgumentOutOfRangeException(nameof(initialMembershipRole), "The initial project role must be Administrator.");
        if (!initialMembershipActive)
            throw new ArgumentException("The initial project membership must be active.", nameof(initialMembershipActive));
        if (initialMembershipVersion != 1)
            throw new ArgumentOutOfRangeException(nameof(initialMembershipVersion), "The initial membership version must be one.");
        if (fingerprint != ProjectCreationFingerprint.Create(project.OrganizationId, originalActor, idempotencyKey, metadata))
            throw new ArgumentException("The project creation fingerprint must match its original facts.", nameof(fingerprint));

        Project = project;
        IdempotencyKey = idempotencyKey;
        OriginalActor = originalActor;
        Metadata = metadata;
        Fingerprint = fingerprint;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        InitialProjectVersion = initialProjectVersion;
        InitialMembershipRole = initialMembershipRole;
        InitialMembershipActive = initialMembershipActive;
        InitialMembershipVersion = initialMembershipVersion;
    }

    public ProjectScope Project { get; }
    public Guid IdempotencyKey { get; }
    public PrincipalId OriginalActor { get; }
    public ProjectCreationMetadata Metadata { get; }
    public ProjectCreationFingerprint Fingerprint { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public long InitialProjectVersion { get; }
    public ProjectRole InitialMembershipRole { get; }
    public bool InitialMembershipActive { get; }
    public long InitialMembershipVersion { get; }
}

public enum ProjectCreationOutcome
{
    Created = 1,
    Replayed = 2,
    NotAdmitted = 3,
    CapabilityDenied = 4,
    IdempotencyConflict = 5
}

/// <summary>Only a successfully committed creation or replay exposes original facts.</summary>
public sealed record ProjectCreationResult
{
    private ProjectCreationResult(ProjectCreationOutcome outcome, ProjectCreationReceipt? receipt)
    {
        Outcome = outcome;
        Receipt = receipt;
    }

    public ProjectCreationOutcome Outcome { get; }
    public ProjectCreationReceipt? Receipt { get; }

    public static ProjectCreationResult ForReceipt(ProjectCreationOutcome outcome, ProjectCreationReceipt receipt)
    {
        if (outcome is not (ProjectCreationOutcome.Created or ProjectCreationOutcome.Replayed))
            throw new ArgumentOutOfRangeException(nameof(outcome), "Only creation or replay carries a receipt.");
        ArgumentNullException.ThrowIfNull(receipt);
        return new(outcome, receipt);
    }

    public static ProjectCreationResult WithoutReceipt(ProjectCreationOutcome outcome)
    {
        if (outcome is not (ProjectCreationOutcome.NotAdmitted or ProjectCreationOutcome.CapabilityDenied or
            ProjectCreationOutcome.IdempotencyConflict))
            throw new ArgumentOutOfRangeException(nameof(outcome), "A denial or conflict cannot carry a receipt.");
        return new(outcome, null);
    }
}

/// <summary>Reads current active identity/admission and explicit organization creation capability.</summary>
public interface IOrganizationProjectCreationReader
{
    /// <summary>
    /// Null means absent or inactive identity/organization admission. A non-null
    /// false capability is an admitted denial. This is one observation, not a
    /// cached permission; persistence failures and cancellation remain errors.
    /// </summary>
    Task<OrganizationProjectCreationAdmission?> GetAdmissionAsync(ActorContext actor, Guid organizationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Atomically creates a project, first Administrator membership and immutable creation facts.</summary>
public interface IProjectCreator
{
    /// <summary>
    /// Rechecks active identity, organization admission and explicit capability
    /// before the scoped key lookup, protecting those authority rows through the
    /// caller-owned commit. A successful result is returned only after commit.
    /// Replay never recreates, reactivates or promotes current membership. SQL
    /// failure, cancellation and ambiguous commit remain errors rather than
    /// denial or a fabricated receipt; a caller may reconcile the same key later.
    /// The actor comes from a trusted identity boundary, not caller claims.
    /// </summary>
    Task<ProjectCreationResult> CreateAsync(ActorContext actor, Guid organizationId, Guid idempotencyKey,
        ProjectCreationMetadata metadata, CancellationToken cancellationToken = default);
}
