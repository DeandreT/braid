using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Weft.Application;
using Weft.Domain;

namespace Weft.Persistence.Postgres;

/// <summary>Private bounded metadata transport restoration. No content references or timestamps are invented.</summary>
internal static partial class PrivateObjectMaintenancePersistence
{
    private const int MaximumTransportBytes = 1_048_576;

    internal static async Task<string?> ReadJsonAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        var json = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (json is not null && (json.Length > MaximumTransportBytes || Encoding.UTF8.GetByteCount(json) > MaximumTransportBytes) ||
            await reader.ReadAsync(cancellationToken)) throw InvalidFacts();
        return json;
    }

    internal static T Restore<T>(string json, Func<JsonElement, T> materialize)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidFacts();
            return materialize(document.RootElement);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or
            OverflowException or JsonException or KeyNotFoundException or InvalidCastException)
        { throw InvalidFacts(); }
    }

    internal static ObjectCleanupClaimBatch Batch(JsonElement row, ProjectScope project, Guid instance,
        CancellationToken cancellationToken)
    {
        var observed = Time(row, "observed_at");
        var claims = row.GetProperty("claims");
        if (claims.ValueKind != JsonValueKind.Array || claims.GetArrayLength() > ObjectCleanupPolicy.MaximumTargets) throw InvalidFacts();
        var owned = new List<ObjectCleanupClaim>();
        foreach (var claimRow in claims.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = Claim(claimRow);
            if (claim.ServerInstanceId != instance || claim.StartedAtUtc > observed) throw InvalidFacts();
            owned.Add(claim);
        }
        var cursor = Null(row, "next_cursor") ? null : Cursor(row.GetProperty("next_cursor"));
        cancellationToken.ThrowIfCancellationRequested();
        return new(project, owned, Int(row, "examined"), Int(row, "protected"), Int(row, "not_due"), cursor);
    }

    internal static ObjectCleanupClaim Claim(JsonElement row) => new(Retirement(row.GetProperty("retirement")),
        Id(row, "attempt_id"), Long(row, "generation"), Id(row, "instance_id"), Id(row, "lease_token"),
        Time(row, "started_at"), Time(row, "lease_expires_at"));

    internal static ObjectCleanupAttempt Attempt(JsonElement row) => new(Claim(row), NullableTime(row, "finished_at"),
        Null(row, "disposition") ? null : Disposition(Text(row, "disposition")));

    private static ObjectCleanupRetirement Retirement(JsonElement row)
    {
        if (Text(row, "policy_id") != ObjectCleanupPolicy.Identity) throw InvalidFacts();
        var scope = Scope(row);
        var origin = Text(row, "origin_kind") switch
        {
            "native_ingestion" when Null(row, "cache_entry_id") && Null(row, "derivation_id") && Null(row, "origin_generation") =>
                ObjectCleanupOrigin.ForIngestion(scope, Id(row, "ingestion_operation_id")),
            "report_attempt" when Null(row, "ingestion_operation_id") => ObjectCleanupOrigin.ForReport(scope,
                Id(row, "cache_entry_id"), Id(row, "derivation_id"), Long(row, "origin_generation")),
            _ => throw InvalidFacts()
        };
        var target = Target(row);
        if (target.Object.Class == PrivateObjectClass.NativeArtifact
            ? NullableId(row, "native_object_id") != target.Object.ObjectId || !Null(row, "candidate_object_id")
            : NullableId(row, "candidate_object_id") != target.Object.ObjectId || !Null(row, "native_object_id"))
            throw InvalidFacts();
        var publication = Null(row, "publication") ? null : Publication(row.GetProperty("publication"));
        return new(Id(row, "retirement_id"), target, origin, Reason(Text(row, "reason")),
            Time(row, "eligibility_origin_at"), Time(row, "retired_at"), publication);
    }

    internal static PrivatePublicationRecord Publication(JsonElement row)
    {
        if (Text(row, "format") != PrivatePublicationFileIdentity.Format) throw InvalidFacts();
        var @object = Object(row);
        var actor = new ActorContext(new(Id(row, "actor_principal_id")));
        PrivatePublicationOwner owner;
        switch (Text(row, "owner_kind"))
        {
            case "native_ingestion":
                {
                    if (@object.Class != PrivateObjectClass.NativeArtifact || !Null(row, "cache_entry_id") ||
                        !Null(row, "derivation_id") || !Null(row, "candidate_object_id") || Id(row, "native_object_id") != @object.ObjectId)
                        throw InvalidFacts();
                    var ingestion = row.GetProperty("ingestion");
                    var attempt = row.GetProperty("owner_ingestion_attempt");
                    var operation = Id(row, "ingestion_operation_id");
                    if (Id(ingestion, "organization_id") != @object.Scope.OrganizationId ||
                        Id(ingestion, "project_id") != @object.Scope.ProjectId ||
                        Id(ingestion, "future_mapping_id") != @object.Scope.MappingId ||
                        Id(ingestion, "future_revision_id") != @object.Scope.RevisionId ||
                        Id(ingestion, "native_object_id") != @object.ObjectId || Text(ingestion, "native_class") != "native" ||
                        Id(ingestion, "operation_id") != operation || Id(ingestion, "actor_principal_id") != actor.PrincipalId.Value ||
                        Id(attempt, "organization_id") != @object.Scope.OrganizationId || Id(attempt, "project_id") != @object.Scope.ProjectId ||
                        Id(attempt, "operation_id") != operation || Long(attempt, "generation") != Long(row, "owner_generation") ||
                        Id(attempt, "claimant_instance_id") != Id(row, "owner_instance_id") || Id(attempt, "claim_token") != Id(row, "owner_token"))
                        throw InvalidFacts();
                    owner = PrivatePublicationOwner.ForIngestion(actor, new(new(@object.Scope.OrganizationId, @object.Scope.ProjectId),
                        operation, actor.PrincipalId, Id(row, "owner_instance_id"), Id(row, "owner_token"),
                        Long(row, "owner_generation"), Time(row, "owner_lease_expires_at")));
                    break;
                }
            case "report_attempt":
                {
                    if (@object.Class != PrivateObjectClass.DerivedArtifact || !Null(row, "ingestion_operation_id") ||
                        !Null(row, "native_object_id") || Id(row, "candidate_object_id") != @object.ObjectId) throw InvalidFacts();
                    // The full original claim is reconstructed from its actual immutable key and
                    // exact originating attempt, never from a selected/current replacement or a fake key.
                    var entry = RevisionReportPersistence.Entry(row.GetProperty("report_entry"));
                    var attempt = row.GetProperty("owner_report_attempt");
                    var claim = new RevisionReportClaim(entry.Id, Id(attempt, "derivation_id"), entry.Key,
                        new(Id(attempt, "actor_principal_id")), Id(attempt, "claimant_instance_id"), Id(attempt, "claim_token"),
                        Long(attempt, "generation"), Time(attempt, "started_at"), Time(attempt, "lease_expires_at"),
                        PrivateObjectId.Restore(Scope(attempt), Id(attempt, "candidate_object_id"), PrivateObjectClass.DerivedArtifact),
                        NullableId(attempt, "repair_target_derivation_id"));
                    if (entry.Id != Id(row, "cache_entry_id") || Id(attempt, "cache_entry_id") != entry.Id ||
                        entry.Key.Scope != @object.Scope || claim.CandidateObjectId != @object || Text(attempt, "derived_class") != "derived" ||
                        claim.DerivationId != Id(row, "derivation_id") || claim.Generation != Long(row, "owner_generation") ||
                        claim.OriginalActor != actor.PrincipalId || claim.ServerInstanceId != Id(row, "owner_instance_id") ||
                        claim.LeaseToken != Id(row, "owner_token") || claim.LeaseExpiresAtUtc != Time(row, "owner_lease_expires_at"))
                        throw InvalidFacts();
                    owner = PrivatePublicationOwner.ForReport(actor, claim);
                    break;
                }
            default: throw InvalidFacts();
        }
        var key = new PrivatePublicationKey(Id(row, "root_id"), @object, Id(row, "temp_id"));
        var reservation = new PrivatePublicationReservation(Id(row, "publication_id"), key, owner,
            Digest(row, "absence_record_digest"), Time(row, "registered_at"));
        var present = BindingColumns.Count(name => !Null(row, name));
        if (present == 0) return new(reservation);
        if (present != BindingColumns.Length) throw InvalidFacts();
        var identity = new PrivatePublicationFileIdentity(key.RootId, Id(row, "control_namespace_id"), Id(row, "linux_boot_id"),
            Ulong(row, "mount_namespace_device"), Ulong(row, "mount_namespace_inode"), Uint(row, "effective_user_id"),
            new(Ulong(row, "file_inode"), Uint(row, "file_device_major"), Uint(row, "file_device_minor"), Ulong(row, "file_mount_id")),
            new(Ulong(row, "directory_inode"), Uint(row, "directory_device_major"), Uint(row, "directory_device_minor"), Ulong(row, "directory_mount_id")));
        return new(reservation, new(identity, Digest(row, "bound_record_digest"), Time(row, "bound_at")));
    }

    private static readonly string[] BindingColumns = ["control_namespace_id", "linux_boot_id", "mount_namespace_device",
        "mount_namespace_inode", "effective_user_id", "file_inode", "file_device_major", "file_device_minor", "file_mount_id",
        "directory_inode", "directory_device_major", "directory_device_minor", "directory_mount_id", "bound_record_digest", "bound_at"];

    internal static ObjectCleanupScanCursor Cursor(JsonElement row)
    {
        var project = new ProjectScope(Id(row, "organization_id"), Id(row, "project_id"));
        return new(project, (ObjectCleanupScanStream)Int(row, "stream"), Time(row, "eligible_at"), Target(row));
    }

    private static ObjectCleanupTarget Target(JsonElement row) => new(Object(row), Text(row, "target_kind") switch
    {
        "final_object" => CleanupTargetKind.FinalObject,
        "tracked_temporary" => CleanupTargetKind.TrackedTemporary,
        _ => throw InvalidFacts()
    }, NullableId(row, "publication_id"));

    private static PrivateObjectId Object(JsonElement row) => PrivateObjectId.Restore(Scope(row), Id(row, "object_id"),
        Text(row, "object_class") switch { "native" => PrivateObjectClass.NativeArtifact, "derived" => PrivateObjectClass.DerivedArtifact, _ => throw InvalidFacts() });
    private static RevisionScope Scope(JsonElement row) => new(Id(row, "organization_id"), Id(row, "project_id"), Id(row, "mapping_id"), Id(row, "revision_id"));
    private static ObjectCleanupReason Reason(string value) => value switch
    {
        "terminal_native_stage" => ObjectCleanupReason.TerminalNativeStage,
        "failed_derived_candidate" => ObjectCleanupReason.FailedDerivedCandidate,
        "expired_derived_candidate" => ObjectCleanupReason.ExpiredDerivedCandidate,
        "abandoned_tracked_temporary" => ObjectCleanupReason.AbandonedTrackedTemporary,
        _ => throw InvalidFacts()
    };
    internal static ObjectCleanupDisposition Disposition(string value) => value switch
    {
        "deleted" => ObjectCleanupDisposition.Deleted,
        "missing" => ObjectCleanupDisposition.Missing,
        "canceled" => ObjectCleanupDisposition.Canceled,
        "deadline" => ObjectCleanupDisposition.Deadline,
        "storage_io" => ObjectCleanupDisposition.StorageIo,
        "unsafe_storage" => ObjectCleanupDisposition.UnsafeStorage,
        _ => throw InvalidFacts()
    };
    internal static string Disposition(ObjectCleanupDisposition value) => value switch
    {
        ObjectCleanupDisposition.Deleted => "deleted",
        ObjectCleanupDisposition.Missing => "missing",
        ObjectCleanupDisposition.Canceled => "canceled",
        ObjectCleanupDisposition.Deadline => "deadline",
        ObjectCleanupDisposition.StorageIo => "storage_io",
        ObjectCleanupDisposition.UnsafeStorage => "unsafe_storage",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    internal static void AddProject(NpgsqlCommand command, ProjectScope project)
    {
        Add(command, "organizationId", NpgsqlDbType.Uuid, project.OrganizationId);
        Add(command, "projectId", NpgsqlDbType.Uuid, project.ProjectId);
    }
    internal static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw InvalidFacts();
    internal static bool Null(JsonElement row, string name) => row.GetProperty(name).ValueKind == JsonValueKind.Null;
    internal static Guid Id(JsonElement row, string name) => row.GetProperty(name).GetGuid();
    private static Guid? NullableId(JsonElement row, string name) => Null(row, name) ? null : Id(row, name);
    internal static long Long(JsonElement row, string name) => row.GetProperty(name).GetInt64();
    private static int Int(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static ulong Ulong(JsonElement row, string name) => row.GetProperty(name).GetUInt64();
    private static uint Uint(JsonElement row, string name) => row.GetProperty(name).GetUInt32();
    internal static DateTimeOffset Time(JsonElement row, string name) => row.GetProperty(name).GetDateTimeOffset().ToUniversalTime();
    private static DateTimeOffset? NullableTime(JsonElement row, string name) => Null(row, name) ? null : Time(row, name);
    private static Sha256Digest Digest(JsonElement row, string name)
    {
        var text = Text(row, name);
        var digest = Sha256Digest.Parse(text);
        if (text != digest.Value) throw InvalidFacts();
        return digest;
    }
    internal static InvalidOperationException InvalidFacts() => new("The database contains inconsistent private object maintenance metadata.");
    internal static InvalidOperationException Unavailable() => new("Private object maintenance metadata could not be completed.");
}
