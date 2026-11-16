#!/usr/bin/env bash
set -euo pipefail
# Run explicitly as a database administrator; never through the Server credential.
# Password is delivered to psql from the environment, never interpolated by the shell.
: "${BRAID_LOCAL_ACCOUNT_DISPLAY_PASSWORD:?Set a separate account display password}"
psql --no-psqlrc --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv display_password BRAID_LOCAL_ACCOUNT_DISPLAY_PASSWORD
BEGIN;
CREATE ROLE braid_account_display LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
    PASSWORD :'display_password';
GRANT CONNECT ON DATABASE braid TO braid_account_display;
DO $grants$
BEGIN
    IF pg_catalog.to_regclass('braid.account_display_metadata') IS NOT NULL THEN
        GRANT USAGE ON SCHEMA braid TO braid_account_display;
        GRANT SELECT (organization_id,binding_id,display_name,observed_at),
            INSERT (organization_id,binding_id,display_name,observed_at), UPDATE (display_name,observed_at)
            ON braid.account_display_metadata TO braid_account_display;
    END IF;
END;
$grants$;
COMMIT;
SQL
