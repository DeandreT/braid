using System.Globalization;
using Weft.Application;
using Weft.Contracts.Catalog;
using Weft.Contracts.Projections;
using Weft.Domain;

namespace Weft.Api.Catalog;

/// <summary>Explicit public catalog projection; ingestion references, claims, evidence, and worker profiles are omitted.</summary>
public static class CatalogPublicMapper
{
    public static CatalogFeaturesV1 Features { get; } = new(true, false, false, false, false, false, false, false, false);

    public static CatalogContextV1 Context(CatalogOrganizationAdmission admission, string requestToken,
        bool metadataEditsEnabled = false, bool revisionIngestionEnabled = false,
        bool projectCreationEnabled = false, bool canCreateProjects = false,
        bool readReportsEnabled = false, bool deriveReportsEnabled = false) =>
        Context(admission, requestToken, "development", metadataEditsEnabled, revisionIngestionEnabled,
            projectCreationEnabled, canCreateProjects, readReportsEnabled, deriveReportsEnabled);

    internal static CatalogContextV1 Context(CatalogOrganizationAdmission admission, string requestToken,
        string mode, bool metadataEditsEnabled, bool revisionIngestionEnabled, bool projectCreationEnabled,
        bool canCreateProjects, bool readReportsEnabled, bool deriveReportsEnabled, string? identityContext = null,
        bool chatEnabled = false) =>
        new(mode, Id(admission.PrincipalId.Value), Id(admission.OrganizationId), admission.Name,
            metadataEditsEnabled || revisionIngestionEnabled || projectCreationEnabled || readReportsEnabled || deriveReportsEnabled || chatEnabled
                ? new(true, projectCreationEnabled, revisionIngestionEnabled, metadataEditsEnabled, revisionIngestionEnabled,
                    readReportsEnabled, chatEnabled, false, false, deriveReportsEnabled)
                : Features, new CatalogOrganizationActionsV1(projectCreationEnabled && canCreateProjects), requestToken, identityContext);

    public static CatalogProjectV1 Project(CatalogProject project, bool revisionIngestionEnabled = false,
        bool readReportsEnabled = false, bool deriveReportsEnabled = false)
    {
        var summary = project.Summary;
        return new(ProjectScope(summary.Scope), summary.Name, summary.Description,
            Id(summary.CreatorPrincipalId.Value), summary.CreatedAtUtc, summary.UpdatedAtUtc,
            Decimal(summary.Version), Access(project.Access, createMappingsEnabled: revisionIngestionEnabled,
                readReportsEnabled: readReportsEnabled, deriveReportsEnabled: deriveReportsEnabled));
    }

    public static CatalogMappingV1 Mapping(CatalogMapping mapping, bool metadataEditsEnabled = false,
        bool revisionIngestionEnabled = false, bool readReportsEnabled = false, bool deriveReportsEnabled = false) =>
        Mapping(mapping.Summary, mapping.Access, metadataEditsEnabled, revisionIngestionEnabled, readReportsEnabled, deriveReportsEnabled);

    public static CatalogMappingV1 Mapping(CatalogMappingSummary summary, ProjectAccess access,
        bool metadataEditsEnabled = false, bool revisionIngestionEnabled = false,
        bool readReportsEnabled = false, bool deriveReportsEnabled = false) =>
        new(MappingScope(summary.Scope), summary.Name, summary.Description, summary.BackendId,
            Id(summary.Creator.Value), Id(summary.Owner.Value), summary.CreatedAtUtc, summary.UpdatedAtUtc,
            Decimal(summary.Version), new CatalogHeadV1(RevisionScope(summary.Head.Scope),
                Decimal(summary.Head.Sequence), Admission(summary.Head.Admission)), Access(access, metadataEditsEnabled,
                    uploadRevisionsEnabled: revisionIngestionEnabled, readReportsEnabled: readReportsEnabled,
                    deriveReportsEnabled: deriveReportsEnabled));

    public static CatalogMappingMetadataV1 Metadata(CatalogMappingMetadata metadata) =>
        new(MappingScope(metadata.Scope), metadata.Name, metadata.Description, Decimal(metadata.Version));

