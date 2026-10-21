#!/usr/bin/env bash
set -euo pipefail
passwords=()
for role in BUILD_RUN_READER BUILD_RUN_WRITER BUILD_DISPATCHER; do
  variable=WEFT_LOCAL_${role}_PASSWORD
  [[ -n ${!variable:-} ]] || { echo "Set $variable." >&2; exit 1; }
  for previous in "${passwords[@]}"; do
    [[ ${!variable} != "$previous" ]] || { echo 'Build run passwords must be independent.' >&2; exit 1; }
  done
  passwords+=("${!variable}")
done
unset passwords previous
# Explicit administrator operation on an existing owned local database; never an application startup task.
psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv reader_password WEFT_LOCAL_BUILD_RUN_READER_PASSWORD
\getenv writer_password WEFT_LOCAL_BUILD_RUN_WRITER_PASSWORD
\getenv dispatcher_password WEFT_LOCAL_BUILD_DISPATCHER_PASSWORD
BEGIN;
CREATE ROLE weft_build_run_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'reader_password';
CREATE ROLE weft_build_run_writer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'writer_password';
CREATE ROLE weft_build_dispatcher LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD :'dispatcher_password';
GRANT CONNECT ON DATABASE weft TO weft_build_run_reader,weft_build_run_writer,weft_build_dispatcher;
COMMIT;
SQL
