using Npgsql;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

/// <summary>Actual PostgreSQL catalog qualification with explicitly synthetic committed admission fixtures.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCatalogReaderTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OrganizationAdmissionDoesNotRequireAProjectAndDoesNotConferProjectVisibility()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var admitted = new ActorContext(new(test.Seed.Admitted));
        var admission = await test.Reader.GetOrganizationAdmissionAsync(admitted, test.Seed.OrganizationA);
        Assert.Equal(test.Seed.OrganizationA, admission!.OrganizationId);
        Assert.Equal(admitted.PrincipalId, admission.PrincipalId);
        Assert.Equal("Organization A", admission.Name);
        var empty = await test.Reader.ListProjectsAsync(admitted, test.Seed.OrganizationA, 10);
        Assert.Equal(admission, empty!.Admission);
        Assert.Empty(empty.Projects);
        Assert.Null(empty.NextCursor);
        Assert.Null(await test.Reader.GetProjectAsync(admitted, test.Project));
        Assert.Null(await test.Reader.ListMappingsAsync(admitted, test.Project, 10));

        var otherAdministrator = new ActorContext(new(test.Seed.OtherProjectAdmin));
        Assert.NotNull(await test.Reader.GetOrganizationAdmissionAsync(otherAdministrator, test.Seed.OrganizationA));
        var otherProjects = await test.Reader.ListProjectsAsync(otherAdministrator, test.Seed.OrganizationA, 10);
        Assert.Equal(test.Seed.HiddenProjectA, Assert.Single(otherProjects!.Projects).Summary.Scope.ProjectId);
        Assert.Null(await test.Reader.GetProjectAsync(otherAdministrator, test.Project));
        Assert.Null(await test.Reader.ListMappingsAsync(otherAdministrator, test.Project, 10));

        var bob = new ActorContext(new(test.Seed.Bob));
        Assert.NotNull(await test.Reader.GetOrganizationAdmissionAsync(bob, test.Seed.OrganizationB));
        Assert.Empty((await test.Reader.ListProjectsAsync(bob, test.Seed.OrganizationB, 10))!.Projects);
        Assert.Null(await test.Reader.GetOrganizationAdmissionAsync(bob, test.Seed.OrganizationA));
        Assert.Null(await test.Reader.ListProjectsAsync(bob, test.Seed.OrganizationA, 10));
        Assert.Null(await test.Reader.GetOrganizationAdmissionAsync(new(new PrincipalId(Guid.NewGuid())), test.Seed.OrganizationA));
        Assert.Null(await test.Reader.GetOrganizationAdmissionAsync(test.Actor, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task InaccessibleCollectionParentIsNullWhileVisibleEmptyContinuationRetainsItsAuthority()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var empty = await test.Reader.ListMappingsAsync(test.Actor, test.Project, 10);
        Assert.Equal(test.Project, empty!.Access.Scope);
        Assert.Equal(test.Actor.PrincipalId, empty.Access.PrincipalId);
        Assert.Empty(empty.Mappings);
        Assert.Null(empty.NextCursor);
        var hidden = new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA);
        Assert.Null(await test.Reader.ListMappingsAsync(test.Actor, hidden, 10));
        Assert.Null(await test.Reader.ListMappingsAsync(test.Actor, new(test.Project.OrganizationId, Guid.NewGuid()), 10));
        Assert.Null(await test.Reader.ListRevisionsAsync(test.Actor,
            new(test.Project.OrganizationId, test.Project.ProjectId, Guid.NewGuid()), 10));

        var committed = await test.CommitCreateAsync();
        var mapping = PostgresCatalogTestSupport.Target(committed);
        var afterLast = await test.Reader.ListMappingsAsync(test.Actor, test.Project, 10,
            new(test.Project, mapping.MappingId));
        Assert.Empty(afterLast!.Mappings);
        Assert.Equal(empty.Access, afterLast.Access);
        Assert.Null(afterLast.NextCursor);
        var beforeFirst = await test.Reader.ListRevisionsAsync(test.Actor, mapping, 10, new(mapping, 1));
        Assert.Equal(mapping, beforeFirst!.Mapping);
        Assert.Equal(test.Project, beforeFirst.Access.Scope);
        Assert.Empty(beforeFirst.Revisions);
        Assert.Null(beforeFirst.NextCursor);
        var projectEnd = await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 10,
            new(test.Project.OrganizationId, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")));
        Assert.Empty(projectEnd!.Projects);
        Assert.Equal(test.Actor.PrincipalId, projectEnd.Admission.PrincipalId);
    }

    [PostgresFact]
    public async Task EveryMappingAndRevisionLookupRequiresAllAncestorsEvenWithAuthorityInTheOtherProject()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync();
        var mapping = PostgresCatalogTestSupport.Target(committed);
        var revision = committed.Facts.FutureRevision;
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA, test.Seed.Alice, "viewer");
        foreach (var wrong in new[]
        {
            new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectB, mapping.MappingId),
            new MappingScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA, mapping.MappingId),
            new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectA, mapping.MappingId),
            new MappingScope(mapping.OrganizationId, mapping.ProjectId, Guid.NewGuid())
        })
        {
            Assert.Null(await test.Reader.GetMappingAsync(test.Actor, wrong));
            Assert.Null(await test.Reader.GetMappingMetadataAsync(test.Actor, wrong));
            Assert.Null(await test.Reader.ListRevisionsAsync(test.Actor, wrong, 10));
            Assert.Null(await test.Reader.GetRevisionAsync(test.Actor,
                new(wrong.OrganizationId, wrong.ProjectId, wrong.MappingId, revision.RevisionId)));
        }
        Assert.Null(await test.Reader.GetRevisionAsync(test.Actor,
            new(mapping.OrganizationId, mapping.ProjectId, mapping.MappingId, Guid.NewGuid())));
        var second = await test.CommitCreateAsync();
        Assert.Null(await test.Reader.GetRevisionAsync(test.Actor,
            new(mapping.OrganizationId, mapping.ProjectId, second.Facts.FutureRevision.MappingId, revision.RevisionId)));
        Assert.Equal(mapping, (await test.Reader.GetMappingAsync(test.Actor, mapping))!.Summary.Scope);
        Assert.Equal(revision, (await test.Reader.GetRevisionAsync(test.Actor, revision))!.Summary.Scope);
    }

    [PostgresFact]
    public async Task EveryExplicitProjectRoleReadsCatalogAndRoleChangesAreVisibleOnTheNextStatement()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync(valid: false);
        var mapping = PostgresCatalogTestSupport.Target(committed);
        foreach (var (sqlRole, role) in new[]
        {
            ("viewer", ProjectRole.Viewer), ("runner", ProjectRole.Runner),
            ("publisher", ProjectRole.Publisher), ("administrator", ProjectRole.Administrator)
        })
        {
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                sqlRole, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            Assert.Equal(role, (await test.Reader.GetProjectAsync(test.Actor, test.Project))!.Access.Role);
            Assert.Equal(role, Assert.Single((await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 10))!.Projects).Access.Role);
            Assert.Equal(role, (await test.Reader.ListMappingsAsync(test.Actor, test.Project, 10))!.Access.Role);
            Assert.Equal(role, (await test.Reader.GetMappingAsync(test.Actor, mapping))!.Access.Role);
            Assert.Equal(role, (await test.Reader.ListRevisionsAsync(test.Actor, mapping, 10))!.Access.Role);
            var revision = await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision);
            Assert.Equal(role, revision!.Access.Role);
            Assert.Equal(IngestionAdmissionClassification.EligibleDecodedInvalid, revision.Summary.Admission);
            Assert.NotNull(await test.Reader.GetMappingMetadataAsync(test.Actor, mapping));
        }
    }

    [PostgresFact]
    public async Task PrincipalOrganizationAndProjectRevocationAffectsEveryNextReadAndRestorationRecovers()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = await test.CommitCreateAsync();
        var mapping = PostgresCatalogTestSupport.Target(committed);
        foreach (var (revoke, restore, values, losesAdmission) in new[]
        {
            ("UPDATE braid.principals SET active=false WHERE id=$1", "UPDATE braid.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }, true),
            ("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", new object[] { test.Project.OrganizationId, test.Seed.Alice }, true),
            ("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE braid.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice }, false)
        })
        {
            Assert.NotNull(await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision));
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                var admission = await test.Reader.GetOrganizationAdmissionAsync(test.Actor, test.Project.OrganizationId);
                Assert.Equal(losesAdmission, admission is null);
                var projects = await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 10);
                if (losesAdmission) Assert.Null(projects); else Assert.Empty(projects!.Projects);
                Assert.Null(await test.Reader.GetProjectAsync(test.Actor, test.Project));
                Assert.Null(await test.Reader.ListMappingsAsync(test.Actor, test.Project, 10));
                Assert.Null(await test.Reader.GetMappingAsync(test.Actor, mapping));
                Assert.Null(await test.Reader.GetMappingMetadataAsync(test.Actor, mapping));
                Assert.Null(await test.Reader.ListRevisionsAsync(test.Actor, mapping, 10));
                Assert.Null(await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision));
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
            Assert.NotNull(await test.Reader.GetOrganizationAdmissionAsync(test.Actor, test.Project.OrganizationId));
            Assert.Equal(mapping, (await test.Reader.GetMappingAsync(test.Actor, mapping))!.Summary.Scope);
            Assert.Equal(committed.Facts.FutureRevision, (await test.Reader.GetRevisionAsync(test.Actor, committed.Facts.FutureRevision))!.Summary.Scope);
        }
    }

    [PostgresFact]
    public async Task LiteralMetadataAndDecodedInvalidHistoryRemainReadableWithoutWorkerOrPrivateArtifactPorts()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        const string name = "literal '); DROP TABLE braid.mappings; -- Ω";
        const string description = "line one\nline two $1 \\ unicode 🧵 and a/b~c";
        var original = await test.CommitCreateAsync(name, description, filename: "original Ω.native.json");
        var appended = await test.CommitAppendAsync(original, valid: false, filename: "decoded-invalid.native.json");
        var target = PostgresCatalogTestSupport.Target(original);
        var mapping = await test.Reader.GetMappingAsync(test.Actor, target);
        Assert.Equal(name, mapping!.Summary.Name);
        Assert.Equal(description, mapping.Summary.Description);
        Assert.Equal("ferrule", mapping.Summary.BackendId);
        Assert.Equal(test.Actor.PrincipalId, mapping.Summary.Creator);
        Assert.Equal(test.Actor.PrincipalId, mapping.Summary.Owner);
        Assert.Equal(1, mapping.Summary.Version);
        Assert.Equal(appended.Facts.FutureRevision, mapping.Summary.Head.Scope);
        Assert.Equal(2, mapping.Summary.Head.Sequence);
        Assert.Equal(IngestionAdmissionClassification.EligibleDecodedInvalid, mapping.Summary.Head.Admission);
        var metadata = await test.Reader.GetMappingMetadataAsync(test.Actor, target);
        Assert.Equal(new CatalogMappingMetadata(target, name, description, 1), metadata);
        var history = await test.Reader.ListRevisionsAsync(test.Actor, target, 10);
        Assert.Equal(new long[] { 2, 1 }, history!.Revisions.Select(row => row.Sequence));
        Assert.Equal(new[] { IngestionAdmissionClassification.EligibleDecodedInvalid, IngestionAdmissionClassification.EligibleValid },
            history.Revisions.Select(row => row.Admission));
        foreach (var (receipt, filename) in new[] { (original, "original Ω.native.json"), (appended, "decoded-invalid.native.json") })
        {
            var row = (await test.Reader.GetRevisionAsync(test.Actor, receipt.Facts.FutureRevision))!.Summary;
            Assert.Equal(receipt.Facts.FutureRevision, row.Scope);
            Assert.Equal(filename, row.OriginalFilename);
            Assert.Equal("entry.native.json", row.LogicalDocument);
            Assert.Equal(receipt.Facts.NativeObject.Digest, row.NativeDigest);
            Assert.Equal(receipt.Facts.NativeObject.ByteCount, row.NativeByteCount);
            Assert.Equal(test.Actor.PrincipalId, row.Creator);
            Assert.Equal(TimeSpan.Zero, row.CommittedAtUtc.Offset);
            Assert.Equal(receipt.CommittedRevision!.CommittedAtUtc, row.CommittedAtUtc);
        }
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mappings"));
    }

    [PostgresFact]
    public async Task ProjectKeysetPagesUsePostgresUuidOrderAndFreshAuthorityWithoutDuplicatingRows()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var ids = new[] { "10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000001",
            "30000000-0000-0000-0000-000000000001", "40000000-0000-0000-0000-000000000001" }.Select(Guid.Parse).ToArray();
        foreach (var id in ids)
        {
            await test.Seed.AddProjectAsync(test.Case, test.Project.OrganizationId, id, test.Seed.Alice, id.ToString("D"), "literal ' $1");
            await test.Seed.AddMembershipAsync(test.Case, test.Project.OrganizationId, id, test.Seed.Alice, "viewer");
        }
        var first = (await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 2))!;
        Assert.Equal(new[] { test.Project.ProjectId, ids[0] }, first.Projects.Select(row => row.Summary.Scope.ProjectId));
        Assert.Equal(new CatalogProjectCursor(test.Project.OrganizationId, ids[0]), first.NextCursor);
        var behind = Guid.Parse("08000000-0000-0000-0000-000000000001");
        var ahead = Guid.Parse("50000000-0000-0000-0000-000000000001");
        foreach (var id in new[] { behind, ahead })
        {
            await test.Seed.AddProjectAsync(test.Case, test.Project.OrganizationId, id, test.Seed.Alice, "inserted after page", "");
            await test.Seed.AddMembershipAsync(test.Case, test.Project.OrganizationId, id, test.Seed.Alice, "viewer");
        }
        // A previously issued cursor conveys no permission for subsequent rows.
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Project.OrganizationId, ids[1], test.Seed.Alice);
        var seen = first.Projects.Select(row => row.Summary.Scope.ProjectId).ToList();
        var cursor = first.NextCursor;
        var pageCount = 0;
        while (cursor is not null)
        {
            Assert.InRange(++pageCount, 1, 4);
            var page = (await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 2, cursor))!;
            Assert.InRange(page.Projects.Count, 1, 2);
            seen.AddRange(page.Projects.Select(row => row.Summary.Scope.ProjectId));
            if (page.NextCursor is not null) Assert.Equal(page.Projects[^1].Summary.Scope.ProjectId, page.NextCursor.AfterProjectId);
            cursor = page.NextCursor;
        }
        Assert.Equal(new[] { test.Project.ProjectId, ids[0], ids[2], ids[3], ahead }, seen);
        Assert.DoesNotContain(behind, seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        var fresh = (await test.Reader.ListProjectsAsync(test.Actor, test.Project.OrganizationId, 100))!;
        Assert.Contains(fresh.Projects, row => row.Summary.Scope.ProjectId == behind);
        Assert.DoesNotContain(fresh.Projects, row => row.Summary.Scope.ProjectId == test.Seed.HiddenProjectA);
    }

    [PostgresFact]
    public async Task MappingAndHistoryKeysetsStayScopedAndNewHeadDoesNotDuplicateOlderContinuation()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var committed = new List<IngestionReceipt>();
        for (var index = 0; index < 5; index++) committed.Add(await test.CommitCreateAsync("mapping " + index));
        var expected = committed.Select(receipt => receipt.Facts.FutureRevision.MappingId)
            .OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToArray();
        var seen = new List<Guid>();
        CatalogMappingCursor? mappingCursor = null;
        var pageCount = 0;
        do
        {
            Assert.InRange(++pageCount, 1, 3);
            var page = (await test.Reader.ListMappingsAsync(test.Actor, test.Project, 2, mappingCursor))!;
            Assert.Equal(test.Project, page.Access.Scope);
            Assert.InRange(page.Mappings.Count, 1, 2);
            seen.AddRange(page.Mappings.Select(row => row.Scope.MappingId));
            if (page.NextCursor is not null) Assert.Equal(new CatalogMappingCursor(test.Project, page.Mappings[^1].Scope.MappingId), page.NextCursor);
            mappingCursor = page.NextCursor;
        } while (mappingCursor is not null);
        Assert.Equal(expected, seen);

        var target = PostgresCatalogTestSupport.Target(committed[0]);
        var second = await test.CommitAppendAsync(committed[0], valid: false);
        var third = await test.CommitAppendAsync(committed[0]);
        var first = (await test.Reader.ListRevisionsAsync(test.Actor, target, 2))!;
        Assert.Equal(new long[] { 3, 2 }, first.Revisions.Select(row => row.Sequence));
        Assert.Equal(new CatalogRevisionCursor(target, 2), first.NextCursor);
        var newest = await test.CommitAppendAsync(committed[0]);
        var continuation = (await test.Reader.ListRevisionsAsync(test.Actor, target, 2, first.NextCursor))!;
        Assert.Equal(1, Assert.Single(continuation.Revisions).Sequence);
        Assert.Null(continuation.NextCursor);
        Assert.DoesNotContain(continuation.Revisions, row => row.Scope == newest.Facts.FutureRevision);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, (await test.Reader.ListRevisionsAsync(test.Actor, target, 100))!.Revisions.Select(row => row.Sequence));
        Assert.Equal(IngestionAdmissionClassification.EligibleDecodedInvalid,
            (await test.Reader.GetRevisionAsync(test.Actor, second.Facts.FutureRevision))!.Summary.Admission);
        Assert.Equal(3, (await test.Reader.GetRevisionAsync(test.Actor, third.Facts.FutureRevision))!.Summary.Sequence);
    }

    [PostgresFact]
    public async Task PositiveInt64VersionsHeadsAndHistoryCursorsRemainExactBeyondJavaScriptIntegerRange()
    {
        await using var test = await PostgresCatalogTestSupport.CreateAsync(fixture);
        var first = await test.CommitCreateAsync();
        var second = await test.CommitAppendAsync(first);
        var third = await test.CommitAppendAsync(first, valid: false);
        const long older = 9007199254740993;
        const long middle = 9007199254740995;
        var mapping = PostgresCatalogTestSupport.Target(first);
        // Owner-controlled synthetic edge facts in this disposable database. Restore
        // the exact guards atomically before a reader is admitted; no product clock,
        // trigger, or mutation hook is exposed by the catalog.
        await using (var connection = await test.Case.Owner.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var disable = new NpgsqlCommand("""
                ALTER TABLE braid.mappings DISABLE TRIGGER mappings_transition_guard;
                ALTER TABLE braid.mapping_revisions DISABLE TRIGGER mapping_revisions_transition_guard;
                """, connection, transaction))
                await disable.ExecuteNonQueryAsync();
            await using (var command = new NpgsqlCommand("""
                UPDATE braid.mapping_revisions SET sequence=CASE id WHEN $1 THEN $2 WHEN $3 THEN $4 ELSE $5 END
                  WHERE organization_id=$6 AND project_id=$7 AND mapping_id=$8;
                """, connection, transaction))
            {
                foreach (var value in new object[] { first.Facts.FutureRevision.RevisionId, older, second.Facts.FutureRevision.RevisionId, middle,
                    long.MaxValue, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId }) command.Parameters.Add(new NpgsqlParameter { Value = value });
                Assert.Equal(3, await command.ExecuteNonQueryAsync());
            }
            await using (var update = new NpgsqlCommand("UPDATE braid.mappings SET version=$1,head_sequence=$2 WHERE organization_id=$3 AND project_id=$4 AND id=$5", connection, transaction))
            {
                foreach (var value in new object[] { older, long.MaxValue, mapping.OrganizationId, mapping.ProjectId, mapping.MappingId })
                    update.Parameters.Add(new NpgsqlParameter { Value = value });
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }
            // Both sides of the accepted deferred head FK now agree. Execute its
            // pending checks before ALTER TABLE restores the transition triggers.
            await using (var check = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction))
                await check.ExecuteNonQueryAsync();
            await using (var enable = new NpgsqlCommand("""
                ALTER TABLE braid.mapping_revisions ENABLE TRIGGER mapping_revisions_transition_guard;
                ALTER TABLE braid.mappings ENABLE TRIGGER mappings_transition_guard;
                """, connection, transaction))
                await enable.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await test.Case.ExecuteAsync("UPDATE braid.projects SET version=$1 WHERE organization_id=$2 AND id=$3", long.MaxValue, test.Project.OrganizationId, test.Project.ProjectId);
        Assert.Equal(long.MaxValue, (await test.Reader.GetProjectAsync(test.Actor, test.Project))!.Summary.Version);
        var header = (await test.Reader.GetMappingAsync(test.Actor, mapping))!.Summary;
        Assert.Equal(older, header.Version);
        Assert.Equal(long.MaxValue, header.Head.Sequence);
        Assert.Equal(third.Facts.FutureRevision, header.Head.Scope);
        Assert.Equal(older, (await test.Reader.GetMappingMetadataAsync(test.Actor, mapping))!.Version);
        var page = (await test.Reader.ListRevisionsAsync(test.Actor, mapping, 1))!;
        Assert.Equal(long.MaxValue, Assert.Single(page.Revisions).Sequence);
        Assert.Equal(new CatalogRevisionCursor(mapping, long.MaxValue), page.NextCursor);
        var next = (await test.Reader.ListRevisionsAsync(test.Actor, mapping, 1, page.NextCursor))!;
        Assert.Equal(middle, Assert.Single(next.Revisions).Sequence);
        Assert.Equal(new CatalogRevisionCursor(mapping, middle), next.NextCursor);
        var last = (await test.Reader.ListRevisionsAsync(test.Actor, mapping, 1, next.NextCursor))!;
        Assert.Equal(older, Assert.Single(last.Revisions).Sequence);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task TypedCursorAndArgumentFailuresAreRejectedBeforeLeasingAnyConnection()
    {
        await using var database = new PostgresCatalogDatabase("Host=127.0.0.1;Port=1;Database=unused;Username=braid_catalog_reader;Timeout=1");
        ICatalogReader reader = new PostgresCatalogReader(database);
        var actor = new ActorContext(new PrincipalId(Guid.NewGuid()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var mapping = new MappingScope(project.OrganizationId, project.ProjectId, Guid.NewGuid());
        foreach (var limit in new[] { 0, 101 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListProjectsAsync(actor, project.OrganizationId, limit));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListMappingsAsync(actor, project, limit));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ListRevisionsAsync(actor, mapping, limit));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListProjectsAsync(actor, project.OrganizationId, 1, new(Guid.NewGuid(), Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListMappingsAsync(actor, project, 1, new(new(project.OrganizationId, Guid.NewGuid()), Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListMappingsAsync(actor, project, 1, new(new(Guid.NewGuid(), project.ProjectId), Guid.NewGuid())));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ListRevisionsAsync(actor, mapping, 1, new(new(mapping.OrganizationId, mapping.ProjectId, Guid.NewGuid()), 1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.GetOrganizationAdmissionAsync(null!, project.OrganizationId));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetOrganizationAdmissionAsync(actor, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.GetMappingAsync(actor, null!));
    }
}
