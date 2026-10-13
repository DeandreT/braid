using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual metadata-read qualification; mapping setup uses explicitly synthetic C1 admission evidence.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestionWorkflowReaderTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task FullKeyNamespaceSeparatesKindTargetActorProjectAndOrganization()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mappingA = await test.CommitCreateAsync();
        var mappingB = await test.CommitCreateAsync();
        var targetA = Target(mappingA);
        var targetB = Target(mappingB);
        var key = Guid.NewGuid();
        var create = await test.ReserveAsync(key);
        var appendA = await test.ReserveAsync(key, PostgresIngestionTestSupport.AppendIntent(mappingA));
        var appendB = await test.ReserveAsync(key, PostgresIngestionTestSupport.AppendIntent(mappingB));
        var otherActor = new ActorContext(new PrincipalId(test.Seed.Admitted));
        var other = await ReserveAsync(test, otherActor, test.Project, key);
        var otherOrganization = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var foreign = await ReserveAsync(test, test.Actor, otherOrganization, key);
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA,
            test.Seed.Alice, "publisher");
        var otherProject = new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA);
        var hidden = await ReserveAsync(test, test.Actor, otherProject, key);

        Assert.Equal(create, (await reader.FindOwnReceiptAsync(test.Actor, test.Project,
            IngestionOperationKind.Create, null, key))!.Facts);
        Assert.Equal(appendA, (await reader.FindOwnReceiptAsync(test.Actor, test.Project,
            IngestionOperationKind.Append, targetA, key))!.Facts);
        Assert.Equal(appendB, (await reader.FindOwnReceiptAsync(test.Actor, test.Project,
            IngestionOperationKind.Append, targetB, key))!.Facts);
        Assert.Equal(other, (await reader.FindOwnReceiptAsync(otherActor, test.Project,
            IngestionOperationKind.Create, null, key))!.Facts);
        Assert.Equal(foreign, (await reader.FindOwnReceiptAsync(test.Actor, otherOrganization,
            IngestionOperationKind.Create, null, key))!.Facts);
        Assert.Equal(hidden, (await reader.FindOwnReceiptAsync(test.Actor, otherProject,
            IngestionOperationKind.Create, null, key))!.Facts);
        Assert.Equal(6, new[] { create.OperationId, appendA.OperationId, appendB.OperationId,
            other.OperationId, foreign.OperationId, hidden.OperationId }.Distinct().Count());
        Assert.Null(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Append,
            new MappingScope(test.Project.OrganizationId, test.Project.ProjectId, Guid.NewGuid()), key));
        Assert.Null(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create,
            null, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task LookupRestoresOriginalCommittedFactsAndEvidenceAfterProposedConfigurationChanges()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var key = Guid.NewGuid();
        var intent = PostgresIngestionTestSupport.CreateIntent(filename: "original.native.json");
        var originalProfile = new IngestionBackendProfile("ferrule", "synthetic.original.engine",
            "synthetic.original.worker", "synthetic.original.adapter");
        var originalPolicy = new IngestionAdmissionPolicy("weft.native-admission.v1", 512);
        var reserved = await test.Store.ReserveAsync(test.Actor, test.Project, key, intent,
            originalProfile, originalPolicy);
        var facts = reserved.Receipt!.Facts;
        var claim = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, claim);
        var committed = await test.Store.FinalizeAsync(test.Actor, claim);
        var replacementProfile = new IngestionBackendProfile("ferrule", "synthetic.replacement.engine",
            "synthetic.replacement.worker", "synthetic.replacement.adapter");
        var reducedPolicy = new IngestionAdmissionPolicy("weft.native-admission.v1", 1);
        Assert.Equal(IngestionStoreOutcome.Existing, (await test.Store.ReserveAsync(test.Actor, test.Project,
            key, intent, replacementProfile, reducedPolicy)).Outcome);

        IIngestionWorkflowReader reader = test.Store;
        var receipt = await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, key);
        Assert.NotNull(receipt);
        Assert.Equal(facts, receipt.Facts);
        Assert.Equal(originalProfile, receipt.Facts.BackendProfile);
        Assert.Equal(originalPolicy, receipt.Facts.AdmissionPolicy);
        Assert.Equal(IngestionPhase.Committed, receipt.Phase);
        Assert.Equal(committed.Receipt!.CommittedRevision, receipt.CommittedRevision);
        Assert.Equal(claim.Generation, receipt.EligibleAttemptGeneration);
        var attempt = Assert.Single(receipt.Attempts);
        Assert.Equal(IngestionAdmissionClassification.EligibleValid, attempt.Classification);
        var validation = attempt.Completion!.Evidence!;
        Assert.Equal(originalProfile, validation.Facts.Provenance.BackendProfile);
        Assert.Equal(facts.FutureRevision, validation.Facts.Provenance.Scope);
        Assert.Equal(facts.NativeObject.Digest, validation.Facts.Provenance.NativeDigest);
        Assert.Equal(originalProfile.BackendBuild,
            validation.CopyDocument().GetProperty("backendProfile").GetProperty("backendBuild").GetString());
        Assert.Equal(originalProfile.BackendId, (await reader.GetAppendTargetAsync(test.Actor, Target(receipt)))!.BackendId);
    }

    [PostgresFact]
    public async Task OtherPublishersCannotReadReceiptButExplicitProjectPublishersCanReadAppendTarget()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mapping = await test.CommitCreateAsync();
        var target = Target(mapping);
        var key = mapping.Facts.IdempotencyKey;
        var publisher = new ActorContext(new PrincipalId(test.Seed.Admitted));
        Assert.Null(await reader.FindOwnReceiptAsync(publisher, test.Project, IngestionOperationKind.Create, null, key));
        Assert.Equal(target, (await reader.GetAppendTargetAsync(publisher, target))!.Scope);
        await test.Case.ExecuteAsync("UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            test.Seed.OrganizationA, test.Seed.ProjectA, test.Seed.Admitted);
        Assert.Null(await reader.FindOwnReceiptAsync(publisher, test.Project, IngestionOperationKind.Create, null, key));
        Assert.NotNull(await reader.GetAppendTargetAsync(publisher, target));

        var otherProjectAdministrator = new ActorContext(new PrincipalId(test.Seed.OtherProjectAdmin));
        Assert.Null(await reader.FindOwnReceiptAsync(otherProjectAdministrator, test.Project,
            IngestionOperationKind.Create, null, key));
        Assert.Null(await reader.GetAppendTargetAsync(otherProjectAdministrator, target));
        await test.Case.ExecuteAsync("INSERT INTO weft.organization_memberships(organization_id,principal_id,active) VALUES ($1,$2,true)",
            test.Seed.OrganizationA, test.Seed.Bob);
        var admittedOnly = new ActorContext(new PrincipalId(test.Seed.Bob));
        Assert.Null(await reader.FindOwnReceiptAsync(admittedOnly, test.Project, IngestionOperationKind.Create, null, key));
        Assert.Null(await reader.GetAppendTargetAsync(admittedOnly, target));
    }

    [PostgresFact]
    public async Task EveryReadObservesFreshPrincipalOrganizationProjectAndPublisherAuthority()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mapping = await test.CommitCreateAsync();
        var target = Target(mapping);
        var key = mapping.Facts.IdempotencyKey;
        foreach (var (revoke, restore, values) in new[]
        {
            ("UPDATE weft.principals SET active=false WHERE id=$1",
                "UPDATE weft.principals SET active=true WHERE id=$1", new object[] { test.Seed.Alice }),
            ("UPDATE weft.organization_memberships SET active=false WHERE organization_id=$1 AND principal_id=$2",
                "UPDATE weft.organization_memberships SET active=true WHERE organization_id=$1 AND principal_id=$2",
                new object[] { test.Seed.OrganizationA, test.Seed.Alice }),
            ("UPDATE weft.project_memberships SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "UPDATE weft.project_memberships SET active=true,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                new object[] { test.Seed.OrganizationA, test.Seed.ProjectA, test.Seed.Alice }),
            ("UPDATE weft.project_memberships SET role='runner',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "UPDATE weft.project_memberships SET role='publisher',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                new object[] { test.Seed.OrganizationA, test.Seed.ProjectA, test.Seed.Alice }),
            ("UPDATE weft.project_memberships SET role='viewer',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                "UPDATE weft.project_memberships SET role='administrator',version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
                new object[] { test.Seed.OrganizationA, test.Seed.ProjectA, test.Seed.Alice })
        })
        {
            Assert.NotNull(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, key));
            Assert.NotNull(await reader.GetAppendTargetAsync(test.Actor, target));
            await test.Case.ExecuteAsync(revoke, values);
            try
            {
                Assert.Null(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, key));
                Assert.Null(await reader.GetAppendTargetAsync(test.Actor, target));
            }
            finally { await test.Case.ExecuteAsync(restore, values); }
            Assert.NotNull(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, key));
            Assert.NotNull(await reader.GetAppendTargetAsync(test.Actor, target));
        }
    }

    [PostgresFact]
    public async Task ForeignAncestorsAndMissingMappingAreHiddenEvenWhenActorHasOtherProjectAuthority()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mapping = await test.CommitCreateAsync();
        var target = Target(mapping);
        await test.Seed.AddMembershipAsync(test.Case, test.Seed.OrganizationA, test.Seed.HiddenProjectA,
            test.Seed.Alice, "publisher");
        foreach (var foreign in new[]
        {
            new MappingScope(test.Seed.OrganizationB, test.Seed.ProjectB, target.MappingId),
            new MappingScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA, target.MappingId),
            new MappingScope(test.Seed.OrganizationB, target.ProjectId, target.MappingId),
            new MappingScope(target.OrganizationId, target.ProjectId, Guid.NewGuid())
        })
            Assert.Null(await reader.GetAppendTargetAsync(test.Actor, foreign));
        foreach (var foreignProject in new[]
        {
            new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB),
            new ProjectScope(test.Seed.OrganizationA, test.Seed.HiddenProjectA),
            new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectA)
        })
            Assert.Null(await reader.FindOwnReceiptAsync(test.Actor, foreignProject, IngestionOperationKind.Create,
                null, mapping.Facts.IdempotencyKey));
        var actual = await reader.GetAppendTargetAsync(test.Actor, target);
        Assert.Equal(target, actual!.Scope);
        Assert.Equal(mapping.Facts.BackendProfile.BackendId, actual.BackendId);
    }

    [Fact]
    public async Task InvalidKeyNamespacesAreRejectedBeforeAnyDatabaseConnection()
    {
        await using var database = new PostgresIngestionDatabase("Host=127.0.0.1;Port=1;Database=unused;Username=weft_ingest;Timeout=1");
        IIngestionWorkflowReader reader = new PostgresRevisionIngestionStore(database);
        var actor = new ActorContext(new PrincipalId(Guid.NewGuid()));
        var project = new ProjectScope(Guid.NewGuid(), Guid.NewGuid());
        var target = new MappingScope(project.OrganizationId, project.ProjectId, Guid.NewGuid());
        var key = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.FindOwnReceiptAsync(actor, project,
            (IngestionOperationKind)0, null, key));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Create, target, key));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Append, null, key));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Append, new MappingScope(Guid.NewGuid(), project.ProjectId, target.MappingId), key));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Append, new MappingScope(project.OrganizationId, Guid.NewGuid(), target.MappingId), key));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindOwnReceiptAsync(actor, project,
            IngestionOperationKind.Create, null, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.FindOwnReceiptAsync(null!, project,
            IngestionOperationKind.Create, null, key));
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.GetAppendTargetAsync(actor, null!));
    }

    [PostgresFact]
    public async Task FoundMissingDeniedAndCanceledReadsReleaseTheOnlyPoolConnectionAndScope()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mapping = await test.CommitCreateAsync();
        var target = Target(mapping);
        var key = mapping.Facts.IdempotencyKey;
        var backend = await AssertUnboundAsync(test.Database);
        Assert.Equal(1, new NpgsqlConnectionStringBuilder(test.Database.DataSource.ConnectionString).MaxPoolSize);
        Assert.NotNull(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, key));
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        Assert.NotNull(await reader.GetAppendTargetAsync(test.Actor, target));
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        Assert.Null(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create, null, Guid.NewGuid()));
        Assert.Null(await reader.GetAppendTargetAsync(test.Actor,
            new MappingScope(target.OrganizationId, target.ProjectId, Guid.NewGuid())));
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        var other = new ActorContext(new PrincipalId(test.Seed.OtherProjectAdmin));
        Assert.Null(await reader.FindOwnReceiptAsync(other, test.Project, IngestionOperationKind.Create, null, key));
        Assert.Null(await reader.GetAppendTargetAsync(other, target));
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        await using (var occupied = await test.Database.BeginTransactionAsync(test.Project.OrganizationId))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.FindOwnReceiptAsync(test.Actor,
                test.Project, IngestionOperationKind.Create, null, key, cancellation.Token));
            using var secondCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAppendTargetAsync(test.Actor,
                target, secondCancellation.Token));
            await occupied.CommitAsync();
        }
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        var foreignProject = new ProjectScope(test.Seed.OrganizationB, test.Seed.ProjectB);
        var foreign = await ReserveAsync(test, test.Actor, foreignProject, key);
        Assert.Equal(foreign, (await reader.FindOwnReceiptAsync(test.Actor, foreignProject,
            IngestionOperationKind.Create, null, key))!.Facts);
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
    }

    [PostgresFact]
    public async Task DatabaseLockFailuresStayCategoricalAndRollbackBeforeTheNextRead()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        IIngestionWorkflowReader reader = test.Store;
        var mapping = await test.CommitCreateAsync();
        var target = Target(mapping);
        var backend = await AssertUnboundAsync(test.Database);
        await using (var owner = await test.Case.Owner.OpenConnectionAsync())
        await using (var blocked = await owner.BeginTransactionAsync())
        {
            await using var gate = new NpgsqlCommand("LOCK TABLE weft.mappings IN ACCESS EXCLUSIVE MODE", owner, blocked);
            await gate.ExecuteNonQueryAsync();
            var lookupError = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.FindOwnReceiptAsync(test.Actor,
                test.Project, IngestionOperationKind.Create, null, mapping.Facts.IdempotencyKey));
            var targetError = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAppendTargetAsync(test.Actor, target));
            foreach (var error in new[] { lookupError, targetError })
            {
                Assert.Equal("Ingestion metadata persistence failed (55P03).", error.Message);
                Assert.Null(error.InnerException);
            }
            await blocked.RollbackAsync();
        }
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
        Assert.NotNull(await reader.FindOwnReceiptAsync(test.Actor, test.Project, IngestionOperationKind.Create,
            null, mapping.Facts.IdempotencyKey));
        Assert.NotNull(await reader.GetAppendTargetAsync(test.Actor, target));
        Assert.Equal(backend, await AssertUnboundAsync(test.Database));
    }

    private static MappingScope Target(IngestionReceipt receipt) => new(receipt.Facts.FutureRevision.OrganizationId,
        receipt.Facts.FutureRevision.ProjectId, receipt.Facts.FutureRevision.MappingId);

    private static async Task<ReservedIngestionFacts> ReserveAsync(PostgresIngestionTestSupport test,
        ActorContext actor, ProjectScope project, Guid key)
    {
        var result = await test.Store.ReserveAsync(actor, project, key, PostgresIngestionTestSupport.CreateIntent(),
            PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
        Assert.Equal(IngestionStoreOutcome.Reserved, result.Outcome);
        return result.Receipt!.Facts;
    }

    private static async Task<int> AssertUnboundAsync(PostgresIngestionDatabase database)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await database.DataSource.OpenConnectionAsync(deadline.Token);
        await using var command = new NpgsqlCommand("""
            SELECT pg_backend_pid(), NULLIF(current_setting('weft.organization_id',true),''),
                current_setting('lock_timeout'), current_setting('statement_timeout')
            """, connection);
        await using var result = await command.ExecuteReaderAsync(deadline.Token);
        Assert.True(await result.ReadAsync(deadline.Token));
        Assert.True(result.IsDBNull(1));
        Assert.Equal("0", result.GetString(2));
        Assert.Equal("0", result.GetString(3));
        return result.GetInt32(0);
    }
}
