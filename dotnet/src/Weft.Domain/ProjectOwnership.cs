namespace Weft.Domain;

public sealed record PrincipalId
{
    public PrincipalId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A principal identifier cannot be empty.", nameof(value));

        Value = value;
    }

    public Guid Value { get; }
}

public sealed record ProjectScope
{
    public ProjectScope(Guid organizationId, Guid projectId)
    {
        OrganizationId = RequireId(organizationId, nameof(organizationId));
        ProjectId = RequireId(projectId, nameof(projectId));
    }

    public Guid OrganizationId { get; }
    public Guid ProjectId { get; }

    private static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("A project scope identifier cannot be empty.", parameterName);

        return value;
    }
}

public enum ProjectRole
{
    Viewer = 1,
    Runner = 2,
    Publisher = 3,
    Administrator = 4
}

public enum ProjectAction
{
    ReadMetadata = 1,
    SubmitPreview = 2,
    UploadRevision = 3,
    ManageMembership = 4
}

public static class ProjectPermissions
{
    public static bool Can(ProjectRole role, ProjectAction action) => role switch
    {
        ProjectRole.Viewer => action is ProjectAction.ReadMetadata,
        ProjectRole.Runner => action is ProjectAction.ReadMetadata or ProjectAction.SubmitPreview,
        ProjectRole.Publisher => action is ProjectAction.ReadMetadata or ProjectAction.SubmitPreview or
            ProjectAction.UploadRevision,
        ProjectRole.Administrator => action is ProjectAction.ReadMetadata or ProjectAction.SubmitPreview or
            ProjectAction.UploadRevision or ProjectAction.ManageMembership,
        _ => false
    };
}
