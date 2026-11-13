using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

// Genuine prefix installation and synthetic historical facts only. No provider,
// codec, worker, private filesystem, API activation or provisioning runs in prep.
[Collection(PostgresCollection.Name)]
public sealed class PostgresChatUpgradeTests(PostgresFixture fixture)
{
    private static readonly ConditionalWeakTable<byte[], Dictionary<string, byte[]>> SnapshotParts = new();
    [PostgresFact]
    public async Task FreshConcurrentAndRepeatedInstallationKeepsExactHistoryAndAcknowledgedChatRows()
    {
        await using var test = await fixture.CreateCaseAsync(migrate: false);
        await ConcurrentAsync(test);
        await HistoryAsync(test, MigrationRunner.EmbeddedMigrations.Count);
        var before = await SnapshotAsync(test, installed: true);
        await test.MigrateAsync(); await ConcurrentAsync(test);
        Same(before, await SnapshotAsync(test, installed: true));
        await using var chat = await PostgresChatTestSupport.CreateAsync(fixture);
        var turn = await chat.StartAsync();
        var round = PostgresChatTestSupport.Round();
        Assert.Equal(ChatStoreOutcome.Applied, (await chat.Writer.ReserveRoundAsync(chat.Actor, chat.Key, turn.Intent.TurnId, turn.Version, round)).Outcome);
        var rows = await PostgresChatSecurityTests.RowsAsync(chat);
        await ConcurrentAsync(chat.Case); await chat.Case.MigrateAsync();
        PostgresChatSecurityTests.Same(rows, await PostgresChatSecurityTests.RowsAsync(chat));
        Assert.True(await chat.Reader.ReadAsync(chat.Actor, chat.Key) == chat.Conversation);
    }

