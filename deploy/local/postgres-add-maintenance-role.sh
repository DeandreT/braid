#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. An existing role is refused without rotating it.
: "${BRAID_LOCAL_MAINTENANCE_PASSWORD:?Set a separate maintenance password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv maintenance_password BRAID_LOCAL_MAINTENANCE_PASSWORD
BEGIN;
CREATE ROLE braid_maintenance LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'maintenance_password';
GRANT CONNECT ON DATABASE braid TO braid_maintenance;
COMMIT;
SQL
