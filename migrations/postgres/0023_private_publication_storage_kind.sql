-- Publications retain the registering store's explicit kind. The publication guard stamps
-- the format label from that kind and never infers it from identity facts. Every row
-- registered before this migration was written by the filesystem store and keeps its label.
DO $braid_require_publication_format_owner$
BEGIN
    IF current_user <> 'braid_migrator' OR session_user <> current_user THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Publication format admission requires the separately provisioned owner login.';
    END IF;
END;
$braid_require_publication_format_owner$;

LOCK TABLE braid.private_object_publications IN ACCESS EXCLUSIVE MODE;

ALTER TABLE braid.private_object_publications ADD COLUMN storage_kind text NOT NULL DEFAULT 'filesystem';
ALTER TABLE braid.private_object_publications ALTER COLUMN storage_kind DROP DEFAULT;
ALTER TABLE braid.private_object_publications DROP CONSTRAINT private_object_publications_format_check;
ALTER TABLE braid.private_object_publications ADD CONSTRAINT private_object_publications_format_check
    CHECK (((storage_kind = 'filesystem' AND format = 'braid.private-publication.linux.v2') OR (storage_kind = 'azure-blob' AND format = 'braid.private-publication.azure-blob.v1')) AND absence_record_digest ~ '^[0-9a-f]{64}$');

CREATE OR REPLACE FUNCTION braid.guard_private_object_publications_v1() RETURNS trigger
LANGUAGE plpgsql VOLATILE PARALLEL UNSAFE SECURITY DEFINER
SET search_path = pg_catalog, braid, pg_temp AS $braid_guard_private_object_publications_v1$
DECLARE observed_at timestamp with time zone;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication registrations are retained.'; END IF;
    PERFORM braid.assert_private_object_context_v1(NEW.organization_id,NEW.project_id,false);
    IF (session_user <> 'braid_ingest' OR NEW.owner_kind <> 'native_ingestion')
        AND (session_user <> 'braid_deriver' OR NEW.owner_kind <> 'report_attempt') THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Publication requires its dedicated original-owner login.';
    END IF;
    IF NOT braid.lock_private_object_origin_v1(NEW.organization_id,NEW.project_id,NEW.mapping_id,NEW.revision_id,NEW.object_class,NEW.object_id) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The original publication owner is unavailable.';
    END IF;
    IF NOT braid.private_publication_owner_live_v1(NEW) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The original publication owner is unavailable.';
    END IF;
    IF TG_OP = 'UPDATE' AND (pg_catalog.to_jsonb(NEW) - ARRAY['control_namespace_id','linux_boot_id','mount_namespace_device','mount_namespace_inode','effective_user_id','file_inode','file_device_major','file_device_minor','file_mount_id','directory_inode','directory_device_major','directory_device_minor','directory_mount_id','bound_record_digest','bound_at'])
        IS DISTINCT FROM (pg_catalog.to_jsonb(OLD) - ARRAY['control_namespace_id','linux_boot_id','mount_namespace_device','mount_namespace_inode','effective_user_id','file_inode','file_device_major','file_device_minor','file_mount_id','directory_inode','directory_device_major','directory_device_minor','directory_mount_id','bound_record_digest','bound_at']) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication ownership and absence facts are immutable.';
    END IF;
    IF EXISTS (SELECT 1 FROM braid.private_object_retirements r WHERE r.organization_id = NEW.organization_id AND r.project_id = NEW.project_id
        AND r.mapping_id = NEW.mapping_id AND r.revision_id = NEW.revision_id AND r.object_class = NEW.object_class AND r.object_id = NEW.object_id
        AND (r.target_kind = 'final_object' OR (r.target_kind = 'tracked_temporary' AND r.publication_id = NEW.publication_id))) THEN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'The exact publication target is retired.';
    END IF;
    observed_at := pg_catalog.clock_timestamp();
    IF TG_OP = 'INSERT' THEN
        IF num_nonnulls(NEW.publication_id,NEW.format,NEW.registered_at,NEW.control_namespace_id,NEW.linux_boot_id,NEW.mount_namespace_device,NEW.mount_namespace_inode,NEW.effective_user_id,NEW.file_inode,NEW.file_device_major,NEW.file_device_minor,NEW.file_mount_id,NEW.directory_inode,NEW.directory_device_major,NEW.directory_device_minor,NEW.directory_mount_id,NEW.bound_record_digest,NEW.bound_at,NEW.native_object_id,NEW.candidate_object_id) <> 0 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication begins with caller intent only.';
        END IF;
        IF NEW.storage_kind IS NULL OR NEW.storage_kind NOT IN ('filesystem','azure-blob') THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication requires its registering store''s explicit kind.';
        END IF;
        NEW.publication_id := pg_catalog.gen_random_uuid(); NEW.registered_at := observed_at;
        NEW.format := CASE NEW.storage_kind WHEN 'filesystem' THEN 'braid.private-publication.linux.v2'
            WHEN 'azure-blob' THEN 'braid.private-publication.azure-blob.v1' END;
        NEW.native_object_id := CASE WHEN NEW.object_class = 'native' THEN NEW.object_id END;
        NEW.candidate_object_id := CASE WHEN NEW.object_class = 'derived' THEN NEW.object_id END;
    ELSE
        IF OLD.bound_at IS NOT NULL THEN
            IF NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication binding is immutable.'; END IF;
            RETURN OLD;
        END IF;
        IF NEW.bound_at IS NOT NULL OR num_nonnulls(NEW.control_namespace_id,NEW.linux_boot_id,NEW.mount_namespace_device,NEW.mount_namespace_inode,NEW.effective_user_id,NEW.file_inode,NEW.file_device_major,NEW.file_device_minor,NEW.file_mount_id,NEW.directory_inode,NEW.directory_device_major,NEW.directory_device_minor,NEW.directory_mount_id,NEW.bound_record_digest) <> 14 THEN
            RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'Publication binding requires one complete actual identity.';
        END IF;
        NEW.bound_at := observed_at;
    END IF;
    RETURN NEW;
END;
$braid_guard_private_object_publications_v1$;

GRANT SELECT (storage_kind), INSERT (storage_kind) ON braid.private_object_publications TO braid_ingest,braid_deriver;
