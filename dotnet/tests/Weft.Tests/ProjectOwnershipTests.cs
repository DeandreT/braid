using Weft.Application;
using Weft.Domain;

namespace Weft.Tests;

public sealed class ProjectOwnershipTests
{
    private static PrincipalId Principal => new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static ProjectScope Scope => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static DateTimeOffset CreatedAtUtc => new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OwnershipIdentifiersAreNonemptyValuesAndKeepOrganizationAncestry()
    {
        Assert.Throws<ArgumentException>(() => new PrincipalId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ProjectScope(Guid.Empty, Scope.ProjectId));
        Assert.Throws<ArgumentException>(() => new ProjectScope(Scope.OrganizationId, Guid.Empty));

        Assert.Equal(Principal, new PrincipalId(Principal.Value));
        Assert.Equal(Scope, new ProjectScope(Scope.OrganizationId, Scope.ProjectId));
        Assert.NotEqual(Scope, new ProjectScope(Principal.Value, Scope.ProjectId));
        Assert.NotEqual(Scope, new ProjectScope(Scope.OrganizationId, Principal.Value));
    }

    [Theory]
    [InlineData(ProjectRole.Viewer, true, false, false, false, false)]
    [InlineData(ProjectRole.Runner, true, true, false, false, false)]
    [InlineData(ProjectRole.Publisher, true, true, true, false, true)]
    [InlineData(ProjectRole.Administrator, true, true, true, true, true)]
    public void ObservedRolesMatchTheArchitecturePermissionMatrix(ProjectRole role,
        bool canRead, bool canPreview, bool canUpload, bool canManage, bool canEditMetadata)
    {
        var access = new ProjectAccess(Scope, Principal, role);

        Assert.Equal(canRead, access.Can(ProjectAction.ReadMetadata));
        Assert.Equal(canPreview, access.Can(ProjectAction.SubmitPreview));
        Assert.Equal(canUpload, access.Can(ProjectAction.UploadRevision));
        Assert.Equal(canManage, access.Can(ProjectAction.ManageMembership));
        Assert.Equal(canEditMetadata, access.Can(ProjectAction.EditMetadata));
        Assert.Equal(canUpload, access.Can(ProjectAction.EditMapping));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void UnknownRolesFailClosed(int unknown)
    {
        foreach (var action in Enum.GetValues<ProjectAction>())
            Assert.False(ProjectPermissions.Can((ProjectRole)unknown, action));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectAccess(Scope, Principal, (ProjectRole)unknown));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void UnknownActionsFailClosed(int unknown)
    {
        foreach (var role in Enum.GetValues<ProjectRole>())
        {
            Assert.False(ProjectPermissions.Can(role, (ProjectAction)unknown));
            Assert.False(new ProjectAccess(Scope, Principal, role).Can((ProjectAction)unknown));
        }
    }

    [Fact]
    public void MetadataEditingPreservesExistingPermissionIdentifiers()
    {
        Assert.Equal(1, (int)ProjectAction.ReadMetadata);
        Assert.Equal(2, (int)ProjectAction.SubmitPreview);
        Assert.Equal(3, (int)ProjectAction.UploadRevision);
        Assert.Equal(4, (int)ProjectAction.ManageMembership);
        Assert.Equal(5, (int)ProjectAction.EditMetadata);
        Assert.Equal(6, (int)ProjectAction.EditMapping);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<ProjectRole>().Select(role => (int)role));
    }

    [Fact]
    public void AccessObservationsKeepTheirActorAndScopeAndDoNotChangeEachOther()
    {
        var actor = new ActorContext(Principal);
        var earlier = new ProjectAccess(Scope, actor.PrincipalId, ProjectRole.Publisher);
        var later = new ProjectAccess(Scope, actor.PrincipalId, ProjectRole.Viewer);

        Assert.Equal(Principal, actor.PrincipalId);
        Assert.Equal(Scope, earlier.Scope);
        Assert.Equal(actor.PrincipalId, earlier.PrincipalId);
        Assert.True(earlier.Can(ProjectAction.UploadRevision));
        Assert.False(later.Can(ProjectAction.UploadRevision));
        Assert.NotEqual(earlier, later);
    }

