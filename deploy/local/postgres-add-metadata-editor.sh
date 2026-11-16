#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. Existing logins, passwords and grants stay intact.
: "${BRAID_LOCAL_METADATA_PASSWORD:?Set a separate mapping metadata editor password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv metadata_password BRAID_LOCAL_METADATA_PASSWORD
BEGIN;
CREATE ROLE braid_catalog_editor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'metadata_password';
GRANT CONNECT ON DATABASE braid TO braid_catalog_editor;
COMMIT;
SQL
