using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;
using static Weft.Persistence.Postgres.RevisionReportPersistence;

namespace Weft.Persistence.Postgres;

public sealed partial class PostgresRevisionDerivationStore
{
    private static async Task<RevisionReportStoreResult> TryClaimCoreAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportKey key, Guid serverInstanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preliminary = await ReadSourceAsync(transaction, actor, key.Scope, cancellationToken);
        if (preliminary is null || preliminary.Source != key.Source) return await DeniedAsync(transaction, cancellationToken);
        await InsertEntryAsync(transaction, actor, key, cancellationToken);
        if (await LockEntryAsync(transaction, actor, key, null, cancellationToken) is null)
            return await DeniedAsync(transaction, cancellationToken);

        // This statement begins only after any unique/key wait. Its authority,
        // source, complete key, current header and DB clock share one snapshot.
        var row = await ReadCurrentAsync(transaction, actor, key.Scope, key.Operation, key.Fingerprint.Value, null, cancellationToken);
        if (row is null || row.Source.Source != key.Source) return await DeniedAsync(transaction, cancellationToken);
        var snapshot = Read(() => row.Snapshot(key));
        if (snapshot.State == RevisionReportState.Available)
            return RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Available, snapshot);
        if (snapshot.IsClaimLive)
            return RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.Pending, snapshot);
        if (snapshot.State == RevisionReportState.Failed && snapshot.NextAttemptAtUtc is { } retryAt && retryAt > snapshot.Source.ObservedAtUtc)
            return RevisionReportStoreResult.ForSnapshot(RevisionReportStoreOutcome.RetryDelayed, snapshot);
        if (snapshot.Generation == long.MaxValue)
            return RevisionReportStoreResult.WithoutData(RevisionReportStoreOutcome.GenerationExhausted);
        return await InsertAndAdvanceAsync(transaction, actor, key, row.Entry ?? throw InvalidFacts(), serverInstanceId, null, cancellationToken);
    }

    private const string KeyColumns = """
        organization_id,project_id,mapping_id,revision_id,operation_kind,
        native_object_id,native_class,native_digest,native_byte_count,logical_entry_document,dependency_manifest_id,
        backend_id,engine_revision,backend_build,adapter_build,schema_version,algorithm_version,codec_version,
        policy_schema_version,policy_json,policy_fingerprint,native_byte_limit,worker_response_byte_limit,item_limit,
        response_item_limit,json_depth_limit,detail_depth_limit,diagnostic_limit,validation_issue_limit,text_byte_limit,
        header_byte_limit,stored_object_byte_limit,response_byte_limit,key_fingerprint
        """;

    private const string SourceMatchesParametersSql = """
        source.native_object_id=@nativeObjectId AND source.native_class='native' AND source.native_digest=@nativeDigest
        AND source.native_byte_count=@nativeByteCount AND source.logical_entry_document=@logicalDocument
        AND source.dependency_manifest_id=@dependencyIdentity AND source.backend_id=@backendId
        """;

    private static async Task InsertEntryAsync(PostgresTransaction transaction, ActorContext actor, RevisionReportKey key,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand(AuthorizedSourceSql + $$"""
            INSERT INTO weft.revision_derivations ({{KeyColumns}})
            SELECT source.organization_id,source.project_id,source.mapping_id,source.id,@operation,
                source.native_object_id,source.native_class,source.native_digest,source.native_byte_count,
                source.logical_entry_document,source.dependency_manifest_id,source.backend_id,@backendRevision,@backendBuild,@adapterBuild,
                @schemaVersion,@algorithmVersion,@codecVersion,@policySchemaVersion,@policyJson,@policyFingerprint,
                @nativeByteLimit,@workerResponseByteLimit,@itemLimit,@responseItemLimit,@jsonDepthLimit,@detailDepthLimit,
                @diagnosticLimit,@validationIssueLimit,@textByteLimit,@headerByteLimit,@storedObjectByteLimit,@responseByteLimit,@fingerprint
            FROM authorized_source source WHERE {{SourceMatchesParametersSql}}
            ON CONFLICT ON CONSTRAINT revision_derivations_identity_key DO NOTHING
            """);
        AddKey(command, actor, key);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        if (count is < 0 or > 1) throw InvalidFacts();
    }

    private static async Task<RevisionReportStoreResult> InsertAndAdvanceAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionReportKey key, PersistedEntry entry, Guid instance, Guid? repairTarget,
        CancellationToken cancellationToken)
    {
        await using var insert = transaction.CreateCommand(AuthorizedSourceSql + $$"""
            , inserted AS (
                INSERT INTO weft.revision_derivation_attempts
                    (organization_id,project_id,mapping_id,revision_id,cache_entry_id,generation,actor_principal_id,
                     claimant_instance_id,repair_target_derivation_id)
                SELECT entry.organization_id,entry.project_id,entry.mapping_id,entry.revision_id,entry.cache_entry_id,
                    CASE WHEN entry.current_generation < 9223372036854775807 THEN entry.current_generation+1 ELSE NULL END,
                    @actorId,@instanceId,@repairTarget
                FROM weft.revision_derivations entry JOIN authorized_source source ON {{SourceMatchesEntrySql}}
                CROSS JOIN observed
                WHERE entry.cache_entry_id=@cacheEntryId AND entry.current_generation=@generation
                  AND entry.current_generation < 9223372036854775807
                  AND entry.operation_kind=@operation AND entry.key_fingerprint=@fingerprint
                  AND ((@repairTarget::uuid IS NULL AND entry.selected_derivation_id IS NULL)
                       OR entry.selected_derivation_id=@repairTarget)
                RETURNING *
            )
            SELECT (SELECT count(*) FROM authorized_source) AS authorized_count,
                (SELECT to_jsonb(inserted)::text FROM inserted) AS attempt_row
            """);
        AddKey(insert, actor, key);
        Add(insert, "cacheEntryId", NpgsqlDbType.Uuid, entry.Id);
        Add(insert, "generation", NpgsqlDbType.Bigint, entry.Generation);
        Add(insert, "instanceId", NpgsqlDbType.Uuid, instance);
        Add(insert, "repairTarget", NpgsqlDbType.Uuid, repairTarget);
        JsonElement? inserted;
        long authorized;
        await using (var reader = await insert.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
            authorized = reader.GetInt64(0);
            inserted = reader.IsDBNull(1) ? null : Read(() => Parse(reader.GetString(1)));
            if (await reader.ReadAsync(cancellationToken) || authorized is < 0 or > 1) throw InvalidFacts();
        }
        if (authorized == 0) return await DeniedAsync(transaction, cancellationToken);
        if (inserted is null) throw InvalidFacts();
        // The trigger issued the intention; the immediate current FK is satisfied
        // only after this same transaction advances the key pointer.
        var generation = checked(entry.Generation + 1);
        var expectedEntry = entry with { Generation = generation, CurrentAttemptId = Id(inserted.Value, "derivation_id"), SelectedDerivationId = null };
        var attempt = Read(() => Attempt(inserted.Value, expectedEntry));
        if (attempt.Claim.OriginalActor != actor.PrincipalId || attempt.Claim.ServerInstanceId != instance ||
            attempt.Claim.Generation != generation || attempt.Claim.RepairTargetDerivationId != repairTarget ||
            attempt.Summary.Completion is not null || attempt.Result is not null) throw InvalidFacts();
        await using var advance = transaction.CreateCommand($"""
            UPDATE weft.revision_derivations SET current_generation=@nextGeneration,current_attempt_id=@derivationId,
                selected_derivation_id=NULL
            WHERE {ScopeSql} AND cache_entry_id=@cacheEntryId AND current_generation=@generation
              AND current_attempt_id IS NOT DISTINCT FROM @previousAttempt::uuid
              AND selected_derivation_id IS NOT DISTINCT FROM @repairTarget::uuid
            """);
        AddScope(advance, actor, key.Scope);
        Add(advance, "cacheEntryId", NpgsqlDbType.Uuid, entry.Id);
        Add(advance, "generation", NpgsqlDbType.Bigint, entry.Generation);
        Add(advance, "nextGeneration", NpgsqlDbType.Bigint, generation);
        Add(advance, "derivationId", NpgsqlDbType.Uuid, attempt.Claim.DerivationId);
        Add(advance, "previousAttempt", NpgsqlDbType.Uuid, entry.CurrentAttemptId);
        Add(advance, "repairTarget", NpgsqlDbType.Uuid, repairTarget);
        if (await advance.ExecuteNonQueryAsync(cancellationToken) != 1) throw InvalidFacts();
        var row = await ReadCurrentAsync(transaction, actor, key.Scope, key.Operation, key.Fingerprint.Value, entry.Id, cancellationToken);
        if (row is null) return await DeniedAsync(transaction, cancellationToken);
        var snapshot = Read(() => row.Snapshot(key));
        if (row.Current?.Claim != attempt.Claim) throw InvalidFacts();
        return Read(() => RevisionReportStoreResult.ForClaim(snapshot, attempt.Claim));
    }

    private static void AddKey(NpgsqlCommand command, ActorContext actor, RevisionReportKey key)
    {
        AddScope(command, actor, key.Scope);
        Add(command, "operation", NpgsqlDbType.Text, Operation(key.Operation));
        Add(command, "nativeObjectId", NpgsqlDbType.Uuid, key.Source.NativeObject.Id.ObjectId);
        Add(command, "nativeDigest", NpgsqlDbType.Text, key.NativeDigest.Value);
        Add(command, "nativeByteCount", NpgsqlDbType.Bigint, key.Source.NativeObject.ByteCount);
        Add(command, "logicalDocument", NpgsqlDbType.Text, key.Source.LogicalDocument);
        Add(command, "dependencyIdentity", NpgsqlDbType.Text, key.Source.DependencyIdentity);
        Add(command, "backendId", NpgsqlDbType.Text, key.BackendId);
        Add(command, "backendRevision", NpgsqlDbType.Text, key.BackendRevision);
        Add(command, "backendBuild", NpgsqlDbType.Text, key.BackendBuild);
        Add(command, "adapterBuild", NpgsqlDbType.Text, key.AdapterBuild);
        Add(command, "schemaVersion", NpgsqlDbType.Text, key.SchemaVersion);
        Add(command, "algorithmVersion", NpgsqlDbType.Text, key.AlgorithmVersion);
        Add(command, "codecVersion", NpgsqlDbType.Text, key.CodecVersion);
        Add(command, "policySchemaVersion", NpgsqlDbType.Text, key.Policy.SchemaVersion);
        Add(command, "policyJson", NpgsqlDbType.Text, Encoding.UTF8.GetString(key.Policy.CopyCanonicalUtf8()));
        Add(command, "policyFingerprint", NpgsqlDbType.Text, key.Policy.Fingerprint.Value);
        Add(command, "fingerprint", NpgsqlDbType.Text, key.Fingerprint.Value);
        var limits = new (string Name, int Value)[]
        {
            ("nativeByteLimit", key.Policy.NativeByteLimit), ("workerResponseByteLimit", key.Policy.WorkerResponseByteLimit),
            ("itemLimit", key.Policy.ItemLimit), ("responseItemLimit", key.Policy.ResponseItemLimit), ("jsonDepthLimit", key.Policy.JsonDepthLimit),
            ("detailDepthLimit", key.Policy.DetailDepthLimit), ("diagnosticLimit", key.Policy.DiagnosticLimit),
            ("validationIssueLimit", key.Policy.ValidationIssueLimit), ("textByteLimit", key.Policy.TextByteLimit),
            ("headerByteLimit", key.Policy.HeaderByteLimit), ("storedObjectByteLimit", key.Policy.StoredObjectByteLimit),
            ("responseByteLimit", key.Policy.ResponseByteLimit)
        };
        foreach (var limit in limits) Add(command, limit.Name, NpgsqlDbType.Integer, limit.Value);
    }
}
