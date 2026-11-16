-- Optional presentation data, isolated from principals, canonical tickets and permission facts.
CREATE TABLE braid.account_display_metadata (
    organization_id uuid NOT NULL REFERENCES braid.organizations(id) ON DELETE RESTRICT,
    binding_id uuid NOT NULL REFERENCES braid.external_identity_bindings(binding_id) ON DELETE RESTRICT,
    display_name text COLLATE "C",
    observed_at timestamp with time zone NOT NULL CHECK (pg_catalog.isfinite(observed_at)),
    PRIMARY KEY (organization_id,binding_id),
    CHECK (display_name IS NULL OR (pg_catalog.char_length(display_name) BETWEEN 1 AND 200
        AND display_name !~ U&'^[\0009-\000D\0020\0085\00A0\1680\2000-\200A\2028\2029\202F\205F\3000]*$'
        AND display_name !~ U&'[\0001-\001F\007F-\009F\061C\200E\200F\202A-\202E\2066-\2069]'))
);
REVOKE ALL ON braid.account_display_metadata FROM PUBLIC;
-- Optional deployments need not provision this credential. The explicit role installer
-- applies the same grants when enabling presentation after migrations have already run.
DO $display_grants$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname='braid_account_display') THEN
        GRANT USAGE ON SCHEMA braid TO braid_account_display;
        GRANT SELECT (organization_id,binding_id,display_name,observed_at),
            INSERT (organization_id,binding_id,display_name,observed_at), UPDATE (display_name,observed_at)
            ON braid.account_display_metadata TO braid_account_display;
    END IF;
END;
$display_grants$;
