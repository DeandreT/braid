using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresMappingMetadataWriterTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task OnlyFreshExactProjectPublishersAndAdministratorsEditWhileEveryRoleRetainsItsReadPermission()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var immutable = await test.ImmutableSnapshotAsync();
        foreach (var (sqlRole, role, allowed) in new[]
        {
            ("viewer", ProjectRole.Viewer, false), ("runner", ProjectRole.Runner, false),
            ("publisher", ProjectRole.Publisher, true), ("administrator", ProjectRole.Administrator, true)
        })
        {
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                sqlRole, test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice);
            var access = (await test.Reader.GetMappingAsync(test.Actor, test.Scope))!.Access;
            Assert.Equal(role, access.Role);
            Assert.True(access.Can(ProjectAction.ReadMetadata));
            Assert.Equal(allowed, access.Can(ProjectAction.EditMetadata));
            var current = await test.HeaderAsync();
            var before = await test.RowSnapshotAsync();
            var result = await test.ReplaceAsync(current.Version, "edited by " + sqlRole, "fresh project role");
            Assert.Equal(allowed ? MappingMetadataWriteOutcome.Updated : MappingMetadataWriteOutcome.Unavailable, result.Outcome);
            if (allowed) Assert.Equal(current.Version + 1, result.Metadata!.Version);
            else { Assert.Null(result.Metadata); Assert.Equal(before, await test.RowSnapshotAsync()); }
        }
        foreach (var principal in new[] { test.Seed.Admitted, test.Seed.OtherProjectAdmin, test.Seed.Bob, Guid.NewGuid() })
        {
            var before = await test.RowSnapshotAsync();
            var result = await test.Writer.ReplaceAsync(new(new PrincipalId(principal)), test.Scope,
                new(test.Scope, 1), new("denied replacement", "no admission fallback"));
            Assert.Equal(MappingMetadataWriteOutcome.Unavailable, result.Outcome);
            Assert.Null(result.Metadata);
            Assert.Equal(before, await test.RowSnapshotAsync());
        }
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    [PostgresFact]
    public async Task FullAncestorAndExpectedVersionScopeAreComparedAfterFreshTargetAuthority()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA, test.Seed.Alice, "publisher");
        var before = await test.RowSnapshotAsync();
        foreach (var wrong in new[]
        {
            new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectB, test.Scope.MappingId),
            new MappingScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA, test.Scope.MappingId),
            new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectA, test.Scope.MappingId),
            new MappingScope(test.Scope.OrganizationId, test.Scope.ProjectId, Guid.NewGuid())
        })
        {
            var missing = await test.Writer.ReplaceAsync(test.Actor, wrong, new(wrong, 1), new("foreign mapping", "hidden"));
            Assert.Equal(MappingMetadataWriteOutcome.Unavailable, missing.Outcome);
            Assert.Null(missing.Metadata);
            var mismatch = await test.Writer.ReplaceAsync(test.Actor, test.Scope, new(wrong, 1), new("visible target", "wrong expected scope"));
            Assert.Equal(MappingMetadataWriteOutcome.PreconditionFailed, mismatch.Outcome);
            Assert.Null(mismatch.Metadata);
        }
        Assert.Equal(before, await test.RowSnapshotAsync());
        var other = new ActorContext(new PrincipalId(test.Seed.OtherProjectAdmin));
        var unavailable = await test.Writer.ReplaceAsync(other, test.Scope,
            new(new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectB, Guid.NewGuid()), long.MaxValue), new("not authorized", "must not disclose version"));
        Assert.Equal(MappingMetadataWriteOutcome.Unavailable, unavailable.Outcome);
        Assert.Null(unavailable.Metadata);
    }

    [PostgresFact]
    public async Task RevocationCommittedBetweenPreviousReadAndConditionalStatementDeniesEveryNextWrite()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        foreach (var (revoke, restore, values) in new[]
        {
            ("UPDATE braid.principals SET active=false WHERE id=$1", "UPDATE braid.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }),
            ("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", new object[] { test.Project.OrganizationId, test.Seed.Alice }),
            ("UPDATE braid.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE braid.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice }),
            ("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", "UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3", new object[] { test.Project.OrganizationId, test.Project.ProjectId, test.Seed.Alice })
        })
        {
            var prior = (await test.Reader.GetMappingMetadataAsync(test.Actor, test.Scope))!;
            var before = await test.RowSnapshotAsync();
            var gate = new RevisionIngestionGate();
            var writer = new PostgresMetadataWriterGate(test.Writer, gate);
            var operation = writer.ReplaceAsync(test.Actor, test.Scope, new(test.Scope, prior.Version), new("revoked edit", "previous reads grant no authority"));
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await test.Case.ExecuteAsync(revoke, values);
                try
                {
                    gate.Release();
                    var denied = await operation.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(MappingMetadataWriteOutcome.Unavailable, denied.Outcome);
                    Assert.Null(denied.Metadata);
                    Assert.Equal(before, await test.RowSnapshotAsync());
                }
                finally { await test.Case.ExecuteAsync(restore, values); }
            }
            finally { gate.Release(); await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
            var recovered = await test.ReplaceAsync(prior.Version, prior.Name, prior.Description);
            Assert.Equal(MappingMetadataWriteOutcome.Unchanged, recovered.Outcome);
        }
    }

    [PostgresFact]
    public async Task AcceptedLiteralAndMaximumUnicodeMetadataChangeExactlyOnceWithoutTouchingHeadOrHistory()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var before = await test.HeaderAsync();
        var immutable = await test.ImmutableSnapshotAsync();
        const string name = "literal '); DROP TABLE braid.mappings; -- Ω";
        const string description = "preserve newline\n $1 \\ a/b~c 🧵";
        var edited = await test.ReplaceAsync(before.Version, " \t" + name + "\r\n", description);
        Assert.Equal(MappingMetadataWriteOutcome.Updated, edited.Outcome);
        Assert.Equal(new CatalogMappingMetadata(test.Scope, name, description, 2), edited.Metadata);
        var current = await test.HeaderAsync();
        PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(before, current);
        Assert.Equal(before.Head, current.Head);
        Assert.True(current.UpdatedAtUtc >= before.UpdatedAtUtc);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
        var maximumName = string.Concat(Enumerable.Repeat("🧵", 200));
        var maximumDescription = string.Concat(Enumerable.Repeat("🧵", 4000));
        var maximum = await test.ReplaceAsync(2, " \t" + maximumName + "\r\n", maximumDescription);
        Assert.Equal(MappingMetadataWriteOutcome.Updated, maximum.Outcome);
        Assert.Equal(new CatalogMappingMetadata(test.Scope, maximumName, maximumDescription, 3), maximum.Metadata);
        Assert.Equal(immutable, await test.ImmutableSnapshotAsync());
    }

    [PostgresFact]
    public async Task CheckedNormalizedNoOpPreservesEveryColumnAndStaleNoOpStillFails()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        var original = await test.HeaderAsync();
        var before = await test.RowSnapshotAsync();
        var unchanged = await test.ReplaceAsync(original.Version, " \t" + original.Name + "\r\n", original.Description);
        Assert.Equal(MappingMetadataWriteOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(original.Version, unchanged.Metadata!.Version);
        Assert.Equal(before, await test.RowSnapshotAsync());
        var changed = await test.ReplaceAsync(original.Version, "one accepted change", "once");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, changed.Outcome);
        var changedRow = await test.RowSnapshotAsync();
        var staleNoOp = await test.ReplaceAsync(original.Version, changed.Metadata!.Name, changed.Metadata.Description);
        Assert.Equal(MappingMetadataWriteOutcome.PreconditionFailed, staleNoOp.Outcome);
        Assert.Null(staleNoOp.Metadata);
        Assert.Equal(changedRow, await test.RowSnapshotAsync());
        var currentNoOp = await test.ReplaceAsync(changed.Metadata.Version, changed.Metadata.Name, changed.Metadata.Description);
        Assert.Equal(MappingMetadataWriteOutcome.Unchanged, currentNoOp.Outcome);
        Assert.Equal(changed.Metadata, currentNoOp.Metadata);
        Assert.Equal(changedRow, await test.RowSnapshotAsync());
    }

    [PostgresFact]
    public async Task EditRetainsTheLatestLockedTimestampWhenSyntheticExistingDataIsAheadOfDatabaseTime()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        // Owned edge data only; neither the database clock nor an ingestion lease is changed.
        await test.SetSyntheticVersionAsync(1, aheadTimestamp: true);
        var original = await test.HeaderAsync();
        var result = await test.ReplaceAsync(1, "changed while timestamp is ahead", "greatest protects the existing timestamp");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, result.Outcome);
        var current = await test.HeaderAsync();
        Assert.Equal(original.UpdatedAtUtc, current.UpdatedAtUtc);
        Assert.Equal(2, current.Version);
        Assert.Equal(original.Head, current.Head);
        PostgresMappingMetadataTestSupport.AssertIdentityUnchanged(original, current);
    }

    [PostgresFact]
    public async Task ExactInt64MetadataVersionExhaustionOccursOnlyForAnAuthorizedMatchingActualChange()
    {
        await using var test = await PostgresMappingMetadataTestSupport.CreateAsync(fixture);
        const long large = 9007199254740993;
        await test.SetSyntheticVersionAsync(large);
        var advanced = await test.ReplaceAsync(large, "exact large version", "above JavaScript integer range");
        Assert.Equal(MappingMetadataWriteOutcome.Updated, advanced.Outcome);
        Assert.Equal(large + 1, advanced.Metadata!.Version);
        await test.SetSyntheticVersionAsync(long.MaxValue);
        var max = await test.HeaderAsync();
        var before = await test.RowSnapshotAsync();
        var unchanged = await test.ReplaceAsync(long.MaxValue, max.Name, max.Description);
        Assert.Equal(MappingMetadataWriteOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(long.MaxValue, unchanged.Metadata!.Version);
        Assert.Equal(before, await test.RowSnapshotAsync());
        var stale = await test.ReplaceAsync(long.MaxValue - 1, "changed", "stale before exhaustion");
        Assert.Equal(MappingMetadataWriteOutcome.PreconditionFailed, stale.Outcome);
        Assert.Null(stale.Metadata);
        var exhausted = await test.ReplaceAsync(long.MaxValue, "changed", "cannot overflow");
        Assert.Equal(MappingMetadataWriteOutcome.VersionExhausted, exhausted.Outcome);
        Assert.Null(exhausted.Metadata);
        var denied = await test.Writer.ReplaceAsync(new(new PrincipalId(test.Seed.Admitted)), test.Scope,
            new(test.Scope, long.MaxValue), new("changed", "denial precedes exhaustion"));
        Assert.Equal(MappingMetadataWriteOutcome.Unavailable, denied.Outcome);
        Assert.Null(denied.Metadata);
        Assert.Equal(before, await test.RowSnapshotAsync());
    }

    [Fact]
    public async Task NullWriterInputsFailBeforeAnyDatabaseLease()
    {
        await using var database = new PostgresMappingMetadataDatabase("Host=127.0.0.1;Port=1;Database=unused;Username=braid_catalog_editor;Timeout=1");
        IMappingMetadataWriter writer = new PostgresMappingMetadataWriter(database);
        var actor = new ActorContext(new PrincipalId(Guid.NewGuid()));
        var scope = new MappingScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var expected = new MappingMetadataVersion(scope, 1);
        var edit = new MappingMetadataEdit("valid", "");
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.ReplaceAsync(null!, scope, expected, edit));
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.ReplaceAsync(actor, null!, expected, edit));
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.ReplaceAsync(actor, scope, null!, edit));
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.ReplaceAsync(actor, scope, expected, null!));
    }
}
