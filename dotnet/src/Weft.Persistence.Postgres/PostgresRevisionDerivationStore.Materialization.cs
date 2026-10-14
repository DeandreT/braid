using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Internal metadata-row restoration, never a report payload or native object codec.</summary>
internal static class RevisionReportPersistence
{
    internal const string AuthorizedSourceSql = """
        WITH observed AS MATERIALIZED (SELECT clock_timestamp() AS observed_at),
        authorized_source AS MATERIALIZED (
            SELECT revision.* FROM weft.organizations organization
            JOIN weft.principals principal ON principal.id=@actorId AND principal.active
            JOIN weft.organization_memberships admission
              ON admission.organization_id=organization.id AND admission.principal_id=principal.id AND admission.active
            JOIN weft.projects project ON project.organization_id=organization.id AND project.id=@projectId
            JOIN weft.project_memberships membership
              ON membership.organization_id=project.organization_id AND membership.project_id=project.id
             AND membership.principal_id=principal.id AND membership.active
             AND membership.role IN ('viewer','runner','publisher','administrator')
            JOIN weft.mappings mapping ON mapping.organization_id=project.organization_id
             AND mapping.project_id=project.id AND mapping.id=@mappingId
            JOIN weft.mapping_revisions revision ON revision.organization_id=mapping.organization_id
             AND revision.project_id=mapping.project_id AND revision.mapping_id=mapping.id AND revision.id=@revisionId
            WHERE organization.id=@organizationId
        )
        """;

    internal const string ScopeSql = "organization_id=@organizationId AND project_id=@projectId AND mapping_id=@mappingId AND revision_id=@revisionId";
    internal const string SourceMatchesEntrySql = """
        source.organization_id=entry.organization_id AND source.project_id=entry.project_id
        AND source.mapping_id=entry.mapping_id AND source.id=entry.revision_id
        AND source.native_object_id=entry.native_object_id AND source.native_class=entry.native_class
        AND source.native_digest=entry.native_digest AND source.native_byte_count=entry.native_byte_count
        AND source.logical_entry_document=entry.logical_entry_document
        AND source.dependency_manifest_id=entry.dependency_manifest_id AND source.backend_id=entry.backend_id
        """;

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    internal static void AddScope(NpgsqlCommand command, ActorContext actor, RevisionScope scope)
    {
        Add(command, "actorId", NpgsqlDbType.Uuid, actor.PrincipalId.Value);
        Add(command, "organizationId", NpgsqlDbType.Uuid, scope.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, scope.ProjectId);
        Add(command, "mappingId", NpgsqlDbType.Uuid, scope.MappingId);
        Add(command, "revisionId", NpgsqlDbType.Uuid, scope.RevisionId);
    }

