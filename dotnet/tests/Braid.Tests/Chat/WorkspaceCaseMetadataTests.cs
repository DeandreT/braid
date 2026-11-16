using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Braid.Api.Chat;
using Braid.Api.Chat.Workspace;
using Braid.Application;
using Braid.Application.Regression;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

public sealed class WorkspaceCaseMetadataTests
{
    [Fact]
    public void Exact_case_metadata_has_a_pinned_definition_without_sample_columns()
    {
        var loader = typeof(RegressionCaseRoleGuard).GetMethod("LoadBodies", BindingFlags.Static | BindingFlags.NonPublic)!;
        var bodies = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(loader.Invoke(null, null));
        var body = bodies["read_regression_case_metadata"];
        Assert.DoesNotContain("envelope", body, StringComparison.Ordinal);
        Assert.DoesNotContain("head_version", body, StringComparison.Ordinal);
        Assert.Contains("v.version=requested_version", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_superseded_case_version_is_cited_by_metadata_without_a_head_listing_or_sample_read()
    {
        var fixture = new WorkspaceToolFixture { Role = ProjectRole.Viewer };
        var cases = new CaseMetadata();
        var service = new WorkspaceToolService(new Services(fixture.Services, cases), new ChatApiOptions(), new(false, true, false, false));
        var citation = ChatWorkspaceCitationFact.RegressionCase(WorkspaceToolFixture.Mapping, CaseMetadata.Id, 1);
        Assert.Single(await service.VerifyAsync(WorkspaceToolFixture.Actor, fixture.Conversation(), [citation], CancellationToken.None));
        Assert.Equal(new long[] { 1 }, cases.Versions);
        Assert.Empty(await service.VerifyAsync(WorkspaceToolFixture.Actor, fixture.Conversation(),
            [ChatWorkspaceCitationFact.RegressionCase(WorkspaceToolFixture.Mapping, CaseMetadata.Id, 3)], CancellationToken.None));
        fixture.Role = null;
        Assert.Empty(await service.VerifyAsync(WorkspaceToolFixture.Actor, fixture.Conversation(), [citation], CancellationToken.None));
    }

    private sealed class Services(IServiceProvider other, CaseMetadata cases) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IRegressionCaseMetadataReader) || serviceType == typeof(IRegressionCaseReader)
            ? cases : other.GetService(serviceType);
    }
    private sealed class CaseMetadata : IRegressionCaseMetadataReader, IRegressionCaseReader
    {
        internal static readonly Guid Id = Guid.Parse("cccccccc-2222-4444-8888-111111111111");
        internal List<long> Versions { get; } = [];
        public Task<RegressionCaseSummary?> ReadMetadataAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
            CancellationToken cancellationToken = default)
        {
            Versions.Add(version);
            return Task.FromResult<RegressionCaseSummary?>(caseId == Id && version is 1 or 2 && mapping == WorkspaceToolFixture.Mapping
                ? new(Id, version, WorkspaceToolFixture.Head, WorkspaceToolFixture.Actor.PrincipalId, "case", WorkspaceToolFixture.Now,
                    WorkspaceToolFixture.Actor.PrincipalId) : null);
        }
        public Task<RegressionCasePage> ListAsync(ActorContext actor, MappingScope mapping, int limit, Guid? afterCaseId = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Head-only metadata must not verify a retained citation.");
        public Task<RegressionCaseVersion?> ReadAsync(ActorContext actor, MappingScope mapping, Guid caseId, long version,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Citation verification must not read sample bytes.");
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresWorkspaceCaseMetadataTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task A_viewer_without_payload_access_can_verify_version_one_after_version_two_is_saved()
    {
        await using var test = await RegressionCasePostgresTestSupport.CreateAsync(fixture);
        Assert.Equal(RegressionCaseSaveOutcome.Created, (await test.Writer.SaveAsync(test.Actor, test.Version(1, "first"))).Outcome);
        Assert.Equal(RegressionCaseSaveOutcome.Appended, (await test.Writer.SaveAsync(test.Actor, test.Version(2, "second"))).Outcome);
        await test.RoleAsync("viewer");
        await test.Case.ExecuteAsync("REVOKE EXECUTE ON FUNCTION braid.read_regression_case(uuid,uuid,uuid,uuid,bigint) FROM braid_case_reader");
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.project_payload_read_grants"));
        var metadata = Assert.IsType<RegressionCaseSummary>(await test.Reader.ReadMetadataAsync(test.Actor, test.Mapping, test.CaseId, 1));
        Assert.Equal(1, metadata.Version);
        Assert.Equal("first", metadata.Name);
        await using var catalogDatabase = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var catalog = new PostgresCatalogReader(catalogDatabase);
        var reports = new MappingToolFixture();
        await using var services = new ServiceCollection().AddSingleton<ICatalogReader>(catalog)
            .AddSingleton<ICatalogResourceLocator>(catalog).AddSingleton<IRegressionCaseMetadataReader>(test.Reader)
            .AddSingleton<IRevisionReportReader>(reports).AddSingleton<IRevisionReportMetadataReader>(reports).BuildServiceProvider();
        var tools = new WorkspaceToolService(services, new ChatApiOptions(), new(false, true, false, false));
        var conversation = ChatConversationIntent.ForWorkspace(ChatConversationKey.ForWorkspace(test.Mapping.OrganizationId,
            test.Actor.PrincipalId, Guid.NewGuid()), null, new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero).AddHours(1));
        var citation = ChatWorkspaceCitationFact.RegressionCase(test.Mapping, test.CaseId, 1);
        Assert.Single(await tools.VerifyAsync(test.Actor, conversation, [citation], CancellationToken.None));
        Assert.Null(await test.Reader.ReadMetadataAsync(test.Actor, test.Mapping, test.CaseId, 3));
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE principal_id=$1 AND project_id=$2",
            test.Seed.Alice, test.Seed.ProjectA);
        Assert.Null(await test.Reader.ReadMetadataAsync(test.Actor, test.Mapping, test.CaseId, 1));
        Assert.Empty(await tools.VerifyAsync(test.Actor, conversation, [citation], CancellationToken.None));
    }
}
