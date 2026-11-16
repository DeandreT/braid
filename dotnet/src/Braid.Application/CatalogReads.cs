using System.Text;
using Braid.Domain;

namespace Braid.Application;

/// <summary>One fresh organization admission; it grants no access to another operation or project.</summary>
public sealed record CatalogOrganizationAdmission
{
    public CatalogOrganizationAdmission(Guid organizationId, PrincipalId principalId, string name)
    {
        CatalogReadGuard.RequireId(organizationId, nameof(organizationId));
        ArgumentNullException.ThrowIfNull(principalId);
        CatalogReadGuard.RequireName(name, false);
        OrganizationId = organizationId;
        PrincipalId = principalId;
        Name = name;
    }

    public Guid OrganizationId { get; }
    public PrincipalId PrincipalId { get; }
    public string Name { get; }
}

public sealed record CatalogProject
{
    public CatalogProject(ProjectSummary summary, ProjectAccess access)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(access);
        CatalogReadGuard.RequireName(summary.Name, false);
        CatalogReadGuard.RequireDescription(summary.Description);
        if (summary.Scope != access.Scope)
            throw new ArgumentException("Project metadata and observed access must have the same complete scope.");
        Summary = summary;
        Access = access;
    }

    public ProjectSummary Summary { get; }
    public ProjectAccess Access { get; }
}

public sealed record CatalogProjectCursor
{
    public CatalogProjectCursor(Guid organizationId, Guid afterProjectId)
    {
        CatalogReadGuard.RequireId(organizationId, nameof(organizationId));
        CatalogReadGuard.RequireId(afterProjectId, nameof(afterProjectId));
        OrganizationId = organizationId;
        AfterProjectId = afterProjectId;
    }

    public Guid OrganizationId { get; }
    public Guid AfterProjectId { get; }
}

public sealed record CatalogProjectPage
{
    public CatalogProjectPage(CatalogOrganizationAdmission admission, IEnumerable<CatalogProject> projects,
        CatalogProjectCursor? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var snapshot = CatalogReadGuard.Snapshot(projects);
        for (var index = 0; index < snapshot.Length; index++)
        {
            var project = snapshot[index];
            if (project.Summary.Scope.OrganizationId != admission.OrganizationId ||
                project.Access.PrincipalId != admission.PrincipalId ||
                (index > 0 && CatalogReadGuard.CompareIds(snapshot[index - 1].Summary.Scope.ProjectId,
                    project.Summary.Scope.ProjectId) >= 0))
                throw new ArgumentException("A project page requires admitted, unique, scope-matched ascending projects.");
        }
        if (nextCursor is not null && (snapshot.Length == 0 ||
            nextCursor.OrganizationId != admission.OrganizationId ||
            nextCursor.AfterProjectId != snapshot[^1].Summary.Scope.ProjectId))
            throw new ArgumentException("A project continuation must name this page's last emitted project.");
        Admission = admission;
        Projects = Array.AsReadOnly(snapshot);
        NextCursor = nextCursor;
    }

    public CatalogOrganizationAdmission Admission { get; }
    public IReadOnlyList<CatalogProject> Projects { get; }
    public CatalogProjectCursor? NextCursor { get; }
}

public sealed record CatalogRevisionHead
{
    public CatalogRevisionHead(RevisionScope scope, long sequence, IngestionAdmissionClassification admission)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        CatalogReadGuard.RequireAdmission(admission);
        Scope = scope;
        Sequence = sequence;
        Admission = admission;
    }

    public RevisionScope Scope { get; }
    public long Sequence { get; }
    public IngestionAdmissionClassification Admission { get; }
}

public sealed record CatalogMappingSummary
{
    public CatalogMappingSummary(MappingScope scope, string name, string description, string backendId,
        PrincipalId creator, PrincipalId owner, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc,
        long version, CatalogRevisionHead head)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(head);
        CatalogReadGuard.RequireName(name, true);
        CatalogReadGuard.RequireDescription(description);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (updatedAtUtc < createdAtUtc || !CatalogReadGuard.BelongsTo(head.Scope, scope))
            throw new ArgumentException("Mapping timestamps and head must preserve the complete mapping ancestry.");
        Scope = scope;
        Name = name;
        Description = description;
        BackendId = RevisionIngestionGuard.RequireBackendId(backendId);
        Creator = creator;
        Owner = owner;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        Version = version;
        Head = head;
    }

    public MappingScope Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public string BackendId { get; }
    public PrincipalId Creator { get; }
    public PrincipalId Owner { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public long Version { get; }
    public CatalogRevisionHead Head { get; }
}

public sealed record CatalogMapping
{
    public CatalogMapping(CatalogMappingSummary summary, ProjectAccess access)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(access);
        CatalogReadGuard.RequireProject(access, summary.Scope.OrganizationId, summary.Scope.ProjectId);
        Summary = summary;
        Access = access;
    }

    public CatalogMappingSummary Summary { get; }
    public ProjectAccess Access { get; }
}