    public static CatalogRevisionV1 Revision(CatalogRevision revision) => Revision(revision.Summary);

    public static CatalogRevisionV1 Revision(CatalogRevisionSummary summary) =>
        new(RevisionScope(summary.Scope), Decimal(summary.Sequence), Admission(summary.Admission),
            Id(summary.Creator.Value), summary.CommittedAtUtc, summary.OriginalFilename, summary.LogicalDocument,
            new ArtifactDigest("sha256", summary.NativeDigest.Value), checked((int)summary.NativeByteCount), summary.BackendId);

    public static CatalogProjectPageV1 Projects(CatalogProjectPage page, bool revisionIngestionEnabled = false,
        bool readReportsEnabled = false, bool deriveReportsEnabled = false) =>
        new(page.Projects.Select(project => Project(project, revisionIngestionEnabled, readReportsEnabled, deriveReportsEnabled)),
            page.NextCursor is { } cursor ? CatalogCursorCodec.Encode(cursor) : null);

    public static CatalogMappingPageV1 Mappings(CatalogMappingPage page, bool metadataEditsEnabled = false,
        bool revisionIngestionEnabled = false, bool readReportsEnabled = false, bool deriveReportsEnabled = false) =>
        new(page.Mappings.Select(mapping => Mapping(mapping, page.Access, metadataEditsEnabled, revisionIngestionEnabled,
                readReportsEnabled, deriveReportsEnabled)),
            page.NextCursor is { } cursor ? CatalogCursorCodec.Encode(cursor) : null);

    public static CatalogRevisionPageV1 Revisions(CatalogRevisionPage page) =>
        new(page.Revisions.Select(Revision), page.NextCursor is { } cursor ? CatalogCursorCodec.Encode(cursor) : null);

    public static CatalogProjectScopeV1 ProjectScope(ProjectScope scope) => new(Id(scope.OrganizationId), Id(scope.ProjectId));
    public static CatalogMappingScopeV1 MappingScope(MappingScope scope) =>
        new(Id(scope.OrganizationId), Id(scope.ProjectId), Id(scope.MappingId));
    public static CatalogRevisionScopeV1 RevisionScope(RevisionScope scope) =>
        new(Id(scope.OrganizationId), Id(scope.ProjectId), Id(scope.MappingId), Id(scope.RevisionId));

    private static CatalogAccessV1 Access(ProjectAccess access, bool metadataEditsEnabled = false,
        bool createMappingsEnabled = false, bool uploadRevisionsEnabled = false,
        bool readReportsEnabled = false, bool deriveReportsEnabled = false) => new(access.Role switch
        {
            ProjectRole.Viewer => CatalogRoleV1.Viewer,
            ProjectRole.Runner => CatalogRoleV1.Runner,
            ProjectRole.Publisher => CatalogRoleV1.Publisher,
            ProjectRole.Administrator => CatalogRoleV1.Administrator,
            _ => throw new InvalidOperationException("The catalog role is unknown.")
        }, new CatalogActionsV1(access.Can(ProjectAction.ReadMetadata), false,
        createMappingsEnabled && access.Can(ProjectAction.UploadRevision),
        metadataEditsEnabled && access.Can(ProjectAction.EditMetadata),
        uploadRevisionsEnabled && access.Can(ProjectAction.UploadRevision), readReportsEnabled && access.Can(ProjectAction.ReadMetadata),
        false, false, false, deriveReportsEnabled && access.Can(ProjectAction.ReadMetadata)));

    private static CatalogAdmissionV1 Admission(IngestionAdmissionClassification admission) => admission switch
    {
        IngestionAdmissionClassification.EligibleValid => CatalogAdmissionV1.Valid,
        IngestionAdmissionClassification.EligibleDecodedInvalid => CatalogAdmissionV1.DecodedInvalid,
        _ => throw new InvalidOperationException("The catalog revision has no immutable eligible admission.")
    };

    private static string Id(Guid id) => id.ToString("D");
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