    internal static string Operation(RevisionReportOperation operation) => operation switch
    {
        RevisionReportOperation.Projection => "projection",
        RevisionReportOperation.Validation => "validation",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal static async Task<AuthorizedRevisionReportSource?> ReadSourceAsync(PostgresTransaction transaction,
        ActorContext actor, RevisionScope scope, CancellationToken cancellationToken)
    {
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        await using var command = transaction.CreateCommand(AuthorizedSourceSql + """
            SELECT observed.observed_at,to_jsonb(source)::text AS source_row
            FROM authorized_source source CROSS JOIN observed
            """);
        AddScope(command, actor, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = Read(() => new AuthorizedRevisionReportSource(Source(Parse(reader, "source_row"), "id"),
            actor.PrincipalId, reader.GetFieldValue<DateTimeOffset>(0)));
        if (result.Source.Scope != scope || await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        return result;
    }

    internal static async Task<MetadataRow?> ReadCurrentAsync(PostgresTransaction transaction, ActorContext actor,
        RevisionScope scope, RevisionReportOperation operation, string fingerprint, Guid? cacheEntryId,
        CancellationToken cancellationToken)
    {
        await transaction.RequireOrganizationAsync(scope.OrganizationId, cancellationToken);
        var identity = cacheEntryId is null ? "entry.operation_kind=@operation AND entry.key_fingerprint=@fingerprint" : "entry.cache_entry_id=@cacheEntryId";
        await using var command = transaction.CreateCommand(AuthorizedSourceSql + $$"""
            SELECT observed.observed_at,to_jsonb(source)::text AS source_row,to_jsonb(entry)::text AS entry_row,
                to_jsonb(current_attempt)::text AS current_row,to_jsonb(selected_attempt)::text AS selected_row
            FROM authorized_source source CROSS JOIN observed
            LEFT JOIN weft.revision_derivations entry ON entry.organization_id=source.organization_id
              AND entry.project_id=source.project_id AND entry.mapping_id=source.mapping_id AND entry.revision_id=source.id
              AND {{identity}}
            LEFT JOIN weft.revision_derivation_attempts current_attempt ON current_attempt.organization_id=entry.organization_id
              AND current_attempt.project_id=entry.project_id AND current_attempt.mapping_id=entry.mapping_id
              AND current_attempt.revision_id=entry.revision_id AND current_attempt.cache_entry_id=entry.cache_entry_id
              AND current_attempt.generation=entry.current_generation AND current_attempt.derivation_id=entry.current_attempt_id
            LEFT JOIN weft.revision_derivation_attempts selected_attempt ON selected_attempt.organization_id=entry.organization_id
              AND selected_attempt.project_id=entry.project_id AND selected_attempt.mapping_id=entry.mapping_id
              AND selected_attempt.revision_id=entry.revision_id AND selected_attempt.cache_entry_id=entry.cache_entry_id
              AND selected_attempt.derivation_id=entry.selected_derivation_id AND selected_attempt.result_usable=entry.selected_usable
            """);
        AddScope(command, actor, scope);
        Add(command, "operation", NpgsqlDbType.Text, Operation(operation));
        Add(command, "fingerprint", NpgsqlDbType.Text, fingerprint);
        Add(command, "cacheEntryId", NpgsqlDbType.Uuid, cacheEntryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = Read(() =>
        {
            var authorized = new AuthorizedRevisionReportSource(Source(Parse(reader, "source_row"), "id"),
                actor.PrincipalId, reader.GetFieldValue<DateTimeOffset>(0));
            var entry = Optional(reader, "entry_row", Entry);
            if (authorized.Source.Scope != scope || entry is not null && entry.Key.Source != authorized.Source) throw InvalidFacts();
            var current = Optional(reader, "current_row", value => Attempt(value, entry ?? throw InvalidFacts()));
            var selected = Optional(reader, "selected_row", value => Attempt(value, entry ?? throw InvalidFacts()));
            if (entry is null && (current is not null || selected is not null)) throw InvalidFacts();
            if (entry is not null && (entry.CurrentAttemptId != current?.Claim.DerivationId ||
                entry.SelectedDerivationId != selected?.Claim.DerivationId || entry.Generation != (current?.Claim.Generation ?? 0))) throw InvalidFacts();
            if (selected is not null && selected.Result is null) throw InvalidFacts();
            return new MetadataRow(authorized, entry, current, selected);
        });
        if (await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        return result;
    }

    internal static async Task<JsonElement?> LockEntryAsync(PostgresTransaction transaction, ActorContext actor,
        RevisionReportKey key, Guid? cacheEntryId, CancellationToken cancellationToken)
    {
        var identity = cacheEntryId is null ? "operation_kind=@operation AND key_fingerprint=@fingerprint" : "cache_entry_id=@cacheEntryId";
        await using var command = transaction.CreateCommand($"SELECT to_jsonb(entry)::text FROM weft.revision_derivations entry WHERE {ScopeSql} AND {identity} FOR UPDATE OF entry");
        AddScope(command, actor, key.Scope);
        Add(command, "operation", NpgsqlDbType.Text, Operation(key.Operation));
        Add(command, "fingerprint", NpgsqlDbType.Text, key.Fingerprint.Value);
        Add(command, "cacheEntryId", NpgsqlDbType.Uuid, cacheEntryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var row = Parse(reader.GetString(0));
        _ = Read(() => Entry(row));
        if (await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        return row;
    }

    internal static RevisionReportSource Source(JsonElement row, string revisionIdColumn)
    {
        var scope = new RevisionScope(Id(row, "organization_id"), Id(row, "project_id"), Id(row, "mapping_id"), Id(row, revisionIdColumn));
        if (Text(row, "native_class") != "native" || Text(row, "logical_entry_document") != "entry.native.json" ||
            Text(row, "dependency_manifest_id") != "weft.dependencies.empty.v1") throw InvalidFacts();
        return new(scope, new(PrivateObjectId.Restore(scope, Id(row, "native_object_id"), PrivateObjectClass.NativeArtifact),
            Digest(row, "native_digest"), Long(row, "native_byte_count")), Text(row, "logical_entry_document"),
            Text(row, "dependency_manifest_id"), Text(row, "backend_id"));
    }

    internal static PersistedEntry Entry(JsonElement row)
    {
        var source = Source(row, "revision_id");
        var profile = new RevisionReportBackendProfile(Text(row, "backend_id"), Text(row, "engine_revision"), Text(row, "backend_build"), Text(row, "adapter_build"));
        var policy = new RevisionReportPolicy(Int(row, "native_byte_limit"), Int(row, "worker_response_byte_limit"),
            Int(row, "item_limit"), Int(row, "response_item_limit"), Int(row, "json_depth_limit"), Int(row, "detail_depth_limit"),
            Int(row, "diagnostic_limit"), Int(row, "validation_issue_limit"), Int(row, "text_byte_limit"), Int(row, "header_byte_limit"),
            Int(row, "stored_object_byte_limit"), Int(row, "response_byte_limit"));
        if (Text(row, "policy_schema_version") != policy.SchemaVersion || Text(row, "policy_json") != Encoding.UTF8.GetString(policy.CopyCanonicalUtf8()) ||
            Digest(row, "policy_fingerprint") != policy.Fingerprint) throw InvalidFacts();
        var operation = Text(row, "operation_kind") switch
        {
            "projection" => RevisionReportOperation.Projection,
            "validation" => RevisionReportOperation.Validation,
            _ => throw InvalidFacts()
        };
        RevisionReportKey key = operation == RevisionReportOperation.Projection
            ? new ProjectionCacheKey(source, profile, Text(row, "schema_version"), Text(row, "algorithm_version"), Text(row, "codec_version"), policy)
            : new ValidationCacheKey(source, profile, Text(row, "schema_version"), Text(row, "algorithm_version"), Text(row, "codec_version"), policy);
        if (Digest(row, "key_fingerprint") != key.Fingerprint) throw InvalidFacts();
        var id = Id(row, "cache_entry_id");
        var generation = Long(row, "current_generation");
        var current = NullableGuid(row, "current_attempt_id");
        var selected = NullableGuid(row, "selected_derivation_id");
        _ = Time(row, "created_at");
        if (id == System.Guid.Empty || generation < 0 || !Bool(row, "selected_usable") ||
            generation == 0 && (current is not null || selected is not null) || generation > 0 && current is null ||
            current == System.Guid.Empty || selected == System.Guid.Empty) throw InvalidFacts();
        return new(id, key, generation, current, selected);
    }

    internal static PersistedAttempt Attempt(JsonElement row, PersistedEntry entry)
    {
        var key = entry.Key;
        var scope = new RevisionScope(Id(row, "organization_id"), Id(row, "project_id"), Id(row, "mapping_id"), Id(row, "revision_id"));
        if (scope != key.Scope || Id(row, "cache_entry_id") != entry.Id || Text(row, "derived_class") != "derived") throw InvalidFacts();
        var claim = new RevisionReportClaim(entry.Id, Id(row, "derivation_id"), key, new(Id(row, "actor_principal_id")),
            Id(row, "claimant_instance_id"), Id(row, "claim_token"), Long(row, "generation"), Time(row, "started_at"),
            Time(row, "lease_expires_at"), PrivateObjectId.Restore(scope, Id(row, "candidate_object_id"), PrivateObjectClass.DerivedArtifact),
            NullableGuid(row, "repair_target_derivation_id"));
        if (claim.Generation > entry.Generation) throw InvalidFacts();
        var observation = Observation(row, key.Operation);
        var kind = NullableText(row, "completion_kind");
        var finish = NullableTime(row, "finished_at");
        RevisionReportCompletion? completion = kind switch
        {
            null => null,
            "usable" => RevisionReportCompletion.ForUsable(key, observation ?? throw InvalidFacts(),
                new(claim.CandidateObjectId, Digest(row, "derived_digest"), Long(row, "derived_byte_count"))),
            "failed" => RevisionReportCompletion.ForFailure(Failure(Text(row, "failure_code")), observation),
            _ => throw InvalidFacts()
        };
        if ((finish is null) != (completion is null) || finish is { } completedAt && completedAt >= claim.LeaseExpiresAtUtc ||
            Bool(row, "result_usable") != (completion?.IsUsable == true) ||
            completion?.IsUsable == true && !Null(row, "failure_code") || completion?.IsUsable != true &&
            (!Null(row, "derived_digest") || !Null(row, "derived_byte_count")) || completion is null &&
            (observation is not null || !Null(row, "failure_code"))) throw InvalidFacts();
        var summary = new RevisionReportAttemptSummary(claim.DerivationId, claim.Generation, claim.StartedAtUtc,
            claim.LeaseExpiresAtUtc, finish, completion, claim.RepairTargetDerivationId);
        RevisionReportResult? result = completion?.IsUsable == true
            ? new(claim.DerivationId, entry.Id, key, claim.Generation, claim.OriginalActor, claim.StartedAtUtc,
                finish!.Value, completion.DerivedObject!, completion.Observation!) : null;
        return new(claim, summary, result);
    }

    private static readonly string[] ObservedColumns = ["backend_status", "provenance_confirmed", "provenance_organization_id", "provenance_project_id",
        "provenance_mapping_id", "provenance_revision_id", "provenance_native_digest", "provenance_backend_id", "provenance_engine_revision",
        "provenance_backend_build", "provenance_adapter_build", "projection_coverage", "document_item_count", "document_diagnostic_count",
        "coverage_gap_count", "operation_diagnostic_count", "is_valid", "total_issue_count", "emitted_issue_count", "issues_truncated", "validation_diagnostic_count"];

    internal static RevisionReportObservation? Observation(JsonElement row, RevisionReportOperation operation)
    {
        if (!Bool(row, "observation_present"))
        {
            if (ObservedColumns.Any(column => !Null(row, column))) throw InvalidFacts();
            return null;
        }
        RevisionReportProvenance? provenance = null;
        var provenanceFields = ObservedColumns.Where(column => column.StartsWith("provenance_", StringComparison.Ordinal)).ToArray();
        var provenanceCount = provenanceFields.Count(column => !Null(row, column));
        if (provenanceCount != 0)
        {
            if (provenanceCount != provenanceFields.Length) throw InvalidFacts();
            provenance = new(new(Id(row, "provenance_organization_id"), Id(row, "provenance_project_id"), Id(row, "provenance_mapping_id"), Id(row, "provenance_revision_id")),
                Digest(row, "provenance_native_digest"), new(Text(row, "provenance_backend_id"), Text(row, "provenance_engine_revision"),
                    Text(row, "provenance_backend_build"), Text(row, "provenance_adapter_build")), Bool(row, "provenance_confirmed"));
        }
        RevisionReportProjectionSummary? projection = null;
        var projectionFields = new[] { "projection_coverage", "document_item_count", "document_diagnostic_count", "coverage_gap_count", "operation_diagnostic_count" };
        if (projectionFields.Any(column => !Null(row, column)))
        {
            if (projectionFields.Skip(1).Any(column => Null(row, column))) throw InvalidFacts();
            var coverage = NullableText(row, "projection_coverage") switch
            {
                null => (RevisionReportCoverage?)null,
                "complete" => RevisionReportCoverage.Complete,
                "partial" => RevisionReportCoverage.Partial,
                "unavailable" => RevisionReportCoverage.Unavailable,
                _ => throw InvalidFacts()
            };
            projection = new(coverage, Int(row, "document_item_count"), Int(row, "document_diagnostic_count"), Int(row, "coverage_gap_count"), Int(row, "operation_diagnostic_count"));
        }
        RevisionReportValidationSummary? validation = null;
        var validationFields = new[] { "is_valid", "total_issue_count", "emitted_issue_count", "issues_truncated", "validation_diagnostic_count" };
        if (validationFields.Any(column => !Null(row, column)))
        {
            if (validationFields.Skip(1).Any(column => Null(row, column))) throw InvalidFacts();
            validation = new(NullableBool(row, "is_valid"), Int(row, "total_issue_count"), Int(row, "emitted_issue_count"), Bool(row, "issues_truncated"), Int(row, "validation_diagnostic_count"));
        }
        return new(operation, NullableText(row, "backend_status") is { } status ? Status(status) : null, provenance, projection, validation);
    }

    internal static RevisionReportBackendStatus Status(string value) => value switch
    {
        "succeeded" => RevisionReportBackendStatus.Succeeded,
        "invalid_mapping" => RevisionReportBackendStatus.InvalidMapping,
        "unsupported_artifact" => RevisionReportBackendStatus.UnsupportedArtifact,
        "resource_limit" => RevisionReportBackendStatus.ResourceLimit,
        "protocol_failure" => RevisionReportBackendStatus.ProtocolFailure,
        "backend_unavailable" => RevisionReportBackendStatus.BackendUnavailable,
        "backend_failure" => RevisionReportBackendStatus.BackendFailure,
        "timed_out" => RevisionReportBackendStatus.TimedOut,
        "canceled" => RevisionReportBackendStatus.Canceled,
        "isolation_failure" => RevisionReportBackendStatus.IsolationFailure,
        _ => throw InvalidFacts()
    };

    internal static RevisionReportFailureCategory Failure(string value) => value switch
    {
        "object_unavailable" => RevisionReportFailureCategory.ObjectUnavailable,
        "storage_integrity" => RevisionReportFailureCategory.StorageIntegrity,
        "storage_io" => RevisionReportFailureCategory.StorageIo,
        "backend_unavailable" => RevisionReportFailureCategory.BackendUnavailable,
        "backend_failure" => RevisionReportFailureCategory.BackendFailure,
        "timed_out" => RevisionReportFailureCategory.TimedOut,
        "canceled" => RevisionReportFailureCategory.Canceled,
        "isolation_failure" => RevisionReportFailureCategory.IsolationFailure,
        "protocol_failure" => RevisionReportFailureCategory.ProtocolFailure,
        "profile_unavailable" => RevisionReportFailureCategory.ProfileUnavailable,
        "resource_limit" => RevisionReportFailureCategory.ResourceLimit,
        "unsupported_artifact" => RevisionReportFailureCategory.UnsupportedArtifact,
        _ => throw InvalidFacts()
    };

    internal static JsonElement Parse(NpgsqlDataReader reader, string column) => Parse(reader.GetString(reader.GetOrdinal(column)));
    internal static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidFacts();
        return document.RootElement.Clone();
    }
    private static T? Optional<T>(NpgsqlDataReader reader, string column, Func<JsonElement, T> materialize) where T : class =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : materialize(Parse(reader, column));
    internal static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw InvalidFacts();
    internal static string? NullableText(JsonElement row, string name) => Null(row, name) ? null : Text(row, name);
    internal static bool Null(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null;
    internal static Guid Id(JsonElement row, string name) => row.GetProperty(name).GetGuid();
    internal static Guid? NullableGuid(JsonElement row, string name) => Null(row, name) ? null : Id(row, name);
    internal static long Long(JsonElement row, string name) => row.GetProperty(name).GetInt64();
    internal static int Int(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    internal static bool Bool(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
    internal static bool? NullableBool(JsonElement row, string name) => Null(row, name) ? null : Bool(row, name);
    internal static DateTimeOffset Time(JsonElement row, string name) => row.GetProperty(name).GetDateTimeOffset().ToUniversalTime();
    internal static DateTimeOffset? NullableTime(JsonElement row, string name) => Null(row, name) ? null : Time(row, name);
    internal static Sha256Digest Digest(JsonElement row, string name)
    {
        var raw = Text(row, name);
        var digest = Sha256Digest.Parse(raw);
        if (raw != digest.Value) throw InvalidFacts();
        return digest;
    }

    internal static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException or FormatException or OverflowException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw InvalidFacts(); }
    }
    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> execute, CancellationToken cancellationToken)
    {
        try { return await execute(); }
        catch (NpgsqlException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }
    internal static InvalidOperationException InvalidFacts() => new("The database contains inconsistent revision report metadata.");
    internal sealed record PersistedEntry(Guid Id, RevisionReportKey Key, long Generation, Guid? CurrentAttemptId, Guid? SelectedDerivationId);
    internal sealed record PersistedAttempt(RevisionReportClaim Claim, RevisionReportAttemptSummary Summary, RevisionReportResult? Result);
    internal sealed record MetadataRow(AuthorizedRevisionReportSource Source, PersistedEntry? Entry, PersistedAttempt? Current, PersistedAttempt? Selected)
    {
        internal RevisionReportReadSnapshot Snapshot(RevisionReportKey key)
        {
            if (Entry is not null && Entry.Key != key) throw InvalidFacts();
            return new(Source, key, Entry?.Id, Entry?.Generation ?? 0, Selected?.Result, Current?.Summary);
        }
    }
}