    [PostgresFact]
    public async Task PopulatedTenPrefixMissingAndBroadenedRoleFailuresConserveHistoryRowsSchemaAndOldAuthority()
    {
        var isolated = new PostgresFixture(provisionMetadataEditor: true, provisionChatReader: false, provisionChatWriter: false, provisionChatLifecycle: false);
        await isolated.InitializeAsync();
        try
        {
            await using var catalog = await PostgresCatalogTestSupport.CreateAsync(isolated, installCatalog: false);
            var original = await catalog.CommitCreateAsync(filename: "historical-chat-upgrade.native.json");
            var appended = await catalog.CommitAppendAsync(original, valid: false);
            await PrefixAsync(catalog.Case, 6);
            var reportKey = await SeedReportAsync(catalog.Case, catalog.Actor, original);
            await PrefixAsync(catalog.Case, 10);
            await SeedIdentityPayloadAndSessionsAsync(catalog);
            await HistoryAsync(catalog.Case, 10);
            Assert.True(await catalog.Case.ScalarAsync<bool>("""
                SELECT (SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle'))=0
                    AND (SELECT count(*) FROM weft.mapping_revisions)>=2
                    AND EXISTS(SELECT 1 FROM weft.revision_derivation_attempts WHERE result_usable)
                    AND EXISTS(SELECT 1 FROM weft.server_sessions WHERE revoked_at IS NOT NULL)
                    AND EXISTS(SELECT 1 FROM weft.project_payload_read_grants WHERE NOT active)
                    AND pg_catalog.to_regclass('weft.chat_conversations') IS NULL
                """), "The chat upgrade did not start from a populated genuine ten-script prefix.");
            await RejectedAsync(catalog.Case); // both absent
            await ProvisionAsync(catalog.Case, writer: false);
            await RejectedAsync(catalog.Case); // writer absent
            await ProvisionAsync(catalog.Case, writer: true);
            await RejectedAsync(catalog.Case); // lifecycle absent
            await ProvisionAsync(catalog.Case, writer: false, lifecycle: true);
            await catalog.Case.BootstrapAsync($"REVOKE CONNECT ON DATABASE {catalog.Case.Name} FROM weft_chat_reader; DROP ROLE weft_chat_reader");
            await RejectedAsync(catalog.Case); // reader absent
            await ProvisionAsync(catalog.Case, writer: false);
            foreach (var role in new[] { "weft_chat_reader", "weft_chat_writer", "weft_chat_lifecycle" })
            {
                await catalog.Case.BootstrapAsync($"ALTER ROLE {role} INHERIT");
                try { await RejectedAsync(catalog.Case); }
                finally { await catalog.Case.BootstrapAsync($"ALTER ROLE {role} NOINHERIT"); }
                await catalog.Case.ExecuteAsync($"GRANT SELECT(id) ON weft.principals TO {role}");
                try { await RejectedAsync(catalog.Case); }
                finally { await catalog.Case.ExecuteAsync($"REVOKE SELECT(id) ON weft.principals FROM {role}"); }
            }
            // Ordinary application names beginning with pg are not reserved pg_
            // schemas. A PUBLIC namespace grant must not hide either profile's
            // unexpected pgx table/column authority during migration preflight.
            await catalog.Case.ExecuteAsync("CREATE SCHEMA pgx; CREATE TABLE pgx.chat_admission_probe(id integer); GRANT USAGE ON SCHEMA pgx TO PUBLIC");
            try
            {
                foreach (var role in new[] { "weft_chat_reader", "weft_chat_writer", "weft_chat_lifecycle" })
                {
                    await catalog.Case.ExecuteAsync($"GRANT SELECT(id) ON pgx.chat_admission_probe TO {role}");
                    try
                    {
                        await RejectedAsync(catalog.Case);
                        Assert.True(await catalog.Case.ScalarAsync<bool>("SELECT has_schema_privilege($1,'pgx','USAGE') AND has_column_privilege($1,'pgx.chat_admission_probe','id','SELECT')", role),
                            "A rejected install repaired or removed the deliberately invalid pgx authority instead of refusing it.");
                    }
                    finally { await catalog.Case.ExecuteAsync($"REVOKE SELECT(id) ON pgx.chat_admission_probe FROM {role}"); }
                }
            }
            finally { await catalog.Case.ExecuteAsync("DROP TABLE pgx.chat_admission_probe; DROP SCHEMA pgx"); }
            var before = await SnapshotAsync(catalog.Case, installed: false);
            await ConcurrentAsync(catalog.Case);
            await HistoryAsync(catalog.Case, MigrationRunner.EmbeddedMigrations.Count);
            Same(before, await SnapshotAsync(catalog.Case, installed: true));
            Assert.True(await catalog.Case.ScalarAsync<bool>("SELECT NOT EXISTS(SELECT 1 FROM weft.chat_conversations) AND NOT EXISTS(SELECT 1 FROM weft.chat_turns)"));
            // Fresh exact admission validates the entire allowed new delta before
            // its FK parent triggers/role column grants are projected out below.
            var key = new ChatConversationKey(original.Facts.FutureRevision, catalog.Actor.PrincipalId, Guid.NewGuid());
            await using (var database = new PostgresChatDatabase(catalog.Case.ChatReaderConnectionString, ChatDatabaseProfile.Reader))
            await using (var admitted = await database.BeginAsync(catalog.Actor, key, false, CancellationToken.None)) { }
            var read = await catalog.Reader.GetRevisionAsync(catalog.Actor, original.Facts.FutureRevision);
            var appendRead = await catalog.Reader.GetRevisionAsync(catalog.Actor, appended.Facts.FutureRevision);
            Assert.True(read is not null && appendRead is not null, "Chat upgrade changed historical catalog reads.");
            await using (var database = new PostgresRevisionReportDatabase(catalog.Case.ReportConnectionString))
                Assert.True(await new PostgresRevisionReportMetadataReader(database).GetCurrentAsync(catalog.Actor, reportKey) is { SelectedResult: not null });
            await catalog.Case.MigrateAsync(); await ConcurrentAsync(catalog.Case);
            Same(before, await SnapshotAsync(catalog.Case, installed: true));
        }
        finally { await isolated.DisposeAsync(); }
    }

    private static async Task RejectedAsync(PostgresCase test)
    {
        var before = await SnapshotAsync(test, installed: false);
        var error = await Assert.ThrowsAsync<PostgresException>(() => test.MigrateAsync());
        Assert.True(error.SqlState == PostgresErrorCodes.InsufficientPrivilege, "Chat install did not refuse its exact fresh role precondition.");
        await HistoryAsync(test, 10);
        Same(before, await SnapshotAsync(test, installed: false));
        Assert.True(await test.ScalarAsync<bool>("""
            SELECT NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class r WHERE r.relnamespace='weft'::regnamespace AND r.relname LIKE 'chat_%')
                AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_proc f WHERE f.pronamespace='weft'::regnamespace
                    AND f.proname IN ('chat_authorized','guard_chat_conversation','guard_chat_child','check_chat_counters'))
            """), "A rejected chat migration left objects for adoption.");
    }

