using Npgsql;
using Braid.Application;
using Braid.Application.Lifecycle;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CatalogArchiveVisibilityTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ArchiveHidesOnlyDiscoveryAndRestoreKeepsTheSameIdentifiers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture, installCatalog: false);
        var committed = await test.CommitCreateAsync("Archive needle", filename: "needle.json");
        await test.Case.MigrateAsync();
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='administrator',version=version+1 WHERE principal_id=$1 AND project_id=$2", test.Actor.PrincipalId.Value, test.Project.ProjectId);
        var project = test.Project;
        var revision = committed.Facts.FutureRevision;
        var mapping = new MappingScope(project.OrganizationId, project.ProjectId, revision.MappingId);
        var search = new PostgresCatalogSearch(test.Reader);
        await using var admin = new PostgresAdministrationStore(test.Case.RegressionConnectionString("braid_administration"));
        foreach (var key in new[] { new ResourceKey(project, mapping.MappingId), new ResourceKey(project) })
        {
            var initial = (await admin.ReadAsync(test.Actor, key, token))!;
            var archive = await admin.ChangeAsync(test.Actor, new(key, ResourceLifecycleAction.Archive, Guid.NewGuid(), initial.Version), token);
            Assert.Equal(ResourceLifecycleOutcome.Archived, archive.Outcome);
            var listed = await test.Reader.ListProjectsAsync(test.Actor, project.OrganizationId, 100, cancellationToken: token);
            Assert.Equal(key.IsMapping, listed!.Projects.Any(p => p.Summary.Scope == project));
            Assert.Empty((await test.Reader.ListMappingsAsync(test.Actor, project, 100, cancellationToken: token))!.Mappings);
            Assert.Empty((await search.SearchAsync(test.Actor, project.OrganizationId, new("needle"), 10, cancellationToken: token))!.Hits);
            Assert.NotNull(await test.Reader.GetProjectAsync(test.Actor, project, token));
            Assert.NotNull(await test.Reader.GetMappingAsync(test.Actor, mapping, token));
            Assert.NotNull(await test.Reader.GetRevisionAsync(test.Actor, revision, token));
            Assert.Null(await test.Reader.GetMappingAsync(new(new(Guid.NewGuid())), mapping, token));
            Assert.Equal(ResourceLifecycleOutcome.Restored, (await admin.ChangeAsync(test.Actor,
                new(key, ResourceLifecycleAction.Restore, Guid.NewGuid(), archive.Receipt!.Version), token)).Outcome);
            Assert.Equal(mapping, Assert.Single((await test.Reader.ListMappingsAsync(test.Actor, project, 100, cancellationToken: token))!.Mappings).Scope);
            Assert.NotEmpty((await search.SearchAsync(test.Actor, project.OrganizationId, new("needle"), 10, cancellationToken: token))!.Hits);
        }

        // An archive holds the organization gate until commit. A delete arriving with the old
        // version waits, then checks the committed version rather than accepting the stale review.
        var resource = new ResourceKey(project, mapping.MappingId);
        var version = (await admin.ReadAsync(test.Actor, resource, token))!.Version;
        await using var holder = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.RegressionConnectionString("braid_administration")) { Pooling = false }.ConnectionString);
        await holder.OpenAsync(token);
        await using var tx = await holder.BeginTransactionAsync(token);
        await using var archiveCommand = new NpgsqlCommand("SELECT set_config('braid.organization_id',$1,true)", holder, tx);
        archiveCommand.Parameters.AddWithValue(project.OrganizationId.ToString());
        await archiveCommand.ExecuteNonQueryAsync(token);
        archiveCommand.CommandText = "SELECT braid.change_resource_lifecycle($1,$2,$3,$4,$5,'archive',$6,$7)->>'outcome'";
        archiveCommand.Parameters.Clear();
        foreach (var v in new object[] { project.OrganizationId, test.Actor.PrincipalId.Value, Guid.NewGuid(), project.ProjectId, mapping.MappingId, version.Project, version.Mapping!.Value })
            archiveCommand.Parameters.Add(new NpgsqlParameter { Value = v });
        Assert.Equal("archived", await archiveCommand.ExecuteScalarAsync(token));
        var deletion = admin.RequestAsync(test.Actor, new(resource, Guid.NewGuid(), Guid.NewGuid(), version), token);
        while (!await test.Case.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND locktype='advisory' AND classid=1464158804 AND NOT granted)"))
        {
            Assert.False(deletion.IsCompleted);
            await Task.Delay(10, token);
        }
        await tx.CommitAsync(token);
        Assert.Equal(ResourcePurgeOutcome.PreconditionFailed, (await deletion).Outcome);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.deletion_jobs"));
    }
}
