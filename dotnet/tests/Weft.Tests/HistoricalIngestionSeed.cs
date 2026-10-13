using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using Weft.Persistence.Postgres;

namespace Weft.Tests;

/// <summary>
/// Owner-only fixtures for the unchanged 0002 schema. Receipts contain synthetic C1
/// admission evidence; this helper invokes neither private storage nor a native worker.
/// It is deliberately separate from current product schema admission and persistence.
/// </summary>
internal static class HistoricalIngestionSeed
{
    private const string OwnershipChecksum = "0a6c95e1357d9fba7736dd5870895a11443b0e924989163675f00ba2e858f9d1";
    private const string IngestionChecksum = "d61a8bc2d56b6a39607414cf0f14c58db511594369ac124fd270a08f77455ae5";
    private const long MigrationLock = 6287392987644524871;
    private const int MaximumTableRows = 64;
    private const int MaximumJournalRows = 16;
    private const int MaximumSchemaRows = 512;
    private const int MaximumSnapshotBytes = 1024 * 1024;
    private static readonly (string Name, string Order)[] Tables =
    [
        ("organizations", "id"),
        ("principals", "id"),
        ("organization_memberships", "organization_id,principal_id"),
        ("projects", "organization_id,id"),
        ("project_memberships", "organization_id,project_id,principal_id"),
        ("mappings", "organization_id,project_id,id"),
        ("revision_ingestions", "organization_id,project_id,operation_id"),
        ("revision_ingestion_attempts", "organization_id,project_id,operation_id,generation"),
        ("mapping_revisions", "organization_id,project_id,mapping_id,id")
    ];
    private static readonly string[] GuardTables =
        ["mappings", "revision_ingestions", "revision_ingestion_attempts", "mapping_revisions"];

    internal static Task<IngestionReceipt> CommitCreateAsync(PostgresCase test, ActorContext actor,
        ProjectScope project, string name = "Synthetic catalog mapping",
        string description = "Synthetic C1 admission evidence only", bool valid = true,
        string? filename = null, CancellationToken cancellationToken = default) =>
        CommitAsync(test, actor, project, PostgresIngestionTestSupport.CreateIntent(name, description, filename),
            valid, cancellationToken);