    private static async Task SeedIdentityPayloadAndSessionsAsync(PostgresCatalogTestSupport catalog)
    {
        var test = catalog.Case; var seed = catalog.Seed;
        var active = Guid.NewGuid(); var disabled = Guid.NewGuid();
        foreach (var id in new[] { active, disabled })
            await test.ExecuteAsync("INSERT INTO weft.external_identity_bindings(binding_id,provider,authority,subject,principal_id) VALUES($1,'test-provider','https://identity.example/Exact Tenant',$2,$3)", id, id.ToString("N"), seed.Alice);
        await test.ExecuteAsync("UPDATE weft.external_identity_bindings SET enabled=false,version=2,disabled_at=clock_timestamp() WHERE binding_id=$1", disabled);
        await seed.AddMembershipAsync(test, seed.OrganizationA, seed.ProjectA, seed.Admitted, "viewer");
        await test.ExecuteAsync("INSERT INTO weft.project_payload_read_grants(organization_id,project_id,principal_id,active,version) VALUES($1,$2,$3,true,1),($1,$2,$4,true,1)", seed.OrganizationA, seed.ProjectA, seed.Alice, seed.Admitted);
        await test.ExecuteAsync("UPDATE weft.project_payload_read_grants SET active=false,version=2 WHERE principal_id=$1", seed.Admitted);
        foreach (var age in new[] { 0, 120, 180 })
            await test.ExecuteAsync("""
                INSERT INTO weft.server_sessions(handle_digest,organization_id,binding_id,binding_version,login_epoch,
                    issued_at,idle_expires_at,absolute_expires_at,version,revoked_at,policy_version,codec_version,protected_ticket)
                SELECT $1,$2,$3,1,$4,issued,issued+interval '20 minutes',issued+interval '60 minutes',1,NULL,1,1,$5
                FROM (SELECT statement_timestamp()-make_interval(mins=>$6) issued) clock
                """, RandomNumberGenerator.GetBytes(32), seed.OrganizationA, active, Guid.NewGuid(), new byte[] { 7, 19, 31 }, age);
        await test.ExecuteAsync("""
            UPDATE weft.server_sessions SET revoked_at=statement_timestamp(),version=version+1
            WHERE issued_at<(statement_timestamp()-interval '150 minutes')
            """);
    }

