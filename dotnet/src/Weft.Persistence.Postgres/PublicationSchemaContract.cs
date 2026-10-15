namespace Weft.Persistence.Postgres;

internal static class PublicationSchemaContract
{
    internal const string ReadTable = "private_object_publications";
    internal static readonly string[] RetirementIdentityColumns =
        ["organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id", "target_kind", "publication_id"];
    internal static readonly string[] InsertColumns =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id",
        "root_id", "temp_id", "owner_kind", "actor_principal_id", "ingestion_operation_id",
        "cache_entry_id", "derivation_id", "owner_generation", "owner_instance_id", "owner_token",
        "owner_lease_expires_at", "absence_record_digest"
    ];
    internal static readonly string[] BindingColumns =
    [
        "control_namespace_id", "linux_boot_id", "mount_namespace_device", "mount_namespace_inode",
        "effective_user_id", "file_inode", "file_device_major", "file_device_minor", "file_mount_id",
        "directory_inode", "directory_device_major", "directory_device_minor", "directory_mount_id",
        "bound_record_digest"
    ];
    internal static readonly string[] ReadColumns =
    [
        "organization_id", "project_id", "mapping_id", "revision_id", "object_class", "object_id",
        "publication_id", "root_id", "temp_id", "format", "owner_kind", "actor_principal_id",
        "ingestion_operation_id", "cache_entry_id", "derivation_id", "native_object_id", "candidate_object_id", "owner_generation", "owner_instance_id",
        "owner_token", "owner_lease_expires_at", "absence_record_digest", "registered_at",
        .. BindingColumns, "bound_at"
    ];
}