    internal static Task<IngestionReceipt> CommitAppendAsync(PostgresCase test, IngestionReceipt mapping,
        bool valid = true, string? filename = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Phase != IngestionPhase.Committed || mapping.CommittedRevision is null)
            throw new ArgumentException("A historical append requires a committed receipt.", nameof(mapping));
        var facts = mapping.Facts;
        var scope = facts.FutureRevision;
        var body = PostgresIngestionTestSupport.SyntheticBody;
        var intent = IngestionIntent.ForAppend(new(scope.OrganizationId, scope.ProjectId, scope.MappingId),
            facts.BackendProfile.BackendId, filename, new AnyHead(), Sha256Digest.FromBytes(body), body.LongLength);
        return CommitAsync(test, new(facts.OriginalActor), facts.Project, intent, valid, cancellationToken);
    }

    private static async Task<IngestionReceipt> CommitAsync(PostgresCase test, ActorContext actor,
        ProjectScope project, IngestionIntent intent, bool valid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(project);
        await using var connection = await test.Owner.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ConfigureAsync(connection, transaction, readOnly: false, cancellationToken);
        await using (var migrationLock = Command(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)"))
        {
            Add(migrationLock, "key", NpgsqlDbType.Bigint, MigrationLock);
            await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await RequireOriginalSchemaAsync(connection, transaction, cancellationToken);

        var operation = Guid.NewGuid();
        var key = Guid.NewGuid();
        var revision = new RevisionScope(project.OrganizationId, project.ProjectId,
            intent.Target?.MappingId ?? Guid.NewGuid(), Guid.NewGuid());
        var native = new PrivateObjectReference(PrivateObjectId.Create(revision, PrivateObjectClass.NativeArtifact),
            intent.NativeDigest, intent.NativeByteCount);
        var fingerprint = CanonicalFingerprint.Create(actor.PrincipalId, project, intent);
        var profile = PostgresIngestionTestSupport.Profile;
        var policy = PostgresIngestionTestSupport.Policy;
        DateTimeOffset reservedAt;
        await using (var reserve = Command(connection, transaction, """
            INSERT INTO weft.revision_ingestions
            (organization_id,project_id,operation_id,actor_principal_id,operation_kind,idempotency_key,
             fingerprint_version,intent_fingerprint,target_mapping_id,future_mapping_id,future_revision_id,
             native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,
             dependency_manifest_id,backend_id,engine_revision,backend_build,adapter_build,admission_policy_id,
             effective_native_byte_limit,display_filename,create_name,create_description,head_condition,
             expected_head_revision_id,phase,lease_generation,claim_count)
            VALUES (@organization,@project,@operation,@actor,@kind,@key,1,@fingerprint,@target,@mapping,@revision,
                    @object,'native',@digest,@bytes,@document,@dependencies,@backend,@engine,@build,@adapter,
                    @policy,@limit,@filename,@name,@description,'any',NULL,'reserved',0,0)
            RETURNING created_at
            """))
        {
            AddScope(reserve, project, operation);
            Add(reserve, "actor", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(reserve, "kind", NpgsqlDbType.Text, intent.Kind == IngestionOperationKind.Create ? "create" : "append");
            Add(reserve, "key", NpgsqlDbType.Uuid, key);
            Add(reserve, "fingerprint", NpgsqlDbType.Text, fingerprint.Digest.Value);
            Add(reserve, "target", NpgsqlDbType.Uuid, intent.Target?.MappingId);
            Add(reserve, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(reserve, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            Add(reserve, "object", NpgsqlDbType.Uuid, native.Id.ObjectId);
            Add(reserve, "digest", NpgsqlDbType.Text, native.Digest.Value);
            Add(reserve, "bytes", NpgsqlDbType.Bigint, native.ByteCount);
            Add(reserve, "document", NpgsqlDbType.Text, intent.LogicalDocument);
            Add(reserve, "dependencies", NpgsqlDbType.Text, intent.DependencyIdentity);
            Add(reserve, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(reserve, "engine", NpgsqlDbType.Text, profile.EngineRevision);
            Add(reserve, "build", NpgsqlDbType.Text, profile.BackendBuild);
            Add(reserve, "adapter", NpgsqlDbType.Text, profile.AdapterBuild);
            Add(reserve, "policy", NpgsqlDbType.Text, policy.Identity);
            Add(reserve, "limit", NpgsqlDbType.Bigint, policy.EffectiveNativeByteLimit);
            Add(reserve, "filename", NpgsqlDbType.Text, intent.OriginalFilename);
            Add(reserve, "name", NpgsqlDbType.Text, intent.CreateMetadata?.Name);
            Add(reserve, "description", NpgsqlDbType.Text, intent.CreateMetadata?.Description);
            reservedAt = await ReadTimestampAsync(reserve, cancellationToken);
        }
        var facts = new ReservedIngestionFacts(project, operation, actor.PrincipalId, key, fingerprint, intent,
            revision, native, profile, policy, reservedAt);
        var instance = Guid.NewGuid();
        var token = Guid.NewGuid();
        DateTimeOffset startedAt;
        // INSERT-time guards must see an empty attempt before any claim/stage/completion updates.
        await using (var attempt = Command(connection, transaction, """
            INSERT INTO weft.revision_ingestion_attempts
            (organization_id,project_id,operation_id,generation,claimant_instance_id,claim_token)
            VALUES (@organization,@project,@operation,1,@instance,@token) RETURNING started_at
            """))
        {
            AddScope(attempt, project, operation);
            Add(attempt, "instance", NpgsqlDbType.Uuid, instance);
            Add(attempt, "token", NpgsqlDbType.Uuid, token);
            startedAt = await ReadTimestampAsync(attempt, cancellationToken);
        }
        await using (var claim = Command(connection, transaction, """
            UPDATE weft.revision_ingestions SET lease_owner_id=@instance,lease_token=@token,
                lease_generation=1,claim_count=1,lease_expires_at=@started + interval '120 seconds'
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND phase='reserved' AND lease_generation=0
            """))
        {
            AddScope(claim, project, operation);
            Add(claim, "instance", NpgsqlDbType.Uuid, instance);
            Add(claim, "token", NpgsqlDbType.Uuid, token);
            Add(claim, "started", NpgsqlDbType.TimestampTz, startedAt);
            await RequireOneAsync(claim, cancellationToken);
        }

        DateTimeOffset stagedAt;
        await using (var stage = Command(connection, transaction, """
            UPDATE weft.revision_ingestion_attempts SET verified_stage_at=clock_timestamp()
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND generation=1 AND claimant_instance_id=@instance AND claim_token=@token
              AND verified_stage_at IS NULL AND finished_at IS NULL
            RETURNING verified_stage_at
            """))
        {
            AddScope(stage, project, operation);
            Add(stage, "instance", NpgsqlDbType.Uuid, instance);
            Add(stage, "token", NpgsqlDbType.Uuid, token);
            stagedAt = await ReadTimestampAsync(stage, cancellationToken);
        }
        await using (var stage = FencedCommand(connection, transaction, facts, instance, token, """
            UPDATE weft.revision_ingestions SET phase='staged',verified_stage_generation=1,verified_stage_at=@staged
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND phase='reserved' AND lease_generation=1 AND lease_owner_id=@instance AND lease_token=@token
              AND lease_expires_at>clock_timestamp()
            """))
        {
            Add(stage, "staged", NpgsqlDbType.TimestampTz, stagedAt);
            await RequireOneAsync(stage, cancellationToken);
        }

        var completion = PostgresIngestionTestSupport.SyntheticCompletion(facts, valid);
        var evidence = completion.Evidence!;
        var classification = completion.Classify(facts);
        var admission = valid ? "eligible_valid" : "eligible_decoded_invalid";
        DateTimeOffset finishedAt;
        await using (var complete = Command(connection, transaction, """
            UPDATE weft.revision_ingestion_attempts SET finished_at=clock_timestamp(),completion_kind='validation',
                backend_status=@status,admission_classification=@admission,provenance_confirmed=true,
                provenance_organization_id=@organization,provenance_project_id=@project,
                provenance_mapping_id=@mapping,provenance_revision_id=@revision,provenance_native_digest=@digest,
                provenance_backend_id=@backend,provenance_engine_revision=@engine,provenance_backend_build=@build,
                provenance_adapter_build=@adapter,is_valid=@valid,total_issue_count=0,emitted_issue_count=0,
                issues_truncated=false,evidence_version=@version,evidence=@evidence
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND generation=1 AND claimant_instance_id=@instance AND claim_token=@token
              AND verified_stage_at IS NOT NULL AND finished_at IS NULL
            RETURNING finished_at,evidence::text
            """))
        {
            AddScope(complete, project, operation);
            Add(complete, "instance", NpgsqlDbType.Uuid, instance);
            Add(complete, "token", NpgsqlDbType.Uuid, token);
            Add(complete, "status", NpgsqlDbType.Text, IngestionPersistenceNames.BackendStatus(evidence.Facts.Status));
            Add(complete, "admission", NpgsqlDbType.Text, admission);
            Add(complete, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(complete, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            Add(complete, "digest", NpgsqlDbType.Text, native.Digest.Value);
            Add(complete, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(complete, "engine", NpgsqlDbType.Text, profile.EngineRevision);
            Add(complete, "build", NpgsqlDbType.Text, profile.BackendBuild);
            Add(complete, "adapter", NpgsqlDbType.Text, profile.AdapterBuild);
            Add(complete, "valid", NpgsqlDbType.Boolean, valid);
            Add(complete, "version", NpgsqlDbType.Text, evidence.Version);
            Add(complete, "evidence", NpgsqlDbType.Jsonb, Encoding.UTF8.GetString(evidence.CopyUtf8()));
            await using var reader = await complete.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw InvalidTransition();
            finishedAt = reader.GetFieldValue<DateTimeOffset>(0);
            // Return the persisted derived JSONB bytes, not its pre-storage key order/spacing.
            completion = IngestionAttemptCompletion.FromValidation(IngestionEvidenceDocument.Restore(evidence.Facts,
                Encoding.UTF8.GetBytes(reader.GetString(1))));
            if (await reader.ReadAsync(cancellationToken))
                throw InvalidTransition();
        }
        await using (var ready = FencedCommand(connection, transaction, facts, instance, token, """
            UPDATE weft.revision_ingestions SET phase='ready',eligible_attempt_generation=1,eligible_admission=@admission
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND phase='staged' AND verified_stage_generation=1 AND lease_generation=1
              AND lease_owner_id=@instance AND lease_token=@token AND lease_expires_at>clock_timestamp()
            """))
        {
            Add(ready, "admission", NpgsqlDbType.Text, admission);
            await RequireOneAsync(ready, cancellationToken);
        }

        long sequence = 1;
        if (intent.Kind == IngestionOperationKind.Append)
        {
            await using var head = Command(connection, transaction, """
                SELECT head_sequence FROM weft.mappings
                WHERE organization_id=@organization AND project_id=@project AND id=@mapping AND backend_id=@backend
                FOR UPDATE
                """);
            AddScope(head, project, operation);
            Add(head, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(head, "backend", NpgsqlDbType.Text, profile.BackendId);
            var current = await head.ExecuteScalarAsync(cancellationToken);
            if (current is not long previous || previous == long.MaxValue)
                throw InvalidTransition();
            sequence = checked(previous + 1);
        }
        DateTimeOffset publishedAt;
        await using (var fence = FencedCommand(connection, transaction, facts, instance, token, """
            SELECT clock_timestamp() FROM weft.revision_ingestions
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND phase='ready' AND verified_stage_generation=1 AND eligible_attempt_generation=1
              AND lease_generation=1 AND lease_owner_id=@instance AND lease_token=@token
              AND lease_expires_at>clock_timestamp()
            """))
            publishedAt = await ReadTimestampAsync(fence, cancellationToken);
        if (intent.Kind == IngestionOperationKind.Create)
        {
            await using var mapping = Command(connection, transaction, """
                INSERT INTO weft.mappings
                (organization_id,project_id,id,name,description,backend_id,creator_principal_id,owner_principal_id,
                 created_at,updated_at,version,head_revision_id,head_sequence)
                VALUES (@organization,@project,@mapping,@name,@description,@backend,@actor,@actor,@published,@published,1,@revision,1)
                """);
            AddScope(mapping, project, operation);
            Add(mapping, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(mapping, "name", NpgsqlDbType.Text, intent.CreateMetadata!.Name);
            Add(mapping, "description", NpgsqlDbType.Text, intent.CreateMetadata.Description);
            Add(mapping, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(mapping, "actor", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(mapping, "published", NpgsqlDbType.TimestampTz, publishedAt);
            Add(mapping, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            await RequireOneAsync(mapping, cancellationToken);
        }
        await using (var insertRevision = Command(connection, transaction, """
            INSERT INTO weft.mapping_revisions
            (organization_id,project_id,mapping_id,id,sequence,native_object_id,native_class,native_digest,
             native_byte_count,logical_entry_document,dependency_manifest_id,backend_id,creator_principal_id,
             committed_at,ingestion_operation_id,initial_attempt_generation,initial_admission)
            VALUES (@organization,@project,@mapping,@revision,@sequence,@object,'native',@digest,@bytes,
                    @document,@dependencies,@backend,@actor,@published,@operation,1,@admission)
            """))
        {
            AddScope(insertRevision, project, operation);
            Add(insertRevision, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(insertRevision, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            Add(insertRevision, "sequence", NpgsqlDbType.Bigint, sequence);
            Add(insertRevision, "object", NpgsqlDbType.Uuid, native.Id.ObjectId);
            Add(insertRevision, "digest", NpgsqlDbType.Text, native.Digest.Value);
            Add(insertRevision, "bytes", NpgsqlDbType.Bigint, native.ByteCount);
            Add(insertRevision, "document", NpgsqlDbType.Text, intent.LogicalDocument);
            Add(insertRevision, "dependencies", NpgsqlDbType.Text, intent.DependencyIdentity);
            Add(insertRevision, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(insertRevision, "actor", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
            Add(insertRevision, "published", NpgsqlDbType.TimestampTz, publishedAt);
            Add(insertRevision, "admission", NpgsqlDbType.Text, admission);
            await RequireOneAsync(insertRevision, cancellationToken);
        }
        if (intent.Kind == IngestionOperationKind.Append)
        {
            await using var advance = Command(connection, transaction, """
                UPDATE weft.mappings SET head_revision_id=@revision,head_sequence=@sequence,
                    updated_at=GREATEST(updated_at,@published)
                WHERE organization_id=@organization AND project_id=@project AND id=@mapping
                  AND backend_id=@backend AND head_sequence=@previous
                """);
            AddScope(advance, project, operation);
            Add(advance, "mapping", NpgsqlDbType.Uuid, revision.MappingId);
            Add(advance, "backend", NpgsqlDbType.Text, profile.BackendId);
            Add(advance, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            Add(advance, "sequence", NpgsqlDbType.Bigint, sequence);
            Add(advance, "previous", NpgsqlDbType.Bigint, sequence - 1);
            Add(advance, "published", NpgsqlDbType.TimestampTz, publishedAt);
            await RequireOneAsync(advance, cancellationToken);
        }
        await using (var finalize = FencedCommand(connection, transaction, facts, instance, token, """
            UPDATE weft.revision_ingestions SET phase='committed',completed_at=@published,
                committed_revision_id=@revision,lease_owner_id=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE organization_id=@organization AND project_id=@project AND operation_id=@operation
              AND phase='ready' AND verified_stage_generation=1 AND eligible_attempt_generation=1
              AND lease_generation=1 AND lease_owner_id=@instance AND lease_token=@token
              AND lease_expires_at>clock_timestamp()
            """))
        {
            Add(finalize, "published", NpgsqlDbType.TimestampTz, publishedAt);
            Add(finalize, "revision", NpgsqlDbType.Uuid, revision.RevisionId);
            await RequireOneAsync(finalize, cancellationToken);
        }
        await using (var constraints = Command(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE"))
            await constraints.ExecuteNonQueryAsync(cancellationToken);
        var committed = new CommittedRevisionFacts(revision, sequence, native, profile, intent.LogicalDocument,
            intent.DependencyIdentity, actor.PrincipalId, publishedAt, operation, 1, classification);
        var receipt = new IngestionReceipt(facts, IngestionPhase.Committed, 1, 1, 1, stagedAt, 1,
            [new IngestionAttemptReceipt(1, startedAt, finishedAt, completion, classification)], committed, publishedAt);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static async Task RequireOriginalSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var scripts = MigrationRunner.EmbeddedMigrations;
        if (scripts.Single(script => script.Version == 1).Checksum != OwnershipChecksum ||
            scripts.Single(script => script.Version == 2).Checksum != IngestionChecksum)
            throw new InvalidOperationException("The historical fixture requires the literal original migration bytes.");
        await using (var identity = Command(connection, transaction, """
            SELECT current_user='weft_migrator' AND session_user=current_user AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_catalog.pg_roles WHERE rolname=current_user
            """))
        {
            if (await identity.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("The historical fixture requires its unprivileged migration owner.");
        }
        await using (var history = Command(connection, transaction,
            "SELECT version,checksum FROM weft.schema_migrations ORDER BY version LIMIT 3"))
        {
            await using var reader = await history.ExecuteReaderAsync(cancellationToken);
            foreach (var (version, checksum) in new[] { (1, OwnershipChecksum), (2, IngestionChecksum) })
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != version || reader.GetString(1) != checksum)
                    throw OriginalSchemaRequired();
            }
            if (await reader.ReadAsync(cancellationToken))
                throw OriginalSchemaRequired();
        }
        var originalSql = scripts.Single(script => script.Version == 2).Sql;
        foreach (var table in GuardTables)
        {
            var function = $"guard_{table}_transition";
            var match = Regex.Match(originalSql,
                $@"CREATE FUNCTION weft\.{function}\(\).*? AS \$guard\$(?<body>.*?)\$guard\$;",
                RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success)
                throw new InvalidOperationException("The pinned historical guard body was not found.");
            await using var guard = Command(connection, transaction, """
                SELECT count(*)=1 AND bool_and(
                    table_owner.rolname='weft_migrator' AND trigger.tgname=@trigger AND trigger.tgtype=31
                    AND trigger.tgenabled='O' AND trigger.tgnargs=0 AND trigger.tgattr::text=''
                    AND trigger.tgqual IS NULL AND NOT trigger.tgdeferrable AND NOT trigger.tginitdeferred
                    AND trigger.tgoldtable IS NULL AND trigger.tgnewtable IS NULL
                    AND function_namespace.nspname='weft' AND function.proname=@function
                    AND function_owner.rolname='weft_migrator' AND language.lanname='plpgsql'
                    AND NOT function.prosecdef AND function.proconfig=ARRAY['search_path=pg_catalog, weft']::text[]
                    AND function.prosrc=@body AND function.pronargs=0
                    AND function.prorettype='pg_catalog.trigger'::regtype AND NOT function.proretset
                    AND function.prokind='f' AND function.provolatile='v' AND function.proparallel='u'
                    AND NOT function.proisstrict AND NOT function.proleakproof)
                FROM pg_catalog.pg_class relation
                JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
                JOIN pg_catalog.pg_roles table_owner ON table_owner.oid=relation.relowner
                JOIN pg_catalog.pg_trigger trigger ON trigger.tgrelid=relation.oid AND NOT trigger.tgisinternal
                JOIN pg_catalog.pg_proc function ON function.oid=trigger.tgfoid
                JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid=function.pronamespace
                JOIN pg_catalog.pg_roles function_owner ON function_owner.oid=function.proowner
                JOIN pg_catalog.pg_language language ON language.oid=function.prolang
                WHERE namespace.nspname='weft' AND relation.relname=@table
                """);
            Add(guard, "table", NpgsqlDbType.Text, table);
            Add(guard, "trigger", NpgsqlDbType.Text, $"{table}_transition_guard");
            Add(guard, "function", NpgsqlDbType.Text, function);
            Add(guard, "body", NpgsqlDbType.Text, match.Groups["body"].Value);
            if (await guard.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("The historical fixture requires all original enabled transition guards.");
        }
    }

    /// <summary>Bounded, complete owner facts/schema evidence, readable on both old and current schemas.</summary>
    internal static async Task<HistoricalIngestionOwnerSnapshot> CaptureSnapshotAsync(PostgresCase test,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(test);
        await using var connection = await test.Owner.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await ConfigureAsync(connection, transaction, readOnly: true, cancellationToken);
        var budget = new SnapshotBudget();
        using var facts = new SnapshotBuffer(budget);
        using (var writer = new Utf8JsonWriter(facts))
        {
            writer.WriteStartObject();
            foreach (var (name, order) in Tables)
                await WriteRowsAsync(writer, connection, transaction, name,
                    $"SELECT to_jsonb(row) AS value FROM weft.{name} row ORDER BY {order}", MaximumTableRows, cancellationToken);
            writer.WriteEndObject();
        }
        using var schema = new SnapshotBuffer(budget);
        using (var writer = new Utf8JsonWriter(schema))
        {
            writer.WriteStartObject();
            await WriteRowsAsync(writer, connection, transaction, "journal",
                "SELECT to_jsonb(row) AS value FROM weft.schema_migrations row ORDER BY version", MaximumJournalRows, cancellationToken);
            foreach (var (name, sql) in SchemaQueries)
                await WriteRowsAsync(writer, connection, transaction, name, sql, MaximumSchemaRows, cancellationToken);
            writer.WriteEndObject();
        }
        await transaction.CommitAsync(cancellationToken);
        return new(Encoding.UTF8.GetString(facts.ToArray()), Encoding.UTF8.GetString(schema.ToArray()));
    }

    private static readonly (string Name, string Sql)[] SchemaQueries =
    [
        ("schemas", """
            SELECT jsonb_build_object('name',nspname,'owner',pg_get_userbyid(nspowner),
                'acl',ARRAY(SELECT entry::text FROM unnest(nspacl) entry ORDER BY entry::text)) AS value
            FROM pg_catalog.pg_namespace WHERE nspname='weft' ORDER BY nspname
            """),
        ("relations", """
            SELECT jsonb_build_object('name',relation.relname,'kind',relation.relkind,
                'owner',pg_get_userbyid(relation.relowner),'rowSecurity',relation.relrowsecurity,
                'forceRowSecurity',relation.relforcerowsecurity,
                'acl',ARRAY(SELECT entry::text FROM unnest(relation.relacl) entry ORDER BY entry::text)) AS value
            FROM pg_catalog.pg_class relation JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='weft' ORDER BY relation.relname
            """),
        ("columns", """
            SELECT jsonb_build_object('table',relation.relname,'number',attribute.attnum,'name',attribute.attname,
                'type',format_type(attribute.atttypid,attribute.atttypmod),'notNull',attribute.attnotnull,
                'identity',attribute.attidentity,'generated',attribute.attgenerated,
                'default',pg_get_expr(defaults.adbin,defaults.adrelid),
                'acl',ARRAY(SELECT entry::text FROM unnest(attribute.attacl) entry ORDER BY entry::text)) AS value
            FROM pg_catalog.pg_attribute attribute JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef defaults ON defaults.adrelid=relation.oid AND defaults.adnum=attribute.attnum
            WHERE namespace.nspname='weft' AND relation.relkind IN ('r','p','v','m','f')
              AND attribute.attnum>0 AND NOT attribute.attisdropped ORDER BY relation.relname,attribute.attnum
            """),
        ("functions", """
            SELECT jsonb_build_object('name',function.proname,'identityArguments',pg_get_function_identity_arguments(function.oid),
                'owner',pg_get_userbyid(function.proowner),'language',language.lanname,
                'definition',pg_get_functiondef(function.oid),'source',function.prosrc,'config',function.proconfig,
                'securityDefiner',function.prosecdef,'kind',function.prokind,'volatility',function.provolatile,
                'parallel',function.proparallel,'strict',function.proisstrict,'leakproof',function.proleakproof,
                'acl',ARRAY(SELECT entry::text FROM unnest(function.proacl) entry ORDER BY entry::text)) AS value
            FROM pg_catalog.pg_proc function JOIN pg_catalog.pg_namespace namespace ON namespace.oid=function.pronamespace
            JOIN pg_catalog.pg_language language ON language.oid=function.prolang
            WHERE namespace.nspname='weft' ORDER BY function.proname,pg_get_function_identity_arguments(function.oid)
            """),
        ("triggers", """
            SELECT jsonb_build_object('table',relation.relname,'name',trigger.tgname,'type',trigger.tgtype,
                'enabled',trigger.tgenabled,'argumentCount',trigger.tgnargs,'arguments',encode(trigger.tgargs,'hex'),
                'columns',trigger.tgattr::text,'condition',pg_get_expr(trigger.tgqual,trigger.tgrelid),
                'deferrable',trigger.tgdeferrable,'initiallyDeferred',trigger.tginitdeferred,
                'oldTable',trigger.tgoldtable,'newTable',trigger.tgnewtable,
                'function',trigger.tgfoid::regprocedure::text,'definition',pg_get_triggerdef(trigger.oid)) AS value
            FROM pg_catalog.pg_trigger trigger JOIN pg_catalog.pg_class relation ON relation.oid=trigger.tgrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='weft' AND NOT trigger.tgisinternal ORDER BY relation.relname,trigger.tgname
            """),
        ("constraints", """
            SELECT jsonb_build_object('table',relation.relname,'name',constraint_row.conname,'type',constraint_row.contype,
                'deferrable',constraint_row.condeferrable,'initiallyDeferred',constraint_row.condeferred,
                'validated',constraint_row.convalidated,'definition',pg_get_constraintdef(constraint_row.oid)) AS value
            FROM pg_catalog.pg_constraint constraint_row JOIN pg_catalog.pg_class relation ON relation.oid=constraint_row.conrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='weft' ORDER BY relation.relname,constraint_row.conname
            """),
        ("policies", """
            SELECT jsonb_build_object('table',relation.relname,'name',policy.polname,'command',policy.polcmd,
                'permissive',policy.polpermissive,
                'roles',ARRAY(SELECT CASE WHEN role_id=0 THEN 'PUBLIC' ELSE pg_get_userbyid(role_id) END
                    FROM unnest(policy.polroles) role_id ORDER BY role_id),
                'using',pg_get_expr(policy.polqual,policy.polrelid),'check',pg_get_expr(policy.polwithcheck,policy.polrelid)) AS value
            FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class relation ON relation.oid=policy.polrelid
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
            WHERE namespace.nspname='weft' ORDER BY relation.relname,policy.polname
            """),
        ("defaultAcls", """
            SELECT jsonb_build_object('owner',pg_get_userbyid(default_acl.defaclrole),
                'schema',namespace.nspname,'kind',default_acl.defaclobjtype,
                'acl',ARRAY(SELECT entry::text FROM unnest(default_acl.defaclacl) entry ORDER BY entry::text)) AS value
            FROM pg_catalog.pg_default_acl default_acl
            LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=default_acl.defaclnamespace
            WHERE namespace.nspname='weft' OR (default_acl.defaclnamespace=0 AND pg_get_userbyid(default_acl.defaclrole)='weft_migrator')
            ORDER BY pg_get_userbyid(default_acl.defaclrole),namespace.nspname,default_acl.defaclobjtype
            """)
    ];

    private static async Task WriteRowsAsync(Utf8JsonWriter writer, NpgsqlConnection connection,
        NpgsqlTransaction transaction, string name, string sql, int maximumRows, CancellationToken cancellationToken)
    {
        // All identifiers/order clauses above are fixed fixture allowlists. Bound rows before serialization,
        // and refuse a large encoded row before sending its JSON text to the client.
        await using var command = Command(connection, transaction, $"""
            SELECT CASE WHEN octet_length(value::text)<=@maximumBytes THEN value::text ELSE NULL END
            FROM ({sql} LIMIT @rowLimit) bounded
            """);
        Add(command, "maximumBytes", NpgsqlDbType.Integer, MaximumSnapshotBytes);
        Add(command, "rowLimit", NpgsqlDbType.Integer, maximumRows + 1);
        writer.WriteStartArray(name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (++count > maximumRows || reader.IsDBNull(0))
                throw SnapshotLimit();
            // PostgreSQL produced this complete JSON value through the fixed to_jsonb/
            // jsonb_build_object queries. Preserve nested evidence without a second parser's
            // smaller default depth limit; the SQL row and shared UTF8 budgets still apply.
            writer.WriteRawValue(reader.GetString(0), skipInputValidation: true);
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.Flush();
    }

    private static async Task ConfigureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        bool readOnly, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, readOnly ? """
            SET TRANSACTION READ ONLY;
            SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'; SET LOCAL TIME ZONE 'UTC';
            SET LOCAL search_path=pg_catalog,weft;
            """ : """
            SET LOCAL lock_timeout='1s'; SET LOCAL statement_timeout='5s'; SET LOCAL TIME ZONE 'UTC';
            SET LOCAL search_path=pg_catalog,weft;
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = 5 };

    private static NpgsqlCommand FencedCommand(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ReservedIngestionFacts facts, Guid instance, Guid token, string sql)
    {
        var command = Command(connection, transaction, sql);
        AddScope(command, facts.Project, facts.OperationId);
        Add(command, "instance", NpgsqlDbType.Uuid, instance);
        Add(command, "token", NpgsqlDbType.Uuid, token);
        return command;
    }

    private static void AddScope(NpgsqlCommand command, ProjectScope project, Guid operation)
    {
        Add(command, "organization", NpgsqlDbType.Uuid, project.OrganizationId);
        Add(command, "project", NpgsqlDbType.Uuid, project.ProjectId);
        Add(command, "operation", NpgsqlDbType.Uuid, operation);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static async Task<DateTimeOffset> ReadTimestampAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw InvalidTransition();
        var result = reader.GetFieldValue<DateTimeOffset>(0);
        if (await reader.ReadAsync(cancellationToken))
            throw InvalidTransition();
        return result;
    }

    private static async Task RequireOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidTransition();
    }

    private static InvalidOperationException OriginalSchemaRequired() =>
        new("Historical writes require exactly the unchanged 0001/0002 migration prefix; current schemas are refused.");
    private static InvalidOperationException InvalidTransition() =>
        new("The synthetic historical fixture did not complete one accepted original transition.");
    private static InvalidOperationException SnapshotLimit() =>
        new("The complete historical owner snapshot exceeds its fixed row or UTF8 byte limit; no truncated snapshot is returned.");

    private sealed class SnapshotBudget
    {
        private long used;
        internal void Consume(int count)
        {
            if (used + count > MaximumSnapshotBytes)
                throw SnapshotLimit();
            used += count;
        }
    }

    private sealed class SnapshotBuffer(SnapshotBudget budget) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            budget.Consume(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            // MemoryStream's span fallback can dispatch to the virtual byte[] overload
            // for derived streams. Route through it explicitly so bytes count once.
            Write(buffer.ToArray(), 0, buffer.Length);
        }
    }
}

internal sealed record HistoricalIngestionOwnerSnapshot(string RetainedFactsJson, string SchemaStateJson);
