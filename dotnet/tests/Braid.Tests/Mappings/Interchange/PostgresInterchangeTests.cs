using Braid.Application;
using Braid.Domain;
using Braid.Persistence.Postgres;

namespace Braid.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresInterchangeTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task MaximumPackageCrossesReceiptQueryOnceWithZeroOrEightAttempts()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var package = new InterchangePackage("design.mfd", Enumerable.Range(0, 4)
            .Select(index => new InterchangeFile(index == 0 ? "design.mfd" : $"asset-{index}.json",
                new byte[InterchangePackage.MaximumFileBytes])));
        var digest = Sha256Digest.FromBytes(PostgresIngestionTestSupport.SyntheticBody);
        var provenance = new InterchangeImportProvenance(package, digest,
            Sha256Digest.FromBytes("review"u8), Sha256Digest.FromBytes("report"u8), PostgresIngestionTestSupport.Profile);
        var intent = IngestionIntent.ForCreate(new("Maximum package", ""), "ferrule", "design.mfd",
            digest, PostgresIngestionTestSupport.SyntheticBody.Length, provenance);
        var facts = await test.ReserveAsync(intent: intent);
        var encodedBytes = provenance.Encode().LongLength;
        Assert.InRange(encodedBytes, 21L * 1024 * 1024, 24L * 1024 * 1024);
        await AssertQueryBytes(1);
        for (var generation = 1; generation <= 8; generation++)
        {
            var claim = await test.ClaimAsync(facts);
            Assert.Equal(generation, claim.Generation);
            await test.Store.CompleteAttemptAsync(test.Actor, claim,
                IngestionAttemptCompletion.FromOperationalFailure(IngestionFailureCode.NeedsSource));
        }
        await AssertQueryBytes(8);
        var restored = await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId);
        Assert.Equal(provenance, restored!.Facts.Intent.ImportProvenance);
        Assert.Equal(8, restored.Attempts.Count);
        var replay = await test.Store.FindOwnReceiptAsync(test.Actor, test.Project,
            IngestionOperationKind.Create, null, facts.IdempotencyKey);
        Assert.Equal(provenance, replay!.Facts.Intent.ImportProvenance);
        Assert.Equal(8, replay.Attempts.Count);

        async Task AssertQueryBytes(int expectedRows)
        {
            await using var transaction = await test.Database.BeginTransactionAsync(test.Project.OrganizationId);
            // Execute the exact production result projection, rather than counting
            // decoder calls: every non-null bytea is transferred even if not decoded.
            await using var command = transaction.CreateCommand(PostgresRevisionIngestionStore.ReceiptQuery(
                "AND ingestion.operation_id = @operationId"));
            command.Parameters.AddWithValue("organizationId", test.Project.OrganizationId);
            command.Parameters.AddWithValue("projectId", test.Project.ProjectId);
            command.Parameters.AddWithValue("actorId", test.Actor.PrincipalId.Value);
            command.Parameters.AddWithValue("operationId", facts.OperationId);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = 0;
            long transferredPackageBytes = 0;
            while (await reader.ReadAsync())
            {
                var column = reader.GetOrdinal("import_provenance");
                if (!reader.IsDBNull(column))
                {
                    Assert.Equal(0, rows);
                    transferredPackageBytes += reader.GetBytes(column, 0, null, 0, 0);
                }
                rows++;
            }
            Assert.Equal(expectedRows, rows);
            Assert.Equal(encodedBytes, transferredPackageBytes);
        }
    }

    [PostgresFact]
    public async Task MissingOriginalProvenanceCannotBecomeANativeOnlyReceipt()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var review = Review(test);
        var provenance = new InterchangeImportProvenance(review.Package, review.Conversion.NativeDigest,
            review.Digest, review.Conversion.Report.Digest, review.Conversion.Profile);
        var facts = await test.ReserveAsync(intent: IngestionIntent.ForCreate(new("Imported", ""), "ferrule",
            "design.mfd", review.Conversion.NativeDigest, review.Conversion.Native.Length, provenance));
        await test.Case.ExecuteAsync("""
            ALTER TABLE braid.revision_import_provenance DISABLE TRIGGER USER;
            DELETE FROM braid.revision_import_provenance;
            ALTER TABLE braid.revision_import_provenance ENABLE TRIGGER USER;
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId));
    }

    [PostgresFact]
    public async Task DurableReviewRoundTripsAcrossStoreInstancesAndRechecksOriginalActorAuthority()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var review = Review(test);
        await test.Store.SaveAsync(test.Actor, review);
        await using var otherDatabase = new PostgresIngestionDatabase(test.Case.IngestionConnectionString);
        var other = new PostgresRevisionIngestionStore(otherDatabase);
        var restored = await other.FindAsync(test.Actor, test.Project, review.Id);
        Assert.NotNull(restored);
        Assert.Equal(review.Digest, restored.Digest);
        Assert.Equal(review.Package.Encode(), restored.Package.Encode());
        Assert.Equal(review.Conversion.Native.ToArray(), restored.Conversion.Native.ToArray());
        Assert.Null(await other.FindAsync(new(new PrincipalId(test.Seed.Admitted)), test.Project, review.Id));
        await test.Case.ExecuteAsync("UPDATE braid.project_memberships SET role='viewer',version=version+1 WHERE principal_id=$1", test.Seed.Alice);
        Assert.Null(await other.FindAsync(test.Actor, test.Project, review.Id));
    }

    [PostgresFact]
    public async Task OriginalPackageIsReservedAtomicallyAndPublishedThroughTheSameRevisionOperation()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        var review = Review(test);
        var provenance = new InterchangeImportProvenance(review.Package, review.Conversion.NativeDigest,
            review.Digest, review.Conversion.Report.Digest, review.Conversion.Profile);
        var intent = IngestionIntent.ForCreate(new("Imported mapping", ""), "ferrule", "design.mfd",
            review.Conversion.NativeDigest, review.Conversion.Native.Length, provenance);
        var facts = await test.ReserveAsync(intent: intent);
        Assert.Equal(provenance, facts.Intent.ImportProvenance);
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.mapping_revisions"));
        var claim = await test.ClaimAsync(facts);
        await test.ReadyAsync(facts, claim);
        Assert.Equal(IngestionPhase.Committed, (await test.Store.FinalizeAsync(test.Actor, claim)).Receipt!.Phase);
        Assert.Equal(1L, await test.Case.ScalarAsync<long>("""
            SELECT count(*) FROM braid.mapping_revisions r JOIN braid.revision_import_provenance p
              ON p.organization_id=r.organization_id AND p.project_id=r.project_id
             AND p.operation_id=r.ingestion_operation_id
            WHERE r.id=$1
            """, facts.FutureRevision.RevisionId));
        Assert.Equal(provenance.Encode(), await test.Case.ScalarAsync<byte[]>(
            "SELECT provenance FROM braid.revision_import_provenance WHERE operation_id=$1", facts.OperationId));
        Assert.Equal(provenance, (await test.Store.GetReceiptAsync(test.Actor, test.Project, facts.OperationId))!.Facts.Intent.ImportProvenance);
        // Even the owner cannot accidentally update or remove retained original provenance.
        await Assert.ThrowsAnyAsync<Exception>(() => test.Case.ExecuteAsync(
            "DELETE FROM braid.revision_import_provenance WHERE operation_id=$1", facts.OperationId));
    }

    [PostgresFact]
    public async Task UnrecognizedProvenanceTriggerRefusesReservationWithoutWritingRows()
    {
        await using var test = await PostgresIngestionTestSupport.CreateAsync(fixture);
        await test.Case.ExecuteAsync("""
            CREATE FUNCTION braid.test_refuse_import() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test refusal'; END $$;
            CREATE TRIGGER test_refuse_import BEFORE INSERT ON braid.revision_import_provenance
              FOR EACH ROW EXECUTE FUNCTION braid.test_refuse_import();
            """);
        var review = Review(test);
        var provenance = new InterchangeImportProvenance(review.Package, review.Conversion.NativeDigest,
            review.Digest, review.Conversion.Report.Digest, review.Conversion.Profile);
        var intent = IngestionIntent.ForCreate(new("Imported", ""), "ferrule", "design.mfd",
            review.Conversion.NativeDigest, review.Conversion.Native.Length, provenance);
        await Assert.ThrowsAnyAsync<Exception>(() => test.ReserveAsync(intent: intent));
        Assert.Equal(0L, await test.Case.ScalarAsync<long>("SELECT count(*) FROM braid.revision_ingestions"));
    }

    [PostgresFact]
    public Task ExistingVersion24DatabaseUpgradesWithoutRewritingItsJournal() => UpgradeWithoutRewritingJournal(24);

    [PostgresFact]
    public Task ExistingAuditVersion25DatabaseUpgradesWithoutRewritingItsJournal() => UpgradeWithoutRewritingJournal(25);

    [PostgresFact]
    public Task ExistingDisplayVersion26DatabaseUpgradesWithoutRewritingItsJournal() => UpgradeWithoutRewritingJournal(26);

    [PostgresFact]
    public Task ExistingAdministrationVersion27DatabaseUpgradesWithoutRewritingItsJournal() => UpgradeWithoutRewritingJournal(27);

    private async Task UpgradeWithoutRewritingJournal(int previousVersion)
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(2).ToArray()))
            await old.MigrateAsync();
        var seed = await PostgresSeedData.CreateAsync(test);
        await test.ExecuteAsync("UPDATE braid.project_memberships SET role='publisher',version=version+1 WHERE principal_id=$1", seed.Alice);
        var actor = new ActorContext(new(seed.Alice));
        var original = await HistoricalIngestionSeed.CommitCreateAsync(test, actor, new(seed.OrganizationA, seed.ProjectA));
        await using (var old = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(previousVersion).ToArray()))
            await old.MigrateAsync();
        var nativeBefore = await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM braid.mapping_revisions r");
        var before = await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m");
        await test.MigrateAsync();
        await test.MigrateAsync();
        Assert.Equal(before, await test.ScalarAsync<string>("SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text FROM braid.schema_migrations m WHERE version<=$1", previousVersion));
        Assert.Equal(47L, await test.ScalarAsync<long>("SELECT count(*) FROM braid.schema_migrations"));
        Assert.Equal(nativeBefore, await test.ScalarAsync<string>("SELECT to_jsonb(r)::text FROM braid.mapping_revisions r"));
        await using var database = new PostgresIngestionDatabase(test.IngestionConnectionString);
        var restored = await new PostgresRevisionIngestionStore(database).GetReceiptAsync(actor, original.Facts.Project, original.Facts.OperationId);
        Assert.Equal(original.Facts.Fingerprint, restored!.Facts.Fingerprint);
        Assert.Null(restored.Facts.Intent.ImportProvenance);
    }

    private static ImportReview Review(PostgresIngestionTestSupport test)
    {
        var package = new InterchangePackage("design.mfd", [new("design.mfd", "<self-authored/>"u8)]);
        return new ImportReview(Guid.NewGuid(), test.Actor.PrincipalId,
            ImportDestination.Create(test.Project, "ferrule", new("Imported", "")), package,
            new(PostgresIngestionTestSupport.Profile, package.Digest, PostgresIngestionTestSupport.SyntheticBody,
                new("Synthetic conversion", [new("unsupported", "Synthetic warning")])), DateTimeOffset.UtcNow.AddHours(1));
    }
}
