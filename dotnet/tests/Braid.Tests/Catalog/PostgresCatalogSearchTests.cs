using Npgsql;
using Braid.Application;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogSearchTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task MetadataSearchIsLiteralPagedAndFreshlyAuthorized()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync("Needle %_🧵", "needle description", filename: "Needle.json");
        var search = new PostgresCatalogSearch(test.Reader);
        var page = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("needle"), 1);
        Assert.Single(page!.Hits);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(CatalogSearchKind.Mapping, page.Hits[0].Key.Kind);
        Assert.Equal(committed.Facts.FutureRevision.RevisionId, page.Hits[0].TargetRevisionId);
        var next = await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("needle"), 1, page.NextCursor);
        Assert.Equal(CatalogSearchKind.Revision, Assert.Single(next!.Hits).Key.Kind);
        Assert.Null(next.NextCursor);
        Assert.Single((await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("%_"), 100))!.Hits);
        Assert.Empty((await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("absent"), 100))!.Hits);
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, test.Project.ProjectId, test.Actor.PrincipalId.Value);
        Assert.Empty((await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("needle"), 1, page.NextCursor))!.Hits);
        Assert.Null(await search.SearchAsync(new(new(Guid.NewGuid())), test.Project.OrganizationId, new("needle"), 1));
    }

    [PostgresFact]
    public async Task AllMetadataKindsRespectProjectMembershipPrincipalAndOrganizationRevocation()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync("Unique mapping", "Metadata description", filename: "uploaded-file.json");
        var search = new PostgresCatalogSearch(test.Reader);
        foreach (var (query, kind) in new[]
        {
            ("Project A", CatalogSearchKind.Project), ("Metadata description", CatalogSearchKind.Mapping),
            (committed.Facts.BackendProfile.BackendId, CatalogSearchKind.Mapping),
            ("uploaded-file.json", CatalogSearchKind.Revision), (committed.Facts.NativeObject.Digest.Value, CatalogSearchKind.Revision)
        })
            Assert.Equal(kind, Assert.Single((await search.SearchAsync(test.Actor, test.Project.OrganizationId, new(query), 100))!.Hits).Key.Kind);
        Assert.Empty((await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("Hidden project"), 100))!.Hits);
        Assert.Empty((await search.SearchAsync(new(new(test.Seed.Admitted)), test.Project.OrganizationId, new("Unique mapping"), 100))!.Hits);
        await test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
            test.Project.OrganizationId, test.Actor.PrincipalId.Value);
        Assert.Null(await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("Unique mapping"), 100));
        await test.Case.ExecuteAsync("UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
            test.Project.OrganizationId, test.Actor.PrincipalId.Value);
        await test.Case.ExecuteAsync("UPDATE braid.principals SET active=false WHERE id=$1", test.Actor.PrincipalId.Value);
        Assert.Null(await search.SearchAsync(test.Actor, test.Project.OrganizationId, new("Unique mapping"), 100));
    }

    [PostgresFact]
    public async Task SearchRefusesAlteredIndexExpressionsPoliciesAndConstraints()
    {
        foreach (var change in new[]
        {
            "DROP INDEX braid.mappings_search_trgm; CREATE INDEX mappings_search_trgm ON braid.mappings USING gin ((lower(name COLLATE \"C\")) braid_search_extensions.gin_trgm_ops)",
            "ALTER POLICY catalog_search_report_index_organization_scope ON braid.catalog_search_report_index USING (true)",
            "ALTER TABLE braid.catalog_search_report_index ADD COLUMN unexpected text",
            "ALTER TABLE braid.catalog_search_report_index DROP CONSTRAINT catalog_search_report_index_terms_check",
            "ALTER FUNCTION braid.index_catalog_report(uuid,uuid,uuid,uuid,uuid,uuid,text,jsonb) IMMUTABLE"
        })
        {
            await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
            await test.Case.ExecuteAsync(change);
            var search = new PostgresCatalogSearch(test.Reader);
            await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(test.Actor, test.Project.OrganizationId, new("orders"), 1));
        }
    }

    [PostgresFact]
    public async Task SearchRefusesChangedPublicationViewOrDisabledIndexRowSecurity()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("ALTER TABLE braid.catalog_search_report_index DISABLE ROW LEVEL SECURITY");
        var search = new PostgresCatalogSearch(test.Reader);
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(test.Actor, test.Project.OrganizationId, new("orders"), 1));
        await test.Case.ExecuteAsync("ALTER TABLE braid.catalog_search_report_index ENABLE ROW LEVEL SECURITY");
        await test.Case.ExecuteAsync("CREATE OR REPLACE VIEW braid.catalog_search_reports WITH (security_barrier=true) AS SELECT organization_id,project_id,mapping_id,revision_id,derivation_id,derived_digest FROM braid.revision_derivation_attempts WHERE result_usable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync(test.Actor, test.Project.OrganizationId, new("orders"), 1));
    }

    [PostgresFact]
    public async Task SearchMigrationDoesNotGrantPublicFunctionExecution()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(test.Case.CatalogConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'braid' AND (has_function_privilege('public',p.oid,'EXECUTE') OR has_function_privilege(p.oid,'EXECUTE'))
            """, connection);
        var names = new List<string>();
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) names.Add(rows.GetString(0));
        Assert.Empty(names);
    }
}