public sealed record CatalogMappingCursor
{
    public CatalogMappingCursor(ProjectScope project, Guid afterMappingId)
    {
        ArgumentNullException.ThrowIfNull(project);
        CatalogReadGuard.RequireId(afterMappingId, nameof(afterMappingId));
        Project = project;
        AfterMappingId = afterMappingId;
    }

    public ProjectScope Project { get; }
    public Guid AfterMappingId { get; }
}

public sealed record CatalogMappingPage
{
    public CatalogMappingPage(ProjectAccess access, IEnumerable<CatalogMappingSummary> mappings,
        CatalogMappingCursor? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        var snapshot = CatalogReadGuard.Snapshot(mappings);
        for (var index = 0; index < snapshot.Length; index++)
        {
            var mapping = snapshot[index];
            CatalogReadGuard.RequireProject(access, mapping.Scope.OrganizationId, mapping.Scope.ProjectId);
            if (index > 0 && CatalogReadGuard.CompareIds(snapshot[index - 1].Scope.MappingId,
                mapping.Scope.MappingId) >= 0)
                throw new ArgumentException("A mapping page requires unique ascending mapping identities.");
        }
        if (nextCursor is not null && (snapshot.Length == 0 || nextCursor.Project != access.Scope ||
            nextCursor.AfterMappingId != snapshot[^1].Scope.MappingId))
            throw new ArgumentException("A mapping continuation must name this page's last emitted mapping.");
        Access = access;
        Mappings = Array.AsReadOnly(snapshot);
        NextCursor = nextCursor;
    }

    public ProjectAccess Access { get; }
    public IReadOnlyList<CatalogMappingSummary> Mappings { get; }
    public CatalogMappingCursor? NextCursor { get; }
}

/// <summary>Exact metadata representation; head-driven time and observed access are excluded.</summary>
public sealed record CatalogMappingMetadata
{
    public CatalogMappingMetadata(MappingScope scope, string name, string description, long version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CatalogReadGuard.RequireName(name, true);
        CatalogReadGuard.RequireDescription(description);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Scope = scope;
        Name = name;
        Description = description;
        Version = version;
    }

    public MappingScope Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public long Version { get; }
}

public sealed record CatalogRevisionSummary
{
    public CatalogRevisionSummary(RevisionScope scope, long sequence, IngestionAdmissionClassification admission,
        PrincipalId creator, DateTimeOffset committedAtUtc, string originalFilename, string logicalDocument,
        Sha256Digest nativeDigest, long nativeByteCount, string backendId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        ArgumentNullException.ThrowIfNull(originalFilename);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        CatalogReadGuard.RequireAdmission(admission);
        RevisionIngestionGuard.RequireNativeLimit(nativeByteCount);
        if (logicalDocument != IngestionIntent.EntryDocument)
            throw new ArgumentException("A native revision requires its fixed logical entry document.", nameof(logicalDocument));
        Scope = scope;
        Sequence = sequence;
        Admission = admission;
        Creator = creator;
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
        OriginalFilename = RevisionIngestionGuard.RequireFilename(originalFilename);
        LogicalDocument = logicalDocument;
        NativeDigest = nativeDigest;
        NativeByteCount = nativeByteCount;
        BackendId = RevisionIngestionGuard.RequireBackendId(backendId);
    }

    public RevisionScope Scope { get; }
    public long Sequence { get; }
    public IngestionAdmissionClassification Admission { get; }
    public PrincipalId Creator { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public string OriginalFilename { get; }
    public string LogicalDocument { get; }
    public Sha256Digest NativeDigest { get; }
    public long NativeByteCount { get; }
    public string BackendId { get; }
}

public sealed record CatalogRevision
{
    public CatalogRevision(CatalogRevisionSummary summary, ProjectAccess access)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(access);
        CatalogReadGuard.RequireProject(access, summary.Scope.OrganizationId, summary.Scope.ProjectId);
        Summary = summary;
        Access = access;
    }

    public CatalogRevisionSummary Summary { get; }
    public ProjectAccess Access { get; }
}

public sealed record CatalogRevisionCursor
{
    public CatalogRevisionCursor(MappingScope mapping, long beforeSequence)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentOutOfRangeException.ThrowIfLessThan(beforeSequence, 1);
        Mapping = mapping;
        BeforeSequence = beforeSequence;
    }

    public MappingScope Mapping { get; }
    public long BeforeSequence { get; }
}

