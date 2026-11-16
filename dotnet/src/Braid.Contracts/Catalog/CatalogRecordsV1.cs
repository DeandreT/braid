using Braid.Contracts.Projections;

namespace Braid.Contracts.Catalog;

public sealed record CatalogProjectV1
{
    public CatalogProjectV1(CatalogProjectScopeV1 scope, string name, string description, string creatorPrincipalId,
        DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, string version, CatalogAccessV1 access)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(access);
        CatalogValueV1.RequireHistory(createdAtUtc, updatedAtUtc);
        Scope = scope;
        Name = CatalogValueV1.RequireName(name, nameof(name));
        Description = CatalogValueV1.RequireDescription(description);
        CreatorPrincipalId = CatalogValueV1.RequireId(creatorPrincipalId, nameof(creatorPrincipalId));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        Version = CatalogValueV1.RequirePositiveDecimal(version, nameof(version));
        Access = access;
    }

    public CatalogProjectScopeV1 Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public string CreatorPrincipalId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string Version { get; }
    public CatalogAccessV1 Access { get; }
}

public sealed record CatalogHeadV1
{
    public CatalogHeadV1(CatalogRevisionScopeV1 scope, string sequence, CatalogAdmissionV1 admission)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CatalogValueV1.RequireAdmission(admission);
        Scope = scope;
        Sequence = CatalogValueV1.RequirePositiveDecimal(sequence, nameof(sequence));
        Admission = admission;
    }

    public CatalogRevisionScopeV1 Scope { get; }
    public string Sequence { get; }
    public CatalogAdmissionV1 Admission { get; }
}

public sealed record CatalogMappingV1
{
    public CatalogMappingV1(CatalogMappingScopeV1 scope, string name, string description, string backendId,
        string creatorPrincipalId, string ownerPrincipalId, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc,
        string version, CatalogHeadV1 head, CatalogAccessV1 access)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(access);
        CatalogValueV1.RequireHistory(createdAtUtc, updatedAtUtc);
        if (head.Scope.OrganizationId != scope.OrganizationId || head.Scope.ProjectId != scope.ProjectId ||
            head.Scope.MappingId != scope.MappingId)
            throw new ArgumentException("The mapping head must preserve full mapping ancestry.", nameof(head));
        Scope = scope;
        Name = CatalogValueV1.RequireName(name, nameof(name));
        Description = CatalogValueV1.RequireDescription(description);
        BackendId = CatalogValueV1.RequireText(backendId, 128, nameof(backendId));
        CreatorPrincipalId = CatalogValueV1.RequireId(creatorPrincipalId, nameof(creatorPrincipalId));
        OwnerPrincipalId = CatalogValueV1.RequireId(ownerPrincipalId, nameof(ownerPrincipalId));
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        Version = CatalogValueV1.RequirePositiveDecimal(version, nameof(version));
        Head = head;
        Access = access;
    }

    public CatalogMappingScopeV1 Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public string BackendId { get; }
    public string CreatorPrincipalId { get; }
    public string OwnerPrincipalId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string Version { get; }
    public CatalogHeadV1 Head { get; }
    public CatalogAccessV1 Access { get; }
}

public sealed record CatalogRevisionV1
{
    public CatalogRevisionV1(CatalogRevisionScopeV1 scope, string sequence, CatalogAdmissionV1 admission,
        string creatorPrincipalId, DateTimeOffset committedAtUtc, string originalFilename, string logicalDocument,
        ArtifactDigest nativeDigest, int nativeByteCount, string backendId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeDigest);
        CatalogValueV1.RequireAdmission(admission);
        if (nativeByteCount is < 1 or > 4194304)
            throw new ArgumentOutOfRangeException(nameof(nativeByteCount), "Native byte counts must fit the admitted catalog bound.");
        if (logicalDocument != "entry.native.json")
            throw new ArgumentException("The logical document must match the admitted catalog identity.", nameof(logicalDocument));
        if (nativeDigest.Algorithm != "sha256" || nativeDigest.Value is not { Length: 64 } digest ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Catalog native digests must contain canonical SHA256 facts.", nameof(nativeDigest));

        Scope = scope;
        Sequence = CatalogValueV1.RequirePositiveDecimal(sequence, nameof(sequence));
        Admission = admission;
        CreatorPrincipalId = CatalogValueV1.RequireId(creatorPrincipalId, nameof(creatorPrincipalId));
        CommittedAtUtc = committedAtUtc.ToUniversalTime();
        OriginalFilename = CatalogValueV1.RequireFilename(originalFilename);
        LogicalDocument = logicalDocument;
        NativeDigest = new ArtifactDigest("sha256", digest);
        NativeByteCount = nativeByteCount;
        BackendId = CatalogValueV1.RequireText(backendId, 128, nameof(backendId));
    }

    public CatalogRevisionScopeV1 Scope { get; }
    public string Sequence { get; }
    public CatalogAdmissionV1 Admission { get; }
    public string CreatorPrincipalId { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public string OriginalFilename { get; }
    public string LogicalDocument { get; }
    public ArtifactDigest NativeDigest { get; }
    public int NativeByteCount { get; }
    public string BackendId { get; }
}

/// <summary>Exact metadata representation; excludes head timestamps and actor-varying action availability.</summary>
public sealed record CatalogMappingMetadataV1
{
    public CatalogMappingMetadataV1(CatalogMappingScopeV1 scope, string name, string description, string version)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
        Name = CatalogValueV1.RequireName(name, nameof(name));
        Description = CatalogValueV1.RequireDescription(description);
        Version = CatalogValueV1.RequirePositiveDecimal(version, nameof(version));
    }

    public CatalogMappingScopeV1 Scope { get; }
    public string Name { get; }
    public string Description { get; }
    public string Version { get; }
}
