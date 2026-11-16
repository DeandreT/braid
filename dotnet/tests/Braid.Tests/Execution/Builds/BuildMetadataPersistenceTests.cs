using Braid.Application;
using Braid.Deployment;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BuildMetadataPersistenceTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task Revoked_approval_and_inactive_environment_remain_original_owned_metadata_not_active_admission()
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await test.Approve());
        await using var reader = Reader(test);
        var mapping = Mapping(test.Intent);
        var before = await reader.ReadApprovalAsync(test.Actor, mapping, test.Intent.ApprovalId);
        Assert.NotNull(before); Assert.False(before.Revoked); Assert.True(before.EnvironmentActive);
        Assert.True(await test.Store.RevokeAsync(test.Actor, mapping.OrganizationId, mapping.ProjectId, test.Intent.ApprovalId));
        Assert.Equal(BuildControlOutcome.Created, await test.Store.SaveEnvironmentAsync(test.Actor,
            new(new(test.Intent.Environment.Project, test.Intent.Environment.EnvironmentId, 2), test.Intent.Profile, true)));
        var retained = (await reader.ReadApprovalAsync(test.Actor, mapping, test.Intent.ApprovalId))!;
        Assert.Equal(before.Intent.Fingerprint, retained.Intent.Fingerprint);
        Assert.True(retained.Revoked); Assert.False(retained.EnvironmentActive);
        var old = (await reader.ReadEnvironmentAsync(test.Actor, test.Intent.Environment))!;
        Assert.False(old.Environment.Active); Assert.Equal(test.Intent.Profile, old.Environment.Profile);
        Assert.Null(await test.Store.ReadApprovalAsync(test.Actor, test.Intent));
        Assert.Null(await test.Store.ReadEnvironmentAsync(test.Actor, test.Intent.Environment));
    }

    [PostgresFact]
    public async Task Own_pages_are_stable_and_foreign_scope_or_cursors_remain_opaque()
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var reader = Reader(test);
        await using var commands = new PostgresBuildRunStore(test.Runs.Cases.Case.RegressionConnectionString("braid_build_run_writer"), BuildRunDatabaseProfile.Writer);
        var intents = new[] { test.Intent, Copy(test.Intent), Copy(test.Intent) };
        foreach (var intent in intents)
        {
            Assert.Equal(BuildControlOutcome.Created, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, intent, test.Native, test.Evidence)));
            Assert.Equal(BuildRunSubmitOutcome.Created, await commands.SubmitAsync(test.Actor, intent));
        }
        Assert.True(await commands.CancelAsync(test.Actor, test.Intent.Revision.OrganizationId, intents[0].BuildId));
        var mapping = Mapping(test.Intent);
        var approvals = new List<Guid>(); Guid? cursor = null;
        do
        {
            var page = (await reader.ListApprovalsAsync(test.Actor, mapping, 1, cursor))!;
            approvals.AddRange(page.Approvals.Select(value => value.Intent.ApprovalId)); cursor = page.NextApprovalId;
        } while (cursor is not null);
        Assert.Equal(3, approvals.Count); Assert.Equal(3, approvals.Distinct().Count());
        var runs = (await reader.ListRunsAsync(test.Actor, mapping, 10))!;
        Assert.Equal(3, runs.Runs.Count); Assert.Contains(runs.Runs, value => value.State == BuildRunState.Canceled);
        Assert.Null(await reader.ListRunsAsync(test.Actor, mapping, 1, Guid.NewGuid()));
        Assert.Null(await reader.ListApprovalsAsync(test.Actor, mapping, 1, Guid.NewGuid()));
        Assert.Null(await reader.ReadApprovalAsync(new(new(Guid.NewGuid())), mapping, test.Intent.ApprovalId));
        Assert.Null(await reader.ReadApprovalAsync(test.Actor, new(mapping.OrganizationId, mapping.ProjectId, Guid.NewGuid()), test.Intent.ApprovalId));
        Assert.Null(await reader.ListApprovalsAsync(test.Actor, new(Guid.NewGuid(), mapping.ProjectId, mapping.MappingId), 1));
    }

    [PostgresFact]
    public async Task Byte_limited_history_shortens_the_page_and_preserves_every_continuation()
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        await using var reader = Reader(test);
        var boundaries = new List<BuildBoundary>
        {
            new(BuildBoundaryKind.PrimaryInput, null, new string('+', 128)),
            new(BuildBoundaryKind.PrimaryOutput, null, new string('+', 128))
        };
        boundaries.AddRange(Enumerable.Range(0, 126).Select(index => new BuildBoundary(BuildBoundaryKind.NamedInput,
            "name" + new string('+', 240) + index.ToString(System.Globalization.CultureInfo.InvariantCulture), new string('+', 128))));
        var expected = new HashSet<Guid>();
        for (var index = 0; index < 24; index++)
        {
            var intent = Copy(test.Intent, boundaries);
            Assert.True(intent.CopyCanonicalBytes().Length > 180000);
            Assert.Equal(BuildControlOutcome.Created, await test.Store.ApproveAsync(test.Actor, new(test.Actor.PrincipalId, intent, test.Native, test.Evidence)));
            expected.Add(intent.ApprovalId);
        }
        var first = (await reader.ListApprovalsAsync(test.Actor, Mapping(test.Intent), 100))!;
        Assert.InRange(first.Approvals.Count, 1, 23); Assert.NotNull(first.NextApprovalId);
        var found = first.Approvals.Select(value => value.Intent.ApprovalId).ToList();
        var cursor = first.NextApprovalId;
        while (cursor is not null)
        {
            var next = (await reader.ListApprovalsAsync(test.Actor, Mapping(test.Intent), 100, cursor))!;
            found.AddRange(next.Approvals.Select(value => value.Intent.ApprovalId)); cursor = next.NextApprovalId;
        }
        Assert.Equal(24, found.Count); Assert.True(expected.SetEquals(found));
    }

    [PostgresFact]
    public async Task A_metadata_function_on_a_schema18_shaped_database_cannot_use_legacy_guard_fallback()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var prefix = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(18).ToArray())) await prefix.MigrateAsync();
        await test.ExecuteAsync("""
            CREATE FUNCTION braid.read_build_metadata(operation text, org uuid, project uuid, mapping uuid, key uuid, revision bigint, maximum integer, after_id uuid, after_version bigint)
            RETURNS TABLE(document jsonb,intent bytea) LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,braid,pg_temp
            AS 'SELECT ''{}''::jsonb,NULL::bytea';
            REVOKE ALL ON FUNCTION braid.read_build_metadata(text,uuid,uuid,uuid,uuid,bigint,integer,uuid,bigint) FROM PUBLIC;
            GRANT USAGE ON SCHEMA braid TO braid_build_metadata_reader;
            GRANT EXECUTE ON FUNCTION braid.read_build_metadata(text,uuid,uuid,uuid,uuid,bigint,integer,uuid,bigint) TO braid_build_metadata_reader
            """);
        await using var reader = new PostgresBuildMetadataReader(test.RegressionConnectionString("braid_build_metadata_reader"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEnvironmentAsync(new(new(Guid.NewGuid())),
            new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid(), 1)));
    }

    [RegressionPostgresTheory]
    [InlineData("function")]
    [InlineData("index")]
    [InlineData("direct-table-grant")]
    [InlineData("foreign-function-grant")]
    public async Task Metadata_profile_refuses_tampered_schema_or_broadened_credentials(string change)
    {
        await using var test = await BuildPersistenceTests.Context.CreateAsync(fixture);
        Assert.Equal(BuildControlOutcome.Created, await test.Approve());
        await using var reader = Reader(test);
        Assert.NotNull(await reader.ReadApprovalAsync(test.Actor, Mapping(test.Intent), test.Intent.ApprovalId));
        await test.Runs.Cases.Case.ExecuteAsync(change switch
        {
            "function" => "ALTER FUNCTION braid.read_build_metadata(text,uuid,uuid,uuid,uuid,bigint,integer,uuid,bigint) VOLATILE",
            "index" => "DROP INDEX braid.build_approval_history",
            "direct-table-grant" => "GRANT SELECT ON braid.build_approvals TO braid_build_metadata_reader",
            _ => "GRANT EXECUTE ON FUNCTION braid.build_json_text(text,boolean) TO braid_build_metadata_reader"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadApprovalAsync(test.Actor, Mapping(test.Intent), test.Intent.ApprovalId));
    }

    private static PostgresBuildMetadataReader Reader(BuildPersistenceTests.Context test) =>
        new(test.Runs.Cases.Case.RegressionConnectionString("braid_build_metadata_reader"));
    private static MappingScope Mapping(BuildIntent intent) => new(intent.Revision.OrganizationId, intent.Revision.ProjectId, intent.Revision.MappingId);
    private static BuildIntent Copy(BuildIntent intent, IEnumerable<BuildBoundary>? boundaries = null) => new(Guid.NewGuid(), intent.Revision, intent.NativeDigest,
        intent.Backend, intent.Profile, (boundaries ?? intent.Boundaries).ToArray(), intent.Environment, Guid.NewGuid(), intent.RegressionEvidenceId);
}