    [Fact]
    public void ActorAndProjectAccessRejectMissingInternalIdentityOrScope()
    {
        Assert.Throws<ArgumentNullException>(() => new ActorContext(null!));
        Assert.Throws<ArgumentNullException>(() => new ProjectAccess(null!, Principal, ProjectRole.Viewer));
        Assert.Throws<ArgumentNullException>(() => new ProjectAccess(Scope, null!, ProjectRole.Viewer));
    }

    [Fact]
    public void ProjectSummaryPreservesMetadataAndNormalizesInstantsToUtc()
    {
        var offset = TimeSpan.FromHours(-7);
        var created = CreatedAtUtc.ToOffset(offset);
        var updated = created.AddMinutes(1);
        var summary = new ProjectSummary(Scope, "Example project", "Exact description\n", Principal, created, updated, 7);

        Assert.Equal(Scope, summary.Scope);
        Assert.Equal("Example project", summary.Name);
        Assert.Equal("Exact description\n", summary.Description);
        Assert.Equal(Principal, summary.CreatorPrincipalId);
        Assert.Equal(CreatedAtUtc, summary.CreatedAtUtc);
        Assert.Equal(CreatedAtUtc.AddMinutes(1), summary.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, summary.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, summary.UpdatedAtUtc.Offset);
        Assert.Equal(7, summary.Version);
    }

    [Fact]
    public void ProjectSummaryRejectsMissingOwnershipAndInvalidHistory()
    {
        Assert.Throws<ArgumentNullException>(() => new ProjectSummary(null!, "Project", "", Principal,
            CreatedAtUtc, CreatedAtUtc, 1));
        Assert.Throws<ArgumentNullException>(() => new ProjectSummary(Scope, "Project", "", null!,
            CreatedAtUtc, CreatedAtUtc, 1));
        Assert.Throws<ArgumentNullException>(() => new ProjectSummary(Scope, "Project", null!, Principal,
            CreatedAtUtc, CreatedAtUtc, 1));
        Assert.Throws<ArgumentNullException>(() => new ProjectSummary(Scope, null!, "", Principal,
            CreatedAtUtc, CreatedAtUtc, 1));
        Assert.Throws<ArgumentException>(() => new ProjectSummary(Scope, " \t", "", Principal,
            CreatedAtUtc, CreatedAtUtc, 1));
        Assert.Throws<ArgumentException>(() => new ProjectSummary(Scope, "Project", "", Principal,
            CreatedAtUtc, CreatedAtUtc.AddTicks(-1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSummary(Scope, "Project", "", Principal,
            CreatedAtUtc, CreatedAtUtc, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSummary(Scope, "Project", "", Principal,
            CreatedAtUtc, CreatedAtUtc, -1));
    }

    [Fact]
    public void ProjectPageOwnsItsCollectionAndRetainsTheSeekCursor()
    {
        var summary = Summary();
        var source = new List<ProjectSummary> { summary };
        var page = new ProjectPage(source, Scope.ProjectId);
        source.Clear();

        Assert.Same(summary, Assert.Single(page.Projects));
        Assert.Equal(Scope.ProjectId, page.NextProjectId);
        var exposedCollection = Assert.IsAssignableFrom<IList<ProjectSummary>>(page.Projects);
        Assert.True(exposedCollection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposedCollection.Clear());
        Assert.Same(summary, Assert.Single(page.Projects));
    }

    [Fact]
    public void ProjectPageRepresentsNoAccessWithoutAContinuationAndRejectsMalformedPages()
    {
        var empty = new ProjectPage([]);
        Assert.Empty(empty.Projects);
        Assert.Null(empty.NextProjectId);
        Assert.Throws<ArgumentNullException>(() => new ProjectPage(null!));
        Assert.Throws<ArgumentException>(() => new ProjectPage([null!]));
        Assert.Throws<ArgumentException>(() => new ProjectPage([Summary()], Guid.Empty));
    }

    private static ProjectSummary Summary() => new(Scope, "Project", "", Principal, CreatedAtUtc, CreatedAtUtc, 1);
}
