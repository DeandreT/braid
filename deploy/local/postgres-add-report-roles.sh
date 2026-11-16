#!/usr/bin/env bash
set -euo pipefail

# Run explicitly with the local database administrator before the coordinated
# stop/migrate/start upgrade. Both new logins are created in one transaction.
: "${BRAID_LOCAL_REPORT_PASSWORD:?Set a separate report reader password}"
: "${BRAID_LOCAL_DERIVATION_PASSWORD:?Set a separate report derivation password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv report_password BRAID_LOCAL_REPORT_PASSWORD
\getenv derivation_password BRAID_LOCAL_DERIVATION_PASSWORD
BEGIN;
CREATE ROLE braid_report_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'report_password';
CREATE ROLE braid_deriver LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  PASSWORD :'derivation_password';
GRANT CONNECT ON DATABASE braid TO braid_report_reader, braid_deriver;
COMMIT;
SQL
