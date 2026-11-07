-- Standalone lane migration; renumber when integrated with concurrent migrations.
-- The original package is reserved atomically with the immutable ingestion intent.
-- mapping_revisions.ingestion_operation_id publishes that provenance in the same
-- transaction as the native revision, head advancement and completed receipt.
CREATE TABLE weft.revision_import_provenance (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    provenance bytea NOT NULL,
    CONSTRAINT revision_import_provenance_pk PRIMARY KEY (organization_id, project_id, operation_id),
    CONSTRAINT revision_import_provenance_ingestion_fk FOREIGN KEY (organization_id, project_id, operation_id)
        REFERENCES weft.revision_ingestions(organization_id, project_id, operation_id) ON DELETE RESTRICT,
    CONSTRAINT revision_import_provenance_bound CHECK (octet_length(provenance) BETWEEN 1 AND 25165824)
);

CREATE FUNCTION weft.guard_revision_import_provenance_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Original import provenance is immutable and retained.' USING ERRCODE = '23514';
    END IF;
    -- This row can only accompany a new reserved operation. The insertion is in
    -- that reservation transaction, before any validation/publication can claim it.
    IF NOT EXISTS (
        SELECT 1 FROM weft.revision_ingestions i
        WHERE i.organization_id = NEW.organization_id AND i.project_id = NEW.project_id
          AND i.operation_id = NEW.operation_id AND i.phase = 'reserved' AND i.claim_count = 0
    ) THEN
        RAISE EXCEPTION 'Import provenance must accompany a new reservation.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $guard$;
CREATE TRIGGER revision_import_provenance_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.revision_import_provenance
    FOR EACH ROW EXECUTE FUNCTION weft.guard_revision_import_provenance_transition();
ALTER TABLE weft.revision_import_provenance ENABLE ROW LEVEL SECURITY;
CREATE POLICY revision_import_provenance_organization_scope ON weft.revision_import_provenance
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
GRANT SELECT, INSERT ON weft.revision_import_provenance TO weft_ingest;

CREATE TABLE weft.import_reviews (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    id uuid NOT NULL,
    actor_principal_id uuid NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    review_digest text NOT NULL,
    payload bytea NOT NULL,
    CONSTRAINT import_reviews_pk PRIMARY KEY (organization_id, project_id, id),
    CONSTRAINT import_reviews_actor_fk FOREIGN KEY (organization_id, project_id, actor_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT import_reviews_bound CHECK (octet_length(payload) BETWEEN 1 AND 33554432),
    CONSTRAINT import_reviews_identity CHECK (id <> '00000000-0000-0000-0000-000000000000' AND review_digest ~ '^[0-9a-f]{64}$')
);
CREATE FUNCTION weft.guard_import_reviews_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Import review snapshots are immutable and retained.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $guard$;
CREATE TRIGGER import_reviews_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.import_reviews
    FOR EACH ROW EXECUTE FUNCTION weft.guard_import_reviews_transition();
ALTER TABLE weft.import_reviews ENABLE ROW LEVEL SECURITY;
CREATE POLICY import_reviews_organization_scope ON weft.import_reviews
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
GRANT SELECT, INSERT ON weft.import_reviews TO weft_ingest;

REVOKE ALL ON FUNCTION weft.guard_revision_import_provenance_transition(),
    weft.guard_import_reviews_transition() FROM PUBLIC, weft_runtime, weft_ingest;
REVOKE ALL ON weft.revision_import_provenance, weft.import_reviews FROM PUBLIC, weft_runtime;

CREATE TABLE weft.export_reviews (
    organization_id uuid NOT NULL,
    project_id uuid NOT NULL,
    mapping_id uuid NOT NULL,
    revision_id uuid NOT NULL,
    id uuid NOT NULL,
    actor_principal_id uuid NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    review_digest text NOT NULL,
    payload bytea NOT NULL,
    CONSTRAINT export_reviews_pk PRIMARY KEY (organization_id, project_id, mapping_id, revision_id, id),
    CONSTRAINT export_reviews_actor_fk FOREIGN KEY (organization_id, project_id, actor_principal_id)
        REFERENCES weft.project_memberships(organization_id, project_id, principal_id) ON DELETE RESTRICT,
    CONSTRAINT export_reviews_revision_fk FOREIGN KEY (organization_id, project_id, mapping_id, revision_id)
        REFERENCES weft.mapping_revisions(organization_id, project_id, mapping_id, id) ON DELETE RESTRICT,
    CONSTRAINT export_reviews_bound CHECK (octet_length(payload) BETWEEN 1 AND 33554432),
    CONSTRAINT export_reviews_identity CHECK (id <> '00000000-0000-0000-0000-000000000000' AND review_digest ~ '^[0-9a-f]{64}$')
);
CREATE FUNCTION weft.guard_export_reviews_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path TO pg_catalog, weft AS $guard$
BEGIN
    IF TG_OP <> 'INSERT' THEN
        RAISE EXCEPTION 'Export review snapshots are immutable and retained.' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $guard$;
CREATE TRIGGER export_reviews_transition_guard BEFORE INSERT OR UPDATE OR DELETE ON weft.export_reviews
    FOR EACH ROW EXECUTE FUNCTION weft.guard_export_reviews_transition();
ALTER TABLE weft.export_reviews ENABLE ROW LEVEL SECURITY;
CREATE POLICY export_reviews_organization_scope ON weft.export_reviews
    USING (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid)
    WITH CHECK (organization_id = NULLIF(current_setting('weft.organization_id', true), '')::uuid);
REVOKE ALL ON FUNCTION weft.guard_export_reviews_transition() FROM PUBLIC, weft_runtime, weft_ingest;
REVOKE ALL ON weft.export_reviews FROM PUBLIC, weft_runtime, weft_ingest;
GRANT SELECT, INSERT ON weft.export_reviews TO weft_ingest;
