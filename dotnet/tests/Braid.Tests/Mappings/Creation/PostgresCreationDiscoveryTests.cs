using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresCreationDiscoveryTests(PostgresFixture fixture)
{
    // These tests exercise the real restricted adapter and owner functions once reviewed 0046 is integrated.
    // No replacement SQL, schema-admission bypass or fake database connection is used.
    public sealed class DiscoveryPostgresTheoryAttribute : TheoryAttribute
    {
        public DiscoveryPostgresTheoryAttribute()
        {
            if (MigrationRunner.EmbeddedMigrations.Count < 46)
                Skip = "Pending reviewed 0046 discovery functions and exact schema admission integration.";
            else if (!PostgresFixture.IsConfigured)
                Skip = "Set BRAID_POSTGRES_IMAGE or BRAID_POSTGRES_BIN to execute actual database gates.";
        }
    }

    [DiscoveryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proposal_discovery_survives_inactive_or_relocated_source_store_but_not_content_revocation(bool relocated)
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var actor = test.Actor; var project = test.Project;
        await test.Case.ExecuteAsync("INSERT INTO braid.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1)",
            project.OrganizationId, project.ProjectId, actor.PrincipalId.Value);
        var binding = new SpecStoreBinding(Guid.NewGuid(), SpecStoreKind.FileSystem, Sha256Digest.FromBytes("synthetic-store-location"u8));
        await test.Case.ExecuteAsync("INSERT INTO braid.spec_stores VALUES($1,'filesystem',$2,true)", binding.StoreId, binding.LocationDigest.Value);
        await using var writerDb = new PostgresSpecBundleDatabase(test.Case.RegressionConnectionString("braid_spec_writer"), SpecDatabaseProfile.Writer);
        await using var readerDb = new PostgresSpecBundleDatabase(test.Case.RegressionConnectionString("braid_spec_reader"), SpecDatabaseProfile.Reader);
        var writer = writerDb.ForActor(actor, binding);
        var bundle = new SpecBundleScope(project, Guid.NewGuid());
        await writer.CreateAsync(bundle, Guid.NewGuid(), default);
        var source = new SpecCapturedSource(new(bundle, Guid.NewGuid(), 1), SpecSourceFormat.Text, "Synthetic field mapping"u8);
        var original = new SpecObjectReference(new(source.Identity, SpecObjectKind.Original, Guid.NewGuid()), source.Digest, source.ByteCount);
        var originalReservation = await writer.ReserveAsync(original, source.Format, Guid.NewGuid(), 1, null, default);
        await writer.ExecutePublicationAsync(originalReservation.Claim, _ => Task.FromResult(SpecPublishResult.Created), default);
        var evidence = SpecSourceExtractor.Extract(source);
        var extraction = new SpecObjectReference(new(source.Identity, SpecObjectKind.Extraction, Guid.NewGuid()), evidence.Identity.EvidenceDigest, evidence.CopyEvidenceBytes().Length);
        var version = (await writer.InventoryAsync(bundle, default)).Bundle.Version;
        var extractionReservation = await writer.ReserveAsync(extraction, source.Format, Guid.NewGuid(), version, evidence.Identity, default);
        await writer.ExecutePublicationAsync(extractionReservation.Claim, _ => Task.FromResult(SpecPublishResult.Created), default);
        var specification = new CreationSpecification(bundle, [source], [evidence]);
        var plan = new MappingCreationPlan([new("a", CreationScalarType.Text, true)], [new("b", CreationScalarType.Text, true)], [new("a", "b")]);
        var id = Guid.NewGuid();
        var compilation = new CreationCompilation(project, id, plan.CompilerDigest, specification.Snapshot.Digest, "synthetic-engine", "synthetic-worker",
            PostgresIngestionTestSupport.SyntheticBody, "{}"u8, "{}"u8);
        var proposal = new MappingCreationProposal(actor.PrincipalId, id, Guid.NewGuid(), specification, plan,
            new([new(source.Identity, 0)], [], []), compilation, new(new("Synthetic mapping", "Fixture"), "ferrule"));
        await using var proposals = new PostgresMappingCreationStore(test.Case.RegressionConnectionString("braid_creation_reader"),
            test.Case.RegressionConnectionString("braid_creation_writer"), test.Case.RegressionConnectionString("braid_creation_approver"));
        Assert.NotNull(await proposals.RetainAsync(actor, proposal, default));
        var discovery = new PostgresCreationDiscovery(readerDb, binding);
        Assert.Equal(extraction.Id.ObjectId, Assert.Single(await discovery.SelectionsAsync(actor, project, null, null, 20, default)).ExtractionId);
        Assert.Equal(proposal.Fingerprint, Assert.Single(await discovery.ProposalsAsync(actor, project, null, 20, default)).Fingerprint);

        if (relocated)
            await test.Case.ExecuteAsync("UPDATE braid.spec_stores SET location_digest=$2 WHERE id=$1", binding.StoreId, Sha256Digest.FromBytes("relocated-synthetic-store"u8).Value);
        else
            await test.Case.ExecuteAsync("UPDATE braid.spec_stores SET active=false WHERE id=$1", binding.StoreId);

        var found = Assert.Single(await discovery.ProposalsAsync(actor, project, null, 20, default));
        Assert.Equal(proposal.Id, found.ProposalId); Assert.Equal(proposal.Fingerprint, found.Fingerprint);
        Assert.Equal(proposal.Fingerprint, (await proposals.ReadAsync(actor, project, found.ProposalId, default))!.Proposal.Fingerprint);
        var selectionDenied = await Assert.ThrowsAsync<SpecStorageException>(() => discovery.SelectionsAsync(actor, project, null, null, 20, default));
        Assert.Equal(SpecStorageError.NotAdmitted, selectionDenied.Error);

        await test.Case.ExecuteAsync("UPDATE braid.project_payload_read_grants SET active=false,version=version+1 WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3",
            project.OrganizationId, project.ProjectId, actor.PrincipalId.Value);
        var denied = await Assert.ThrowsAsync<SpecStorageException>(() => discovery.ProposalsAsync(actor, project, null, 20, default));
        Assert.Equal(SpecStorageError.NotAdmitted, denied.Error);
        Assert.Null(await proposals.ReadAsync(actor, project, proposal.Id, default));
    }
}
