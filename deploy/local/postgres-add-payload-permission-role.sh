#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. An existing role is refused without rotating it.
: "${BRAID_LOCAL_PAYLOAD_PERMISSION_PASSWORD:?Set a separate payload permission reader password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv payload_permission_password BRAID_LOCAL_PAYLOAD_PERMISSION_PASSWORD
BEGIN;
CREATE ROLE braid_payload_permission_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'payload_permission_password';
GRANT CONNECT ON DATABASE braid TO braid_payload_permission_reader;
COMMIT;
SQL
