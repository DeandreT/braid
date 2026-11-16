#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. An existing role is refused without rotating it.
: "${BRAID_LOCAL_IDENTITY_PASSWORD:?Set a separate identity reader password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv identity_password BRAID_LOCAL_IDENTITY_PASSWORD
BEGIN;
CREATE ROLE braid_identity_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'identity_password';
GRANT CONNECT ON DATABASE braid TO braid_identity_reader;
COMMIT;
SQL
