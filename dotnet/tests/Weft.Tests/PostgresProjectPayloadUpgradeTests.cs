using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>Actual migration conservation with synthetic metadata; no payload, native worker or private object I/O.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresProjectPayloadUpgradeTests(PostgresFixture fixture)
{
    private const int PrefixVersion = 8;

    [PostgresFact]
    public async Task FreshAndRepeatedPayloadMigrationPreserveEveryJournalAndAcknowledgedGrantFact()
    {
        await using var test = await fixture.CreateCaseAsync();
        try
        {
            await AssertCurrentHistoryAsync(test);
            var seed = await PostgresSeedData.CreateAsync(test);
            await test.ExecuteAsync("""
                INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version)
                VALUES($1,$2,$3,true,1)
                """, seed.OrganizationA, seed.ProjectA, seed.Alice);
            await test.ExecuteAsync("""
                UPDATE weft.project_payload_read_grants SET active=false,version=2
                WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
                """, seed.OrganizationA, seed.ProjectA, seed.Alice);
            await using var connection = OpenUnpooled(test.MigrationConnectionString);
            await connection.OpenAsync();
            var journal = await JournalDigestAsync(connection);
            var rows = await RowDigestsAsync(connection, includePayload: true);
            await test.MigrateAsync();
            await test.MigrateAsync();
            await AssertCurrentHistoryAsync(test);
            SameDigest(journal, await JournalDigestAsync(connection), "Repeated migration changed the acknowledged migration journal.");
            SameRows(rows, await RowDigestsAsync(connection, includePayload: true));
            Assert.True(await test.ScalarAsync<bool>("""
                SELECT count(*)=1 AND bool_and(NOT active AND version=2) FROM weft.project_payload_read_grants
                """), "Repeated migration changed the acknowledged inactive payload grant.");
        }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The fresh payload migration conservation operation failed."); }
    }

    [PostgresFact]
    public async Task ConcurrentUpgradeFromGenuinelyPopulatedEightPreservesOldRowsDefinitionsAndAuthority()
    {
        // The new cluster role is genuinely absent while the eight-script source
        // installation and its old metadata are populated and observed.
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionPayloadPermissionReader: false);
        await isolated.InitializeAsync();
        try
        {
            await using var catalog = await PostgresCatalogTestSupport.CreateAsync(isolated, installCatalog: false);
            var test = catalog.Case;
            var original = await catalog.CommitCreateAsync(filename: "historical-payload-upgrade.native.json");
            await catalog.CommitAppendAsync(original, valid: false);
            await MigratePrefixAsync(test, 6);
            await SeedHistoricalReportAsync(test, catalog.Actor, original);
            await MigratePrefixAsync(test, PrefixVersion);
            await SeedIdentityAndCapabilityFactsAsync(test, catalog.Seed);
            await AssertPrefixHistoryAsync(test);
            Assert.True(await test.ScalarAsync<bool>("""
                SELECT (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname='weft_payload_permission_reader')=0
                  AND pg_catalog.to_regclass('weft.project_payload_read_grants') IS NULL
                  AND (SELECT count(*) FROM weft.mappings)>0
                  AND (SELECT count(*) FROM weft.mapping_revisions)>=2
                  AND (SELECT count(*) FROM weft.revision_ingestions WHERE phase='committed')>=2
                  AND (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable)>0
                  AND (SELECT count(*) FROM weft.external_identity_bindings)=2
                  AND (SELECT count(*) FROM weft.external_identity_bindings WHERE NOT enabled)=1
                  AND (SELECT count(*) FROM weft.organization_project_creation_capabilities)>0
                """), "The historical installation was not genuinely populated before payload upgrade.");
            await using var connection = OpenUnpooled(test.MigrationConnectionString);
            await connection.OpenAsync();
            var source = await PostgresProjectPayloadUpgradeConservation.CaptureEightAsync(connection);
            var rows = await RowDigestsAsync(connection, includePayload: false);
            var journal = await JournalDigestAsync(connection, PrefixVersion);

            // Provision only the separate new login after the historical snapshot.
            // The normal migration still owns all schema and column grants.
            var password = Guid.NewGuid().ToString("N");
            Assert.True(Guid.TryParseExact(test.Name["weft_case_".Length..], "N", out _),
                "The isolated migration database name is not fixture-owned.");
            await test.BootstrapAsync($"""
                CREATE ROLE weft_payload_permission_reader LOGIN PASSWORD '{password}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT CONNECT ON DATABASE {test.Name} TO weft_payload_permission_reader;
                """);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                await using var runner = new MigrationRunner(test.MigrationConnectionString,
                    MigrationRunner.EmbeddedMigrations.Take(PrefixVersion + 1).ToArray());
                await runner.MigrateAsync();
            }));
            await AssertPayloadHistoryAsync(test);
            await source.AssertPayloadUpgradePreservedAsync(connection);
            SameDigest(journal, await JournalDigestAsync(connection, PrefixVersion),
                "Payload upgrade changed an old migration journal fact.");
            SameRows(rows, await RowDigestsAsync(connection, includePayload: false));
            Assert.True(await test.ScalarAsync<long>("SELECT count(*) FROM weft.project_payload_read_grants") == 0,
                "Payload migration inferred a grant from existing authority or identity facts.");
            var payloadJournal = await JournalDigestAsync(connection);
            await MigratePrefixAsync(test, PrefixVersion + 1);
            await MigratePrefixAsync(test, PrefixVersion + 1);
            SameDigest(payloadJournal, await JournalDigestAsync(connection), "Repeated upgrade changed the payload-prefix migration journal.");
            await source.AssertPayloadUpgradePreservedAsync(connection);
            SameRows(rows, await RowDigestsAsync(connection, includePayload: false));

            // Finish the strict original eight-to-nine conservation before any
            // later supplement. Current readers require the full embedded history.
            await test.MigrateAsync();
            await AssertCurrentHistoryAsync(test);

            // Existing runtime and identity profiles must still admit the same
            // current facts after the additive migration; neither grants payload.
            await using var runtime = new PostgresDatabase(test.RuntimeConnectionString);
            var visible = await new PostgresProjectReader(runtime).GetVisibleProjectAsync(catalog.Actor, catalog.Project);
            Assert.True(visible is not null && visible.Scope == catalog.Project,
                "Payload upgrade changed ordinary project visibility.");
            await using var identity = new PostgresExternalIdentityDatabase(test.IdentityConnectionString);
            var resolved = await new PostgresExternalIdentityReader(identity).ResolveAsync(
                PostgresExternalIdentityTestSupport.Key("payload-upgrade-active"));
            Assert.True(resolved.Outcome == ExternalIdentityResolutionOutcome.Resolved
                && resolved.Binding is { Enabled: true } binding
                && binding.PrincipalId == new PrincipalId(catalog.Seed.Alice),
                "Payload upgrade changed an existing external identity binding.");
            SameRows(rows, await RowDigestsAsync(connection, includePayload: false));
        }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The historical payload upgrade conservation operation failed."); }
        finally { await isolated.DisposeAsync(); }
    }

    private static async Task SeedIdentityAndCapabilityFactsAsync(PostgresCase test, PostgresSeedData seed)
    {
        await test.ExecuteAsync("""
            INSERT INTO weft.organization_project_creation_capabilities(organization_id,principal_id,can_create_projects)
            VALUES($1,$2,true),($1,$3,false)
            """, seed.OrganizationA, seed.Admitted, seed.Alice);
        foreach (var subject in new[] { "payload-upgrade-active", "payload-upgrade-disabled" })
            await test.ExecuteAsync("""
                INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id)
                VALUES($1,'test-provider','https://identity.example/Exact Tenant',$2,$3)
                """, Guid.NewGuid(), subject, seed.Alice);
        await test.ExecuteAsync("""
            UPDATE weft.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp()
            WHERE subject='payload-upgrade-disabled'
            """);
    }

    private static async Task SeedHistoricalReportAsync(PostgresCase test, ActorContext actor, IngestionReceipt receipt)
    {
        // This is explicitly synthetic metadata authored through genuine0006
        // constraints and transitions. No private report or native bytes exist.
        var facts = receipt.Facts;
        var source = new RevisionReportSource(facts.FutureRevision, facts.NativeObject,
            facts.Intent.LogicalDocument, facts.Intent.DependencyIdentity, facts.BackendProfile.BackendId);
        var key = new ProjectionCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
            "weft.mapping-projection.v1", "synthetic.payload-upgrade.v1", "synthetic.report.codec.v1", new());
        await using var connection = OpenUnpooled(test.MigrationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivations
              (organization_id,project_id,mapping_id,revision_id,operation_kind,native_object_id,native_class,
               native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,
               engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,
               policy_schema_version,policy_json,policy_fingerprint,native_byte_limit,worker_response_byte_limit,
               item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,
               validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    $22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34) RETURNING cache_entry_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(insert, key.Scope);
        object[] values = ["projection", key.Source.NativeObject.Id.ObjectId, "native", key.NativeDigest.Value,
            key.Source.NativeObject.ByteCount, key.Source.LogicalDocument, key.Source.DependencyIdentity,
            key.BackendId, key.BackendRevision, key.BackendBuild, key.AdapterBuild, key.SchemaVersion,
            key.AlgorithmVersion, key.CodecVersion, key.Policy.SchemaVersion,
            Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()), key.Policy.Fingerprint.Value,
            key.Policy.NativeByteLimit, key.Policy.WorkerResponseByteLimit, key.Policy.ItemLimit,
            key.Policy.ResponseItemLimit, key.Policy.JsonDepthLimit, key.Policy.DetailDepthLimit,
            key.Policy.DiagnosticLimit, key.Policy.ValidationIssueLimit, key.Policy.TextByteLimit,
            key.Policy.HeaderByteLimit, key.Policy.StoredObjectByteLimit, key.Policy.ResponseByteLimit, key.Fingerprint.Value];
        foreach (var value in values) insert.Parameters.AddWithValue(value);
        var cacheId = Assert.IsType<Guid>(await insert.ExecuteScalarAsync());
        await using var claim = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivation_attempts
              (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,claimant_instance_id)
            VALUES ($1,$2,$3,$4,$5,1,$6,$7) RETURNING derivation_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(claim, key.Scope);
        claim.Parameters.AddWithValue(cacheId);
        claim.Parameters.AddWithValue(actor.PrincipalId.Value);
        claim.Parameters.AddWithValue(Guid.NewGuid());
        var derivation = Assert.IsType<Guid>(await claim.ExecuteScalarAsync());
        await using (var advance = new NpgsqlCommand("""
            UPDATE weft.revision_derivations SET current_generation=1,current_attempt_id=$2 WHERE cache_entry_id=$1
            """, connection, transaction))
        {
            advance.Parameters.AddWithValue(cacheId);
            advance.Parameters.AddWithValue(derivation);
            Assert.True(await advance.ExecuteNonQueryAsync() == 1, "The synthetic historical report claim was not acknowledged.");
        }
        await using (var finish = new NpgsqlCommand("""
            UPDATE weft.revision_derivation_attempts a SET completion_kind='usable',observation_present=true,
              backend_status='succeeded',provenance_confirmed=true,provenance_organization_id=k.organization_id,
              provenance_project_id=k.project_id,provenance_mapping_id=k.mapping_id,provenance_revision_id=k.revision_id,
              provenance_native_digest=k.native_digest,provenance_backend_id=k.backend_id,
              provenance_engine_revision=k.engine_revision,provenance_backend_build=k.backend_build,
              provenance_adapter_build=k.adapter_build,projection_coverage='complete',document_item_count=3,
              document_diagnostic_count=0,coverage_gap_count=0,operation_diagnostic_count=0,
              derived_digest=repeat('a',64),derived_byte_count=73
            FROM weft.revision_derivations k WHERE a.derivation_id=$1 AND k.cache_entry_id=a.cache_entry_id
            """, connection, transaction))
        {
            finish.Parameters.AddWithValue(derivation);
            Assert.True(await finish.ExecuteNonQueryAsync() == 1, "The synthetic historical report completion was not acknowledged.");
        }
        await using (var select = new NpgsqlCommand(
            "UPDATE weft.revision_derivations SET selected_derivation_id=$2 WHERE cache_entry_id=$1", connection, transaction))
        {
            select.Parameters.AddWithValue(cacheId);
            select.Parameters.AddWithValue(derivation);
            Assert.True(await select.ExecuteNonQueryAsync() == 1, "The synthetic historical report selection was not acknowledged.");
        }
        await transaction.CommitAsync();
    }

    private static NpgsqlConnection OpenUnpooled(string connectionString) =>
        new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    private static async Task MigratePrefixAsync(PostgresCase test, int count)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(count).ToArray());
        await runner.MigrateAsync();
    }

    private static async Task AssertPrefixHistoryAsync(PostgresCase test)
    {
        var scripts = MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray();
        Assert.True(scripts.Select(script => script.Checksum)
            .SequenceEqual(PostgresProjectPayloadUpgradeConservation.PrefixChecksums, StringComparer.Ordinal),
            "The trusted eight-script migration prefix is unavailable.");
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(Enumerable.Range(1, PrefixVersion)), "The historical migration versions are not the exact eight-script prefix.");
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(scripts.Select(script => script.Checksum), StringComparer.Ordinal),
            "The historical migration checksums are not the trusted eight-script prefix.");
    }

    private static async Task AssertPayloadHistoryAsync(PostgresCase test)
    {
        var scripts = MigrationRunner.EmbeddedMigrations.Take(PrefixVersion + 1).ToArray();
        Assert.True(scripts.Length == PrefixVersion + 1, "This test requires exactly one additive payload migration after the trusted prefix.");
        Assert.True(scripts.Take(PrefixVersion).Select(script => script.Checksum)
            .SequenceEqual(PostgresProjectPayloadUpgradeConservation.PrefixChecksums, StringComparer.Ordinal),
            "The trusted eight-script migration bodies changed.");
        Assert.True(StringComparer.Ordinal.Equals(scripts[^1].Checksum, PayloadPermissionSchemaContract.MigrationChecksum),
            "The payload-prefix migration body changed.");
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(Enumerable.Range(1, scripts.Length)), "The historical migration versions are not the exact payload prefix.");
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(scripts.Select(script => script.Checksum), StringComparer.Ordinal),
            "The historical migration checksums are not the trusted payload prefix.");
    }

    private static async Task AssertCurrentHistoryAsync(PostgresCase test)
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        Assert.True(scripts.Take(PrefixVersion).Select(script => script.Checksum)
            .SequenceEqual(PostgresProjectPayloadUpgradeConservation.PrefixChecksums, StringComparer.Ordinal),
            "The trusted eight-script migration bodies changed.");
        Assert.True(scripts.Skip(PrefixVersion).Select(script => script.Checksum)
            .SequenceEqual(new[] { PayloadPermissionSchemaContract.MigrationChecksum, ServerSessionSchemaContract.MigrationChecksum, ChatSchemaContract.MigrationChecksum,
                PreviewSchemaTestContract.MigrationChecksum, PreviewSchemaTestContract.MessagesMigrationChecksum,
                RegressionSchemaTestContract.CasesChecksum, RegressionSchemaTestContract.RunsChecksum, RegressionSchemaTestContract.DispatchChecksum, RegressionSchemaTestContract.HistoryChecksum, RegressionSchemaTestContract.BuildChecksum, RegressionSchemaTestContract.BuildDispatchChecksum, RegressionSchemaTestContract.BuildMetadataChecksum, RegressionSchemaTestContract.QualificationChecksum, RegressionSchemaTestContract.LocalReleaseChecksum,
                MaintenanceSchemaContract.StoreKindMigrationChecksum }, StringComparer.Ordinal),
            "The current migration supplements do not match the payload, session, chat and preview sources.");
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(Enumerable.Range(1, scripts.Count)), "The complete migration versions do not match this build.");
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(scripts.Select(script => script.Checksum), StringComparer.Ordinal),
            "The complete migration checksums do not match this build.");
    }

    private static async Task<byte[]> JournalDigestAsync(NpgsqlConnection connection, int maximumVersion = int.MaxValue)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(j) ORDER BY version),'[]'::jsonb)::text
            FROM weft.schema_migrations j WHERE version<=$1
            """, connection);
        command.Parameters.AddWithValue(maximumVersion);
        return Digest(Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> RowDigestsAsync(NpgsqlConnection connection, bool includePayload)
    {
        var tables = PostgresProjectPayloadUpgradeConservation.PrefixTables.Where(name => name != "schema_migrations");
        if (includePayload) tables = tables.Append("project_payload_read_grants").Append(ServerSessionSchemaContract.SessionTable);
        var rows = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var table in tables)
            rows.Add(table, Digest(await PostgresExternalIdentityTestSupport.TableRowsAsync(connection, table)));
        return rows;
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static void SameRows(IReadOnlyDictionary<string, byte[]> before, IReadOnlyDictionary<string, byte[]> after)
    {
        Assert.True(before.Count == after.Count, "The private historical row snapshot set changed.");
        foreach (var (table, digest) in before)
            Assert.True(after.TryGetValue(table, out var current) && CryptographicOperations.FixedTimeEquals(digest, current),
                "An acknowledged private historical row snapshot changed.");
    }

    private static void SameDigest(byte[] before, byte[] after, string label) =>
        Assert.True(CryptographicOperations.FixedTimeEquals(before, after), label);
}