public sealed record CatalogRevisionPage
{
    public CatalogRevisionPage(MappingScope mapping, ProjectAccess access,
        IEnumerable<CatalogRevisionSummary> revisions, CatalogRevisionCursor? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(access);
        CatalogReadGuard.RequireProject(access, mapping.OrganizationId, mapping.ProjectId);
        var snapshot = CatalogReadGuard.Snapshot(revisions);
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (!CatalogReadGuard.BelongsTo(snapshot[index].Scope, mapping) ||
                (index > 0 && snapshot[index - 1].Sequence <= snapshot[index].Sequence))
                throw new ArgumentException("A revision page requires unique descending sequences in its exact mapping.");
        }
        if (nextCursor is not null && (snapshot.Length == 0 || nextCursor.Mapping != mapping ||
            nextCursor.BeforeSequence != snapshot[^1].Sequence))
            throw new ArgumentException("A revision continuation must name this page's last emitted sequence.");
        Mapping = mapping;
        Access = access;
        Revisions = Array.AsReadOnly(snapshot);
        NextCursor = nextCursor;
    }

    public MappingScope Mapping { get; }
    public ProjectAccess Access { get; }
    public IReadOnlyList<CatalogRevisionSummary> Revisions { get; }
    public CatalogRevisionCursor? NextCursor { get; }
}

/// <summary>Fresh scoped metadata only; reads return owned snapshots and never call storage or native workers.</summary>
public interface ICatalogReader
{
    Task<CatalogOrganizationAdmission?> GetOrganizationAdmissionAsync(ActorContext actor, Guid organizationId,
        CancellationToken cancellationToken = default);
    Task<CatalogProjectPage?> ListProjectsAsync(ActorContext actor, Guid organizationId, int limit,
        CatalogProjectCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<CatalogProject?> GetProjectAsync(ActorContext actor, ProjectScope scope,
        CancellationToken cancellationToken = default);
    Task<CatalogMappingPage?> ListMappingsAsync(ActorContext actor, ProjectScope scope, int limit,
        CatalogMappingCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<CatalogMapping?> GetMappingAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken = default);
    Task<CatalogMappingMetadata?> GetMappingMetadataAsync(ActorContext actor, MappingScope scope,
        CancellationToken cancellationToken = default);
    Task<CatalogRevisionPage?> ListRevisionsAsync(ActorContext actor, MappingScope scope, int limit,
        CatalogRevisionCursor? cursor = null, CancellationToken cancellationToken = default);
    Task<CatalogRevision?> GetRevisionAsync(ActorContext actor, RevisionScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a mapping or revision identity to its complete scope within one organization, under the same fresh
/// active-principal, organization admission and project membership authority as the catalog reads. Absent,
/// foreign and unreadable resources are indistinguishable; the result grants nothing beyond the following read.
/// </summary>
public interface ICatalogResourceLocator
{
    Task<MappingScope?> LocateMappingAsync(ActorContext actor, Guid organizationId, Guid mappingId,
        CancellationToken cancellationToken = default);
    Task<RevisionScope?> LocateRevisionAsync(ActorContext actor, Guid organizationId, Guid revisionId,
        CancellationToken cancellationToken = default);
}

internal static class CatalogReadGuard
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A catalog identity cannot be empty.", name);
    }

    internal static void RequireName(string name, bool trimmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireText(name, 200);
        if (trimmed && name != name.Trim())
            throw new ArgumentException("Persisted mapping names must retain their accepted trimmed spelling.", nameof(name));
    }

    internal static void RequireDescription(string description) => RequireText(description, 4000);

    private static void RequireText(string value, int scalarLimit)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0'))
            throw new ArgumentException("Catalog text cannot contain NUL.", nameof(value));
        try
        {
            if (StrictUtf8.GetByteCount(value) > scalarLimit * 4)
                throw new ArgumentException("Catalog text exceeds its Unicode limit.", nameof(value));
            var count = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                _ = rune;
                if (++count > scalarLimit)
                    throw new ArgumentException("Catalog text exceeds its Unicode limit.", nameof(value));
            }
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Catalog text must contain valid Unicode.", nameof(value));
        }
    }

    internal static void RequireAdmission(IngestionAdmissionClassification admission)
    {
        if (admission is not (IngestionAdmissionClassification.EligibleValid or IngestionAdmissionClassification.EligibleDecodedInvalid))
            throw new ArgumentOutOfRangeException(nameof(admission), "A committed revision requires eligible admission.");
    }

    internal static void RequireProject(ProjectAccess access, Guid organizationId, Guid projectId)
    {
        if (access.Scope.OrganizationId != organizationId || access.Scope.ProjectId != projectId)
            throw new ArgumentException("Catalog metadata and observed access must retain complete project ancestry.");
    }

    internal static bool BelongsTo(RevisionScope revision, MappingScope mapping) =>
        revision.OrganizationId == mapping.OrganizationId && revision.ProjectId == mapping.ProjectId &&
        revision.MappingId == mapping.MappingId;

    internal static T[] Snapshot<T>(IEnumerable<T> values) where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = values.Take(101).ToArray();
        if (snapshot.Length > 100 || snapshot.Any(value => value is null))
            throw new ArgumentException("A catalog page must own at most one hundred nonnull rows.", nameof(values));
        return snapshot;
    }

    internal static int CompareIds(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return ((ReadOnlySpan<byte>)leftBytes).SequenceCompareTo(rightBytes);
    }
}