    private static async Task<ProjectionCacheKey> SeedReportAsync(PostgresCase test, ActorContext actor, IngestionReceipt receipt)
    {
        var facts = receipt.Facts;
        var source = new RevisionReportSource(facts.FutureRevision, facts.NativeObject, facts.Intent.LogicalDocument, facts.Intent.DependencyIdentity, facts.BackendProfile.BackendId);
        var key = new ProjectionCacheKey(source, PostgresRevisionDerivationTestSupport.Profile,
            "weft.mapping-projection.v1", "synthetic.chat-upgrade.v1", "synthetic.report.codec.v1", new());
        await using var connection = PostgresChatSecurityTests.Open(test.MigrationConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivations(organization_id,project_id,mapping_id,revision_id,operation_kind,
                native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,
                engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,policy_schema_version,policy_json,policy_fingerprint,
                native_byte_limit,worker_response_byte_limit,item_limit,response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,
                validation_issue_limit,text_byte_limit,header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34)
            RETURNING cache_entry_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(insert, key.Scope);
        object[] values = ["projection", key.Source.NativeObject.Id.ObjectId, "native", key.NativeDigest.Value, key.Source.NativeObject.ByteCount,
            key.Source.LogicalDocument, key.Source.DependencyIdentity, key.BackendId, key.BackendRevision, key.BackendBuild, key.AdapterBuild,
            key.SchemaVersion, key.AlgorithmVersion, key.CodecVersion, key.Policy.SchemaVersion, Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()),
            key.Policy.Fingerprint.Value, key.Policy.NativeByteLimit, key.Policy.WorkerResponseByteLimit, key.Policy.ItemLimit, key.Policy.ResponseItemLimit,
            key.Policy.JsonDepthLimit, key.Policy.DetailDepthLimit, key.Policy.DiagnosticLimit, key.Policy.ValidationIssueLimit, key.Policy.TextByteLimit,
            key.Policy.HeaderByteLimit, key.Policy.StoredObjectByteLimit, key.Policy.ResponseByteLimit, key.Fingerprint.Value];
        foreach (var value in values) insert.Parameters.AddWithValue(value);
        var entry = Assert.IsType<Guid>(await insert.ExecuteScalarAsync());
        await using var claim = new NpgsqlCommand("""
            INSERT INTO weft.revision_derivation_attempts(organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,claimant_instance_id)
            VALUES($1,$2,$3,$4,$5,1,$6,$7) RETURNING derivation_id
            """, connection, transaction);
        PostgresRevisionDerivationTestSupport.AddScope(claim, key.Scope);
        claim.Parameters.AddWithValue(entry); claim.Parameters.AddWithValue(actor.PrincipalId.Value); claim.Parameters.AddWithValue(Guid.NewGuid());
        var derivation = Assert.IsType<Guid>(await claim.ExecuteScalarAsync());
        await using (var advance = new NpgsqlCommand("UPDATE weft.revision_derivations SET current_generation=1,current_attempt_id=$2 WHERE cache_entry_id=$1", connection, transaction))
        { advance.Parameters.AddWithValue(entry); advance.Parameters.AddWithValue(derivation); Assert.Equal(1, await advance.ExecuteNonQueryAsync()); }
        await using (var finish = new NpgsqlCommand("""
            UPDATE weft.revision_derivation_attempts a SET completion_kind='usable',observation_present=true,backend_status='succeeded',
                provenance_confirmed=true,provenance_organization_id=k.organization_id,provenance_project_id=k.project_id,
                provenance_mapping_id=k.mapping_id,provenance_revision_id=k.revision_id,provenance_native_digest=k.native_digest,
                provenance_backend_id=k.backend_id,provenance_engine_revision=k.engine_revision,provenance_backend_build=k.backend_build,
                provenance_adapter_build=k.adapter_build,projection_coverage='complete',document_item_count=3,document_diagnostic_count=0,
                coverage_gap_count=0,operation_diagnostic_count=0,derived_digest=repeat('a',64),derived_byte_count=73
            FROM weft.revision_derivations k WHERE a.derivation_id=$1 AND k.cache_entry_id=a.cache_entry_id
            """, connection, transaction))
        { finish.Parameters.AddWithValue(derivation); Assert.Equal(1, await finish.ExecuteNonQueryAsync()); }
        await using (var select = new NpgsqlCommand("UPDATE weft.revision_derivations SET selected_derivation_id=$2 WHERE cache_entry_id=$1", connection, transaction))
        { select.Parameters.AddWithValue(entry); select.Parameters.AddWithValue(derivation); Assert.Equal(1, await select.ExecuteNonQueryAsync()); }
        await transaction.CommitAsync(); return key;
    }

