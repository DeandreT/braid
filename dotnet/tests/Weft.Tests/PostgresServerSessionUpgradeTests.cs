using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Genuine migration/history conservation. Native/report admission and protected ticket
/// bytes are explicitly synthetic metadata: no worker, private store, codec or cookie runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresServerSessionUpgradeTests(PostgresFixture fixture)
{
    private const int PrefixVersion = 9;

    [PostgresFact]
    public async Task FreshConcurrentAndRepeatedMigrationPreserveLiveRevokedAndExpiredSessionMetadata()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        try
        {
            await MigrateConcurrentlyAsync(test);
            await AssertCurrentHistoryAsync(test);
            var seed = await PostgresSeedData.CreateAsync(test);
            ExternalIdentityBinding binding;
            await using (var owner = new ExternalIdentityBindingProvisioner(test.MigrationConnectionString))
            {
                var result = await owner.BindAsync(PostgresExternalIdentityTestSupport.Key("session-fresh"),
                    new PrincipalId(seed.Alice));
                Assert.True(result.Outcome == ExternalIdentityBindingOwnerOutcome.Created && result.Binding is not null,
                    "The fresh session fixture did not acknowledge its explicit binding.");
                binding = result.Binding ?? throw new InvalidOperationException("The acknowledged fixture binding is absent.");
            }
            await using var writerPool = new PostgresServerSessionDatabase(test.SessionWriterConnectionString,
                ServerSessionDatabaseProfile.Writer);
            await using var readerPool = new PostgresServerSessionDatabase(test.SessionReaderConnectionString,
                ServerSessionDatabaseProfile.Reader);
            var writer = new PostgresServerSessionWriter(writerPool);
            var reader = new PostgresServerSessionReader(readerPool);
            var sessions = await CreateSessionStatesAsync(test, writer, seed.OrganizationA, binding.BindingId,
                binding.Version, binding.PrincipalId);
            await AssertSessionStatesAsync(reader, sessions);
            await using var connection = OpenUnpooled(test.MigrationConnectionString);
            await connection.OpenAsync();
            var rows = await RowDigestsAsync(connection, includeSessions: true);
            var journal = await JournalDigestAsync(connection);
            await test.MigrateAsync();
            await MigrateConcurrentlyAsync(test);
            await AssertCurrentHistoryAsync(test);
            SameDigest(journal, await JournalDigestAsync(connection),
                "Repeated session migration changed an acknowledged journal fact.");
            SameRows(rows, await RowDigestsAsync(connection, includeSessions: true));
            await AssertSessionStatesAsync(reader, sessions);
            SameRows(rows, await RowDigestsAsync(connection, includeSessions: true));
        }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The fresh session migration conservation operation failed."); }
    }

    [PostgresFact]
    public async Task PopulatedNinePrefixUpgradePreservesHistoricalSourceRowsAndEveryOldAuthority()
    {
        // Both cluster-global logins are genuinely absent throughout the nine-script
        // installation and its historical population. Root owns these additive flags.
        var isolated = new PostgresFixture(provisionMetadataEditor: true,
            provisionSessionReader: false, provisionSessionWriter: false);
        await isolated.InitializeAsync();
        try
        {
            await using var catalog = await PostgresCatalogTestSupport.CreateAsync(isolated, installCatalog: false);
            var test = catalog.Case;
            var original = await catalog.CommitCreateAsync(filename: "historical-session-upgrade.native.json");
            var appended = await catalog.CommitAppendAsync(original, valid: false);
            await MigratePrefixAsync(test, 6);
            var reportKey = await SeedHistoricalReportAsync(test, catalog.Actor, original);
            await MigratePrefixAsync(test, PrefixVersion);
            var binding = await SeedHistoricalIdentityAndPayloadAsync(test, catalog.Seed);
            await AssertPrefixHistoryAsync(test);
            Assert.True(await test.ScalarAsync<bool>("""
                SELECT (SELECT count(*) FROM pg_catalog.pg_roles
                    WHERE rolname IN ('weft_session_reader','weft_session_writer'))=0
                  AND pg_catalog.to_regclass('weft.server_sessions') IS NULL
                  AND (SELECT count(*) FROM weft.mappings)>0
                  AND (SELECT count(*) FROM weft.mapping_revisions)>=2
                  AND (SELECT count(*) FROM weft.revision_ingestions WHERE phase='committed')>=2
                  AND (SELECT count(*) FROM weft.revision_derivation_attempts WHERE result_usable)>0
                  AND (SELECT count(*) FROM weft.external_identity_bindings)=2
                  AND (SELECT count(*) FROM weft.external_identity_bindings WHERE NOT enabled)=1
                  AND (SELECT count(*) FROM weft.project_payload_read_grants WHERE active)=1
                  AND (SELECT count(*) FROM weft.project_payload_read_grants WHERE NOT active)=1
                """), "The source prefix was not genuinely populated before the session upgrade.");
            await using var connection = OpenUnpooled(test.MigrationConnectionString);
            await connection.OpenAsync();
            var source = await PostgresServerSessionUpgradeConservation.CaptureNineAsync(connection);
            var rows = await RowDigestsAsync(connection, includeSessions: false);
            var journal = await JournalDigestAsync(connection, PrefixVersion);

            // Each intentional bootstrap/drift phase has its own precise private
            // baseline. No failed install can adopt roles, schema, grants or rows.
            await AssertInstallRejectedWithoutAdoptionAsync(test, connection); // both absent
            await ProvisionSessionConnectOnlyAsync(test, "weft_session_reader");
            await AssertInstallRejectedWithoutAdoptionAsync(test, connection); // writer absent
            await ProvisionSessionConnectOnlyAsync(test, "weft_session_writer");
            await test.BootstrapAsync($"""
                REVOKE CONNECT ON DATABASE {test.Name} FROM weft_session_reader;
                DROP ROLE weft_session_reader;
                """);
            await AssertInstallRejectedWithoutAdoptionAsync(test, connection); // reader absent
            await ProvisionSessionConnectOnlyAsync(test, "weft_session_reader");
            await test.BootstrapAsync("ALTER ROLE weft_session_reader INHERIT");
            try { await AssertInstallRejectedWithoutAdoptionAsync(test, connection); }
            finally { await test.BootstrapAsync("ALTER ROLE weft_session_reader NOINHERIT"); }
            await test.ExecuteAsync("GRANT USAGE ON SCHEMA weft TO weft_session_writer");
            try { await AssertInstallRejectedWithoutAdoptionAsync(test, connection); }
            finally { await test.ExecuteAsync("REVOKE USAGE ON SCHEMA weft FROM weft_session_writer"); }
            await MigrateConcurrentlyAsync(test);
            await AssertCurrentHistoryAsync(test);
            await source.AssertSessionUpgradePreservedAsync(connection, test);
            SameDigest(journal, await JournalDigestAsync(connection, PrefixVersion),
                "Session upgrade changed a historical journal fact.");
            SameRows(rows, await RowDigestsAsync(connection, includeSessions: false));
            Assert.True(await test.ScalarAsync<long>("SELECT count(*) FROM weft.server_sessions") == 0,
                "Session migration inferred an authentication session from old authority or grants.");

            // Public readers run only after the complete new history is installed.
            // Current owner APIs intentionally cannot seed an incomplete future build.
            await AssertHistoricalReadersAsync(catalog, original, appended, reportKey, binding);
            SameRows(rows, await RowDigestsAsync(connection, includeSessions: false));
            await using var writerPool = new PostgresServerSessionDatabase(test.SessionWriterConnectionString,
                ServerSessionDatabaseProfile.Writer);
            await using var readerPool = new PostgresServerSessionDatabase(test.SessionReaderConnectionString,
                ServerSessionDatabaseProfile.Reader);
            var sessions = await CreateSessionStatesAsync(test, new PostgresServerSessionWriter(writerPool),
                catalog.Seed.OrganizationA, binding, 1, catalog.Actor.PrincipalId);
            await AssertSessionStatesAsync(new PostgresServerSessionReader(readerPool), sessions);
            var allRows = await RowDigestsAsync(connection, includeSessions: true);
            var fullJournal = await JournalDigestAsync(connection);
            await test.MigrateAsync();
            await MigrateConcurrentlyAsync(test);
            await AssertCurrentHistoryAsync(test);
            SameDigest(fullJournal, await JournalDigestAsync(connection),
                "Repeated session upgrade changed the complete journal.");
            SameRows(allRows, await RowDigestsAsync(connection, includeSessions: true));
            SameRows(rows, await RowDigestsAsync(connection, includeSessions: false));
            await source.AssertSessionUpgradePreservedAsync(connection, test);
            await AssertSessionStatesAsync(new PostgresServerSessionReader(readerPool), sessions);
            await AssertHistoricalReadersAsync(catalog, original, appended, reportKey, binding);
            SameRows(allRows, await RowDigestsAsync(connection, includeSessions: true));
        }
        catch (NpgsqlException)
        { throw new InvalidOperationException("The historical session upgrade conservation operation failed."); }
        finally { await isolated.DisposeAsync(); }
    }

    private sealed record SessionStates(ServerSessionFacts Live, ServerSessionFacts Revoked, ServerSessionKey Expired, PrincipalId Owner)
    {
        public override string ToString() => nameof(SessionStates);
    }

    private static async Task<SessionStates> CreateSessionStatesAsync(PostgresCase test,
        PostgresServerSessionWriter writer, Guid organization, Guid bindingId, long bindingVersion, PrincipalId owner)
    {
        // This deadline is a synthetic verified-login input from DB time, not provider evidence.
        var deadline = new DateTimeOffset(await test.ScalarAsync<DateTime>(
            "SELECT statement_timestamp()+interval '2 hours'"));
        ServerSessionCreateIntent Intent() => new(NewKey(organization), bindingId, bindingVersion,
            owner, Guid.NewGuid(), deadline, new byte[] { 11, 29, 47 });
        var liveResult = await writer.CreateOnlyAsync(Intent());
        var revokeResult = await writer.CreateOnlyAsync(Intent());
        Assert.True(liveResult.Outcome == ServerSessionWriteOutcome.Created && liveResult.Facts is not null
            && revokeResult.Outcome == ServerSessionWriteOutcome.Created && revokeResult.Facts is not null,
            "The fixture did not acknowledge two create-only session rows.");
        var live = liveResult.Facts ?? throw new InvalidOperationException("The live fixture session is absent.");
        var beforeRevoke = revokeResult.Facts ?? throw new InvalidOperationException("The revocation fixture session is absent.");
        var revoked = await writer.RevokeAsync(beforeRevoke.Key);
        Assert.True(revoked.Outcome == ServerSessionWriteOutcome.Revoked && revoked.Facts is not null,
            "The fixture did not acknowledge its one-way session revocation.");
        var expired = NewKey(organization);
        // Legitimate already-expired version1 metadata uses the real new guard; no
        // clock override, disabled trigger, codec, worker or public mint route exists.
        await test.ExecuteAsync("""
            WITH clock AS (SELECT statement_timestamp()-interval '2 hours' AS issued)
            INSERT INTO weft.server_sessions(handle_digest,organization_id,binding_id,binding_version,login_epoch,
                issued_at,idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket)
            SELECT $1,$2,$3,$4,$5,issued,issued+interval '20 minutes',issued+interval '60 minutes',
                1,NULL,1,1,$6 FROM clock
            """, Convert.FromHexString(expired.HandleDigest.Value), organization, bindingId, bindingVersion,
                Guid.NewGuid(), new byte[] { 53, 71, 89 });
        return new(live, revoked.Facts!, expired, owner);
    }

    private static async Task AssertSessionStatesAsync(PostgresServerSessionReader reader, SessionStates states)
    {
        var live = await reader.ObserveAsync(states.Live.Key);
        Assert.True(live is not null && live.PrincipalId == states.Owner && live.Facts.Key == states.Live.Key
            && live.Facts.BindingId == states.Live.BindingId && live.Facts.BindingVersion == states.Live.BindingVersion
            && live.Facts.LoginEpoch == states.Live.LoginEpoch && live.Facts.Lifetime == states.Live.Lifetime
            && live.Facts.Version == states.Live.Version && live.Facts.RevokedAtUtc is null
            && CryptographicOperations.FixedTimeEquals(live.Facts.CopyProtectedTicket(), states.Live.CopyProtectedTicket()),
            "Repeated migration changed the live session observation or opaque protected bytes.");
        Assert.True(await reader.ObserveAsync(states.Revoked.Key) is null,
            "Repeated migration exposed a revoked session as live.");
        Assert.True(await reader.ObserveAsync(states.Expired) is null,
            "Repeated migration exposed legitimately expired metadata as live.");
    }

    private static async Task<Guid> SeedHistoricalIdentityAndPayloadAsync(PostgresCase test, PostgresSeedData seed)
    {
        // These are constrained historical91/93 transitions, not successful calls to
        // the current owner APIs (which correctly require the complete future journal).
        await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Admitted, "viewer");
        await test.ExecuteAsync("""
            INSERT INTO weft.organization_project_creation_capabilities(organization_id,principal_id,can_create_projects)
            VALUES($1,$2,true),($1,$3,false)
            """, seed.OrganizationA, seed.Admitted, seed.Alice);
        var active = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        foreach (var (id, subject) in new[] { (active, "session-upgrade-active"), (disabled, "session-upgrade-disabled") })
            await test.ExecuteAsync("""
                INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id)
                VALUES($1,'test-provider','https://identity.example/Exact Tenant',$2,$3)
                """, id, subject, seed.Alice);
        await test.ExecuteAsync("""
            UPDATE weft.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp()
            WHERE binding_id=$1
            """, disabled);
        await test.ExecuteAsync("""
            INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version)
            VALUES($1,$2,$3,true,1),($1,$2,$4,true,1)
            """, seed.OrganizationA, seed.ProjectA, seed.Alice, seed.Admitted);
        await test.ExecuteAsync("""
            UPDATE weft.project_payload_read_grants SET active=false,version=2
            WHERE organization_id=$1 AND project_id=$2 AND principal_id=$3
            """, seed.OrganizationA, seed.ProjectA, seed.Admitted);
        return active;
    }

    private static async Task AssertHistoricalReadersAsync(PostgresCatalogTestSupport catalog,
        IngestionReceipt original, IngestionReceipt appended, ProjectionCacheKey reportKey, Guid bindingId)
    {
        var originalRevision = await catalog.Reader.GetRevisionAsync(catalog.Actor, original.Facts.FutureRevision);
        var appendedRevision = await catalog.Reader.GetRevisionAsync(catalog.Actor, appended.Facts.FutureRevision);
        Assert.True(originalRevision is not null && appendedRevision is not null
            && originalRevision.Summary.Scope == original.Facts.FutureRevision && appendedRevision.Summary.Scope == appended.Facts.FutureRevision
            && originalRevision.Summary.NativeDigest == original.Facts.NativeObject.Digest
            && appendedRevision.Summary.NativeDigest == appended.Facts.NativeObject.Digest
            && originalRevision.Summary.NativeByteCount == original.Facts.NativeObject.ByteCount
            && appendedRevision.Summary.NativeByteCount == appended.Facts.NativeObject.ByteCount
            && originalRevision.Summary.Sequence == 1 && appendedRevision.Summary.Sequence == 2,
            "Session upgrade changed historical ordinary native revision reads.");
        await using var identity = new PostgresExternalIdentityDatabase(catalog.Case.IdentityConnectionString);
        var identityReader = new PostgresExternalIdentityReader(identity);
        var resolved = await identityReader.ResolveAsync(PostgresExternalIdentityTestSupport.Key("session-upgrade-active"));
        Assert.True(resolved.Outcome == ExternalIdentityResolutionOutcome.Resolved && resolved.Binding is { Enabled: true } binding
            && binding.BindingId == bindingId && binding.PrincipalId == catalog.Actor.PrincipalId,
            "Session upgrade changed a historical enabled external identity.");
        var hidden = await identityReader.ResolveAsync(PostgresExternalIdentityTestSupport.Key("session-upgrade-disabled"));
        Assert.True(hidden.Outcome == ExternalIdentityResolutionOutcome.Disabled && hidden.Binding is null,
            "Session upgrade exposed or changed a historical disabled external identity.");
        await using var payload = new PostgresPayloadPermissionDatabase(catalog.Case.PayloadPermissionConnectionString);
        var payloadReader = new PostgresProjectPayloadPermissionReader(payload);
        var grant = await payloadReader.ObserveAsync(catalog.Actor, catalog.Project);
        Assert.True(grant is { Active: true, Version: 1 } && grant.Scope == catalog.Project
            && grant.PrincipalId == catalog.Actor.PrincipalId,
            "Session upgrade changed the historical explicit active payload grant.");
        Assert.True(await payloadReader.ObserveAsync(new(new PrincipalId(catalog.Seed.Admitted)), catalog.Project) is null,
            "Session upgrade exposed a revoked payload grant.");
        await using var reports = new PostgresRevisionReportDatabase(catalog.Case.ReportConnectionString);
        var report = await new PostgresRevisionReportMetadataReader(reports).GetCurrentAsync(catalog.Actor, reportKey);
        Assert.True(report is { SelectedResult: not null } && report.Key == reportKey,
            "Session upgrade changed the previously selected historical report metadata.");
    }

    internal static async Task<ProjectionCacheKey> SeedHistoricalReportAsync(PostgresCase test, ActorContext actor, IngestionReceipt receipt)
    {
        // This is explicitly synthetic metadata authored through genuine0006
        // constraints and transitions. No private report or native bytes exist.
        var facts = receipt.Facts;
        var source = new RevisionReportSource(facts.FutureRevision, facts.NativeObject,
            facts.Intent.LogicalDocument, facts.Intent.DependencyIdentity, facts.BackendProfile.BackendId);
        var key = new ProjectionCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
            "weft.mapping-projection.v1", "synthetic.session-upgrade.v1", "synthetic.report.codec.v1", new());
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
        return key;
    }

    private static async Task AssertInstallRejectedWithoutAdoptionAsync(PostgresCase test, NpgsqlConnection connection)
    {
        await AssertPrefixHistoryAsync(test);
        var source = await PostgresServerSessionUpgradeConservation.CaptureBeforeRejectedInstallAsync(connection);
        var rows = await RowDigestsAsync(connection, includeSessions: false);
        var journal = await JournalDigestAsync(connection, PrefixVersion);
        var rejected = false;
        try
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString);
            await runner.MigrateAsync();
        }
        catch (PostgresException error)
        {
            rejected = error.SqlState == PostgresErrorCodes.InsufficientPrivilege;
        }
        Assert.True(rejected, "Session installation did not refuse the invalid CONNECT-only role precondition.");
        await AssertPrefixHistoryAsync(test);
        await source.AssertRejectedInstallPreservedAsync(connection);
        SameDigest(journal, await JournalDigestAsync(connection, PrefixVersion),
            "Rejected session installation changed historical journal bytes or creation timestamps.");
        SameRows(rows, await RowDigestsAsync(connection, includeSessions: false));
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT pg_catalog.to_regclass('weft.server_sessions') IS NULL
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                    WHERE n.nspname='weft' AND p.proname='guard_server_sessions_transition')
            """), "Rejected session installation left new objects to be adopted.");
    }

    private static async Task ProvisionSessionConnectOnlyAsync(PostgresCase test, string onlyRole)
    {
        Assert.True(Guid.TryParseExact(test.Name["weft_case_".Length..], "N", out _),
            "The isolated database name is not fixture-owned.");
        if (onlyRole is not "weft_session_reader" and not "weft_session_writer")
            throw new InvalidOperationException("The owned session bootstrap role is invalid.");
        foreach (var (name, value) in new[] { ("weft_session_reader", test.SessionReaderConnectionString),
            ("weft_session_writer", test.SessionWriterConnectionString) })
        {
            if (name != onlyRole) continue;
            var configuration = new NpgsqlConnectionStringBuilder(value);
            if (configuration.Username != name || configuration.Database != test.Name || string.IsNullOrEmpty(configuration.Password))
                throw new InvalidOperationException("The owned session profile configuration is inconsistent.");
            var password = configuration.Password.Replace("'", "''", StringComparison.Ordinal);
            await test.BootstrapAsync($"""
                CREATE ROLE {name} LOGIN PASSWORD '{password}'
                  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                GRANT CONNECT ON DATABASE {test.Name} TO {name};
                """);
        }
    }

    private static ServerSessionKey NewKey(Guid organization) =>
        new(organization, Sha256Digest.FromBytes(RandomNumberGenerator.GetBytes(32)));

    private static NpgsqlConnection OpenUnpooled(string connectionString) =>
        new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    private static async Task MigratePrefixAsync(PostgresCase test, int count)
    {
        await using var runner = new MigrationRunner(test.MigrationConnectionString,
            MigrationRunner.EmbeddedMigrations.Take(count).ToArray());
        await runner.MigrateAsync();
    }

    private static async Task MigrateConcurrentlyAsync(PostgresCase test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var pending = Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var runner = new MigrationRunner(test.MigrationConnectionString);
            await runner.MigrateAsync(deadline.Token);
        }).ToArray();
        Exception? primary = null;
        try { await Task.WhenAll(pending); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            deadline.Cancel();
            try { await Task.WhenAll(pending); }
            catch when (primary is not null) { /* Original failure already propagates; every migration has settled. */ }
        }
    }

    private static async Task AssertPrefixHistoryAsync(PostgresCase test)
    {
        var scripts = MigrationRunner.EmbeddedMigrations.Take(PrefixVersion).ToArray();
        Assert.True(scripts.Select(script => script.Checksum)
            .SequenceEqual(PostgresServerSessionUpgradeConservation.PrefixChecksums, StringComparer.Ordinal),
            "The trusted nine-script source prefix changed.");
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(Enumerable.Range(1, PrefixVersion)), "The historical journal is not the exact nine-script prefix.");
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(scripts.Select(script => script.Checksum), StringComparer.Ordinal),
            "The historical journal checksum prefix changed.");
    }

    private static async Task AssertCurrentHistoryAsync(PostgresCase test)
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        Assert.True(scripts.Count == PrefixVersion + 20
            && scripts.Take(PrefixVersion).Select(script => script.Checksum)
                .SequenceEqual(PostgresServerSessionUpgradeConservation.PrefixChecksums, StringComparer.Ordinal)
            && scripts[PrefixVersion].Checksum == ServerSessionSchemaContract.MigrationChecksum
            && scripts[PrefixVersion + 1].Version == 11 && scripts[PrefixVersion + 1].Checksum == ChatSchemaContract.MigrationChecksum
            && scripts[PrefixVersion + 2].Version == 12 && scripts[PrefixVersion + 2].Checksum == PreviewSchemaTestContract.MigrationChecksum
            && scripts[PrefixVersion + 3].Version == 13 && scripts[PrefixVersion + 3].Checksum == PreviewSchemaTestContract.MessagesMigrationChecksum
            && scripts[PrefixVersion + 4].Version == 14 && scripts[PrefixVersion + 4].Checksum == RegressionSchemaTestContract.CasesChecksum
            && scripts[PrefixVersion + 5].Version == 15 && scripts[PrefixVersion + 5].Checksum == RegressionSchemaTestContract.RunsChecksum
            && scripts[PrefixVersion + 6].Version == 16 && scripts[PrefixVersion + 6].Checksum == RegressionSchemaTestContract.DispatchChecksum
            && scripts[PrefixVersion + 7].Version == 17 && scripts[PrefixVersion + 7].Checksum == RegressionSchemaTestContract.HistoryChecksum
            && scripts[PrefixVersion + 8].Version == 18 && scripts[PrefixVersion + 8].Checksum == RegressionSchemaTestContract.BuildChecksum
            && scripts[PrefixVersion + 9].Version == 19 && scripts[PrefixVersion + 9].Checksum == RegressionSchemaTestContract.BuildDispatchChecksum
            && scripts[PrefixVersion + 10].Version == 20 && scripts[PrefixVersion + 10].Checksum == RegressionSchemaTestContract.BuildMetadataChecksum
            && scripts[PrefixVersion + 11].Version == 21 && scripts[PrefixVersion + 11].Checksum == RegressionSchemaTestContract.QualificationChecksum
            && scripts[PrefixVersion + 12].Version == 22 && scripts[PrefixVersion + 12].Checksum == RegressionSchemaTestContract.LocalReleaseChecksum
            && scripts[PrefixVersion + 13].Version == 23 && scripts[PrefixVersion + 13].Checksum == MaintenanceSchemaContract.StoreKindMigrationChecksum
            && scripts[PrefixVersion + 14].Version == 24 && scripts[PrefixVersion + 14].Checksum == RegressionSchemaTestContract.MachineInvocationChecksum
            && scripts[PrefixVersion + 15].Version == 25 && scripts[PrefixVersion + 15].Checksum == RegressionSchemaTestContract.ApiRequestAuditChecksum
            && scripts[PrefixVersion + 16].Version == 26 && scripts[PrefixVersion + 16].Checksum == RegressionSchemaTestContract.AccountDisplayChecksum
            && scripts[PrefixVersion + 17].Version == 27 && scripts[PrefixVersion + 17].Checksum == RegressionSchemaTestContract.AdministrationChecksum
            && scripts[PrefixVersion + 18].Version == 28 && scripts[PrefixVersion + 18].Checksum == InterchangeSchemaTestContract.MigrationChecksum
            && scripts[PrefixVersion + 19].Version == 29 && scripts[PrefixVersion + 19].Checksum == CatalogSearchSchemaAdmission.MigrationChecksum
            && typeof(MigrationRunner).Assembly.GetManifestResourceNames().Contains(
                "Weft.Persistence.Postgres.Migrations." + ServerSessionSchemaContract.MigrationName + ".sql", StringComparer.Ordinal),
            "The embedded source is not the trusted prefix, exact session migration and exact chat and preview suffix.");
        await PostgresServerSessionUpgradeConservation.AdmitChatSuffixAsync(test);
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(Enumerable.Range(1, scripts.Count)), "The complete journal versions changed.");
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations"))
            .SequenceEqual(scripts.Select(script => script.Checksum), StringComparer.Ordinal),
            "The complete journal checksums changed.");
    }

    private static async Task<byte[]> JournalDigestAsync(NpgsqlConnection connection, int maximumVersion = int.MaxValue)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(j) ORDER BY version),'[]'::jsonb)::text
            FROM weft.schema_migrations j WHERE version<=$1
            """, connection);
        command.Parameters.AddWithValue(maximumVersion);
        var value = await command.ExecuteScalarAsync();
        if (value is not string text) throw new InvalidOperationException("The private journal observation is unavailable.");
        return Digest(text);
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> RowDigestsAsync(NpgsqlConnection connection, bool includeSessions)
    {
        // Journal rows/timestamps are compared separately with their exact old/full range.
        IEnumerable<string> tables = PostgresServerSessionUpgradeConservation.PrefixTables.Where(name => name != "schema_migrations");
        if (includeSessions) tables = tables.Append("server_sessions");
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
                "An acknowledged private historical row changed during session migration.");
    }

    private static void SameDigest(byte[] before, byte[] after, string category) =>
        Assert.True(CryptographicOperations.FixedTimeEquals(before, after), category);
}
