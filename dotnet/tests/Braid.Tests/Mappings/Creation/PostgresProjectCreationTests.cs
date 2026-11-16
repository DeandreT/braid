using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectCreationTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task ActualCreationCommitsExactlyOneProjectAdministratorMembershipAndImmutableFact()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var before = await test.CountsAsync();
        var key = Guid.NewGuid();
        var metadata = new ProjectCreationMetadata(" \u00a0Created 😀\u3000", " \r\nExact description\t ");
        var result = await test.CreateAsync(key, metadata);
        Assert.Equal(ProjectCreationOutcome.Created, result.Outcome);
        var receipt = Assert.IsType<ProjectCreationReceipt>(result.Receipt);
        Assert.NotEqual(Guid.Empty, receipt.Project.ProjectId);
        Assert.Equal(test.OrganizationId, receipt.Project.OrganizationId);
        Assert.Equal(test.Actor.PrincipalId, receipt.OriginalActor);
        Assert.Equal(key, receipt.IdempotencyKey);
        Assert.Equal(metadata, receipt.Metadata);
        Assert.Equal(TimeSpan.Zero, receipt.CreatedAtUtc.Offset);
        Assert.Equal(1, receipt.InitialProjectVersion);
        Assert.Equal(ProjectRole.Administrator, receipt.InitialMembershipRole);
        Assert.True(receipt.InitialMembershipActive);
        Assert.Equal(1, receipt.InitialMembershipVersion);
        Assert.Equal(ProjectCreationTestSupport.IndependentFingerprint(test.OrganizationId, test.Seed.Alice, key,
            metadata.Name, metadata.Description), receipt.Fingerprint.Digest.Value);
        Assert.Equal(before.Select(value => value + 1), await test.CountsAsync());
        Assert.True(await test.Case.ScalarAsync<bool>("""
            SELECT project.name=$3 AND project.description=$4 AND project.creator_principal_id=$5
              AND project.version=1 AND project.created_at=project.updated_at AND project.created_at=$6
              AND member.principal_id=$5 AND member.role='administrator' AND member.active AND member.version=1
            FROM braid.projects project JOIN braid.project_memberships member
              ON member.organization_id=project.organization_id AND member.project_id=project.id
            WHERE project.organization_id=$1 AND project.id=$2
            """, test.OrganizationId, receipt.Project.ProjectId, metadata.Name, metadata.Description,
            test.Seed.Alice, receipt.CreatedAtUtc));
        using var fact = JsonDocument.Parse(await test.CreationSnapshotAsync(key));
        Assert.Equal(13, fact.RootElement.EnumerateObject().Count());
        Assert.Equal(receipt.Fingerprint.Digest.Value, fact.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(1, fact.RootElement.GetProperty("fingerprint_version").GetInt32());
        await using var runtime = new PostgresDatabase(test.Case.RuntimeConnectionString);
        var access = await new PostgresProjectReader(runtime).GetAccessAsync(test.Actor, receipt.Project);
        Assert.Equal(ProjectRole.Administrator, access!.Role);
        await using var catalog = new PostgresCatalogDatabase(test.Case.CatalogConnectionString);
        var visible = await new PostgresCatalogReader(catalog).GetProjectAsync(test.Actor, receipt.Project);
        Assert.Equal(metadata.Name, visible!.Summary.Name);
        Assert.Equal(metadata.Description, visible.Summary.Description);
        Assert.Equal(1, visible.Summary.Version);
    }

    [PostgresFact]
    public async Task ReplayPreservesOriginalFactsAndNeverReactivatesPromotesOrRecreatesCurrentMembership()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var created = await test.CreateAsync(key);
        var receipt = created.Receipt!;
        var original = await test.CreationSnapshotAsync(key);
        await OwnerSetupAsync(test.Case,
            ("""
                UPDATE braid.projects SET name='Current mutable name',description='Current mutable description',
                  version=version+1,updated_at=clock_timestamp() WHERE organization_id=$1 AND id=$2
                """, [test.OrganizationId, receipt.Project.ProjectId]),
            ("""
                UPDATE braid.project_memberships SET role='viewer',active=false,version=version+1
                  WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
                """, [test.OrganizationId, receipt.Project.ProjectId, test.Seed.Alice]));
        var current = await test.Case.ScalarAsync<string[]>("""
            SELECT ARRAY[(SELECT to_jsonb(project)::text FROM braid.projects project WHERE organization_id=$1 AND id=$2),
              (SELECT to_jsonb(member)::text FROM braid.project_memberships member
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3)]
            """, test.OrganizationId, receipt.Project.ProjectId, test.Seed.Alice);
        var replay = await test.CreateAsync(key, new("\u3000Created project\u00a0", "Exact description"));
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        Assert.Equal(receipt, replay.Receipt);
        Assert.Equal(original, await test.CreationSnapshotAsync(key));
        Assert.Equal(current, await test.Case.ScalarAsync<string[]>("""
            SELECT ARRAY[(SELECT to_jsonb(project)::text FROM braid.projects project WHERE organization_id=$1 AND id=$2),
              (SELECT to_jsonb(member)::text FROM braid.project_memberships member
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3)]
            """, test.OrganizationId, receipt.Project.ProjectId, test.Seed.Alice));
        await using var runtime = new PostgresDatabase(test.Case.RuntimeConnectionString);
        var reader = new PostgresProjectReader(runtime);
        Assert.Null(await reader.GetAccessAsync(test.Actor, receipt.Project));
        await test.Case.ExecuteAsync("DELETE FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.OrganizationId, receipt.Project.ProjectId, test.Seed.Alice);
        var deletedCounts = await test.CountsAsync();
        var afterDelete = await test.CreateAsync(key);
        Assert.Equal(ProjectCreationOutcome.Replayed, afterDelete.Outcome);
        Assert.Equal(receipt, afterDelete.Receipt);
        Assert.Equal(original, await test.CreationSnapshotAsync(key));
        Assert.Equal(deletedCounts, await test.CountsAsync());
        Assert.Null(await reader.GetAccessAsync(test.Actor, receipt.Project));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>(
            "SELECT count(*) FROM braid.project_memberships WHERE organization_id=$1 AND project_id=$2", test.OrganizationId, receipt.Project.ProjectId));
    }

    [PostgresFact]
    public async Task SameOrganizationKeyConflictsAreOpaqueAndKeysRemainIndependentAcrossOrganizations()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var created = await test.CreateAsync(key);
        var original = await test.CreationSnapshotAsync(key);
        foreach (var metadata in new[]
        {
            new ProjectCreationMetadata("Changed name", "Exact description"),
            new ProjectCreationMetadata("Created project", "Exact description "),
            new ProjectCreationMetadata("created project", "Exact description")
        })
        {
            var conflict = await test.CreateAsync(key, metadata);
            Assert.Equal(ProjectCreationOutcome.IdempotencyConflict, conflict.Outcome);
            Assert.Null(conflict.Receipt);
        }
        await test.SetCapabilityAsync(true, test.Seed.Admitted);
        var other = await test.Creator.CreateAsync(new(new PrincipalId(test.Seed.Admitted)), test.OrganizationId, key,
            ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.IdempotencyConflict, other.Outcome);
        Assert.Null(other.Receipt);
        Assert.Equal(original, await test.CreationSnapshotAsync(key));
        await test.SetCapabilityAsync(true, organization: test.Seed.OrganizationB);
        var differentOrganization = await test.Creator.CreateAsync(test.Actor, test.Seed.OrganizationB, key,
            ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.Created, differentOrganization.Outcome);
        Assert.NotEqual(created.Receipt!.Project, differentOrganization.Receipt!.Project);
        Assert.Equal(test.Seed.OrganizationB, differentOrganization.Receipt.Project.OrganizationId);
        Assert.Equal(new[] { 5L, 5L, 2L }, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task FreshIdentityAdmissionAndCreationCapabilityAreRecheckedBeforeExistingKeyLookup()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        await test.CreateAsync(key);
        var before = await test.CountsAsync();
        var original = await test.CreationSnapshotAsync(key);
        foreach (var (revoke, restore, outcome, values) in new[]
        {
            ("UPDATE braid.principals SET active=false WHERE id=$1", "UPDATE braid.principals SET active=true WHERE id=$1", ProjectCreationOutcome.NotAdmitted, new object[] { test.Seed.Alice }),
            ("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE braid.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2", ProjectCreationOutcome.NotAdmitted, new object[] { test.OrganizationId, test.Seed.Alice }),
            ("UPDATE braid.organization_project_creation_capabilities SET can_create_projects=false WHERE organization_id=$1 AND principal_id=$2", "UPDATE braid.organization_project_creation_capabilities SET can_create_projects=true WHERE organization_id=$1 AND principal_id=$2", ProjectCreationOutcome.CapabilityDenied, new object[] { test.OrganizationId, test.Seed.Alice })
        })
        {
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                var admission = await test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId);
                if (outcome == ProjectCreationOutcome.NotAdmitted) Assert.Null(admission);
                else Assert.False(admission!.CanCreateProjects);
                var denied = await test.CreateAsync(key, new("Changed secret intent", ""));
                Assert.Equal(outcome, denied.Outcome);
                Assert.Null(denied.Receipt);
                Assert.Equal(before, await test.CountsAsync());
                Assert.Equal(original, await test.CreationSnapshotAsync(key));
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
        }
        await test.Case.ExecuteAsync("DELETE FROM braid.organization_project_creation_capabilities WHERE organization_id=$1 AND principal_id=$2",
            test.OrganizationId, test.Seed.Alice);
        try
        {
            Assert.False((await test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId))!.CanCreateProjects);
            var absentCapability = await test.CreateAsync(key, new("Changed secret intent", ""));
            Assert.Equal(ProjectCreationOutcome.CapabilityDenied, absentCapability.Outcome);
            Assert.Null(absentCapability.Receipt);
            Assert.Equal(before, await test.CountsAsync());
            Assert.Equal(original, await test.CreationSnapshotAsync(key));
        }
        finally { await test.SetCapabilityAsync(true); }
        var replay = await test.CreateAsync(key);
        Assert.Equal(ProjectCreationOutcome.Replayed, replay.Outcome);
        var missing = await test.Creator.CreateAsync(test.Actor, Guid.NewGuid(), Guid.NewGuid(), ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.NotAdmitted, missing.Outcome);
        Assert.Null(missing.Receipt);
        var unadmitted = await test.Creator.CreateAsync(new(new PrincipalId(test.Seed.Bob)), test.OrganizationId, key,
            ProjectCreationTestSupport.DefaultMetadata);
        Assert.Equal(ProjectCreationOutcome.NotAdmitted, unadmitted.Outcome);
        Assert.Null(unadmitted.Receipt);
    }

    [PostgresFact]
    public async Task OrganizationAdmissionAndEveryExistingProjectRoleDoNotImplicitlyGrantCreationCapability()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        var before = await test.CountsAsync();
        foreach (var role in new[] { "viewer", "runner", "publisher", "administrator" })
        {
            await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role=$1,version=version+1 WHERE organization_id=$2 AND project_id=$3 AND principal_id=$4",
                role, test.OrganizationId, test.Seed.ProjectA, test.Seed.Alice);
            var admission = await test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId);
            Assert.Equal(test.OrganizationId, admission!.OrganizationId);
            Assert.Equal(test.Actor.PrincipalId, admission.PrincipalId);
            Assert.False(admission.CanCreateProjects);
            var result = await test.CreateAsync();
            Assert.Equal(ProjectCreationOutcome.CapabilityDenied, result.Outcome);
            Assert.Null(result.Receipt);
        }
        Assert.Equal(before, await test.CountsAsync());
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.organization_project_creation_capabilities"));
        await test.SetCapabilityAsync(true);
        Assert.True((await test.Creator.GetAdmissionAsync(test.Actor, test.OrganizationId))!.CanCreateProjects);
        Assert.Equal(before, await test.CountsAsync());
    }

    [PostgresFact]
    public async Task ExplicitCapabilityProvisioningNeverCreatesOrReactivatesIdentityAdmissionOrProjectMembership()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture, grantCapability: false);
        var before = await test.CountsAsync();
        var identities = await IdentitySnapshotAsync(test.Case);
        await test.SetCapabilityAsync(true);
        Assert.Equal(identities, await IdentitySnapshotAsync(test.Case));
        Assert.Equal(before, await test.CountsAsync());
        await OwnerSetupAsync(test.Case,
            ("UPDATE braid.principals SET active=false WHERE id=$1", [test.Seed.Alice]),
            ("UPDATE braid.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                [test.OrganizationId, test.Seed.Alice]));
        var inactive = await IdentitySnapshotAsync(test.Case);
        await test.SetCapabilityAsync(false);
        Assert.False(await test.Case.ScalarAsync<bool>(
            "SELECT can_create_projects FROM braid.organization_project_creation_capabilities WHERE organization_id=$1 AND principal_id=$2",
            test.OrganizationId, test.Seed.Alice));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.SetCapabilityAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.SetCapabilityAsync(true, Guid.NewGuid()));
        Assert.Equal(inactive, await IdentitySnapshotAsync(test.Case));
        Assert.Equal(before, await test.CountsAsync());
        await using var wrongOwner = new OrganizationProjectCreationCapabilityProvisioner(test.Case.ProjectCreationConnectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongOwner.SetAsync(test.OrganizationId, test.Actor.PrincipalId, false));
    }

    [PostgresFact]
    public async Task ActualServerFingerprintMatchesIndependentAsymmetricUuidUtf8AndLengthFramingVectors()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var organization = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var actor = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
        var key = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
        await OwnerSetupAsync(test.Case,
            ("INSERT INTO braid.organizations(id,name) VALUES($1,'Fingerprint vectors')", [organization]),
            ("INSERT INTO braid.principals(id,active) VALUES($1,true)", [actor]),
            ("INSERT INTO braid.organization_memberships(organization_id,principal_id,active) VALUES($1,$2,true)", [organization, actor]));
        await test.SetCapabilityAsync(true, actor, organization);
        var vectors = new[]
        {
            (Name: "Project", Description: "Exact description\r\n", Digest: "c4677036d6500835f720dd50872c60cfdf52fb21ef03fb2c5e428258b2c5269e"),
            (Name: " \u00a0Café😀\u3000", Description: " \r\nCafe\u0301|😀\t ", Digest: "efd4f3c7127e7626fe5c660d6c8d0e4d1986ef2cd6a2a104764bac8bc2d0c970"),
            (Name: "ab", Description: "c", Digest: "8b3cb61b1c76c79f8f85bdd1702d6c66b59e5ed234da41a04f95227a0b9f45f8"),
            (Name: "a", Description: "bc", Digest: "66167ca5bef3862bd4d3f086eab6abd9910de12e7e839a02e9edc68d6f662283"),
            (Name: "é", Description: "", Digest: "44f1c63b46acd86ff45b5c178efc47daeb1eb1045ad8675e0759e75d299e8cb4"),
            (Name: "e\u0301", Description: "", Digest: "beb163ff8fc9cce54e93840d58ce7f0ca9a455257475bf2f98853fab7283b2dc")
        };
        foreach (var vector in vectors)
        {
            var metadata = new ProjectCreationMetadata(vector.Name, vector.Description);
            Assert.Equal(vector.Digest, ProjectCreationTestSupport.IndependentFingerprint(organization, actor, key, metadata.Name, metadata.Description));
            Assert.Equal(vector.Digest, await test.Case.ScalarAsync<string>(
                "SELECT braid.project_creation_fingerprint_v1($1,$2,$3,$4,$5)", organization, actor, key, metadata.Name, metadata.Description));
        }
        var created = await test.Creator.CreateAsync(new(new PrincipalId(actor)), organization, key,
            new(vectors[0].Name, vectors[0].Description));
        Assert.Equal(ProjectCreationOutcome.Created, created.Outcome);
        Assert.Equal(vectors[0].Digest, created.Receipt!.Fingerprint.Digest.Value);
        var maximum = new ProjectCreationMetadata(string.Concat(Enumerable.Repeat("😀", 200)), string.Concat(Enumerable.Repeat("😀", 4000)));
        var maxKey = Guid.NewGuid();
        var maxResult = await test.CreateAsync(maxKey, maximum);
        Assert.Equal(ProjectCreationOutcome.Created, maxResult.Outcome);
        Assert.Equal(maximum, maxResult.Receipt!.Metadata);
        Assert.Equal(ProjectCreationTestSupport.IndependentFingerprint(test.OrganizationId, test.Seed.Alice, maxKey,
            maximum.Name, maximum.Description), maxResult.Receipt.Fingerprint.Digest.Value);
    }

    [PostgresFact]
    public async Task DirectSqlRejectsEveryFrozenTrimBoundaryNullIdsAndOverboundTextWithoutPartialWrites()
    {
        await using var test = await ProjectCreationTestSupport.CreateAsync(fixture);
        var before = await test.CountsAsync();
        var backend = await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource);
        int[] trim = [0x0009,0x000a,0x000b,0x000c,0x000d,0x0020,0x0085,0x00a0,0x1680,
            0x2000,0x2001,0x2002,0x2003,0x2004,0x2005,0x2006,0x2007,0x2008,0x2009,0x200a,
            0x2028,0x2029,0x202f,0x205f,0x3000];
        foreach (var point in trim)
            await RejectAsync(test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (char)point + "Project" + (char)point, "");
        await RejectAsync(null, test.Seed.Alice, Guid.NewGuid(), "Project", "");
        await RejectAsync(test.OrganizationId, null, Guid.NewGuid(), "Project", "");
        await RejectAsync(test.OrganizationId, test.Seed.Alice, null, "Project", "");
        await RejectAsync(Guid.Empty, test.Seed.Alice, Guid.NewGuid(), "Project", "");
        foreach (var (organization, actor, key, name, description) in new[]
        {
            (test.OrganizationId, test.Seed.Alice, Guid.Empty, (string?)"Project", (string?)""),
            (test.OrganizationId, Guid.Empty, Guid.NewGuid(), (string?)"Project", (string?)""),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)null, (string?)""),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)"Project", (string?)null),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)"", (string?)""),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)new string('x',201), (string?)""),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)"Project", (string?)new string('x',4001)),
            (test.OrganizationId, test.Seed.Alice, Guid.NewGuid(), (string?)string.Concat(Enumerable.Repeat("😀",201)), (string?)"")
        }) await RejectAsync(organization, actor, key, name, description);
        Assert.Equal(before, await test.CountsAsync());
        Assert.Equal(backend, await ProjectCreationTestSupport.BackendAsync(test.Database.DataSource));
        var retained = new ProjectCreationMetadata("\u200bProject\ufeff", " \tExact\r\n ");
        var valid = await test.CreateAsync(metadata: retained);
        Assert.Equal(ProjectCreationOutcome.Created, valid.Outcome);
        Assert.Equal(retained, valid.Receipt!.Metadata);

        async Task RejectAsync(Guid? organization, Guid? actor, Guid? key, string? name, string? description)
        {
            await using var transaction = await test.Database.BeginTransactionAsync(test.OrganizationId);
            await using var command = transaction.CreateCommand("SELECT * FROM braid.create_project_v1($1,$2,$3,$4,$5)");
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)organization ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)actor ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)key ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)name ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)description ?? DBNull.Value });
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InvalidParameterValue, error.SqlState);
        }
    }

    private static async Task OwnerSetupAsync(PostgresCase test, params (string Sql, object[] Values)[] statements)
    {
        await using var connection = await test.Owner.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        foreach (var (sql, values) in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private static Task<string[]> IdentitySnapshotAsync(PostgresCase test) => test.ScalarAsync<string[]>("""
        SELECT ARRAY[(SELECT jsonb_agg(to_jsonb(principal) ORDER BY id)::text FROM braid.principals principal),
          (SELECT jsonb_agg(to_jsonb(member) ORDER BY organization_id,principal_id)::text FROM braid.organization_memberships member),
          (SELECT jsonb_agg(to_jsonb(member) ORDER BY organization_id,project_id,principal_id)::text FROM braid.project_memberships member)]
        """);
}
