using System.Text;
using System.Text.Json;
using Braid.Application;
using Braid.Domain;

namespace Braid.Persistence.Postgres;

internal static partial class PrivateObjectMaintenancePersistence
{
    internal static string ClaimJson(ObjectCleanupClaim claim, CancellationToken cancellationToken) => Json(writer =>
    {
        var retirement = claim.Retirement;
        var target = retirement.Target;
        var origin = retirement.Origin;
        writer.WriteStartObject();
        writer.WritePropertyName("retirement");
        writer.WriteStartObject();
        WriteObject(writer, target.Object);
        writer.WriteString("retirement_id", retirement.RetirementId);
        writer.WriteString("target_kind", TargetKind(target.Kind));
        WriteId(writer, "publication_id", target.PublicationAttemptId);
        writer.WriteString("origin_kind", OwnerKind(origin.Kind));
        WriteId(writer, "ingestion_operation_id", origin.IngestionOperationId);
        WriteId(writer, "cache_entry_id", origin.CacheEntryId);
        WriteId(writer, "derivation_id", origin.DerivationId);
        if (origin.Generation is { } generation) writer.WriteNumber("origin_generation", generation);
        else writer.WriteNull("origin_generation");
        WriteConditionalObjectIds(writer, target.Object);
        writer.WriteString("reason", Reason(retirement.Reason));
        writer.WriteString("eligibility_origin_at", retirement.EligibilityOriginAtUtc);
        writer.WriteString("retired_at", retirement.RetiredAtUtc);
        writer.WriteString("policy_id", ObjectCleanupPolicy.Identity);
        writer.WritePropertyName("publication");
        if (retirement.Publication is { } publication) WritePublication(writer, publication);
        else writer.WriteNullValue();
        writer.WriteEndObject();
        writer.WriteString("attempt_id", claim.AttemptId);
        writer.WriteNumber("generation", claim.Generation);
        writer.WriteString("instance_id", claim.ServerInstanceId);
        writer.WriteString("lease_token", claim.LeaseToken);
        writer.WriteString("started_at", claim.StartedAtUtc);
        writer.WriteString("lease_expires_at", claim.LeaseExpiresAtUtc);
        writer.WriteEndObject();
    }, cancellationToken);

    internal static string CursorJson(ObjectCleanupScanCursor cursor, CancellationToken cancellationToken) => Json(writer =>
    {
        writer.WriteStartObject();
        WriteObject(writer, cursor.Target.Object);
        writer.WriteNumber("stream", (int)cursor.Stream);
        writer.WriteString("eligible_at", cursor.EligibleAtUtc);
        writer.WriteString("target_kind", TargetKind(cursor.Target.Kind));
        WriteId(writer, "publication_id", cursor.Target.PublicationAttemptId);
        writer.WriteEndObject();
    }, cancellationToken);

