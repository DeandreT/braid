#!/usr/bin/env bash
set -euo pipefail
: "${BRAID_LOCAL_ORGANIZATION_DISCOVERY_PASSWORD:?Set a separate organization discovery password}"
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv discovery_password BRAID_LOCAL_ORGANIZATION_DISCOVERY_PASSWORD
CREATE ROLE braid_organization_discovery LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'discovery_password';
GRANT CONNECT ON DATABASE braid TO braid_organization_discovery;
SQL