    private static async Task ProvisionAsync(PostgresCase test, bool writer, bool lifecycle = false)
    {
        if (!Guid.TryParseExact(test.Name["weft_case_".Length..], "N", out _)) throw new InvalidOperationException("The chat upgrade database is not fixture-owned.");
        var role = lifecycle ? "weft_chat_lifecycle" : writer ? "weft_chat_writer" : "weft_chat_reader";
        var config = new NpgsqlConnectionStringBuilder(lifecycle ? test.ChatLifecycleConnectionString : writer ? test.ChatWriterConnectionString : test.ChatReaderConnectionString);
        if (config.Username != role || config.Database != test.Name || string.IsNullOrEmpty(config.Password)) throw new InvalidOperationException("The chat upgrade profile is not fixture-owned.");
        var password = config.Password.Replace("'", "''", StringComparison.Ordinal);
        await test.BootstrapAsync($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS; GRANT CONNECT ON DATABASE {test.Name} TO {role}");
    }
    private static async Task PrefixAsync(PostgresCase test, int count)
    { await using var runner = new MigrationRunner(test.MigrationConnectionString, MigrationRunner.EmbeddedMigrations.Take(count).ToArray()); await runner.MigrateAsync(); }
    private static async Task ConcurrentAsync(PostgresCase test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var calls = Enumerable.Range(0, 3).Select(async _ => { await using var runner = new MigrationRunner(test.MigrationConnectionString); await runner.MigrateAsync(deadline.Token); }).ToArray();
        try { await Task.WhenAll(calls); }
        finally { deadline.Cancel(); foreach (var call in calls) await PostgresServerSessionTestSupport.SettleAsync(call); }
    }
    private static async Task HistoryAsync(PostgresCase test, int count)
    {
        var source = MigrationRunner.EmbeddedMigrations;
        Assert.True(source.Take(10).Select(s => s.Checksum).SequenceEqual(ChatSchemaContract.PrefixChecksums, StringComparer.Ordinal), "The trusted ten-script prefix changed.");
        Assert.True((await test.ScalarAsync<int[]>("SELECT array_agg(version ORDER BY version) FROM weft.schema_migrations")).SequenceEqual(Enumerable.Range(1, count)));
        Assert.True((await test.ScalarAsync<string[]>("SELECT array_agg(checksum ORDER BY version) FROM weft.schema_migrations")).SequenceEqual(source.Take(count).Select(s => s.Checksum), StringComparer.Ordinal));
    }
    private static async Task<byte[]> SnapshotAsync(PostgresCase test, bool installed)
    {
        if (installed)
        {
            // No delta is projected away until exact combined source admission has
            // checked every function, trigger, role and grant. No actor content is read.
            var principal = new PrincipalId(Guid.NewGuid());
            var key = new ChatConversationKey(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), principal, Guid.NewGuid());
            await using var database = new PostgresChatDatabase(test.ChatReaderConnectionString, ChatDatabaseProfile.Reader);
            await using var admitted = await database.BeginAsync(new(principal), key, false, CancellationToken.None);
            await RegressionSchemaTestContract.AdmitAsync(test);
            await using var ingestion = new PostgresIngestionDatabase(test.IngestionConnectionString);
            await using var admittedInterchange = await ingestion.BeginTransactionAsync(Guid.NewGuid());
            await using var display = new PostgresAccountDisplayMetadata(test.AccountDisplayConnectionString);
            Assert.Null(await display.ReadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        }
        if (installed) Assert.True(await CatalogSearchUpgradeTestSupport.AdmitAsync(test));
        await using var connection = PostgresChatSecurityTests.Open(test.MigrationConnectionString); await connection.OpenAsync();
        var text = new StringBuilder();
        var parts = new Dictionary<string, byte[]>();
        foreach (var table in PostgresServerSessionUpgradeConservation.PrefixTables.Append("server_sessions"))
        {
            await using var rows = new NpgsqlCommand($"SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]'::jsonb)::text FROM weft.{table} x" + (table == "schema_migrations" ? " WHERE version<=10" : ""), connection);
            var rowText = Assert.IsType<string>(await rows.ExecuteScalarAsync());
            text.Append(rowText);
            parts["rows:" + table] = SHA256.HashData(Encoding.UTF8.GetBytes(rowText));
        }
        await using var schema = new NpgsqlCommand(SnapshotSql, connection);
        schema.Parameters.AddWithValue(PostgresServerSessionUpgradeConservation.PrefixTables.Append("server_sessions").ToArray());
        schema.Parameters.AddWithValue(installed);
        schema.Parameters.AddWithValue(new[] { ChatDatabaseProfile.Reader, ChatDatabaseProfile.Writer, ChatDatabaseProfile.Lifecycle, ChatDatabaseProfile.Approver }.SelectMany(p => ChatSchemaContract.ColumnGrants(p)
            .Select(g => g + "|" + ChatRoleGuard.RoleName(p))).ToArray());
        schema.Parameters.AddWithValue(ChatSchemaContract.FunctionNames);
        schema.Parameters.AddWithValue(PreviewSchemaTestContract.FunctionNames.Concat(RegressionSchemaTestContract.Functions).Concat(InterchangeSchemaTestContract.Functions).Append("index_catalog_report").ToArray());
        schema.Parameters.AddWithValue(PreviewSchemaTestContract.Roles.Concat(RegressionSchemaTestContract.Roles).Append("weft_account_display").ToArray());
        schema.Parameters.AddWithValue(PreviewSchemaTestContract.ParentForeignKeys.Concat(MappingDraftSchemaTestContract.PreviewParentForeignKeys).ToArray());
        schema.Parameters.AddWithValue(RegressionSchemaTestContract.Tables.Concat(InterchangeSchemaTestContract.Tables).Append("catalog_search_report_index").Append("account_display_metadata").ToArray());
        schema.Parameters.AddWithValue(CatalogSearchSchemaAdmission.IndexNames);
        // 0040 and 0045 each add exactly one named trigger to the old revision_ingestions relation.
        schema.Parameters.AddWithValue(AutomationSchemaAdmission.AllTriggerNames.Append("chat_mapping_write_commit").Append("revision_ingestions_creation_guard").ToArray());
        var schemaText = Assert.IsType<string>(await schema.ExecuteScalarAsync());
        text.Append(schemaText);
        using var document = JsonDocument.Parse(schemaText);
        foreach (var property in document.RootElement.EnumerateObject()) parts["schema:" + property.Name] = SHA256.HashData(Encoding.UTF8.GetBytes(property.Value.GetRawText()));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        SnapshotParts.Add(digest, parts);
        return digest;
    }
    private static void Same(byte[] before, byte[] after)
    {
        var changed = SnapshotParts.TryGetValue(before, out var oldParts) && SnapshotParts.TryGetValue(after, out var newParts)
            ? string.Join(", ", oldParts.Keys.Where(key => !CryptographicOperations.FixedTimeEquals(oldParts[key], newParts[key]))) : "unavailable";
        Assert.True(CryptographicOperations.FixedTimeEquals(before, after), "Chat migration changed historical rows, schema, authority or grants outside its exact additive delta: " + changed);
    }

    // Migration 0023 deliberately appends the publication store kind with its grants and
    // replaces the publication format check and guard body; maintenance admission pins
    // those exact objects, so only they are projected out of the conserved prefix.
    private const string SnapshotSql = """
        WITH old_relations AS (SELECT * FROM pg_catalog.pg_class WHERE relnamespace='weft'::regnamespace AND relname=ANY($1::text[])),
        new_roles AS (SELECT oid,rolname FROM pg_catalog.pg_roles WHERE rolname IN ('weft_chat_reader','weft_chat_writer','weft_chat_lifecycle','weft_chat_approver')),
        expected_columns AS (SELECT split_part(x,'|',1) tab,split_part(x,'|',2) col,split_part(x,'|',3) privilege,split_part(x,'|',4) role FROM unnest($3::text[]) x),
        columns AS (SELECT r.relname,a.* FROM old_relations r JOIN pg_catalog.pg_attribute a ON a.attrelid=r.oid AND a.attnum>0
            WHERE NOT ($2 AND r.relname='private_object_publications' AND a.attname='storage_kind')),
        column_acls AS (SELECT c.relname,c.attname,x.* FROM columns c CROSS JOIN LATERAL pg_catalog.aclexplode(c.attacl) x),
        retained_column_acls AS (SELECT a.* FROM column_acls a WHERE NOT ($2 AND EXISTS(SELECT 1 FROM expected_columns e
            JOIN new_roles nr ON nr.rolname=e.role JOIN old_relations r ON r.relname=e.tab
            WHERE a.relname=e.tab AND a.attname=e.col AND a.privilege_type=e.privilege AND a.grantee=nr.oid AND a.grantor=r.relowner AND NOT a.is_grantable))),
        retained_triggers AS (SELECT t.* FROM pg_catalog.pg_trigger t JOIN old_relations r ON r.oid=t.tgrelid
            WHERE NOT ($2 AND t.tgname=ANY($10::text[]))
            AND NOT ($2 AND t.tgisinternal AND EXISTS(SELECT 1 FROM pg_catalog.pg_constraint c
                WHERE c.oid=t.tgconstraint AND c.contype='f' AND (c.conrelid=pg_catalog.to_regclass('weft.chat_conversations')
                    AND c.conname IN ('chat_conversations_creator_fkey','chat_conversations_revision_fkey','chat_conversations_projection_fkey','chat_conversations_validation_fkey','chat_conversations_focus_mapping_fkey','chat_conversations_focus_revision_fkey')
                    OR c.conrelid=pg_catalog.to_regclass('weft.preview_runs') AND c.conname=ANY($7::text[])
                    OR c.conrelid IN (SELECT pg_catalog.to_regclass('weft.' || suffix) FROM unnest($8::text[]) suffix))))),
        namespace_acls AS (SELECT a.* FROM pg_catalog.pg_namespace n CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) a
            WHERE n.nspname='weft' AND NOT ($2 AND a.grantee IN (SELECT oid FROM new_roles UNION ALL SELECT oid FROM pg_catalog.pg_roles WHERE rolname=ANY($6::text[]))
                AND a.grantor=n.nspowner AND a.privilege_type='USAGE' AND NOT a.is_grantable))
        SELECT jsonb_build_object(
            'relations',(SELECT coalesce(jsonb_agg(jsonb_build_array(oid,relname,relkind,relpersistence,relowner,
                relnatts - CASE WHEN $2 AND relname='private_object_publications' THEN 1 ELSE 0 END,
                relacl,relrowsecurity,relforcerowsecurity,reloptions,relreplident) ORDER BY relname),'[]'::jsonb) FROM old_relations),
            'columns',(SELECT coalesce(jsonb_agg(to_jsonb(c)-'attacl' ORDER BY relname,attnum),'[]'::jsonb) FROM columns c),
            'columnAcls',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY relname,attname,grantor,grantee,privilege_type),'[]'::jsonb) FROM retained_column_acls a),
            'constraints',(SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.oid),'[]'::jsonb) FROM pg_catalog.pg_constraint c WHERE c.conrelid IN (SELECT oid FROM old_relations)
                AND NOT (c.conrelid=pg_catalog.to_regclass('weft.private_object_publications') AND c.conname='private_object_publications_format_check')),
            'triggers',(SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY t.oid),'[]'::jsonb) FROM retained_triggers t),
            'indexes',(SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY i.indexrelid),'[]'::jsonb) FROM pg_catalog.pg_index i
                JOIN pg_catalog.pg_class r ON r.oid=i.indexrelid WHERE i.indrelid IN (SELECT oid FROM old_relations)
                AND NOT ($2 AND r.relname=ANY($9::text[]))),
            'indexOwners',(SELECT coalesce(jsonb_agg(jsonb_build_array(r.oid,r.relname,r.relowner,r.reloptions,r.relkind,r.relpersistence) ORDER BY r.oid),'[]'::jsonb)
                FROM pg_catalog.pg_class r WHERE r.oid IN (SELECT indexrelid FROM pg_catalog.pg_index WHERE indrelid IN (SELECT oid FROM old_relations))
                AND NOT ($2 AND r.relname=ANY($9::text[]))),
            'columnDefaults',(SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d.oid),'[]'::jsonb) FROM pg_catalog.pg_attrdef d WHERE d.adrelid IN (SELECT oid FROM old_relations)),
            'rules',(SELECT coalesce(jsonb_agg(to_jsonb(w) ORDER BY w.oid),'[]'::jsonb) FROM pg_catalog.pg_rewrite w WHERE w.ev_class IN (SELECT oid FROM old_relations)),
            'inherits',(SELECT coalesce(jsonb_agg(to_jsonb(h) ORDER BY inhrelid,inhseqno),'[]'::jsonb) FROM pg_catalog.pg_inherits h WHERE h.inhrelid IN (SELECT oid FROM old_relations) OR h.inhparent IN (SELECT oid FROM old_relations)),
            'policies',(SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.oid),'[]'::jsonb) FROM pg_catalog.pg_policy p WHERE p.polrelid IN (SELECT oid FROM old_relations)),
            'functions',(SELECT coalesce(jsonb_agg(to_jsonb(f) ORDER BY f.oid),'[]'::jsonb) FROM pg_catalog.pg_proc f WHERE f.pronamespace='weft'::regnamespace
                AND f.proname<>'guard_private_object_publications_v1' AND NOT ($2 AND (f.proname=ANY($4::text[]) OR f.proname=ANY($5::text[])))),
            'namespaceAcls',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY grantor,grantee,privilege_type),'[]'::jsonb) FROM namespace_acls a),
            'roles',(SELECT coalesce(jsonb_agg(to_jsonb(r)-'rolpassword' ORDER BY r.oid),'[]'::jsonb) FROM pg_catalog.pg_roles r WHERE r.rolname LIKE 'weft_%'),
            'memberships',(SELECT coalesce(jsonb_agg(to_jsonb(m) ORDER BY roleid,member,grantor),'[]'::jsonb) FROM pg_catalog.pg_auth_members m),
            'settings',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY setdatabase,setrole),'[]'::jsonb) FROM pg_catalog.pg_db_role_setting s),
            'defaults',(SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d.oid),'[]'::jsonb) FROM pg_catalog.pg_default_acl d),
            'parameters',(SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.oid),'[]'::jsonb) FROM pg_catalog.pg_parameter_acl p),
            'databaseAcl',(SELECT jsonb_build_array(datdba,datacl::text) FROM pg_catalog.pg_database WHERE datname=current_database()),
            'namespaceOwner',(SELECT nspowner FROM pg_catalog.pg_namespace WHERE nspname='weft')
        )::text
        """;
}
