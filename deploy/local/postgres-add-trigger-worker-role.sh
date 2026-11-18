#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_TRIGGER_WORKER_PASSWORD:?Set a separate trigger worker password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv trigger_worker_password BRAID_LOCAL_TRIGGER_WORKER_PASSWORD
CREATE ROLE braid_trigger_worker LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'trigger_worker_password';
GRANT CONNECT ON DATABASE braid TO braid_trigger_worker;
SQL