    private static string Json(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This fixed private header contains only checked scalar identities, times, enum text
        // and digests. It never serializes a policy/key, payload, physical path or source row.
        using var buffer = new MemoryStream(4096);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
            cancellationToken.ThrowIfCancellationRequested();
            writer.Flush();
        }
        if (buffer.Length > 16_384) throw InvalidFacts();
        cancellationToken.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static void WritePublication(Utf8JsonWriter writer, PrivatePublicationRecord record)
    {
        var reservation = record.Reservation;
        var owner = reservation.Owner;
        writer.WriteStartObject();
        WriteObject(writer, reservation.Key.Object);
        writer.WriteString("publication_id", reservation.PublicationId);
        writer.WriteString("root_id", reservation.Key.RootId);
        writer.WriteString("temp_id", reservation.Key.TempId);
        writer.WriteString("format", PrivatePublicationFileIdentity.Format(reservation.StoreKind));
        writer.WriteString("storage_kind", StorageKind(reservation.StoreKind));
        writer.WriteString("owner_kind", OwnerKind(owner.Kind));
        writer.WriteString("actor_principal_id", owner.OriginalActor.Value);
        WriteId(writer, "ingestion_operation_id", owner.IngestionClaim?.OperationId);
        WriteId(writer, "cache_entry_id", owner.ReportClaim?.CacheEntryId);
        WriteId(writer, "derivation_id", owner.ReportClaim?.DerivationId);
        WriteConditionalObjectIds(writer, reservation.Key.Object);
        writer.WriteNumber("owner_generation", owner.Generation);
        writer.WriteString("owner_instance_id", owner.ServerInstanceId);
        writer.WriteString("owner_token", owner.LeaseToken);
        writer.WriteString("owner_lease_expires_at", owner.LeaseExpiresAtUtc);
        writer.WriteString("absence_record_digest", reservation.AbsenceRecordDigest.Value);
        writer.WriteString("registered_at", reservation.RegisteredAtUtc);
        if (record.Binding is { } binding)
        {
            var identity = binding.ActualIdentity;
            writer.WriteString("control_namespace_id", identity.ControlNamespaceId);
            writer.WriteString("linux_boot_id", identity.LinuxBootId);
            writer.WriteNumber("mount_namespace_device", identity.MountNamespaceDevice);
            writer.WriteNumber("mount_namespace_inode", identity.MountNamespaceInode);
            writer.WriteNumber("effective_user_id", identity.EffectiveUserId);
            writer.WriteNumber("file_inode", identity.FileIdentity.Inode);
            writer.WriteNumber("file_device_major", identity.FileIdentity.DeviceMajor);
            writer.WriteNumber("file_device_minor", identity.FileIdentity.DeviceMinor);
            writer.WriteNumber("file_mount_id", identity.FileIdentity.MountId);
            writer.WriteNumber("directory_inode", identity.DirectoryIdentity.Inode);
            writer.WriteNumber("directory_device_major", identity.DirectoryIdentity.DeviceMajor);
            writer.WriteNumber("directory_device_minor", identity.DirectoryIdentity.DeviceMinor);
            writer.WriteNumber("directory_mount_id", identity.DirectoryIdentity.MountId);
            writer.WriteString("bound_record_digest", binding.BoundRecordDigest.Value);
            writer.WriteString("bound_at", binding.BoundAtUtc);
        }
        else foreach (var name in BindingColumns) writer.WriteNull(name);
        // Ancillary source restoration rows are deliberately excluded. Only the complete
        // immutable flat registration/binding facts participate in full-claim equality.
        writer.WriteEndObject();
    }

    private static void WriteObject(Utf8JsonWriter writer, PrivateObjectId @object)
    {
        writer.WriteString("organization_id", @object.Scope.OrganizationId);
        writer.WriteString("project_id", @object.Scope.ProjectId);
        writer.WriteString("mapping_id", @object.Scope.MappingId);
        writer.WriteString("revision_id", @object.Scope.RevisionId);
        writer.WriteString("object_class", ObjectClass(@object.Class));
        writer.WriteString("object_id", @object.ObjectId);
    }

    private static void WriteConditionalObjectIds(Utf8JsonWriter writer, PrivateObjectId @object)
    {
        WriteId(writer, "native_object_id", @object.Class == PrivateObjectClass.NativeArtifact ? @object.ObjectId : null);
        WriteId(writer, "candidate_object_id", @object.Class == PrivateObjectClass.DerivedArtifact ? @object.ObjectId : null);
    }

    private static void WriteId(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } id) writer.WriteString(name, id);
        else writer.WriteNull(name);
    }

    internal static string ObjectClass(PrivateObjectClass value) => value switch
    {
        PrivateObjectClass.NativeArtifact => "native",
        PrivateObjectClass.DerivedArtifact => "derived",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    internal static string OwnerKind(PrivateObjectOwnerKind value) => value switch
    {
        PrivateObjectOwnerKind.NativeIngestion => "native_ingestion",
        PrivateObjectOwnerKind.ReportAttempt => "report_attempt",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    internal static string StorageKind(PrivatePublicationStoreKind value) => value switch
    {
        PrivatePublicationStoreKind.FileSystem => "filesystem",
        PrivatePublicationStoreKind.AzureBlob => "azure-blob",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string TargetKind(CleanupTargetKind value) => value switch
    {
        CleanupTargetKind.FinalObject => "final_object",
        CleanupTargetKind.TrackedTemporary => "tracked_temporary",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static string Reason(ObjectCleanupReason value) => value switch
    {
        ObjectCleanupReason.TerminalNativeStage => "terminal_native_stage",
        ObjectCleanupReason.FailedDerivedCandidate => "failed_derived_candidate",
        ObjectCleanupReason.ExpiredDerivedCandidate => "expired_derived_candidate",
        ObjectCleanupReason.AbandonedTrackedTemporary => "abandoned_tracked_temporary",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
