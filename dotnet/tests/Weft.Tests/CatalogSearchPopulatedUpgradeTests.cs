using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Weft.Application;
using Weft.Contracts.Operations;
using Weft.Contracts.Projections;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual populated PostgreSQL upgrades; report and native admission facts are synthetic, with no worker or private store.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogSearchPopulatedUpgradeTests(PostgresFixture fixture)
{
    [RegressionPostgresTheory]
    [InlineData(27)]
    [InlineData(28)]
    public async Task PopulatedPrefixPreservesSourceAuthorityReviewsAndRetainedReports(int prefix)
    {
        var keys = new List<ProjectionCacheKey>();
        await using var data = await PostgresRevisionDerivationTestSupport.CreateHistoricalAsync(fixture, prefix,
            async (database, actor, firstRevision, secondRevision) =>
            {
                keys.Add(await PostgresServerSessionUpgradeTests.SeedHistoricalReportAsync(database, actor, firstRevision));
                keys.Add(await PostgresServerSessionUpgradeTests.SeedHistoricalReportAsync(database, actor, secondRevision));
            });
        var test = data.Case;
        var binding = Guid.NewGuid();
        await test.ExecuteAsync("""
            INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id)
            VALUES($1,'test-provider','https://identity.example/upgrade','retained-upgrade-user',$2)
            """, binding, data.Actor.PrincipalId.Value);
        await using var display = new PostgresAccountDisplayMetadata(test.AccountDisplayConnectionString);
        await display.ObserveAsync(data.Project.OrganizationId, binding, "Retained upgrade user", DateTimeOffset.UtcNow, default);
        await OrganizationAdministratorBootstrap.BootstrapAsync(test.MigrationConnectionString,
            data.Project.OrganizationId, data.Actor.PrincipalId.Value, Guid.Parse("11111111-1111-4111-8111-111111111111"));
        await using var administration = new PostgresAdministrationStore(test.RegressionConnectionString("weft_administration"));
        var organization = (await administration.ReadOrganizationAsync(data.Actor, data.Project.OrganizationId, default))!;
        Assert.Equal("updated", await administration.WriteAsync(data.Actor, data.Project.OrganizationId, null, organization.Version,
            AdministrationOperation.Invite, Guid.NewGuid(), JsonSerializer.SerializeToElement(new
            {
                tenantId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                expectedObjectId = Guid.NewGuid(),
                expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                projectRoles = Array.Empty<object>(),
                secretHash = OrganizationInvitationSecret.Hash(OrganizationInvitationSecret.Create())
            }), default));
        await using var sessions = new PostgresServerSessionDatabase(test.SessionWriterConnectionString, ServerSessionDatabaseProfile.Writer);
        var sessionKey = new ServerSessionKey(data.Project.OrganizationId, Sha256Digest.FromBytes(RandomNumberGenerator.GetBytes(32)));
        var deadline = new DateTimeOffset(await test.ScalarAsync<DateTime>("SELECT statement_timestamp()+interval '2 hours'"));
        var session = await new PostgresServerSessionWriter(sessions).CreateOnlyAsync(new(sessionKey, binding, 1,
            data.Actor.PrincipalId, Guid.NewGuid(), deadline, new byte[] { 11, 29, 47 }));
        Assert.Equal(ServerSessionWriteOutcome.Created, session.Outcome);

        ImportReview? import = null;
        ExportReview? export = null;
        IngestionReceipt? imported = null;
        if (prefix == 28)
        {
            var package = new InterchangePackage("design.mfd", [new("design.mfd", "<self-authored/>"u8), new("schema.json", "{}"u8)]);
            import = new ImportReview(Guid.NewGuid(), data.Actor.PrincipalId,
                ImportDestination.Create(data.Project, "ferrule", new("Imported upgrade mapping", "Original package retained")), package,
                new(PostgresIngestionTestSupport.Profile, package.Digest, PostgresIngestionTestSupport.SyntheticBody,
                    new("Synthetic conversion", [])), DateTimeOffset.UtcNow.AddHours(1));
            await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
            var store = new PostgresRevisionIngestionStore(ingestion);
            await store.SaveAsync(data.Actor, import);
            var intent = IngestionIntent.ForCreate(import.Destination.Metadata!, "ferrule", "design.mfd",
                import.Conversion.NativeDigest, import.Conversion.Native.Length, import.Revision.Provenance);
            var reserved = await store.ReserveAsync(data.Actor, data.Project, Guid.NewGuid(), intent,
                PostgresIngestionTestSupport.Profile, PostgresIngestionTestSupport.Policy);
            Assert.Equal(IngestionStoreOutcome.Reserved, reserved.Outcome);
            var facts = reserved.Receipt!.Facts;
            var claim = (await store.ClaimAsync(data.Actor, data.Project, facts.OperationId, Guid.NewGuid())).Claim!;
            Assert.Equal(IngestionStoreOutcome.Applied, (await store.MarkStagedAsync(data.Actor, claim, facts.NativeObject)).Outcome);
            Assert.Equal(IngestionPhase.Ready, (await store.CompleteAttemptAsync(data.Actor, claim,
                PostgresIngestionTestSupport.SyntheticCompletion(facts))).Receipt!.Phase);
            imported = (await store.FinalizeAsync(data.Actor, claim)).Receipt!;
            Assert.Equal(IngestionPhase.Committed, imported.Phase);
            export = new ExportReview(Guid.NewGuid(), data.Actor.PrincipalId, data.Revision, data.Source.NativeObject.Digest,
                "ferrule", new(PostgresIngestionTestSupport.Profile, data.Source.NativeObject.Digest, package,
                    new("Synthetic export", [])), DateTimeOffset.UtcNow.AddHours(1));
            await store.SaveAsync(data.Actor, export);
        }

        Assert.Equal((long)prefix, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.Equal(2L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.organization_invitations"));
        Assert.Equal(1L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.server_sessions"));
        if (prefix == 28)
            foreach (var table in InterchangeSchemaTestContract.Tables)
                Assert.Equal(1L, await test.ScalarAsync<long>($"SELECT count(*) FROM ONLY weft.{table}"));
        var before = await Snapshot.CaptureAsync(test, prefix);
        await CatalogSearchUpgradeTestSupport.MigrateSearchAsync(test);
        Assert.Equal(29L, await test.ScalarAsync<long>("SELECT count(*) FROM weft.schema_migrations"));
        Assert.True(await CatalogSearchUpgradeTestSupport.AdmitAsync(test));
        await using (var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString))
        await using (var admission = await ingestion.BeginTransactionAsync(data.Project.OrganizationId)) { }
        await before.AssertPreservedAsync(test, prefix);
        Assert.Equal("Retained upgrade user", await display.ReadAsync(data.Project.OrganizationId, binding, default));
        await using var sessionReader = new PostgresServerSessionDatabase(test.SessionReaderConnectionString, ServerSessionDatabaseProfile.Reader);
        Assert.Equal(new byte[] { 11, 29, 47 }, (await new PostgresServerSessionReader(sessionReader).ObserveAsync(sessionKey))!.Facts.CopyProtectedTicket());
        Assert.NotNull(await administration.ReadOrganizationAsync(data.Actor, data.Project.OrganizationId, default));
        await test.ExecuteAsync("ALTER INDEX weft.projects_search_trgm RENAME TO projects_search_trgm_unexpected");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.ReadOrganizationAsync(data.Actor, data.Project.OrganizationId, default));
        }
        finally { await test.ExecuteAsync("ALTER INDEX weft.projects_search_trgm_unexpected RENAME TO projects_search_trgm"); }
        await test.ExecuteAsync("CREATE INDEX upgrade_unexpected ON weft.projects(name)");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.ReadOrganizationAsync(data.Actor, data.Project.OrganizationId, default));
        }
        finally { await test.ExecuteAsync("DROP INDEX weft.upgrade_unexpected"); }

        await using var catalogDatabase = new PostgresCatalogDatabase(test.CatalogConnectionString);
        var catalog = new PostgresCatalogReader(catalogDatabase);
        Assert.Equal(data.SourceReceipt.Facts.NativeObject.Digest, (await catalog.GetRevisionAsync(data.Actor, data.Revision))!.Summary.NativeDigest);
        var cold = new PostgresCatalogSearch(catalog);
        await Assert.ThrowsAsync<CatalogSearchIndexPendingException>(() => cold.SearchAsync(data.Actor, data.Project.OrganizationId, new("RetainedUpgradeField"), 100));
        var first = Report((await data.Reader.GetCurrentAsync(data.Actor, keys[0]))!.SelectedResult!);
        var second = Report((await data.Reader.GetCurrentAsync(data.Actor, keys[1]))!.SelectedResult!);
        var reports = new PostgresCatalogSearchReportTests.RetainedReader([first, second]);
        var search = new PostgresCatalogSearch(catalog, () => reports, () => data.DerivationDatabase);
        var result = (await search.SearchAsync(data.Actor, data.Project.OrganizationId, new("RetainedUpgradeField"), 100))!;
        Assert.Equal(2, result.Hits.Count);
        Assert.Equal(new[] { first.Metadata.DerivationId, second.Metadata.DerivationId }.Order(),
            result.Hits.Select(hit => hit.Key.ProjectionDerivationId!.Value).Distinct().Order());
        Assert.Equal(keys.Select(key => key.Scope.RevisionId).Order(), result.Hits.Select(hit => hit.Key.RevisionId!.Value).Order());
        Assert.Equal(2, reports.Reads);
        var paged = new List<CatalogSearchKey>();
        CatalogSearchCursor? cursor = null;
        do
        {
            var page = (await search.SearchAsync(data.Actor, data.Project.OrganizationId, new("RetainedUpgradeField"), 1, cursor))!;
            paged.AddRange(page.Hits.Select(hit => hit.Key));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(result.Hits.Select(hit => hit.Key), paged);
        Assert.Equal(2, reports.Reads);
        if (import is not null && export is not null && imported is not null)
        {
            await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
            var store = new PostgresRevisionIngestionStore(ingestion);
            Assert.Equal(import.Package.Encode(), (await store.FindAsync(data.Actor, data.Project, import.Id))!.Package.Encode());
            Assert.Equal(export.Digest, (await store.FindExportAsync(data.Actor, data.Revision, export.Id))!.Digest);
            Assert.Equal(imported.Facts.Intent.ImportProvenance, (await store.GetReceiptAsync(data.Actor, data.Project, imported.Facts.OperationId))!.Facts.Intent.ImportProvenance);
        }
        await CatalogSearchUpgradeTestSupport.MigrateSearchAsync(test);
        await before.AssertPreservedAsync(test, prefix);
    }

    private static VerifiedProjectionReport Report(RevisionReportResult metadata)
    {
        var key = (ProjectionCacheKey)metadata.Key;
        var scope = key.Scope;
        var wireScope = new ScopedRevisionId(scope.OrganizationId.ToString("D"), scope.ProjectId.ToString("D"), scope.MappingId.ToString("D"), scope.RevisionId.ToString("D"));
        var digest = new ArtifactDigest("sha256", key.NativeDigest.Value);
        var backend = new BackendIdentity(key.Profile.BackendId, key.Profile.BackendRevision, key.Profile.BackendBuild);
        var locator = new JsonPointerNativeLocator(key.Source.LogicalDocument, "/output");
        var report = new ProjectionReportV1(OperationStatusV1.Succeeded,
            new(wireScope, digest, backend, key.Profile.AdapterBuild, true),
            new(new(wireScope, digest, backend, key.Profile.AdapterBuild, key.AlgorithmVersion), new(CoverageStatus.Complete, []),
                [new("output", "Orders", BoundaryRole.PrimaryOutput, "json", BoundaryShape.Object, null, locator)],
                [new("field", "output", [new FieldPathSegment("RetainedUpgradeField")], "RetainedUpgradeField", new(FieldDataTypeKind.Scalar, [ScalarDataType.String]),
                    new(CardinalityKind.ExactlyOne), FieldRequirement.Required, FieldNullability.NonNullable, locator)], [], [], [],
                [new("member", RelationshipKind.BoundaryMember, new FieldNodeReference("field"), new BoundaryNodeReference("output"))], [], []), []);
        return VerifiedProjectionReport.Create(metadata, report, new());
    }

    private sealed record Snapshot(string[] Tables, string[] Functions, IReadOnlyDictionary<string, byte[]> Digests)
    {
        internal static async Task<Snapshot> CaptureAsync(PostgresCase test, int prefix)
        {
            var tables = await test.ScalarAsync<string[]>("SELECT array_agg(relname::text ORDER BY relname) FROM pg_catalog.pg_class WHERE relnamespace='weft'::regnamespace AND relkind IN ('r','p')");
            var functions = await test.ScalarAsync<string[]>("SELECT array_agg(oid::regprocedure::text ORDER BY oid::regprocedure::text) FROM pg_catalog.pg_proc WHERE pronamespace='weft'::regnamespace");
            return new(tables, functions, await ObserveAsync(test, prefix, tables, functions, false));
        }

        internal async Task AssertPreservedAsync(PostgresCase test, int prefix)
        {
            var current = await ObserveAsync(test, prefix, Tables, Functions, true);
            foreach (var (category, digest) in Digests)
                Assert.True(CryptographicOperations.FixedTimeEquals(digest, current[category]), $"Search upgrade changed existing {category}.");
        }

        private static async Task<IReadOnlyDictionary<string, byte[]>> ObserveAsync(PostgresCase test, int prefix,
            string[] tables, string[] functions, bool upgraded)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (var table in tables)
                result.Add("rows:" + table, Digest(await test.ScalarAsync<string>($"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM ONLY weft.{table} r" +
                    (table == "schema_migrations" ? $" WHERE version<={prefix}" : ""))));
            var schema = await test.ScalarAsync<string>("""
                WITH relations AS (SELECT * FROM pg_catalog.pg_class WHERE relnamespace='weft'::regnamespace AND relname=ANY($1::text[])),
                attributes AS (SELECT r.relname,a.* FROM relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0 AND NOT a.attisdropped)
                SELECT jsonb_build_object(
                  'relations',(SELECT jsonb_agg(jsonb_build_array(oid,relname,relkind,relowner,relacl,relrowsecurity,relforcerowsecurity,
                    relnatts,reloptions,relreplident,relpersistence,relispartition,reltype,relam,relhasrules) ORDER BY relname) FROM relations),
                  'columns',(SELECT jsonb_agg(to_jsonb(a) ORDER BY relname,attnum) FROM attributes a),
                  'constraints',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.oid) FROM pg_catalog.pg_constraint c WHERE c.conrelid IN (SELECT oid FROM relations)),
                  'indexes',(SELECT jsonb_agg(jsonb_build_array(to_jsonb(i),c.relname,c.relowner,c.relam,c.reloptions) ORDER BY i.indexrelid)
                    FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid=i.indexrelid WHERE i.indrelid IN (SELECT oid FROM relations) AND NOT c.relname=ANY($3::text[])),
                  'policies',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid) FROM pg_catalog.pg_policy p WHERE p.polrelid IN (SELECT oid FROM relations)),
                  'triggers',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.oid) FROM pg_catalog.pg_trigger t WHERE t.tgrelid IN (SELECT oid FROM relations)
                    AND NOT ($4 AND t.tgisinternal AND EXISTS(SELECT 1 FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class child ON child.oid=c.conrelid
                      WHERE c.oid=t.tgconstraint AND c.contype='f' AND child.relname=ANY($5::text[])))),
                  'functions',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid) FROM pg_catalog.pg_proc p WHERE p.oid=ANY(ARRAY(SELECT value::regprocedure::oid FROM unnest($2::text[]) value))),
                  'roles',(SELECT jsonb_agg(to_jsonb(r) ORDER BY rolname) FROM pg_catalog.pg_roles r WHERE rolname LIKE 'weft_%'),
                  'membership',(SELECT jsonb_agg(to_jsonb(m) ORDER BY roleid,member,grantor) FROM pg_catalog.pg_auth_members m),
                  'schemaAcl',(SELECT jsonb_agg(jsonb_build_array(nspname,nspowner,nspacl) ORDER BY nspname)
                    FROM pg_catalog.pg_namespace WHERE nspname='weft'),
                  'sequences',(SELECT jsonb_agg(to_jsonb(s) ORDER BY sequencename) FROM pg_catalog.pg_sequences s WHERE schemaname='weft'),
                  'defaults',(SELECT jsonb_agg(to_jsonb(a) ORDER BY oid) FROM pg_catalog.pg_default_acl a),
                  'settings',(SELECT jsonb_agg(to_jsonb(s) ORDER BY setdatabase,setrole) FROM pg_catalog.pg_db_role_setting s))::text
                """, tables, functions, upgraded ? CatalogSearchSchemaAdmission.IndexNames : [],
                upgraded, prefix == 27
                    ? InterchangeSchemaTestContract.Tables.Append("catalog_search_report_index").ToArray()
                    : new[] { "catalog_search_report_index" });
            result.Add("schema and authority", Digest(schema));
            return result;
        }

        private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }
}
