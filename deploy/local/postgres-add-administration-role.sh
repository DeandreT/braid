#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_ADMINISTRATION_PASSWORD:?Set a separate administration password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv administration_password BRAID_LOCAL_ADMINISTRATION_PASSWORD
CREATE ROLE braid_administration LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'administration_password';
GRANT CONNECT ON DATABASE braid TO braid_administration;
SQL
